using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Automation;

const string releaseExe = @"D:\HuiTian\Build\Desktop-MVP-0.3.5-DDrive\HuiTian.App.exe";
const string validationRoot = @"D:\HuiTian\Build\Validation-0.3.5";

var process = Process.GetProcessesByName("HuiTian.App")
    .FirstOrDefault(item => string.Equals(item.MainModule?.FileName, releaseExe,
        StringComparison.OrdinalIgnoreCase));
if (process is null)
{
    process = Process.Start(new ProcessStartInfo(releaseExe) { UseShellExecute = true })
              ?? throw new InvalidOperationException("Could not launch release build.");
}

for (var attempt = 0; attempt < 40 && process.MainWindowHandle == IntPtr.Zero; attempt++)
{
    await Task.Delay(250);
    process.Refresh();
}

if (process.MainWindowHandle == IntPtr.Zero)
{
    throw new InvalidOperationException("HuiTian main window did not appear.");
}

var main = AutomationElement.FromHandle(process.MainWindowHandle);
var voiceButton = FindDescendant(main, "启动语音对话")
                  ?? throw new InvalidDataException("Right-side voice entry is missing.");
var attachButton = FindDescendant(main, "＋ 文件")
                   ?? throw new InvalidDataException("File attachment button is missing.");
var voiceBounds = voiceButton.Current.BoundingRectangle;
var attachBounds = attachButton.Current.BoundingRectangle;
if (voiceBounds.Left <= attachBounds.Left)
{
    throw new InvalidDataException("Voice entry is not located at the right side of the composer.");
}

Directory.CreateDirectory(validationRoot);
SaveWindow(main, Path.Combine(validationRoot, "main-window.png"));
Invoke(voiceButton);
await Task.Delay(2200);

var muteVoice = FindDescendant(main, "关闭麦克风")
                ?? throw new InvalidDataException("Voice panel mute control is missing.");
var hideVoice = FindDescendant(main, "关闭语音框")
                ?? throw new InvalidDataException("Voice panel hide control is missing.");
var stopSpeaking = FindDescendant(main, "停止朗读")
                   ?? throw new InvalidDataException("Voice panel stop-speaking control is missing.");
var muteBounds = muteVoice.Current.BoundingRectangle;
var hideBounds = hideVoice.Current.BoundingRectangle;
var closeBounds = stopSpeaking.Current.BoundingRectangle;
var desktop = System.Windows.Forms.SystemInformation.VirtualScreen;
// UI Automation reports physical pixels (the current display uses 150% DPI),
// while WPF sizes the compact panel in device-independent units.
if (closeBounds.Right - muteBounds.Left > 270 ||
    Math.Abs(muteBounds.Top - hideBounds.Top) > 4 ||
    Math.Abs(hideBounds.Top - closeBounds.Top) > 4 ||
    desktop.Right - closeBounds.Right > 70)
{
    throw new InvalidDataException(
        $"Voice controls are not compact/right-docked: {muteBounds} / {hideBounds} / {closeBounds}");
}

SaveDesktop(Path.Combine(validationRoot, "voice-panel.png"));
Invoke(stopSpeaking);
await Task.Delay(350);
_ = FindDescendant(main, "停止朗读")
    ?? throw new InvalidDataException("Stopping speech unexpectedly closed the voice panel.");
Invoke(hideVoice);
await Task.Delay(250);

var settingsButton = FindDescendant(main, "⚙  设置") ?? FindDescendant(main, "设置")
                     ?? throw new InvalidDataException("Settings entry is missing.");
Invoke(settingsButton);
await Task.Delay(500);
var generalSettingsTab = FindDescendant(main, "常规")
                         ?? throw new InvalidDataException("General settings tab is missing.");
if (!generalSettingsTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var generalTabPattern))
{
    throw new InvalidDataException("General settings tab is not selectable.");
}

