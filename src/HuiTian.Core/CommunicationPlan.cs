using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HuiTian.Core;

public enum CommunicationExecutionMode
{
    DeliverOnce,
    ContinueUntilObjective,
}

public enum CommunicationObjectiveKind
{
    Inform,
    Request,
    Question,
    Discuss,
    Other,
}

public sealed record CommunicationObjective(
    int Sequence,
    CommunicationObjectiveKind Kind,
    string SourceText,
    string Content);

/// <summary>
/// A lossless, executable representation of a request to communicate with a
/// person.  The original goal and every independently stated objective are
/// retained so a convenient template can never silently discard a later
/// requirement.
/// </summary>
public sealed record CommunicationPlan(
    string Platform,
    string Recipient,
    string? Persona,
    string OriginalGoal,
    IReadOnlyList<CommunicationObjective> Objectives,
    string OpeningMessage,
    CommunicationExecutionMode Mode,
    int MaximumOutgoingMessages,
    TimeSpan MaximumDuration)
{
    public bool RequiresReplyObservation =>
        Mode == CommunicationExecutionMode.ContinueUntilObjective;

    /// <summary>
    /// True only after a private/ambiguous goal has been converted into text
    /// that is safe to send to the real contact. Compiler templates leave this
    /// false so downstream delivery can fail closed instead of leaking an
    /// internal purpose such as "provide emotional support".
    /// </summary>
    public bool OpeningIsSemanticallyComposed { get; init; }

    /// <summary>
    /// The user explicitly selected the conversation already open in the
    /// desktop client.  No contact search is performed in this mode.
    /// </summary>
    public bool UseCurrentConversation { get; init; }

    /// <summary>
    /// The duration actually requested by the user, before applying the
    /// execution safety ceiling. Null means the user set no time limit;
    /// MaximumDuration is then only an internal session budget, not a goal.
    /// Missing values on older saved plans deliberately remain unspecified.
    /// </summary>
    public TimeSpan? RequestedDuration { get; init; }

    /// <summary>
    /// Examples explicitly supplied by the user of their own outgoing voice.
    /// These are private composition hints, never literal messages to send.
    /// </summary>
    public IReadOnlyList<string> UserStyleSamples { get; init; } = [];

    public bool UserDurationIsEffectiveLimit =>
        RequestedDuration is { } requested && requested <= MaximumDuration;
}

/// <summary>
/// Compiles a communication goal without calling the large model.  It uses
/// punctuation/conjunction structure to preserve all parallel objectives and
/// only applies small tone rewrites after the objective boundaries are known.
/// Unknown wording is kept verbatim instead of being rejected or guessed.
/// </summary>
public static partial class CommunicationPlanCompiler
{
    public static readonly TimeSpan DefaultSessionBudget = TimeSpan.FromMinutes(30);

    public static CommunicationPlan CompileGoal(
        string platform,
        string recipient,
        string goal,
        string? persona,
        bool continueConversation,
        TimeSpan? requestedDuration = null,
        bool useCurrentConversation = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var normalizedGoal = Normalize(goal);
        var objectives = SplitObjectives(normalizedGoal);
        var opening = ComposeOpeningMessage(objectives, persona);
        var mode = continueConversation
            ? CommunicationExecutionMode.ContinueUntilObjective
            : CommunicationExecutionMode.DeliverOnce;
        var explicitDuration = requestedDuration is { } requested && requested > TimeSpan.Zero
            ? requestedDuration
            : null;
        var duration = continueConversation
            ? ClampDuration(explicitDuration ?? DefaultSessionBudget)
            : TimeSpan.FromSeconds(20);
        var maximumOutgoingMessages = continueConversation
            ? CalculateMaximumOutgoingMessages(duration)
            : 1;

        return new CommunicationPlan(
            platform.Trim(),
            recipient.Trim(),
            string.IsNullOrWhiteSpace(persona) ? null : persona.Trim(),
            normalizedGoal,
            objectives,
            opening,
            mode,
            maximumOutgoingMessages,
            duration)
        {
            UseCurrentConversation = useCurrentConversation,
            RequestedDuration = continueConversation ? explicitDuration : null,
        };
    }

    public static CommunicationPlan CompileLiteral(
        string platform,
        string recipient,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var value = message.Trim();
        var objective = new CommunicationObjective(
            1,
            CommunicationObjectiveKind.Inform,
            value,
            value.TrimEnd('。', '！', '!', '？', '?'));
        return new CommunicationPlan(
            platform.Trim(),
            recipient.Trim(),
            null,
            value,
            [objective],
            value,
            CommunicationExecutionMode.DeliverOnce,
            1,
            TimeSpan.FromSeconds(20));
    }

    internal static IReadOnlyList<CommunicationObjective> SplitObjectives(string goal)
    {
        var parts = ObjectiveBoundaryRegex()
            .Split(goal)
            .Select(Normalize)
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count == 0)
        {
            parts.Add(goal);
        }

