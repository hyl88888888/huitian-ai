using HuiTian.Infrastructure;
using System.Diagnostics;
using System.Windows.Forms;

if (args.Contains("--platform-guard-only"))
{
    foreach (var supported in new[] { "微信", "WeChat", "Weixin", "QQ", "qq", "腾讯QQ" })
    {
        if (!WindowsUiAutomation.IsSupportedChatSendPlatform(supported))
            throw new InvalidDataException($"A supported chat alias was rejected: {supported}");
    }
    foreach (var unsupported in new[] { "Soul", "抖音", "douyin", "小红书", "任意窗口标题" })
    {
        if (WindowsUiAutomation.IsSupportedChatSendPlatform(unsupported))
            throw new InvalidDataException($"An unsupported platform was accepted: {unsupported}");

        var results = new[]
        {
            WindowsUiAutomation.SendChatMessage(unsupported, "测试对象", "仅验证拒绝路径"),
            WindowsUiAutomation.SendChatPayload(unsupported, "测试对象", "仅验证拒绝路径", null),
            WindowsUiAutomation.SendChatFiles(unsupported, "测试对象", [@"D:\HuiTian\not-a-real-file.txt"]),
            WindowsUiAutomation.SendToVerifiedCurrentChat(unsupported, "仅验证拒绝路径", null),
        };
        if (results.Any(result => result.Success || result.ResultCode != "unsupported_platform" ||
                                  !result.Text.Contains("没有输入或发送", StringComparison.Ordinal)))
            throw new InvalidDataException($"An unsupported send path was not rejected: {unsupported}");
    }
    var fastPath = new WindowsChatFastPath();
    var rejectedFastPath = fastPath.SendAsync("Soul", "测试对象", "仅验证拒绝路径")
        .GetAwaiter().GetResult();
    var rejectedContinuation = fastPath.ContinueVerifiedTargetAsync("抖音", "仅验证拒绝路径")
        .GetAwaiter().GetResult();
    if (rejectedFastPath.Success || rejectedFastPath.ResultCode != "unsupported_platform" ||
        rejectedContinuation.Success || rejectedContinuation.ResultCode != "unsupported_platform")
        throw new InvalidDataException("The product fast path must retain the unsupported-platform result.");
    Console.WriteLine("PASS: unsupported chat platforms are rejected before any window or file interaction; WeChat/QQ aliases remain supported.");
    return;
}

const string windowTitle = "回天自动化输入验证";
using var ready = new ManualResetEventSlim();
Form? form = null;
TextBox? input = null;
TextBox? genericInput = null;
PasteCaptureControl? customInput = null;
DoubleClickButton? doubleClickButton = null;
Button? pointButton = null;
TextBox? chatSearch = null;
Button? chatContact = null;
ChatPayloadTextBox? chatMessage = null;
Label? sentMessage = null;
Label? chatTitle = null;
Button? chatHistoryMention = null;
IDataObject? userClipboard = null;
var doubleClickCount = 0;
var pointClickCount = 0;
var historyMentionClickCount = 0;
var showSearchResult = true;
var titleOnOpen = "凉雨";

if (!WindowsUiAutomation.AreLikelyChineseHomophones("良宇", "凉雨") ||
    WindowsUiAutomation.ScoreContactCandidate("良宇", "凉雨（工作号）") >= 760)
{
    throw new InvalidDataException("Chinese homophone matching must suggest 良宇/凉雨 without auto-selecting it.");
}

if (WindowsUiAutomation.IsLikelyChatSearchResultBounds(
        new System.Windows.Rect(810, 220, 240, 44),
        new System.Windows.Rect(510, 28, 250, 30),
        new System.Windows.Rect(0, 0, 1200, 620)))
{
    throw new InvalidDataException("A same-name chat-history row must not be treated as a search result.");
}

var scaledDpiPoint = WindowsUiAutomation.MapCapturedPoint(
    500,
    300,
    1000,
    600,
    new System.Drawing.Rectangle(100, 50, 1500, 900));