((SelectionItemPattern)generalTabPattern).Select();
await Task.Delay(250);
foreach (var header in new[] { "聊天与工作已合并", "本机习惯记忆" })
{
    var expander = FindDescendant(main, header)
                   ?? throw new InvalidDataException($"Work explanation is missing: {header}");
    if (!expander.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
    {
        throw new InvalidDataException($"Work explanation is not expandable: {header}");
    }
}

_ = FindDescendant(main, "立即开始自我进化")
    ?? throw new InvalidDataException("Manual local improvement control is missing.");
_ = FindDescendant(main, "电脑闲置时自动持续优化")
    ?? throw new InvalidDataException("Idle local improvement setting is missing.");

var voiceSettingsTab = FindDescendant(main, "语音")
                       ?? throw new InvalidDataException("Voice settings tab is missing.");
if (!voiceSettingsTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var voiceTabPattern))
{
    throw new InvalidDataException("Voice settings tab is not selectable.");
}

((SelectionItemPattern)voiceTabPattern).Select();
await Task.Delay(350);
var voiceCombo = FindDescendantByAutomationId(main, "VoiceCombo")
                 ?? throw new InvalidDataException("Voice selection control is missing.");
if (!voiceCombo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var voiceComboPattern))
{
    throw new InvalidDataException("Voice selection control cannot be expanded.");
}

((ExpandCollapsePattern)voiceComboPattern).Expand();
await Task.Delay(200);
_ = FindDescendant(voiceCombo, "清和（女声·自然）")
    ?? throw new InvalidDataException("The enhanced local voice is missing from settings.");
((ExpandCollapsePattern)voiceComboPattern).Collapse();
var previewVoice = FindDescendant(main, "试听")
                   ?? throw new InvalidDataException("Voice preview control is missing.");
Invoke(previewVoice);
await Task.Delay(6000);
process.Refresh();
if (process.HasExited)
{
    throw new InvalidDataException("The app exited during enhanced voice preview.");
}

SaveWindow(main, Path.Combine(validationRoot, "settings.png"));
var settingsClose = FindDescendants(main, "关闭")
                        .OrderBy(element => element.Current.BoundingRectangle.Left)
                        .FirstOrDefault()
                    ?? throw new InvalidDataException("Settings close button is missing.");
Invoke(settingsClose);
Console.WriteLine($"PASS: main UI, voice panel, and expandable work settings => {validationRoot}");

static AutomationElement? FindDescendant(AutomationElement root, string name)
{
    try
    {
        return root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
    }
    catch (ElementNotAvailableException)
    {
        return null;
    }
}

static AutomationElement? FindDescendantByAutomationId(AutomationElement root, string automationId)
{
    try
    {
        return root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
    }
    catch (ElementNotAvailableException)
    {
        return null;
    }
}

static IReadOnlyList<AutomationElement> FindDescendants(AutomationElement root, string name)
{
    try
    {
        var matches = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
        return Enumerable.Range(0, matches.Count)
            .Select(index => matches[index])
            .ToArray();
    }
    catch (ElementNotAvailableException)
    {
        return [];
    }
}

static void Invoke(AutomationElement element)
{
    if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
    {
        throw new InvalidOperationException($"Control is not invokable: {element.Current.Name}");
    }

    ((InvokePattern)pattern).Invoke();
}

static void SaveWindow(AutomationElement window, string path)
{
    var rect = window.Current.BoundingRectangle;
    SaveBounds(Rectangle.FromLTRB(
        (int)Math.Floor(rect.Left),
        (int)Math.Floor(rect.Top),
        (int)Math.Ceiling(rect.Right),
        (int)Math.Ceiling(rect.Bottom)), path);
}

static void SaveDesktop(string path) =>
    SaveBounds(System.Windows.Forms.SystemInformation.VirtualScreen, path);

static void SaveBounds(Rectangle bounds, string path)
{
    using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
    }

    bitmap.Save(path, ImageFormat.Png);
}
