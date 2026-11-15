using System.Text.RegularExpressions;

namespace HuiTian.Core;

public enum MediaConsumptionKind
{
    Video,
    Audio,
}

/// <summary>
/// A high-confidence request to consume named media now.  This is deliberately
/// separate from questions, reviews and recommendations: matching this intent
/// means the product should take an action, not merely describe what to do.
/// </summary>
public sealed record MediaConsumptionIntent(
    MediaConsumptionKind Kind,
    string Query,
    bool ExplicitPlaybackRequested,
    string DisplayName);

/// <summary>
/// Fast, zero-model recognition for common ways people ask to watch or listen
/// to something.  It extracts the requested title/artist rather than keeping a
/// catalogue of titles.  Unfamiliar or ambiguous phrasing should still flow to
/// the semantic intent router; this parser is the low-latency, high-confidence
/// path, not the product's only language-understanding mechanism.
/// </summary>
public static partial class MediaConsumptionIntentParser
{
    public static bool TryParse(string? input, out MediaConsumptionIntent intent)
    {
        intent = null!;
        var value = ConversationPolicy.NormalizeUserInput(input).Trim();
        if (value.Length is < 2 or > 500 ||
            PastOrDiscussionRegex().IsMatch(value) ||
            RecommendationOnlyRegex().IsMatch(value))
        {
            return false;
        }

        var quotedTitle = QuotedTitleRegex().Match(value);
        var action = MatchAction(value);
        if (action is null && !SourceDiscoveryCueRegex().IsMatch(value))
        {
            return false;
        }

        var rawQuery = quotedTitle.Success
            ? quotedTitle.Groups["title"].Value
            : action?.Query ?? ExtractSourceDiscoveryQuery(value);
        var query = NormalizeQuery(rawQuery, value);
        if (query.Length is < 1 or > 180)
        {
            return false;
        }

        var hasExplicitMediaNoun = MediaNounRegex().IsMatch(value);
        var hasQuotedTitle = quotedTitle.Success;
        if (NonMediaObjectRegex().IsMatch(query) &&
            !hasExplicitMediaNoun &&
            !hasQuotedTitle)
        {
            return false;
        }

        // "想听听你的建议" and "想看看你怎么做" contain consumption
        // verbs but are conversational requests, not requests to launch media.
        if (ConversationalObjectRegex().IsMatch(query))
        {
            return false;
        }

        var kind = InferKind(value, action?.Verb);
        var explicitPlayback = ExplicitPlaybackRegex().IsMatch(value) ||
                               DesireToConsumeRegex().IsMatch(value);
        var operation = kind == MediaConsumptionKind.Audio ? "收听" : "观看";
        intent = new MediaConsumptionIntent(
            kind,
            query,
            explicitPlayback,
            $"查找并打开“{query}”的正版{operation}来源");
        return true;
    }

    /// <summary>
    /// Builds a safe discovery query after the semantic router has already
    /// established that an unfamiliar expression is a media-consumption
    /// action.  This is intentionally not used on its own: the semantic
    /// decision supplies the meaning, while this method merely avoids sending
    /// the slow general agent through screenshots to extract a title.
    /// </summary>
    public static bool TryCreateSemanticFallback(
        string? input,
        out MediaConsumptionIntent intent)
    {
        intent = null!;
        var value = ConversationPolicy.NormalizeUserInput(input).Trim();
        if (value.Length is < 2 or > 500)
        {
            return false;
        }

        var quoted = QuotedTitleRegex().Match(value);
        var query = quoted.Success
            ? quoted.Groups["title"].Value
            : SemanticRequestPrefixRegex().Replace(value, string.Empty);
        query = TrailingExecutionRegex().Replace(query, string.Empty)
            .Trim(' ', '\t', '\r', '\n', '。', '！', '!', '？', '?',
                '，', ',', '：', ':', '“', '”', '"', '\'', '《', '》');
        if (query.Length is < 1 or > 180)
        {
            return false;
        }

        var kind = InferKind(value, verb: null);
        var operation = kind == MediaConsumptionKind.Audio ? "收听" : "观看";
        intent = new MediaConsumptionIntent(
            kind,
            query,
            ExplicitPlaybackRequested: true,
            $"查找并打开“{query}”的正版{operation}来源");
        return true;
    }

