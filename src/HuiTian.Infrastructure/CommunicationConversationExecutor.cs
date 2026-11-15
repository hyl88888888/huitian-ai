using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public enum CommunicationSessionStatus
{
    Completed,
    WaitingForReply,
    NeedsUserAttention,
    TurnLimitReached,
    TimedOut,
    Failed,
}

public sealed record CommunicationTranscriptEntry(
    string Role,
    string Text,
    DateTimeOffset Timestamp);

public sealed record ObservedChatReply(
    string Text,
    DateTimeOffset Timestamp,
    string Fingerprint);

public sealed class ChatTargetMismatchException : Exception
{
    public ChatTargetMismatchException() : base("当前聊天会话无法核对为原定联系人，已停止续聊。") { }
}

public sealed record CommunicationTurnProposal(
    string? Reply,
    bool ObjectiveSatisfied,
    bool NeedsUserAttention,
    string Reason);

public sealed record CommunicationSessionResult(
    CommunicationSessionStatus Status,
    CommunicationPlan Plan,
    IReadOnlyList<CommunicationTranscriptEntry> Transcript,
    int OutgoingMessageCount,
    string Detail,
    ChatFastPathResult? LastTransportResult);

public sealed record CommunicationSessionProgress(
    string Stage,
    string Detail,
    int OutgoingMessageCount);

public interface IChatConversationTransport
{
    Task<ChatFastPathResult> SendToContactAsync(
        string platform,
        string recipient,
        string message,
        CancellationToken cancellationToken);

    Task<ChatFastPathResult> SendToCurrentConversationAsync(
        string platform,
        string message,
        CancellationToken cancellationToken);

    Task<ObservedChatReply?> WaitForReplyAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task EndSessionAsync(string platform, string recipient) => Task.CompletedTask;
}

public interface ICommunicationTurnComposer
{
    Task<CommunicationTurnProposal> ComposeAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken);
}

public interface ICommunicationTurnPreparer
{
    Task PrepareAsync(
        CommunicationPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// A bounded state machine for real contact conversations.  Contact search is
/// performed exactly once; every continuation uses the already verified chat.
/// It never repeats an outgoing sentence, never continues after an uncertain
/// send result, and pauses for payments, credentials or identity-sensitive
/// requests.
/// </summary>
public sealed class CommunicationConversationExecutor
{
    private readonly IChatConversationTransport _transport;
    private readonly TimeProvider _timeProvider;

    public CommunicationConversationExecutor(
        IChatConversationTransport transport,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CommunicationSessionResult> ExecuteAsync(
        CommunicationPlan plan,
        ICommunicationTurnComposer? composer = null,
        bool continueWhenRepliesArrive = true,
        CancellationToken cancellationToken = default,
        IProgress<CommunicationSessionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var transcript = new List<CommunicationTranscriptEntry>();
        var outgoing = new HashSet<string>(StringComparer.Ordinal);
        var observedFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var startedAt = _timeProvider.GetTimestamp();
        ChatFastPathResult? lastResult;

        if (CommunicationOpeningPolicy.RequiresSemanticComposition(plan) &&
            !plan.OpeningIsSemanticallyComposed)
        {
            return Result(
                CommunicationSessionStatus.Failed,
                "内部沟通目的尚未转换为自然开场，已阻止直接外发。",
                null);
        }
        if (CommunicationStylePolicy.CopiesUserSample(
                plan.OpeningMessage, plan.UserStyleSamples))
        {
            return Result(
                CommunicationSessionStatus.Failed,
                "开场内容复用了您提供的说话样本，已停止发送；需要重新生成符合风格的新消息。",
                null);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CommunicationSessionProgress(
                plan.UseCurrentConversation ? "核对当前会话" : "定位联系人",
                plan.UseCurrentConversation
                    ? $"正在核对{plan.Platform}当前已经打开的会话"
                    : $"正在打开并核对{plan.Platform}联系人“{plan.Recipient}”",
                0));
            lastResult = plan.UseCurrentConversation
                ? await _transport.SendToCurrentConversationAsync(
                        plan.Platform,
                        plan.OpeningMessage,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _transport.SendToContactAsync(
                        plan.Platform,
                        plan.Recipient,
                        plan.OpeningMessage,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(
                CommunicationSessionStatus.TimedOut,
                "首条消息发送已取消或超过时限。",
                null);
        }
        catch
        {
            return Result(
                CommunicationSessionStatus.Failed,
                "首条消息没有发送完成。",
                null);
        }

        var openingActionUnverified =
            lastResult.Status == ChatFastPathStatus.ResultVerificationRequired;
        if (!lastResult.Success && !openingActionUnverified)
        {
            return Result(
                CommunicationSessionStatus.Failed,
                lastResult.Detail,
                lastResult);
        }

        AddOutgoing(plan.OpeningMessage);
        // Application startup/contact lookup must not silently consume the
        // conversation duration before the first message has been sent.
        startedAt = _timeProvider.GetTimestamp();
        progress?.Report(new CommunicationSessionProgress(
            "等待回复",
            openingActionUnverified
                ? "开场发送动作已执行，正在等待对方的新回复作为送达依据"
                : "开场消息已发送，正在持续读取对方的新回复",
            outgoing.Count));
        if (!plan.RequiresReplyObservation)
        {
            return openingActionUnverified
                ? Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    "发送动作已执行，但客户端没有提供可读回执；为避免重复，本次不会再次发送。",
                    lastResult)
                : Result(
                    CommunicationSessionStatus.Completed,
                    "消息已发送，沟通目标中的各项要求均已包含。",
                    lastResult);
        }

        if (!continueWhenRepliesArrive || composer is null)
        {
            return Result(
                CommunicationSessionStatus.WaitingForReply,
                "首条消息已发送，会话目标已保留；等待对方回复后可继续当前会话。",
                lastResult);
        }

        // Use the natural gap between the opening message and the contact's
        // first reply to prime the local continuation prefix.  No text is sent
        // by this preparation call.  On typical conversations this removes
        // the cold-model penalty before a reply actually needs composing.
        if (composer is ICommunicationTurnPreparer preparer)
        {
            _ = preparer.PrepareAsync(plan, cancellationToken);
        }

        while (outgoing.Count < plan.MaximumOutgoingMessages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(CommunicationSessionStatus.TimedOut,
                    "本轮持续聊天已取消，会话记录已保留，不会继续发送。", lastResult);
            }
            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero)
            {
                return DurationLimitResult();
            }

            ObservedChatReply? incoming;
            var pollStartedAt = _timeProvider.GetTimestamp();
            try
            {
                progress?.Report(new CommunicationSessionProgress(
                    "等待回复",
                    "正在本机读取对方的新消息",
                    outgoing.Count));
                incoming = await _transport.WaitForReplyAsync(
                        plan,
                        transcript,
                        remaining < TimeSpan.FromSeconds(25)
                            ? remaining
                            : TimeSpan.FromSeconds(25),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChatTargetMismatchException)
            {
                return Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    "当前聊天窗口已切换或会话标题无法核对，已停止续聊，避免发给其他人。",
                    lastResult);
            }
            catch (OperationCanceledException)
            {
                return Result(
                    CommunicationSessionStatus.TimedOut,
                    "等待对方回复已取消。",
                    lastResult);
            }

            if (Remaining() <= TimeSpan.Zero) return DurationLimitResult();
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(CommunicationSessionStatus.TimedOut,
                    "本轮持续聊天已取消，会话记录已保留，不会继续发送。", lastResult);
            }

            if (incoming is null)
            {
                // A real continuous-chat request must remain alive between
                // replies.  A quiet 25-second polling window is normal, not a
                // reason to tear down the session and lose the verified chat.
                await PacePollingAsync(pollStartedAt).ConfigureAwait(false);
                continue;
            }

            if (CommunicationTranscriptPolicy.IsLikelyOwnMessageEcho(
                    transcript,
                    incoming.Text))
            {
                progress?.Report(new CommunicationSessionProgress(
                    "已忽略己方消息回声",
                    "画面读取到了回天刚发送的内容，已识别为己方气泡，不会再次回复",
                    outgoing.Count));
                await PacePollingAsync(pollStartedAt).ConfigureAwait(false);
                continue;
            }

            // Two distinct incoming bubbles may say the same thing. The
            // observer's event identity, not its text, decides whether this
            // particular message has already been processed.
            if (!string.IsNullOrWhiteSpace(incoming.Fingerprint) &&
                !observedFingerprints.Add(incoming.Fingerprint))
            {
                await PacePollingAsync(pollStartedAt).ConfigureAwait(false);
                continue;
            }

            transcript.Add(new CommunicationTranscriptEntry(
                "contact",
                incoming.Text,
                incoming.Timestamp));
            progress?.Report(new CommunicationSessionProgress(
                "已识别回复",
                "已读取到对方的新消息，正在结合沟通目的判断",
                outgoing.Count));

            if (CommunicationSafetyPolicy.RequiresUserAttention(incoming.Text))
            {
                return Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    "对方回复涉及登录、验证码、身份确认或收付款，需要用户接手。",
                    lastResult);
            }

            CommunicationTurnProposal proposal;
            using (var turnBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var compositionBudget = Remaining();
                if (compositionBudget <= TimeSpan.Zero) return DurationLimitResult();
                turnBudget.CancelAfter(compositionBudget);
                try
                {
                    proposal = await composer.ComposeAsync(plan, transcript, incoming, turnBudget.Token)
                        .WaitAsync(turnBudget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return cancellationToken.IsCancellationRequested
                        ? Result(CommunicationSessionStatus.TimedOut,
                            "本轮持续聊天已取消，会话记录已保留，不会继续发送。", lastResult)
                        : DurationLimitResult();
                }
            }
            // A composer can return just as the timer/cancellation fires.
            // Recheck before interpreting its result or causing another send.
            if (cancellationToken.IsCancellationRequested)
            {
                return Result(CommunicationSessionStatus.TimedOut,
                    "本轮持续聊天已取消，会话记录已保留，不会继续发送。", lastResult);
            }
            if (Remaining() <= TimeSpan.Zero) return DurationLimitResult();
            if (proposal.NeedsUserAttention)
            {
                return Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    proposal.Reason,
                    lastResult);
            }

            if (proposal.ObjectiveSatisfied)
            {
                return Result(
                    CommunicationSessionStatus.Completed,
                    proposal.Reason,
                    lastResult);
            }

            var reply = proposal.Reply?.Trim();
            if (string.IsNullOrWhiteSpace(reply))
            {
                return Result(
                    CommunicationSessionStatus.WaitingForReply,
                    proposal.Reason,
                    lastResult);
            }

            if (CommunicationStylePolicy.CopiesUserSample(
                    reply, plan.UserStyleSamples))
            {
                return Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    "建议回复复用了您提供的说话样本，已暂停发送，避免把示例原文发给联系人。",
                    lastResult);
            }