if (Math.Abs(scaledDpiPoint.X - 850) > 1 || Math.Abs(scaledDpiPoint.Y - 500) > 1)
{
    throw new InvalidDataException($"150% DPI coordinate map mismatch: {scaledDpiPoint}");
}

var multiMonitorBounds = new System.Drawing.Rectangle(-1920, -200, 4480, 1800);
var multiMonitorPoint = WindowsUiAutomation.MapCapturedPoint(
    896,
    360,
    1792,
    720,
    multiMonitorBounds);
if (Math.Abs(multiMonitorPoint.X - 320) > 2 || Math.Abs(multiMonitorPoint.Y - 700) > 2)
{
    throw new InvalidDataException($"Multi-monitor coordinate map mismatch: {multiMonitorPoint}");
}

var multiMonitorLastPixel = WindowsUiAutomation.MapCapturedPoint(
    1791,
    719,
    1792,
    720,
    multiMonitorBounds);
if (!multiMonitorBounds.Contains(multiMonitorLastPixel.X, multiMonitorLastPixel.Y))
{
    throw new InvalidDataException($"Mapped edge escaped virtual desktop: {multiMonitorLastPixel}");
}

var oddScaleBounds = new System.Drawing.Rectangle(-1537, 87, 2559, 1439);
var oddScaleFirst = WindowsUiAutomation.MapCapturedPoint(
    0, 0, 1280, 720, oddScaleBounds);
var oddScaleLast = WindowsUiAutomation.MapCapturedPoint(
    1279, 719, 1280, 720, oddScaleBounds);
if (!oddScaleBounds.Contains(oddScaleFirst.X, oddScaleFirst.Y) ||
    !oddScaleBounds.Contains(oddScaleLast.X, oddScaleLast.Y) ||
    oddScaleFirst.X >= oddScaleLast.X || oddScaleFirst.Y >= oddScaleLast.Y)
{
    throw new InvalidDataException(
        $"Fractional/negative-origin mapping escaped bounds: {oddScaleFirst} -> {oddScaleLast}");
}

