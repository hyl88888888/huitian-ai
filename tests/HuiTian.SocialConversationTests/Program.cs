using HuiTian.Core;
using HuiTian.Infrastructure;
using System.Text.Json;

if (args.Contains("--inspect-soul-uia", StringComparer.OrdinalIgnoreCase))
{
    // Read-only local diagnostics. Never print page text, account names,
    // profile details, message content, exact URLs, or automation IDs.
    var snapshot = await new WindowsSoulAccessibilitySnapshotSource()
        .CaptureAsync(CancellationToken.None);
    if (snapshot is null)
    {
        Console.WriteLine("Soul UIA: window_or_tree_unavailable");
        return;
    }
    var address = snapshot.PageAddress;
    var parsedAddress = Uri.TryCreate(address?.Contains("://", StringComparison.Ordinal) == true
            ? address : "https://" + address,
        UriKind.Absolute, out var pageUri);
    var trustedHost = parsedAddress && pageUri is not null &&
        (pageUri.Host.Equals("soulapp.cn", StringComparison.OrdinalIgnoreCase) ||
         pageUri.Host.EndsWith(".soulapp.cn", StringComparison.OrdinalIgnoreCase));
    var nodes = snapshot.Nodes;
    var semanticRegions = nodes.Count(node => node.AutomationId is
        "conversation-view" or "conversation-panel" or "chat-view" or "chat-panel");
    var semanticTitles = nodes.Count(node => node.AutomationId is
        "conversation-title" or "chat-title" or "conversation-header" or "chat-header");
    var semanticLists = nodes.Count(node => node.AutomationId is
        "message-list" or "chat-history" or "conversation-messages");
    var incoming = nodes.Count(node => node.AutomationId.StartsWith(
        "message-incoming-", StringComparison.OrdinalIgnoreCase));
    var outgoing = nodes.Count(node => node.AutomationId.StartsWith(
        "message-outgoing-", StringComparison.OrdinalIgnoreCase));
    var textNodes = nodes.Count(node => node.Role == "Text" &&
        !string.IsNullOrWhiteSpace(node.Name));
    var roleCounts = nodes.GroupBy(node => node.Role, StringComparer.Ordinal)
        .Where(group => group.Key is "Document" or "Pane" or "Group" or
            "List" or "ListItem" or "Text" or "Edit" or "Button")
        .ToDictionary(group => group.Key, group => group.Count());
    var chatKeywordNodes = nodes.Count(node =>
        node.Name.Contains("消息", StringComparison.OrdinalIgnoreCase) ||
        node.Name.Contains("聊天", StringComparison.OrdinalIgnoreCase) ||
        node.Name.Contains("发送", StringComparison.OrdinalIgnoreCase) ||
        node.Name.Contains("输入", StringComparison.OrdinalIgnoreCase));
    var path = pageUri?.AbsolutePath ?? string.Empty;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        windowAvailable = true,
        windowTitleMentionsSoul = snapshot.WindowTitle.Contains("Soul", StringComparison.OrdinalIgnoreCase),
        stableWindowHandle = snapshot.WindowHandle != 0,
        addressBarReadable = !string.IsNullOrWhiteSpace(address),
        soulHostVerified = trustedHost,
        snapshot.Truncated,
        nodeCount = nodes.Count,
        textNodeCount = textNodes,
        roleCounts,
        chatKeywordNodeCount = chatKeywordNodes,
        pagePathMentionsMessages = path.Contains("message", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("im", StringComparison.OrdinalIgnoreCase),
        semanticConversationRegions = semanticRegions,
        semanticConversationTitles = semanticTitles,
        semanticMessageLists = semanticLists,
        explicitlyIncomingMessages = incoming,
        explicitlyOutgoingMessages = outgoing,
    }));
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var discoverSlots = new CommunicationTargetSlots(
    "discover", ["抖音"], [], ["家人"], true, false,
    "本地、成年人、免费可聊", null)
{
    DesiredOutcome = SocialDiscoveryDesiredOutcome.CandidateShortlist,
};
var socialGuidance = SocialDiscoveryExecutionContract.BuildAgentGuidance(
    discoverSlots, hasUserStyleSamples: false);
Check(socialGuidance.Contains("打开公开首页只是入口", StringComparison.Ordinal) &&
      socialGuidance.Contains("扫码", StringComparison.Ordinal) &&
      socialGuidance.Contains("不代表已建立恋爱关系", StringComparison.Ordinal) == false &&
      socialGuidance.Contains("不要自动批量关注", StringComparison.Ordinal) &&
      socialGuidance.Contains("没有用户本人聊天样本", StringComparison.Ordinal),
    "social discovery must have staged login, screening and truthful-style guidance");
var claimedDiscovery = new AgentTaskOutcome(true, "任务完成", ["已打开公开页面"]);
var noCandidate = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, discoverSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(0, 0, 0, TimeSpan.FromMinutes(2)));
Check(!noCandidate.Succeeded && noCandidate.UnresolvedIssue?.Contains("没有核验", StringComparison.Ordinal) == true,
    "opening a social public page must not complete a people-discovery request");
