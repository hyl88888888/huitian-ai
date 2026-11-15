using System.Diagnostics;
using System.Runtime.InteropServices;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed record EverydayActionResult(
    bool Success,
    string Detail,
    long ElapsedMilliseconds,
    string ResultCode);

/// <summary>
/// Bounded handlers for frequent Windows actions that do not benefit from a
/// model plan or repeated screenshots.
/// </summary>
public sealed class WindowsEverydayFastPath
{
    public async Task<EverydayActionResult> ExecuteAsync(
        EverydayActionIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string detail;
            switch (intent.Kind)
            {
                case EverydayActionKind.BrowserSearch:
                    OpenBrowserSearch(intent.Argument ?? string.Empty);
                    detail = $"已在浏览器搜索“{intent.Argument}”。";
                    break;
                case EverydayActionKind.OpenKnownFolder:
                    var path = ResolveKnownFolder(intent.Argument ?? string.Empty);
                    OpenWithShell(path);
                    detail = $"已打开{intent.Argument}。";
                    break;
                case EverydayActionKind.MediaPlayPause:
                    await EnsureMediaPlayerThenPressAsync(VkMediaPlayPause, cancellationToken)
                        .ConfigureAwait(false);
                    detail = "已向本机播放器发送播放指令。";
                    break;
                case EverydayActionKind.MediaPause:
                    PressVirtualKey(VkMediaPlayPause);
                    detail = "已向本机播放器发送暂停指令。";
                    break;
                case EverydayActionKind.MediaNext:
                    PressVirtualKey(VkMediaNextTrack);
                    detail = "已切换到下一首。";
                    break;
                case EverydayActionKind.MediaPrevious:
                    PressVirtualKey(VkMediaPreviousTrack);
                    detail = "已切换到上一首。";
                    break;
                case EverydayActionKind.VolumeUp:
                    PressVirtualKey(VkVolumeUp);
                    PressVirtualKey(VkVolumeUp);
                    detail = "已调高系统音量。";
                    break;
                case EverydayActionKind.VolumeDown:
                    PressVirtualKey(VkVolumeDown);
                    PressVirtualKey(VkVolumeDown);
                    detail = "已调低系统音量。";
                    break;
                case EverydayActionKind.VolumeMute:
                    PressVirtualKey(VkVolumeMute);
                    detail = "已切换系统静音状态。";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(intent), intent.Kind, null);
            }

            stopwatch.Stop();
            return new EverydayActionResult(
                true,
                detail,
                stopwatch.ElapsedMilliseconds,
                "completed");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return new EverydayActionResult(
                false,
                "这个常用操作暂时没有成功执行。",
                stopwatch.ElapsedMilliseconds,
                "failed");
        }
    }

    private static void OpenBrowserSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("缺少搜索内容。", nameof(query));
        }

        var url = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query.Trim());
        OpenWithShell(url);
    }

    private static string ResolveKnownFolder(string name)
    {
        var path = name switch
        {
            "桌面" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "下载" or "下载文件夹" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "文档" or "文档文件夹" =>
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "图片" or "图片文件夹" =>
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "音乐" or "音乐文件夹" =>
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "视频" or "视频文件夹" =>
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        return path;
    }

    private static async Task EnsureMediaPlayerThenPressAsync(
        byte key,
        CancellationToken cancellationToken)
    {
        var knownMediaRunning = false;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (KnownMediaProcesses.Contains(process.ProcessName))
                {
                    knownMediaRunning = true;
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        if (!knownMediaRunning)
        {
            try
            {
                OpenWithShell("mswindowsmusic:");
                await Task.Delay(650, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A default media session may still respond to the global key.
            }
        }

        PressVirtualKey(key);
    }

    private static void OpenWithShell(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    private static void PressVirtualKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private static readonly HashSet<string> KnownMediaProcesses = new(
        [
            "Microsoft.Media.Player", "Music.UI", "wmplayer", "Spotify",
            "QQMusic", "cloudmusic", "KuGou", "KuwoMusic",
        ],
        StringComparer.OrdinalIgnoreCase);

    private const uint KeyEventKeyUp = 0x0002;
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPreviousTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    [DllImport("user32.dll")]
    private static extern void keybd_event(
        byte virtualKey,
        byte scanCode,
        uint flags,
        UIntPtr extraInfo);
}
