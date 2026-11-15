using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

internal sealed record DynamicToolResult(
    bool Success,
    string Text,
    string? ImageDataUrl = null,
    int? PointerX = null,
    int? PointerY = null,
    bool RequiresVerification = false,
    string? ResultCode = null,
    IReadOnlyList<string>? ArtifactPaths = null);

public enum ChatFastPathStatus
{
    Completed,
    TargetVerificationRequired,
    ResultVerificationRequired,
    RecipientNotFound,
    RecipientAmbiguous,
    TargetMismatch,
    RecognitionUnavailable,
    UnsupportedPlatform,
    ClipboardUnavailable,
    TimedOut,
    NotReady,
    Failed,
}

public sealed record ChatFastPathResult(
    ChatFastPathStatus Status,
    string Detail,
    string? ImageDataUrl,
    int? PointerX,
    int? PointerY,
    long ElapsedMilliseconds,
    string ResultCode)
{
    // Only a result that the client could actually verify is complete.  An
    // Enter key was pressed without a readable receipt is an uncertain action,
    // not proof that the other person received the message.
    public bool Success => Status == ChatFastPathStatus.Completed;
}

/// <summary>
/// Small seam between conversation orchestration and the concrete Windows
/// sender.  Keeping this contract separate lets the session scheduler be
/// latency-tested without opening a real chat client.
/// </summary>
public interface IChatFastPathSender
{
    Task<ChatFastPathResult> SendAsync(
        string platform,
        string contact,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default);

    Task<ChatFastPathResult> ContinueVerifiedTargetAsync(
        string platform,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default);
}

public sealed record ApplicationLaunchFastPathResult(
    bool Success,
    string Detail,
    long ElapsedMilliseconds,
    string ResultCode);

/// <summary>
/// Product-layer entry point for a single explicit application-launch
/// request.  It reuses the same installed-program resolver as the agent tools,
/// but bypasses model planning, screenshots, and accessibility-tree passes.
/// </summary>
public sealed class WindowsApplicationFastPath
{
    private readonly WindowsToolBridge _tools = new();

    public async Task<ApplicationLaunchFastPathResult> OpenAsync(
        string application,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var arguments = JsonSerializer.SerializeToElement(new { application });
        try
        {
            var result = await Task.Run(
                    () => _tools.ExecuteAsync(
                        "huitian_open_application",
                        arguments,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return new ApplicationLaunchFastPathResult(
                result.Success,
                result.Text,
                stopwatch.ElapsedMilliseconds,
                result.Success ? "completed" : "not_found");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ApplicationLaunchFastPathResult(
                false,
                "打开程序超过了快速处理时限。",
                stopwatch.ElapsedMilliseconds,
                "timeout");
        }
        catch (Exception)
        {
            stopwatch.Stop();
            return new ApplicationLaunchFastPathResult(
                false,
                "这个程序暂时没有成功打开。",
                stopwatch.ElapsedMilliseconds,
                "failed");
        }
    }
}

/// <summary>
/// Product-layer entry point for the bounded WeChat/QQ path.  It deliberately
/// bypasses model planning and never exposes an accessibility-tree dump to the
/// user. Passing an empty contact explicitly targets the conversation that is
/// already open and never performs a recipient search. WeChat GPU-only clients
/// use one local-OCR pass internally; ambiguous recipients are rejected before
/// payload input.
/// </summary>
public sealed class WindowsChatFastPath : IChatFastPathSender
{
    private static readonly object DiagnosticLogGate = new();

    public Task<ChatFastPathResult> SendAsync(
        string platform,
        string contact,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            () => string.IsNullOrWhiteSpace(contact)
                ? WindowsUiAutomation.SendToVerifiedCurrentChat(platform, message, filePaths)
                : WindowsUiAutomation.SendChatPayload(platform, contact, message, filePaths),
            cancellationToken,
            BuildSensitiveValues(contact, message, filePaths));

    public Task<ChatFastPathResult> ContinueVerifiedTargetAsync(
        string platform,
        string? message,
        IReadOnlyList<string>? filePaths = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            () => WindowsUiAutomation.SendToVerifiedCurrentChat(platform, message, filePaths),
            cancellationToken,
            BuildSensitiveValues(null, message, filePaths));

    private static async Task<ChatFastPathResult> RunAsync(
        Func<DynamicToolResult> action,
        CancellationToken cancellationToken,
        IReadOnlyList<string> sensitiveValues)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(action, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var fastPathResult = new ChatFastPathResult(
                MapStatus(result),
                ToUserSafeDetail(result),
                result.ImageDataUrl,
                result.PointerX,
                result.PointerY,
                stopwatch.ElapsedMilliseconds,
                NormalizeResultCode(result));
            // Normal helper details can include recognized conversation titles.
            // The OCR layer writes its own redacted technical log when needed;
            // this product-level log keeps only status/timing for normal results.
            WriteDiagnostic(fastPathResult, null, sensitiveValues);
            return fastPathResult;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            WriteDiagnostic(new ChatFastPathResult(
                ChatFastPathStatus.TimedOut,
                "产品快速发送等待已取消或超过时限。",
                null,
                null,
                null,
                stopwatch.ElapsedMilliseconds,
                "client_timeout"), null, sensitiveValues);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            WriteDiagnostic(new ChatFastPathResult(
                ChatFastPathStatus.Failed,
                "发送通道暂时没有响应。",
                null,
                null,
                null,
                stopwatch.ElapsedMilliseconds,
                "client_exception"), $"{exception.GetType().Name}: {exception}", sensitiveValues);
            throw;
        }
    }

    internal static string ToUserSafeDetail(DynamicToolResult result) =>
        MapStatus(result) switch
        {
            ChatFastPathStatus.Completed => "发送已完成。",
            ChatFastPathStatus.ResultVerificationRequired => "发送动作已执行，但当前客户端没有提供可读取的回执。",
            ChatFastPathStatus.TargetVerificationRequired => "当前页面信息不足，无法可靠核对联系人。",
            ChatFastPathStatus.RecipientNotFound => "没有在搜索结果中识别到指定联系人。",
            ChatFastPathStatus.RecipientAmbiguous => "搜索结果中存在多个可点击的同名或相近联系人行，需要完整备注名才能唯一定位。",
            ChatFastPathStatus.TargetMismatch => "打开后的会话名称与指定联系人不一致。",
            ChatFastPathStatus.RecognitionUnavailable => "本机联系人识别组件暂时不可用。",
            ChatFastPathStatus.UnsupportedPlatform => "当前快速发送通道只适配微信和 QQ；这个平台没有可核验的发送实现，尚未发送。",
            ChatFastPathStatus.ClipboardUnavailable => "系统剪贴板正被其他程序占用。",
            ChatFastPathStatus.TimedOut => "联系人页面识别超过了快速时限。",
            ChatFastPathStatus.NotReady => "没有发现已登录并打开的聊天主窗口。",
            _ => "发送通道暂时没有响应，详细原因已写入本机诊断记录。",
        };

    internal static ChatFastPathStatus MapStatus(DynamicToolResult result) =>
        result.ResultCode switch
        {
            "target_verification_required" => ChatFastPathStatus.TargetVerificationRequired,
            "result_verification_required" => ChatFastPathStatus.ResultVerificationRequired,
            "not_found" => ChatFastPathStatus.RecipientNotFound,
            "ambiguous" => ChatFastPathStatus.RecipientAmbiguous,
            "title_mismatch" => ChatFastPathStatus.TargetMismatch,
            "ocr_unavailable" or "ocr_helper_missing" or "ocr_failed" =>
                ChatFastPathStatus.RecognitionUnavailable,
            "unsupported_platform" => ChatFastPathStatus.UnsupportedPlatform,
            "clipboard_busy" or "clipboard_restore_failed" =>
                ChatFastPathStatus.ClipboardUnavailable,
            "ocr_timeout" => ChatFastPathStatus.TimedOut,
            "not_ready" => ChatFastPathStatus.NotReady,
            _ when result.Success && result.RequiresVerification =>
                ChatFastPathStatus.ResultVerificationRequired,
            _ when result.Success => ChatFastPathStatus.Completed,
            _ => ChatFastPathStatus.Failed,
        };

    private static string NormalizeResultCode(DynamicToolResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ResultCode))
        {
            return result.ResultCode.Trim();
        }

        if (result.Success && result.RequiresVerification)
        {
            return "result_verification_required";
        }