var candidatesOnly = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, discoverSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 0, 0, TimeSpan.FromMinutes(2)));
Check(candidatesOnly.Succeeded && candidatesOnly.Summary.Contains("不代表已建立恋爱关系", StringComparison.Ordinal),
    "a genuine profile shortlist may complete discovery without claiming a relationship");
var chatSlots = discoverSlots with { Action = "chat" };
var noSentMessage = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, chatSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 0, 0, TimeSpan.FromMinutes(2)));
Check(!noSentMessage.Succeeded && noSentMessage.UnresolvedIssue?.Contains("没有核验向正确账号发出消息", StringComparison.Ordinal) == true,
    "a new-person chat request needs verified outgoing evidence, not just a profile shortlist");
var verifiedSentMessage = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, chatSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 1, 0, TimeSpan.FromMinutes(2)));
Check(!verifiedSentMessage.Succeeded && verifiedSentMessage.UnresolvedIssue?.Contains("真实回复", StringComparison.Ordinal) == true,
    "a sent message alone must not complete a reciprocal conversation");
var replied = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, chatSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 1, 1, TimeSpan.FromMinutes(2)));
Check(replied.Succeeded && replied.Summary.Contains("关系进展仍需双方真实互动", StringComparison.Ordinal),
    "a reciprocal chat may complete conversation only without claiming a relationship");
var contactStage = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery,
    discoverSlots with { DesiredOutcome = SocialDiscoveryDesiredOutcome.ContactInitiated },
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 1, 0, TimeSpan.FromMinutes(2)));
Check(contactStage.Succeeded && contactStage.Summary.Contains("发出消息", StringComparison.Ordinal),
    "the contact tier requires a verified outgoing message but not an invented reply");
var tooShort = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, chatSlots with { RequestedDuration = TimeSpan.FromMinutes(20) },
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 1, 1, TimeSpan.FromMinutes(2)));
Check(!tooShort.Succeeded && tooShort.UnresolvedIssue?.Contains("20 分钟", StringComparison.Ordinal) == true,
    "a specified conversation duration must not be silently shortened");
var relationshipSlots = discoverSlots with
{
    DesiredOutcome = SocialDiscoveryDesiredOutcome.RelationshipEstablished,
};
var relationshipClaim = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, relationshipSlots,
    new SocialDiscoveryExecutionContract.CompletionEvidence(3, 2, 2, TimeSpan.FromHours(2)));
Check(!relationshipClaim.Succeeded && relationshipClaim.UnresolvedIssue?.Contains("双方是否建立关系尚无可核验证据", StringComparison.Ordinal) == true,
    "candidate profiles and sent messages must never prove a reciprocal relationship");
var unspecified = SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
    claimedDiscovery, discoverSlots with { DesiredOutcome = SocialDiscoveryDesiredOutcome.Unspecified },
    new SocialDiscoveryExecutionContract.CompletionEvidence(2, 1, 0, TimeSpan.FromMinutes(2)));
Check(!unspecified.Succeeded,
    "a missing outcome tier must fail closed rather than silently becoming a shortlist");
Check(SocialDiscoveryExecutionContract.BuildAgentGuidance(
        discoverSlots with { FindNewPeople = false }, false) == string.Empty,
    "ordinary named-contact chats must not inherit new-person discovery rules");

