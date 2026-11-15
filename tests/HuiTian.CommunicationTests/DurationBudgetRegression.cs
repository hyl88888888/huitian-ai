using System.Text.Json;
using System.Text.Json.Nodes;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class DurationBudgetRegression
{
    public static async Task RunAsync()
    {
        var checks = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidDataException(message);
            checks++;
        }

        foreach (var durationSlot in new[] { "\"m\":0,", "\"m\":null,", "" })
        {
            Check(CommunicationIntentResolver.TryParse(
                    "{\"a\":\"chat\",\"p\":\"wechat\",\"r\":\"测试联系人\"," + durationSlot +
                    "\"v\":\"自然交流近况\",\"i\":\"朋友\"}", out var intent) &&
                  intent.CommunicationPlan is { RequestedDuration: null, UserDurationIsEffectiveLimit: false } &&
                  intent.CommunicationPlan.MaximumDuration == CommunicationPlanCompiler.DefaultSessionBudget,
                "Absent/zero/null semantic duration must remain unspecified, with only an internal budget.");
        }

        Check(CommunicationIntentResolver.TryParse(
                  """{"a":"chat","p":"wechat","r":"测试联系人","m":1200,"v":"自然交流近况"}""", out var longIntent) &&
              longIntent.CommunicationPlan is { RequestedDuration.TotalHours: 20, MaximumDuration.TotalHours: 12, UserDurationIsEffectiveLimit: false },
            "The safety ceiling must not erase a longer duration requested by the user.");

        var plan = CommunicationPlanCompiler.CompileGoal("微信", "测试联系人", "自然交流近况", "朋友", true) with
        {
            OpeningMessage = "最近怎么样？",
            OpeningIsSemanticallyComposed = true,
        };
        var savedPlan = JsonSerializer.SerializeToNode(plan)!.AsObject();
        savedPlan.Remove(nameof(CommunicationPlan.RequestedDuration));
        var legacyPlan = savedPlan.Deserialize<CommunicationPlan>();
        Check(legacyPlan is { RequestedDuration: null, UserDurationIsEffectiveLimit: false },
            "Old persisted plans without duration provenance must never be attributed to the user.");

        async Task<CommunicationSessionResult> Expire(CommunicationPlan testPlan)
        {
            var clock = new ManualClock();
            var transport = new ClockedTransport(clock) { PollElapsed = testPlan.MaximumDuration };
            return await new CommunicationConversationExecutor(transport, clock).ExecuteAsync(testPlan, new ComposedReply());
        }

        var unspecified = await Expire(plan);
        Check(unspecified.Status == CommunicationSessionStatus.TimedOut &&
              unspecified.Detail.Contains("没有设置聊天时长", StringComparison.Ordinal) &&
              unspecified.Detail.Contains("系统安全预算", StringComparison.Ordinal),
            "An unspecified session budget ending must pause rather than report successful timed chat.");

        var explicitPlan = plan with { RequestedDuration = TimeSpan.FromMinutes(20), MaximumDuration = TimeSpan.FromMinutes(20) };
        var explicitlyTimed = await Expire(explicitPlan);
        Check(explicitlyTimed.Status == CommunicationSessionStatus.Completed &&
              explicitlyTimed.Detail.Contains("明确设定", StringComparison.Ordinal),
            "Only an actually user-timed open conversation can finish by its requested duration.");

        var clamped = await Expire(plan with { RequestedDuration = TimeSpan.FromHours(20), MaximumDuration = TimeSpan.FromHours(12) });
        Check(clamped.Status == CommunicationSessionStatus.TimedOut &&
              clamped.Detail.Contains("尚未达到", StringComparison.Ordinal),
            "An internal ceiling below a requested duration is not successful fulfillment.");

        var specificGoal = CommunicationPlanCompiler.CompileGoal("微信", "测试联系人", "通知他会议时间", null, true, TimeSpan.FromMinutes(1));
        Check((await Expire(specificGoal)).Status == CommunicationSessionStatus.TimedOut,
            "Elapsed duration alone cannot prove a concrete communication objective was fulfilled.");

        var startupClock = new ManualClock();
        var startupTransport = new ClockedTransport(startupClock)
        {
            OpeningElapsed = TimeSpan.FromHours(1),
            PollElapsed = explicitPlan.MaximumDuration,
        };
        await new CommunicationConversationExecutor(startupTransport, startupClock).ExecuteAsync(explicitPlan, new ComposedReply());
        Check(startupTransport.PollCount == 1,
            "Contact lookup/startup time must not consume the conversation timer before the first outgoing message.");

        var slowClock = new ManualClock();
        var slowTransport = new ClockedTransport(slowClock) { Incoming = "今天有点累", PollElapsed = TimeSpan.FromSeconds(1) };
        var slowResult = await new CommunicationConversationExecutor(slowTransport, slowClock).ExecuteAsync(
            explicitPlan, new ComposedReply(() => slowClock.Advance(explicitPlan.MaximumDuration)));
        Check(slowResult.Status == CommunicationSessionStatus.Completed && slowTransport.ContinuationCount == 0,
            "A reply generated after the deadline must never be sent.");

        using (var cancellation = new CancellationTokenSource())
        {
            var canceledTransport = new ClockedTransport();
            cancellation.Cancel();
            await new CommunicationConversationExecutor(canceledTransport).ExecuteAsync(plan, new ComposedReply(), cancellationToken: cancellation.Token);
            Check(canceledTransport.OpeningCount == 0, "A canceled session must not send an opening even through a non-cooperative transport.");
        }

        using (var cancellation = new CancellationTokenSource())
        {
            var canceledTransport = new ClockedTransport { Incoming = "今天有点累" };
            var canceledResult = await new CommunicationConversationExecutor(canceledTransport).ExecuteAsync(
                plan, new ComposedReply(cancellation.Cancel), cancellationToken: cancellation.Token);
            Check(canceledTransport.ContinuationCount == 0 && canceledResult.Detail.Contains("取消", StringComparison.Ordinal),
                "Cancellation racing with a composed reply must stop external sending and report cancellation honestly.");
        }

        var emptyTransport = new ClockedTransport();
        var emptyResult = await new CommunicationConversationExecutor(emptyTransport).ExecuteAsync(
            plan with { MaximumDuration = TimeSpan.FromMilliseconds(80) }, new ComposedReply());
        Check(emptyResult.Status == CommunicationSessionStatus.TimedOut && emptyTransport.PollCount <= 2,
            "An immediate empty observer must not spin repeatedly until the session budget expires.");

        Console.WriteLine($"PASS: {checks} duration-provenance, honest-stop, deadline, cancellation and no-busy-poll checks (fake transport only)");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }

    private sealed class ComposedReply(Action? onCompose = null) : ICommunicationTurnComposer
    {
        public Task<CommunicationTurnProposal> ComposeAsync(CommunicationPlan plan,
            IReadOnlyList<CommunicationTranscriptEntry> transcript, ObservedChatReply incoming, CancellationToken cancellationToken)
        {
            onCompose?.Invoke();
            return Task.FromResult(new CommunicationTurnProposal("先休息一下，愿意聊聊今天的事吗？", false, false, "测试生成"));
        }
    }

    private sealed class ClockedTransport(ManualClock? clock = null) : IChatConversationTransport
    {
        public TimeSpan OpeningElapsed { get; init; }
        public TimeSpan PollElapsed { get; init; }
        public string? Incoming { get; init; }
        public int OpeningCount { get; private set; }
        public int PollCount { get; private set; }
        public int ContinuationCount { get; private set; }

        public Task<ChatFastPathResult> SendToContactAsync(string platform, string recipient, string message, CancellationToken cancellationToken)
        {
            OpeningCount++;
            clock?.Advance(OpeningElapsed);
            return Task.FromResult(Sent());
        }

        public Task<ChatFastPathResult> SendToCurrentConversationAsync(string platform, string message, CancellationToken cancellationToken)
        {
            ContinuationCount++;
            return Task.FromResult(Sent());
        }

        public Task<ObservedChatReply?> WaitForReplyAsync(CommunicationPlan plan,
            IReadOnlyList<CommunicationTranscriptEntry> transcript, TimeSpan timeout, CancellationToken cancellationToken)
        {
            PollCount++;
            clock?.Advance(PollElapsed);
            return Task.FromResult(Incoming is null ? null : new ObservedChatReply(Incoming, DateTimeOffset.Now, "fixture"));
        }

        private static ChatFastPathResult Sent() => new(ChatFastPathStatus.Completed, "测试发送成功", null, null, null, 1, "completed");
    }
}
