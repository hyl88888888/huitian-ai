using System.Text.RegularExpressions;

namespace HuiTian.Core;

public enum EverydayActionKind
{
    BrowserSearch,
    OpenKnownFolder,
    MediaPlayPause,
    MediaPause,
    MediaNext,
    MediaPrevious,
    VolumeUp,
    VolumeDown,
    VolumeMute,
}

public sealed record EverydayActionIntent(
    EverydayActionKind Kind,
    string? Argument,
    string DisplayName);

/// <summary>
/// Zero-model routing for common, unambiguous Windows actions.  It is kept
/// intentionally narrow: uncertain language is handled by semantic intent
/// routing rather than guessed here.
/// </summary>
public static partial class EverydayTaskParser
{
    public static bool TryParse(string? input, out EverydayActionIntent intent)
    {
        intent = null!;
        var value = ConversationPolicy.NormalizeUserInput(input).Trim();
        if (value.Length == 0 || InformationalRegex().IsMatch(value))
        {
            return false;
        }

        var browser = BrowserSearchRegex().Match(value);
        if (browser.Success)
        {
            var query = browser.Groups["query"].Value
                .Trim().TrimEnd('。', '！', '!', '？', '?');
            if (query.Length is >= 1 and <= 300)
            {
                intent = new EverydayActionIntent(
                    EverydayActionKind.BrowserSearch,
                    query,
                    $"在浏览器搜索“{query}”");
                return true;
            }
        }

        var folder = KnownFolderRegex().Match(value);
        if (folder.Success)
        {
            var name = folder.Groups["folder"].Value;
            intent = new EverydayActionIntent(
                EverydayActionKind.OpenKnownFolder,
                name,
                $"打开{name}");
            return true;
        }

        foreach (var (regex, kind, title) in new[]
                 {
                     (MediaNextRegex(), EverydayActionKind.MediaNext, "切到下一首"),
                     (MediaPreviousRegex(), EverydayActionKind.MediaPrevious, "切到上一首"),
                     (MediaPauseRegex(), EverydayActionKind.MediaPause, "暂停播放"),
                     (VolumeMuteRegex(), EverydayActionKind.VolumeMute, "切换静音"),
                     (VolumeUpRegex(), EverydayActionKind.VolumeUp, "调高音量"),
                     (VolumeDownRegex(), EverydayActionKind.VolumeDown, "调低音量"),
                     (MediaPlayRegex(), EverydayActionKind.MediaPlayPause, "开始播放"),
                 })
        {
            if (!regex.IsMatch(value))
            {
                continue;
            }

            intent = new EverydayActionIntent(kind, null, title);
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"(?:如何|怎么|为什么|教程|方法|能不能|是否支持).{0,30}(?:浏览器|搜索|文件夹|音乐|歌曲|播放|暂停|音量|静音)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InformationalRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:打开|启动)?\s*(?:浏览器|网页)\s*(?:里|中|上)?\s*(?:搜索|搜|查找|查)\s*(?<query>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BrowserSearchRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:打开|进入|去)\s*(?<folder>桌面|下载|下载文件夹|文档|文档文件夹|图片|图片文件夹|音乐|音乐文件夹|视频|视频文件夹)(?:目录|文件夹)?[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownFolderRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:播放|放|继续播放)(?:一首|首|点)?(?:歌|歌曲|音乐)?[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaPlayRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:暂停|暂停播放|把音乐暂停|先停一下)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaPauseRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:下一首|切到下一首|换下一首|切歌)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaNextRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:上一首|切到上一首|换上一首)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaPreviousRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:音量调大|调大音量|声音大一点|音量大一点|增加音量)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeUpRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:音量调小|调小音量|声音小一点|音量小一点|降低音量)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeDownRegex();

    [GeneratedRegex(@"^(?:请\s*)?(?:帮我\s*)?(?:静音|取消静音|打开声音|关闭声音|切换静音)[。！!]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeMuteRegex();
}