var progressClock = new MutableTimeProvider(
    new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
var progress = new SocialDiscoveryProgressSession(
    new SocialDiscoveryProgressOptions(
        SocialDiscoveryDesiredOutcome.ReciprocalConversation,
        RequiredContacts: 2,
        MaximumDuration: TimeSpan.FromMinutes(30),
        NoProgressTimeout: TimeSpan.FromMinutes(10),
        RequestedConversationDuration: TimeSpan.FromMinutes(5)),
    progressClock);
var progressSoul = SocialContactKey.Create("soul", "progress-person-1");
var progressOther = SocialContactKey.Create("soul", "progress-person-2");
Check(progress.ObserveCandidate(progressSoul) &&
      !progress.ObserveCandidate(SocialContactKey.Create("soulapp", "progress-person-1")) &&
      progress.Snapshot() is { ObservedCandidates: 1, VerifiedCandidates: 0 },
    "observing the same account twice must not manufacture progress");
var progressEvidence = new SocialCandidateEvidence(
    progressSoul, "候选甲", true, SocialAdultStatus.VerifiedAdult,
    SocialRelationshipStatus.NewPerson, SocialMessagingCost.Free, true);
Check(!progress.VerifyCandidate(progressEvidence with
      { AdultStatus = SocialAdultStatus.Unknown }).EligibleForDraft &&
      progress.Snapshot().VerifiedCandidates == 0,
    "an unknown-age candidate must not enter the verified stage");
Check(progress.VerifyCandidate(progressEvidence).EligibleForDraft &&
      progress.Snapshot().VerifiedCandidates == 1,
    "complete profile evidence must advance exactly one candidate");
var firstSend = new SocialVerifiedOutgoingEvidence(
    progressSoul, "conversation-1", "outgoing-1", progressClock.GetUtcNow(),
    RecipientVerified: true, DeliveryVerified: false);
Check(!progress.RecordVerifiedOutgoing(firstSend) &&
      progress.Snapshot().ContactedCandidates == 0,
    "a key press without verified delivery must not count as contact");
Check(progress.RecordVerifiedOutgoing(firstSend with { DeliveryVerified = true }) &&
      !progress.RecordVerifiedOutgoing(firstSend with { DeliveryVerified = true }) &&
      progress.Snapshot().ContactedCandidates == 1,
    "one verified outgoing event must count once for its bound account");
var firstReply = new SocialVerifiedReplyEvidence(
    progressSoul, "wrong-conversation", "incoming-1", progressClock.GetUtcNow(),
    SenderVerified: true, NewIncomingVerified: true);
Check(!progress.RecordVerifiedReply(firstReply) &&
      !progress.RecordVerifiedReply(firstReply with
      { ConversationId = "conversation-1", NewIncomingVerified = false }) &&
      progress.Snapshot().RepliedCandidates == 0,
    "a different conversation or old/own bubble must not count as a reply");
Check(progress.RecordVerifiedReply(firstReply with
      { ConversationId = "conversation-1" }) &&
      !progress.RecordVerifiedReply(firstReply with
      { ConversationId = "conversation-1" }) &&
      progress.Snapshot() is
      { RepliedCandidates: 1, Status: SocialDiscoveryRunStatus.InProgress },
    "one reciprocal exchange does not finish a requested two-person conversation");
Check(!SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
        claimedDiscovery, chatSlots, progress.Snapshot()).Succeeded,
    "a model's success claim cannot override an incomplete evidence state");
Check(progress.ObserveCandidate(progressOther) &&
      progress.VerifyCandidate(progressEvidence with
      { Contact = progressOther, DisplayName = "候选乙" }).EligibleForDraft,
    "a second independently verified account must be tracked separately");
var secondSend = firstSend with
{
    Contact = progressOther,
    ConversationId = "conversation-2",
    EventFingerprint = "outgoing-2",
    DeliveryVerified = true,
};
Check(!progress.RecordVerifiedOutgoing(secondSend with
      { ConversationId = "conversation-1" }) &&
      progress.RecordVerifiedOutgoing(secondSend) &&
      !progress.RecordVerifiedReply(firstReply with
      { Contact = progressOther, ConversationId = "conversation-1" }) &&
      progress.RecordVerifiedReply(firstReply with
      { Contact = progressOther, ConversationId = "conversation-2",
        EventFingerprint = "incoming-2" }) &&
      progress.Snapshot() is
      { RepliedCandidates: 2, Status: SocialDiscoveryRunStatus.InProgress },
    "each reply must match its own verified recipient and conversation");
progressClock.Advance(TimeSpan.FromMinutes(5));
Check(progress.Snapshot() is
      { Status: SocialDiscoveryRunStatus.Completed, CanClaimCompletion: true },
    "requested conversation time starts from verified outreach and cannot finish early");
Check(SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
        claimedDiscovery, chatSlots, progress.Snapshot()).Succeeded,
    "a completed evidence state may satisfy the matching social goal contract");

var idleClock = new MutableTimeProvider(
    new DateTimeOffset(2026, 9, 24, 11, 0, 0, TimeSpan.Zero));
var idleProgress = new SocialDiscoveryProgressSession(
    new SocialDiscoveryProgressOptions(
        SocialDiscoveryDesiredOutcome.ContactInitiated, 1,
        TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(3)), idleClock);
idleProgress.ObserveCandidate(SocialContactKey.Create("Soul", "idle-account"));
idleClock.Advance(TimeSpan.FromMinutes(3));
Check(idleProgress.Snapshot() is
      { Status: SocialDiscoveryRunStatus.Stalled, CanClaimCompletion: false },
    "an unchanged page must leave the in-progress state after the idle budget");
var loginProgress = new SocialDiscoveryProgressSession(
    new SocialDiscoveryProgressOptions(
        SocialDiscoveryDesiredOutcome.CandidateShortlist, 1,
        TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(3)), idleClock);
loginProgress.PauseForUser("需要本人扫码登录");
Check(loginProgress.Snapshot() is
      { Status: SocialDiscoveryRunStatus.NeedsUserAttention, CanClaimCompletion: false },
    "a login wall must be reported as user attention, never task completion");
var budgetProgress = new SocialDiscoveryProgressSession(
    new SocialDiscoveryProgressOptions(
        SocialDiscoveryDesiredOutcome.RelationshipEstablished, 1,
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)), idleClock);
idleClock.Advance(TimeSpan.FromMinutes(5));
Check(budgetProgress.Snapshot() is
      { Status: SocialDiscoveryRunStatus.TimedOut, CanClaimCompletion: false },
    "a relationship goal cannot be inferred from replies and must end at its budget");

var soulBaseline = SoulTree("清和金",
    ("message-outgoing-old1", "哈喽"),
    ("message-outgoing-old2", "早啊"));