var uiThread = new Thread(() =>
{
    form = new Form
    {
        Text = windowTitle,
        Width = 1220,
        Height = 620,
        StartPosition = FormStartPosition.CenterScreen,
    };
    input = new TextBox
    {
        AccessibleName = "测试输入框",
        Left = 24,
        Top = 28,
        Width = 440,
    };
    doubleClickButton = new DoubleClickButton
    {
        Text = "双击测试",
        AccessibleName = "双击测试",
        Left = 24,
        Top = 78,
        Width = 140,
        Height = 42,
    };
    customInput = new PasteCaptureControl
    {
        AccessibleName = "自绘输入区",
        Left = 24,
        Top = 140,
        Width = 440,
        Height = 46,
        TabStop = true,
    };
    pointButton = new Button
    {
        Text = "坐标点击测试",
        AccessibleName = "坐标点击测试",
        Left = 24,
        Top = 202,
        Width = 140,
        Height = 32,
    };
    genericInput = new TextBox
    {
        Name = "genericBottomInput",
        AccessibleName = "通用消息输入框",
        Left = 24,
        Top = 242,
        Width = 440,
    };
    chatSearch = new TextBox
    {
        AccessibleName = "搜索联系人",
        Left = 510,
        Top = 28,
        Width = 250,
    };
    chatContact = new Button
    {
        Text = "凉雨（工作号）",
        AccessibleName = "凉雨（工作号）",
        Left = 510,
        Top = 78,
        Width = 250,
        Height = 42,
        Visible = false,
    };
    chatMessage = new ChatPayloadTextBox
    {
        AccessibleName = "消息输入框",
        Left = 820,
        Top = 420,
        Width = 340,
        Height = 52,
        Multiline = true,
        Visible = false,
    };
    sentMessage = new Label
    {
        AccessibleName = "尚未发送消息",
        Left = 820,
        Top = 340,
        Width = 340,
        Height = 52,
        Visible = false,
    };
    chatTitle = new Label
    {
        Text = "凉雨",
        AccessibleName = "凉雨",
        Left = 900,
        Top = 32,
        Width = 220,
        Height = 32,
        Visible = false,
    };
    chatHistoryMention = new Button
    {
        Text = "凉雨",
        AccessibleName = "凉雨",
        Left = 860,
        Top = 210,
        Width = 250,
        Height = 42,
        Visible = true,
    };
    doubleClickButton.DoubleClick += (_, _) => Interlocked.Increment(ref doubleClickCount);
    pointButton.Click += (_, _) => Interlocked.Increment(ref pointClickCount);
    chatSearch.TextChanged += (_, _) =>
        chatContact.Visible = showSearchResult &&
                              (chatSearch.Text.Contains("凉雨", StringComparison.Ordinal) ||
                               chatSearch.Text.Contains("良宇", StringComparison.Ordinal));
    chatContact.Click += (_, _) =>
    {
        chatTitle.Text = titleOnOpen;
        chatTitle.AccessibleName = titleOnOpen;
        chatTitle.Visible = true;
        chatMessage.Visible = true;
        chatMessage.Focus();
    };
    chatHistoryMention.Click += (_, _) =>
        Interlocked.Increment(ref historyMentionClickCount);
    chatMessage.KeyDown += (_, eventArgs) =>
    {
        if (eventArgs.KeyCode != Keys.Enter || eventArgs.Shift)
        {
            return;
        }

        var sentValue = chatMessage.PendingFiles.Count > 0
            ? string.Join(";", chatMessage.PendingFiles.Select(Path.GetFileName))
            : chatMessage.Text;
        sentMessage.Text = sentValue;
        sentMessage.AccessibleName = sentValue;
        sentMessage.Visible = true;
        chatMessage.Clear();
        chatMessage.ClearPendingFiles();
        eventArgs.SuppressKeyPress = true;
        eventArgs.Handled = true;
    };
    form.Controls.Add(input);
    form.Controls.Add(doubleClickButton);
    form.Controls.Add(customInput);
    form.Controls.Add(pointButton);
    form.Controls.Add(genericInput);
    form.Controls.Add(chatSearch);
    form.Controls.Add(chatContact);
    form.Controls.Add(chatTitle);
    form.Controls.Add(chatHistoryMention);
    form.Controls.Add(chatMessage);
    form.Controls.Add(sentMessage);
    form.Shown += (_, _) => ready.Set();
    Application.Run(form);
});
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();

