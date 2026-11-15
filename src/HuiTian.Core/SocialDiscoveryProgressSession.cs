namespace HuiTian.Core;

/// <summary>
/// Progress for one verified platform account. A candidate is not eligible for
/// outreach until its profile evidence passes SocialCandidatePolicy.
/// </summary>
public enum SocialDiscoveryContactStage
{
    Candidate,
    Verified,
    Contacted,
    Replied,
}

public enum SocialDiscoveryRunStatus
{
    InProgress,
    Completed,
    NeedsUserAttention,
    Stalled,
    TimedOut,
}

public sealed record SocialDiscoveryProgressOptions(
    SocialDiscoveryDesiredOutcome DesiredOutcome,
    int RequiredContacts,
    TimeSpan MaximumDuration,
    TimeSpan NoProgressTimeout,
    TimeSpan? RequestedConversationDuration = null);

/// <summary>
/// The caller must create this only after checking the visible account, the
/// outgoing bubble or delivery receipt, and the bound conversation. An Enter
/// key press alone is not a verified delivery.
/// </summary>
public sealed record SocialVerifiedOutgoingEvidence(
    SocialContactKey Contact,
    string ConversationId,
    string EventFingerprint,
    DateTimeOffset ObservedAt,
    bool RecipientVerified,
    bool DeliveryVerified);

/// <summary>
/// The caller must distinguish a new incoming bubble from an own-message echo
/// or an older message already visible when the conversation was opened.
/// </summary>
public sealed record SocialVerifiedReplyEvidence(
    SocialContactKey Contact,
    string ConversationId,
    string EventFingerprint,
    DateTimeOffset ObservedAt,
    bool SenderVerified,
    bool NewIncomingVerified);

public sealed record SocialDiscoveryContactProgress(
    SocialContactKey Contact,
    SocialDiscoveryContactStage Stage);

public sealed record SocialDiscoveryProgressSnapshot(
    SocialDiscoveryRunStatus Status,
    string Detail,
    int ObservedCandidates,
    int VerifiedCandidates,
    int ContactedCandidates,
    int RepliedCandidates,
    DateTimeOffset StartedAt,
    DateTimeOffset LastProgressAt,
    DateTimeOffset ObservedAt,
    IReadOnlyList<SocialDiscoveryContactProgress> Contacts)
{
    public bool CanClaimCompletion => Status == SocialDiscoveryRunStatus.Completed;

    public SocialDiscoveryExecutionContract.CompletionEvidence ToCompletionEvidence() =>
        new(VerifiedCandidates, ContactedCandidates, RepliedCandidates,
            ObservedAt - StartedAt);
}

/// <summary>
/// A bounded, evidence-driven task state for finding new people. This class
/// does not inspect a website or send a message. Adapters supply verified
/// evidence; the task controller periodically reads Snapshot to decide whether
/// to continue, ask the user to log in, or report a concrete stopping point.
/// </summary>
public sealed class SocialDiscoveryProgressSession
{
    private const int MaximumTrackedContacts = 128;
    private readonly SocialDiscoveryProgressOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<SocialContactKey, ContactState> _contacts = [];
    private readonly Dictionary<string, SocialContactKey> _conversationOwners =
        new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset? _firstVerifiedOutgoingAt;
    private string? _attentionReason;

