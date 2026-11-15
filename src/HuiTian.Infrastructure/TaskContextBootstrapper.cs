using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HuiTian.Infrastructure;

public sealed record TaskContextWindow(
    string ProcessName,
    string Title,
    bool WasForeground);

public sealed record TaskContextFile(
    string Path,
    string Source,
    long Length,
    DateTime LastWriteTimeUtc);

/// <summary>
/// A cheap, read-only snapshot captured before the local brain starts its
/// first generation.  It is context for selecting the first action, never
/// completion evidence.
/// </summary>
public sealed record TaskContextBootstrapSnapshot(
    IReadOnlyList<TaskContextWindow> Windows,
    IReadOnlyList<TaskContextFile> Files,
    int IndexedApplicationCount,
    long ElapsedMilliseconds)
{
    public bool HasUsefulContext => Windows.Count > 0 || Files.Count > 0;

    public string FormatForAgent()
    {
        if (!HasUsefulContext)
        {
            return "【即时只读电脑上下文】未发现可复用窗口或本轮相关文件。此项只是开始任务时的环境检查，不是完成证据。";
        }

        var builder = new StringBuilder();
        builder.AppendLine("【即时只读电脑上下文】");
        builder.AppendLine("以下状态已在本轮开始时读取，可直接用于选择最快第一步；它不是任务完成证据，界面发生变化后仍须重新核验。");
        if (Windows.Count > 0)
        {
            builder.AppendLine("已打开的可见窗口：");
            foreach (var window in Windows)
            {
                builder.Append("- ")
                    .Append(window.ProcessName)
                    .Append("：")
                    .Append(window.Title);
                if (window.WasForeground)
                {
                    builder.Append("（采集时位于前台）");
                }

                builder.AppendLine();
            }
        }

        if (Files.Count > 0)
        {
            builder.AppendLine("本轮明确相关的现有文件：");
            foreach (var file in Files)
            {
                builder.Append("- [")
                    .Append(file.Source)
                    .Append("] ")
                    .Append(file.Path)
                    .Append("（")
                    .Append(file.Length)
                    .AppendLine(" bytes）");
            }
        }

        builder.Append("本机已建立 ")
            .Append(IndexedApplicationCount)
            .Append(" 项程序索引；需要启动程序时直接使用程序能力，无需先从开始菜单逐步查找。");
        return builder.ToString().TrimEnd();
    }
}

/// <summary>
/// Captures window identity and metadata for files already placed in the task.
/// It never takes screenshots, reads document contents, changes focus, or
/// starts an application.  The bounded snapshot gives the full agent useful
/// state immediately while avoiding the former inspect-before-every-action
/// loop.
/// </summary>
public sealed class TaskContextBootstrapper
{
    private const int MaximumWindows = 5;
    private const int MaximumFiles = 6;

    public Task<TaskContextBootstrapSnapshot> CaptureAsync(
        IReadOnlyList<string>? attachedFiles,
        IReadOnlyList<string>? recentArtifacts,
        int ignoredProcessId,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Capture(attachedFiles, recentArtifacts, ignoredProcessId, cancellationToken),
        cancellationToken);

    private static TaskContextBootstrapSnapshot Capture(
        IReadOnlyList<string>? attachedFiles,
        IReadOnlyList<string>? recentArtifacts,
        int ignoredProcessId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var foreground = GetForegroundWindow();
        var windows = new List<TaskContextWindow>(MaximumWindows);
        try
        {
            EnumWindows((handle, _) =>
            {
                if (windows.Count >= MaximumWindows)
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!IsWindowVisible(handle) || GetWindowTextLength(handle) <= 0)
                {
                    return true;
                }

                GetWindowThreadProcessId(handle, out var processId);
                if (processId == 0 || processId == ignoredProcessId)
                {
                    return true;
                }

                var title = ReadWindowTitle(handle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                string processName;
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                }
                catch (Exception exception) when (exception is
                    ArgumentException or InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    return true;
                }

                windows.Add(new TaskContextWindow(
                    processName,
                    Compact(title, 140),
                    handle == foreground));
                return true;
            }, IntPtr.Zero);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Window enumeration is an optimization.  File context remains
            // useful even if one desktop process disappears while enumerating.
        }

        var files = new List<TaskContextFile>(MaximumFiles);
        AddFiles(files, attachedFiles, "本轮附件", cancellationToken);
        AddFiles(files, recentArtifacts, "最近产物", cancellationToken);
        stopwatch.Stop();
        return new TaskContextBootstrapSnapshot(
            windows,
            files,
            WindowsApplicationRegistry.Default.Snapshot().Count,
            stopwatch.ElapsedMilliseconds);
    }

    private static void AddFiles(
        ICollection<TaskContextFile> destination,
        IReadOnlyList<string>? paths,
        string source,
        CancellationToken cancellationToken)
    {
        if (paths is null) return;
        foreach (var candidate in paths)
        {
            if (destination.Count >= MaximumFiles) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string path;
            try
            {
                path = Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (destination.Any(item =>
                    item.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                destination.Add(new TaskContextFile(
                    path,
                    source,
                    info.Length,
                    info.LastWriteTimeUtc));
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException)
            {
                // A disappearing or locked file must not delay the task.
            }
        }
    }

    private static string? ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return null;
        var builder = new StringBuilder(Math.Min(length + 1, 1024));
        return GetWindowText(handle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : null;
    }

    private static string Compact(string value, int maximumLength)
    {
        var compact = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximumLength
            ? compact
            : compact[..(maximumLength - 1)] + "…";
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr handle,
        StringBuilder text,
        int maximumCount);
}