            if (outgoing.Contains(reply))
            {
                return Result(
                    CommunicationSessionStatus.NeedsUserAttention,
                    "续聊器准备重复同一条消息，已停止以免打扰联系人。",
                    lastResult);
            }

            // The desktop's active chat can change while composing. For a
            // named recipient, enter the verified contact path again before
            // every send; it refuses ambiguous or unreadable targets.
            try
            {
                lastResult = plan.UseCurrentConversation
                    ? await _transport.SendToCurrentConversationAsync(
                            plan.Platform,
                            reply,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _transport.SendToContactAsync(
                            plan.Platform,
                            plan.Recipient,
                            reply,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (ChatTargetMismatchException)
            {
                return Result(CommunicationSessionStatus.NeedsUserAttention,
                    "当前聊天窗口已切换或会话标题无法核对，已停止续聊，避免发给其他人。",
                    lastResult);
            }
            var actionUnverified =
                lastResult.Status == ChatFastPathStatus.ResultVerificationRequired;
            if (!lastResult.Success && !actionUnverified)
            {
                return Result(
                    CommunicationSessionStatus.Failed,
                    lastResult.Detail,
                    lastResult);
            }

            outgoing.Add(reply);
            transcript.Add(new CommunicationTranscriptEntry(
                "assistant",
                reply,
                DateTimeOffset.Now));
            progress?.Report(new CommunicationSessionProgress(
                "继续沟通",
                "已根据对方回复生成合适内容，并在同一会话继续发送",
                outgoing.Count));
        }

        return Result(
            CommunicationSessionStatus.TurnLimitReached,
            "已达到本轮自动发送的安全条数上限，已暂停并保留会话；这不表示沟通目标已经完成。",
            lastResult);

        TimeSpan Remaining() => plan.MaximumDuration - _timeProvider.GetElapsedTime(startedAt);

        CommunicationSessionResult DurationLimitResult()
        {
            if (!plan.UserDurationIsEffectiveLimit)
            {
                return Result(CommunicationSessionStatus.TimedOut,
                    plan.RequestedDuration is null
                        ? "您没有设置聊天时长。本轮自动会话的系统安全预算已用完，已暂停并保留记录，可继续；这不表示沟通目标已经完成。"
                        : "本轮自动会话已达到系统安全时间上限，尚未达到您要求的完整时长；已暂停并保留记录，可继续。",
                    lastResult);
            }
            return Result(
                CommunicationOpeningPolicy.IsOpenEnded(plan)
                    ? CommunicationSessionStatus.Completed : CommunicationSessionStatus.TimedOut,
                CommunicationOpeningPolicy.IsOpenEnded(plan)
                    ? "已按您明确设定的聊天时长结束本轮会话。"
                    : "已达到您明确设定的聊天时长，但尚未确认沟通目标完成；已停止发送并保留记录。",
                lastResult);
        }

        async Task PacePollingAsync(long pollStart)
        {
            // Empty, duplicate or echoed reads can return immediately. Avoid
            // spinning the CPU or hammering OCR/UIA until the session expires.
            var delay = TimeSpan.FromMilliseconds(250) - _timeProvider.GetElapsedTime(pollStart);
            var remaining = Remaining();
            if (delay <= TimeSpan.Zero || remaining <= TimeSpan.Zero) return;
            if (delay > remaining) delay = remaining;
            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* The loop reports cancellation. */ }
        }

        void AddOutgoing(string text)
        {
            outgoing.Add(text);
            transcript.Add(new CommunicationTranscriptEntry(
                "assistant",
                text,
                DateTimeOffset.Now));
        }

        CommunicationSessionResult Result(
            CommunicationSessionStatus status,
            string detail,
            ChatFastPathResult? transportResult)
        {
            progress?.Report(new CommunicationSessionProgress(
                status.ToString(),
                detail,
                outgoing.Count));
            return new CommunicationSessionResult(
                status,
                plan,
                transcript.ToArray(),
                outgoing.Count,
                detail,
                transportResult);
        }
    }
}

public sealed record CommunicationContactProgress(
    string Platform,
    string Recipient,
    CommunicationSessionProgress Progress);

/// <summary>
/// Runs bounded conversations with distinct, explicitly identified contacts.
/// Every contact has its own transcript, reply composer, watcher and lifetime;
/// the shared transport serializes the actual foreground desktop access.
/// This coordinator never discovers strangers or broadcasts a message.
/// </summary>
public sealed class CommunicationMultiSessionExecutor
{
    private readonly IChatConversationTransport _transport;
    private readonly TimeProvider _timeProvider;

    public CommunicationMultiSessionExecutor(
        IChatConversationTransport transport,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<CommunicationSessionResult>> ExecuteAsync(
        IReadOnlyList<CommunicationPlan> plans,
        Func<CommunicationPlan, ICommunicationTurnComposer?> composerFactory,
        CancellationToken cancellationToken = default,
        IProgress<CommunicationContactProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(composerFactory);
        if (plans.Count == 0) return [];
        if (plans.Count > 1 && plans.Any(plan => plan.UseCurrentConversation ||
                                                   plan.Recipient == "@current"))
        {
            throw new ArgumentException(
                "同时聊天必须逐个指定可核对的联系人，不能混用当前会话。", nameof(plans));
        }

        var targets = plans.Select(plan =>
            (CommunicationPlatformIdentity.Canonicalize(plan.Platform) ??
             plan.Platform.Trim()) + "\u001f" + plan.Recipient.Trim()).ToArray();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
        {
            throw new ArgumentException("同时聊天的联系人不能重复。", nameof(plans));
        }

        var sessions = plans.Select(RunContactAsync).ToArray();
        return await Task.WhenAll(sessions).ConfigureAwait(false);

        async Task<CommunicationSessionResult> RunContactAsync(CommunicationPlan plan)
        {
            var single = new CommunicationConversationExecutor(_transport, _timeProvider);
            var contactProgress = progress is null ? null :
                new Progress<CommunicationSessionProgress>(step =>
                    progress.Report(new CommunicationContactProgress(
                        plan.Platform, plan.Recipient, step)));
            CommunicationSessionResult? result = null;
            var cleanupFailed = false;
            try
            {
                try
                {
                    result = await single.ExecuteAsync(
                        plan,
                        composerFactory(plan),
                        continueWhenRepliesArrive: true,
                        cancellationToken,
                        contactProgress).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new CommunicationSessionResult(
                        CommunicationSessionStatus.Failed, plan, [], 0,
                        "当前联系人会话发生未预期错误，未确认后续消息发送；其他联系人会话不受影响。",
                        null);
                }
            }
            finally
            {
                try
                {
                    await _transport.EndSessionAsync(plan.Platform, plan.Recipient)
                        .ConfigureAwait(false);
                }
                catch
                {
                    cleanupFailed = true;
                }
            }
            return cleanupFailed
                ? result! with
                {
                    Status = CommunicationSessionStatus.NeedsUserAttention,
                    Detail = result!.Detail + " 本机会话监听器未能正常清理，请检查后再继续。",
                }
                : result!;
        }
    }
}

/// <summary>
/// A read-only way to activate a named conversation before observing it.
/// Sending still takes the separate verified send path.
/// </summary>
public interface IChatTargetNavigator
{
    Task<bool> EnsureVisibleAsync(
        string platform,
        string recipient,
        CancellationToken cancellationToken);
}

public sealed class WindowsChatTargetNavigator : IChatTargetNavigator
{
    public Task<bool> EnsureVisibleAsync(
        string platform,
        string recipient,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return WindowsUiAutomation.OpenVerifiedChatForObservation(platform, recipient).Success;
    }, cancellationToken);
}

