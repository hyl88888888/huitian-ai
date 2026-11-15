using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HuiTian.Infrastructure;

public sealed record WindowsDesktopState(
    string? ForegroundApplication,
    string? ForegroundProcess,
    string? ForegroundWindowTitle,
    int? ProcessId,
    DateTimeOffset ObservedAt)
{
    public IReadOnlyDictionary<string, string> ToContextDictionary()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["observed_at"] = ObservedAt.ToString("O"),
        };
        if (!string.IsNullOrWhiteSpace(ForegroundApplication))
        {
            values["foreground_application"] = ForegroundApplication;
        }

        if (!string.IsNullOrWhiteSpace(ForegroundProcess))
        {
            values["foreground_process"] = ForegroundProcess;
        }

        if (!string.IsNullOrWhiteSpace(ForegroundWindowTitle))
        {
            values["foreground_window_title"] = ForegroundWindowTitle;
        }

        if (ProcessId.HasValue)
        {
            values["foreground_process_id"] = ProcessId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }
}

/// <summary>
/// Captures only the active-window identity.  It does not take a screenshot or
/// read window contents, so intent context stays cheap and privacy-minimal.
/// </summary>
public sealed class WindowsDesktopStateProvider
{
    public WindowsDesktopState Capture()
    {
        var observedAt = DateTimeOffset.Now;
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return new WindowsDesktopState(null, null, null, null, observedAt);
            }

            GetWindowThreadProcessId(handle, out var processId);
            var title = ReadWindowTitle(handle);
            string? processName = null;
            string? applicationName = null;
            if (processId > 0)
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                applicationName = process.MainModule?.FileVersionInfo.FileDescription;
                if (string.IsNullOrWhiteSpace(applicationName))
                {
                    applicationName = processName;
                }
            }

            return new WindowsDesktopState(
                applicationName,
                processName,
                title,
                processId > 0 ? (int)processId : null,
                observedAt);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return new WindowsDesktopState(null, null, null, null, observedAt);
        }
    }

    private static string? ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0) return null;
        var builder = new StringBuilder(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumCount);
}
