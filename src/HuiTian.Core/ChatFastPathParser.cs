using System.Text.RegularExpressions;

namespace HuiTian.Core;

public enum ChatFastPathPlatform
{
    WeChat,
    QQ,
}

public sealed record ChatFastPathIntent(
    ChatFastPathPlatform Platform,
    string? Recipient,
    string? Message,
    bool UseCurrentConversation,
    bool ReferencesFiles,
    bool ReferencesMultipleFiles,
    string PayloadText)
{
    public string PlatformName => Platform == ChatFastPathPlatform.WeChat ? "微信" : "QQ";

    /// <summary>
    /// True when the user supplied a communication goal instead of dictating
    /// the exact sentence.  The product composes one bounded first message and
    /// then uses the same verified delivery path as literal messages.
    /// </summary>
    public bool MessageComposedFromGoal { get; init; }

    public string? CommunicationGoal { get; init; }

    public string? Persona { get; init; }

    /// <summary>
    /// Structured, lossless communication objectives.  Unlike Message this
    /// also describes whether the user asked HuiTian to keep conversing after
    /// the opening delivery.
    /// </summary>
    public CommunicationPlan? CommunicationPlan { get; init; }

    public bool ContinuousConversationRequested =>
        CommunicationPlan?.RequiresReplyObservation == true;
}

/// <summary>
/// Recognizes high-confidence chat delivery requests.  Literal messages stay
/// byte-for-byte user supplied.  A second bounded form accepts an explicit
/// recipient plus a communication goal (for example, "以老板身份提醒他完成今天
/// 的工作") and composes one conservative first message without a model turn.
/// The fast path never invents a recipient and never starts an unbounded
/// autonomous conversation.
/// </summary>
public static partial class ChatFastPathParser
{
    private const double SemanticFallbackMinimumConfidence = 0.82;

    private const string PlatformPattern =
        @"(?<platform>微信|WeChat|Weixin|QQ)";

    private const string NaturalOperationPattern =
        @"(?:发送(?:一条)?(?:消息|信息|文字|内容)?|发(?:一条)?(?:消息|信息|文字|内容)?|回复(?:一下)?|回(?:一句|消息|一下)?|聊天(?:对话)?|聊一下|聊聊|聊|对话|沟通(?:一下)?|交流(?:一下)?|联系(?:一下)?|找|问询|询问(?:一下)?|问问|问一下|问下|问|请教|提醒(?:一下)?|催促|催一下|催|督促|通知|告知|告诉|知道|转告|捎话|带话|说一句|说一下|说)";

