using System.Diagnostics;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed record MediaConsumptionResult(
    bool Success,
    string Detail,
    long ElapsedMilliseconds,
    string ResultCode,
    Uri? OpenedUri);

/// <summary>
/// Opens a target using the Windows shell.  The small interface keeps URL
/// construction and execution independently testable without launching a real
/// browser during regression tests.
/// </summary>
public interface IMediaTargetLauncher
{
    void Open(Uri target);
}

public sealed class ShellMediaTargetLauncher : IMediaTargetLauncher
{
    public void Open(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Process.Start(new ProcessStartInfo
        {
            FileName = target.AbsoluteUri,
            UseShellExecute = true,
        });
    }
}

/// <summary>
/// A bounded media-consumption handler.  It immediately opens a Mainland-
/// reachable search for licensed sources instead of asking the local model to
/// plan browser steps.  It never searches for cracks, pirated downloads, VIP
/// bypasses or watermark removal and never claims that a paid title was played.
/// </summary>
public sealed class WindowsMediaConsumptionFastPath
{
    private readonly IMediaTargetLauncher _launcher;

    public WindowsMediaConsumptionFastPath(IMediaTargetLauncher? launcher = null)
    {
        _launcher = launcher ?? new ShellMediaTargetLauncher();
    }

    public Task<MediaConsumptionResult> ExecuteAsync(
        MediaConsumptionIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var uri = BuildLicensedSourceSearchUri(intent);
            _launcher.Open(uri);
            stopwatch.Stop();
            var action = intent.Kind == MediaConsumptionKind.Audio ? "收听" : "观看";
            return Task.FromResult(new MediaConsumptionResult(
                true,
                $"已打开“{intent.Query}”的正版{action}来源搜索页；这还不是播放完成。" +
                "回天需要继续进入标题匹配的官方页面并确认播放器出现。平台授权、登录和会员状态可能需要用户处理。",
                stopwatch.ElapsedMilliseconds,
                "licensed_source_search_opened",
                uri));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch
        {
            stopwatch.Stop();
            return Task.FromResult(new MediaConsumptionResult(
                false,
                "暂时没有成功打开正版媒体来源。",
                stopwatch.ElapsedMilliseconds,
                "open_failed",
                null));
        }
    }

    public static Uri BuildLicensedSourceSearchUri(MediaConsumptionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (string.IsNullOrWhiteSpace(intent.Query))
        {
            throw new ArgumentException("缺少媒体名称。", nameof(intent));
        }

        var trustedSites = intent.Kind == MediaConsumptionKind.Audio
            ? AudioSourceDomains
            : VideoSourceDomains;
        var mediaWord = intent.Kind == MediaConsumptionKind.Audio
            ? "正版收听 官方"
            : "正版观看 官方";
        var siteFilter = string.Join(" OR ", trustedSites.Select(site => $"site:{site}"));
        var searchQuery = $"\"{SanitizeQuery(intent.Query)}\" {mediaWord} ({siteFilter})";
        return new Uri(
            "https://cn.bing.com/search?q=" + Uri.EscapeDataString(searchQuery),
            UriKind.Absolute);
    }

    private static string SanitizeQuery(string query)
    {
        var sanitized = query.Trim();
        foreach (var term in AccessBypassTerms)
        {
            sanitized = sanitized.Replace(term, string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        sanitized = sanitized.Trim(' ', '，', ',', '。', '！', '!', '？', '?');
        if (sanitized.Length == 0)
        {
            throw new ArgumentException("媒体名称无效。", nameof(query));
        }

        return sanitized.Length <= 180 ? sanitized : sanitized[..180];
    }

    private static readonly string[] VideoSourceDomains =
    [
        "v.qq.com",
        "iqiyi.com",
        "youku.com",
        "bilibili.com",
        "mgtv.com",
        "1905.com",
        "cctv.com",
    ];

    private static readonly string[] AudioSourceDomains =
    [
        "y.qq.com",
        "music.163.com",
        "kugou.com",
        "kuwo.cn",
        "music.apple.com",
    ];

    private static readonly string[] AccessBypassTerms =
    [
        "免费", "免会员", "不用会员", "免VIP", "破解", "盗版",
        "磁力", "BT", "网盘", "去广告", "去水印",
    ];
}
