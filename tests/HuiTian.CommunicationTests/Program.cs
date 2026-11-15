using System.Diagnostics;
using HuiTian.Core;
using HuiTian.Infrastructure;

await DurationBudgetRegression.RunAsync();

const string compoundRequest =
    "和微信里的张亚博聊天，以老板的身份，目的是督促她完成今天的工作并告知她明天早上九点开会";
Assert(
    ChatFastPathParser.TryParse(compoundRequest, false, out var compound),
    "the exact compound request must enter the chat fast path");
Assert(compound.Recipient == "张亚博", "the recipient must not absorb the instructions");
var plan = compound.CommunicationPlan ??
           throw new InvalidDataException("a goal conversation must carry an executable plan");
Assert(plan!.Mode == CommunicationExecutionMode.ContinueUntilObjective,
    "the verb 'chat' must retain a bounded continuation state");
Assert(plan.Objectives.Count == 2,
    "the work reminder and meeting notice must remain separate objectives");
Assert(plan.OpeningMessage.Contains("完成今天的工作", StringComparison.Ordinal) &&
       plan.OpeningMessage.Contains("明天早上九点开会", StringComparison.Ordinal),
    "the opening message must not drop the second parallel objective");
Assert(plan.MaximumOutgoingMessages is > 1 and <= 24,
    "continuous chat must be useful but bounded");

Assert(
    ChatFastPathParser.TryParse(
        "和微信里的张亚博聊天10分钟，目的是了解她今天的进展",
        false,
        out var timedConversation) &&
    timedConversation.CommunicationPlan is
    {
        MaximumDuration.TotalMinutes: 10,
        MaximumOutgoingMessages: > 6 and <= 24,
    },
    "an explicit chat duration must be retained instead of falling back to a one-shot task");

Assert(
    ChatFastPathParser.TryParse(
        "代替我和微信里的张亚博自动持续聊天对话，以老板的身份，目的是督促她完成今天的工作",
        false,
        out var automaticConversation) &&
    automaticConversation.Recipient == "张亚博" &&
    automaticConversation.Persona == "老板" &&
    automaticConversation.ContinuousConversationRequested,
    "natural continuous-chat modifiers must not be swallowed into the contact name");

Assert(
    ChatFastPathParser.TryParse(
        "以老板身份和微信里的张亚博聊天十分钟，目的是确认工作进度",
        false,
        out var leadingPersonaConversation) &&
    leadingPersonaConversation.Persona == "老板" &&
    leadingPersonaConversation.CommunicationPlan?.MaximumDuration.TotalMinutes == 10,
    "persona-before-platform wording and Chinese duration must retain their slots");

Assert(
    ChatFastPathParser.TryParse(
        "代替我和微信里的女朋友聊天，聊10个小时，以女朋友身份，目的是给她提供情绪价值，哄她开心",
        false,
        out var tenHourConversation) &&
    tenHourConversation.ContinuousConversationRequested &&
    tenHourConversation.CommunicationPlan is
    {
        MaximumDuration.TotalHours: 10,
        MaximumOutgoingMessages: > 24 and <= 360,
    },
    "a natural 10-hour request must remain a long but bounded continuous conversation");
var socialPlan = tenHourConversation.CommunicationPlan ??
                 throw new InvalidDataException("the social request must carry a plan");
Assert(
    CommunicationOpeningPolicy.RequiresSemanticComposition(socialPlan),
    "an arbitrary relationship and private purpose must require semantic opening composition");
var safeSocialOpening = CommunicationOpeningPolicy.ComposeSafeImmediateOpening(socialPlan);
Assert(
    !safeSocialOpening.Contains("情绪价值", StringComparison.Ordinal) &&
    !safeSocialOpening.Contains("目的", StringComparison.Ordinal) &&
    safeSocialOpening.Length > 4,
    "the low-latency opening must be natural and must not expose the private social goal");
var repeatedSocialOpenings = new[] { safeSocialOpening }
    .Concat(Enumerable.Range(0, 11)
        .Select(_ => CommunicationOpeningPolicy.ComposeSafeImmediateOpening(socialPlan)))
    .ToArray();
Assert(
    repeatedSocialOpenings
        .Zip(repeatedSocialOpenings.Skip(1))
        .All(pair => !pair.First.Equals(pair.Second, StringComparison.Ordinal)) &&
    repeatedSocialOpenings.Distinct(StringComparer.Ordinal).Count() >= 4,
    "equivalent social requests must rotate through several openings without consecutive repeats");
Assert(
    repeatedSocialOpenings.All(opening =>
        !opening.Contains("情绪价值", StringComparison.Ordinal) &&
        !opening.Contains("哄她开心", StringComparison.Ordinal) &&
        !opening.Contains("女朋友身份", StringComparison.Ordinal) &&
        !opening.Contains("目的", StringComparison.Ordinal)),
    "every immediate-opening candidate must keep persona and private-goal instructions internal");

Assert(CommunicationStylePolicy.CopiesUserSample(
        "今儿怎么样呀，想听你聊聊。",
        ["我平时会说：今儿怎么样呀，想听你聊聊。"]),
    "a model must not copy a full user style sentence into an outgoing message");
Assert(!CommunicationStylePolicy.CopiesUserSample(
        "这周有什么新鲜事？",
        ["我平时会说：今儿怎么样呀，想听你聊聊。"]),
    "a new sentence in a similar casual tone must remain sendable");

var copiedOpeningTransport = new ScriptedTransport([]);
var copiedOpeningPlan = socialPlan with
{
    UserStyleSamples = ["我平时会说：今儿怎么样呀，想听你聊聊。"],
    OpeningMessage = "今儿怎么样呀，想听你聊聊。",
    OpeningIsSemanticallyComposed = true,
};
var copiedOpeningResult = await new CommunicationConversationExecutor(copiedOpeningTransport)
    .ExecuteAsync(copiedOpeningPlan, new FastCommunicationTurnComposer());