try
{
    if (!ready.Wait(TimeSpan.FromSeconds(8)))
    {
        throw new TimeoutException("Test window did not open.");
    }

    userClipboard = (IDataObject?)form!.Invoke(Clipboard.GetDataObject);

    var typed = WindowsUiAutomation.TypeText(
        windowTitle,
        "测试输入框",
        "回天输入验证",
        replace: true);
    if (!typed.Success)
    {
        throw new InvalidDataException(typed.Text);
    }

    var firstValue = (string)form!.Invoke(() => input!.Text);
    if (firstValue != "回天输入验证")
    {
        throw new InvalidDataException($"Named input mismatch: {firstValue}");
    }

    var focused = WindowsUiAutomation.Click(windowTitle, "测试输入框", clickCount: 1);
    if (!focused.Success)
    {
        throw new InvalidDataException(focused.Text);
    }

    form.Invoke(() =>
    {
        input!.SelectionStart = input.TextLength;
        input.SelectionLength = 0;
    });
    var activeTyped = WindowsUiAutomation.TypeActive(
        windowTitle,
        "继续成功",
        replace: false);
    var secondValue = (string)form.Invoke(() => input!.Text);
    if (!activeTyped.Success)
    {
        throw new InvalidDataException($"{activeTyped.Text} Actual value: {secondValue}");
    }

    if (secondValue != "回天输入验证继续成功")
    {
        throw new InvalidDataException($"Focused input mismatch: {secondValue}");
    }

    var inspected = WindowsUiAutomation.Inspect(windowTitle, 100);
    if (!inspected.Success || !inspected.Text.Contains("genericBottomInput", StringComparison.Ordinal))
    {
        throw new InvalidDataException($"Unnamed editable inspection missing AutomationId: {inspected.Text}");
    }

    var captured = WindowsUiAutomation.CaptureScreen(windowTitle);
    if (!captured.Success || string.IsNullOrWhiteSpace(captured.ImageDataUrl))
    {
        throw new InvalidDataException($"Screen capture failed: {captured.Text}");
    }

    var separator = captured.ImageDataUrl.IndexOf(',');
    using var imageBytes = new MemoryStream(Convert.FromBase64String(captured.ImageDataUrl[(separator + 1)..]));
    using var capturedImage = new System.Drawing.Bitmap(imageBytes);
    var target = ((int X, int Y, System.Drawing.Rectangle Bounds))form.Invoke(() =>
    {
        var centre = pointButton!.PointToScreen(
            new System.Drawing.Point(pointButton.Width / 2, pointButton.Height / 2));
        return (centre.X, centre.Y, form.Bounds);
    });
    var captureX = Math.Clamp(
        (int)Math.Floor((target.X - target.Bounds.Left) * capturedImage.Width / (double)target.Bounds.Width),
        0,
        capturedImage.Width - 1);
    var captureY = Math.Clamp(
        (int)Math.Floor((target.Y - target.Bounds.Top) * capturedImage.Height / (double)target.Bounds.Height),
        0,
        capturedImage.Height - 1);
    var pointClicked = WindowsUiAutomation.ClickPoint(
        windowTitle,
        captureX,
        captureY,
        direct: false,
        clickCount: 1);
    Thread.Sleep(180);
    if (!pointClicked.Success || Volatile.Read(ref pointClickCount) != 1)
    {
        throw new InvalidDataException(
            $"Scaled screenshot coordinate click failed: {pointClicked.Text}; clicks={pointClickCount}");
    }

    var genericTyped = WindowsUiAutomation.TypeText(
        windowTitle,
        "输入框",
        "通用输入框定位成功",
        replace: true);
    var genericValue = (string)form.Invoke(() => genericInput!.Text);
    if (!genericTyped.Success || genericValue != "通用输入框定位成功")
    {
        throw new InvalidDataException($"Generic input mismatch: {genericTyped.Text} Actual value: {genericValue}");
    }

    form.Invoke(() =>
    {
        var testClipboard = new DataObject();
        testClipboard.SetData(DataFormats.UnicodeText, true, "用户原剪贴板文字");
        testClipboard.SetData("HuiTian.Test.Marker", false, "marker-value");
        Clipboard.SetDataObject(testClipboard, true);
        form.Activate();
        form.ActiveControl = customInput;
        customInput!.Select();
        customInput.Focus();
    });
    Thread.Sleep(90);
    var focusBeforeCustomInput = (string)form.Invoke(() =>
        $"active={form.ActiveControl?.Name}/{form.ActiveControl?.GetType().Name}; focused={customInput!.Focused}");
    var customTyped = WindowsUiAutomation.TypeActive(
        windowTitle,
        "自绘区域输入成功",
        replace: false);
    var customValue = (string)form.Invoke(() => customInput!.TextValue);
    var genericAfterCustomInput = (string)form.Invoke(() => genericInput!.Text);
    var restoredClipboardText = (string)form.Invoke(Clipboard.GetText);
    var restoredMarker = (string?)form.Invoke(() =>
        Clipboard.GetDataObject()?.GetData("HuiTian.Test.Marker", false)?.ToString());
    if (!customTyped.Success || customValue != "自绘区域输入成功")
    {
        throw new InvalidDataException($"Custom input mismatch: {customTyped.Text} Actual value: {customValue}; {focusBeforeCustomInput}; generic={genericAfterCustomInput}");
    }

    if (restoredClipboardText != "用户原剪贴板文字" || restoredMarker != "marker-value")
    {
        throw new InvalidDataException(
            $"Clipboard was not fully restored: text={restoredClipboardText}, marker={restoredMarker}");
    }

    var doubleClicked = WindowsUiAutomation.Click(
        windowTitle,
        "双击测试",
        clickCount: 2);
    if (!doubleClicked.Success)
    {
        throw new InvalidDataException(doubleClicked.Text);
    }

    Thread.Sleep(250);
    if (Volatile.Read(ref doubleClickCount) != 1)
    {
        throw new InvalidDataException(
            $"Expected one Windows double-click event, actual {doubleClickCount}.");
    }

    const string outgoingMessage = "回天专用消息快速路径验证";

    form.Invoke(() =>
    {
        showSearchResult = false;
        chatContact!.Visible = false;
        chatTitle!.Visible = false;
        chatMessage!.Visible = false;
        sentMessage!.Text = string.Empty;
        sentMessage.AccessibleName = "尚未发送消息";
        sentMessage.Visible = false;
    });
    var historyOnly = WindowsUiAutomation.SendChatMessageToSyntheticWindow(
        windowTitle,
        "凉雨",
        outgoingMessage);
    if (historyOnly.Success ||
        Volatile.Read(ref historyMentionClickCount) != 0 ||
        (string)form.Invoke(() => sentMessage!.Text) != string.Empty)
    {
        throw new InvalidDataException(
            $"A chat-history name was incorrectly selected: {historyOnly.Text}; historyClicks={historyMentionClickCount}");
    }

    form.Invoke(() =>
    {
        showSearchResult = true;
        chatSearch!.Clear();
        chatContact!.Visible = false;
        chatTitle!.Visible = false;
        chatMessage!.Visible = false;
    });
    var homophoneCandidate = WindowsUiAutomation.SendChatMessageToSyntheticWindow(
        windowTitle,
        "良宇",
        outgoingMessage);
    var homophoneUiState = (string)form.Invoke(() =>
    {
        var windowElement = System.Windows.Automation.AutomationElement.FromHandle(form.Handle);
        var searchElement = windowElement.FindFirst(
            System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty,
                "搜索联系人"));
        var resultElement = windowElement.FindFirst(
            System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.NameProperty,
                "凉雨（工作号）"));
        return $"search={chatSearch!.Text}; resultVisible={chatContact!.Visible}; " +
               $"searchUia={searchElement?.Current.BoundingRectangle}; resultUia={resultElement?.Current.BoundingRectangle}; " +
               $"windowUia={windowElement.Current.BoundingRectangle}; geometry=" +
               (searchElement is not null && resultElement is not null &&
                WindowsUiAutomation.IsLikelyChatSearchResultBounds(
                    resultElement.Current.BoundingRectangle,
                    searchElement.Current.BoundingRectangle,
                    windowElement.Current.BoundingRectangle));
    });
    if (homophoneCandidate.Success ||
        !homophoneCandidate.Text.Contains("同音", StringComparison.Ordinal) ||
        !homophoneCandidate.Text.Contains("凉雨", StringComparison.Ordinal) ||
        (string)form.Invoke(() => sentMessage!.Text) != string.Empty)
    {
        throw new InvalidDataException(
            $"A homophone candidate must be returned for confirmation, never guessed: {homophoneCandidate.Text}; {homophoneUiState}; inspect={WindowsUiAutomation.Inspect(windowTitle, 100).Text}");
    }

    form.Invoke(() =>
    {
        titleOnOpen = "凉风";
        chatSearch!.Clear();
        chatContact!.Visible = false;
        chatTitle!.Visible = false;
        chatMessage!.Visible = false;
        chatMessage.Clear();
    });
    var wrongTitle = WindowsUiAutomation.SendChatMessageToSyntheticWindow(
        windowTitle,
        "凉雨",
        outgoingMessage);
    if (wrongTitle.Success ||
        !wrongTitle.RequiresVerification ||
        (string)form.Invoke(() => chatMessage!.Text) != string.Empty ||
        (string)form.Invoke(() => sentMessage!.Text) != string.Empty)
    {
        throw new InvalidDataException(
            $"A mismatched conversation title must stop before typing: {wrongTitle.Text}");
    }

    form.Invoke(() =>
    {
        titleOnOpen = "凉雨";
        chatSearch!.Clear();
        chatContact!.Visible = false;
        chatTitle!.Visible = false;
        chatMessage!.Visible = false;
        chatMessage.Clear();
    });
    var messageStopwatch = Stopwatch.StartNew();
    var chatSent = WindowsUiAutomation.SendChatMessageToSyntheticWindow(
        windowTitle,
        "凉雨",
        outgoingMessage);
    messageStopwatch.Stop();
    var sentValue = (string)form.Invoke(() => sentMessage!.Text);
    var editorValue = (string)form.Invoke(() => chatMessage!.Text);
    if (!chatSent.Success || chatSent.RequiresVerification ||
        sentValue != outgoingMessage || editorValue.Length != 0)
    {
        throw new InvalidDataException(
            $"Dedicated chat send failed: {chatSent.Text}; sent={sentValue}; editor={editorValue}");
    }
    if (!WindowsUiAutomation.VerifyVisibleChatRecipient(windowTitle, "凉雨") ||
        WindowsUiAutomation.VerifyVisibleChatRecipient(windowTitle, "凉风") ||
        WindowsUiAutomation.ReadVisibleChatTitle(windowTitle) != "凉雨")
    {
        throw new InvalidDataException("The open chat title must be readable and match only its actual recipient.");
    }

    if (messageStopwatch.Elapsed >= TimeSpan.FromSeconds(10))
    {
        throw new InvalidDataException(
            $"Dedicated chat message path exceeded ten seconds: {messageStopwatch.Elapsed}.");
    }

    const string currentChatMessage = "回天当前会话文字快速路径验证";
    form.Invoke(() =>
    {
        sentMessage!.Text = string.Empty;
        sentMessage.AccessibleName = "尚未发送当前会话消息";
        sentMessage.Visible = false;
        chatMessage!.Clear();
        chatMessage.Focus();
    });
    var currentMessageStopwatch = Stopwatch.StartNew();
    var currentChatSent = WindowsUiAutomation.SendToSyntheticCurrentChat(
        windowTitle, currentChatMessage, null);
    currentMessageStopwatch.Stop();
    var currentSentValue = (string)form.Invoke(() => sentMessage!.Text);
    if (!currentChatSent.Success || currentSentValue != currentChatMessage)
    {
        throw new InvalidDataException(
            $"Explicit current-chat text path failed: {currentChatSent.Text}; sent={currentSentValue}");
    }

    if (currentMessageStopwatch.Elapsed >= TimeSpan.FromSeconds(10))
    {
        throw new InvalidDataException(
            $"Explicit current-chat text path exceeded ten seconds: {currentMessageStopwatch.Elapsed}.");
    }

    var testFileRoot = Path.Combine(
        Directory.Exists(@"D:\HuiTian\Cache") ? @"D:\HuiTian\Cache" : Path.GetTempPath(),
        "UiAutomationSmokeTests");
    Directory.CreateDirectory(testFileRoot);
    var testFile = Path.Combine(testFileRoot, "回天文件快速发送验证.txt");
    File.WriteAllText(testFile, "synthetic chat attachment test");
    try
    {
        form.Invoke(() =>
        {
            sentMessage!.Text = string.Empty;
            sentMessage.AccessibleName = "尚未发送文件";
            sentMessage.Visible = false;
            chatMessage!.Clear();
            chatMessage.ClearPendingFiles();
            chatMessage.Focus();
        });
        var fileStopwatch = Stopwatch.StartNew();
        var fileSent = WindowsUiAutomation.SendChatFilesToSyntheticWindow(
            windowTitle,
            "凉雨",
            [testFile]);
        fileStopwatch.Stop();
        var sentFileName = (string)form.Invoke(() => sentMessage!.Text);
        if (!fileSent.Success || fileSent.RequiresVerification ||
            sentFileName != Path.GetFileName(testFile))
        {
            throw new InvalidDataException(
                $"Dedicated chat file send failed: {fileSent.Text}; sent={sentFileName}");
        }

        if (fileStopwatch.Elapsed >= TimeSpan.FromSeconds(10))
        {
            throw new InvalidDataException(
                $"Dedicated chat file path exceeded ten seconds: {fileStopwatch.Elapsed}.");
        }

        form.Invoke(() =>
        {
            sentMessage!.Text = string.Empty;
            sentMessage.AccessibleName = "尚未发送当前会话文件";
            sentMessage.Visible = false;
            chatMessage!.Clear();
            chatMessage.ClearPendingFiles();
            chatMessage.Focus();
        });
        var currentFileStopwatch = Stopwatch.StartNew();
        var currentFileSent = WindowsUiAutomation.SendChatFilesToSyntheticWindow(
            windowTitle,
            string.Empty,
            [testFile]);
        currentFileStopwatch.Stop();
        var currentSentFileName = (string)form.Invoke(() => sentMessage!.Text);
        if (!currentFileSent.Success || currentFileSent.RequiresVerification ||
            currentSentFileName != Path.GetFileName(testFile))
        {
            throw new InvalidDataException(
                $"Explicit current-chat file path failed: {currentFileSent.Text}; sent={currentSentFileName}");
        }

        if (currentFileStopwatch.Elapsed >= TimeSpan.FromSeconds(10))
        {
            throw new InvalidDataException(
                $"Explicit current-chat file path exceeded ten seconds: {currentFileStopwatch.Elapsed}.");
        }
    }
    finally
    {
        File.Delete(testFile);
    }

    Console.WriteLine($"PASS: fractional-DPI/multi-monitor mapping, screenshot-coordinate click, named/generic input, native custom input, clipboard preservation, inspection, virtual double-click, result-region filtering, conservative Chinese homophone suggestions, title verification, named/current-chat text and file paths under ten seconds (namedText={messageStopwatch.ElapsedMilliseconds}ms; currentText={currentMessageStopwatch.ElapsedMilliseconds}ms)");
}
finally
{
    if (form is { IsDisposed: false })
    {
        form.Invoke(() =>
        {
            if (userClipboard is null)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetDataObject(userClipboard, true, 10, 30);
            }

            form.Close();
        });
    }

    uiThread.Join(TimeSpan.FromSeconds(5));
}