        var objectives = new List<CommunicationObjective>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var source = parts[index];
            var (kind, content) = ClassifyAndExtract(source);
            objectives.Add(new CommunicationObjective(
                index + 1,
                kind,
                source,
                string.IsNullOrWhiteSpace(content) ? source : content));
        }

        return objectives;
    }

    internal static TimeSpan? ExtractRequestedDuration(string source)
    {
        var match = DurationRegex().Match(source);
        if (!match.Success)
        {
            return null;
        }

        var amount = match.Groups["half"].Success
            ? 0.5
            : double.TryParse(
                match.Groups["number"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numeric)
                ? numeric
                : ParseChineseNumber(match.Groups["cnnumber"].Value);
        if (amount <= 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        var duration = IsHourUnit(unit)
            ? TimeSpan.FromHours(amount)
            : TimeSpan.FromMinutes(amount);
        // Keep the user's requested value. The compiler applies the safety
        // ceiling separately, so reaching that ceiling cannot be reported as
        // having fulfilled a longer user-selected duration.
        return duration;
    }

    private static TimeSpan ClampDuration(TimeSpan duration) =>
        TimeSpan.FromMinutes(Math.Clamp(duration.TotalMinutes, 1, 12 * 60));

    private static int CalculateMaximumOutgoingMessages(TimeSpan duration)
    {
        // Preserve the existing responsiveness for short conversations, then
        // grow much more slowly for hours-long sessions.  A 10-hour request is
        // therefore useful (309 replies at most) but can never become an
        // unbounded autonomous loop.
        if (duration <= TimeSpan.FromMinutes(30))
        {
            return Math.Clamp((int)Math.Ceiling(duration.TotalMinutes * 3), 6, 24);
        }

        var additional = (int)Math.Ceiling(
            (duration - TimeSpan.FromMinutes(30)).TotalMinutes / 2);
        return Math.Clamp(24 + additional, 24, 360);
    }

    private static bool IsHourUnit(string unit) =>
        unit.Equals("小时", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("钟头", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("h", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("hr", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("hrs", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("hour", StringComparison.OrdinalIgnoreCase) ||
        unit.Equals("hours", StringComparison.OrdinalIgnoreCase);

    private static int ParseChineseNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        static int Digit(char character) => character switch
        {
            '零' or '〇' => 0,
            '一' => 1,
            '二' or '两' => 2,
            '三' => 3,
            '四' => 4,
            '五' => 5,
            '六' => 6,
            '七' => 7,
            '八' => 8,
            '九' => 9,
            _ => 0,
        };

        if (!value.Any(character => character is '十' or '百' or '千'))
        {
            var digits = 0;
            foreach (var character in value)
            {
                digits = checked(digits * 10 + Digit(character));
            }

            return digits;
        }

        var total = 0;
        var pendingDigit = 0;
        foreach (var character in value)
        {
            var unit = character switch
            {
                '十' => 10,
                '百' => 100,
                '千' => 1000,
                _ => 0,
            };
            if (unit == 0)
            {
                pendingDigit = Digit(character);
                continue;
            }

            total += (pendingDigit == 0 ? 1 : pendingDigit) * unit;
            pendingDigit = 0;
        }

        return total + pendingDigit;
    }

    internal static string ComposeOpeningMessage(
        IReadOnlyList<CommunicationObjective> objectives,
        string? persona)
    {
        if (objectives.Count == 0)
        {
            return string.Empty;
        }

        var authoritative = !string.IsNullOrWhiteSpace(persona) &&
            AuthoritativePersonaRegex().IsMatch(persona);
        var sentences = new List<string>();
        foreach (var objective in objectives)
        {
            var sentence = RewriteObjective(objective, authoritative);
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                sentences.Add(sentence);
            }
        }

        var message = string.Join("", sentences);
        // Losslessness is more important than a polished template.  When a
        // rewrite did not retain the objective's meaningful content, append
        // the original content so later parallel requirements cannot vanish.
        foreach (var objective in objectives)
        {
            if (!HasMeaningfulCoverage(message, objective.Content))
            {
                message += $"另外，{EndSentence(objective.Content)}";
            }
        }

        return message.Trim();
    }

    private static (CommunicationObjectiveKind Kind, string Content)
        ClassifyAndExtract(string source)
    {
        var match = DirectiveRegex().Match(source);
        if (!match.Success)
        {
            return (CommunicationObjectiveKind.Other, source);
        }

        var directive = match.Groups["directive"].Value;
        var content = match.Groups["content"].Value
            .Trim('，', ',', '。', '！', '!', '？', '?', ' ');
        var kind = directive switch
        {
            "告知" or "告诉" or "通知" or "转告" or "说明" =>
                CommunicationObjectiveKind.Inform,
            "询问" or "问" or "了解" or "确认" =>
                CommunicationObjectiveKind.Question,
            "聊" or "聊天" or "讨论" or "商量" or "协商" or "沟通" =>
                CommunicationObjectiveKind.Discuss,
            "督促" or "催" or "提醒" or "要求" or "请" or "让" =>
                CommunicationObjectiveKind.Request,
            _ => CommunicationObjectiveKind.Other,
        };
        return (kind, content);
    }

    private static string RewriteObjective(
        CommunicationObjective objective,
        bool authoritative)
    {
        var content = objective.Content
            .Trim('，', ',', '。', '！', '!', '？', '?', ' ');
        if (content.Length == 0)
        {
            return string.Empty;
        }

        return objective.Kind switch
        {
            CommunicationObjectiveKind.Request when
                objective.SourceText.StartsWith("提醒", StringComparison.Ordinal) =>
                $"提醒一下，请记得{EndClause(content)}。",
            CommunicationObjectiveKind.Request when
                TodayWorkRegex().IsMatch(content) => authoritative
                    ? $"今天的工作进度怎么样了？请抓紧{content}，有问题及时沟通，完成后告诉我。"
                    : $"想确认一下今天的工作进度，请尽快{content}，有问题及时沟通，完成后告诉我一声。",
            CommunicationObjectiveKind.Request => authoritative
                ? $"请尽快{EndClause(content)}。"
                : $"麻烦{EndClause(content)}。",
            CommunicationObjectiveKind.Inform when TimeOrMeetingRegex().IsMatch(content) =>
                $"另外，{EndClause(content)}，请提前安排好时间。",
            CommunicationObjectiveKind.Inform => $"另外，{EndSentence(content)}",
            CommunicationObjectiveKind.Question => $"想确认一下，{EndQuestion(content)}",
            CommunicationObjectiveKind.Discuss => $"想和你沟通一下，{EndSentence(content)}",
            _ => $"另外，{EndSentence(content)}",
        };
    }

    private static string EndClause(string value) =>
        value.TrimEnd('。', '！', '!', '？', '?', '，', ',', ' ');

    private static string EndSentence(string value) =>
        EndClause(value) + "。";

    private static string EndQuestion(string value) =>
        value.TrimEnd('。', '！', '!', '？', '?', '，', ',', ' ') + "？";

    private static bool HasMeaningfulCoverage(string message, string content)
    {
        var target = CoverageNoiseRegex().Replace(content, string.Empty);
        var source = CoverageNoiseRegex().Replace(message, string.Empty);
        if (target.Length == 0 || source.Contains(target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Chinese has no mandatory spaces.  Character bigrams give a cheap,
        // language-independent loss check while tolerating a small tone rewrite.
        var grams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < target.Length; index++)
        {
            grams.Add(target.Substring(index, 2));
        }

        if (grams.Count == 0)
        {
            return source.Contains(target, StringComparison.OrdinalIgnoreCase);
        }

        var covered = grams.Count(gram =>
            source.Contains(gram, StringComparison.OrdinalIgnoreCase));
        return covered >= Math.Ceiling(grams.Count * 0.72);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWhitespace)
                {
                    builder.Append(' ');
                }

                previousWhitespace = true;
                continue;
            }

            previousWhitespace = false;
            builder.Append(character);
        }

        return builder.ToString()
            .Trim('，', ',', '。', '！', '!', '？', '?', ' ');
    }

    [GeneratedRegex(
        @"\s*(?:[；;。！!]+|，?\s*(?:并且|并|同时|另外|以及|再|然后)\s*)\s*",
        RegexOptions.CultureInvariant)]
    private static partial Regex ObjectiveBoundaryRegex();

    [GeneratedRegex(
        @"^(?<directive>告知|告诉|通知|转告|说明|询问|问|了解|确认|聊天|聊|讨论|商量|协商|沟通|督促|催|提醒|要求|请|让)(?:一下)?(?:让)?(?:他|她|对方|联系人|其)?(?:要|去)?\s*(?<content>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"(?:老板|领导|负责人|主管|经理)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthoritativePersonaRegex();

    [GeneratedRegex(@"(?:今天|今日).*(?:工作|任务)|(?:工作|任务).*(?:今天|今日)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TodayWorkRegex();

    [GeneratedRegex(@"(?:开会|会议|\d{1,2}\s*[点时:]|早上|上午|下午|晚上|明天|后天|周[一二三四五六日天])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeOrMeetingRegex();

    [GeneratedRegex(@"[\s，,。；;：:！!？?的了呢吧啊呀嘛哦哈]",
        RegexOptions.CultureInvariant)]
    private static partial Regex CoverageNoiseRegex();

    [GeneratedRegex(
        @"(?:(?<number>\d{1,4}(?:\.\d{1,2})?)|(?<cnnumber>[零〇一二三四五六七八九十百千两]{1,8})|(?<half>半))\s*(?:个\s*)?(?<unit>小时|钟头|分钟|分(?:钟)?|hours?|hrs?|h|minutes?|mins?|min)\b?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();
}