var soulOwnOnly = SoulTree("清和金",
    ("message-outgoing-old1", "哈喽"),
    ("message-outgoing-old2", "早啊"),
    ("message-outgoing-new1", "今天过得怎么样"));
var soulFirstReply = SoulTree("清和金",
    ("message-outgoing-old1", "哈喽"),
    ("message-outgoing-old2", "早啊"),
    ("message-outgoing-new1", "今天过得怎么样"),
    ("message-incoming-new1", "早啊"));
var soulRepeatedText = SoulTree("清和金",
    ("message-outgoing-old1", "哈喽"),
    ("message-outgoing-old2", "早啊"),
    ("message-outgoing-new1", "今天过得怎么样"),
    ("message-incoming-new1", "早啊"),
    ("message-incoming-new2", "早啊"));
var soulObserver = new SoulConversationObserver(new SequenceSoulSnapshotSource(
    soulBaseline, soulOwnOnly, soulFirstReply, soulFirstReply,
    soulRepeatedText, SoulTree("别人", ("message-incoming-new3", "你好"))));
Check((await soulObserver.ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.BaselineCaptured, NewReplies.Count: 0 },
    "existing Soul bubbles must become a baseline, never new replies");
Check((await soulObserver.ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.NoNewReply, NewReplies.Count: 0 },
    "a new own-message bubble must not be mistaken for the contact's reply");
var observedSoulReply = await soulObserver.ObserveAsync("清和金");
Check(observedSoulReply is
      { Status: SoulObservationStatus.NewReply, NewReplies.Count: 1 } &&
      observedSoulReply.NewReplies[0] is
      { IsIncoming: true, Text: "早啊", EventId: "message-incoming-new1" },
    "only a new incoming event in the bound conversation may be returned");
Check((await soulObserver.ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.NoNewReply },
    "the same incoming event must not be returned twice");
Check((await soulObserver.ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.NewReply, NewReplies.Count: 1 },
    "two distinct incoming events may have identical text");
Check((await soulObserver.ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.ConversationChanged, NewReplies.Count: 0 },
    "a switched conversation must stop the read without leaking its message");
var soulImpostor = soulBaseline with
{ PageAddress = "https://soulapp.cn.example.org/messages" };
Check((await new SoulConversationObserver(
        new SequenceSoulSnapshotSource(soulImpostor)).ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.PlatformUnverified },
    "a lookalike domain must not prove a Soul session");
var soulUnknownDirection = SoulTree("清和金",
    ("message-unknown-abcd", "你好"));
Check((await new SoulConversationObserver(
        new SequenceSoulSnapshotSource(soulUnknownDirection)).ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.MessageSemanticsUnavailable },
    "unlabeled message direction must return uncertainty");
var soulVisualOnly = SoulTree("清和金") with
{
    Nodes = [.. SoulTree("清和金").Nodes,
        new SoulAccessibilityNode(4, "Text", "你好", "")],
};
Check((await new SoulConversationObserver(
        new SequenceSoulSnapshotSource(soulVisualOnly)).ObserveAsync("清和金")) is
      { Status: SoulObservationStatus.MessageSemanticsUnavailable },
    "plain visual text without message IDs must not be treated as a reply");
var fullSoulCapability = SoulPageCapabilityAnalyzer.Analyze(soulBaseline);
Check(fullSoulCapability is
      { AuthenticHost: true, LoginWallDetected: false, ChatPageVisible: true,
        StableConversationSemantics: true, StableMessageSemantics: true,
        CanEstablishReadOnlyReplyBaseline: true },
    "semantic UIA evidence should allow a read-only reply baseline only");
var capabilityText = SoulPageCapabilityAnalyzer.Format(fullSoulCapability);
Check(!capabilityText.Contains("清和金", StringComparison.Ordinal) &&
      !capabilityText.Contains("哈喽", StringComparison.Ordinal) &&
      capabilityText.Contains("仍不证明可发送或支持多人聊天", StringComparison.Ordinal),
    "capability diagnostics must exclude personal text and avoid a multi-chat claim");
Check(SoulPageCapabilityAnalyzer.Analyze(soulImpostor) is
      { AuthenticHost: false, CanEstablishReadOnlyReplyBaseline: false } &&
      SoulPageCapabilityAnalyzer.Analyze(soulVisualOnly) is
      { StableMessageSemantics: false, CanEstablishReadOnlyReplyBaseline: false },
    "lookalike domains and visual-only bubbles must fail the capability check");
var soulLoginWall = soulBaseline with
{
    Nodes = [.. soulBaseline.Nodes,
        new SoulAccessibilityNode(1, "Dialog", "请先登录", "")],
};
Check(SoulPageCapabilityAnalyzer.Analyze(soulLoginWall) is
      { LoginWallDetected: true, CanEstablishReadOnlyReplyBaseline: false },
    "a login wall must block the read-only reply capability claim");
