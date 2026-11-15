using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace HuiTian.Infrastructure;

internal sealed record UserIntervention(string Title, string Detail);
internal sealed record VisibleUiControl(string Role, string Name, bool IsPassword = false);
internal sealed class UiObservationUnavailableException : Exception;

internal static class UiInterventionDetector
{
    // Prefer the edges of each sibling list. Browser overlays are commonly
    // appended after a long feed, while native dialogs can appear first.
    // Keep breadth-first traversal so a large branch cannot monopolize the
    // bounded scan. The same scheduler is exercised with synthetic trees.
    internal static UserIntervention? DetectPrioritized<TNode>(
        TNode root,
        Func<TNode, VisibleUiControl?> readControl,
        Func<TNode, IReadOnlyList<TNode>> readChildren,
        Func<bool> deadlineReached,
        int maxControls = 850,
        int maxDepth = 16)
    {
        var queue = new LinkedList<(TNode Node, int Depth)>();
        var controls = new List<VisibleUiControl>();
        queue.AddLast((root, 0));
        while (queue.Count > 0 && controls.Count < maxControls && !deadlineReached())
        {
            var (node, depth) = queue.First!.Value;
            queue.RemoveFirst();
            var control = readControl(node);
            if (control is null) continue;
            controls.Add(control);
            if (controls.Count % 8 == 0 || HasSecurityCue(control.Name))
            {
                var found = Detect(controls);
                if (found is not null) return found;
            }
            if (depth >= maxDepth || deadlineReached()) continue;
            var children = readChildren(node);
            var isDialogBranch = depth > 0 &&
                ((control.Role is "Dialog" or "Window") ||
                 (control.Role is "Pane" or "Group") && HasSecurityCue(control.Name));
            var left = 0;
            var right = children.Count - 1;
            var scheduled = new List<TNode>(Math.Min(children.Count, maxControls * 2));
            while (left <= right && scheduled.Count < maxControls * 2 &&
                   (isDialogBranch || queue.Count + scheduled.Count < maxControls * 2))
            {
                scheduled.Add(children[right--]);
                if (left <= right && scheduled.Count < maxControls * 2 &&
                    (isDialogBranch || queue.Count + scheduled.Count < maxControls * 2))
                    scheduled.Add(children[left++]);
            }
            if (isDialogBranch)
            {
                for (var index = scheduled.Count - 1; index >= 0; index--)
                    queue.AddFirst((scheduled[index], depth + 1));
                while (queue.Count > maxControls * 2) queue.RemoveLast();
            }
            else
                foreach (var child in scheduled)
                    queue.AddLast((child, depth + 1));
        }
        return Detect(controls);
    }

    private static bool HasSecurityCue(string name) =>
        new[] { "扫码", "二维码", "登录", "登陆", "验证码", "密码", "权限",
            "QR code", "sign in", "log in", "verification code", "password", "permission" }
            .Any(cue => name.Contains(cue, StringComparison.OrdinalIgnoreCase));