    public SocialDiscoveryProgressSession(
        SocialDiscoveryProgressOptions options,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.DesiredOutcome) || options.RequiredContacts < 1 ||
            options.MaximumDuration <= TimeSpan.Zero ||
            options.NoProgressTimeout <= TimeSpan.Zero ||
            options.NoProgressTimeout > options.MaximumDuration ||
            options.RequestedConversationDuration is { } duration &&
            (duration <= TimeSpan.Zero || duration > options.MaximumDuration))
            throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _clock = clock ?? TimeProvider.System;
        _startedAt = _clock.GetUtcNow();
        _lastProgressAt = _startedAt;
    }

    public bool ObserveCandidate(SocialContactKey contact)
    {
        EnsureInProgress();
        contact = Normalize(contact);
        if (_contacts.ContainsKey(contact)) return false;
        if (_contacts.Count >= MaximumTrackedContacts) return false;
        _contacts.Add(contact, new ContactState());
        MarkProgress();
        return true;
    }

    public SocialCandidateDecision VerifyCandidate(
        SocialCandidateEvidence evidence,
        bool alreadyContacted = false)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureInProgress();
        var contact = Normalize(evidence.Contact);
        if (!_contacts.TryGetValue(contact, out var state))
            throw new InvalidOperationException("须先从真实资料页观察候选账号，再核验其资格。");
        var decision = SocialCandidatePolicy.EvaluateForNewOutreach(
            evidence with { Contact = contact }, alreadyContacted);
        if (decision.EligibleForDraft && state.Stage == SocialDiscoveryContactStage.Candidate)
        {
            state.Stage = SocialDiscoveryContactStage.Verified;
            MarkProgress();
        }
        return decision;
    }

    public bool RecordVerifiedOutgoing(SocialVerifiedOutgoingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureInProgress();
        var contact = Normalize(evidence.Contact);
        var state = GetEligibleContact(contact);
        if (!evidence.RecipientVerified || !evidence.DeliveryVerified ||
            string.IsNullOrWhiteSpace(evidence.ConversationId) ||
            string.IsNullOrWhiteSpace(evidence.EventFingerprint) ||
            evidence.ObservedAt < _startedAt || evidence.ObservedAt > _clock.GetUtcNow())
            return false;
        var conversationKey = contact.Platform + "\u001f" + evidence.ConversationId;
        if (_conversationOwners.TryGetValue(conversationKey, out var owner) &&
            owner != contact)
            return false;
        if (state.ConversationId is not null &&
            !string.Equals(state.ConversationId, evidence.ConversationId,
                StringComparison.Ordinal))
            return false;
        if (!state.OutgoingEvents.Add(evidence.EventFingerprint)) return false;
        _conversationOwners.TryAdd(conversationKey, contact);
        state.ConversationId = evidence.ConversationId;
        state.FirstOutgoingAt ??= evidence.ObservedAt;
        _firstVerifiedOutgoingAt ??= evidence.ObservedAt;
        if (state.Stage < SocialDiscoveryContactStage.Contacted)
            state.Stage = SocialDiscoveryContactStage.Contacted;
        MarkProgress();
        return true;
    }

    public bool RecordVerifiedReply(SocialVerifiedReplyEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureInProgress();
        var contact = Normalize(evidence.Contact);
        if (!_contacts.TryGetValue(contact, out var state) ||
            state.Stage < SocialDiscoveryContactStage.Contacted ||
            !evidence.SenderVerified || !evidence.NewIncomingVerified ||
            string.IsNullOrWhiteSpace(evidence.EventFingerprint) ||
            state.OutgoingEvents.Contains(evidence.EventFingerprint) ||
            !string.Equals(state.ConversationId, evidence.ConversationId,
                StringComparison.Ordinal) ||
            evidence.ObservedAt < state.FirstOutgoingAt ||
            evidence.ObservedAt > _clock.GetUtcNow())
            return false;
        if (!state.IncomingEvents.Add(evidence.EventFingerprint)) return false;
        state.Stage = SocialDiscoveryContactStage.Replied;
        MarkProgress();
        return true;
    }

    public void PauseForUser(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        EnsureInProgress();
        _attentionReason = reason.Trim();
    }

    public SocialDiscoveryProgressSnapshot Snapshot()
    {
        var now = _clock.GetUtcNow();
        var states = _contacts.Values.ToArray();
        var verified = states.Count(state => state.Stage >= SocialDiscoveryContactStage.Verified);
        var contacted = states.Count(state => state.Stage >= SocialDiscoveryContactStage.Contacted);
        var replied = states.Count(state => state.Stage >= SocialDiscoveryContactStage.Replied);
        var reached = _options.DesiredOutcome switch
        {
            SocialDiscoveryDesiredOutcome.CandidateShortlist =>
                verified >= _options.RequiredContacts,
            SocialDiscoveryDesiredOutcome.ContactInitiated =>
                contacted >= _options.RequiredContacts,
            SocialDiscoveryDesiredOutcome.ReciprocalConversation =>
                replied >= _options.RequiredContacts &&
                (_options.RequestedConversationDuration is not { } duration ||
                 _firstVerifiedOutgoingAt is { } first && now - first >= duration),
            // A reply cannot prove a mutually established relationship.
            _ => false,
        };
        var status = _attentionReason is not null
            ? SocialDiscoveryRunStatus.NeedsUserAttention
            : reached
                ? SocialDiscoveryRunStatus.Completed
                : now - _startedAt >= _options.MaximumDuration
                    ? SocialDiscoveryRunStatus.TimedOut
                    : now - _lastProgressAt >= _options.NoProgressTimeout
                        ? SocialDiscoveryRunStatus.Stalled
                        : SocialDiscoveryRunStatus.InProgress;
        var detail = status switch
        {
            SocialDiscoveryRunStatus.Completed => "已达到本轮可核验的社交目标阶段。",
            SocialDiscoveryRunStatus.NeedsUserAttention => _attentionReason!,
            SocialDiscoveryRunStatus.Stalled => "一段时间没有获得新的候选、发送或回复证据；请更换入口或说明当前卡点。",
            SocialDiscoveryRunStatus.TimedOut => "本轮时间预算已用完；请报告已核验进度，不能宣称未达到的目标完成。",
            _ => "正在继续核验候选与会话进度。",
        };
        return new SocialDiscoveryProgressSnapshot(
            status, detail, states.Length, verified, contacted, replied,
            _startedAt, _lastProgressAt, now,
            _contacts.Select(pair => new SocialDiscoveryContactProgress(pair.Key, pair.Value.Stage))
                .ToArray());
    }

    private ContactState GetEligibleContact(SocialContactKey contact)
    {
        contact = Normalize(contact);
        if (!_contacts.TryGetValue(contact, out var state) ||
            state.Stage < SocialDiscoveryContactStage.Verified)
            throw new InvalidOperationException("须先核验该候选人的身份、年龄、关系和聊天资格。");
        return state;
    }

    private void EnsureInProgress()
    {
        if (Snapshot().Status != SocialDiscoveryRunStatus.InProgress)
            throw new InvalidOperationException("本轮社交发现任务已暂停或结束，不能继续记入新证据。");
    }

    private void MarkProgress() => _lastProgressAt = _clock.GetUtcNow();

    private static SocialContactKey Normalize(SocialContactKey contact) =>
        SocialContactKey.Create(contact.Platform, contact.StableContactId);

    private sealed class ContactState
    {
        public SocialDiscoveryContactStage Stage { get; set; }
        public string? ConversationId { get; set; }
        public DateTimeOffset? FirstOutgoingAt { get; set; }
        public HashSet<string> OutgoingEvents { get; } = new(StringComparer.Ordinal);
        public HashSet<string> IncomingEvents { get; } = new(StringComparer.Ordinal);
    }
}