Assert(copiedOpeningResult.Status == CommunicationSessionStatus.Failed &&
       copiedOpeningTransport.ContactSendCount == 0,
    "an opening copied from a style example must stop before any external send");

var copiedContinuationTransport = new ScriptedTransport(["今天还挺有意思的"]);
var copiedContinuationResult = await new CommunicationConversationExecutor(copiedContinuationTransport)
    .ExecuteAsync(copiedOpeningPlan with { OpeningMessage = "今天你忙什么呢？" },
        new FixedTurnComposer("今儿怎么样呀，想听你聊聊。"));
Assert(copiedContinuationResult.Status == CommunicationSessionStatus.NeedsUserAttention &&
       copiedContinuationTransport.ContactSendCount == 1,
    "a copied style example in a continuation must be blocked before the second send");

var namedSocialPlan = socialPlan with { Recipient = "小雅" };
var namedSocialOpening = CommunicationOpeningPolicy.ComposeSafeImmediateOpening(namedSocialPlan);
Assert(
    namedSocialOpening.StartsWith("小雅，", StringComparison.Ordinal),
    "a real contact name should be used as a natural salutation");

var professionalConversation = CommunicationPlanCompiler.CompileGoal(
    "微信",
    "小王",
    "围绕项目进展和风险自然交流",
    "项目负责人",
    continueConversation: true,
    requestedDuration: TimeSpan.FromMinutes(10));
Assert(
    CommunicationOpeningPolicy.IsOpenEnded(professionalConversation),
    "an ambiguous professional discussion should use the safe immediate-opening path");
var professionalOpenings = Enumerable.Range(0, 6)
    .Select(_ => CommunicationOpeningPolicy.ComposeSafeImmediateOpening(professionalConversation))
    .ToArray();
Assert(
    professionalOpenings.All(opening => opening.StartsWith("小王，", StringComparison.Ordinal)) &&
    professionalOpenings.Any(opening =>
        opening.Contains("进展", StringComparison.Ordinal) ||
        opening.Contains("工作", StringComparison.Ordinal) ||
        opening.Contains("推进", StringComparison.Ordinal)),
    "recipient, persona and goal category should shape a natural professional opening");

using (var noModelOpeningComposer = new LocalCommunicationTurnComposer(
           new AgentBackendConfiguration(
               "must-not-be-called",
               "offline-test",
               "http://127.0.0.1:0/v1",
               IsProductDefault: false)))