/// <summary>
/// Keeps per-contact observer state while serializing all foreground desktop
/// reads and writes. Each named observation is bound back to its recipient;
/// a temporary switch to another chat is not treated as a reply or a failure.
/// </summary>
public sealed class WindowsChatConversationTransport : IChatConversationTransport
{
    // All chat clients share the foreground desktop, even when their observer
    // sessions have different recipient keys. Never interleave a send with a
    // contact verification or an accessibility read for another session.
    private static readonly SemaphoreSlim DesktopGate = new(1, 1);
    private readonly IChatFastPathSender _fastPath;
    private readonly IChatReplyObserver _observer;
    private readonly IChatTargetNavigator? _navigator;
    private readonly TimeSpan _multiSessionPollInterval;
    private readonly ConcurrentDictionary<string, ObserverSession> _observerSessions =
        new(StringComparer.OrdinalIgnoreCase);

    public WindowsChatConversationTransport(
        IChatFastPathSender? fastPath = null,
        IChatReplyObserver? observer = null,
        IChatTargetNavigator? navigator = null,
        TimeSpan? multiSessionPollInterval = null)
    {
        _fastPath = fastPath ?? new WindowsChatFastPath();
        _observer = observer ?? new AccessibleChatReplyObserver();
        // Unit-test senders have no real Windows chat window. Production uses
        // the same verified target finder as the named send path.
        _navigator = navigator ?? (_fastPath is WindowsChatFastPath
            ? new WindowsChatTargetNavigator() : null);
        _multiSessionPollInterval = multiSessionPollInterval ?? TimeSpan.FromSeconds(4);
    }