var nonSoulSource = new SequenceSoulSnapshotSource(soulBaseline);
var plainToolResult = new DynamicToolResult(true, "原操作结果");
Check(ReferenceEquals(await SoulPageCapabilityAnalyzer.AppendIfNamedAsync(
          "抖音", plainToolResult, nonSoulSource, CancellationToken.None),
          plainToolResult) && nonSoulSource.Calls == 0,
    "an unrelated window must not trigger a Soul UIA read");
var soulAppendSource = new SequenceSoulSnapshotSource(soulBaseline);
var appendedCapability = await SoulPageCapabilityAnalyzer.AppendIfNamedAsync(
    "Soul - Chrome", plainToolResult, soulAppendSource, CancellationToken.None);
Check(appendedCapability.Success && soulAppendSource.Calls == 1 &&
      appendedCapability.Text.Contains("[Soul只读能力]", StringComparison.Ordinal) &&
      !appendedCapability.Text.Contains("清和金", StringComparison.Ordinal) &&
      !appendedCapability.Text.Contains("哈喽", StringComparison.Ordinal),
    "an explicitly named Soul window gets only a compact anonymous diagnostic");
var soulDiagnosticWatch = System.Diagnostics.Stopwatch.StartNew();
var timedOutCapability = await SoulPageCapabilityAnalyzer.AppendIfNamedAsync(
    "Soul", plainToolResult, new HangingSoulSnapshotSource(), CancellationToken.None);
Check(soulDiagnosticWatch.Elapsed < TimeSpan.FromSeconds(2) &&
      timedOutCapability.Text.Contains("超过短时限", StringComparison.Ordinal),
    "a blocked UIA read must not stall the tool result beyond two seconds");
var soulSwitchedWindow = new SoulConversationObserver(
    new SequenceSoulSnapshotSource(soulBaseline,
        soulFirstReply with { WindowHandle = 4321 }));
Check((await soulSwitchedWindow.ObserveAsync("清和金")).Status ==
      SoulObservationStatus.BaselineCaptured &&
      (await soulSwitchedWindow.ObserveAsync("清和金")).Status ==
      SoulObservationStatus.ConversationChanged,
    "a different browser window with the same title must not inherit the old baseline");

static SoulAccessibilitySnapshot SoulTree(
    string title,
    params (string Id, string Text)[] messages)
{
    var nodes = new List<SoulAccessibilityNode>
    {
        new(-1, "Window", "Soul - Google Chrome", ""),
        new(0, "Document", "Soul", ""),
        new(1, "Group", "", "conversation-view"),
        new(2, "Text", title, "conversation-title"),
        new(2, "List", "", "message-list"),
    };
    nodes.AddRange(messages.Select(message =>
        new SoulAccessibilityNode(4, "ListItem", message.Text, message.Id)));
    return new SoulAccessibilitySnapshot(
        "Soul - Google Chrome", "https://home.soulapp.cn/messages", nodes,
        WindowHandle: 1234);
}

var wxContact = SocialContactKey.Create("WeChat", "wxid-person-001");
var wxOther = SocialContactKey.Create("微信", "wxid-person-002");
var soulContact = SocialContactKey.Create("soul", "wxid-person-001");
Check(SocialContactKey.Create("douyin", "same-id") ==
      SocialContactKey.Create("抖音", "same-id") &&
      SocialContactKey.Create("soulapp", "same-id") ==
      SocialContactKey.Create("Soul", "same-id"),
    "platform aliases must share one stable ID namespace without language keyword routing");
var eligible = new SocialCandidateEvidence(
    soulContact, "小林", true, SocialAdultStatus.VerifiedAdult,
    SocialRelationshipStatus.NewPerson, SocialMessagingCost.Free, true);
Check(SocialCandidatePolicy.EvaluateForNewOutreach(eligible, false).EligibleForDraft,
    "verified adult, new, free-message candidate may be considered for a draft");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(eligible, true).EligibleForDraft,
    "someone previously contacted must not enter new-acquaintance outreach again");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { StableIdentityVerified = false }, false).EligibleForDraft,
    "a nickname without verified stable ID cannot authorize outreach");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { AdultStatus = SocialAdultStatus.Unknown }, false).EligibleForDraft,
    "unknown age must be skipped for dating outreach");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { AdultStatus = SocialAdultStatus.Minor }, false).EligibleForDraft,
    "underage profile must be skipped");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { RelationshipStatus = SocialRelationshipStatus.FamilyOrProtected }, false)
    .EligibleForDraft, "family/protected relationship must be skipped");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { RelationshipStatus = SocialRelationshipStatus.ExistingContact }, false)
    .EligibleForDraft, "existing contacts must be skipped for new dating outreach");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { RelationshipStatus = SocialRelationshipStatus.Unknown }, false)
    .EligibleForDraft, "unknown relationship must be skipped");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { MessagingCost = SocialMessagingCost.Paid }, false).EligibleForDraft,
    "paid chat must be skipped");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { MessagingCost = SocialMessagingCost.Unknown }, false).EligibleForDraft,
    "unknown chat price must be skipped");