        return result.Success ? "completed" : "failed";
    }

    private static IReadOnlyList<string> BuildSensitiveValues(
        string? contact,
        string? message,
        IReadOnlyList<string>? filePaths)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact)) values.Add(contact);
        if (!string.IsNullOrWhiteSpace(message)) values.Add(message);
        if (filePaths is not null)
        {
            foreach (var path in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                values.Add(path);
                var fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName)) values.Add(fileName);
            }
        }

        return values;
    }

    internal static string RedactDiagnostic(string? detail, IReadOnlyList<string> sensitiveValues)
    {
        var redacted = detail ?? string.Empty;
        foreach (var sensitiveValue in sensitiveValues
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(value => value.Length))
        {
            redacted = redacted.Replace(
                sensitiveValue,
                "[用户内容已隐去]",
                StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    private static void WriteDiagnostic(
        ChatFastPathResult result,
        string? technicalDetail,
        IReadOnlyList<string> sensitiveValues)
    {
        try
        {
            var root = Directory.Exists(@"D:\HuiTian")
                ? @"D:\HuiTian\Cache\AppState"
                : Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(root);
            var line = JsonSerializer.Serialize(new
            {
                time = DateTimeOffset.Now,
                status = result.Status.ToString(),
                code = result.ResultCode,
                elapsedMilliseconds = result.ElapsedMilliseconds,
                detail = RedactDiagnostic(technicalDetail, sensitiveValues),
            });
            lock (DiagnosticLogGate)
            {
                File.AppendAllText(
                    Path.Combine(root, "chat-fast-path.jsonl"),
                    line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // A diagnostic must never block or change the send result.
        }
    }
}

internal sealed class WindowsToolBridge
{
    private readonly BoundedUiRead _boundedUiRead = new();
    private readonly BoundedUiRead _boundedScreenRead = new(
        TimeSpan.FromSeconds(3),
        "截图读取超过 3 秒，本次只读，没有点击或输入。请确认目标窗口存在，改用具名控件或应用专用能力推进；不要反复截同一画面，也无需用户接管。");
    private readonly ScreenObservationDeduplicator _screenObservations = new();
    private readonly AutomationAttemptTracker _attemptTracker = new(TimeSpan.FromMinutes(2));
    private static readonly HttpClient ResearchClient = CreateResearchClient();
    private static readonly WindowsApplicationRegistry AppRegistry =
        WindowsApplicationRegistry.Default;
    private static readonly ExecutionMemoryStore ExecutionMemory =
        ExecutionMemoryStore.Default;
    private static readonly SocialConversationMemoryStore DefaultSocialMemory =
        new(new HuiTianPaths());
    private readonly SocialConversationMemoryStore _socialMemory;
    private readonly ISoulAccessibilitySnapshotSource _soulSnapshotSource;

    internal WindowsToolBridge(
        SocialConversationMemoryStore? socialMemory = null,
        ISoulAccessibilitySnapshotSource? soulSnapshotSource = null)
    {
        _socialMemory = socialMemory ?? DefaultSocialMemory;
        _soulSnapshotSource = soulSnapshotSource ?? new WindowsSoulAccessibilitySnapshotSource();
    }

    private static readonly IReadOnlyDictionary<string, string> Applications =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["记事本"] = "notepad.exe", ["notepad"] = "notepad.exe",
            ["计算器"] = "calc.exe", ["calculator"] = "calc.exe",
            ["文件资源管理器"] = "explorer.exe", ["资源管理器"] = "explorer.exe",
            ["explorer"] = "explorer.exe", ["画图"] = "mspaint.exe", ["paint"] = "mspaint.exe",
            ["浏览器"] = "msedge.exe", ["edge"] = "msedge.exe", ["Microsoft Edge"] = "msedge.exe",
            ["Chrome"] = "chrome.exe", ["谷歌浏览器"] = "chrome.exe",
            ["Word"] = "winword.exe", ["Excel"] = "excel.exe",
            ["PowerPoint"] = "powerpnt.exe", ["PPT"] = "powerpnt.exe",
            ["设置"] = "ms-settings:", ["Windows设置"] = "ms-settings:",
            ["任务管理器"] = "taskmgr.exe", ["控制面板"] = "control.exe",
            ["终端"] = "wt.exe", ["Windows终端"] = "wt.exe",
            ["命令提示符"] = "cmd.exe", ["PowerShell"] = "powershell.exe",
            ["截图工具"] = "snippingtool.exe",
            ["微信"] = "Weixin.exe", ["WeChat"] = "Weixin.exe", ["Weixin"] = "Weixin.exe",
            ["QQ"] = "QQ.exe", ["腾讯QQ"] = "QQ.exe",
        };

    private static readonly string[] SensitiveControlWords =
    [
        "支付", "付款", "购买", "转账", "汇款", "收款", "提交订单", "确认订单",
        // A site's login/register navigation button is safe to open. The
        // pre-action UI observer interrupts on a real credential, OTP or QR
        // wall; treating the header button as approval-worthy stalled normal
        // browsing before the user was actually needed.
        "验证码", "更改密码", "保存密码",
    ];

    private static readonly string[] SensitiveInputWords =
    [
        "密码", "验证码", "身份证", "银行卡", "信用卡", "手机号", "收款", "支付",
        "账号", "密钥", "令牌",
    ];

    public bool RequiresApproval(string toolName, JsonElement arguments)
    {
        if (toolName == "huitian_click_control")
        {
            var target = ReadString(arguments, "elementName") ?? string.Empty;
            return SensitiveControlWords.Any(word => target.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        if (toolName == "huitian_type_text")
        {
            var target = ReadString(arguments, "elementName") ?? string.Empty;
            return SensitiveInputWords.Any(word => target.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        if (toolName == "huitian_click_point")
        {
            var target = ReadString(arguments, "elementDescription") ?? string.Empty;
            return SensitiveControlWords.Any(word => target.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        if (toolName == "huitian_type_active")
        {
            var target = ReadString(arguments, "fieldDescription") ?? string.Empty;
            return SensitiveInputWords.Any(word => target.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        if (toolName == "huitian_press_keys")
        {
            var purpose = ReadString(arguments, "purpose") ?? string.Empty;
            return SensitiveControlWords.Any(word => purpose.Contains(word, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    public string Describe(string toolName, JsonElement arguments)
    {
        if (toolName == "huitian_click_control")
        {
            return $"回天准备点击可能产生重要结果的控件：{ReadString(arguments, "elementName") ?? "指定控件"}";
        }

        if (toolName == "huitian_type_text")
        {
            return $"回天准备在敏感输入框中填写内容：{ReadString(arguments, "elementName") ?? "指定输入框"}";
        }

        if (toolName == "huitian_click_point")
        {
            return $"回天准备点击可能产生重要结果的位置：{ReadString(arguments, "elementDescription") ?? "指定位置"}";
        }

        if (toolName == "huitian_type_active")
        {
            return $"回天准备在敏感输入区域填写内容：{ReadString(arguments, "fieldDescription") ?? "当前输入区域"}";
        }

        if (toolName == "huitian_press_keys")
        {
            return $"回天准备通过键盘确认重要操作：{ReadString(arguments, "purpose") ?? "当前操作"}";
        }

        return "回天准备执行一项可能影响电脑或账号的重要操作。";
    }

    public async Task<DynamicToolResult> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (toolName == "huitian_inspect_windows")
        {
            var inspection = await _boundedUiRead.ReadAsync(() => InspectWindows(arguments), cancellationToken).ConfigureAwait(false);
            inspection = await SoulPageCapabilityAnalyzer.AppendIfNamedAsync(
                ReadString(arguments, "windowTitle"), inspection,
                _soulSnapshotSource, cancellationToken).ConfigureAwait(false);
            RecordExecutionMemory(toolName, arguments, inspection, stopwatch.ElapsedMilliseconds);
            return inspection;
        }
        if (toolName == "huitian_capture_screen")
        {
            var captured = await _boundedScreenRead.ReadAsync(
                () => CaptureScreen(arguments), cancellationToken).ConfigureAwait(false);
            var filtered = _screenObservations.Filter(ReadString(arguments, "windowTitle"), captured);
            var requestedWindow = ReadString(arguments, "windowTitle");
            if (filtered.Success &&
                requestedWindow is not null &&
                (requestedWindow.Contains("抖音", StringComparison.OrdinalIgnoreCase) ||
                 requestedWindow.Contains("douyin", StringComparison.OrdinalIgnoreCase)))
            {
                var pageState = await _boundedUiRead.ReadAsync(
                    () => WindowsUiAutomation.InspectDouyinDiscoveryState(requestedWindow),
                    cancellationToken).ConfigureAwait(false);
                if (pageState.Success)
                    filtered = filtered with { Text = filtered.Text + "\n" + pageState.Text };
            }
            filtered = await SoulPageCapabilityAnalyzer.AppendIfNamedAsync(
                requestedWindow, filtered, _soulSnapshotSource,
                cancellationToken).ConfigureAwait(false);
            RecordExecutionMemory(toolName, arguments, filtered, stopwatch.ElapsedMilliseconds);
            return filtered;
        }
        if (toolName == "huitian_search_web")
        {
            var searchResult = await SearchWebAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            RecordExecutionMemory(toolName, arguments, searchResult, stopwatch.ElapsedMilliseconds);
            return searchResult;
        }
        if (toolName == "huitian_screen_social_candidate")
        {
            var screening = await ScreenSocialCandidateAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            RecordExecutionMemory(toolName, arguments, screening, stopwatch.ElapsedMilliseconds);
            return screening;
        }
        if (toolName == "huitian_read_social_candidates")
        {
            var remembered = await ReadSocialCandidatesAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            RecordExecutionMemory(toolName, arguments, remembered, stopwatch.ElapsedMilliseconds);
            return remembered;
        }

        var guarded = IsGuardedAutomationAction(toolName);
        if (IsPotentialDesktopAction(toolName)) _screenObservations.MarkDesktopAction();
        var attemptKey = guarded ? BuildAttemptKey(toolName, arguments) : string.Empty;
        if (guarded && _attemptTracker.ShouldBlock(attemptKey))
        {
            var blocked = new DynamicToolResult(
                false,
                "同一操作路径已经连续两次没有达到目标，回天不会继续盲目重复。请先重新观察现状，并改用具名控件、键盘路径、局部画面或其他不同策略。 ");
            stopwatch.Stop();
            RecordExecutionMemory(toolName, arguments, blocked, stopwatch.ElapsedMilliseconds);
            return blocked;
        }

        var result = toolName switch
        {
            "huitian_respond" => Respond(arguments),
            "huitian_search_files" => SearchFiles(arguments),
            "huitian_read_text_file" => ReadTextFile(arguments),
            "huitian_read_document" => ReadDocument(arguments),
            "huitian_write_text_file" => WriteTextFile(arguments),
            "huitian_create_document" => CreateDocument(arguments),
            "huitian_create_spreadsheet" => CreateSpreadsheet(arguments),
            "huitian_modify_file" => ModifyFile(arguments),
            "huitian_open_application" => OpenApplication(arguments),
            "huitian_open_browser" => OpenBrowser(arguments),
            "huitian_find_media" => FindMedia(arguments),
            "huitian_move_file" => MoveFile(arguments),
            "huitian_inspect_windows" => InspectWindows(arguments),
            "huitian_activate_window" => ActivateWindow(arguments),
            "huitian_click_control" => ClickControl(arguments),
            "huitian_type_text" => TypeText(arguments),
            "huitian_scroll_window" => ScrollWindow(arguments),
            "huitian_capture_screen" => CaptureScreen(arguments),
            "huitian_click_point" => ClickPoint(arguments),
            "huitian_type_active" => TypeActive(arguments),
            "huitian_press_keys" => PressKeys(arguments),
            "huitian_send_chat_message" => SendChatMessage(arguments),
            "huitian_send_chat_files" => SendChatFiles(arguments),
            "huitian_create_presentation" => CreatePresentation(arguments),
            _ => new DynamicToolResult(false, "暂不支持这项本机操作。"),
        };

        if (guarded)
        {
            if (result.Success && !result.RequiresVerification)
            {
                _attemptTracker.RecordSuccess(attemptKey);
            }
            else
            {
                var failures = _attemptTracker.RecordFailure(attemptKey);
                if (failures >= 2)
                {
                    result = result with
                    {
                        Text = result.Text.TrimEnd() +
                               " 同一路径已经两次没有得到可验证结果，下一步必须改用不同策略，不能再原样重试。",
                    };
                }
            }
        }

        stopwatch.Stop();
        RecordExecutionMemory(toolName, arguments, result, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private static DynamicToolResult Respond(JsonElement arguments)
    {
        var text = ReadString(arguments, "text")?.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? new DynamicToolResult(false, "没有提供要回复给用户的内容。", ResultCode: "missing_response")
            : new DynamicToolResult(
                true,
                "回复内容已交给产品界面显示；不要重复生成相同内容，直接结束本轮。",
                ResultCode: "response_delivered");
    }

    private async Task<DynamicToolResult> ScreenSocialCandidateAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var platform = ReadString(arguments, "platform");
        var stableId = ReadString(arguments, "stableId");
        var displayName = ReadString(arguments, "displayName");
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(stableId))
            return new DynamicToolResult(false, "须提供平台和从资料页核验的稳定联系人 ID。",
                ResultCode: "social_identity_missing");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 200)
            return new DynamicToolResult(false, "须提供资料页可见昵称，候选记忆只保存 ID 和昵称。",
                ResultCode: "social_display_name_missing");

        var requiredFlags = new[]
        {
            "identityVerified", "ageVerified", "freeToMessage", "newPerson",
            "knownFamilyOrRelationship", "messagingAvailable",
        };
        if (requiredFlags.Any(flag =>
                arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty(flag, out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            return new DynamicToolResult(false, "候选人的身份、成年、费用、关系和可聊天状态都必须逐项核验；未知时填 false。",
                ResultCode: "social_evidence_incomplete");

        SocialContactKey key;
        try { key = SocialContactKey.Create(platform, stableId); }
        catch (ArgumentException)
        {
            return new DynamicToolResult(false, "稳定联系人 ID 格式无效。",
                ResultCode: "social_identity_invalid");
        }

        SocialContactMemory? prior;
        try
        {
            prior = await _socialMemory.ReadContactAsync(key, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new DynamicToolResult(false, "本地候选记忆暂时不可读取，尚未筛选或联系此人。",
                ResultCode: "social_memory_unavailable");
        }
        var relationship = ReadBoolean(arguments, "knownFamilyOrRelationship", false)
            ? SocialRelationshipStatus.FamilyOrProtected
            : ReadBoolean(arguments, "newPerson", false)
                ? SocialRelationshipStatus.NewPerson
                : SocialRelationshipStatus.Unknown;
        var age = ReadBoolean(arguments, "knownUnderage", false)
            ? SocialAdultStatus.Minor
            : ReadBoolean(arguments, "ageVerified", false)
                ? SocialAdultStatus.VerifiedAdult
                : SocialAdultStatus.Unknown;
        var cost = ReadBoolean(arguments, "paidChat", false)
            ? SocialMessagingCost.Paid
            : ReadBoolean(arguments, "freeToMessage", false)
                ? SocialMessagingCost.Free
                : SocialMessagingCost.Unknown;
        var evidence = new SocialCandidateEvidence(
            key,
            displayName,
            ReadBoolean(arguments, "identityVerified", false),
            age,
            relationship,
            cost,
            ReadBoolean(arguments, "messagingAvailable", false));
        var decision = SocialCandidatePolicy.EvaluateForNewOutreach(
            evidence,
            alreadyContacted: prior is { RecentTurns.Count: > 0 });
        if (decision.EligibleForDraft)
        {
            try
            {
                await _socialMemory.RememberEligibleCandidateAsync(key, displayName,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return new DynamicToolResult(false, "候选人符合筛选条件，但本地记忆保存失败；尚未联系此人。",
                    ResultCode: "social_memory_unavailable");
            }
        }
        var code = decision.EligibleForDraft
            ? "social_candidate_eligible_for_draft"
            : "social_candidate_skip";
        var detail = JsonSerializer.Serialize(new
        {
            decision = decision.EligibleForDraft ? "eligible_for_draft" : "skip",
            eligibleForDraft = decision.EligibleForDraft,
            candidateStored = decision.EligibleForDraft,
            reasons = decision.Reasons.Select(reason => reason.ToString()).ToArray(),
            note = "仅作候选筛选，不会关注、私信或付费；须以平台当前可见证据为准。",
        });
        return new DynamicToolResult(true, detail, ResultCode: code);
    }

    private async Task<DynamicToolResult> ReadSocialCandidatesAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var platform = ReadString(arguments, "platform");
        var stableId = ReadString(arguments, "stableId");
        if (!string.IsNullOrWhiteSpace(stableId) && string.IsNullOrWhiteSpace(platform))
            return new DynamicToolResult(false, "按稳定 ID 查询时还须提供平台，避免跨平台认错人。",
                ResultCode: "social_platform_required");
        try
        {
            if (!string.IsNullOrWhiteSpace(stableId))
            {
                var key = SocialContactKey.Create(platform, stableId);
                var candidate = await _socialMemory.ReadCandidateAsync(key, cancellationToken)
                    .ConfigureAwait(false);
                return new DynamicToolResult(true, JsonSerializer.Serialize(new
                {
                    found = candidate is not null,
                    candidate = candidate is null ? null : CandidateSummary(candidate),
                    note = "候选记忆不证明已经发送消息。",
                }), ResultCode: candidate is null ? "social_candidate_not_found" : "social_candidate_found");
            }

            var candidates = await _socialMemory.ListCandidatesAsync(platform,
                    ReadInt32(arguments, "limit", 20), cancellationToken)
                .ConfigureAwait(false);
            return new DynamicToolResult(true, JsonSerializer.Serialize(new
            {
                candidates = candidates.Select(CandidateSummary).ToArray(),
                note = "仅列出平台 ID、昵称和已核验会话状态，不含资料正文或聊天内容。",
            }), ResultCode: "social_candidate_listed");
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException)
        {
            return new DynamicToolResult(false, "候选人的平台或稳定 ID 无效。",
                ResultCode: "social_identity_invalid");
        }
        catch (Exception)
        {
            return new DynamicToolResult(false, "本地候选记忆暂时不可读取。",
                ResultCode: "social_memory_unavailable");
        }
    }

    private static object CandidateSummary(SocialCandidateMemory candidate) => new
    {
        platform = candidate.Contact.Platform,
        stableId = candidate.Contact.StableContactId,
        displayName = candidate.DisplayName,
        stage = candidate.HasVerifiedTurn ? "verified_turns_recorded" : "candidate_only",
        hasVerifiedOutgoing = candidate.HasVerifiedOutgoing,
        lastScreenedAt = candidate.LastScreenedAt,
    };

    private static void RecordExecutionMemory(
        string toolName,
        JsonElement arguments,
        DynamicToolResult result,
        long elapsedMilliseconds)
    {
        var app = ReadString(arguments, "application") ??
                  ReadString(arguments, "windowTitle") ??
                  ReadString(arguments, "platform") ??
                  ApplicationCapability.Any;
        var channel = toolName switch
        {
            "huitian_search_files" or "huitian_read_text_file" or
            "huitian_read_document" or "huitian_write_text_file" or
            "huitian_create_document" or "huitian_create_spreadsheet" or
            "huitian_modify_file" or
            "huitian_move_file" or "huitian_search_web" or
            "huitian_screen_social_candidate" or "huitian_read_social_candidates" or
            "huitian_open_browser" or "huitian_find_media" => ComputerTaskChannel.Api,
            "huitian_open_application" => ComputerTaskChannel.Native,
            "huitian_send_chat_message" or "huitian_send_chat_files" or
            "huitian_create_document" or "huitian_create_spreadsheet" or
            "huitian_modify_file" or "huitian_create_presentation" => ComputerTaskChannel.AppAdapter,
            "huitian_press_keys" => ComputerTaskChannel.Shortcut,
            "huitian_capture_screen" or "huitian_click_point" =>
                ComputerTaskChannel.Visual,
            _ => ComputerTaskChannel.DomOrUia,
        };
        var operation = toolName switch
        {
            "huitian_search_files" => "search",
            "huitian_read_text_file" or "huitian_read_document" => "read",
            "huitian_write_text_file" => "write",
            "huitian_create_document" => "create_document",
            "huitian_create_spreadsheet" => "create_spreadsheet",
            "huitian_modify_file" => "modify_file",
            "huitian_move_file" => "move",
            "huitian_search_web" => "research",
            "huitian_find_media" => "find_media",
            "huitian_open_browser" or "huitian_open_application" => "open",
            "huitian_inspect_windows" or "huitian_capture_screen" => "observe",
            "huitian_activate_window" => "activate",
            "huitian_click_control" or "huitian_click_point" => "click",
            "huitian_type_text" or "huitian_type_active" => "type",
            "huitian_scroll_window" => "scroll",
            "huitian_press_keys" => "shortcut",
            "huitian_send_chat_message" or "huitian_send_chat_files" => "send",
            "huitian_screen_social_candidate" => "screen_candidate",
            "huitian_read_social_candidates" => "read_candidates",
            "huitian_create_presentation" => "create_presentation",
            _ => toolName,
        };
        var verified = result.Success && !result.RequiresVerification;
        ExecutionMemory.Record(
            app,
            operation,
            channel,
            toolName + ":" + channel,
            result.Success,
            verified,
            elapsedMilliseconds,
            result.ResultCode ?? (verified ? "verified-result" : "unverified"));
        if (verified || !result.Success)
        {
            _ = ExecutionMemory.SaveAsync();
        }
    }

    private static bool IsGuardedAutomationAction(string toolName) => toolName is
        "huitian_click_control" or "huitian_type_text" or
        "huitian_click_point" or "huitian_type_active" or
        "huitian_press_keys" or "huitian_send_chat_message" or
        "huitian_send_chat_files";

    private static bool IsPotentialDesktopAction(string toolName) => toolName is
        "huitian_open_application" or "huitian_open_browser" or
        "huitian_find_media" or "huitian_activate_window" or
        "huitian_click_control" or "huitian_click_point" or
        "huitian_scroll_window" or "huitian_type_text" or
        "huitian_type_active" or "huitian_press_keys" or
        "huitian_send_chat_message" or "huitian_send_chat_files";

    internal static string BuildAttemptKey(string toolName, JsonElement arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, arguments);
        }

        return toolName + ":" + Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteRawValue(value.GetRawText());
                break;
        }
    }

    private static DynamicToolResult SearchFiles(JsonElement arguments)
    {
        var query = ReadString(arguments, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new DynamicToolResult(false, "缺少文件名关键词。 ");
        }

        var root = ResolveSearchRoot(ReadString(arguments, "root")?.Trim());
        if (!Directory.Exists(root))
        {
            return new DynamicToolResult(false, "指定的搜索位置不存在。 ");
        }

        var results = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var visitedDirectories = 0;
        const int directoryLimit = 3000;
        const int resultLimit = 30;
        while (pending.Count > 0 && visitedDirectories < directoryLimit && results.Count < resultLimit)
        {
            var current = pending.Dequeue();
            visitedDirectories++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                        if (results.Count >= resultLimit) break;
                    }
                }

                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    try
                    {
                        if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Enqueue(directory);
                        }
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                    {
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return results.Count == 0
            ? new DynamicToolResult(
                true,
                $"在 {root} 内没有找到包含“{query}”的文件名。 ",
                ResultCode: "no_results")
            : new DynamicToolResult(
                true,
                "找到以下文件：\n" + string.Join("\n", results),
                ResultCode: "research_results");
    }

    private static DynamicToolResult ReadTextFile(JsonElement arguments)
    {
        var pathText = ReadString(arguments, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(pathText) || !Path.IsPathFullyQualified(pathText))
        {
            return new DynamicToolResult(false, "文件路径必须是完整路径。 ");
        }

        var path = Path.GetFullPath(pathText);
        if (!File.Exists(path))
        {
            return new DynamicToolResult(false, "要读取的文件不存在。 ");
        }

        var info = new FileInfo(path);
        if (info.Length > 2 * 1024 * 1024)
        {
            return new DynamicToolResult(false, "这个文件超过 2 MB，请先缩小范围或分段处理。 ");
        }

        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".json", ".xml", ".csv", ".tsv", ".log", ".ini",
            ".yaml", ".yml", ".cs", ".xaml", ".js", ".ts", ".html", ".css",
            ".py", ".ps1", ".bat", ".cmd", ".toml", ".sql",
        };
        if (!allowedExtensions.Contains(info.Extension))
        {
            return new DynamicToolResult(false, "第一版只能直接读取常见文字、表格文本和代码文件。 ");
        }

        try
        {
            return new DynamicToolResult(true, $"文件：{path}\n\n{File.ReadAllText(path, Encoding.UTF8)}");
        }
        catch (DecoderFallbackException)
        {
            return new DynamicToolResult(false, "文件文字编码暂时无法识别。 ");
        }
    }

    private static DynamicToolResult WriteTextFile(JsonElement arguments)
    {
        var pathText = ReadString(arguments, "path")?.Trim();
        var content = ReadString(arguments, "content");
        if (string.IsNullOrWhiteSpace(pathText) || content is null || !Path.IsPathFullyQualified(pathText))
        {
            return new DynamicToolResult(false, "请提供完整文件路径和写入内容。 ");
        }

        var path = Path.GetFullPath(pathText);
        if (IsProtectedProductOrSystemPath(path))
        {
            return new DynamicToolResult(false, "为保护系统和回天核心，不能写入该位置。 ");
        }

        if (File.Exists(path))
        {
            return new DynamicToolResult(false, "目标位置已有同名文件，回天没有覆盖它。 ");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));

            // File creation is an atomic product capability: do the cheap,
            // deterministic verification here instead of asking the 27B model
            // to spend another generation deciding to call the read tool.
            var verifiedContent = File.ReadAllText(path, Encoding.UTF8);
            if (!string.Equals(content, verifiedContent, StringComparison.Ordinal))
            {
                return new DynamicToolResult(
                    false,
                    $"文件已写入但重新读取的内容不一致：{path}",
                    ResultCode: "verification_failed");
            }

            return new DynamicToolResult(
                true,
                $"文件已创建并重新读取核验：{path}（{content.Length} 个字符）",
                ResultCode: "completed",
                ArtifactPaths: [path]);
        }
        catch (IOException exception)
        {
            return new DynamicToolResult(
                false,
                $"文件创建或重新读取核验失败：{exception.Message}",
                ResultCode: "io_failed");
        }
        catch (UnauthorizedAccessException exception)
        {
            return new DynamicToolResult(
                false,
                $"没有权限在该位置创建并核验文件：{exception.Message}",
                ResultCode: "access_denied");
        }
    }

    private static DynamicToolResult ReadDocument(JsonElement arguments)
    {
        var pathText = ReadString(arguments, "path")?.Trim();
        if (string.IsNullOrWhiteSpace(pathText) || !Path.IsPathFullyQualified(pathText))
        {
            return new DynamicToolResult(false, "文件路径必须是完整路径。 ");
        }

        var path = Path.GetFullPath(pathText);
        if (!File.Exists(path))
        {
            return new DynamicToolResult(false, "要读取的文件不存在。 ");
        }

        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".docx" or ".doc" => ReadWordDocument(path),
                ".xlsx" or ".xls" => ReadExcelWorkbook(path),
                ".pptx" => OpenXmlPresentationEditor.ReadText(path,
                    arguments.TryGetProperty("startPage", out var startPage) ? startPage.GetInt32() : 1,
                    arguments.TryGetProperty("maxPages", out var maxPages) ? maxPages.GetInt32() : 5,
                    arguments.TryGetProperty("maxChars", out var maxChars) ? maxChars.GetInt32() : 6000),
                ".ppt" => ReadPowerPointPresentation(path),
                _ => ReadTextFile(arguments),
            };
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"暂时无法读取这个 Office 文件：{exception.Message}");
        }
    }

    private static DynamicToolResult CreateDocument(JsonElement arguments)
    {
        var title = ReadString(arguments, "title")?.Trim();
        var content = ReadString(arguments, "content");
        var requestedFormat = (ReadString(arguments, "format") ?? "docx")
            .Trim()
            .TrimStart('.')
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(content))
        {
            return new DynamicToolResult(false, "正式文档需要标题和完整正文。", ResultCode: "missing_content");
        }

        if (content.Length > 500_000)
        {
            return new DynamicToolResult(false, "文档正文超过本次可安全生成的范围，请分卷创建。", ResultCode: "content_too_large");
        }

        if (requestedFormat is not ("docx" or "pdf"))
        {
            return new DynamicToolResult(false, "正式文档当前支持 DOCX 或 PDF 格式。", ResultCode: "unsupported_format");
        }

        var outputDirectory = @"D:\HuiTian\UserContent\文档";
        var requestedName = ReadString(arguments, "fileName")?.Trim();
        var baseName = string.IsNullOrWhiteSpace(requestedName)
            ? title
            : Path.GetFileNameWithoutExtension(requestedName);
        var safeName = string.Join("_", baseName
            .Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "回天文档";
        Directory.CreateDirectory(outputDirectory);
        var suffix = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var docxPath = Path.Combine(outputDirectory, $"{safeName}-{suffix}.docx");
        var landscape = string.Equals(
            ReadString(arguments, "orientation"),
            "landscape",
            StringComparison.OrdinalIgnoreCase);

        try
        {
            var writeResult = OpenXmlDocumentWriter.Create(
                docxPath,
                title,
                content,
                landscape);
            if (!writeResult.Success)
            {
                return new DynamicToolResult(
                    false,
                    writeResult.Detail,
                    ResultCode: "verification_failed",
                    ArtifactPaths: [docxPath]);
            }

            if (requestedFormat == "docx")
            {
                return new DynamicToolResult(
                    true,
                    $"正式文档已创建并重新打开核验：{docxPath}\n版式：A4、标准页边距、统一标题与正文样式。",
                    ResultCode: "artifact_verified",
                    ArtifactPaths: [docxPath]);
            }

            var pdfPath = Path.ChangeExtension(docxPath, ".pdf");
            var exportResult = ExportDocumentToPdf(docxPath, pdfPath);
            return exportResult.Success
                ? new DynamicToolResult(
                    true,
                    $"可打印 PDF 已创建并核验：{pdfPath}\n可编辑源文档：{docxPath}",
                    ResultCode: "artifact_verified",
                    ArtifactPaths: [pdfPath, docxPath])
                : new DynamicToolResult(
                    false,
                    exportResult.Detail + $" 可编辑 DOCX 已保留：{docxPath}",
                    ResultCode: "pdf_export_failed",
                    ArtifactPaths: [docxPath]);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException)
        {
            return new DynamicToolResult(
                false,
                $"正式文档没有创建成功：{exception.Message}",
                ResultCode: "document_creation_failed");
        }
    }

    private static (bool Success, string Detail) ExportDocumentToPdf(
        string docxPath,
        string pdfPath)
    {
        var wordType = Type.GetTypeFromProgID("Word.Application");
        if (wordType is null)
        {
            return (false, "这台电脑没有可用于导出 PDF 的 Word。 ");
        }

        dynamic? application = null;
        dynamic? document = null;
        try
        {
            application = Activator.CreateInstance(wordType)!;
            application.Visible = false;
            application.DisplayAlerts = 0;
            document = application.Documents.Open(docxPath, ReadOnly: true, Visible: false);
            document.ExportAsFixedFormat(pdfPath, 17);
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length < 1_000)
            {
                return (false, "Word 没有生成可用的 PDF 文件。 ");
            }

            using var stream = File.OpenRead(pdfPath);
            Span<byte> signature = stackalloc byte[5];
            if (stream.Read(signature) != signature.Length ||
                !signature.SequenceEqual("%PDF-"u8))
            {
                return (false, "生成文件没有通过 PDF 格式核验。 ");
            }

            return (true, "PDF 已导出并核验。 ");
        }
        catch (Exception exception)
        {
            return (false, $"PDF 导出失败：{exception.Message}");
        }
        finally
        {
            try
            {
                document?.Close(false);
            }
            catch
            {
            }

            try
            {
                application?.Quit(false);
            }
            catch
            {
            }
        }
    }

    private static DynamicToolResult ReadWordDocument(string path)
    {
        var type = Type.GetTypeFromProgID("Word.Application");
        if (type is null)
        {
            return new DynamicToolResult(false, "这台电脑没有检测到可用的 Word。 ");
        }

        dynamic? application = null;
        dynamic? document = null;
        try
        {
            application = Activator.CreateInstance(type)!;
            application.Visible = false;
            document = application.Documents.Open(path, ReadOnly: true, Visible: false);
            string text = document.Content.Text;
            return new DynamicToolResult(true, $"文件：{path}\n\n{LimitText(text)}");
        }
        finally
        {
            if (document is not null) document.Close(false);
            if (application is not null) application.Quit(false);
        }
    }

    private static DynamicToolResult ReadExcelWorkbook(string path)
    {
        var type = Type.GetTypeFromProgID("Excel.Application");
        if (type is null)
        {
            return new DynamicToolResult(false, "这台电脑没有检测到可用的 Excel。 ");
        }

        dynamic? application = null;
        dynamic? workbook = null;
        try
        {
            application = Activator.CreateInstance(type)!;
            application.Visible = false;
            application.DisplayAlerts = false;
            workbook = application.Workbooks.Open(path, ReadOnly: true);
            var builder = new StringBuilder($"文件：{path}\n");
            foreach (dynamic sheet in workbook.Worksheets)
            {
                builder.AppendLine($"\n工作表：{sheet.Name}");
                object? value = sheet.UsedRange.Value2;
                if (value is Array values && values.Rank == 2)
                {
                    var rowStart = values.GetLowerBound(0);
                    var rowEnd = Math.Min(values.GetUpperBound(0), rowStart + 199);
                    var columnStart = values.GetLowerBound(1);
                    var columnEnd = Math.Min(values.GetUpperBound(1), columnStart + 39);
                    for (var row = rowStart; row <= rowEnd; row++)
                    {
                        var cells = new List<string>();
                        for (var column = columnStart; column <= columnEnd; column++)
                        {
                            cells.Add(Convert.ToString(values.GetValue(row, column)) ?? string.Empty);
                        }

                        builder.AppendLine(string.Join("\t", cells));
                        if (builder.Length >= 60_000) break;
                    }
                }
                else if (value is not null)
                {
                    builder.AppendLine(Convert.ToString(value));
                }

                if (builder.Length >= 60_000) break;
            }

            return new DynamicToolResult(true, LimitText(builder.ToString()));
        }
        finally
        {
            if (workbook is not null) workbook.Close(false);
            if (application is not null) application.Quit();
        }
    }

    private static DynamicToolResult ReadPowerPointPresentation(string path)
    {
        var type = Type.GetTypeFromProgID("PowerPoint.Application");
        if (type is null)
        {
            return new DynamicToolResult(false, "这台电脑没有检测到可用的 PowerPoint。 ");
        }

        dynamic? application = null;
        dynamic? presentation = null;
        try
        {
            application = Activator.CreateInstance(type)!;
            presentation = application.Presentations.Open(path, ReadOnly: true, WithWindow: false);
            var builder = new StringBuilder($"文件：{path}\n");
            foreach (dynamic slide in presentation.Slides)
            {
                builder.AppendLine($"\n第 {slide.SlideIndex} 页");
                foreach (dynamic shape in slide.Shapes)
                {
                    if (shape.HasTextFrame == -1 && shape.TextFrame.HasText == -1)
                    {
                        builder.AppendLine(Convert.ToString(shape.TextFrame.TextRange.Text));
                    }
                }

                if (builder.Length >= 60_000) break;
            }

            return new DynamicToolResult(true, LimitText(builder.ToString()));
        }
        finally
        {
            if (presentation is not null) presentation.Close();
            if (application is not null) application.Quit();
        }
    }

    private static string LimitText(string value) => value.Length <= 60_000
        ? value
        : value[..60_000] + "\n（内容较长，已读取前 60000 个字符。）";

    private static DynamicToolResult FindMedia(JsonElement arguments)
    {
        var query = (ReadString(arguments, "query") ?? ReadString(arguments, "title"))?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new DynamicToolResult(false, "没有提供要观看或收听的内容名称。", ResultCode: "missing_media_query");
        }

        var kind = string.Equals(ReadString(arguments, "kind"), "audio", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ReadString(arguments, "kind"), "音乐", StringComparison.OrdinalIgnoreCase)
            ? MediaConsumptionKind.Audio
            : MediaConsumptionKind.Video;
        var operation = kind == MediaConsumptionKind.Audio ? "收听" : "观看";
        try
        {
            var intent = new MediaConsumptionIntent(
                kind,
                query,
                ExplicitPlaybackRequested: true,
                $"查找并打开“{query}”的正版{operation}来源");
            var uri = WindowsMediaConsumptionFastPath.BuildLicensedSourceSearchUri(intent);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return new DynamicToolResult(
                true,
                $"已打开“{query}”的正版{operation}来源搜索。这个结果只是中间状态；请继续观察浏览器，" +
                "打开标题匹配的官方页面并确认播放器出现。登录、会员或付费属于真实平台限制，不能绕过。",
                RequiresVerification: true,
                ResultCode: "media_search_opened");
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(
                false,
                $"正版媒体来源没有打开：{exception.Message}",
                ResultCode: "media_search_failed");
        }
    }

    private static DynamicToolResult OpenApplication(JsonElement arguments)
    {
        var requested = ReadString(arguments, "application")?.Trim();
        if (string.IsNullOrWhiteSpace(requested))
        {
            return new DynamicToolResult(false, "没有指定要打开的程序。 ");
        }

        var existingWindow = WindowsUiAutomation.Activate(requested);
        if (existingWindow.Success)
        {
            return new DynamicToolResult(true, $"已切换到现有的{requested}窗口，没有重复打开程序。 ");
        }

        if (Path.IsPathFullyQualified(requested) && File.Exists(requested))
        {
            Process.Start(new ProcessStartInfo(Path.GetFullPath(requested)) { UseShellExecute = true });
            return new DynamicToolResult(true, $"已打开：{requested}");
        }

        if (Applications.TryGetValue(requested, out var executable))
        {
            try
            {
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                return new DynamicToolResult(true, $"已打开{requested}。 ");
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        if (AppRegistry.TryResolve(requested, out var registered))
        {
            try
            {
                Process.Start(new ProcessStartInfo(registered.LaunchTarget)
                {
                    UseShellExecute = true,
                });
                return new DynamicToolResult(
                    true,
                    $"已打开{registered.DisplayName}。 ");
            }
            catch (Exception exception) when (exception is
                System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // The cache may contain a stale target. Fall through to the
                // live Start Menu lookup and let background refresh replace it.
            }
        }

        var shortcut = FindStartMenuShortcut(requested);
        if (shortcut is null)
        {
            return new DynamicToolResult(false, $"没有在已安装程序中找到“{requested}”。可以告诉我更完整的程序名称。 ");
        }

        Process.Start(new ProcessStartInfo(shortcut) { UseShellExecute = true });
        AppRegistry.RegisterOrUpdate(new WindowsApplicationRecord(
            "shortcut:" + shortcut.ToLowerInvariant(),
            Path.GetFileNameWithoutExtension(shortcut),
            shortcut,
            ApplicationLaunchKind.Shortcut,
            null,
            "task-discovery",
            [Path.GetFileNameWithoutExtension(shortcut), requested],
            DateTimeOffset.Now));
        _ = AppRegistry.SaveAsync();
        return new DynamicToolResult(true, $"已打开{Path.GetFileNameWithoutExtension(shortcut)}。 ");
    }

    private static DynamicToolResult OpenBrowser(JsonElement arguments)
    {
        var requested = ReadString(arguments, "url")?.Trim();
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return new DynamicToolResult(false, "网址格式无效，仅支持 http 或 https 网页。 ");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return new DynamicToolResult(true, $"已在浏览器打开 {uri.AbsoluteUri}。 ");
    }

    private static async Task<DynamicToolResult> SearchWebAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = ReadString(arguments, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return new DynamicToolResult(false, "没有提供联网搜索关键词。 ");
        }

        var uri = "https://cn.bing.com/search?q=" + Uri.EscapeDataString(query) +
                  "&count=8&setlang=zh-Hans";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await ResearchClient.GetAsync(uri, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(timeout.Token)
                .ConfigureAwait(false);
            var results = ExtractSearchResults(html, maximumResults: 6);
            if (results.Count > 0)
            {
                return new DynamicToolResult(
                    true,
                    $"已直接读取“{query}”的公开搜索结果，无需观察浏览器画面：\n" +
                    string.Join("\n\n", results),
                    ResultCode: "research_results");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through to the visible-browser fallback. A slow public
            // search provider must not hold the whole agent turn indefinitely.
        }
        catch (HttpRequestException)
        {
            // The browser fallback remains available on networks that block
            // direct extraction while still allowing normal browsing.
        }

        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return new DynamicToolResult(
            true,
            $"公开搜索结果未能在 8 秒内直接读取，已在浏览器打开“{query}”。只观察这个结果窗口一次并提炼可见摘要，不要重复截图或重复搜索。 ",
            RequiresVerification: true,
            ResultCode: "browser_observation_required");
    }

    internal static IReadOnlyList<string> ExtractSearchResults(
        string html,
        int maximumResults = 6)
    {
        if (string.IsNullOrWhiteSpace(html) || maximumResults <= 0)
        {
            return [];
        }

        var results = new List<string>();
        var blocks = Regex.Matches(
            html,
            """<li[^>]+class=["']?[^"'>]*\bb_algo\b[^"'>]*["']?[^>]*>(?<body>.*?)</li>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match block in blocks)
        {
            var body = block.Groups["body"].Value;
            var titleMatch = Regex.Match(
                body,
                """<h2[^>]*>.*?<a[^>]+href=["'](?<url>https?://[^"']+)["'][^>]*>(?<title>.*?)</a>.*?</h2>""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!titleMatch.Success)
            {
                continue;
            }

            var snippetMatch = Regex.Match(
                body,
                @"<p[^>]*>(?<snippet>.*?)</p>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var title = CleanHtmlText(titleMatch.Groups["title"].Value);
            var snippet = CleanHtmlText(snippetMatch.Groups["snippet"].Value);
            var url = WebUtility.HtmlDecode(titleMatch.Groups["url"].Value).Trim();
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var result = $"- {title}\n  {url}";
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                result += $"\n  摘要：{snippet}";
            }

            results.Add(result);
            if (results.Count >= maximumResults)
            {
                break;
            }
        }

        return results;
    }

    private static string CleanHtmlText(string value)
    {
        var withoutTags = Regex.Replace(value, @"<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static HttpClient CreateResearchClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "Chrome/131.0 Safari/537.36 HuiTianAI/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.Add(
            new StringWithQualityHeaderValue("zh-CN"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    private static DynamicToolResult MoveFile(JsonElement arguments)
    {
        var sourceText = ReadString(arguments, "source")?.Trim();
        var destinationText = ReadString(arguments, "destinationFolder")?.Trim();
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(destinationText) ||
            !Path.IsPathFullyQualified(sourceText) || !Path.IsPathFullyQualified(destinationText))
        {
            return new DynamicToolResult(false, "源文件和目标文件夹必须使用完整路径。 ");
        }

        var source = Path.GetFullPath(sourceText);
        var destinationFolder = Path.GetFullPath(destinationText);
        if (!File.Exists(source))
        {
            return new DynamicToolResult(false, "要移动的文件不存在。 ");
        }

        if (IsProtectedProductOrSystemPath(source) || IsProtectedProductOrSystemPath(destinationFolder))
        {
            return new DynamicToolResult(false, "为保护系统和回天核心，不能移动该位置的文件。 ");
        }

        Directory.CreateDirectory(destinationFolder);
        var destination = Path.Combine(destinationFolder, Path.GetFileName(source));
        if (File.Exists(destination))
        {
            return new DynamicToolResult(false, "目标位置已有同名文件，回天没有覆盖它。 ");
        }

        File.Move(source, destination);
        return new DynamicToolResult(true, $"文件已移动到：{destination}");
    }

    private static DynamicToolResult InspectWindows(JsonElement arguments) =>
        WindowsUiAutomation.Inspect(ReadString(arguments, "windowTitle"), ReadInt32(arguments, "maxItems", 80));

    private static DynamicToolResult ActivateWindow(JsonElement arguments) =>
        WindowsUiAutomation.Activate(ReadString(arguments, "windowTitle"));

    private static DynamicToolResult ClickControl(JsonElement arguments) =>
        WindowsUiAutomation.Click(
            ReadString(arguments, "windowTitle"),
            ReadString(arguments, "elementName"),
            ReadInt32(arguments, "clickCount", 1));

    private static DynamicToolResult TypeText(JsonElement arguments) =>
        WindowsUiAutomation.TypeText(
            ReadString(arguments, "windowTitle"),
            ReadString(arguments, "elementName"),
            ReadString(arguments, "text"),
            ReadBoolean(arguments, "replace", true));

    private static DynamicToolResult ScrollWindow(JsonElement arguments) =>
        WindowsUiAutomation.Scroll(
            ReadString(arguments, "windowTitle"),
            ReadString(arguments, "direction") ?? "down",
            ReadInt32(arguments, "amount", 3));

    private static DynamicToolResult CaptureScreen(JsonElement arguments) =>
        WindowsUiAutomation.CaptureScreen(ReadString(arguments, "windowTitle"));

    private static DynamicToolResult ClickPoint(JsonElement arguments) =>
        WindowsUiAutomation.ClickPoint(
            ReadString(arguments, "windowTitle"),
            ReadInt32(arguments, "x", -1),
            ReadInt32(arguments, "y", -1),
            ReadBoolean(arguments, "direct", false),
            ReadInt32(arguments, "clickCount", 1));

    private static DynamicToolResult TypeActive(JsonElement arguments) =>
        WindowsUiAutomation.TypeActive(
            ReadString(arguments, "windowTitle"),
            ReadString(arguments, "text"),
            ReadBoolean(arguments, "replace", false));

    private static DynamicToolResult PressKeys(JsonElement arguments) =>
        WindowsUiAutomation.PressKeys(
            ReadString(arguments, "windowTitle"),
            ReadString(arguments, "keys"));

    private static DynamicToolResult SendChatMessage(JsonElement arguments) =>
        WindowsUiAutomation.SendChatMessage(
            ReadString(arguments, "platform"),
            ReadString(arguments, "contact"),
            ReadString(arguments, "message"));

    private static DynamicToolResult SendChatFiles(JsonElement arguments) =>
        WindowsUiAutomation.SendChatFiles(
            ReadString(arguments, "platform"),
            ReadString(arguments, "contact"),
            ReadStringArray(arguments, "filePaths"));

    private static DynamicToolResult CreateSpreadsheet(JsonElement arguments) =>
        OfficeArtifactService.CreateSpreadsheet(arguments);

    private static DynamicToolResult ModifyFile(JsonElement arguments) =>
        OfficeArtifactService.ModifyFile(arguments);

    private static DynamicToolResult CreatePresentation(JsonElement arguments)
    {
        var title = ReadString(arguments, "title")?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return new DynamicToolResult(false, "没有提供演示文稿标题。 ");
        }

        var slides = new List<(string Title, string Body)>();
        if (arguments.TryGetProperty("slides", out var slideValues) && slideValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var slide in slideValues.EnumerateArray().Take(30))
            {
                var slideTitle = ReadString(slide, "title")?.Trim();
                var body = ReadString(slide, "body")?.Trim();
                if (!string.IsNullOrWhiteSpace(slideTitle)) slides.Add((slideTitle, body ?? string.Empty));
            }
        }

        if (slides.Count == 0)
        {
            return new DynamicToolResult(false, "演示文稿至少需要一页内容。 ");
        }

        var outputDirectory = @"D:\HuiTian\UserContent\演示文稿";
        Directory.CreateDirectory(outputDirectory);
        var safeTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var outputPath = Path.Combine(outputDirectory, $"{safeTitle}-{DateTime.Now:yyyyMMdd-HHmmss}.pptx");
        dynamic? powerPoint = null;
        dynamic? presentation = null;
        try
        {
            var powerPointType = Type.GetTypeFromProgID("PowerPoint.Application");
            if (powerPointType is null)
            {
                return new DynamicToolResult(false, "这台电脑没有检测到可用的 PowerPoint。 ");
            }

            powerPoint = Activator.CreateInstance(powerPointType)!;
            // Create without an Office window. Showing and painting the full
            // editor added noticeable startup time and provided no value while
            // the finished file is already surfaced in HuiTian's artifact bar.
            presentation = powerPoint.Presentations.Add(WithWindow: false);
            presentation.PageSetup.SlideWidth = 960f;
            presentation.PageSetup.SlideHeight = 540f;
            var index = 1;
            if (slides.Count > 1)
            {
                dynamic cover = presentation.Slides.Add(index++, 12);
                BuildPresentationCover(cover, title, slides.Count);
            }

            foreach (var slide in slides)
            {
                dynamic page = presentation.Slides.Add(index, 12);
                BuildPresentationContent(
                    page,
                    slides.Count == 1 ? title : slide.Title,
                    slides.Count == 1 ? slide.Title : title,
                    slide.Body,
                    index,
                    slides.Count + (slides.Count > 1 ? 1 : 0));
                index++;
            }

            presentation.SaveAs(outputPath);
            presentation.Close();
            presentation = null;
            var expectedSlides = slides.Count + (slides.Count > 1 ? 1 : 0);
            if (!OfficeArtifactService.VerifyPresentation(outputPath, expectedSlides))
            {
                return new DynamicToolResult(
                    false,
                    $"演示文稿已写入，但重新打开核验未通过：{outputPath}",
                    ResultCode: "verification_failed",
                    ArtifactPaths: [outputPath]);
            }

            return new DynamicToolResult(
                true,
                $"演示文稿已创建并重新打开核验：{outputPath}\n已采用 16:9 版式、视觉卡片、统一配色和清晰信息层级。 ",
                ResultCode: "artifact_verified",
                ArtifactPaths: [outputPath]);
        }
        catch (Exception exception)
        {
            return new DynamicToolResult(false, $"演示文稿暂时没有创建成功：{exception.Message}");
        }
        finally
        {
            try
            {
                presentation?.Close();
            }
            catch
            {
            }

            try
            {
                powerPoint?.Quit();
            }
            catch
            {
            }
        }
    }

    private static void BuildPresentationCover(dynamic slide, string title, int contentSlideCount)
    {
        AddShape(slide, 1, 0, 0, 960, 540, 25, 31, 43, 0);
        AddShape(slide, 9, 690, -95, 355, 355, 132, 104, 202, 0.2f);
        AddShape(slide, 9, 760, 315, 260, 260, 40, 176, 212, 0.24f);
        AddShape(slide, 1, 70, 120, 72, 7, 229, 177, 92, 0);
        AddText(slide, "回天 AI · 精心制作", 70, 76, 420, 28, 14, 229, 190, 121, true);
        AddText(slide, title, 70, 155, 760, 130, 42, 250, 248, 244, true);
        AddText(
            slide,
            $"清晰叙事 · 重点突出 · 共 {contentSlideCount} 个内容章节",
            74,
            305,
            650,
            44,
            18,
            187,
            198,
            211,
            false);
        AddText(slide, DateTime.Now.ToString("yyyy.MM.dd"), 74, 456, 220, 26, 12, 151, 164, 181, false);
    }

    private static void BuildPresentationContent(
        dynamic slide,
        string title,
        string eyebrow,
        string body,
        int pageNumber,
        int totalPages)
    {
        AddShape(slide, 1, 0, 0, 960, 540, 247, 244, 237, 0);
        AddShape(slide, 1, 0, 0, 18, 540, 198, 111, 77, 0);
        AddShape(slide, 9, 835, -82, 205, 205, 213, 184, 145, 0.78f);
        AddShape(slide, 9, 885, 430, 120, 120, 126, 173, 191, 0.82f);
        AddText(slide, eyebrow, 64, 38, 700, 24, 12, 151, 100, 78, true);
        AddText(slide, title, 64, 70, 790, 56, 30, 37, 43, 52, true);
        AddText(slide, $"{pageNumber:00} / {totalPages:00}", 832, 46, 78, 24, 11, 129, 134, 143, true);

        var points = SplitPresentationPoints(body);
        var count = Math.Min(7, Math.Max(1, points.Count));
        var columns = count switch
        {
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 2,
            _ => 3,
        };
        var rows = (int)Math.Ceiling(count / (double)columns);
        var top = 150f;
        var gapX = 30f;
        var gapY = 18f;
        const float gridWidth = 832f;
        const float gridHeight = 330f;
        var cardWidth = (gridWidth - gapX * (columns - 1)) / columns;
        var cardHeight = (gridHeight - gapY * (rows - 1)) / rows;
        (int Red, int Green, int Blue)[] accents =
        [
            (205, 116, 75),
            (126, 101, 185),
            (48, 151, 177),
        ];

        for (var itemIndex = 0; itemIndex < count; itemIndex++)
        {
            var column = itemIndex % columns;
            var row = itemIndex / columns;
            var left = 64f + column * (cardWidth + gapX);
            var itemTop = top + row * (cardHeight + gapY);
            AddShape(slide, 5, left, itemTop, cardWidth, cardHeight, 255, 253, 249, 0);
            var point = points.Count == 0 ? "围绕主题提炼核心信息" : points[itemIndex];
            var accent = accents[itemIndex % accents.Length];
            if (count <= 3)
            {
                AddShape(
                    slide, 9, left + 24, itemTop + 24, 58, 58,
                    accent.Red, accent.Green, accent.Blue, 0);
                AddText(
                    slide,
                    $"{itemIndex + 1:00}",
                    left + 38,
                    itemTop + 38,
                    36,
                    25,
                    15,
                    255,
                    255,
                    255,
                    true);
                var fontSize = point.Length > 60 ? 17f : point.Length > 34 ? 19f : 22f;
                AddText(
                    slide,
                    point,
                    left + 25,
                    itemTop + 108,
                    cardWidth - 50,
                    cardHeight - 132,
                    fontSize,
                    42,
                    48,
                    58,
                    true);
            }
            else
            {
                AddShape(
                    slide, 5, left, itemTop, 8, cardHeight,
                    accent.Red, accent.Green, accent.Blue, 0);
                var fontSize = point.Length > 82 ? 14.5f : point.Length > 50 ? 16f : 18f;
                AddText(
                    slide,
                    $"{itemIndex + 1:00}   {point}",
                    left + 24,
                    itemTop + 18,
                    cardWidth - 44,
                    cardHeight - 32,
                    fontSize,
                    50,
                    55,
                    63,
                    true);
            }
        }

        AddText(slide, "回天 AI", 64, 508, 160, 18, 9, 155, 157, 161, true);
    }

    private static List<string> SplitPresentationPoints(string body)
    {
        var points = body.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Regex.Replace(value, @"^\s*[•·●▪◦\-–—\d\.、]+\s*", string.Empty).Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (points.Count <= 1 && body.Length > 60)
        {
            points = Regex.Split(body, @"(?<=[。！？；])")
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Take(7)
                .ToList();
        }

        return points.Take(7).ToList();
    }

    private static dynamic AddShape(
        dynamic slide,
        int shapeType,
        float left,
        float top,
        float width,
        float height,
        int red,
        int green,
        int blue,
        float transparency)
    {
        dynamic shape = slide.Shapes.AddShape(shapeType, left, top, width, height);
        shape.Fill.Solid();
        shape.Fill.ForeColor.RGB = OfficeRgb(red, green, blue);
        shape.Fill.Transparency = transparency;
        shape.Line.Visible = 0;
        return shape;
    }

    private static dynamic AddText(
        dynamic slide,
        string text,
        float left,
        float top,
        float width,
        float height,
        float fontSize,
        int red,
        int green,
        int blue,
        bool bold)
    {
        dynamic box = slide.Shapes.AddTextbox(1, left, top, width, height);
        box.TextFrame.MarginLeft = 0;
        box.TextFrame.MarginRight = 0;
        box.TextFrame.MarginTop = 0;
        box.TextFrame.MarginBottom = 0;
        box.TextFrame.WordWrap = -1;
        dynamic range = box.TextFrame.TextRange;
        range.Text = text;
        range.Font.Name = "Microsoft YaHei";
        range.Font.NameFarEast = "Microsoft YaHei";
        range.Font.Size = fontSize;
        range.Font.Bold = bold ? -1 : 0;
        range.Font.Color.RGB = OfficeRgb(red, green, blue);
        return box;
    }

    private static int OfficeRgb(int red, int green, int blue) =>
        red | green << 8 | blue << 16;

    private static string? FindStartMenuShortcut(string requested)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path)
                        .Contains(requested, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }

        return null;
    }

    private static bool IsProtectedProductOrSystemPath(string path)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"D:\HuiTian\Project", @"D:\HuiTian\Runtime", @"D:\HuiTian\Models",
        };
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        return roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar))
            .Any(root => fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                         fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSearchRoot(string? requestedRoot)
    {
        if (!string.IsNullOrWhiteSpace(requestedRoot) && Path.IsPathFullyQualified(requestedRoot))
        {
            return Path.GetFullPath(requestedRoot);
        }

        return Directory.Exists(@"D:\") ? @"D:\" : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string[] ReadStringArray(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];

    private static int ReadInt32(JsonElement value, string propertyName, int fallback) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var result) ? result : fallback;

    private static bool ReadBoolean(JsonElement value, string propertyName, bool fallback) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : fallback;
}

internal sealed class AutomationAttemptTracker(TimeSpan retention)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AttemptState> _attempts = new(StringComparer.Ordinal);

    public bool ShouldBlock(string key)
    {
        lock (_sync)
        {
            PruneExpired();
            return _attempts.TryGetValue(key, out var state) && state.Failures >= 2;
        }
    }

    public int RecordFailure(string key)
    {
        lock (_sync)
        {
            PruneExpired();
            var failures = _attempts.TryGetValue(key, out var state)
                ? state.Failures + 1
                : 1;
            _attempts[key] = new AttemptState(failures, DateTimeOffset.UtcNow);
            return failures;
        }
    }

    public void RecordSuccess(string key)
    {
        lock (_sync)
        {
            _attempts.Remove(key);
        }
    }

    private void PruneExpired()
    {
        var threshold = DateTimeOffset.UtcNow - retention;
        foreach (var key in _attempts
                     .Where(pair => pair.Value.UpdatedAt < threshold)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _attempts.Remove(key);
        }
    }

    private readonly record struct AttemptState(int Failures, DateTimeOffset UpdatedAt);
}