    public async Task<ChatFastPathResult> SendToContactAsync(
        string platform,
        string recipient,
        string message,
        CancellationToken cancellationToken)
    {
        var session = _observerSessions.GetOrAdd(
            SessionKey(platform, recipient),
            _ => new ObserverSession(cancellationToken));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, session.Token);
        await session.Gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await DesktopGate.WaitAsync(operation.Token).ConfigureAwait(false);
            ChatFastPathResult result;
            try
            {
                result = await _fastPath.SendAsync(
                        platform,
                        recipient,
                        message,
                        cancellationToken: operation.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                DesktopGate.Release();
            }

            if (result.Success ||
                result.Status == ChatFastPathStatus.ResultVerificationRequired)
            {
                session.ConsecutiveTargetFailures = 0;
                // Start observation without delaying the confirmed send result.
                session.PrimeTask ??= Task.Run(
                    () => PrimeObserverSafelyAsync(
                        platform,
                        recipient,
                        message,
                        session.Token),
                    CancellationToken.None);
            }

            return result;
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task<ChatFastPathResult> SendToCurrentConversationAsync(
        string platform,
        string message,
        CancellationToken cancellationToken)
    {
        var key = SessionKey(platform, "@current");
        _observerSessions.TryGetValue(key, out var session);
        if (session?.PrimeTask is { } primeTask)
        {
            await primeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await DesktopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session is not null)
            {
                var recipient = _observer.GetVerifiedRecipient(platform, "@current");
                if (string.IsNullOrWhiteSpace(recipient))
                {
                    return new ChatFastPathResult(
                        ChatFastPathStatus.TargetVerificationRequired,
                        "当前会话标题无法核对，已停止续聊。",
                        null, null, null, 0, "target_verification_required");
                }

                return await _fastPath.SendAsync(
                        platform, recipient, message, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            // The user's first instruction explicitly selected the visible
            // chat; subsequent sends must use the title bound by its watcher.
            return await _fastPath.ContinueVerifiedTargetAsync(
                    platform, message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DesktopGate.Release();
        }
    }

    public async Task<ObservedChatReply?> WaitForReplyAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var key = SessionKey(plan.Platform, plan.Recipient);
        var session = _observerSessions.GetOrAdd(key, _ => new ObserverSession(cancellationToken));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, session.Token);
        operation.CancelAfter(timeout);
        var entered = false;
        try
        {
            await session.Gate.WaitAsync(operation.Token).ConfigureAwait(false);
            entered = true;
            // A session starting in an already verified, open conversation
            // has no SendToContact call. Initialize its watcher exactly once.
            session.PrimeTask ??= Task.Run(
                () => PrimeObserverSafelyAsync(plan.Platform, plan.Recipient,
                    plan.OpeningMessage, session.Token), CancellationToken.None);
            if (session.PrimeTask is { } primeTask)
            {
                await primeTask.WaitAsync(operation.Token).ConfigureAwait(false);
            }

            operation.Token.ThrowIfCancellationRequested();
            if (_observerSessions.Count > 1 &&
                session.NextPollUtc > DateTimeOffset.UtcNow)
            {
                await Task.Delay(session.NextPollUtc - DateTimeOffset.UtcNow,
                    operation.Token).ConfigureAwait(false);
            }
            await DesktopGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                if (plan.Recipient != "@current" && _navigator is not null &&
                    _observer.IsTargetCurrentlyVisible(plan.Platform, plan.Recipient) != true)
                {
                    if (!await _navigator.EnsureVisibleAsync(
                            plan.Platform, plan.Recipient, operation.Token)
                        .ConfigureAwait(false))
                    {
                        if (++session.ConsecutiveTargetFailures >= 3)
                        {
                            throw new ChatTargetMismatchException();
                        }
                        return null;
                    }
                }

                session.ConsecutiveTargetFailures = 0;
                // A short desktop lease lets other named sessions make
                // progress without a 25-second foreground lock.
                var readWindow = timeout < TimeSpan.FromMilliseconds(1500)
                    ? timeout : TimeSpan.FromMilliseconds(1500);
                var reply = await _observer.WaitForReplyAsync(
                        plan.Platform,
                        plan.Recipient,
                        transcript,
                        readWindow,
                        operation.Token)
                    .ConfigureAwait(false);
                if (reply is null && _observerSessions.Count > 1)
                {
                    // With several live contacts the only portable way to
                    // observe each GPU-rendered chat is time-slicing its
                    // foreground view. Do not switch windows every 250 ms.
                    session.NextPollUtc = DateTimeOffset.UtcNow + _multiSessionPollInterval;
                }
                return reply;
            }
            finally
            {
                DesktopGate.Release();
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !session.Token.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (entered) session.Gate.Release();
        }
    }

    public async Task EndSessionAsync(string platform, string recipient)
    {
        var key = SessionKey(platform, recipient);
        if (!_observerSessions.TryGetValue(key, out var session))
        {
            return;
        }

        // Cancel before joining the gate: an active reply poll must exit before
        // Stop disposes the watcher and its signal. Keep the session registered
        // until cleanup completes so a late send cannot restart it underneath us.
        await session.Cancellation.CancelAsync().ConfigureAwait(false);
        await session.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.Stopped)
            {
                return;
            }

            if (session.PrimeTask is { } primeTask)
            {
                try
                {
                    await primeTask.ConfigureAwait(false);
                }
                catch (ChatTargetMismatchException)
                {
                    // Identity failure is already reported by the executor;
                    // watcher cleanup must still complete.
                }
            }

            await _observer.StopAsync(platform, recipient).ConfigureAwait(false);
            session.Stopped = true;
            _observerSessions.TryRemove(new KeyValuePair<string, ObserverSession>(key, session));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async Task PrimeObserverSafelyAsync(
        string platform,
        string recipient,
        string openingMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DesktopGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (recipient != "@current" && _navigator is not null &&
                    _observer.IsTargetCurrentlyVisible(platform, recipient) != true &&
                    !await _navigator.EnsureVisibleAsync(platform, recipient, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new ChatTargetMismatchException();
                }
                await _observer.PrimeAsync(platform, recipient, openingMessage, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                DesktopGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The enclosing task owns cancellation. There is no observer state
            // to surface as an unobserved background exception.
        }
        catch (ChatTargetMismatchException)
        {
            // The caller must stop the session. An accessibility fallback
            // cannot safely observe a different visible conversation.
            throw;
        }
        catch
        {
            // Persistent observation is an optimization. The observer's normal
            // polling fallback still gets a chance in WaitForReplyAsync.
        }
    }

    private static string SessionKey(string platform, string recipient) =>
        platform.Trim() + "\u001f" + recipient.Trim();

    private sealed class ObserverSession(CancellationToken cancellationToken)
    {
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public CancellationToken Token => Cancellation.Token;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Task? PrimeTask { get; set; }
        public int ConsecutiveTargetFailures { get; set; }
        public DateTimeOffset NextPollUtc { get; set; }
        public bool Stopped { get; set; }
    }
}

public interface IChatReplyObserver
{
    Task PrimeAsync(
        string platform,
        string recipient,
        CancellationToken cancellationToken);

    Task PrimeAsync(
        string platform,
        string recipient,
        string openingMessage,
        CancellationToken cancellationToken) => PrimeAsync(platform, recipient, cancellationToken);

    Task<ObservedChatReply?> WaitForReplyAsync(
        string platform,
        string recipient,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    Task StopAsync(string platform, string recipient);

    string? GetVerifiedRecipient(string platform, string recipient) => null;

    // Null means the observer cannot cheaply tell; the navigator must then
    // verify/reopen the named chat. False is never interpreted as another
    // contact's reply.
    bool? IsTargetCurrentlyVisible(string platform, string recipient) => null;
}

/// <summary>
/// Fast polling reader for clients that expose chat text through Windows
/// accessibility (including QQ and compatible WeChat builds).  GPU-only
/// clients cleanly return no reply so the executor preserves the session
/// instead of guessing from pixels or sending duplicate messages.
/// </summary>
public sealed partial class AccessibleChatReplyObserver : IChatReplyObserver
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _seen =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PersistentWeixinReplyWatcher> _weixinWatchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _initialized =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _boundRecipients =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task PrimeAsync(
        string platform,
        string recipient,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = platform + "\u001f" + recipient;
        // GPU-rendered Weixin has a dedicated persistent observer. Traversing
        // the entire accessibility tree first adds latency without supplying
        // the chat text that this observer needs.
        if (IsWeixin(platform) && FindWeixinWatcherScript() is { } scriptPath)
        {
            var watcher = new PersistentWeixinReplyWatcher(
                scriptPath,
                recipient == "@current" ? null : recipient);
            if (_weixinWatchers.TryGetValue(key, out var previous))
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _weixinWatchers[key] = watcher;
            try
            {
                await watcher.StartAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (ChatTargetMismatchException)
            {
                _weixinWatchers.TryRemove(key, out _);
                await watcher.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                _weixinWatchers.TryRemove(key, out _);
                await watcher.DisposeAsync().ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = WindowsUiAutomation.Inspect(platform, 160);
        cancellationToken.ThrowIfCancellationRequested();
        var boundRecipient = recipient == "@current"
            ? WindowsUiAutomation.ReadVisibleChatTitle(platform)
            : recipient;
        if (string.IsNullOrWhiteSpace(boundRecipient) ||
            !WindowsUiAutomation.VerifyVisibleChatRecipient(platform, boundRecipient))
        {
            throw new ChatTargetMismatchException();
        }
        _boundRecipients[key] = boundRecipient;
        var baseline = snapshot.Success ? ExtractCandidateMessages(snapshot.Text) : [];
        var seen = _seen.GetOrAdd(key, _ => new HashSet<string>(StringComparer.Ordinal));
        lock (seen)
        {
            foreach (var candidate in baseline) seen.Add(candidate);
        }
        // An empty but successfully observed conversation is still a valid
        // baseline; otherwise its first incoming message would be discarded.
        if (snapshot.Success) _initialized[key] = true;
    }

    public async Task<ObservedChatReply?> WaitForReplyAsync(
        string platform,
        string recipient,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var key = platform + "\u001f" + recipient;
        if (_weixinWatchers.TryGetValue(key, out var watcher))
        {
            return await watcher.WaitForReplyAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
        }

        var sentTexts = transcript
            .Where(entry => entry.Role == "assistant")
            .Select(entry => Normalize(entry.Text))
            .ToHashSet(StringComparer.Ordinal);
        var seen = _seen.GetOrAdd(key, _ => new HashSet<string>(StringComparer.Ordinal));
        var deadline = DateTimeOffset.UtcNow + timeout;
        var initialized = _initialized.ContainsKey(key);
        if (!_boundRecipients.TryGetValue(key, out var boundRecipient))
        {
            throw new ChatTargetMismatchException();
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsUiAutomation.VerifyVisibleChatRecipient(platform, boundRecipient))
            {
                throw new ChatTargetMismatchException();
            }
            var snapshot = WindowsUiAutomation.Inspect(platform, 160);
            if (!WindowsUiAutomation.VerifyVisibleChatRecipient(platform, boundRecipient))
            {
                throw new ChatTargetMismatchException();
            }
            var candidates = snapshot.Success
                ? ExtractCandidateMessages(snapshot.Text)
                : [];
            lock (seen)
            {
                if (!initialized && snapshot.Success)
                {
                    foreach (var candidate in candidates)
                    {
                        seen.Add(candidate);
                    }

                    initialized = true;
                    _initialized[key] = true;
                }
                else
                {
                    foreach (var candidate in candidates)
                    {
                        if (sentTexts.Contains(candidate) || !seen.Add(candidate))
                        {
                            continue;
                        }

                        return new ObservedChatReply(
                            candidate,
                            DateTimeOffset.Now,
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(candidate))));
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    public async Task StopAsync(string platform, string recipient)
    {
        var key = platform + "\u001f" + recipient;
        if (_weixinWatchers.TryRemove(key, out var watcher))
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
        }

        _seen.TryRemove(key, out _);
        _initialized.TryRemove(key, out _);
        _boundRecipients.TryRemove(key, out _);
    }

    public string? GetVerifiedRecipient(string platform, string recipient)
    {
        var key = platform + "\u001f" + recipient;
        if (_weixinWatchers.TryGetValue(key, out var watcher))
        {
            return watcher.BoundRecipient;
        }

        return _boundRecipients.TryGetValue(key, out var boundRecipient)
            ? boundRecipient : null;
    }

    public bool? IsTargetCurrentlyVisible(string platform, string recipient)
    {
        var key = platform + "\u001f" + recipient;
        if (_weixinWatchers.TryGetValue(key, out var watcher))
        {
            return watcher.IsTargetActive;
        }

        return _boundRecipients.TryGetValue(key, out var boundRecipient)
            ? WindowsUiAutomation.VerifyVisibleChatRecipient(platform, boundRecipient)
            : null;
    }

    internal static IReadOnlyList<string> ExtractCandidateMessages(string inspection)
    {
        var result = new List<string>();
        foreach (var line in inspection.Split('\n'))
        {
            var match = InspectLineRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var control = match.Groups["control"].Value;
            var text = Normalize(match.Groups["text"].Value);
            if (!MessageControlRegex().IsMatch(control) ||
                text.Length is < 2 or > 600 ||
                ChromeLabelRegex().IsMatch(text))
            {
                continue;
            }

            result.Add(text);
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Normalize(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim();

    private static bool IsWeixin(string platform) =>
        platform.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
        platform.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
        platform.Contains("Weixin", StringComparison.OrdinalIgnoreCase);

    private static string? FindWeixinWatcherScript()
    {
        const string relative = @"vendor\weixin-fast-send\weixin_watch_replies.ps1";
        var direct = Path.Combine(AppContext.BaseDirectory, relative);
        if (File.Exists(direct))
        {
            return direct;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; current is not null && depth < 9; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var working = Path.Combine(Directory.GetCurrentDirectory(), relative);
        return File.Exists(working) ? working : null;
    }

    [GeneratedRegex(@"^-\s*(?<control>[^：:]+)[：:]\s*(?<text>.+?)(?:（标识：.*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex InspectLineRegex();

    [GeneratedRegex(@"(?:文本|Text|Document|文档|ListItem|列表项目)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessageControlRegex();

    [GeneratedRegex(@"^(?:搜索|通讯录|聊天|消息|发送|表情|图片|文件|语音|视频|最小化|最大化|关闭|微信|QQ|未命名输入区域)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChromeLabelRegex();
}

/// <summary>
/// Keeps one Windows OCR process alive for the lifetime of a Weixin
/// conversation.  The worker emits only stable new incoming text through an
/// in-memory pipe; it never stores screenshots or chat content on disk.
/// </summary>
internal sealed class PersistentWeixinReplyWatcher : IAsyncDisposable
{
    private readonly string _scriptPath;
    private readonly string? _expectedRecipient;
    private readonly ConcurrentQueue<ObservedChatReply> _replies = new();
    private readonly SemaphoreSlim _replySignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<bool> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task _reader = Task.CompletedTask;
    private Task _stderrReader = Task.CompletedTask;
    private ChatTargetMismatchException? _targetMismatch;
    private int _isTargetActive;
    public string? BoundRecipient { get; private set; }
    public bool IsTargetActive => Volatile.Read(ref _isTargetActive) != 0;

    public PersistentWeixinReplyWatcher(string scriptPath, string? expectedRecipient)
    {
        _scriptPath = scriptPath;
        _expectedRecipient = expectedRecipient;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
        var tempRoot = Directory.Exists(@"D:\HuiTian\Cache")
            ? @"D:\HuiTian\Cache\Temp\WeixinReplyWatcher"
            : Path.Combine(Path.GetTempPath(), "HuiTian", "WeixinReplyWatcher");
        Directory.CreateDirectory(tempRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(windowsPowerShell) ? windowsPowerShell : "powershell.exe",
            WorkingDirectory = tempRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        const string wrapper =
            "$ProgressPreference='SilentlyContinue';" +
            "$s=[IO.File]::ReadAllText($env:HUITIAN_WX_WATCHER,[Text.UTF8Encoding]::new($false));" +
            "& ([ScriptBlock]::Create($s)) -LifetimeSeconds 43500 -PollMilliseconds 650 -ExpectedContact $env:HUITIAN_WX_EXPECTED";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);
        startInfo.Environment["HUITIAN_WX_WATCHER"] = _scriptPath;
        startInfo.Environment["HUITIAN_WX_EXPECTED"] = _expectedRecipient ?? string.Empty;
        startInfo.Environment["TEMP"] = tempRoot;
        startInfo.Environment["TMP"] = tempRoot;

        _process = Process.Start(startInfo) ??
            throw new InvalidOperationException("无法启动微信本地回复监听器。");
        _reader = ReadOutputAsync(_process, _shutdown.Token);
        // Always consume stderr so a runtime warning cannot fill the pipe and
        // stall OCR. The text is intentionally not retained because it may
        // contain UI details.
        _stderrReader = _process.StandardError.ReadToEndAsync(_shutdown.Token);

        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(6));
        await _ready.Task.WaitAsync(startup.Token).ConfigureAwait(false);
    }

    public async Task<ObservedChatReply?> WaitForReplyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_targetMismatch is { } mismatch) throw mismatch;
        if (_replies.TryDequeue(out var immediate))
        {
            return immediate;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            await _replySignal.WaitAsync(wait.Token).ConfigureAwait(false);
            if (_targetMismatch is { } changedTarget) throw changedTarget;
            return _replies.TryDequeue(out var reply) ? reply : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task ReadOutputAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("ready", out var ready) &&
                        ready.ValueKind == JsonValueKind.True)
                    {
                        BoundRecipient = root.TryGetProperty("title", out var titleElement)
                            ? titleElement.GetString()?.Trim()
                            : null;
                        Volatile.Write(ref _isTargetActive, 1);
                        _ready.TrySetResult(true);
                        continue;
                    }

                    if (root.TryGetProperty("status", out var statusElement))
                    {
                        switch (statusElement.GetString())
                        {
                            case "inactive":
                                Volatile.Write(ref _isTargetActive, 0);
                                _replySignal.Release();
                                continue;
                            case "active":
                                Volatile.Write(ref _isTargetActive, 1);
                                continue;
                            case "target_mismatch":
                                Volatile.Write(ref _isTargetActive, 0);
                                _targetMismatch = new ChatTargetMismatchException();
                                _ready.TrySetException(_targetMismatch);
                                _replySignal.Release();
                                continue;
                        }
                    }

                    var text = root.TryGetProperty("text", out var textElement)
                        ? textElement.GetString()?.Trim()
                        : null;
                    var fingerprint = root.TryGetProperty("fingerprint", out var fingerprintElement)
                        ? fingerprintElement.GetString()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(text) ||
                        string.IsNullOrWhiteSpace(fingerprint))
                    {
                        continue;
                    }

                    _replies.Enqueue(new ObservedChatReply(
                        text,
                        DateTimeOffset.Now,
                        fingerprint));
                    _replySignal.Release();
                }
                catch (JsonException)
                {
                    // Ignore PowerShell startup noise or an incomplete line;
                    // never expose it in the user's conversation.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref _isTargetActive, 0);
            if (_shutdown.IsCancellationRequested)
            {
                _ready.TrySetCanceled();
            }
            else
            {
                // A dead OCR worker cannot safely prove the visible target
                // or deliver new replies; do not silently idle until budget.
                _targetMismatch = new ChatTargetMismatchException();
                _ready.TrySetException(_targetMismatch);
                _replySignal.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        try
        {
            await Task.WhenAll(_reader, _stderrReader).WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch
        {
        }

        _process?.Dispose();
        _shutdown.Dispose();
        _replySignal.Dispose();
    }
}

public static partial class CommunicationSafetyPolicy
{
    public static bool RequiresUserAttention(string text) =>
        !string.IsNullOrWhiteSpace(text) && SensitiveReplyRegex().IsMatch(text);

    [GeneratedRegex(@"(?:登录|注册|验证码|密码|口令|扫码|付款|支付|转账|收款|银行卡|合同签署|人脸|身份证)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveReplyRegex();
}

public static class CommunicationStylePolicy
{
    public static bool CopiesUserSample(
        string? message,
        IReadOnlyList<string>? samples)
    {
        if (string.IsNullOrWhiteSpace(message) || samples is null || samples.Count == 0)
        {
            return false;
        }

        var normalizedMessage = Normalize(message);
        var messageClauses = SplitClauses(message);
        foreach (var sample in samples.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var normalizedSample = Normalize(sample);
            if (normalizedSample.Length >= 2 && normalizedMessage == normalizedSample)
            {
                return true;
            }

            foreach (var clause in SplitClauses(sample))
            {
                if (clause.Length >= 4 && messageClauses.Contains(clause))
                {
                    return true;
                }

                // A copied complete sentence may be followed by another
                // sentence or lose its punctuation during model generation.
                if (clause.Length >= 8 && normalizedMessage.Contains(clause, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static HashSet<string> SplitClauses(string text) =>
        Regex.Split(text, @"[，,。！？!?；;：:\r\n]+")
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// A second line of defence behind visual bubble-side detection. GPU-rendered
/// chat clients can occasionally expose a long outgoing bubble inside the
/// left-side OCR crop. Comparing it with recent assistant turns prevents the
/// product from replying to itself even when a redraw confuses screen layout.
/// </summary>
public static partial class CommunicationTranscriptPolicy
{
    public static bool IsLikelyOwnMessageEcho(
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        string incoming)
    {
        var candidate = NormalizeChatText(incoming);
        if (candidate.Length < 6)
        {
            return false;
        }

        foreach (var entry in transcript
                     .Where(item => item.Role == "assistant")
                     .TakeLast(4))
        {
            var outgoing = NormalizeChatText(entry.Text);
            if (outgoing.Length < 6)
            {
                continue;
            }

            // A screen echo is either the complete outgoing sentence or a
            // truncated OCR fragment of it.  Do not reverse this comparison:
            // a real contact may quote our sentence and then add an answer.
            var lengthRatio = (double)candidate.Length / outgoing.Length;
            if (candidate.Length <= outgoing.Length &&
                lengthRatio >= 0.35 &&
                outgoing.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (candidate.Length >= 8 &&
                candidate.Length <= outgoing.Length &&
                lengthRatio >= 0.55 &&
                BigramCoverage(candidate, outgoing) >= 0.78)
            {
                return true;
            }
        }

        return false;
    }

    private static double BigramCoverage(string shorter, string longer)
    {
        var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < shorter.Length; index++)
        {
            grams.Add(shorter.Substring(index, 2));
        }

        if (grams.Count == 0)
        {
            return 0;
        }

        var covered = grams.Count(gram =>
            longer.Contains(gram, StringComparison.OrdinalIgnoreCase));
        return (double)covered / grams.Count;
    }

    private static string NormalizeChatText(string value) =>
        ChatNoiseRegex().Replace(value ?? string.Empty, string.Empty)
            .ToLowerInvariant();

    [GeneratedRegex(@"[^\p{L}\p{N}]", RegexOptions.CultureInvariant)]
    private static partial Regex ChatNoiseRegex();
}

/// <summary>
/// Keeps an internal conversation goal separate from literal text that may be
/// pasted into another person's chat. Unknown and discussion goals need
/// semantic composition; they must never be sent verbatim as a shortcut.
/// The same structural signal marks social, open-ended sessions where a brief
/// acknowledgement does not mean the conversation is finished.
/// </summary>
public static partial class CommunicationOpeningPolicy
{
    private static readonly ConcurrentDictionary<string, OpeningRotation> OpeningRotations =
        new(StringComparer.Ordinal);

    public static bool RequiresSemanticComposition(CommunicationPlan plan) =>
        plan.Objectives.Any(objective =>
            objective.Kind is CommunicationObjectiveKind.Discuss or
                CommunicationObjectiveKind.Other);

    public static bool IsOpenEnded(CommunicationPlan plan) =>
        plan.Mode == CommunicationExecutionMode.ContinueUntilObjective &&
        RequiresSemanticComposition(plan);

    /// <summary>
    /// Starts an open-ended social conversation without waiting for the large
    /// local brain. The private goal and persona only select a safe tone/topic
    /// family; they are never copied into the outgoing text. Equivalent requests
    /// rotate through several openings so consecutive sessions do not sound like
    /// a canned bot, while subsequent turns still use the real reply and goal.
    /// </summary>
    public static string ComposeSafeImmediateOpening(CommunicationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!IsOpenEnded(plan))
        {
            throw new InvalidOperationException(
                "Only an open-ended conversation can use the neutral fast opening.");
        }

        var candidates = BuildOpeningCandidates(plan);
        var key = string.Join('\u001f',
            plan.Platform.Trim(),
            plan.Recipient.Trim(),
            plan.Persona?.Trim() ?? string.Empty,
            plan.OriginalGoal.Trim());
        var rotation = OpeningRotations.GetOrAdd(
            key,
            _ => new OpeningRotation(Random.Shared.Next(candidates.Count)));
        return rotation.Next(candidates);
    }

    private static IReadOnlyList<string> BuildOpeningCandidates(CommunicationPlan plan)
    {
        var context = string.Join('\u001f',
            plan.Recipient,
            plan.Persona ?? string.Empty,
            plan.OriginalGoal);
        var salutation = ComposeNaturalSalutation(plan.UseCurrentConversation ? string.Empty : plan.Recipient);
        string[] templates;

        if (WorkConversationRegex().IsMatch(context) ||
            AuthoritativePersonaRegex().IsMatch(plan.Persona ?? string.Empty))
        {
            templates =
            [
                "今天进展怎么样？有卡点可以直接和我说。",
                "这会儿方便聊一下吗？想听听你这边的最新情况。",
                "手头的事情推进得还顺利吗？有问题我们及时对一下。",
                "最近工作节奏怎么样？有什么需要沟通的？",
                "方便说说目前的情况吗？我想先听听你的想法。",
                "今天忙得怎么样？有需要一起梳理的事情吗？",
            ];
        }
        else if (EmotionalConversationRegex().IsMatch(context) ||
                 CloseRelationshipRegex().IsMatch(context))
        {
            templates =
            [
                "今天过得怎么样呀？有想聊的就跟我说说。",
                "这会儿在忙吗？想听听你今天怎么样。",
                "忙完了吗？今天心情怎么样？",
                "现在方便聊会儿吗？你今天还顺利吗？",
                "来聊会儿呀，今天有没有什么想和我分享的？",
                "今天还好吗？有什么想说的都可以告诉我。",
            ];
        }
        else if (CatchUpConversationRegex().IsMatch(context))
        {
            templates =
            [
                "最近怎么样？有空聊会儿吗？",
                "这阵子过得还好吗？最近有什么新鲜事？",
                "今天过得怎么样？有空说说近况呀。",
                "这会儿方便吗？想听听你最近怎么样。",
                "最近忙什么呢？一切都还顺利吗？",
                "最近有什么想聊的？我听你说说。",
            ];
        }
        else
        {
            templates =
            [
                "在忙吗？这会儿方便聊两句吗？",
                "今天过得怎么样？有空聊会儿吗？",
                "这会儿方便吗？想听听你最近怎么样。",
                "最近还好吗？有什么想聊的？",
                "现在有空吗？来聊聊你最近的事吧。",
                "今天还顺利吗？有空说两句呀。",
            ];
        }

        return templates
            .Select(template => salutation + template)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComposeNaturalSalutation(string recipient)
    {
        var value = Regex.Replace(recipient, @"\s+", " ")
            .Trim(' ', '“', '”', '\'', '"', '，', ',', '：', ':');
        if (value.Length is 0 or > 20 ||
            GenericRelationshipRegex().IsMatch(value) ||
            UnsafeSalutationRegex().IsMatch(value))
        {
            return string.Empty;
        }

        return value + "，";
    }

    [GeneratedRegex(@"(?:工作|项目|进展|任务|交付|汇报|需求|会议|风险|排期)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkConversationRegex();

    [GeneratedRegex(@"(?:老板|领导|负责人|主管|经理)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthoritativePersonaRegex();

    [GeneratedRegex(@"(?:情绪|开心|安慰|难过|心情|陪伴|关心|倾听|烦恼|压力|不开心|哄)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmotionalConversationRegex();

    [GeneratedRegex(@"(?:女朋友|男朋友|恋人|爱人|对象|伴侣|老婆|老公|媳妇|闺蜜|哥们)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloseRelationshipRegex();

    [GeneratedRegex(@"(?:近况|最近|日常|生活|叙旧|聊聊|聊天|交流)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatchUpConversationRegex();

    [GeneratedRegex(@"^(?:女朋友|男朋友|朋友|好友|恋人|爱人|对象|伴侣|老婆|老公|媳妇|闺蜜|哥们|同事|联系人|家人|爸爸|妈妈|父亲|母亲|哥哥|姐姐|弟弟|妹妹)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericRelationshipRegex();

    [GeneratedRegex(@"(?:微信|WeChat|QQ|联系人|群聊|目的|身份|代替|替我)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeSalutationRegex();

    private sealed class OpeningRotation
    {
        private readonly object _gate = new();
        private int _nextIndex;

        public OpeningRotation(int nextIndex)
        {
            _nextIndex = nextIndex;
        }

        public string Next(IReadOnlyList<string> candidates)
        {
            lock (_gate)
            {
                var candidate = candidates[_nextIndex % candidates.Count];
                _nextIndex = (_nextIndex + 1) % candidates.Count;
                return candidate;
            }
        }
    }
}

/// <summary>
/// Handles obvious acknowledgements and blockers immediately.  A richer local
/// semantic composer can be supplied for free-form replies; this class never
/// fabricates facts that are absent from the original objectives.
/// </summary>
public sealed partial class FastCommunicationTurnComposer :
    ICommunicationTurnComposer,
    ICommunicationTurnPreparer
{
    private readonly ICommunicationTurnComposer? _semanticFallback;

    public FastCommunicationTurnComposer(ICommunicationTurnComposer? semanticFallback = null)
    {
        _semanticFallback = semanticFallback;
    }

    public Task PrepareAsync(
        CommunicationPlan plan,
        CancellationToken cancellationToken) =>
        _semanticFallback is ICommunicationTurnPreparer preparer
            ? preparer.PrepareAsync(plan, cancellationToken)
            : Task.CompletedTask;

    public async Task<CommunicationTurnProposal> ComposeAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken)
    {
        var text = incoming.Text.Trim();
        var hasUserStyle = plan.UserStyleSamples.Any(sample =>
            !string.IsNullOrWhiteSpace(sample));
        var isTerminalAcknowledgement =
            !CommunicationOpeningPolicy.IsOpenEnded(plan) &&
            AcknowledgementRegex().IsMatch(text) &&
            !QualifiedAcknowledgementRegex().IsMatch(text);
        if (hasUserStyle && !isTerminalAcknowledgement &&
            _semanticFallback is not null)
        {
            // The fixed business templates do not reflect the user's own
            // wording. A style-bearing reply must use the semantic composer.
            return await _semanticFallback.ComposeAsync(
                    plan, transcript, incoming, cancellationToken)
                .ConfigureAwait(false);
        }
        // Social/open-ended conversations must be composed from the actual
        // latest message before any business-oriented canned blocker or time
        // template is considered.  Otherwise perfectly ordinary emotional
        // language such as "今天做不完了" produces the same stock reply in
        // every relationship and makes the local semantic composer unreachable.
        if (CommunicationOpeningPolicy.IsOpenEnded(plan) && _semanticFallback is not null)
        {
            return await _semanticFallback.ComposeAsync(
                    plan,
                    transcript,
                    incoming,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (BlockerRegex().IsMatch(text))
        {
            if (TryComposeImmediateBlockerReply(plan, text, out var immediateReply))
            {
                return new CommunicationTurnProposal(
                    immediateReply,
                    false,
                    false,
                    "已结合对方的具体困难和原始沟通目标继续协调。");
            }

            if (_semanticFallback is not null)
            {
                return await _semanticFallback.ComposeAsync(
                        plan,
                        transcript,
                        incoming,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new CommunicationTurnProposal(
                "具体卡在哪里？请把问题和预计完成时间告诉我，我来一起协调。",
                false,
                false,
                "对方反馈存在阻碍，继续了解具体问题。");
        }

        if (!CommunicationOpeningPolicy.IsOpenEnded(plan) &&
            AcknowledgementRegex().IsMatch(text) &&
            !QualifiedAcknowledgementRegex().IsMatch(text))
        {
            return new CommunicationTurnProposal(
                null,
                true,
                false,
                "对方已经明确确认收到或承诺处理，本轮沟通目标完成。");
        }

        var timeObjective = plan.Objectives.FirstOrDefault(objective =>
            TimeObjectiveRegex().IsMatch(objective.Content));
        if (timeObjective is not null && TimeQuestionRegex().IsMatch(text))
        {
            return new CommunicationTurnProposal(
                $"时间是{timeObjective.Content.TrimEnd('。', '？', '?')}，请提前安排好。",
                false,
                false,
                "已依据原始沟通目标回答时间问题。");
        }

        if (_semanticFallback is not null)
        {
            return await _semanticFallback.ComposeAsync(
                    plan,
                    transcript,
                    incoming,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new CommunicationTurnProposal(
            null,
            false,
            false,
            "对方回复需要结合语义生成新答复，会话已保留，未猜测发送。" );
    }

    [GeneratedRegex(@"(?:^|[，,。！!\s])(?:收到|好的|好|知道了|明白了?|没问题|可以|会处理|会完成|已完成|完成了|安排好了?)(?:[，,。！!\s]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcknowledgementRegex();

    [GeneratedRegex(@"(?:做不完|来不及|有问题|有困难|卡住|阻塞|办不到|不能完成|缺少|没有权限)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockerRegex();

    [GeneratedRegex(@"(?:但是|但|不过|然而|还没|尚未|暂时|可能|尽量|争取|不确定|看情况|再说)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedAcknowledgementRegex();

    [GeneratedRegex(@"(?:几点|什么时候|哪天|时间)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeQuestionRegex();

    [GeneratedRegex(@"(?:\d{1,2}\s*[点时:]|早上|上午|下午|晚上|明天|后天|周[一二三四五六日天])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeObjectiveRegex();

    private static bool TryComposeImmediateBlockerReply(
        CommunicationPlan plan,
        string incoming,
        out string reply)
    {
        reply = string.Empty;
        var timeObjective = plan.Objectives.FirstOrDefault(objective =>
            TimeObjectiveRegex().IsMatch(objective.Content));
        var meetingReminder = timeObjective is null
            ? string.Empty
            : $"另外，{timeObjective.Content.TrimEnd('。', '？', '?')}，请提前安排好。";

        if (Regex.IsMatch(incoming, @"(?:做不完|来不及|赶不完|时间不够|可能.*(?:完不成|做不完))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            reply = "我了解了。先把已经完成的部分、当前卡点和预计可交付时间发我；今天能收尾的先完成，需要协调的我来处理。" +
                    meetingReminder;
            return true;
        }

        if (Regex.IsMatch(incoming, @"(?:没有权限|缺少权限|缺资料|缺少资料|拿不到数据|资源不够)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            reply = "请把缺少的权限或资料、受影响的工作和最晚需要时间列清楚发我，我马上协调；不受影响的部分先继续推进。" +
                    meetingReminder;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Generates one context-aware continuation with the already-running local
/// brain.  This deliberately bypasses the full agent/tool harness: a chat turn
/// needs the communication goal and recent dialogue, not twenty Windows tools,
/// screenshots, or a new planning thread.  Reply length is chosen from the
/// other person's message and the objective instead of being forced short.
/// </summary>
public sealed partial class LocalCommunicationTurnComposer :
    ICommunicationTurnComposer,
    ICommunicationTurnPreparer,
    IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly ConcurrentDictionary<string, string> _openingCache =
        new(StringComparer.Ordinal);
    private readonly object _warmupGate = new();
    private Task? _openEndedWarmup;
    private CancellationTokenSource? _openEndedWarmupCancellation;

    public LocalCommunicationTurnComposer(AgentBackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _model = configuration.ModelName;
        // Talk to the existing local engine directly for this small compose
        // request. Disconnecting a cancelled warmup must release inference,
        // not remain queued behind a buffered Responses compatibility hop.
        _endpoint = new Uri((configuration.IsProductDefault
            ? "http://127.0.0.1:18180/v1"
            : configuration.ResponsesBaseUrl.TrimEnd('/')) + "/chat/completions");
        _client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(2),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>
    /// Converts a private communication goal into one natural opening line.
    /// The goal guides wording but is never pasted into the real chat. Results
    /// are cached so an equivalent repeated request can start immediately.
    /// </summary>
    public async Task<string?> ComposeOpeningAsync(
        CommunicationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // The first turn of a sustained social chat must not wait for (or even
        // contact) the local model. Keep this guard here as well as in the UI
        // call site so future callers cannot accidentally reintroduce startup
        // latency or the cached, identical opening problem.
        if (CommunicationOpeningPolicy.IsOpenEnded(plan) &&
            !plan.UserStyleSamples.Any(sample => !string.IsNullOrWhiteSpace(sample)))
        {
            return CommunicationOpeningPolicy.ComposeSafeImmediateOpening(plan);
        }

        var cacheKey = string.Join('\u001f',
            plan.Platform,
            plan.Recipient,
            plan.Persona ?? string.Empty,
            plan.OriginalGoal,
            string.Join('\u001e', plan.UserStyleSamples));
        if (_openingCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = OpeningInstructions },
                    new { role = "user", content = BuildOpeningContext(plan) },
                },
                temperature = 0.35,
                max_tokens = 100,
                stream = false,
                response_format = new { type = "json_object" },
                chat_template_kwargs = new { enable_thinking = false },
                cache_prompt = true,
            };

            using var response = await _client.PostAsJsonAsync(
                    _endpoint,
                    body,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            var opening = ParseOpeningMessage(content);
            if (CommunicationStylePolicy.CopiesUserSample(
                    opening, plan.UserStyleSamples))
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(opening))
            {
                _openingCache.TryAdd(cacheKey, opening);
            }

            return opening;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<CommunicationTurnProposal> ComposeAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var openEnded = CommunicationOpeningPolicy.IsOpenEnded(plan);
        if (openEnded)
        {
            CancelUnfinishedWarmup();

            return await ComposeOpenEndedStreamingAsync(
                    plan,
                    transcript,
                    incoming,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(36));
        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = ContinuationInstructions,
                    },
                    new
                    {
                        role = "user",
                        content = BuildConversationContext(plan, transcript),
                    },
                },
                temperature = 0.25,
                max_tokens = 120,
                stream = false,
                response_format = new { type = "json_object" },
                chat_template_kwargs = new { enable_thinking = false },
                cache_prompt = true,
            };

            using var response = await _client.PostAsJsonAsync(
                    _endpoint,
                    body,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            return ParseProposal(content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "本机正在生成合适回复，但这一轮超过快速时限；会话已保留，没有猜测发送。");
        }
        catch
        {
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "没有可靠生成下一条回复；会话已保留，没有发送不确定内容。");
        }
    }

    public Task PrepareAsync(
        CommunicationPlan plan,
        CancellationToken cancellationToken)
    {
        if (!CommunicationOpeningPolicy.IsOpenEnded(plan))
        {
            return Task.CompletedTask;
        }

        lock (_warmupGate)
        {
            if (_openEndedWarmup is { IsCompleted: false })
            {
                return _openEndedWarmup;
            }

            _openEndedWarmupCancellation?.Dispose();
            _openEndedWarmupCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _openEndedWarmup = WarmOpenEndedPrefixAsync(
                _openEndedWarmupCancellation.Token);
            return _openEndedWarmup;
        }
    }

    private void CancelUnfinishedWarmup()
    {
        Task? warmup;
        CancellationTokenSource? cancellation;
        lock (_warmupGate)
        {
            warmup = _openEndedWarmup;
            cancellation = _openEndedWarmupCancellation;
            _openEndedWarmup = null;
            _openEndedWarmupCancellation = null;
        }

        if (warmup is { IsCompleted: false })
        {
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A concurrent Dispose may already own cleanup.
            }
        }

        if (cancellation is not null)
        {
            _ = (warmup ?? Task.CompletedTask).ContinueWith(
                _ => cancellation.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task WarmOpenEndedPrefixAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = OpenEndedContinuationInstructions },
                    new { role = "user", content = "身份：用户本人\n私下目的：自然回应\n最近对话：\n对方：嗯" },
                },
                temperature = 0,
                max_tokens = 1,
                stream = false,
                chat_template_kwargs = new { enable_thinking = false },
                cache_prompt = true,
            };
            using var response = await _client.PostAsJsonAsync(
                    _endpoint,
                    body,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            _ = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Preparation is opportunistic. The real turn still has its own
            // bounded streaming request and recovery path.
        }
    }

    private async Task<CommunicationTurnProposal> ComposeOpenEndedStreamingAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A pre-warmed turn normally completes well below ten seconds. Keep a
        // larger cold ceiling so the first ever reply is not replaced by a
        // repetitive template merely because model prefix preparation had not
        // finished before the contact answered.
        timeout.CancelAfter(TimeSpan.FromSeconds(22));
        try
        {
            var body = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = OpenEndedContinuationInstructions },
                    new { role = "user", content = BuildOpenEndedConversationContext(plan, transcript) },
                },
                temperature = 0.62,
                top_p = 0.9,
                max_tokens = incoming.Text.Length <= 36 ? 56 : 88,
                stream = true,
                chat_template_kwargs = new { enable_thinking = false },
                cache_prompt = true,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(body),
            };
            using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = new StringBuilder();
            var desiredMinimum = incoming.Text.Length > 28 ? 24 : 6;
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var json = line[5..].Trim();
                if (json == "[DONE]") break;
                try
                {
                    using var chunk = JsonDocument.Parse(json);
                    var choice = chunk.RootElement.GetProperty("choices")[0];
                    string? delta = null;
                    if (choice.TryGetProperty("delta", out var deltaNode) &&
                        deltaNode.TryGetProperty("content", out var deltaContent) &&
                        deltaContent.ValueKind == JsonValueKind.String)
                    {
                        delta = deltaContent.GetString();
                    }
                    else if (choice.TryGetProperty("message", out var messageNode) &&
                             messageNode.TryGetProperty("content", out var messageContent) &&
                             messageContent.ValueKind == JsonValueKind.String)
                    {
                        delta = messageContent.GetString();
                    }

                    if (!string.IsNullOrEmpty(delta)) content.Append(delta);
                }
                catch (JsonException)
                {
                    continue;
                }

                var candidate = ClosedThinkingRegex().Replace(content.ToString(), string.Empty).Trim();
                if (candidate.Length >= desiredMinimum &&
                    candidate.Length <= 500 &&
                    candidate[^1] is '。' or '！' or '？' or '!' or '?')
                {
                    return ParseOpenEndedReply(candidate);
                }

                // For a short incoming message, a complete natural first
                // clause is already a useful real-time response.  Close that
                // clause as a sentence instead of waiting several more seconds
                // for the model to elaborate.  Longer/complex incoming content
                // still waits for a full sentence and therefore keeps the
                // adaptive-length behaviour requested by the user.
                if (incoming.Text.Length <= 28 &&
                    candidate.Length is >= 8 and <= 80 &&
                    candidate[^1] is '，' or ',')
                {
                    return ParseOpenEndedReply(
                        candidate.TrimEnd('，', ',') + "。");
                }

                if (incoming.Text.Length <= 28 &&
                    candidate.Length is >= 18 and <= 80 &&
                    CanCloseAsShortReply(candidate))
                {
                    return ParseOpenEndedReply(candidate + "。");
                }
            }

            return ParseOpenEndedReply(content.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (plan.UserStyleSamples.Any(sample => !string.IsNullOrWhiteSpace(sample)))
            {
                return new CommunicationTurnProposal(
                    null, false, false,
                    "本机未能可靠生成符合您说话方式的回复，已保留会话，没有发送套话。");
            }
            return new CommunicationTurnProposal(
                BuildOpenEndedRecoveryReply(incoming.Text),
                false,
                false,
                "本机快速生成暂时没有完整返回，已用不虚构事实的承接回复保持会话。" );
        }
    }

    public static CommunicationTurnProposal ParseOpenEndedReply(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "没有生成可靠的自然回复，会话已保留。");
        }

        var value = ClosedThinkingRegex().Replace(content, string.Empty).Trim();
        if (value.StartsWith('{'))
        {
            return ParseProposal(value);
        }

        value = ReplyPrefixRegex().Replace(value, string.Empty)
            .Trim(' ', '\r', '\n', '`', '“', '”', '"');
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length is < 1 or > 500)
        {
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "本轮自然回复不完整，会话已保留。");
        }

        return new CommunicationTurnProposal(
            value,
            false,
            false,
            "已通过持续聊天快速通道结合最新回复和私下目的生成下一句。");
    }

    private static bool CanCloseAsShortReply(string candidate)
    {
        var last = candidate[^1];
        return last is not ('的' or '地' or '得' or '和' or '与' or '或' or
            '把' or '被' or '但' or '而' or '因' or '如' or '是' or '在' or
            '有' or '了' or '着' or '过' or '：' or ':' or '；' or ';' or
            '、' or '（' or '(' or '“' or '"');
    }

    private static string BuildOpenEndedRecoveryReply(string incoming)
    {
        var text = incoming.Trim();
        return Regex.IsMatch(
            text,
            @"[？?]|(?:吗|呢|怎么|为什么|咋|如何)[呀啊呢吗]?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? "刚才这句我想认真理解，怕随便回答反而误会你。你愿意再具体说一点吗？"
            : "我在认真听，你慢慢说。这里面你现在最想先聊的是哪一件事？";
    }

    public static CommunicationTurnProposal ParseProposal(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "没有生成可靠回复，会话已保留。");
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var action = root.TryGetProperty("a", out var compactAction)
                ? compactAction.GetString()?.Trim().ToLowerInvariant()
                : root.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()?.Trim().ToLowerInvariant()
                    : "r";
            var reply = root.TryGetProperty("r", out var compactReply)
                ? compactReply.GetString()?.Trim()
                : root.TryGetProperty("reply", out var replyElement)
                    ? replyElement.GetString()?.Trim()
                    : null;
            var reason = "已结合对方回复、最近对话和原始沟通目的判断下一步。";

            return action switch
            {
                "d" or "done" => new CommunicationTurnProposal(null, true, false, reason),
                "p" or "pause" => new CommunicationTurnProposal(null, false, true, reason),
                _ when !string.IsNullOrWhiteSpace(reply) =>
                    new CommunicationTurnProposal(reply, false, false, reason),
                _ => new CommunicationTurnProposal(null, false, false, reason),
            };
        }
        catch (JsonException)
        {
            // A malformed structured answer must never be pasted into another
            // person's chat.  Preserve the session and wait for a clean turn.
            return new CommunicationTurnProposal(
                null,
                false,
                false,
                "本轮回复格式不完整，会话已保留，没有发送不确定内容。");
        }
    }

    public static string? ParseOpeningMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var opening = root.TryGetProperty("r", out var compactReply)
                ? compactReply.GetString()?.Trim()
                : root.TryGetProperty("reply", out var reply)
                    ? reply.GetString()?.Trim()
                    : null;
            if (string.IsNullOrWhiteSpace(opening) || opening.Length > 500)
            {
                return null;
            }

            return Regex.Replace(opening, @"\s+", " ").Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildOpeningContext(CommunicationPlan plan)
    {
        var builder = new StringBuilder();
        builder.Append("平台：").AppendLine(plan.Platform);
        builder.Append("联系人称呼：").AppendLine(plan.Recipient);
        builder.Append("代表身份：").AppendLine(plan.Persona ?? "用户本人");
        builder.Append("内部沟通目的（只能指导措辞，不得原样告诉对方）：")
            .AppendLine(plan.OriginalGoal);
        AppendUserStyleSamples(builder, plan);
        builder.Append("会话形式：")
            .AppendLine(CommunicationOpeningPolicy.IsOpenEnded(plan)
                ? "持续自然对话"
                : "完成明确事项");
        return builder.ToString();
    }

    private static string BuildConversationContext(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript)
    {
        var builder = new StringBuilder();
        builder.Append("平台：").AppendLine(plan.Platform);
        builder.Append("联系人：").AppendLine(plan.Recipient);
        builder.Append("代表身份：").AppendLine(plan.Persona ?? "用户本人");
        builder.Append("最终沟通目的：").AppendLine(plan.OriginalGoal);
        AppendUserStyleSamples(builder, plan);
        builder.Append("会话形式：")
            .AppendLine(CommunicationOpeningPolicy.IsOpenEnded(plan)
                ? "开放式持续对话；普通的‘好/嗯/收到’不代表会话目标完成"
                : "完成明确事项后可以结束");
        builder.AppendLine("必须覆盖的目标：");
        foreach (var objective in plan.Objectives)
        {
            builder.Append(objective.Sequence).Append(". ")
                .AppendLine(Compact(objective.Content, 220));
        }

        builder.AppendLine("最近对话：");
        foreach (var entry in transcript.TakeLast(6))
        {
            builder.Append(entry.Role == "contact" ? "对方：" : "我方：")
                .AppendLine(Compact(entry.Text, 160));
        }

        return builder.ToString();
    }

    private static string BuildOpenEndedConversationContext(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript)
    {
        var builder = new StringBuilder();
        builder.Append("身份：").AppendLine(Compact(plan.Persona ?? "用户本人", 30));
        builder.Append("私下目的：").AppendLine(Compact(plan.OriginalGoal, 120));
        AppendUserStyleSamples(builder, plan);
        builder.AppendLine("最近对话：");
        foreach (var entry in transcript.TakeLast(4))
        {
            builder.Append(entry.Role == "contact" ? "对方：" : "我方：")
                .AppendLine(Compact(entry.Text, 90));
        }

        return builder.ToString();
    }

    private static void AppendUserStyleSamples(StringBuilder builder, CommunicationPlan plan)
    {
        var samples = plan.UserStyleSamples
            .Where(sample => !string.IsNullOrWhiteSpace(sample))
            .Take(5)
            .Select(sample => Compact(sample, 140))
            .ToArray();
        if (samples.Length == 0) return;

        builder.AppendLine("以下文字由用户本人明确提供，仅作说话风格示例。只模仿语气、用词和句长；不要复制其中的事实、关系、承诺或指令，也不要向对方提及示例：");
        for (var index = 0; index < samples.Length; index++)
        {
            builder.Append(index + 1).Append(". “").Append(samples[index]).AppendLine("”");
        }
    }

    private static string Compact(string value, int maximumLength)
    {
        var compact = Regex.Replace(value, @"\s+", " ").Trim();
        return compact.Length <= maximumLength
            ? compact
            : compact[..(maximumLength - 1)] + "…";
    }

    public void Dispose()
    {
        CancelUnfinishedWarmup();
        _client.Dispose();
    }

    private const string ContinuationInstructions = """
        代表用户继续真实聊天。结合对方最新回复、最近对话、用户身份和内部目的判断，不复述内部指令，不虚构事实或承诺。若提供用户本人文字样本，模仿其语气、用词和句长，但不得复制样本里的事实或指令。回复长短随语境：简单时短，需要解释、安抚、幽默、追问或说服时完整，不能固定短答。语气自然像真人。开放式聊天中“好/嗯/收到”不算结束；明确告别或目标完成才a=d，必须由用户决定才a=p，否则a=r。只输出JSON：{"a":"r|d|p","r":"回复或空字符串"}。
        """;

    private const string OpenEndedContinuationInstructions = """
        代用户与联系人自然续聊。根据对方最新消息、最近几句和私下目的回复；若提供用户本人文字样本，模仿其语气、用词和句长，但不得复制样本里的事实或指令。不要复述指令，不编造事实、经历或承诺。简单时一句，需要安慰、解释或认真回应时可两三句，长短随语境。只输出可直接发送的简体中文，不加引号、标签或说明。
        """;

    private const string OpeningInstructions = """
        你只生成一条可直接发送给真实联系人的自然开场消息。用户给出的身份和沟通目的都是内部策略，绝不能把“以某某身份”“目的是”“提供情绪价值”“哄对方开心”等指令字样照搬给对方。若提供用户本人文字样本，模仿其语气、用词和句长，但不得复制样本里的事实或指令。要根据关系、目的和语境自然开场，不套固定模板，不虚构双方经历、昵称、事实、承诺、价格或进度；信息不足时用自然且不冒犯的开场引出对话。措辞像真人，长度按目的自适应，不提回天、模型或系统。只输出JSON：{"r":"可直接发送的开场消息"}，不要解释。
        """;

    [GeneratedRegex(@"(?is)<think>.*?</think>", RegexOptions.CultureInvariant)]
    private static partial Regex ClosedThinkingRegex();

    [GeneratedRegex(@"^(?:回复|答复|消息|发送内容)\s*[:：]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefixRegex();
}