Check(!SocialCandidatePolicy.EvaluateForNewOutreach(
        eligible with { MessagingAvailable = false }, false).EligibleForDraft,
    "unavailable messaging must be skipped");

var toolTestParent = Path.GetFullPath(@"D:\HuiTian\Cache\Temp\SocialCandidateToolTests");
var toolTestRoot = Path.Combine(toolTestParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(toolTestRoot);
try
{
var toolStore = new SocialConversationMemoryStore(new HuiTianPaths(toolTestRoot));
var screeningArguments = JsonSerializer.SerializeToElement(new
{
    platform = "soul",
    stableId = "synthetic-" + Guid.NewGuid().ToString("N"),
    displayName = "合成候选人",
    identityVerified = true,
    ageVerified = true,
    freeToMessage = true,
    newPerson = true,
    knownFamilyOrRelationship = false,
    messagingAvailable = true,
});
var universalCall = CodexAppServerClient.ResolveUniversalToolCall(
    JsonSerializer.SerializeToElement(new
    {
        c = "screen_social_candidate",
        a = screeningArguments,
        d = new[] { "state" },
    }));
Check(universalCall.ToolName == "huitian_screen_social_candidate",
    "the universal tool contract must expose the social screening capability");
Check(ConversationPolicy.IsToolAllowed(universalCall.ToolName, AgentTaskRoute.Composite) &&
      ConversationPolicy.IsToolAllowed(universalCall.ToolName, AgentTaskRoute.Research),
    "social screening must remain available during multi-step and read-only research tasks");
var tool = new WindowsToolBridge(toolStore);
var screened = await tool.ExecuteAsync(universalCall.ToolName,
    universalCall.Arguments, CancellationToken.None);
Check(screened.Success && screened.ResultCode == "social_candidate_eligible_for_draft" &&
      screened.Text.Contains("eligible_for_draft", StringComparison.Ordinal) &&
      !screened.Text.Contains(screeningArguments.GetProperty("stableId").GetString()!,
          StringComparison.Ordinal),
    "screening tool must return a structured draft-only decision without exposing the stable ID");
var screenedKey = SocialContactKey.Create("soul",
    screeningArguments.GetProperty("stableId").GetString());
Check(await toolStore.ReadContactAsync(screenedKey) is null &&
      (await toolStore.ReadCandidateAsync(screenedKey)) is
      { DisplayName: "合成候选人", HasVerifiedTurn: false, HasVerifiedOutgoing: false },
    "a screened candidate must be remembered without creating a contacted conversation");
var readArguments = JsonSerializer.SerializeToElement(new
{
    platform = "soul",
    stableId = screeningArguments.GetProperty("stableId").GetString(),
});
var readCall = CodexAppServerClient.ResolveUniversalToolCall(
    JsonSerializer.SerializeToElement(new { c = "read_social_candidates", a = readArguments,
        d = new[] { "state" } }));
Check(ConversationPolicy.IsToolAllowed(readCall.ToolName, AgentTaskRoute.Composite) &&
      ConversationPolicy.IsToolAllowed(readCall.ToolName, AgentTaskRoute.Research),
    "candidate recall must be available during composite and read-only research tasks");
var recalled = await tool.ExecuteAsync(readCall.ToolName, readCall.Arguments,
    CancellationToken.None);
Check(recalled.Success && recalled.ResultCode == "social_candidate_found" &&
      recalled.Text.Contains("candidate_only", StringComparison.Ordinal) &&
      recalled.Text.Contains(screenedKey.StableContactId, StringComparison.Ordinal),
    "the agent must be able to retrieve an exact candidate by platform and stable ID");
var listed = await tool.ExecuteAsync("huitian_read_social_candidates",
    JsonSerializer.SerializeToElement(new { platform = "soul", limit = 20 }),
    CancellationToken.None);
Check(listed.Success && listed.ResultCode == "social_candidate_listed" &&
      listed.Text.Contains(screenedKey.StableContactId, StringComparison.Ordinal),
    "the agent must be able to list bounded remembered candidates");
await toolStore.RecordVerifiedContactAsync(screenedKey, "合成候选人");
await toolStore.AppendVerifiedTurnAsync(screenedKey,
    SocialConversationTurnRole.UserSent, "PRIVATE_TEST_MESSAGE_NOT_FOR_CANDIDATE_LIST");
var recalledAfterTurn = await tool.ExecuteAsync(readCall.ToolName, readCall.Arguments,
    CancellationToken.None);
Check(recalledAfterTurn.Text.Contains("verified_turns_recorded", StringComparison.Ordinal) &&
      recalledAfterTurn.Text.Contains("\"hasVerifiedOutgoing\":true", StringComparison.Ordinal) &&
      !recalledAfterTurn.Text.Contains("PRIVATE_TEST_MESSAGE_NOT_FOR_CANDIDATE_LIST",
          StringComparison.Ordinal),
    "candidate lookup may report verified-turn status but must not expose message content");
var repeatScreening = await tool.ExecuteAsync("huitian_screen_social_candidate",
    screeningArguments, CancellationToken.None);
Check(repeatScreening.ResultCode == "social_candidate_skip" &&
      repeatScreening.Text.Contains("AlreadyContacted", StringComparison.Ordinal),
    "a candidate with an independently verified outgoing turn must not be approached as new");
var paidScreening = await tool.ExecuteAsync("huitian_screen_social_candidate",
    JsonSerializer.SerializeToElement(new
    {
        platform = "soul",
        stableId = "synthetic-paid-" + Guid.NewGuid().ToString("N"),
        displayName = "付费候选人",
        identityVerified = true,
        ageVerified = true,
        freeToMessage = false,
        paidChat = true,
        newPerson = true,
        knownFamilyOrRelationship = false,
        messagingAvailable = true,
    }), CancellationToken.None);
Check(paidScreening.Success && paidScreening.ResultCode == "social_candidate_skip" &&
      paidScreening.Text.Contains("PaidOrUnknownCost", StringComparison.Ordinal),
    "the registered tool must skip a paid chat without trying to buy access");
Check((await toolStore.ListCandidatesAsync()).Count == 1,
    "rejected or paid candidates must not be persisted");
var incompleteScreening = await tool.ExecuteAsync("huitian_screen_social_candidate",
    JsonSerializer.SerializeToElement(new
    {
        platform = "soul", stableId = "synthetic", displayName = "合成候选人",
    }),
    CancellationToken.None);
Check(!incompleteScreening.Success &&
      incompleteScreening.ResultCode == "social_evidence_incomplete",
    "direct callers must not bypass the required evidence flags");
}
finally
{
    var resolved = Path.GetFullPath(toolTestRoot);
    if (!resolved.StartsWith(toolTestParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing test cleanup outside the tool test directory.");
    Directory.Delete(resolved, recursive: true);
}

var testParent = Path.GetFullPath(@"D:\HuiTian\Cache\Temp\SocialConversationTests");
var testRoot = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
    var paths = new HuiTianPaths(testRoot);
    var store = new SocialConversationMemoryStore(paths, clock);
    Check(store.FilePath.StartsWith(Path.Combine(testRoot, "UserContent"),
            StringComparison.OrdinalIgnoreCase),
        "memory must live under the configured user-data root, not C: or the source tree");

    var newSoulCandidate = SocialContactKey.Create("soul", "same-stable-id");
    var newDouyinCandidate = SocialContactKey.Create("抖音", "same-stable-id");
    await store.RememberEligibleCandidateAsync(newSoulCandidate, "同名新对象");
    await store.RememberEligibleCandidateAsync(newDouyinCandidate, "同名新对象");
    Check(await store.ReadContactAsync(newSoulCandidate) is null &&
          (await store.ReadCandidateAsync(newSoulCandidate)) is
          { HasVerifiedTurn: false, HasVerifiedOutgoing: false } &&
          (await store.ListCandidatesAsync()).Count == 2 &&
          (await store.ListCandidatesAsync("douyin")).Count == 1,
        "remembering a candidate must not manufacture a verified chat or merge platforms");
    await store.RecordVerifiedContactAsync(newSoulCandidate, "同名新对象");
    await store.AppendVerifiedTurnAsync(newSoulCandidate,
        SocialConversationTurnRole.ContactReceived, "经过正确会话核验的来信");
    Check((await store.ReadCandidateAsync(newSoulCandidate)) is
        { HasVerifiedTurn: true, HasVerifiedOutgoing: false },
        "an inbound verified turn must not be misreported as an outbound contact");
    await store.AppendVerifiedTurnAsync(newSoulCandidate,
        SocialConversationTurnRole.UserSent, "已经核验发送的回复");
    Check((await store.ReadCandidateAsync(newSoulCandidate)) is
        { HasVerifiedTurn: true, HasVerifiedOutgoing: true },
        "the candidate state must distinguish verified outbound turns");
    await store.DeleteCandidateAsync(newDouyinCandidate);
    Check(await store.ReadCandidateAsync(newDouyinCandidate) is null &&
          await store.ReadCandidateAsync(newSoulCandidate) is not null,
        "deleting one candidate must leave the other platform identity untouched");

    await store.SetExplicitUserStyleSamplesAsync(["我平时这样聊天，不瞎编身世。"]);
    await store.RecordVerifiedContactAsync(wxContact, "同名好友", "用户亲自提供：这是微信里的新联系人");
    await store.RecordVerifiedContactAsync(wxOther, "同名好友");
    await store.RecordVerifiedContactAsync(soulContact, "同名好友");
    await store.AppendVerifiedTurnAsync(wxContact, SocialConversationTurnRole.ContactReceived,
        "她说今天工作有点累。 ");
    await store.AppendVerifiedTurnAsync(wxContact, SocialConversationTurnRole.UserSent,
        "辛苦了，先休息会儿。 ");
    await store.AppendVerifiedTurnAsync(wxOther, SocialConversationTurnRole.ContactReceived,
        "另一人的消息");
    await store.AppendVerifiedTurnAsync(soulContact, SocialConversationTurnRole.ContactReceived,
        "另一个平台的消息");

    var reloaded = new SocialConversationMemoryStore(paths, clock);
    var first = await reloaded.ReadContactAsync(wxContact);
    var second = await reloaded.ReadContactAsync(wxOther);
    var third = await reloaded.ReadContactAsync(soulContact);
    Check(first is { RecentTurns.Count: 2 } && second is { RecentTurns.Count: 1 } &&
          third is { RecentTurns.Count: 1 },
        "equal display names and equal IDs on different platforms must remain isolated");
    Check(first!.RecentTurns[0].Text == "她说今天工作有点累。" &&
          first.UserProvidedNotes.Contains("用户亲自提供", StringComparison.Ordinal),
        "verified contact context must persist and reload");
    Check((await reloaded.ReadExplicitUserStyleSamplesAsync()).SequenceEqual(
            ["我平时这样聊天，不瞎编身世。"]),
        "only explicitly supplied user-style examples may be used");
    Check(!System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.FilePath))
            .Contains("她说今天工作有点累", StringComparison.Ordinal) &&
          !System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.FilePath))
            .Contains("同名新对象", StringComparison.Ordinal),
        "sensitive chat text and candidate names must not be stored in plaintext");

    await Task.WhenAll(
        Enumerable.Range(0, 8).Select(i =>
            store.AppendVerifiedTurnAsync(wxOther, SocialConversationTurnRole.ContactReceived,
                $"wechat-other-{i}"))
        .Concat(Enumerable.Range(0, 8).Select(i =>
            reloaded.AppendVerifiedTurnAsync(soulContact,
                SocialConversationTurnRole.ContactReceived, $"soul-{i}"))));
    Check((await store.ReadContactAsync(wxOther))?.RecentTurns.Count == 9 &&
          (await store.ReadContactAsync(soulContact))?.RecentTurns.Count == 9,
        "simultaneous contact updates from separate store instances must not overwrite each other");

    for (var i = 0; i < 30; i++)
        await store.AppendVerifiedTurnAsync(wxContact, SocialConversationTurnRole.ContactReceived,
            $"recent-{i}");
    first = await store.ReadContactAsync(wxContact);
    Check(first is { RecentTurns.Count: 24 } && first.RecentTurns[0].Text == "recent-6" &&
          first.RecentTurns[^1].Text == "recent-29",
        "only the bounded recent context may be retained");

    await store.SetExplicitUserStyleSamplesAsync(
        Enumerable.Range(0, 20).Select(i => $"explicit sample {i}"));
    Check((await store.ReadExplicitUserStyleSamplesAsync()).Count == 12,
        "explicit style samples must be bounded");
    await store.DeleteContactAsync(wxOther);
    Check(await store.ReadContactAsync(wxOther) is null &&
          await store.ReadContactAsync(wxContact) is not null,
        "deleting one contact must not erase another with the same display name");
    await store.DeleteContactAsync(newSoulCandidate);
    Check(await store.ReadCandidateAsync(newSoulCandidate) is null &&
          await store.ReadContactAsync(newSoulCandidate) is null,
        "deleting a person must erase both candidate identity and verified chat context");

    await store.RememberEligibleCandidateAsync(
        SocialContactKey.Create("soul", "expiring-candidate"), "过期候选人");
    clock.Advance(TimeSpan.FromDays(91));
    Check((await store.ListContactsAsync()).Count == 0 &&
          (await store.ListCandidatesAsync()).Count == 0,
        "old contact context and candidate identities must expire after the retention period");
    var clockBeforeExpiry = new MutableTimeProvider(
        new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));
    Check((await new SocialConversationMemoryStore(paths, clockBeforeExpiry)
            .ListContactsAsync()).Count == 0 &&
          (await new SocialConversationMemoryStore(paths, clockBeforeExpiry)
            .ListCandidatesAsync()).Count == 0,
        "expired contact and candidate data must also be purged from disk, not merely hidden in memory");
    await store.DeleteAllAsync();
    Check(!File.Exists(store.FilePath) &&
          (await store.ReadExplicitUserStyleSamplesAsync()).Count == 0,
        "the user must be able to clear all stored conversation context");
}
finally
{
    var resolved = Path.GetFullPath(testRoot);
    if (!resolved.StartsWith(testParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing test cleanup outside the test directory.");
    Directory.Delete(resolved, recursive: true);
}

Console.WriteLine("Social conversation policy and per-contact encrypted memory: PASS");

sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _now = initial;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

sealed class SequenceSoulSnapshotSource(params SoulAccessibilitySnapshot?[] snapshots)
    : ISoulAccessibilitySnapshotSource
{
    private int _index;
    public int Calls => _index;

    public Task<SoulAccessibilitySnapshot?> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Math.Min(_index++, snapshots.Length - 1);
        return Task.FromResult(snapshots[current]);
    }
}

sealed class HangingSoulSnapshotSource : ISoulAccessibilitySnapshotSource
{
    public async Task<SoulAccessibilitySnapshot?> CaptureAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }
}
