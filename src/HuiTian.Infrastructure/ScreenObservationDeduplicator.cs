using System.Security.Cryptography;
using System.Text;

namespace HuiTian.Infrastructure;

/// <summary>
/// Avoids paying for a second vision-model pass when the exact same pixels are
/// captured again without an intervening desktop action.  This is deliberately
/// limited to observations; it never suppresses an action or assumes success.
/// </summary>
internal sealed class ScreenObservationDeduplicator
{
    private readonly object _gate = new();
    private long _actionVersion;
    private long _observedVersion = -1;
    private string? _lastWindow;
    private string? _lastImageHash;

    internal void MarkDesktopAction()
    {
        lock (_gate) _actionVersion++;
    }

    internal DynamicToolResult Filter(string? windowTitle, DynamicToolResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.ImageDataUrl)) return result;

        var imageHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(result.ImageDataUrl)));
        var window = (windowTitle ?? string.Empty).Trim();
        lock (_gate)
        {
            var duplicate = _observedVersion == _actionVersion &&
                            string.Equals(_lastWindow, window, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(_lastImageHash, imageHash, StringComparison.Ordinal);
            _observedVersion = _actionVersion;
            _lastWindow = window;
            _lastImageHash = imageHash;
            if (!duplicate) return result;
        }

        return new DynamicToolResult(false,
            "当前画面与上次截图完全相同，且期间没有执行新的电脑动作。本次不再把同一画面交给视觉模型；请利用已有画面判断阻碍，改用不同的安全路径。若需要本人扫码或验证码，明确报告登录步骤待用户完成；不要把尚未完成的任务报为成功。",
            ResultCode: "observation_unchanged");
    }
}