    private static ActionMatch? MatchAction(string value)
    {
        foreach (var regex in new[]
                 {
                     DesireActionRegex(),
                     FindPlayableActionRegex(),
                     DirectPlaybackActionRegex(),
                 })
        {
            var match = regex.Match(value);
            if (!match.Success)
            {
                continue;
            }

            var verb = match.Groups["verb"].Success
                ? match.Groups["verb"].Value
                : string.Empty;
            return new ActionMatch(verb, match.Groups["query"].Value);
        }

        return null;
    }

    private static string ExtractSourceDiscoveryQuery(string value)
    {
        foreach (var regex in new[]
                 {
                     TitleBeforeSourceQuestionRegex(),
                     TitleAfterSourceQuestionRegex(),
                     HasPlayableSourceRegex(),
                 })
        {
            var match = regex.Match(value);
            if (match.Success)
            {
                return match.Groups["query"].Value;
            }
        }

        return string.Empty;
    }

    private static string NormalizeQuery(string? rawQuery, string original)
    {
        var query = (rawQuery ?? string.Empty).Trim();
        query = LeadingPolitenessRegex().Replace(query, string.Empty);
        query = LeadingAccessModifierRegex().Replace(query, string.Empty);
        query = LeadingClassifierRegex().Replace(query, string.Empty);
        // One request can stack both a source phrase and an execution phrase,
        // for example “长安三万里的地方并播放”.  Peel bounded suffix layers
        // instead of letting either phrase leak into the extracted title.
        for (var index = 0; index < 3; index++)
        {
            var trimmed = TrailingExecutionRegex().Replace(query, string.Empty);
            if (trimmed == query)
            {
                break;
            }

            query = trimmed;
        }
        query = query.Trim(' ', '\t', '\r', '\n', '。', '！', '!', '？', '?',
            '，', ',', '：', ':', '“', '”', '"', '\'', '《', '》');

        if (query.Length > 0)
        {
            return query;
        }

        var category = MediaNounRegex().Match(original).Groups["category"].Value;
        return string.IsNullOrWhiteSpace(category)
            ? string.Empty
            : category switch
            {
                "影片" => "电影",
                "歌曲" or "歌" => "音乐",
                _ => category,
            };
    }

    private static MediaConsumptionKind InferKind(string value, string? verb)
    {
        if (AudioCueRegex().IsMatch(value) ||
            (verb?.Contains('听', StringComparison.Ordinal) ?? false))
        {
            return MediaConsumptionKind.Audio;
        }

        return MediaConsumptionKind.Video;
    }

    private sealed record ActionMatch(string Verb, string Query);