using (var alreadyCancelled = new CancellationTokenSource())
{
    alreadyCancelled.Cancel();
    var immediateOpening = await noModelOpeningComposer.ComposeOpeningAsync(
        socialPlan,
        alreadyCancelled.Token);
    Assert(
        !string.IsNullOrWhiteSpace(immediateOpening),
        "sustained social chat must compose its first send synchronously without contacting the local model");

    var styledPlan = socialPlan with
    {
        UserStyleSamples = ["我平时会说：今儿怎么样呀，想听你聊聊。"],
    };
    try
    {
        await noModelOpeningComposer.ComposeOpeningAsync(styledPlan, alreadyCancelled.Token);
        throw new InvalidDataException("a styled opening bypassed the model and used a generic template");
    }
    catch (OperationCanceledException) { }

    var openingContextMethod = typeof(LocalCommunicationTurnComposer).GetMethod(
        "BuildOpeningContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    var openingContext = (string?)openingContextMethod?.Invoke(null, [styledPlan]);
    Assert(openingContext?.Contains("今儿怎么样呀", StringComparison.Ordinal) == true &&
           openingContext.Contains("不要复制其中的事实", StringComparison.Ordinal),
        "the local opening composer must receive only explicit user style examples as guarded context");

    var conversationContextMethod = typeof(LocalCommunicationTurnComposer).GetMethod(
        "BuildOpenEndedConversationContext", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    var conversationContext = (string?)conversationContextMethod?.Invoke(null,
        [styledPlan, Array.Empty<CommunicationTranscriptEntry>()]);
    Assert(conversationContext?.Contains("今儿怎么样呀", StringComparison.Ordinal) == true,
        "the open-ended continuation composer must retain the same user-provided style examples");

    var styledFastReply = await new FastCommunicationTurnComposer(
        new FixedTurnComposer("按本人语气写的下一句"))
        .ComposeAsync(styledPlan, [],
            new ObservedChatReply("今天做不完了", DateTimeOffset.Now, "style-blocker"),
            CancellationToken.None);
    Assert(styledFastReply.Reply == "按本人语气写的下一句",
        "a style-bearing continuation must bypass fixed blocker templates");
}

var blockedPrivateGoalTransport = new ScriptedTransport([]);
var blockedPrivateGoal = await new CommunicationConversationExecutor(blockedPrivateGoalTransport)
    .ExecuteAsync(socialPlan, new FastCommunicationTurnComposer());
Assert(
    blockedPrivateGoal.Status == CommunicationSessionStatus.Failed &&
    blockedPrivateGoalTransport.ContactSendCount == 0,
    "an internal social goal must never be pasted into a real chat before semantic composition");

Assert(
    ChatFastPathParser.TryParse(
        "替我和微信里的女朋友聊天10个小时，以女朋友身份，目的是安慰她并让她开心",
        false,
        out var shortDelegateConversation) &&
    shortDelegateConversation.CommunicationPlan?.MaximumDuration.TotalHours == 10,
    "the common short wording '替我' must retain the same bounded long-session plan");

var composedSocialPlan = socialPlan with
{
    OpeningMessage = "今天过得怎么样？想听你聊聊。",
    OpeningIsSemanticallyComposed = true,
};
var openEndedProposal = await new FastCommunicationTurnComposer(
        new FixedTurnComposer("好呀，那你今天有什么开心或不开心的事想和我说？"))
    .ComposeAsync(
        composedSocialPlan,
        [],
        new ObservedChatReply("好的呀", DateTimeOffset.Now, "social-ack"),
        CancellationToken.None);
Assert(
    !openEndedProposal.ObjectiveSatisfied &&
    openEndedProposal.Reply is { Length: > 10 },
    "a casual acknowledgement must not prematurely end an open-ended social conversation");

Assert(
    LocalCommunicationTurnComposer.ParseOpeningMessage(
        "{\"r\":\"今天过得怎么样？想听你聊聊。\"}") ==
    "今天过得怎么样？想听你聊聊。",
    "a structured semantic opening must be extracted without exposing its internal goal");

Assert(
    ChatFastPathParser.TryParse(
        "和微信里的小王聊天十小时，目的是了解近况",
        false,
        out var chineseHourConversation) &&
    chineseHourConversation.CommunicationPlan?.MaximumDuration.TotalHours == 10,
    "Chinese hour wording must be parsed without requiring the classifier to know the role or goal");

Assert(
    ChatFastPathParser.TryParse(
        "和微信里的Alex聊天1.5 hours，目的是确认行程",
        false,
        out var englishHourConversation) &&
    englishHourConversation.CommunicationPlan?.MaximumDuration.TotalMinutes == 90,
    "English hour units and decimal durations must be retained");

Assert(
    ChatFastPathParser.TryParse(
        "和微信里的李雷聊天90 minutes，目的是了解项目进展",
        false,
        out var englishMinuteConversation) &&
    englishMinuteConversation.CommunicationPlan?.MaximumDuration.TotalMinutes == 90,
    "English minute units must remain supported beyond the previous 30-minute ceiling");

Assert(
    ChatFastPathParser.TryParse(
        "和微信里的小李聊天二十个小时，目的是了解近况",
        false,
        out var clampedConversation) &&
    clampedConversation.CommunicationPlan is
    {
        MaximumDuration.TotalHours: 12,
        MaximumOutgoingMessages: 360,
    },
    "an excessive duration must be clamped to the 12-hour and 360-message safety ceilings");

const string unfamiliarParallelRequest =
    "和微信里的李雷沟通，以项目负责人的身份，目的是请他梳理风险，同时把预算变更原因讲清楚，然后问清交付日期";
Assert(
    ChatFastPathParser.TryParse(unfamiliarParallelRequest, false, out var unfamiliar),
    "an unfamiliar parallel request must enter the structured fast path");
var unfamiliarPlan = unfamiliar.CommunicationPlan ??
                     throw new InvalidDataException("an unfamiliar goal must carry a plan");
Assert(
    unfamiliarPlan.Objectives.Count == 3,
    "parallel goals must be split structurally rather than by one hard-coded business sentence");
Assert(unfamiliarPlan!.OpeningMessage.Contains("梳理风险", StringComparison.Ordinal) &&
       unfamiliarPlan.OpeningMessage.Contains("预算变更原因讲清楚", StringComparison.Ordinal) &&
       unfamiliarPlan.OpeningMessage.Contains("交付日期", StringComparison.Ordinal),
    "unknown objective wording must be preserved instead of discarded");

Assert(
    ChatFastPathParser.TryParse(
        "用QQ联系小王，提醒他明天九点参加会议",
        false,
        out var oneShot) &&
    oneShot.CommunicationPlan is { Mode: CommunicationExecutionMode.DeliverOnce },
    "a direct notification must remain a one-shot delivery");

Assert(
    ChatFastPathParser.TryParse(
        "替我和微信当前这个人继续聊十分钟，看看她说什么再安慰她",
        false,
        out var currentConversation) &&
    currentConversation.CommunicationPlan is
    {
        UseCurrentConversation: true,
        Mode: CommunicationExecutionMode.ContinueUntilObjective,
    },
    "a request aimed at the visible conversation must bypass contact search");
var currentConversationTransport = new ScriptedTransport([]);
var executableCurrentConversationPlan = currentConversation.CommunicationPlan! with
{
    OpeningMessage = "最近怎么样？想听你说说。",
    OpeningIsSemanticallyComposed = true,
};
var currentConversationResult = await new CommunicationConversationExecutor(currentConversationTransport)
    .ExecuteAsync(
        executableCurrentConversationPlan,
        new FastCommunicationTurnComposer(),
        continueWhenRepliesArrive: false);
Assert(
    currentConversationResult.Status == CommunicationSessionStatus.WaitingForReply &&
    currentConversationTransport.ContactSendCount == 0 &&
    currentConversationTransport.CurrentConversationSendCount == 1,
    "the first message for the current visible conversation must use the current-chat send path exactly once");

var scriptedTransport = new ScriptedTransport(
[
    "今天可能做不完，需求这里有问题。",
    "好的，问题解决后我会完成，明早九点也会参加。",
]);
var executor = new CommunicationConversationExecutor(scriptedTransport);
var continued = await executor.ExecuteAsync(
    plan,
    new FastCommunicationTurnComposer(),
    continueWhenRepliesArrive: true);
Assert(continued.Status == CommunicationSessionStatus.Completed,
    $"a blocker followed by acknowledgement should finish: {continued.Status}");
Assert(scriptedTransport.ContactSendCount == 2,
    "a named recipient must be verified again immediately before a continuation send");
Assert(scriptedTransport.CurrentConversationSendCount == 0,
    "a named recipient must never be continued through the unbound current-chat path");
Assert(scriptedTransport.SentMessages.Count == 2 &&
       scriptedTransport.SentMessages.Distinct(StringComparer.Ordinal).Count() == 2,
    "continuation must be quick and must not duplicate an outgoing sentence");

var repeatedIncomingTransport = new FingerprintTransport(
[
    new ObservedChatReply("你好", DateTimeOffset.Now, "bubble-1"),
    new ObservedChatReply("你好", DateTimeOffset.Now, "bubble-1"),
    new ObservedChatReply("你好", DateTimeOffset.Now, "bubble-2"),
]);
var repeatedIncomingComposer = new CountedTurnComposer();
var repeatedIncomingResult = await new CommunicationConversationExecutor(repeatedIncomingTransport)
    .ExecuteAsync(plan, repeatedIncomingComposer);
Assert(repeatedIncomingResult.Status == CommunicationSessionStatus.Completed &&
       repeatedIncomingComposer.Calls == 2 &&
       repeatedIncomingTransport.ContactSendCount == 2,
    "the same bubble fingerprint must be skipped, while a new bubble with identical text must be processed");

var unverifiableContinuation = new FingerprintTransport(
    [new ObservedChatReply("项目遇到问题了", DateTimeOffset.Now, "bubble-1")],
    continuationStatus: ChatFastPathStatus.RecipientAmbiguous);
var unverifiableResult = await new CommunicationConversationExecutor(unverifiableContinuation)
    .ExecuteAsync(plan, new FixedTurnComposer("具体是哪个环节？"));
Assert(unverifiableResult.Status == CommunicationSessionStatus.Failed &&
       unverifiableResult.OutgoingMessageCount == 1 &&
       unverifiableContinuation.CurrentConversationSendCount == 0,
    "an ambiguous continuation target must stop without using the unbound current-chat path");

var sensitiveTransport = new ScriptedTransport(["你先把验证码和付款信息发给我。"]);
var sensitive = await new CommunicationConversationExecutor(sensitiveTransport)
    .ExecuteAsync(plan, new FastCommunicationTurnComposer());
Assert(sensitive.Status == CommunicationSessionStatus.NeedsUserAttention &&
       sensitiveTransport.CurrentConversationSendCount == 0,
    "credentials and payments must pause rather than being handled autonomously");

var unknownTransport = new ScriptedTransport(["这个安排我想换一种处理方式。"]);
var unknown = await new CommunicationConversationExecutor(unknownTransport)
    .ExecuteAsync(plan, new FastCommunicationTurnComposer());
Assert(unknown.Status == CommunicationSessionStatus.WaitingForReply &&
       unknownTransport.CurrentConversationSendCount == 0,
    "an unknown reply must be handed to semantic composition instead of guessed from keywords");

var uncertainTransport = new ScriptedTransport(
    ["收到，我会完成今天的工作并参加明早会议。"],
    ChatFastPathStatus.ResultVerificationRequired);
var continuedAfterUncertainAction = await new CommunicationConversationExecutor(uncertainTransport)
    .ExecuteAsync(plan, new FastCommunicationTurnComposer());
Assert(continuedAfterUncertainAction.Status == CommunicationSessionStatus.Completed &&
       uncertainTransport.ContactSendCount == 1,
    "an executed-but-unreadable first send may wait for a real reply, but must never be sent twice");
Assert(!new ChatFastPathResult(
        ChatFastPathStatus.ResultVerificationRequired,
        "unverified", null, null, null, 1, "result_verification_required").Success,
    "an unreadable receipt must not be reported as a verified successful send");

var adaptive = LocalCommunicationTurnComposer.ParseProposal(
    "{\"action\":\"reply\",\"reply\":\"我理解你担心时间不够。先把目前的阻碍和已完成部分发我，我来协调优先级；明早九点会议不变，我们会上一起定后续安排。\",\"reason\":\"需要同时回应顾虑并推进原目标\"}");
Assert(adaptive.Reply is { Length: > 30 } && !adaptive.ObjectiveSatisfied,
    "contextual chat replies must allow a complete explanation instead of forcing one short sentence");
var compactAdaptive = LocalCommunicationTurnComposer.ParseProposal(
    "{\"a\":\"r\",\"r\":\"我理解你的顾虑，请先把卡点和已完成部分发我，我们一起调整安排。\"}");
Assert(compactAdaptive.Reply is { Length: > 10 },
    "the low-latency compact reply schema must remain context-capable");
var naturalOpenEndedReply = LocalCommunicationTurnComposer.ParseOpenEndedReply(
    "<think>internal</think>\n回复：怎么了宝，跟我说说");
Assert(
    naturalOpenEndedReply.Reply == "怎么了宝，跟我说说" &&
    !naturalOpenEndedReply.ObjectiveSatisfied,
    "the social-chat hot path must accept only the visible natural reply without JSON or thinking text");

var echoTranscript = new List<CommunicationTranscriptEntry>
{
    new(
        "assistant",
        "抱抱，怎么啦？是工作学习上的事，还是遇到什么不开心的事啦？",
        DateTimeOffset.Now),
};
Assert(
    CommunicationTranscriptPolicy.IsLikelyOwnMessageEcho(
        echoTranscript,
        "抱抱，怎么啦？ 是工作/学习上的事，还是遇到什么不开心的事啦？"),
    "punctuation and whitespace differences in an outgoing OCR echo must be ignored");
Assert(
    CommunicationTranscriptPolicy.IsLikelyOwnMessageEcho(
        echoTranscript,
        "是工作学习上的事还是遇到什么不开心的事啦"),
    "a long OCR fragment of the outgoing green bubble must be ignored");
Assert(
    !CommunicationTranscriptPolicy.IsLikelyOwnMessageEcho(
        echoTranscript,
        "工作和学习都有事"),
    "a genuine short contact reply must never be mistaken for our outgoing message");
Assert(
    !CommunicationTranscriptPolicy.IsLikelyOwnMessageEcho(
        echoTranscript,
        "你刚才问是工作学习上的事还是遇到不开心的事，我想说其实两边都有"),
    "a contact quoting our wording and adding a real answer must remain visible");

var delayedObserver = new DelayedPrimeObserver();
var backgroundPrimeTransport = new WindowsChatConversationTransport(
    new ImmediateChatSender(),
    delayedObserver);
var backgroundSend = backgroundPrimeTransport.SendToContactAsync(
    professionalConversation.Platform,
    professionalConversation.Recipient,
    "小王，项目现在进展怎么样？",
    CancellationToken.None);
Assert(
    await Task.WhenAny(backgroundSend, Task.Delay(750)) == backgroundSend,
    "the first-send result must not wait for the persistent reply observer to start");
await delayedObserver.PrimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
Assert(
    !delayedObserver.PrimeCompleted,
    "the latency test must keep observer priming blocked after send returns");
var waitAfterPrime = backgroundPrimeTransport.WaitForReplyAsync(
    professionalConversation,
    [],
    TimeSpan.FromSeconds(2),
    CancellationToken.None);
await Task.Delay(80);
Assert(
    !waitAfterPrime.IsCompleted,
    "reply polling must join the in-flight baseline task before reading messages");
delayedObserver.ReleasePrime();
var observedAfterPrime = await waitAfterPrime.WaitAsync(TimeSpan.FromSeconds(2));
Assert(
    observedAfterPrime?.Text == "真实的新回复",
    "reply polling must continue normally after background priming completes");
await backgroundPrimeTransport.EndSessionAsync(
    professionalConversation.Platform,
    professionalConversation.Recipient);

var canceledObserver = new DelayedPrimeObserver();
var canceledTransport = new WindowsChatConversationTransport(new ImmediateChatSender(), canceledObserver);
await canceledTransport.SendToContactAsync("微信", "本地测试对象", "测试", CancellationToken.None);
await canceledObserver.PrimeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
await canceledTransport.EndSessionAsync("微信", "本地测试对象").WaitAsync(TimeSpan.FromSeconds(2));
Assert(canceledObserver.PrimeCanceled && canceledObserver.StopCount == 1,
    "ending a session must cancel in-flight initialization before disposing its observer");

var currentObserver = new DelayedPrimeObserver();
var currentTransport = new WindowsChatConversationTransport(new ImmediateChatSender(), currentObserver);
var currentPlan = professionalConversation with { UseCurrentConversation = true, Recipient = "@current" };
await currentTransport.SendToCurrentConversationAsync("微信", "测试", CancellationToken.None);
var timedPoll = await currentTransport.WaitForReplyAsync(currentPlan, [], TimeSpan.FromMilliseconds(100), CancellationToken.None);
Assert(timedPoll is null && currentObserver.PrimeCount == 1,
    "a current-conversation start must initialize once, and an observation timeout is not a session failure");
currentObserver.ReleasePrime();
var currentReply = await currentTransport.WaitForReplyAsync(currentPlan, [], TimeSpan.FromSeconds(2), CancellationToken.None);
Assert(currentReply is not null && currentObserver.PrimeCount == 1,
    "the next poll must reuse initialization rather than drop the first reply or restart OCR");
await currentTransport.EndSessionAsync(currentPlan.Platform, currentPlan.Recipient);

var boundSender = new ImmediateChatSender();
var boundObserver = new BoundTargetObserver("小雅");
var boundTransport = new WindowsChatConversationTransport(boundSender, boundObserver);
await boundTransport.SendToCurrentConversationAsync("微信", "第一句", CancellationToken.None);
await boundTransport.WaitForReplyAsync(currentPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
var boundContinuation = await boundTransport.SendToCurrentConversationAsync(
    "微信", "第二句", CancellationToken.None);
Assert(boundContinuation.Success && boundSender.CurrentSendCount == 1 &&
       boundSender.NamedRecipients.SequenceEqual(["小雅"]),
    "a current-conversation continuation must be sent through its observed, named recipient");
await boundTransport.EndSessionAsync(currentPlan.Platform, currentPlan.Recipient);

var unreadableSender = new ImmediateChatSender();
var unreadableTransport = new WindowsChatConversationTransport(
    unreadableSender, new BoundTargetObserver(null));
await unreadableTransport.SendToCurrentConversationAsync("微信", "第一句", CancellationToken.None);
await unreadableTransport.WaitForReplyAsync(currentPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
var unreadableContinuation = await unreadableTransport.SendToCurrentConversationAsync(
    "微信", "第二句", CancellationToken.None);
Assert(unreadableContinuation.Status == ChatFastPathStatus.TargetVerificationRequired &&
       unreadableSender.CurrentSendCount == 1 && unreadableSender.NamedRecipients.Count == 0,
    "a continuation must stop if the original current chat cannot be bound to a readable title");
await unreadableTransport.EndSessionAsync(currentPlan.Platform, currentPlan.Recipient);

var mismatchedObserver = new MismatchedPrimeObserver();
var mismatchedTransport = new WindowsChatConversationTransport(
    new ImmediateChatSender(), mismatchedObserver);
var mismatchedPlan = professionalConversation with
{
    OpeningMessage = "小王，项目最近怎么样？",
    OpeningIsSemanticallyComposed = true,
};
var mismatchedSession = await new CommunicationConversationExecutor(mismatchedTransport)
    .ExecuteAsync(mismatchedPlan, new FixedTurnComposer("下一句"));
Assert(mismatchedSession.Status == CommunicationSessionStatus.NeedsUserAttention &&
       mismatchedSession.OutgoingMessageCount == 1,
    "a mismatched watcher title must stop the session before observing or sending another reply");
await mismatchedTransport.EndSessionAsync(
    professionalConversation.Platform, professionalConversation.Recipient);
Assert(mismatchedObserver.StopCount == 1,
    "watcher identity failure must still permit complete session cleanup");

var sharedDesktop = new SwitchingChatDesktop();
var multiTransport = new WindowsChatConversationTransport(
    sharedDesktop, sharedDesktop, sharedDesktop, TimeSpan.Zero);
var twoPlans = new[]
{
    professionalConversation with
    {
        Recipient = "小甲",
        OpeningMessage = "小甲，今天情况如何？",
        OpeningIsSemanticallyComposed = true,
        MaximumDuration = TimeSpan.FromSeconds(5),
    },
    professionalConversation with
    {
        Recipient = "小乙",
        OpeningMessage = "小乙，今天情况如何？",
        OpeningIsSemanticallyComposed = true,
        MaximumDuration = TimeSpan.FromSeconds(5),
    },
};
sharedDesktop.QueueReply("小甲", "甲方第一条", "甲方第二条");
sharedDesktop.QueueReply("小乙", "乙方第一条", "乙方第二条");
var multiResults = await new CommunicationMultiSessionExecutor(multiTransport)
    .ExecuteAsync(twoPlans, _ => new CountedTurnComposer());
Assert(multiResults.Count == 2 &&
       multiResults.All(result => result.Status == CommunicationSessionStatus.Completed &&
                                  result.OutgoingMessageCount == 2),
    "two named contacts must each keep an independent bounded session and transcript");
Assert(sharedDesktop.NamedSends.Count(send => send.Recipient == "小甲") == 2 &&
       sharedDesktop.NamedSends.Count(send => send.Recipient == "小乙") == 2 &&
       sharedDesktop.CurrentSendCount == 0,
    $"multi-contact continuation must send only through the named recipient path " +
    $"(A={sharedDesktop.NamedSends.Count(send => send.Recipient == "小甲")}, " +
    $"B={sharedDesktop.NamedSends.Count(send => send.Recipient == "小乙")}, " +
    $"current={sharedDesktop.CurrentSendCount}, navigations={sharedDesktop.NavigationCount})");
Assert(multiResults[0].Transcript.Any(entry => entry.Text == "甲方第一条") &&
       !multiResults[0].Transcript.Any(entry => entry.Text == "乙方第一条") &&
       multiResults[1].Transcript.Any(entry => entry.Text == "乙方第一条") &&
       !multiResults[1].Transcript.Any(entry => entry.Text == "甲方第一条"),
    "a visible chat switch must never leak another contact's reply into this contact's transcript");
try
{
    await new CommunicationMultiSessionExecutor(multiTransport).ExecuteAsync(
        [twoPlans[0], twoPlans[0] with { Platform = "WeChat" }],
        _ => new CountedTurnComposer());
    throw new InvalidDataException("same contact was allowed as two simultaneous sessions");
}
catch (ArgumentException) { }
try
{
    await new CommunicationMultiSessionExecutor(multiTransport).ExecuteAsync(
        [twoPlans[0], currentPlan], _ => new CountedTurnComposer());
    throw new InvalidDataException("an unbound current-chat target joined a multi-contact session");
}
catch (ArgumentException) { }

var restoredDesktop = new SwitchingChatDesktop();
var restoredTransport = new WindowsChatConversationTransport(
    restoredDesktop, restoredDesktop, restoredDesktop, TimeSpan.Zero);
var restoredPlan = twoPlans[0];
await restoredTransport.SendToContactAsync(
    restoredPlan.Platform, restoredPlan.Recipient, restoredPlan.OpeningMessage,
    CancellationToken.None);
await restoredTransport.WaitForReplyAsync(
    restoredPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
restoredDesktop.QueueReply(restoredPlan.Recipient, "切回后的一条消息");
await restoredDesktop.SendAsync("微信", "另一人", "模拟切换可见会话");
var restoredReply = await restoredTransport.WaitForReplyAsync(
    restoredPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
Assert(restoredDesktop.NavigationCount > 0 &&
       restoredReply?.Text == "切回后的一条消息",
    "a temporary switch must restore and verify the named target before reading a reply");
await restoredTransport.EndSessionAsync(restoredPlan.Platform, restoredPlan.Recipient);

var blockedDesktop = new SwitchingChatDesktop();
var blockedTransport = new WindowsChatConversationTransport(
    blockedDesktop, blockedDesktop, blockedDesktop, TimeSpan.Zero);
var blockedPlan = twoPlans[0];
await blockedTransport.SendToContactAsync(
    blockedPlan.Platform, blockedPlan.Recipient, blockedPlan.OpeningMessage,
    CancellationToken.None);
await blockedTransport.WaitForReplyAsync(
    blockedPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
blockedDesktop.BlockAndSwitchAway(blockedPlan.Recipient);
for (var attempt = 0; attempt < 2; attempt++)
{
    var noReply = await blockedTransport.WaitForReplyAsync(
        blockedPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
    Assert(noReply is null,
        "a temporary inability to restore a named chat must pause without reading another target");
}
try
{
    await blockedTransport.WaitForReplyAsync(
        blockedPlan, [], TimeSpan.FromSeconds(1), CancellationToken.None);
    throw new InvalidDataException("three failed target verifications did not stop the session");
}
catch (ChatTargetMismatchException) { }
await blockedTransport.EndSessionAsync(blockedPlan.Platform, blockedPlan.Recipient);

using (var cancelledPoll = new CancellationTokenSource())
{
    cancelledPoll.Cancel();
    try
    {
        await currentTransport.WaitForReplyAsync(currentPlan, [], TimeSpan.FromSeconds(1), cancelledPoll.Token);
        throw new InvalidDataException("caller cancellation was incorrectly hidden as an empty reply");
    }
    catch (OperationCanceledException) { }
    await currentTransport.EndSessionAsync(currentPlan.Platform, currentPlan.Recipient);
}

if (args.Contains("--live-chat-latency", StringComparer.OrdinalIgnoreCase))
{
    using var liveComposer = new LocalCommunicationTurnComposer(
        AgentBackendConfiguration.ProductDefault);
    var livePlan = socialPlan with
    {
        OpeningMessage = "今天怎么样？想听你说说。",
        OpeningIsSemanticallyComposed = true,
    };

    async Task<(CommunicationTurnProposal Proposal, long Milliseconds)> Measure(string incomingText)
    {
        var incoming = new ObservedChatReply(
            incomingText,
            DateTimeOffset.Now,
            Guid.NewGuid().ToString("N"));
        CommunicationTranscriptEntry[] recent =
        [
            new("assistant", livePlan.OpeningMessage, DateTimeOffset.Now.AddSeconds(-10)),
            new("contact", incoming.Text, incoming.Timestamp),
        ];
        var stopwatch = Stopwatch.StartNew();
        var proposal = await liveComposer.ComposeAsync(
            livePlan,
            recent,
            incoming,
            CancellationToken.None);
        stopwatch.Stop();
        return (proposal, stopwatch.ElapsedMilliseconds);
    }

    var cold = await Measure("今天工作特别烦，忙了一天还是没做完。");
    var warm = await Measure("我现在脑子很乱，又不知道该从哪里说起。");
    Assert(!string.IsNullOrWhiteSpace(warm.Proposal.Reply),
        "live composer must produce a reply");
    Assert(warm.Proposal.Reason.Contains("持续聊天快速通道", StringComparison.Ordinal),
        $"live composer fell back instead of returning a model reply: {warm.Proposal.Reason}");
    Assert(warm.Milliseconds < 10_000,
        $"warm chat composition exceeded 10s: {warm.Milliseconds}ms");
    Console.WriteLine(
        $"LIVE_CHAT cold={cold.Milliseconds}ms warm={warm.Milliseconds}ms reply={warm.Proposal.Reply}");
}

Console.WriteLine(
    "PASS: varied low-latency openings, compound goals, bounded continuation, outgoing-echo filtering, duplicate prevention, and sensitive-step pause");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

file sealed class ScriptedTransport : IChatConversationTransport
{
    private readonly Queue<string> _incoming;
    private readonly ChatFastPathStatus _firstStatus;

    public ScriptedTransport(
        IEnumerable<string> incoming,
        ChatFastPathStatus firstStatus = ChatFastPathStatus.Completed)
    {
        _incoming = new Queue<string>(incoming);
        _firstStatus = firstStatus;
    }

    public int ContactSendCount { get; private set; }
    public int CurrentConversationSendCount { get; private set; }
    public List<string> SentMessages { get; } = [];

    public Task<ChatFastPathResult> SendToContactAsync(
        string platform,
        string recipient,
        string message,
        CancellationToken cancellationToken)
    {
        ContactSendCount++;
        SentMessages.Add(message);
        return Task.FromResult(Result(_firstStatus));
    }

    public Task<ChatFastPathResult> SendToCurrentConversationAsync(
        string platform,
        string message,
        CancellationToken cancellationToken)
    {
        CurrentConversationSendCount++;
        SentMessages.Add(message);
        return Task.FromResult(Completed());
    }

    public Task<ObservedChatReply?> WaitForReplyAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_incoming.Count == 0)
        {
            return Task.FromResult<ObservedChatReply?>(null);
        }

        var value = _incoming.Dequeue();
        return Task.FromResult<ObservedChatReply?>(new ObservedChatReply(
            value,
            DateTimeOffset.Now,
            Guid.NewGuid().ToString("N")));
    }

    private static ChatFastPathResult Result(ChatFastPathStatus status) => new(
        status,
        status == ChatFastPathStatus.Completed ? "发送已完成。" : "发送动作已执行。",
        null,
        null,
        null,
        20,
        status == ChatFastPathStatus.Completed
            ? "completed"
            : "result_verification_required");

    private static ChatFastPathResult Completed() => Result(ChatFastPathStatus.Completed);
}

file sealed class FixedTurnComposer(string reply) : ICommunicationTurnComposer
{
    public Task<CommunicationTurnProposal> ComposeAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CommunicationTurnProposal(
            reply,
            false,
            false,
            "已按对方内容和用户目的自适应回复。"));
}

file sealed class CountedTurnComposer : ICommunicationTurnComposer
{
    public int Calls { get; private set; }

    public Task<CommunicationTurnProposal> ComposeAsync(
        CommunicationPlan plan,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        ObservedChatReply incoming,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Calls == 1
            ? new CommunicationTurnProposal("你好，最近怎么样？", false, false, "继续聊")
            : new CommunicationTurnProposal(null, true, false, "已收到第二条独立回复"));
    }
}

file sealed class FingerprintTransport(
    IEnumerable<ObservedChatReply> incoming,
    ChatFastPathStatus continuationStatus = ChatFastPathStatus.Completed)
    : IChatConversationTransport
{
    private readonly Queue<ObservedChatReply> _incoming = new(incoming);
    public int ContactSendCount { get; private set; }
    public int CurrentConversationSendCount { get; private set; }

    public Task<ChatFastPathResult> SendToContactAsync(
        string platform, string recipient, string message, CancellationToken cancellationToken)
    {
        ContactSendCount++;
        var status = ContactSendCount == 1 ? ChatFastPathStatus.Completed : continuationStatus;
        return Task.FromResult(new ChatFastPathResult(status, status.ToString(), null, null, null, 1,
            status == ChatFastPathStatus.Completed ? "completed" : "ambiguous"));
    }

    public Task<ChatFastPathResult> SendToCurrentConversationAsync(
        string platform, string message, CancellationToken cancellationToken)
    {
        CurrentConversationSendCount++;
        return Task.FromResult(new ChatFastPathResult(ChatFastPathStatus.Completed,
            "发送已完成。", null, null, null, 1, "completed"));
    }

    public Task<ObservedChatReply?> WaitForReplyAsync(
        CommunicationPlan plan, IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<ObservedChatReply?>(_incoming.TryDequeue(out var reply) ? reply : null);
}

file sealed class ImmediateChatSender : IChatFastPathSender
{
    public int CurrentSendCount { get; private set; }
    public List<string> NamedRecipients { get; } = [];

    public Task<ChatFastPathResult> SendAsync(
        string platform,
        string contact,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default)
    {
        NamedRecipients.Add(contact);
        return Task.FromResult(Completed());
    }

    public Task<ChatFastPathResult> ContinueVerifiedTargetAsync(
        string platform,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default)
    {
        CurrentSendCount++;
        return Task.FromResult(Completed());
    }

    private static ChatFastPathResult Completed() => new(
        ChatFastPathStatus.Completed,
        "发送已完成。",
        null,
        null,
        null,
        1,
        "completed");
}

file sealed class SwitchingChatDesktop :
    IChatFastPathSender, IChatReplyObserver, IChatTargetNavigator
{
    private readonly Dictionary<string, Queue<ObservedChatReply>> _replies =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _activeRecipient;
    private string? _blockedRecipient;

    public List<(string Recipient, string Message)> NamedSends { get; } = [];
    public int CurrentSendCount { get; private set; }
    public int NavigationCount { get; private set; }

    public void QueueReply(string recipient, params string[] messages) =>
        _replies[recipient] = new Queue<ObservedChatReply>(messages.Select(text =>
            new ObservedChatReply(text, DateTimeOffset.Now, Guid.NewGuid().ToString("N"))));

    public void BlockAndSwitchAway(string recipient)
    {
        _blockedRecipient = recipient;
        _activeRecipient = "其他会话";
    }

    public Task<bool> EnsureVisibleAsync(
        string platform, string recipient, CancellationToken cancellationToken)
    {
        NavigationCount++;
        if (recipient == _blockedRecipient) return Task.FromResult(false);
        _activeRecipient = recipient;
        return Task.FromResult(true);
    }

    public bool? IsTargetCurrentlyVisible(string platform, string recipient) =>
        string.Equals(_activeRecipient, recipient, StringComparison.OrdinalIgnoreCase);

    public Task<ChatFastPathResult> SendAsync(
        string platform, string contact, string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidDataException("test sent an empty chat message");
        _activeRecipient = contact;
        NamedSends.Add((contact, message));
        return Task.FromResult(new ChatFastPathResult(
            ChatFastPathStatus.Completed, "sent", null, null, null, 1, "completed"));
    }

    public Task<ChatFastPathResult> ContinueVerifiedTargetAsync(
        string platform, string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default)
    {
        CurrentSendCount++;
        throw new InvalidDataException("named contact continued through a targetless send");
    }

    public Task PrimeAsync(string platform, string recipient, CancellationToken cancellationToken)
    {
        if (IsTargetCurrentlyVisible(platform, recipient) != true)
            throw new ChatTargetMismatchException();
        return Task.CompletedTask;
    }

    public Task<ObservedChatReply?> WaitForReplyAsync(
        string platform, string recipient,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsTargetCurrentlyVisible(platform, recipient) != true)
            throw new InvalidDataException("read another contact's visible chat");
        return Task.FromResult<ObservedChatReply?>(
            _replies.TryGetValue(recipient, out var replies) && replies.TryDequeue(out var reply)
                ? reply : null);
    }

    public Task StopAsync(string platform, string recipient) => Task.CompletedTask;
}

file sealed class BoundTargetObserver(string? target) : IChatReplyObserver
{
    public Task PrimeAsync(string platform, string recipient, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<ObservedChatReply?> WaitForReplyAsync(
        string platform, string recipient, IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.FromResult<ObservedChatReply?>(null);

    public Task StopAsync(string platform, string recipient) => Task.CompletedTask;

    public string? GetVerifiedRecipient(string platform, string recipient) => target;
}

file sealed class MismatchedPrimeObserver : IChatReplyObserver
{
    public int StopCount { get; private set; }

    public Task PrimeAsync(string platform, string recipient, CancellationToken cancellationToken) =>
        Task.FromException(new ChatTargetMismatchException());

    public Task<ObservedChatReply?> WaitForReplyAsync(
        string platform, string recipient, IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new InvalidDataException("a mismatched watcher must never be polled");

    public Task StopAsync(string platform, string recipient)
    {
        StopCount++;
        return Task.CompletedTask;
    }
}

file sealed class DelayedPrimeObserver : IChatReplyObserver
{
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource PrimeStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool PrimeCompleted { get; private set; }
    public bool PrimeCanceled { get; private set; }
    public int PrimeCount { get; private set; }
    public int StopCount { get; private set; }

    public async Task PrimeAsync(
        string platform,
        string recipient,
        CancellationToken cancellationToken)
    {
        PrimeCount++;
        PrimeStarted.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
            PrimeCompleted = true;
        }
        catch (OperationCanceledException)
        {
            PrimeCanceled = true;
            throw;
        }
    }

    public Task<ObservedChatReply?> WaitForReplyAsync(
        string platform,
        string recipient,
        IReadOnlyList<CommunicationTranscriptEntry> transcript,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.FromResult<ObservedChatReply?>(new ObservedChatReply(
            "真实的新回复",
            DateTimeOffset.Now,
            "delayed-prime-test"));

    public Task StopAsync(string platform, string recipient)
    {
        StopCount++;
        return Task.CompletedTask;
    }

    public void ReleasePrime() => _release.TrySetResult();
}