internal sealed class DoubleClickButton : Button
{
    public DoubleClickButton()
    {
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
    }
}

internal sealed class PasteCaptureControl : Control
{
    public string TextValue { get; private set; } = string.Empty;

    public PasteCaptureControl()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint, true);
        BackColor = System.Drawing.Color.White;
        ForeColor = System.Drawing.Color.Black;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.V)
        {
            PasteClipboardText();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V))
        {
            PasteClipboardText();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawRectangle(System.Drawing.Pens.DimGray, 0, 0, Width - 1, Height - 1);
        e.Graphics.DrawString(
            TextValue,
            Font,
            System.Drawing.Brushes.Black,
            new System.Drawing.PointF(7, 12));
    }

    private void PasteClipboardText()
    {
        TextValue += Clipboard.GetText(TextDataFormat.UnicodeText);
        Invalidate();
    }
}

internal sealed class ChatPayloadTextBox : TextBox
{
    private readonly List<string> _pendingFiles = [];

    public IReadOnlyList<string> PendingFiles => _pendingFiles;

    public void ClearPendingFiles() => _pendingFiles.Clear();

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) && Clipboard.ContainsFileDropList())
        {
            _pendingFiles.Clear();
            _pendingFiles.AddRange(Clipboard.GetFileDropList().Cast<string>());
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }
}