    [GeneratedRegex("[《“\\\"](?<title>[^》”\\\"]{1,180})[》”\\\"]",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuotedTitleRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:我\s*)?(?:现在|今天|今晚|这会儿|待会儿|马上)?\s*(?:想|想要|要|希望|准备|打算)\s*(?:(?:免费|不用会员|免会员|免VIP|破解|盗版)地?\s*)?(?<verb>看看?|观看|追|听听?|收听|播放)\s*(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DesireActionRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:(?:麻烦)?(?:帮我|给我))?\s*(?:找|找找|搜|搜索|查|查找)(?:一下|下)?\s*(?<verb>能看|可以看|能观看|可以观看|能听|可以听|能播放|可以播放)\s*(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FindPlayableActionRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:(?:麻烦)?(?:帮我|给我))?\s*(?<verb>看看?|观看|听听?|收听|播放|放|打开来看看|打开看看|打开播放|打开)\s*(?:一下|下)?\s*(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DirectPlaybackActionRegex();

    [GeneratedRegex(@"^(?<query>.+?)(?:在)?(?:哪儿|哪里|什么平台|哪个平台|什么网站|哪个网站)(?:能|可以)?(?:看|观看|播放|听|收听)(?:到)?[。！!？?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleBeforeSourceQuestionRegex();

    [GeneratedRegex(@"^(?:在)?(?:哪儿|哪里|什么平台|哪个平台|什么网站|哪个网站)(?:能|可以)?(?:看|观看|播放|听|收听)(?:到)?\s*(?<query>.+?)[。！!？?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleAfterSourceQuestionRegex();

    [GeneratedRegex(@"^(?:有没有|有没)(?:什么|哪个)?(?:能|可以)(?:看|观看|播放|听|收听)\s*(?<query>.+?)(?:的)?(?:地方|平台|网站|来源)?[。！!？?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HasPlayableSourceRegex();

    [GeneratedRegex(@"(?:哪儿|哪里|什么平台|哪个平台|什么网站|哪个网站|有没有).{0,12}(?:看|观看|播放|听|收听)|(?:看|观看|播放|听|收听).{0,12}(?:哪儿|哪里|平台|网站|地方|来源)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SourceDiscoveryCueRegex();

    [GeneratedRegex(@"(?:我\s*)?(?:现在|今天|今晚|这会儿|待会儿|马上)?\s*(?:想|想要|要|希望|准备|打算)\s*(?:看看?|观看|追|听听?|收听|播放)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesireToConsumeRegex();

    [GeneratedRegex(@"(?:播放|放出来|打开(?:来)?看看|直接看|直接听|给我看|给我听)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPlaybackRegex();

    [GeneratedRegex(@"(?<category>电影|影片|电视剧|连续剧|剧集|综艺|动漫|动画片|纪录片|视频|MV|音乐|歌曲|歌|专辑|有声书|播客)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaNounRegex();

    [GeneratedRegex(@"(?:听|收听|音乐|歌曲|歌|专辑|有声书|播客)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioCueRegex();

    [GeneratedRegex(@"^(?:请|麻烦|劳驾|拜托|一下|下|给我)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingPolitenessRegex();

    [GeneratedRegex(@"^(?:(?:请|麻烦|劳驾)?(?:你)?(?:帮我|给我|替我)?\s*)?(?:(?:我)?(?:现在|今天|今晚|这会儿|待会儿|马上)?\s*(?:想|想要|要|希望|准备|打算)\s*)?(?:(?:找|找找|搜|搜索|查|查找)(?:一下|下)?\s*)?(?:能看|可以看|能观看|可以观看|能听|可以听|能播放|可以播放|看看?|观看|追|听听?|收听|播放|放)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SemanticRequestPrefixRegex();

    [GeneratedRegex(@"^(?:免费|不用会员|免会员|免VIP|破解|盗版|磁力|BT|网盘)(?:地|的|看|观看|听|收听|播放)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingAccessModifierRegex();

    [GeneratedRegex(@"^(?:(?:某|任意|随便)?(?:一)?(?:部|集|首|个|段)\s*)?(?:电影|影片|电视剧|连续剧|剧集|综艺|动漫|动画片|纪录片|视频|MV|音乐|歌曲|歌|专辑|有声书|播客)?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingClassifierRegex();

    [GeneratedRegex(@"\s*(?:(?:的)?(?:地方|平台|网站|正版来源|播放来源)|(?:并|然后|再)?(?:直接)?(?:打开|播放|放出来|给我看|给我听|开始播放))(?:一下|下)?[。！!？?]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingExecutionRegex();

    [GeneratedRegex(@"(?:我|已经|刚|以前|之前)\s*(?:看过|听过|看完|听完)|(?:好看|好听|难看|难听|怎么样|评价|影评|评分|剧情|简介|介绍|解析|讲了什么|讲的什么|是什么意思|你看过|你听过)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PastOrDiscussionRegex();

    [GeneratedRegex(@"(?:推荐|有什么好看的|有什么好听的|哪个好看|哪个好听)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RecommendationOnlyRegex();

    [GeneratedRegex(@"(?:天气|文件|文件夹|文档|表格|PPT|幻灯片|代码|网页|页面|屏幕|桌面|窗口|微信|QQ|联系人|软件|程序|应用|设置|任务|答案|回复)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonMediaObjectRegex();

    [GeneratedRegex(@"^(?:你|您的)?(?:怎么|如何|为什么|的)?(?:做|操作|想|认为|看|说|回答|建议|意见|看法|想法|解释)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConversationalObjectRegex();
}