    private const string NaturalSlotBoundaryPattern =
        NaturalOperationPattern +
        @"|目的(?:是|就是)?|为了|希望|想要|需要|主要是|用.{1,24}(?:身份|口吻|语气)|以.{1,24}(?:身份|口吻|语气)|作为.{1,24}(?:身份|口吻|语气)|持续|连续|一直|继续|接着|自动|\d{1,4}(?:\.\d{1,2})?\s*(?:个\s*)?(?:小时|钟头|分钟|分|hours?|hrs?|h|minutes?|mins?|min)|[零〇一二三四五六七八九十百千两]{1,8}\s*(?:个\s*)?(?:小时|钟头|分钟|分)";

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:直接\s*)?给\s*" +
        PlatformPattern +
        @"\s*(?:(?:里|中|上)(?:的)?)?\s*(?:(?:联系人|好友)\s*)?" +
        @"(?<recipient>.+?)\s*(?:发送(?:一条)?(?:消息|信息|文字|内容)?|发(?:一条)?(?:消息|信息|文字|内容)?)\s*[：:，,\s]*(?<payload>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ForwardRecipientRegex();

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:直接\s*)?(?:在\s*)?" +
        PlatformPattern +
        @"\s*(?:里|中|上)?\s*(?:给|向)\s*(?<recipient>.+?)\s*" +
        @"(?:发送(?:一条)?(?:消息|信息|文字|内容)?|发(?:一条)?(?:消息|信息|文字|内容)?)\s*[：:，,\s]*(?<payload>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PlatformFirstRegex();

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:直接\s*)?(?:把|将)\s*(?<payload>.+?)\s*" +
        @"(?:发送给|发给)\s*" + PlatformPattern +
        @"\s*(?:(?:里|中|上)(?:的)?)?\s*(?:(?:联系人|好友)\s*)?(?<recipient>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ReverseRecipientRegex();

    [GeneratedRegex(
        @"(?:发送(?:一条)?(?:消息|信息|文字|内容)?|发(?:一条)?(?:消息|信息|文字|内容)?)\s*[：:，,\s]*(?<payload>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SendPayloadRegex();

    [GeneratedRegex(
        @"(?:当前|目前|现在|这个|眼前|已经|已|正在|现有|刚打开|已打开|打开着).{0,12}(?:打开|显示|选中|停留)?.{0,12}(?:会话|对话框|聊天框|聊天窗口|聊天|窗口|联系人|对方|这个人|人)|" +
        @"(?:会话|对话框|聊天框|聊天窗口|聊天窗口).{0,10}(?:当前|目前|现在|这个|已经|已|正在|打开)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentConversationRegex();

    [GeneratedRegex(
        @"(?:如何|怎么|怎样|为什么|教程|操作方法|实现方法|有没有办法|是否支持|能不能|可不可以).{0,28}(?:微信|WeChat|Weixin|QQ)?.{0,20}(?:发送|发给|发消息|怎么发|回复|联系|聊天)|" +
        @"(?:微信|WeChat|Weixin|QQ|发送|发消息|回复|联系|聊天).{0,24}(?:如何|怎么|怎样)(?:发|发送|操作|实现)?",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex InformationalQuestionRegex();

    [GeneratedRegex(
        @"(?:不要|别|无需|不用|不必|禁止|切勿|先别|暂时别).{0,18}(?:发送|发出|发给|发消息|回复|联系|聊天)|" +
        @"(?:写|拟|起草|生成).{0,48}(?:但|但是|只是|只|先)?\s*(?:不要|别|无需|不用|不必).{0,10}(?:发送|发出|发给|回复)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NegatedChatActionRegex();

    [GeneratedRegex(
        @"(?:微信|WeChat|Weixin|QQ).{0,10}(?:聊天|联系|发消息|回复).{0,10}(?:真|很|太|挺|有点|比较).{0,12}(?:麻烦|困难|难|烦|方便|好用|有趣|无聊)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ChatObservationRegex();

    [GeneratedRegex(
        @"(?:我)?(?:刚才|刚刚|之前|昨天|上次).{0,24}(?:聊天|聊过|联系|回复|发过).{0,24}(?:觉得|发现|说得|聊得|已经|当时)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PastChatNarrativeRegex();

    [GeneratedRegex(
        @"(?:总结|概括|提炼|改写|润色|翻译|分析|根据.+(?:写|制作|生成)|先.+再(?:发送|发给))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex RequiresContentWorkRegex();

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:直接\s*)?(?:去\s*)?(?:(?:代替|替)我\s*)?" +
        @"(?:(?:用|以|作为)\s*(?<leadingPersona>[^，,。；;！？!?]{1,24}?)(?:的)?(?:身份|口吻|语气)\s*)?" +
        @"(?:和|跟)\s*" +
        PlatformPattern +
        @"\s*(?:(?:里|中|上)(?:的)?)?\s*(?:(?:联系人|好友)\s*)?" +
        @"(?<recipient>.+?)\s*(?:(?:自动|持续|连续|一直)\s*)*" +
        @"(?:聊天(?:对话)?|对话|聊(?:一?下)?|沟通(?:一下)?|联系(?:一下)?)" +
        @"\s*(?<instructions>(?:(?:\d{1,4}(?:\.\d{1,2})?|[零〇一二三四五六七八九十百千两]{1,8}|半)\s*(?:个\s*)?(?:小时|钟头|分钟|分(?:钟)?|hours?|hrs?|h|minutes?|mins?|min)\s*)?[，,:：；;\s].+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex GoalConversationRegex();

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:直接\s*)?(?:(?:用|通过|在|去)\s*)?" +
        PlatformPattern +
        @"\s*(?:(?:里|中|上)(?:的)?)?\s*(?:联系|找|和|跟)\s*" +
        @"(?<recipient>.+?)\s*(?<instructions>[，,:：；;\s](?:告诉|跟|和|问|询问|提醒|催|督促|通知|说|聊).+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex GoalContactRegex();

    [GeneratedRegex(
        @"(?:目的是|目的就是|为了|想要|希望|需要|请|要)\s*(?<goal>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommunicationGoalRegex();

    [GeneratedRegex(
        @"(?:用|以|作为)\s*(?<persona>[^，,。；;！？!?]{1,24}?)(?:的)?(?:身份|口吻|语气)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonaRegex();

    [GeneratedRegex(
        @"(?:目的是|目的就是|为了|想要|希望|需要|告诉|跟他说|跟她说|问(?:一下)?|询问|提醒(?:一下)?|催(?:一下)?|督促|通知|说(?:一下)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GoalCueRegex();

    [GeneratedRegex(
        @"(?:文件|附件|产物|PPT|演示文稿|幻灯片|Word|Excel|表格|文档|PDF|图片|照片|音频|视频)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNounRegex();

    [GeneratedRegex(
        @"^(?:(?:把|将)\s*)?(?:(?:刚才|刚刚|之前|上面|上述|这个|这份|这些|那份|该|最新|已经|已|做好的|制作好的|生成好的|导出的|完成的)\s*)*" +
        @"(?:文件|附件|产物|PPT|演示文稿|幻灯片|Word(?:文档)?|Excel(?:表格)?|表格|文档|PDF|图片|照片|音频|视频)(?:文件)?(?:\s*(?:和|以及)\s*(?:文件|附件|图片|文档|PPT))*[。！!？?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileReferenceRegex();

    [GeneratedRegex(
        @"^(?:这个|这份|这些|它|它们|刚才那个|刚才的|上面的|上述的|附件)[。！!？?]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachedPronounRegex();

    [GeneratedRegex(
        @"(?:这些|所有|全部|多个|几个|它们|附件们|文件们|以及|和)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultipleFilesRegex();

    [GeneratedRegex(
        @"(?:联系人|好友|朋友|给|向|和|跟|找|联系|回复)\s*[“”‘’""'《》【】](?<recipient>[^“”‘’""'《》【】\r\n]{1,80})[“”‘’""'《》【】]",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedRecipientRegex();

    [GeneratedRegex(
        PlatformPattern +
        @"\s*(?:(?:里|中|上|内)(?:的)?\s*)?(?:(?:联系人|好友|朋友)(?:里|中|上)?(?:的)?\s*)?" +
        @"(?:(?:给|向|和|跟|找|联系(?:一下)?|回复(?:一下)?|问询|询问(?:一下)?|问问|问一下|问下|请教)\s*)?" +
        @"(?<recipient>[^，,。；;：:！!？?\r\n]{1,80}?)\s*(?=" +
        @"(?:[，,：:]\s*)?(?:帮我\s*)?(?:接着\s*)?(?:" +
        NaturalSlotBoundaryPattern + @"))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalRecipientAfterPlatformRegex();

    [GeneratedRegex(
        @"(?:给|向|和|跟|找|联系(?:一下)?|回复(?:一下)?|问询|询问(?:一下)?|问问|问一下|问下|请教)\s*" +
        @"(?:(?:联系人|好友|朋友)\s*)?(?<recipient>[^，,。；;：:！!？?\r\n]{1,80}?)\s*" +
        @"(?:用|通过|在|去)\s*" + PlatformPattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalRecipientBeforePlatformRegex();

    [GeneratedRegex(
        @"(?:给|向|和|跟|找|联系(?:一下)?|回复(?:一下)?|问询|询问(?:一下)?|问问|问一下|问下|请教)\s*" +
        @"(?:" + PlatformPattern + @"\s*(?:(?:里|中|上|内)(?:的)?\s*)?)?" +
        @"(?:(?:联系人|好友|朋友)\s*)?(?<recipient>[^，,。；;：:！!？?\r\n]{1,80}?)\s*(?=" +
        @"(?:[，,：:]\s*)?(?:帮我\s*)?(?:接着\s*)?(?:" +
        NaturalSlotBoundaryPattern + @"))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalRecipientAfterRelationRegex();

    [GeneratedRegex(
        @"(?<send>发送(?:一条)?(?:消息|信息|文字|内容)?|发(?:一条)?(?:消息|信息|文字|内容)?)|" +
        @"(?<reply>回复(?:一下)?|回(?:一句|消息|一下)?)|" +
        @"(?<conversation>聊天(?:对话)?|聊一下|聊聊|聊|对话|沟通(?:一下)?|交流(?:一下)?|联系(?:一下)?)|" +
        @"(?<question>问询|询问(?:一下)?|问问|问一下|问下|问|请教)|" +
        @"(?<directive>提醒(?:一下)?|催促|催一下|催|督促|通知|告知|告诉|知道|转告|捎话|带话)|" +
        @"(?<say>说一句|说一下|说)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NaturalOperationRegex();

    [GeneratedRegex(
        @"(?:持续|连续|一直|继续|接着|自动|陪(?:他|她|对方)?).{0,8}(?:聊天|聊|对话|沟通|交流|联系|回复)|" +
        @"(?:聊天|聊|对话|沟通|交流).{0,8}(?:持续|连续|一直|继续|接着|自动)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationCueRegex();

    [GeneratedRegex(
        @"(?<cue>目的是|目的就是|为了|希望(?:能)?|想要|主要是)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PurposeCueRegex();

    [GeneratedRegex(
        @"(?:(?:\d{1,4}(?:\.\d{1,2})?|[零〇一二三四五六七八九十百千两]{1,8}|半)\s*(?:个\s*)?(?:小时|钟头|分钟|分(?:钟)?|hours?|hrs?|h|minutes?|mins?|min))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RequestedDurationRegex();

    [GeneratedRegex(
        @"^(?:请\s*)?(?:帮我\s*)?(?:麻烦\s*)?(?:直接\s*)?(?:去\s*)?(?:(?:代替|替)我\s*)?(?:把|将)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolitePrefixRegex();

    public static bool TryParse(
        string? input,
        bool hasAttachedFiles,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var source = input.Trim();
        if (IsBlockedActionSource(source) ||
            RequiresUnfinishedContentWork(source))
        {
            return false;
        }

        if (!TryReadPlatform(source, out var platform))
        {
            return false;
        }

        if (CurrentConversationRegex().IsMatch(source) &&
            SendPayloadRegex().Match(source) is { Success: true } currentMatch)
        {
            return TryCreate(
                platform,
                recipient: null,
                currentMatch.Groups["payload"].Value,
                useCurrentConversation: true,
                hasAttachedFiles,
                out intent);
        }

        foreach (var regex in new[]
                 {
                     ForwardRecipientRegex(),
                     PlatformFirstRegex(),
                     ReverseRecipientRegex(),
                 })
        {
            var match = regex.Match(source);
            if (!match.Success ||
                !TryReadPlatform(match.Groups["platform"].Value, out platform))
            {
                continue;
            }

            var recipient = CleanRecipient(match.Groups["recipient"].Value);
            if (recipient.Length == 0 || recipient.Length > 80)
            {
                return false;
            }

            return TryCreate(
                platform,
                recipient,
                match.Groups["payload"].Value,
                useCurrentConversation: false,
                hasAttachedFiles,
                out intent);
        }

        // A visible/current conversation is an executable destination, not a
        // contact name.  Route it through the natural parser before the legacy
        // recipient regex can swallow words such as "当前这个人继续" into a
        // synthetic contact and trigger a slow, unnecessary search.
        if (CurrentConversationRegex().IsMatch(source) &&
            TryParseNaturalLanguage(
                source,
                platform,
                hasAttachedFiles,
                semanticOperationConfirmed: false,
                out intent))
        {
            return true;
        }

        if (TryParseGoalConversation(source, platform, hasAttachedFiles, out intent))
        {
            return true;
        }

        return TryParseNaturalLanguage(
            source,
            platform,
            hasAttachedFiles,
            semanticOperationConfirmed: false,
            out intent);
    }

    /// <summary>
    /// Allows the already-running semantic router to confirm an unusual chat
    /// command without allowing its classification to invent an executable
    /// target or message.  The fallback remains side-effect free and still
    /// requires an explicit platform, an explicit contact/current conversation
    /// and concrete content.  Questions, negated sends and observations are
    /// rejected even when a classifier labels them as operations.
    /// </summary>
    public static bool TryParseWithSemanticFallback(
        string? input,
        bool hasAttachedFiles,
        SemanticIntentDecision? semanticDecision,
        out ChatFastPathIntent intent)
    {
        if (TryParse(input, hasAttachedFiles, out intent))
        {
            return true;
        }

        intent = null!;
        if (semanticDecision is not
            {
                Mode: SemanticIntentMode.Operate,
                Domain: SemanticIntentDomain.Chat,
                Confidence: >= SemanticFallbackMinimumConfidence,
            } ||
            string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var source = input.Trim();
        if (IsBlockedActionSource(source) ||
            RequiresUnfinishedContentWork(source) ||
            !TryReadPlatform(source, out var platform))
        {
            return false;
        }

        return TryParseNaturalLanguage(
            source,
            platform,
            hasAttachedFiles,
            semanticOperationConfirmed: true,
            out intent);
    }

    /// <summary>
    /// Shared by the route classifier so a recognized chat action can never
    /// be marked as a text-only answer before the fast-path parser sees it.
    /// </summary>
    public static bool LooksLikeActionRequest(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var source = input.Trim();
        if (IsBlockedActionSource(source) ||
            !TryReadPlatform(source, out _))
        {
            return false;
        }

        return TryParse(source, hasAttachedFiles: false, out _) ||
               ForwardRecipientRegex().IsMatch(source) ||
               PlatformFirstRegex().IsMatch(source) ||
               ReverseRecipientRegex().IsMatch(source) ||
               GoalConversationRegex().IsMatch(source) ||
               GoalContactRegex().IsMatch(source);
    }

    /// <summary>
    /// Extracts only the supported application entity.  It deliberately says
    /// nothing about the user's intent, so callers may use a semantic router
    /// for unfamiliar wording without paying that cost for every unrelated
    /// request.  Application names are computer knowledge, not command-phrase
    /// rules.
    /// </summary>
    public static bool MentionsSupportedPlatform(string? input) =>
        !string.IsNullOrWhiteSpace(input) && TryReadPlatform(input.Trim(), out _);

    private static bool TryParseNaturalLanguage(
        string source,
        ChatFastPathPlatform platform,
        bool hasAttachedFiles,
        bool semanticOperationConfirmed,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        var currentMatch = CurrentConversationRegex().Match(source);
        var useCurrentConversation = currentMatch.Success;
        string? recipient = null;
        var targetStart = currentMatch.Index;
        var targetEnd = currentMatch.Index + currentMatch.Length;
        if (!useCurrentConversation &&
            !TryExtractNaturalRecipient(
                source,
                semanticOperationConfirmed,
                out recipient,
                out targetStart,
                out targetEnd))
        {
            return false;
        }

        var operations = NaturalOperationRegex().Matches(source)
            .Cast<Match>()
            .Where(match => match.Success)
            .ToArray();
        if (operations.Length == 0 && !semanticOperationConfirmed)
        {
            return false;
        }

        var duration = CommunicationPlanCompiler.ExtractRequestedDuration(source);
        var continueConversation = duration is not null ||
                                   ContinuationCueRegex().IsMatch(source) ||
                                   operations.Any(match =>
                                       match.Groups["conversation"].Success &&
                                       !match.Value.StartsWith(
                                           "联系",
                                           StringComparison.Ordinal));
        if (!TryExtractNaturalContent(
                source,
                targetStart,
                targetEnd,
                operations,
                semanticOperationConfirmed,
                out var content,
                out var contentIsGoal))
        {
            return false;
        }

        if (contentIsGoal || continueConversation)
        {
            if (hasAttachedFiles)
            {
                return false;
            }

            var personaMatch = PersonaRegex().Match(source);
            var persona = personaMatch.Success
                ? personaMatch.Groups["persona"].Value.Trim()
                : null;
            return TryCreateGoal(
                platform,
                recipient,
                useCurrentConversation,
                content,
                persona,
                continueConversation,
                duration,
                out intent);
        }

        return TryCreate(
            platform,
            recipient,
            content,
            useCurrentConversation,
            hasAttachedFiles,
            out intent);
    }

    private static bool TryExtractNaturalRecipient(
        string source,
        bool semanticOperationConfirmed,
        out string recipient,
        out int targetStart,
        out int targetEnd)
    {
        recipient = string.Empty;
        targetStart = 0;
        targetEnd = 0;
        foreach (var regex in new[]
                 {
                     QuotedRecipientRegex(),
                     NaturalRecipientAfterPlatformRegex(),
                     NaturalRecipientAfterRelationRegex(),
                     NaturalRecipientBeforePlatformRegex(),
                 })
        {
            foreach (Match match in regex.Matches(source))
            {
                var group = match.Groups["recipient"];
                var candidate = CleanRecipient(group.Value);
                if (!IsUsableNaturalRecipient(candidate))
                {
                    continue;
                }

                recipient = candidate;
                targetStart = source.IndexOf(
                    candidate,
                    group.Index,
                    StringComparison.OrdinalIgnoreCase);
                if (targetStart < 0)
                {
                    continue;
                }

                targetEnd = targetStart + candidate.Length;
                return true;
            }
        }

        if (!semanticOperationConfirmed)
        {
            return false;
        }

        // A semantic decision may validate an unfamiliar action word, but it
        // cannot resolve an unseparated Chinese name.  This final form accepts
        // only an explicit contact noun followed by a delimited name.
        var loose = Regex.Match(
            source,
            @"(?:联系人|好友|朋友)\s*(?<recipient>[\p{L}\p{N}_·.()（）-]{1,40})(?=\s|[，,：:])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!loose.Success)
        {
            return false;
        }

        var looseGroup = loose.Groups["recipient"];
        recipient = CleanRecipient(looseGroup.Value);
        if (!IsUsableNaturalRecipient(recipient))
        {
            return false;
        }

        targetStart = looseGroup.Index;
        targetEnd = looseGroup.Index + looseGroup.Length;
        return true;
    }

    private static bool TryCreateGoal(
        ChatFastPathPlatform platform,
        string? recipient,
        bool useCurrentConversation,
        string goal,
        string? persona,
        bool continueConversation,
        TimeSpan? duration,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        var normalizedGoal = goal.Trim()
            .Trim('：', ':', '，', ',', '。', '；', ';', '！', '!', '？', '?', ' ');
        if (normalizedGoal.Length is < 2 or > 600 ||
            !useCurrentConversation && string.IsNullOrWhiteSpace(recipient))
        {
            return false;
        }

        var planRecipient = useCurrentConversation ? "当前会话" : recipient!.Trim();
        var platformName = platform == ChatFastPathPlatform.WeChat ? "微信" : "QQ";
        var plan = CommunicationPlanCompiler.CompileGoal(
            platformName,
            planRecipient,
            normalizedGoal,
            persona,
            continueConversation,
            duration,
            useCurrentConversation);
        if (string.IsNullOrWhiteSpace(plan.OpeningMessage))
        {
            return false;
        }

        intent = new ChatFastPathIntent(
            platform,
            useCurrentConversation ? null : planRecipient,
            plan.OpeningMessage,
            useCurrentConversation,
            ReferencesFiles: false,
            ReferencesMultipleFiles: false,
            PayloadText: normalizedGoal)
        {
            MessageComposedFromGoal = true,
            CommunicationGoal = normalizedGoal,
            Persona = persona,
            CommunicationPlan = plan,
        };
        return true;
    }

    private static bool TryExtractNaturalContent(
        string source,
        int targetStart,
        int targetEnd,
        IReadOnlyList<Match> operations,
        bool semanticOperationConfirmed,
        out string content,
        out bool contentIsGoal)
    {
        content = string.Empty;
        contentIsGoal = false;
        var purpose = PurposeCueRegex().Match(source);
        if (purpose.Success &&
            TryExtractPurposeContent(
                source,
                purpose,
                targetStart,
                out content))
        {
            contentIsGoal = true;
            return true;
        }

        var directive = operations.FirstOrDefault(match =>
            match.Index >= targetEnd &&
            (match.Groups["question"].Success ||
             match.Groups["directive"].Success));
        if (directive is not null)
        {
            content = CleanNaturalContent(source[directive.Index..]);
            if (IsUsableNaturalContent(content))
            {
                contentIsGoal = true;
                return true;
            }
        }

        var operationAfterTarget = operations.FirstOrDefault(match =>
            match.Index >= targetEnd);
        var operationBeforeTarget = operations.LastOrDefault(match =>
            match.Index + match.Length <= targetStart);
        var contentStart = operationAfterTarget is null
            ? targetEnd
            : operationAfterTarget.Index + operationAfterTarget.Length;
        var raw = contentStart < source.Length
            ? source[contentStart..]
            : string.Empty;
        content = CleanNaturalContent(raw);

        if (!IsUsableNaturalContent(content))
        {
            content = TryExtractLeadingPayload(source, targetStart);
        }

        if (!IsUsableNaturalContent(content))
        {
            return false;
        }

        var governingOperation = operationAfterTarget ?? operationBeforeTarget;
        contentIsGoal = governingOperation is not null &&
                        (governingOperation.Groups["conversation"].Success ||
                         governingOperation.Groups["question"].Success ||
                         governingOperation.Groups["directive"].Success);
        if (governingOperation is null && !semanticOperationConfirmed)
        {
            return false;
        }

        return true;
    }

    private static bool TryExtractPurposeContent(
        string source,
        Match purpose,
        int targetStart,
        out string content)
    {
        var contentEnd = source.Length;
        if (purpose.Index < targetStart)
        {
            var platform = Regex.Match(
                source[purpose.Index..targetStart],
                @"(?:用|通过|在|去)?\s*(?:微信|WeChat|Weixin|QQ)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            contentEnd = platform.Success
                ? purpose.Index + platform.Index
                : targetStart;
        }

        if (contentEnd <= purpose.Index + purpose.Length)
        {
            content = string.Empty;
            return false;
        }

        content = CleanNaturalContent(
            source.Substring(
                purpose.Index + purpose.Length,
                contentEnd - purpose.Index - purpose.Length));
        return IsUsableNaturalContent(content);
    }

    private static string TryExtractLeadingPayload(string source, int targetStart)
    {
        if (targetStart <= 0)
        {
            return string.Empty;
        }

        var beforeTarget = source[..targetStart];
        var platform = Regex.Match(
            beforeTarget,
            @"(?:用|通过|在|去)?\s*(?:微信|WeChat|Weixin|QQ)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!platform.Success || platform.Index == 0)
        {
            return string.Empty;
        }

        return CleanNaturalContent(
            PolitePrefixRegex().Replace(beforeTarget[..platform.Index], string.Empty));
    }

    private static string CleanNaturalContent(string value)
    {
        var result = PersonaRegex().Replace(value, string.Empty);
        result = RequestedDurationRegex().Replace(result, string.Empty);
        result = Regex.Replace(
            result,
            @"(?:用|通过|在|去)?\s*(?:微信|WeChat|Weixin|QQ)\s*(?:(?:里|中|上|内)(?:的)?)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(
            result,
            @"(?:持续|连续|一直|继续|接着|自动)\s*(?:聊天|聊|对话|沟通|交流|联系|回复)?",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = result.Trim()
            .Trim('：', ':', '，', ',', '。', '；', ';', '！', '!', '？', '?', '“', '”', '‘', '’', '\'', ' ');
        result = Regex.Replace(
                result,
                @"^(?:的)?(?:当前|目前|现在|这个)?\s*(?:会话|对话框|聊天框|聊天窗口|窗口|联系人|对方)?\s*(?:里|中|上|内)?\s*(?:的)?\s*(?:消息|信息|文字|内容)?\s*(?:是|为)?\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Trim()
            .Trim('：', ':', '，', ',', '。', '；', ';', '！', '!', '？', '?', '“', '”', '‘', '’', '\'', ' ');
        return Regex.Replace(
                result,
                @"(?:用|通过|在|去)\s*$",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
    }

    private static bool IsUsableNaturalRecipient(string value) =>
        value.Length is >= 1 and <= 80 &&
        !TryReadPlatform(value, out _) &&
        !CurrentConversationRegex().IsMatch(value) &&
        !NaturalOperationRegex().IsMatch(value) &&
        !value.Equals("对方", StringComparison.Ordinal) &&
        !value.Equals("他", StringComparison.Ordinal) &&
        !value.Equals("她", StringComparison.Ordinal);

    private static bool IsUsableNaturalContent(string value) =>
        value.Length is >= 1 and <= 20_000 &&
        !Regex.IsMatch(
            value,
            @"^(?:一下|一会儿?|吧|哦|啊|呢)$",
            RegexOptions.CultureInvariant);

    private static bool IsBlockedActionSource(string source) =>
        InformationalQuestionRegex().IsMatch(source) ||
        NegatedChatActionRegex().IsMatch(source) ||
        ChatObservationRegex().IsMatch(source) ||
        PastChatNarrativeRegex().IsMatch(source);

    private static bool TryParseGoalConversation(
        string source,
        ChatFastPathPlatform platform,
        bool hasAttachedFiles,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        if (hasAttachedFiles || RequiresUnfinishedContentWork(source))
        {
            return false;
        }

        Match? match = null;
        var continueConversation = false;
        foreach (var candidate in new[]
                 {
                     (Regex: GoalConversationRegex(), Continues: true),
                     (Regex: GoalContactRegex(), Continues: false),
                 })
        {
            var current = candidate.Regex.Match(source);
            if (current.Success)
            {
                match = current;
                continueConversation = candidate.Continues;
                break;
            }
        }

        if (match is null)
        {
            return false;
        }

        var recipient = CleanRecipient(match.Groups["recipient"].Value);
        var instructions = match.Groups["instructions"].Value
            .Trim().TrimStart('：', ':', '，', ',', '；', ';', ' ');
        if (recipient.Length is < 1 or > 80 ||
            instructions.Length is < 2 or > 600 ||
            !GoalCueRegex().IsMatch(instructions))
        {
            return false;
        }

        var personaMatch = PersonaRegex().Match(instructions);
        var leadingPersona = match.Groups["leadingPersona"];
        var persona = leadingPersona.Success
            ? leadingPersona.Value.Trim()
            : personaMatch.Success
                ? personaMatch.Groups["persona"].Value.Trim()
                : null;
        var goalMatch = CommunicationGoalRegex().Match(instructions);
        var goal = goalMatch.Success
            ? goalMatch.Groups["goal"].Value.Trim()
            : PersonaRegex().Replace(instructions, string.Empty)
                .Trim().TrimStart('：', ':', '，', ',', '；', ';', ' ');
        goal = goal.TrimEnd('。', '！', '!', '？', '?');
        if (goal.Length is < 2 or > 360)
        {
            return false;
        }

        var plan = CommunicationPlanCompiler.CompileGoal(
            platform == ChatFastPathPlatform.WeChat ? "微信" : "QQ",
            recipient,
            goal,
            persona,
            continueConversation,
            CommunicationPlanCompiler.ExtractRequestedDuration(instructions));
        var message = plan.OpeningMessage;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        intent = new ChatFastPathIntent(
            platform,
            recipient,
            message,
            UseCurrentConversation: false,
            ReferencesFiles: false,
            ReferencesMultipleFiles: false,
            PayloadText: instructions)
        {
            MessageComposedFromGoal = true,
            CommunicationGoal = goal,
            Persona = persona,
            CommunicationPlan = plan,
        };
        return true;
    }

    private static bool TryCreate(
        ChatFastPathPlatform platform,
        string? recipient,
        string rawPayload,
        bool useCurrentConversation,
        bool hasAttachedFiles,
        out ChatFastPathIntent intent)
    {
        intent = null!;
        var payload = rawPayload.Trim().TrimStart('：', ':', '，', ',', ' ');
        if (payload.Length == 0 || payload.Length > 20_000)
        {
            return false;
        }

        var referencesFiles = FileReferenceRegex().IsMatch(payload) ||
                              hasAttachedFiles && AttachedPronounRegex().IsMatch(payload);
        var message = referencesFiles ? null : payload;
        intent = new ChatFastPathIntent(
            platform,
            recipient,
            message,
            useCurrentConversation,
            referencesFiles,
            referencesFiles && MultipleFilesRegex().IsMatch(payload),
            payload)
        {
            CommunicationPlan = referencesFiles || string.IsNullOrWhiteSpace(recipient)
                ? null
                : CommunicationPlanCompiler.CompileLiteral(
                    platform == ChatFastPathPlatform.WeChat ? "微信" : "QQ",
                    recipient,
                    message ?? payload),
        };
        return true;
    }

    private static bool RequiresUnfinishedContentWork(string source)
    {
        if (!RequiresContentWorkRegex().IsMatch(source))
        {
            return false;
        }

        // “刚才做好的文件” and similar phrases describe an existing
        // artifact rather than asking HuiTian to create new content now.
        return !FileNounRegex().IsMatch(source) ||
               !Regex.IsMatch(
                   source,
                   @"(?:刚才|刚刚|之前|已经|已|做好的|制作好的|生成好的|完成的).{0,12}" +
                   @"(?:文件|附件|产物|PPT|演示文稿|Word|Excel|文档|PDF|图片)",
                   RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static string CleanRecipient(string value)
    {
        var result = value.Trim().Trim('：', ':', '，', ',', '。', ' ');
        result = Regex.Replace(
                result,
                @"^(?:(?:代替|替|帮)我\s*)?(?:和|跟|给|向)\s+",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
        // Chinese voice input normally has no whitespace between the helper
        // phrase and a name: “替我跟小王聊…”. Strip only the explicit helper
        // phrase so genuine contact names are not shortened.
        result = Regex.Replace(
                result,
                @"^(?:(?:代替|替|帮)我)(?:和|跟|给|向)",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim();
        foreach (var suffix in new[] { "的对话框里", "的聊天框里", "的会话里", "的对话框", "的聊天框", "的会话" })
        {
            if (result.EndsWith(suffix, StringComparison.Ordinal))
            {
                result = result[..^suffix.Length].Trim();
                break;
            }
        }

        return result;
    }

    private static bool TryReadPlatform(string source, out ChatFastPathPlatform platform)
    {
        if (source.Contains("微信", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("WeChat", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("Weixin", StringComparison.OrdinalIgnoreCase))
        {
            platform = ChatFastPathPlatform.WeChat;
            return true;
        }

        if (Regex.IsMatch(source, @"(?<![A-Za-z])QQ(?![A-Za-z])", RegexOptions.IgnoreCase))
        {
            platform = ChatFastPathPlatform.QQ;
            return true;
        }

        platform = default;
        return false;
    }
}