    // These are observed UI security indicators, not natural-language intent rules.
    internal static UserIntervention? Detect(IReadOnlyList<VisibleUiControl> controls)
    {
        var text = string.Join("\n", controls.Select(c => c.Name));
        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
        bool Button(params string[] labels) => controls.Any(c => c.Role == "Button" &&
            labels.Any(w => c.Name.Equals(w, StringComparison.OrdinalIgnoreCase)));
        bool Edit(params string[] labels) => controls.Any(c => c.Role == "Edit" &&
            labels.Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        if (Has("想要", "wants to", "请求权限", "请求访问") &&
            Has("访问", "权限", "access", "microphone", "camera") &&
            Button("允许", "Allow") && Button("屏蔽", "阻止", "Block", "Don't allow", "不允许"))
            return new("需要你确认网站权限", "网站正在请求访问设备或其他应用的权限。回天已暂停；请检查网站并亲自允许或拒绝，处理后点击“已处理，继续”。");
        var qrLoginLabel = controls.Any(c => c.Role is "Text" or "Document" or "Window" or "Image" &&
            new[] { "扫码登录", "扫码登陆", "二维码登录", "二维码登陆", "扫描二维码登录", "登录二维码", "扫码验证", "Scan QR code to log in", "Scan to log in" }
                .Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            !new[] { "教程", "说明", "帮助文档", "tutorial", "how to" }
                .Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var qrImage = controls.Any(c => c.Role == "Image" &&
            new[] { "二维码", "QR code", "扫码" }.Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        // Some Chromium pages expose a QR image without an accessible name.
        // Require a separate live scan instruction so a QR tutorial or a
        // share-page QR code does not interrupt ordinary browsing.
        var unnamedQrImage = controls.Any(c => c.Role == "Image") &&
            Has("请使用", "请打开", "使用手机", "手机扫码", "scan with", "use your phone") &&
            !Has("教程", "帮助文档", "how to", "tutorial");
        // A number of browser login dialogs render the QR as a canvas, which
        // has no Image node in UI Automation. Two separate visible cues (the
        // login heading and a phone-scan instruction/expired-code action) are
        // still sufficient evidence of a live authentication wall.
        var liveScanInstruction = controls.Any(c => c.Role is "Text" or "Document" &&
            new[] { "扫码", "二维码", "QR code" }
                .Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            new[] { "请使用", "请打开", "使用手机", "手机扫码", "打开手机", "扫码完成登录",
                "用手机", "scan with", "use your phone", "expired", "已过期", "已失效" }
                .Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) &&
            !new[] { "教程", "说明", "帮助文档", "tutorial", "how to" }
                .Any(w => c.Name.Contains(w, StringComparison.OrdinalIgnoreCase)));
        var qrLogin = qrLoginLabel && (Button("点击刷新", "刷新二维码", "Refresh QR code") ||
            qrImage || unnamedQrImage || liveScanInstruction);
        var credentialForm = controls.Any(c => c.IsPassword) &&
            Has("登录", "登陆", "sign in", "log in", "注册", "sign up");
        var otpForm = controls.Any(c => c.Role == "Edit") &&
            Has("验证码", "一次性密码", "短信登录", "手机验证", "verification code", "one-time code", "OTP", "SMS sign in") &&
            (Edit("手机号", "手机号码", "phone number", "mobile number", "验证码", "verification code", "OTP") ||
             Has("验证码登录", "验证码登陆")) &&
            Button("获取验证码", "发送验证码", "获取短信验证码", "发送短信验证码", "Send code", "Get code", "Send verification code");
        if (qrLogin || credentialForm || otpForm)
            return new("需要你完成登录", "当前页面要求扫码、验证码或账号登录。回天已暂停，请你本人完成登录；完成后点击“已处理，继续”，回天会重新检查页面再工作。");
        return null;
    }
}

internal sealed class WindowsUserInterventionObserver
{
    private readonly object _gate = new();
    private Task<UserIntervention?>? _pending;
    private readonly Func<string?, UserIntervention?> _read;
    internal WindowsUserInterventionObserver(Func<string?, UserIntervention?>? read = null) => _read = read ?? Read;
    internal static bool ShouldInspect(string tool) => tool is
        "huitian_click_control" or
        "huitian_click_point" or "huitian_type_text" or "huitian_type_active" or "huitian_press_keys";

    // Observation tools can be the first action after an external web page
    // changes into a login dialog. Inspect *after* their read-only result,
    // never before: a stalled accessibility provider must not prevent the
    // screenshot/window observation that the agent needs for recovery.
    internal static bool ShouldInspectObservedPage(string tool) => tool is
        "huitian_capture_screen" or "huitian_inspect_windows";

    internal async Task<UserIntervention?> CheckAsync(string? title, CancellationToken token)
    {
        Task<UserIntervention?> pending;
        lock (_gate)
        {
            // A hung third-party accessibility provider must not spawn an
            // unbounded number of threads, nor stall the model for minutes.
            if (_pending is { IsCompleted: false })
                throw new UiObservationUnavailableException();
            pending = _pending = Task.Run(() => _read(title), CancellationToken.None);
        }
        try { return await pending.WaitAsync(TimeSpan.FromMilliseconds(1600), token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Technical uncertainty is not evidence of a login or a request
            // for human help. The caller recovers using a read-only channel.
            throw new UiObservationUnavailableException();
        }
    }

    private static UserIntervention? Read(string? title)
    {
        try
        {
            // Use the executor's exact same target/default-window resolution.
            var window = WindowsUiAutomation.FindWindow(title);
            if (window is null) return null;
            var timer = Stopwatch.StartNew();
            var walker = TreeWalker.ControlViewWalker;
            VisibleUiControl? ReadControl(AutomationElement node)
            {
                try
                {
                    var current = node.Current;
                    return current.IsOffscreen ? null : new VisibleUiControl(
                        current.ControlType.ProgrammaticName.Replace("ControlType.", ""),
                        current.Name ?? "", current.IsPassword);
                }
                catch (ElementNotAvailableException) { return null; }
                catch (InvalidOperationException) { return null; }
                catch (COMException) { return null; }
            }
            IReadOnlyList<AutomationElement> ReadChildren(AutomationElement node)
            {
                var children = new List<AutomationElement>();
                try
                {
                    var child = walker.GetFirstChild(node);
                    // A login overlay may be appended after hundreds of feed
                    // items. Sample both ends rather than stopping at the
                    // first 700 siblings and never seeing the popup at all.
                    while (child is not null && children.Count < 240 && timer.ElapsedMilliseconds < 1200)
                    {
                        children.Add(child);
                        child = walker.GetNextSibling(child);
                    }
                    if (child is null || timer.ElapsedMilliseconds >= 1200) return children;
                    var tail = new List<AutomationElement>();
                    child = walker.GetLastChild(node);
                    while (child is not null && tail.Count < 460 && timer.ElapsedMilliseconds < 1200)
                    {
                        tail.Add(child);
                        child = walker.GetPreviousSibling(child);
                    }
                    tail.Reverse();
                    children.AddRange(tail);
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
                catch (COMException) { }
                return children;
            }
            return UiInterventionDetector.DetectPrioritized(
                window, ReadControl, ReadChildren, () => timer.ElapsedMilliseconds >= 1200);
        }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (COMException) { return null; }
    }

}
