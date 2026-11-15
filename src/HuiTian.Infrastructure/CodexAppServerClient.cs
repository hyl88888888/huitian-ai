using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed class CodexAppServerClient : IAgentSessionClient
{
    private readonly HuiTianPaths _paths;
    private readonly string _workspace;
    private readonly AgentBackendConfiguration _backend;
    private readonly WindowsToolBridge _toolBridge = new();
    private readonly SemanticIntentRouter _responseIntentGuard;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _sideEffectToolGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>>
        _pendingRequests = new();
    private readonly ConcurrentDictionary<long, PendingApproval> _pendingApprovals = new();
    private readonly ConcurrentDictionary<long, Task> _serverRequestTasks = new();
    private static readonly string[] ProtectedApprovalWords =
    [
        "支付", "付款", "购买", "转账", "汇款", "收款",
        // Opening a login page and reading an order are ordinary browsing.
        // Actual authentication is handled from the observed credential,
        // SMS-code or QR wall by WindowsUserInterventionObserver.
        "密码", "验证码", "银行卡", "信用卡", "身份证",
        "delete", "remove", "erase", "format", "overwrite", "uninstall", "factory reset",
        "删除", "清空", "格式化", "覆盖", "卸载", "永久移除", "恢复出厂",
    ];
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _traceGate = new();
    private readonly object _semanticContractGate = new();
    private readonly object _compactionGate = new();
    private readonly ConcurrentDictionary<string, StringBuilder> _assistantStreams = new();
    private readonly ConcurrentDictionary<string, byte> _closedTurnIds = new();
    private readonly ConcurrentDictionary<string, byte> _supersededTurnIds = new();
    private readonly List<ToolExecutionEvidence> _turnToolEvidence = [];
    private readonly Dictionary<string, int> _invalidToolCalls = new();
    private readonly WindowsUserInterventionObserver _interventionObserver = new();

    private CancellationTokenSource? _lifetime;
    private Process? _process;
    private StreamWriter? _input;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private long _nextRequestId;
    private int _pendingThreadUpgrades;
    private string? _currentTurnId;
    private object[]? _lastTurnInput;
    private string _lastTurnEffort = "medium";
    private AgentTaskRoute _currentRoute = AgentTaskRoute.ComputerControl;
    private AgentTaskRoute _threadToolRoute = AgentTaskRoute.ComputerControl;
    private int _modelContextTokens = 8_192;
    private bool _resetThreadBeforeNextTurn;
    private int _automaticRecoveryAttempts;
    private TurnFailureKind _lastTurnFailureKind;
    private bool _userInterrupted;
    private AutonomyMode _autonomyMode = AutonomyMode.HighAutonomy;
    private TaskCompletionSource<bool>? _recoveryReady;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private bool _disposed;
    private string _currentSemanticRequest = string.Empty;
    private IntentResolution? _activeSemanticIntent;
    private bool _taskOutcomeEmitted;
    private bool _responseDeliveredByTool;
    private bool _verifiedCompletionInterruptRequested;
    private AgentTaskOutcome? _lastVerifiedTaskOutcome;
    private CancellationTokenSource? _compactionWatchdog;
    private string? _compactionWatchdogTurnId;
    private string? _compactionTimeoutInterruptTurnId;

    internal TimeSpan ContextCompactionTimeout { get; set; } = TimeSpan.FromMinutes(2);
    internal TimeSpan ContextCompactionInterruptGrace { get; set; } = TimeSpan.FromSeconds(15);

    public CodexAppServerClient(
        HuiTianPaths paths,
        string? workspace = null,
        AgentBackendConfiguration? backend = null)
    {
        _paths = paths;
        _workspace = Path.GetFullPath(workspace ?? paths.Project);
        _backend = backend ?? AgentBackendConfiguration.ProductDefault;
        _responseIntentGuard = new SemanticIntentRouter(_backend, _paths);
    }

    public event EventHandler<AgentActivityEventArgs>? ActivityReceived;

    public event EventHandler<AgentPlanEventArgs>? PlanUpdated;

    public event EventHandler<AgentApprovalEventArgs>? ApprovalRequested;

    public event EventHandler<AgentConnectionEventArgs>? ConnectionChanged;

    public event EventHandler<AgentComputerActionEventArgs>? ComputerAction;

    public event EventHandler<AgentIntentDeclaredEventArgs>? IntentDeclared;

    public event EventHandler<AgentTaskFinishedEventArgs>? TaskFinished;

    public string? CurrentThreadId { get; private set; }

    public bool HasActiveTurn => _currentTurnId is not null ||
                                 Volatile.Read(ref _pendingThreadUpgrades) > 0 ||
                                 Volatile.Read(ref _recoveryReady) is not null;

    public void ConfigureContextWindow(int contextTokens)
    {
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("本机核心启动后不能再更改上下文容量。");
        }

        _modelContextTokens = Math.Clamp(contextTokens, 4_096, 131_072);
    }

    public void ConfigureAutonomy(AutonomyMode mode)
    {
        _ = AutonomyRiskPolicy.Evaluate(mode, ComputerTaskRisk.None);
        _autonomyMode = mode;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_process is { HasExited: false })
        {
            return;
        }

        _paths.EnsureDirectories();
        if (!File.Exists(_paths.AgentRuntime))
        {
            throw new FileNotFoundException("回天任务核心尚未安装。", _paths.AgentRuntime);
        }

        await WriteConfigurationAsync(cancellationToken).ConfigureAwait(false);
        File.WriteAllText(ProtocolTracePath, string.Empty, new UTF8Encoding(false));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var startInfo = new ProcessStartInfo(_paths.AgentRuntime, "app-server")
        {
            WorkingDirectory = _workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        startInfo.Environment["CODEX_HOME"] = _paths.AgentHome;
        startInfo.Environment["TEMP"] = _paths.Temp;
        startInfo.Environment["TMP"] = _paths.Temp;
        startInfo.Environment["RUST_LOG"] = "warn";
        startInfo.Environment["LOG_FORMAT"] = "json";
        startInfo.Environment["CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED"] = "1";
        ConfigureLocalBackendProxy(startInfo, _backend.ResponsesBaseUrl);

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("回天任务核心启动失败。 ");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            if (!_disposed && !(_lifetime?.IsCancellationRequested ?? true))
            {
                FailConnection(new InvalidOperationException(
                    $"任务核心意外退出，代码 {_process?.ExitCode}."));
            }
        };
        _input = _process.StandardInput;
        _input.AutoFlush = true;
        _stdoutReader = ReadStdoutAsync(_process.StandardOutput, _lifetime.Token);
        _stderrReader = ReadStderrAsync(_process.StandardError, _lifetime.Token);

        try
        {
            await SendRequestAsync(
                    "initialize",
                    new
                    {
                        clientInfo = new
                        {
                            name = "huitian_desktop",
                            title = "HuiTian AI",
                            version = "0.1.0",
                        },
                        capabilities = new
                        {
                            experimentalApi = true,
                        },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken)
                .ConfigureAwait(false);

            ConnectionChanged?.Invoke(this, new AgentConnectionEventArgs(
                new AgentConnectionState(
                    IsConnected: true,
                    IsBrainReady: false,
                    UserFacingStatus: "任务核心已就绪 · 灵智核心待安装")));
        }
        catch
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> CreateThreadAsync(
        AgentTaskRoute route,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        _currentRoute = route;
        var result = await SendRequestAsync(
                "thread/start",
                new
                {
                    model = _backend.ModelName,
                    modelProvider = _backend.ProviderName,
                    cwd = _workspace,
                    approvalPolicy = "on-request",
                    sandbox = "read-only",
                    ephemeral = false,
                    baseInstructions = CreateBaseInstructions(route),
                    dynamicTools = CreateDynamicToolDefinitions(route),
                },
                cancellationToken)
            .ConfigureAwait(false);

        CurrentThreadId = result
            .GetProperty("thread")
            .GetProperty("id")
            .GetString()
            ?? throw new InvalidDataException("任务核心没有返回任务编号。 ");
        _threadToolRoute = route;
        _currentTurnId = null;
        _resetThreadBeforeNextTurn = false;
        return CurrentThreadId;
    }

    public async Task SendMessageAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (CurrentThreadId is null)
        {
            var route = ConversationPolicy.FindDeclaredRoute(text) ??
                        AgentTaskRoute.Composite;
            await CreateThreadAsync(route, cancellationToken).ConfigureAwait(false);
        }

        await StartTurnAsync(
                new object[]
                {
                    new
                    {
                        type = "text",
                        text,
                    },
                },
                cancellationToken,
                effort: SelectReasoningEffort(text, hasVisualContext: false))
            .ConfigureAwait(false);
    }

    public async Task SendMessageWithLocalImageAsync(
        string text,
        string localImagePath,
        CancellationToken cancellationToken = default)
    {
        await SendMessageWithAttachmentsAsync(text, [localImagePath], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SendMessageWithAttachmentsAsync(
        string text,
        IReadOnlyList<string> localImagePaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("任务说明不能为空。", nameof(text));
        }

        var fullPaths = localImagePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var fullPath in fullPaths)
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("附加图像不存在。", fullPath);
            }
        }

        if (CurrentThreadId is null)
        {
            var route = ConversationPolicy.FindDeclaredRoute(text) ??
                        AgentTaskRoute.Composite;
            await CreateThreadAsync(route, cancellationToken).ConfigureAwait(false);
        }

        var input = new List<object> { new { type = "text", text } };
        input.AddRange(fullPaths.Select(path => (object)new { type = "localImage", path }));
        await StartTurnAsync(
                input.ToArray(),
                cancellationToken,
                effort: SelectReasoningEffort(text, hasVisualContext: fullPaths.Length > 0))
            .ConfigureAwait(false);
    }

    public async Task SteerAsync(
        string text,
        IReadOnlyList<string> localImagePaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var recovery = Volatile.Read(ref _recoveryReady);
        if (recovery is not null)
        {
            await recovery.Task
                .WaitAsync(TimeSpan.FromSeconds(35), cancellationToken)
                .ConfigureAwait(false);
        }

        var expectedTurnId = _currentTurnId;
        if (CurrentThreadId is null || expectedTurnId is null)
        {
            if (localImagePaths.Count == 0)
            {
                await SendMessageAsync(text, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendMessageWithAttachmentsAsync(text, localImagePaths, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        var input = new List<object> { new { type = "text", text } };
        foreach (var path in localImagePaths.Select(Path.GetFullPath))
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("附加图像不存在。", path);
            }

            input.Add(new { type = "localImage", path });
        }

        var steerInput = input.ToArray();
        var steerText = ExtractInputText(steerInput);
        var requestedRoute = ConversationPolicy.FindDeclaredRoute(steerText) ?? AgentTaskRoute.Composite;
        if (RequiresThreadToolUpgrade(_threadToolRoute, requestedRoute))
        {
            // Dynamic tools belong to the thread, not to a route label. Merely
            // changing _currentRoute cannot give a tool-free answer thread the
            // ability to act. Stop its text generation, carry context, and
            // start a real tool-capable thread for the changed goal.
            var carry = (_lastTurnInput ?? []).Concat(steerInput).TakeLast(8).ToArray();
            // The old completion can arrive before thread/start responds. It
            // ends only the replaced answer, never the newly submitted goal.
            Interlocked.Increment(ref _pendingThreadUpgrades);
            _supersededTurnIds[expectedTurnId] = 0;
            try
            {
                await InterruptAsync(cancellationToken).ConfigureAwait(false);
                await CreateThreadAsync(requestedRoute, cancellationToken).ConfigureAwait(false);
                await StartTurnAsync(carry, cancellationToken,
                    effort: SelectReasoningEffort(steerText, localImagePaths.Count > 0)).ConfigureAwait(false);
            }
            catch
            {
                // Do not swallow upgrade failures or retain a suppression
                // marker that could hide a subsequent real user stop.
                _supersededTurnIds.TryRemove(expectedTurnId, out _);
                throw;
            }
            finally
            {
                if (_closedTurnIds.ContainsKey(expectedTurnId))
                {
                    _supersededTurnIds.TryRemove(expectedTurnId, out _);
                }
                Interlocked.Decrement(ref _pendingThreadUpgrades);
            }
            return;
        }
        // Steering is a fresh semantic request inside the same turn. Keep the
        // universal tool surface unless the message contains an explicit
        // machine-generated route marker; never reclassify natural language
        // with keyword/regex rules here.
        _currentRoute = requestedRoute;
        _lastTurnInput = (_lastTurnInput ?? [])
            .Concat(steerInput)
            .TakeLast(8)
            .ToArray();
        _lastTurnEffort = SelectReasoningEffort(
            steerText,
            hasVisualContext: localImagePaths.Count > 0);
        ResetSemanticContract(steerText);

        await SendRequestAsync(
                "turn/steer",
                new
                {
                    threadId = CurrentThreadId,
                    input = steerInput,
                    expectedTurnId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool RequiresThreadToolUpgrade(AgentTaskRoute threadRoute, AgentTaskRoute requestedRoute) =>
        threadRoute == AgentTaskRoute.DirectAnswer && requestedRoute != AgentTaskRoute.DirectAnswer;

    private async Task StartTurnAsync(
        object[] input,
        CancellationToken cancellationToken,
        string? effort = null,
        bool automaticRecovery = false)
    {
        var declaredRoute = ConversationPolicy.FindDeclaredRoute(ExtractInputText(input)) ??
                            _currentRoute;
        if (!automaticRecovery && (_resetThreadBeforeNextTurn ||
            RequiresThreadToolUpgrade(_threadToolRoute, declaredRoute)))
        {
            // The previous answer can finish between the UI's active-turn
            // check and SteerAsync. Its send fallback needs the same upgrade.
            await CreateThreadAsync(declaredRoute, cancellationToken).ConfigureAwait(false);
        }

        if (!automaticRecovery)
        {
            _lastTurnInput = input;
            _lastTurnEffort = NormalizeReasoningEffort(effort);
            _automaticRecoveryAttempts = 0;
            _userInterrupted = false;
            _currentRoute = declaredRoute;
            ResetSemanticContract(ExtractInputText(input));
        }

        // The local Qwen chat template accepts low, medium, and xhigh.  Codex
        // also knows a generic "high" value, but passing that value through
        // causes a deterministic Jinja failure before the image or text is
        // evaluated.  Normalize at the final protocol boundary as a second
        // line of defence, including automatic recovery turns.
        var turnEffort = NormalizeReasoningEffort(effort ?? _lastTurnEffort);
        _lastTurnEffort = turnEffort;
        _lastTurnFailureKind = TurnFailureKind.None;

        var result = await SendRequestAsync(
                "turn/start",
                new
                {
                    threadId = CurrentThreadId,
                    input,
                    effort = turnEffort,
                    summary = "concise",
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (result.TryGetProperty("turn", out var turn) &&
            turn.TryGetProperty("id", out var id))
        {
            _currentTurnId = id.GetString();
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentThreadId is null || _currentTurnId is null)
        {
            return;
        }

        _userInterrupted = true;

        await SendRequestAsync(
                "turn/interrupt",
                new
                {
                    threadId = CurrentThreadId,
                    turnId = _currentTurnId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RespondToApprovalAsync(
        long requestId,
        bool approved,
        bool rememberForSession,
        CancellationToken cancellationToken = default)
    {
        if (!_pendingApprovals.TryRemove(requestId, out var pending))
        {
            return;
        }

        if (pending.Method == "huitian/userIntervention")
        {
            // Human may have completed the action manually. Never replay the
            // stale click/type/submit that was pending before takeover.
            if (pending.ThreadId != CurrentThreadId || pending.TurnId != _currentTurnId) return;
            await SendDynamicToolResponseAsync(requestId, new DynamicToolResult(
                approved,
                approved ? "用户已处理接管请求。必须重新观察当前窗口/登录状态，再决定下一步；没有自动重放之前的操作。"
                         : "用户停止了任务，不得继续操作。",
                ResultCode: approved ? "user_intervention_completed" : "user_cancelled"), cancellationToken).ConfigureAwait(false);
            if (!approved) await InterruptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pending.ToolName is not null)
        {
            if (!approved)
            {
                await SendResponseAsync(
                        requestId,
                        new
                        {
                            success = false,
                            contentItems = new[]
                            {
                                new { type = "inputText", text = "用户取消了这项操作。" },
                            },
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var result = await ExecuteScheduledToolAsync(
                    pending.ToolName,
                    pending.ToolArguments,
                    cancellationToken)
                .ConfigureAwait(false);
            EmitComputerAction(result, pending.ToolName);
            await SendDynamicToolResponseAsync(requestId, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var decision = approved
            ? rememberForSession ? "acceptForSession" : "accept"
            : "decline";
        await SendResponseAsync(requestId, new { decision }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopContextCompactionWatchdog();
        Interlocked.Exchange(ref _recoveryReady, null)?.TrySetResult(false);
        await StopProcessAsync().ConfigureAwait(false);
        var outstandingServerRequests = _serverRequestTasks.Values.ToArray();
        if (outstandingServerRequests.Length > 0)
        {
            try
            {
                await Task.WhenAll(outstandingServerRequests)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Process shutdown cancels any unfinished local tool request.
            }
        }
        _writeGate.Dispose();
        _sideEffectToolGate.Dispose();
        _recoveryGate.Dispose();
        _responseIntentGuard.Dispose();
        _lifetime?.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        EnsureStartedForWrite();
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException("无法登记回天任务请求。 ");
        }

        try
        {
            await SendPayloadAsync(new { method, id, @params = parameters }, cancellationToken)
                .ConfigureAwait(false);
            var response = await completion.Task
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            if (response.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : error.GetRawText();
                throw new InvalidOperationException(message ?? "回天任务请求失败。 ");
            }

            return response.TryGetProperty("result", out var result)
                ? result.Clone()
                : default;
        }
        finally
        {
            _pendingRequests.TryRemove(id, out _);
        }
    }

    private Task SendNotificationAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken) =>
        SendPayloadAsync(new { method, @params = parameters }, cancellationToken);

    private Task SendResponseAsync(
        long id,
        object result,
        CancellationToken cancellationToken) =>
        SendPayloadAsync(new { id, result }, cancellationToken);

    private async Task SendPayloadAsync(object payload, CancellationToken cancellationToken)
    {
        EnsureStartedForWrite();
        var line = JsonSerializer.Serialize(payload, _jsonOptions);
        TraceProtocol("> " + line);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input!.WriteLineAsync(line.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadStdoutAsync(StreamReader output, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                TraceProtocol("< " + line);

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var message = document.RootElement;
                    if (message.TryGetProperty("method", out var methodProperty))
                    {
                        var method = methodProperty.GetString() ?? string.Empty;
                        if (message.TryGetProperty("id", out var serverRequestId) &&
                            serverRequestId.TryGetInt64(out var requestId))
                        {
                            DispatchServerRequest(
                                requestId,
                                method,
                                message.Clone(),
                                cancellationToken);
                        }
                        else
                        {
                            HandleNotification(method, message);
                        }

                        continue;
                    }

                    if (message.TryGetProperty("id", out var responseId) &&
                        responseId.TryGetInt64(out var id) &&
                        _pendingRequests.TryGetValue(id, out var completion))
                    {
                        completion.TrySetResult(message.Clone());
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
        catch (Exception exception)
        {
            FailConnection(exception);
        }
    }

    private async Task ReadStderrAsync(StreamReader error, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await error.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                TraceProtocol("! " + line);

                // Structured App Server notifications below carry the real
                // failure state. Stderr often contains recoverable warnings;
                // showing both made one recoverable issue look like two fatal
                // failures to the user.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
    }

    private async Task HandleServerRequestAsync(
        long requestId,
        string method,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var parameters = message.TryGetProperty("params", out var value)
            ? value
            : default;
        if (string.Equals(method, "item/tool/call", StringComparison.Ordinal))
        {
            if (!OwnsToolRequest(parameters))
            {
                await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var requestedToolName = ReadString(parameters, "tool") ?? string.Empty;
            var requestedArguments = parameters.TryGetProperty("arguments", out var toolArguments)
                ? toolArguments.Clone()
                : JsonSerializer.SerializeToElement(new { });
            var toolName = requestedToolName;
            var arguments = requestedArguments;
            JsonElement? universalContract = null;
            if (requestedToolName == "huitian_execute")
            {
                try
                {
                    (toolName, arguments) = ResolveUniversalToolCall(requestedArguments);
                    lock (_invalidToolCalls) _invalidToolCalls.Remove(toolName[8..]);
                    arguments = GroundUniversalToolArguments(
                        toolName,
                        arguments,
                        ConversationPolicy.ExtractCurrentRequest(_currentSemanticRequest));
                    arguments = GroundGeneratedArtifactArguments(toolName, arguments);
                    universalContract = requestedArguments;
                }
                catch (ArgumentException exception)
                {
                    var capability = ReadString(requestedArguments, "c") ?? "unknown";
                    int failures;
                    lock (_invalidToolCalls)
                    {
                        failures = _invalidToolCalls.GetValueOrDefault(capability) + 1;
                        _invalidToolCalls[capability] = failures;
                    }
                    if (failures >= 2)
                    {
                        await SendDynamicToolResponseAsync(requestId, new DynamicToolResult(false,
                            $"{exception.Message} 此能力的参数已连续被拒绝 {failures} 次，动作尚未执行。不得重复相同无效参数；按具体错误修正缺失字段或枚举值。只有执行方法真实失败才切换方法，参数错误不是用户需要接管的原因。",
                            ResultCode: "invalid_arguments"), cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await SendDynamicToolResponseAsync(
                            requestId,
                            new DynamicToolResult(
                                false,
                                exception.Message,
                                ResultCode: "invalid_arguments"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
            }

            if (WindowsUserInterventionObserver.ShouldInspect(toolName) || toolName == "huitian_request_user")
            {
                UserIntervention? intervention = null;
                try
                {
                    if (toolName != "huitian_request_user" || ReadString(arguments, "kind") is "authentication" or "device_permission")
                        intervention = await _interventionObserver.CheckAsync(
                            ReadString(arguments, "windowTitle"), cancellationToken).ConfigureAwait(false);
                }
                catch (UiObservationUnavailableException)
                {
                    if (!OwnsToolRequest(parameters))
                        await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
                    else
                        await SendDynamicToolResponseAsync(requestId, new DynamicToolResult(false,
                            "可访问接口暂未响应，尚未点击或输入。请用 capture_screen 读取当前目标页面，或改用无需界面点击的专用能力；这是技术恢复，不要呼叫用户接管。",
                            ResultCode: "observation_unavailable"), cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!OwnsToolRequest(parameters))
                {
                    await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (intervention is not null)
                {
                    QueueApproval(requestId, "huitian/userIntervention", intervention.Title,
                        intervention.Detail, parameters, null, default);
                    return;
                }
                if (toolName == "huitian_request_user" && ReadString(arguments, "kind") is "authentication" or "device_permission")
                {
                    await SendDynamicToolResponseAsync(requestId, new DynamicToolResult(false,
                        "未确认当前存在登录墙或设备权限弹窗，不需要用户接管。普通打开、搜索、读取应继续自动执行；先观察实际页面，不得把页眉的登录入口当作阻碍。",
                        ResultCode: "intervention_not_observed"), cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (toolName == "huitian_request_user")
            {
                QueueApproval(requestId, "huitian/userIntervention", "这一步需要你本人处理",
                    ReadString(arguments, "reason")!, parameters, null, default);
                return;
            }

            if (!IsToolAllowedForRoute(toolName, _currentRoute))
            {
                await SendResponseAsync(
                        requestId,
                        new
                        {
                            success = false,
                            contentItems = new[]
                            {
                                new
                                {
                                    type = "inputText",
                                    text = "当前要求应直接在对话中完成，用户没有要求创建文件或操作电脑。请停止调用工具，直接回答当前问题。",
                                },
                            },
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (IsSemanticOutcomeTool(toolName))
            {
                EnsureSemanticContractForTool(toolName, arguments, universalContract);
                EnrichSemanticContractForTool(toolName, arguments);
            }

            if (toolName == "huitian_respond" &&
                await ValidateResponseChoiceAsync(cancellationToken).ConfigureAwait(false) is { } rejection)
            {
                await SendDynamicToolResponseAsync(requestId, rejection, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (toolName == "huitian_begin_task")
            {
                var declaredIntent = ParseDeclaredIntent(arguments);
                var autonomy = AutonomyRiskPolicy.Evaluate(
                    _autonomyMode,
                    ToComputerTaskRisk(declaredIntent.Risk));
                if (autonomy.Kind == AutonomyDecisionKind.Block)
                {
                    await SendDynamicToolResponseAsync(
                            requestId,
                            new DynamicToolResult(
                                false,
                                "这项重大操作不能自动执行。请缩小任务范围后再试。",
                                ResultCode: "autonomy_blocked"),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (autonomy.Kind == AutonomyDecisionKind.RequireApproval)
                {
                    QueueApproval(
                        requestId,
                        method,
                        "请确认任务范围",
                        declaredIntent.Goal,
                        parameters,
                        toolName,
                        arguments);
                    return;
                }
            }

            if (_toolBridge.RequiresApproval(toolName, arguments))
            {
                QueueApproval(
                    requestId,
                    method,
                    "请确认电脑操作",
                    _toolBridge.Describe(toolName, arguments),
                    parameters,
                    toolName,
                    arguments);
                return;
            }

            if (!OwnsToolRequest(parameters))
            {
                await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
                return;
            }
            var result = await ExecuteScheduledToolAsync(
                    toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
            if (!OwnsToolRequest(parameters))
            {
                await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
                return;
            }
            RecordToolEvidence(toolName, arguments, result);
            if (await TryPauseForObservedInterventionAsync(
                    requestId, toolName, arguments, result, parameters, cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }
            if (toolName == "huitian_respond" && result.Success &&
                ReadString(arguments, "text") is { Length: > 0 } responseText)
            {
                _responseDeliveredByTool = true;
                EmitActivity(new AgentActivity(
                    $"response-{Guid.NewGuid():N}",
                    AgentActivityKind.AssistantMessage,
                    string.Empty,
                    ConversationPolicy.SanitizeAssistantText(responseText),
                    AgentActivityStatus.Completed,
                    DateTimeOffset.Now,
                    CurrentThreadId,
                    _currentTurnId));
            }
            EmitComputerAction(result, toolName);
            await SendDynamicToolResponseAsync(requestId, result, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (method is "item/commandExecution/requestApproval" or
            "item/fileChange/requestApproval" or
            "item/permissions/requestApproval")
        {
            var detail = ReadString(parameters, "reason") ??
                         ReadString(parameters, "grantRoot") ??
                         "回天需要您的确认后才能继续这一步。";
            var protectedRequest = RequiresUserApproval(parameters, detail);
            var requestRisk = method == "item/permissions/requestApproval"
                ? ComputerTaskRisk.High
                : ComputerTaskRisk.Low;
            if (!protectedRequest &&
                AutonomyRiskPolicy.Evaluate(_autonomyMode, requestRisk).CanExecuteImmediately)
            {
                await SendResponseAsync(
                        requestId,
                        new { decision = "accept" },
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            QueueApproval(
                requestId,
                method,
                method.Contains("fileChange", StringComparison.Ordinal)
                    ? "请确认文件修改"
                    : "请确认电脑操作",
                detail,
                parameters,
                null,
                default);
            return;
        }

        await SendResponseAsync(
                requestId,
                new
                {
                    success = false,
                    contentItems = new[]
                    {
                        new { type = "inputText", text = "当前客户端暂不支持这项请求。" },
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void DispatchServerRequest(
        long requestId,
        string method,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var task = HandleServerRequestSafelyAsync(
            requestId,
            method,
            message,
            cancellationToken);
        _serverRequestTasks[requestId] = task;
        _ = task.ContinueWith(
            completedTask =>
            {
                _serverRequestTasks.TryRemove(requestId, out var removedTask);
                _ = completedTask;
                _ = removedTask;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool OwnsToolRequest(JsonElement parameters) => !_userInterrupted &&
        !string.IsNullOrWhiteSpace(_currentTurnId) &&
        ReadString(parameters, "threadId") == CurrentThreadId &&
        ReadString(parameters, "turnId") == _currentTurnId;

    private Task SendStaleToolResponseAsync(long requestId, CancellationToken token) =>
        SendResponseAsync(requestId, new
        {
            success = false,
            contentItems = new[] { new { type = "inputText", text = "该任务已经停止或被替换，未执行旧请求，不得继续旧任务。" } },
        }, token);

    private async Task HandleServerRequestSafelyAsync(
        long requestId,
        string method,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleServerRequestAsync(requestId, method, message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the task or product shuts down.
        }
        catch (Exception exception)
        {
            TraceProtocol($"! server request {requestId} failed: {exception.GetType().Name}");
            try
            {
                await SendResponseAsync(
                        requestId,
                        new
                        {
                            success = false,
                            contentItems = new[]
                            {
                                new
                                {
                                    type = "inputText",
                                    text = "这一步暂时没有执行成功，请换一种可行方式继续后续任务。",
                                },
                            },
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The App Server may already have ended the request.
            }
        }
        finally
        {
            _serverRequestTasks.TryRemove(requestId, out _);
        }
    }

    private async Task<DynamicToolResult> ExecuteScheduledToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (toolName == "huitian_begin_task")
        {
            var intent = ParseDeclaredIntent(arguments);
            RegisterSemanticIntent(intent);
            return new DynamicToolResult(
                true,
                "任务目标已登记。现在立即执行第一项能推进目标的动作；每一步都要用真实观察或产物验证，不能停在计划。",
                ResultCode: "intent_registered");
        }

        if (toolName == "huitian_finish_task")
        {
            var outcome = ParseTaskOutcome(arguments);
            RegisterTaskOutcome(outcome);
            return new DynamicToolResult(
                true,
                outcome.Succeeded
                    ? "完成证据已登记。现在只向用户简洁报告确实完成的结果和产物位置。"
                    : "未完成项已登记。现在说明已完成内容、真实阻碍和仍可继续的下一步，不得声称任务成功。",
                ResultCode: outcome.Succeeded ? "task_verified" : "task_unresolved");
        }

        if (CanRunToolInParallel(toolName))
        {
            return await _toolBridge.ExecuteAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
        }

        var ownerThread = CurrentThreadId;
        var ownerTurn = _currentTurnId;
        await _sideEffectToolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_userInterrupted || ownerThread != CurrentThreadId || ownerTurn != _currentTurnId)
                return new DynamicToolResult(false, "该任务已经停止或被替换，未执行旧操作。", ResultCode: "stale_turn");
            return await _toolBridge.ExecuteAsync(toolName, arguments, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sideEffectToolGate.Release();
        }
    }

    internal static bool CanRunToolInParallel(string toolName) => toolName is
        "huitian_search_web" or "huitian_search_files" or
        "huitian_read_text_file" or "huitian_read_document";

    private static bool IsComputerActionTool(string toolName) =>
        toolName.StartsWith("huitian_", StringComparison.Ordinal) &&
        toolName is not ("huitian_begin_task" or "huitian_finish_task" or "huitian_respond");

    private static bool IsSemanticOutcomeTool(string toolName) =>
        IsComputerActionTool(toolName) || toolName == "huitian_respond";

    /// <summary>
    /// The language model is responsible for deciding whether a user needs an
    /// answer or a real computer change.  Once it selects a concrete product
    /// tool, that structured selection is authoritative evidence of the
    /// decision.  Build the execution contract from the selected capability,
    /// not from keywords in the user's sentence, so a missed begin-task meta
    /// call can never leave the task graph uninitialised.
    /// </summary>
    private void EnsureSemanticContractForTool(
        string toolName,
        JsonElement arguments,
        JsonElement? universalContract = null)
    {
        lock (_semanticContractGate)
        {
            if (_activeSemanticIntent is not null)
            {
                return;
            }
        }

        var descriptor = DescribeToolSemantics(toolName);
        var request = ConversationPolicy.ExtractCurrentRequest(_currentSemanticRequest);
        var declaredGoal = universalContract is { } contract
            ? ReadString(contract, "goal")?.Trim()
            : null;
        var goal = !string.IsNullOrWhiteSpace(declaredGoal)
            ? declaredGoal
            : string.IsNullOrWhiteSpace(request)
            ? _toolBridge.Describe(toolName, arguments)
            : request;
        var deliverables = universalContract is { } wrapper
            ? ParseDeliverables(wrapper)
            : Array.Empty<IntentDeliverableRequirement>();
        if (deliverables.Count == 0)
        {
            deliverables = [InferDeliverable(toolName, arguments, descriptor)];
        }
        deliverables = EnrichDeliverablesForTool(deliverables, toolName, arguments);
        var entities = BuildToolEntities(toolName, arguments);
        var applications = BuildToolApplications(toolName, arguments);
        var success = deliverables
            .Select(deliverable => new IntentSuccessCondition(
                EvidenceKindFor(deliverable.Kind),
                deliverable.Description))
            .ToArray();
        var target = entities.FirstOrDefault(entity => entity.Role is
            IntentEntityRole.Recipient or IntentEntityRole.Target or
            IntentEntityRole.File or IntentEntityRole.Object)?.EffectiveValue;
        var plan = new[]
        {
            new IntentPlanStep(
                "step-1",
                descriptor.Action,
                descriptor.Action,
                applications.FirstOrDefault(),
                target,
                [],
                success,
                descriptor.Risk),
        };
        var intent = new IntentResolution(
            deliverables.Count > 1
                ? SemanticTaskType.MultiAppTask
                : descriptor.TaskType,
            deliverables.Count > 1
                ? IntentExecutionDomain.MultiApp
                : descriptor.Domain,
            goal,
            requiresComputer: toolName != "huitian_respond",
            actions: [descriptor.Action],
            applications: applications,
            entities: entities,
            successConditions: success,
            plan: plan,
            risk: descriptor.Risk,
            confidence: 0.86,
            source: "agent-selected-tool-contract",
            deliverables: deliverables);
        RegisterSemanticIntent(intent);
    }

    /// <summary>
    /// Compact d codes deliberately avoid repeating recipient prose on every
    /// local-model step. Bind an external-delivery requirement to the concrete
    /// adapter arguments once the send step is selected, so completion proves
    /// the result reached that contact instead of accepting any successful send.
    /// </summary>
    private void EnrichSemanticContractForTool(string toolName, JsonElement arguments)
    {
        if (toolName is not ("huitian_send_chat_message" or "huitian_send_chat_files"))
        {
            return;
        }

        lock (_semanticContractGate)
        {
            var current = _activeSemanticIntent;
            if (current is null)
            {
                return;
            }

            var deliverables = EnrichDeliverablesForTool(
                current.Deliverables,
                toolName,
                arguments);
            if (deliverables.SequenceEqual(current.Deliverables))
            {
                return;
            }

            var successConditions = deliverables
                .Select(item => new IntentSuccessCondition(
                    EvidenceKindFor(item.Kind),
                    item.Description))
                .ToArray();
            var entities = current.Entities
                .Concat(BuildToolEntities(toolName, arguments))
                .DistinctBy(item => (item.Role, item.Type, item.EffectiveValue))
                .ToArray();
            var applications = current.Applications
                .Concat(BuildToolApplications(toolName, arguments))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _activeSemanticIntent = new IntentResolution(
                current.TaskType,
                current.Domain,
                current.Goal,
                current.RequiresComputer,
                current.Actions,
                applications,
                entities,
                current.Constraints,
                successConditions,
                current.Plan,
                current.Risk,
                current.RequiresClarification,
                current.ClarificationReason,
                current.ClarificationQuestion,
                current.Confidence,
                current.Source,
                current.ElapsedMilliseconds,
                deliverables);
        }
    }

    internal static IReadOnlyList<IntentDeliverableRequirement> EnrichDeliverablesForTool(
        IReadOnlyList<IntentDeliverableRequirement> deliverables,
        string toolName,
        JsonElement arguments)
    {
        if (toolName is not ("huitian_send_chat_message" or "huitian_send_chat_files"))
        {
            return deliverables;
        }

        var contact = ReadString(arguments, "contact")?.Trim();
        var platform = ReadString(arguments, "platform")?.Trim();
        var destination = !string.IsNullOrWhiteSpace(contact) &&
                          !string.IsNullOrWhiteSpace(platform)
            ? $"{platform}/{contact}"
            : contact ?? platform;
        if (string.IsNullOrWhiteSpace(destination))
        {
            return deliverables;
        }

        var changed = false;
        var enriched = deliverables.Select((item, index) =>
        {
            if (item.Kind != IntentDeliverableKind.ExternalDelivery ||
                !string.IsNullOrWhiteSpace(item.Destination))
            {
                return item;
            }

            changed = true;
            return item with
            {
                Destination = destination,
                Description = DefaultDeliverableDescription(
                    IntentDeliverableKind.ExternalDelivery,
                    index + 1,
                    item.Format,
                    destination),
            };
        }).ToArray();
        return changed ? enriched : deliverables;
    }

    private async Task<DynamicToolResult?> ValidateResponseChoiceAsync(
        CancellationToken cancellationToken)
    {
        IntentResolution? intent;
        ToolExecutionEvidence[] evidence;
        lock (_semanticContractGate)
        {
            intent = _activeSemanticIntent;
            evidence = _turnToolEvidence.ToArray();
        }

        if (intent is not null)
        {
            var nonTextDeliverables = intent.Deliverables.Where(item =>
                    item.Kind is not (IntentDeliverableKind.ConversationalAnswer or
                        IntentDeliverableKind.ResearchResult))
                .ToArray();
            if (nonTextDeliverables.Length > 0)
            {
                var verification = DeliverableCompletionVerifier.Verify(
                    nonTextDeliverables,
                    evidence.Select(ToDeliverableEvidence).ToArray());
                if (!verification.Succeeded)
                {
                    return new DynamicToolResult(
                        false,
                        "用户要求的最终文件、应用状态或外部送达尚未完成，不能用一段聊天文字结案。请继续选择能推进最终交付物的能力。",
                        ResultCode: "premature_response");
                }

                // The real deliverables already exist. A short final summary
                // is now legitimate and must not trigger another classifier.
                return null;
            }

            if (intent.Deliverables.Any(item =>
                    item.Kind == IntentDeliverableKind.ResearchResult))
            {
                return HasUsableResearchSource(evidence)
                    ? null
                    : new DynamicToolResult(
                        false,
                        "研究结论还没有取得资料证据。请先查询或读取可靠内容，再形成最终答复。",
                        ResultCode: "research_source_missing");
            }
        }

        // The wrapper and its answer contract are produced by the same model
        // generation. Ask a separate, one-token semantic boundary call before
        // accepting text as the final result.
        var request = ConversationPolicy.ExtractCurrentRequest(_currentSemanticRequest);
        var decision = await _responseIntentGuard.ClassifyAsync(
                request,
                priorMessages: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (decision is not { IsConfident: true } ||
            decision.Mode is not (SemanticIntentMode.Operate or SemanticIntentMode.Mixed))
        {
            return null;
        }

        lock (_semanticContractGate)
        {
            // The next concrete tool call must establish a new, stricter
            // contract from the actual capability. Retaining the rejected
            // conversational contract would leave a later artifact unbound.
            if (_activeSemanticIntent?.Deliverables.All(item =>
                    item.Kind == IntentDeliverableKind.ConversationalAnswer) != false)
            {
                _activeSemanticIntent = null;
                _taskOutcomeEmitted = false;
            }
        }

        return new DynamicToolResult(
            false,
            "独立语义核验表明用户期待电脑或文件产生真实结果，不能只显示一段文字。请根据最终目标选择实际执行能力，并继续到结果可核验。",
            ResultCode: "response_intent_mismatch");
    }

    private void RegisterSemanticIntent(IntentResolution intent)
    {
        var shouldEmit = false;
        lock (_semanticContractGate)
        {
            shouldEmit = _activeSemanticIntent is null;
            // An explicit model contract is richer than the automatic
            // contract derived from its first selected action, so retain it
            // even when the event has already been emitted.
            if (shouldEmit || intent.Source == "agent-semantic-contract")
            {
                _activeSemanticIntent = intent;
            }

            _taskOutcomeEmitted = false;
        }

        if (shouldEmit)
        {
            IntentDeclared?.Invoke(this, new AgentIntentDeclaredEventArgs(intent));
        }
    }

    private void RecordToolEvidence(
        string toolName,
        JsonElement arguments,
        DynamicToolResult result)
    {
        if (!IsComputerActionTool(toolName) && toolName != "huitian_respond")
        {
            return;
        }

        lock (_semanticContractGate)
        {
            _turnToolEvidence.Add(new ToolExecutionEvidence(
                toolName,
                arguments.GetRawText(),
                result.Success,
                result.RequiresVerification,
                CompactEvidence(toolName == "huitian_respond"
                    ? ReadString(arguments, "text") ?? result.Text
                    : result.Text),
                result.ResultCode,
                result.ArtifactPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray() ?? []));
        }
    }

    private void RegisterTaskOutcome(AgentTaskOutcome outcome)
    {
        outcome = ApplyDeliverableGate(outcome);
        _lastVerifiedTaskOutcome = outcome;
        var shouldEmit = false;
        lock (_semanticContractGate)
        {
            if (!_taskOutcomeEmitted)
            {
                _taskOutcomeEmitted = true;
                shouldEmit = true;
            }
        }

        if (shouldEmit)
        {
            TaskFinished?.Invoke(this, new AgentTaskFinishedEventArgs(outcome));
        }
    }

    private AgentTaskOutcome ApplyDeliverableGate(AgentTaskOutcome requested)
    {
        if (!requested.Succeeded)
        {
            return requested;
        }

        IntentResolution? intent;
        ToolExecutionEvidence[] toolEvidence;
        lock (_semanticContractGate)
        {
            intent = _activeSemanticIntent;
            toolEvidence = _turnToolEvidence.ToArray();
        }

        if (intent is null)
        {
            return requested with
            {
                Succeeded = false,
                UnresolvedIssue = "没有建立最终交付物合同，不能核验用户目标已经完成。",
            };
        }

        var verification = DeliverableCompletionVerifier.Verify(
            intent.Deliverables,
            toolEvidence.Select(ToDeliverableEvidence).ToArray());
        if (verification.Succeeded)
        {
            return requested with
            {
                Evidence = requested.Evidence
                    .Concat(verification.Verified)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        var missing = verification.Missing.Count == 0
            ? "最终交付物尚未取得可核验证据。"
            : "尚未交付：" + string.Join("；", verification.Missing);
        return requested with
        {
            Succeeded = false,
            Evidence = requested.Evidence
                .Concat(verification.Verified)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            UnresolvedIssue = missing,
        };
    }

    private void CompleteSemanticContractFromEvidence(bool wasInterrupted)
    {
        IntentResolution? intent;
        ToolExecutionEvidence[] evidence;
        lock (_semanticContractGate)
        {
            if (_taskOutcomeEmitted || _activeSemanticIntent is null ||
                _turnToolEvidence.Count == 0)
            {
                return;
            }

            intent = _activeSemanticIntent;
            evidence = _turnToolEvidence.ToArray();
        }

        var verification = DeliverableCompletionVerifier.Verify(
            intent.Deliverables,
            evidence.Select(ToDeliverableEvidence).ToArray());
        var succeeded = !wasInterrupted && verification.Succeeded;
        var evidenceText = verification.Verified
            .Concat(evidence
                .Where(item => item.Success && !item.RequiresVerification)
                .Select(item => $"{FriendlyToolTitle(item.ToolName, AgentActivityStatus.Completed)}：{item.Detail}"))
            .Distinct(StringComparer.Ordinal)
            .TakeLast(8)
            .ToArray();
        if (evidenceText.Length == 0)
        {
            evidenceText = ["本轮没有取得满足最终交付物合同的证据。"];
        }

        var summary = succeeded
            ? $"{intent.Goal}：已完成 {evidence.Length} 项本机步骤并取得执行证据。"
            : wasInterrupted
                ? $"{intent.Goal}：任务已由用户停止，已完成的结果予以保留。"
                : $"{intent.Goal}：最终交付物尚未全部完成并核验。";
        var unresolved = succeeded
            ? null
            : wasInterrupted
                ? "任务已由用户停止。"
                : verification.Missing.Count > 0
                    ? "尚未交付：" + string.Join("；", verification.Missing)
                    : "执行记录中仍缺少最终交付物证据。";
        RegisterTaskOutcome(new AgentTaskOutcome(
            succeeded,
            summary,
            evidenceText,
            unresolved));
    }

    private static DeliverableExecutionEvidence ToDeliverableEvidence(
        ToolExecutionEvidence evidence) => new(
        evidence.ToolName,
        evidence.Success,
        evidence.RequiresVerification,
        evidence.ArtifactPaths,
        evidence.Detail,
        evidence.ResultCode,
        evidence.Arguments);

    private void ResetSemanticContract(string request)
    {
        lock (_invalidToolCalls) _invalidToolCalls.Clear();
        WindowsUiAutomation.ResetCaptureMap();
        IntentResolution? directAnswerIntent = null;
        if (_currentRoute == AgentTaskRoute.DirectAnswer)
        {
            var goal = ConversationPolicy.ExtractCurrentRequest(request);
            var deliverable = new IntentDeliverableRequirement(
                IntentDeliverableKind.ConversationalAnswer,
                "用户需要的文字答复已经显示在当前对话中。");
            var success = new IntentSuccessCondition(
                IntentEvidenceKind.ReturnValue,
                deliverable.Description);
            directAnswerIntent = new IntentResolution(
                SemanticTaskType.Answer,
                IntentExecutionDomain.Answer,
                string.IsNullOrWhiteSpace(goal) ? "回答用户当前问题" : goal,
                requiresComputer: false,
                actions: ["respond"],
                successConditions: [success],
                plan:
                [
                    new IntentPlanStep(
                        "step-1",
                        "形成并显示答复",
                        "respond",
                        null,
                        null,
                        [],
                        [success],
                        IntentRiskLevel.None),
                ],
                risk: IntentRiskLevel.None,
                confidence: 1,
                source: "direct-answer-route-contract",
                deliverables: [deliverable]);
        }

        lock (_semanticContractGate)
        {
            _currentSemanticRequest = request ?? string.Empty;
            _activeSemanticIntent = null;
            _taskOutcomeEmitted = false;
            _turnToolEvidence.Clear();
            _responseDeliveredByTool = false;
            _verifiedCompletionInterruptRequested = false;
            _lastVerifiedTaskOutcome = null;
        }

        if (directAnswerIntent is not null)
        {
            RegisterSemanticIntent(directAnswerIntent);
        }
    }

    private static ToolSemanticDescriptor DescribeToolSemantics(string toolName) => toolName switch
    {
        "huitian_respond" => new("respond", SemanticTaskType.Answer, IntentExecutionDomain.Answer, IntentRiskLevel.None),
        "huitian_search_files" => new("search_files", SemanticTaskType.ComputerTask, IntentExecutionDomain.File, IntentRiskLevel.None),
        "huitian_read_text_file" => new("read_text_file", SemanticTaskType.ComputerTask, IntentExecutionDomain.File, IntentRiskLevel.None),
        "huitian_read_document" => new("read_document", SemanticTaskType.Hybrid, IntentExecutionDomain.Office, IntentRiskLevel.None),
        "huitian_write_text_file" => new("create_text_file", SemanticTaskType.ComputerTask, IntentExecutionDomain.File, IntentRiskLevel.Reversible),
        "huitian_create_document" => new("create_document", SemanticTaskType.ComputerTask, IntentExecutionDomain.Office, IntentRiskLevel.Reversible),
        "huitian_create_spreadsheet" => new("create_spreadsheet", SemanticTaskType.ComputerTask, IntentExecutionDomain.Office, IntentRiskLevel.Reversible),
        "huitian_modify_file" => new("modify_file", SemanticTaskType.Hybrid, IntentExecutionDomain.File, IntentRiskLevel.Reversible),
        "huitian_move_file" => new("move_file", SemanticTaskType.ComputerTask, IntentExecutionDomain.File, IntentRiskLevel.Reversible),
        "huitian_open_application" => new("open_application", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.None),
        "huitian_open_browser" => new("open_web_page", SemanticTaskType.ComputerAction, IntentExecutionDomain.Browser, IntentRiskLevel.None),
        "huitian_find_media" => new("find_licensed_media", SemanticTaskType.ComputerTask, IntentExecutionDomain.Media, IntentRiskLevel.None),
        "huitian_search_web" => new("search_public_information", SemanticTaskType.Hybrid, IntentExecutionDomain.Research, IntentRiskLevel.None),
        "huitian_send_chat_message" => new("send_chat_message", SemanticTaskType.ComputerTask, IntentExecutionDomain.Communication, IntentRiskLevel.ExternalEffect),
        "huitian_send_chat_files" => new("send_chat_files", SemanticTaskType.MultiAppTask, IntentExecutionDomain.Communication, IntentRiskLevel.ExternalEffect),
        "huitian_screen_social_candidate" => new("screen_social_candidate", SemanticTaskType.Hybrid, IntentExecutionDomain.Communication, IntentRiskLevel.None),
        "huitian_read_social_candidates" => new("read_social_candidates", SemanticTaskType.Hybrid, IntentExecutionDomain.Communication, IntentRiskLevel.None),
        "huitian_create_presentation" => new("create_presentation", SemanticTaskType.ComputerTask, IntentExecutionDomain.Office, IntentRiskLevel.Reversible),
        "huitian_inspect_windows" or "huitian_capture_screen" => new("observe_application_state", SemanticTaskType.ComputerTask, IntentExecutionDomain.System, IntentRiskLevel.None),
        "huitian_activate_window" => new("activate_window", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.None),
        "huitian_click_control" or "huitian_click_point" => new("activate_control", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.Reversible),
        "huitian_type_text" or "huitian_type_active" => new("enter_text", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.Reversible),
        "huitian_scroll_window" => new("scroll_window", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.None),
        "huitian_press_keys" => new("use_keyboard", SemanticTaskType.ComputerAction, IntentExecutionDomain.System, IntentRiskLevel.Reversible),
        _ => new("complete_computer_action", SemanticTaskType.ComputerTask, IntentExecutionDomain.Other, IntentRiskLevel.Reversible),
    };

    internal static (string ToolName, JsonElement Arguments) ResolveUniversalToolCall(
        JsonElement wrapperArguments)
    {
        // Accept the verbose 0.4.3 envelope for persisted/recovery turns, but
        // ask new model turns for the compact c/a/d envelope.  On a 3-token/s
        // local brain, removing repeated long JSON keys from every tool step
        // materially reduces the time before Windows can act.
        var capability = (ReadString(wrapperArguments, "c") ??
                          ReadString(wrapperArguments, "capability"))?.Trim();
        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new ArgumentException("没有提供要执行的具体能力。");
        }

        var toolName = $"huitian_{capability}";
        if (!IsUniversalConcreteTool(toolName))
        {
            throw new ArgumentException($"不支持这项本机能力：{capability}");
        }

        if (!wrapperArguments.TryGetProperty("a", out var arguments) &&
            !wrapperArguments.TryGetProperty("arguments", out arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("具体能力参数必须是一个 JSON 对象。");
        }

        if (!ConcreteToolSchemas.Value.TryGetValue(toolName, out var schema))
            throw new ArgumentException($"能力 {capability} 的参数定义不可用，尚未执行。");
        return (toolName, UniversalToolArgumentContract.Validate(capability, arguments, schema));
    }

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> ConcreteToolSchemas = new(() =>
        CreateAllDynamicToolDefinitions().Where(d => GetDynamicToolName(d) is { } name && IsUniversalConcreteTool(name))
            .ToDictionary(d => GetDynamicToolName(d)!, d => JsonSerializer.SerializeToElement(d.GetType().GetProperty("inputSchema")!.GetValue(d))));

    internal static IReadOnlyList<IntentDeliverableRequirement> ParseDeliverables(
        JsonElement wrapperArguments)
    {
        if (!wrapperArguments.TryGetProperty("d", out var node) &&
            !wrapperArguments.TryGetProperty("deliverables", out node) ||
            node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IntentDeliverableRequirement>();
        }

        var result = new List<IntentDeliverableRequirement>();
        foreach (var item in node.EnumerateArray().Take(6))
        {
            var compactKind = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.ValueKind == JsonValueKind.Object
                    ? ReadString(item, "kind")
                    : null;
            if (string.IsNullOrWhiteSpace(compactKind)) continue;
            var kind = compactKind
                .Trim()
                .Replace('-', '_')
                .ToLowerInvariant() switch
            {
                "ans" or "conversational_answer" => IntentDeliverableKind.ConversationalAnswer,
                "research" or "research_result" => IntentDeliverableKind.ResearchResult,
                "doc" or "document" => IntentDeliverableKind.Document,
                "sheet" or "spreadsheet" => IntentDeliverableKind.Spreadsheet,
                "slides" or "presentation" => IntentDeliverableKind.Presentation,
                "img" or "image" => IntentDeliverableKind.Image,
                "audio" => IntentDeliverableKind.Audio,
                "video" => IntentDeliverableKind.Video,
                "modified" or "modified_file" => IntentDeliverableKind.ModifiedFile,
                "state" or "application_state" => IntentDeliverableKind.ApplicationState,
                "sent" or "external_delivery" => IntentDeliverableKind.ExternalDelivery,
                _ => IntentDeliverableKind.File,
            };
            var format = item.ValueKind == JsonValueKind.Object
                ? ReadString(item, "format")
                : null;
            var destination = item.ValueKind == JsonValueKind.Object
                ? ReadString(item, "destination")
                : null;
            var description = item.ValueKind == JsonValueKind.Object
                ? ReadString(item, "description")?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(description))
            {
                description = DefaultDeliverableDescription(
                    kind,
                    result.Count + 1,
                    format,
                    destination);
            }
            result.Add(new IntentDeliverableRequirement(
                kind,
                description,
                format,
                destination,
                item.ValueKind == JsonValueKind.Object ? ReadString(item, "artifactKey") : null));
        }

        return result;
    }

    internal static JsonElement GroundUniversalToolArguments(
        string toolName,
        JsonElement arguments,
        string? currentRequest)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(currentRequest))
        {
            return arguments.Clone();
        }

        var node = JsonNode.Parse(arguments.GetRawText())?.AsObject();
        if (node is null)
        {
            return arguments.Clone();
        }

        // Paths and opaque identifiers are data, not natural language. The
        // model decides what action to take, while this boundary preserves an
        // unambiguous literal exactly as the user supplied it. This prevents a
        // small local model from silently changing one character in a file,
        // hash, order number, or similar high-entropy value.
        if (toolName is
            "huitian_write_text_file" or "huitian_read_text_file" or
            "huitian_read_document" or "huitian_create_document" or
            "huitian_modify_file")
        {
            var paths = Regex.Matches(
                    currentRequest,
                    "(?<![A-Za-z0-9_])(?:[A-Za-z]:\\\\[^\\s\\\"<>|?*，。；;]+)",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Value.TrimEnd('。', '，', ',', ';', '；'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 1 && node["path"] is JsonValue)
            {
                node["path"] = paths[0];
            }
        }

        if (toolName == "huitian_write_text_file" &&
            node["content"]?.GetValue<string>() is { } content)
        {
            const string opaquePattern =
                @"(?<![A-Za-z0-9_])[A-Za-z0-9][A-Za-z0-9_.-]{15,}(?![A-Za-z0-9_])";
            var sourceTokens = Regex.Matches(
                    currentRequest,
                    opaquePattern,
                    RegexOptions.CultureInvariant)
                .Select(match => match.Value)
                .Where(token => Regex.Matches(
                        currentRequest,
                        Regex.Escape(token),
                        RegexOptions.CultureInvariant).Count >= 2)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var generatedTokens = Regex.Matches(
                    content,
                    opaquePattern,
                    RegexOptions.CultureInvariant)
                .Select(match => match.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (sourceTokens.Length == 1 && generatedTokens.Length == 1 &&
                !string.Equals(sourceTokens[0], generatedTokens[0], StringComparison.Ordinal))
            {
                node["content"] = content.Replace(
                    generatedTokens[0],
                    sourceTokens[0],
                    StringComparison.Ordinal);
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// A generated file path is typed runtime state, not prose for the model
    /// to copy.  When a later send step omits or slightly corrupts that path,
    /// bind it to the most recent verified artifact from the same turn.  Valid
    /// user-supplied paths are never replaced.
    /// </summary>
    private JsonElement GroundGeneratedArtifactArguments(
        string toolName,
        JsonElement arguments)
    {
        if (toolName != "huitian_send_chat_files" ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments.Clone();
        }

        var requested = arguments.TryGetProperty("filePaths", out var pathNode) &&
                        pathNode.ValueKind == JsonValueKind.Array
            ? pathNode.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToArray()
            : [];
        if (requested.Length > 0 && requested.All(File.Exists))
        {
            return arguments.Clone();
        }

        string[] latestArtifacts;
        lock (_semanticContractGate)
        {
            latestArtifacts = _turnToolEvidence
                .AsEnumerable()
                .Reverse()
                .Where(item => item.Success && !item.RequiresVerification)
                .Select(item => item.ArtifactPaths
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray())
                .FirstOrDefault(paths => paths.Length > 0) ?? [];
        }

        if (latestArtifacts.Length == 0)
        {
            return arguments.Clone();
        }

        var requestedExtensions = requested
            .Select(Path.GetExtension)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = requestedExtensions.Length == 0
            ? latestArtifacts
            : latestArtifacts.Where(path => requestedExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
                .ToArray();
        if (selected.Length == 0 && latestArtifacts.Length == 1)
        {
            selected = latestArtifacts;
        }

        if (selected.Length == 0)
        {
            return arguments.Clone();
        }

        var node = JsonNode.Parse(arguments.GetRawText())?.AsObject();
        if (node is null) return arguments.Clone();
        node["filePaths"] = new JsonArray(selected.Select(path => JsonValue.Create(path)).ToArray());
        return JsonSerializer.SerializeToElement(node);
    }

    private static string DefaultDeliverableDescription(
        IntentDeliverableKind kind,
        int ordinal,
        string? format,
        string? destination)
    {
        var target = string.IsNullOrWhiteSpace(destination)
            ? string.Empty
            : $"（目标：{destination}）";
        var type = string.IsNullOrWhiteSpace(format)
            ? string.Empty
            : $" {format.Trim().TrimStart('.')}";
        return kind switch
        {
            IntentDeliverableKind.ConversationalAnswer => "当前要求的文字答复已显示。",
            IntentDeliverableKind.ResearchResult => "取得资料依据并形成当前要求的结论。",
            IntentDeliverableKind.Document => $"第 {ordinal} 份正式{type}文档已创建并可打开。",
            IntentDeliverableKind.Spreadsheet => $"第 {ordinal} 份{type}表格已创建并可打开。",
            IntentDeliverableKind.Presentation => $"第 {ordinal} 份{type}演示文稿已创建并可打开。",
            IntentDeliverableKind.Image => $"第 {ordinal} 个{type}图片产物已创建并可打开。",
            IntentDeliverableKind.Audio => $"第 {ordinal} 个{type}音频产物已创建并可播放。",
            IntentDeliverableKind.Video => $"第 {ordinal} 个{type}视频产物已创建并可播放。",
            IntentDeliverableKind.ModifiedFile => $"第 {ordinal} 个文件已完成修改并核验。",
            IntentDeliverableKind.ApplicationState => $"目标应用状态已真实出现{target}。",
            IntentDeliverableKind.ExternalDelivery => $"正确内容已送达正确对象{target}。",
            _ => $"第 {ordinal} 个{type}文件已创建并核验。",
        };
    }

    private static IntentDeliverableRequirement InferDeliverable(
        string toolName,
        JsonElement arguments,
        ToolSemanticDescriptor descriptor)
    {
        var format = ReadString(arguments, "format") ??
                     Path.GetExtension(ReadString(arguments, "path") ?? string.Empty)
                         .TrimStart('.');
        var destination = ReadString(arguments, "contact") ??
                          ReadString(arguments, "windowTitle") ??
                          ReadString(arguments, "application");
        return toolName switch
        {
            "huitian_respond" => new IntentDeliverableRequirement(
                IntentDeliverableKind.ConversationalAnswer,
                "用户需要的文字答复已经显示在当前对话中。"),
            "huitian_search_web" or "huitian_search_files" or
            "huitian_read_text_file" or "huitian_read_document" =>
                new IntentDeliverableRequirement(
                    IntentDeliverableKind.ResearchResult,
                    "用户需要的信息已取得并可用于当前任务。"),
            "huitian_screen_social_candidate" => new IntentDeliverableRequirement(
                IntentDeliverableKind.ResearchResult,
                "候选对象仅完成证据筛选；尚未关注、联系或建立关系。"),
            "huitian_read_social_candidates" => new IntentDeliverableRequirement(
                IntentDeliverableKind.ResearchResult,
                "本地候选记忆已读取；不能据此声称已经发送消息。"),
            "huitian_create_document" => new IntentDeliverableRequirement(
                IntentDeliverableKind.Document,
                "要求的正式文档已创建、可打开并可使用。",
                string.IsNullOrWhiteSpace(format) ? "docx" : format),
            "huitian_create_spreadsheet" => new IntentDeliverableRequirement(
                IntentDeliverableKind.Spreadsheet,
                "要求的可编辑表格已创建并可打开。",
                "xlsx"),
            "huitian_modify_file" => new IntentDeliverableRequirement(
                IntentDeliverableKind.ModifiedFile,
                "要求的文件已修改为新副本并核验。",
                format),
            "huitian_create_presentation" => new IntentDeliverableRequirement(
                IntentDeliverableKind.Presentation,
                "要求的演示文稿已创建并可打开。",
                "pptx"),
            "huitian_write_text_file" => new IntentDeliverableRequirement(
                IntentDeliverableKind.File,
                "要求的文件已创建并重新读取核验。",
                format),
            "huitian_move_file" => new IntentDeliverableRequirement(
                IntentDeliverableKind.ModifiedFile,
                "文件已移动到目标位置并可用。",
                format,
                ReadString(arguments, "destinationFolder")),
            "huitian_send_chat_message" or "huitian_send_chat_files" =>
                new IntentDeliverableRequirement(
                    IntentDeliverableKind.ExternalDelivery,
                    "正确内容已送达正确联系人。",
                    Destination: destination),
            _ => new IntentDeliverableRequirement(
                IntentDeliverableKind.ApplicationState,
                $"{descriptor.Action}产生的目标应用状态已观察到。",
                Destination: destination),
        };
    }

    private static IntentEvidenceKind EvidenceKindFor(IntentDeliverableKind kind) => kind switch
    {
        IntentDeliverableKind.ConversationalAnswer or
        IntentDeliverableKind.ResearchResult => IntentEvidenceKind.ReturnValue,
        IntentDeliverableKind.Document or IntentDeliverableKind.Spreadsheet or
        IntentDeliverableKind.Presentation or IntentDeliverableKind.Image or
        IntentDeliverableKind.Audio or IntentDeliverableKind.Video or
        IntentDeliverableKind.File or IntentDeliverableKind.ModifiedFile =>
            IntentEvidenceKind.Artifact,
        IntentDeliverableKind.ExternalDelivery => IntentEvidenceKind.ExternalState,
        _ => IntentEvidenceKind.StateObservation,
    };

    private static bool IsUniversalConcreteTool(string toolName) => toolName is
        "huitian_request_user" or "huitian_respond" or "huitian_search_files" or "huitian_read_text_file" or
        "huitian_read_document" or "huitian_write_text_file" or
        "huitian_create_spreadsheet" or "huitian_modify_file" or
        "huitian_open_application" or "huitian_open_browser" or
        "huitian_find_media" or "huitian_search_web" or "huitian_move_file" or
        "huitian_inspect_windows" or "huitian_activate_window" or
        "huitian_click_control" or "huitian_type_text" or
        "huitian_scroll_window" or "huitian_capture_screen" or
        "huitian_click_point" or "huitian_type_active" or
        "huitian_press_keys" or "huitian_send_chat_message" or
        "huitian_send_chat_files" or "huitian_screen_social_candidate" or
        "huitian_read_social_candidates" or
        "huitian_create_document" or
        "huitian_create_presentation";

    private static IReadOnlyList<string> BuildToolApplications(
        string toolName,
        JsonElement arguments)
    {
        var values = new List<string>();
        AddArgumentValue(values, arguments, "application");
        AddArgumentValue(values, arguments, "platform");
        AddArgumentValue(values, arguments, "windowTitle");
        if (toolName == "huitian_create_document")
        {
            values.Add("Word");
        }
        else if (toolName == "huitian_create_spreadsheet")
        {
            values.Add("Excel");
        }
        else if (toolName == "huitian_modify_file")
        {
            values.Add("文件处理");
        }
        else if (toolName == "huitian_create_presentation")
        {
            values.Add("PowerPoint");
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
    }

    private static IReadOnlyList<IntentEntity> BuildToolEntities(
        string toolName,
        JsonElement arguments)
    {
        var result = new List<IntentEntity>();
        AddToolEntity(result, arguments, "path", IntentEntityRole.File, "file");
        AddToolEntity(result, arguments, "source", IntentEntityRole.Source, "file");
        AddToolEntity(result, arguments, "destinationFolder", IntentEntityRole.Target, "folder");
        AddToolEntity(result, arguments, "query", IntentEntityRole.Object, "query");
        AddToolEntity(result, arguments, "contact", IntentEntityRole.Recipient, "person");
        AddToolEntity(result, arguments, "url", IntentEntityRole.Target, "url");
        AddToolEntity(result, arguments, "elementName", IntentEntityRole.Target, "control");
        AddToolEntity(result, arguments, "windowTitle", IntentEntityRole.Application, "window");
        AddToolEntity(result, arguments, "application", IntentEntityRole.Application, "application");
        AddToolEntity(result, arguments, "title", IntentEntityRole.Object, "title");
        if (arguments.TryGetProperty("filePaths", out var files) &&
            files.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray().Take(8))
            {
                if (file.ValueKind == JsonValueKind.String &&
                    file.GetString() is { Length: > 0 } path)
                {
                    result.Add(new IntentEntity(IntentEntityRole.File, "file", path));
                }
            }
        }

        return result;
    }

    private static void AddToolEntity(
        ICollection<IntentEntity> entities,
        JsonElement arguments,
        string property,
        IntentEntityRole role,
        string type)
    {
        if (ReadString(arguments, property) is { Length: > 0 } value)
        {
            entities.Add(new IntentEntity(role, type, value));
        }
    }

    private static void AddArgumentValue(
        ICollection<string> values,
        JsonElement arguments,
        string property)
    {
        if (ReadString(arguments, property) is { Length: > 0 } value)
        {
            values.Add(value);
        }
    }

    private static string CompactEvidence(string value)
    {
        var compact = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return compact.Length <= 360 ? compact : compact[..359] + "…";
    }

    private sealed record ToolSemanticDescriptor(
        string Action,
        SemanticTaskType TaskType,
        IntentExecutionDomain Domain,
        IntentRiskLevel Risk);

    private sealed record ToolExecutionEvidence(
        string ToolName,
        string Arguments,
        bool Success,
        bool RequiresVerification,
        string Detail,
        string? ResultCode,
        IReadOnlyList<string> ArtifactPaths);

    private static bool RequiresUserApproval(JsonElement parameters, string detail)
    {
        var searchable = detail + "\n" + parameters.GetRawText();
        return ProtectedApprovalWords.Any(word =>
            searchable.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static ComputerTaskRisk ToComputerTaskRisk(IntentRiskLevel risk) => risk switch
    {
        IntentRiskLevel.None => ComputerTaskRisk.None,
        IntentRiskLevel.Reversible => ComputerTaskRisk.Low,
        IntentRiskLevel.ExternalEffect => ComputerTaskRisk.Medium,
        IntentRiskLevel.HighImpact => ComputerTaskRisk.High,
        _ => ComputerTaskRisk.High,
    };

    private void QueueApproval(
        long requestId,
        string method,
        string title,
        string detail,
        JsonElement parameters,
        string? toolName,
        JsonElement toolArguments)
    {
        var pending = new PendingApproval(
            method,
            ReadString(parameters, "threadId"),
            ReadString(parameters, "turnId"),
            ReadString(parameters, "itemId") ?? ReadString(parameters, "callId"),
            toolName,
            toolArguments);
        _pendingApprovals[requestId] = pending;
        ApprovalRequested?.Invoke(this, new AgentApprovalEventArgs(
            new AgentApproval(
                requestId,
                title,
                detail,
                method,
                pending.ThreadId,
                pending.TurnId,
                pending.ItemId)));
    }

    private async Task<bool> TryPauseForObservedInterventionAsync(
        long requestId,
        string toolName,
        JsonElement arguments,
        DynamicToolResult result,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!result.Success || !WindowsUserInterventionObserver.ShouldInspectObservedPage(toolName))
            return false;

        UserIntervention? observedIntervention = null;
        try
        {
            observedIntervention = await _interventionObserver.CheckAsync(
                ReadString(arguments, "windowTitle"), cancellationToken).ConfigureAwait(false);
        }
        catch (UiObservationUnavailableException)
        {
            // Preserve the completed read-only observation. A slow
            // accessibility provider is not evidence of a login wall.
        }

        if (!OwnsToolRequest(parameters))
        {
            await SendStaleToolResponseAsync(requestId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (observedIntervention is null) return false;
        QueueApproval(requestId, "huitian/userIntervention", observedIntervention.Title,
            observedIntervention.Detail, parameters, null, default);
        return true;
    }

    private Task SendDynamicToolResponseAsync(
        long requestId,
        DynamicToolResult result,
        CancellationToken cancellationToken)
    {
        var contentItems = new List<object>
        {
            new { type = "inputText", text = result.Text },
        };
        if (!result.Success || result.RequiresVerification)
        {
            contentItems.Add(new
            {
                type = "inputText",
                text = result.ResultCode == "invalid_arguments"
                    ? "本次没有执行任何电脑动作。修正上述参数后再调用；参数错误不需要截图，也不能原样重试。"
                    : result.RequiresVerification
                    ? "动作已发出但尚未验证。请立即重新观察目标界面；只有画面确认成功后才能继续或报告完成。"
                    : "这条路径没有成功。请先重新观察当前状态并判断原因，再换一种不同且安全的路径；不要原样重复失败动作，也不要结束整个任务。",
            });
        }
        if (!string.IsNullOrWhiteSpace(result.ImageDataUrl))
        {
            contentItems.Add(new { type = "inputImage", imageUrl = result.ImageDataUrl });
        }

        return SendResponseAsync(
            requestId,
            new
            {
                // Custom/GPU-rendered editors often accept the input but do not
                // expose a readable value. Treat that as pending verification so
                // the agent must observe the screen before reporting success or
                // attempting another input that could duplicate the text.
                success = result.Success && !result.RequiresVerification,
                contentItems = contentItems.ToArray(),
            },
            cancellationToken);
    }

    private void EmitComputerAction(DynamicToolResult result, string? toolName = null)
    {
        if (result.PointerX is { } x && result.PointerY is { } y)
        {
            ComputerAction?.Invoke(this, new AgentComputerActionEventArgs(
                new AgentComputerAction(x, y, result.Text,
                    toolName is "huitian_click_point" or "huitian_click_control")));
        }
    }

    internal static bool IsCurrentThreadNotification(string? currentThread, string? eventThread) =>
        string.IsNullOrEmpty(currentThread) || string.IsNullOrEmpty(eventThread) ||
        string.Equals(currentThread, eventThread, StringComparison.Ordinal);

    private void HandleNotification(string method, JsonElement message)
    {
        var parameters = message.TryGetProperty("params", out var value)
            ? value
            : default;
        var notificationTurnId = ReadString(parameters, "turnId") ??
            (parameters.ValueKind == JsonValueKind.Object &&
             parameters.TryGetProperty("turn", out var notificationTurn)
                ? ReadString(notificationTurn, "id")
                : null);
        if (notificationTurnId is not null && _closedTurnIds.ContainsKey(notificationTurnId))
        {
            return;
        }
        if (notificationTurnId is not null && _supersededTurnIds.ContainsKey(notificationTurnId))
        {
            if (method == "turn/completed")
            {
                _closedTurnIds[notificationTurnId] = 0;
                _supersededTurnIds.TryRemove(notificationTurnId, out _);
                var activeTurn = Volatile.Read(ref _currentTurnId);
                if (string.Equals(activeTurn, notificationTurnId, StringComparison.Ordinal))
                {
                    // Interlocked compares string references, not their
                    // contents. The event's parsed id is a different object.
                    Interlocked.CompareExchange(ref _currentTurnId, null, activeTurn);
                }
            }

            return;
        }
        if (!IsCurrentThreadNotification(CurrentThreadId, ReadString(parameters, "threadId"))) return;
        switch (method)
        {
            case "thread/started":
                // The matching thread/start response owns CurrentThreadId.
                // A late lifecycle event must not switch the selected thread.
                break;

            case "turn/started":
                StopContextCompactionWatchdog();
                Volatile.Write(ref _compactionTimeoutInterruptTurnId, null);
                if (parameters.TryGetProperty("turn", out var startedTurn))
                {
                    _currentTurnId = ReadString(startedTurn, "id") ?? _currentTurnId;
                    if (_currentTurnId is { } activeTurnId)
                    {
                        _closedTurnIds.TryRemove(activeTurnId, out _);
                    }
                }

                EmitActivity(new AgentActivity(
                    $"turn-{_currentTurnId}",
                    AgentActivityKind.Status,
                    "正在执行",
                    "回天正在理解并处理当前任务。",
                    AgentActivityStatus.InProgress,
                    DateTimeOffset.Now,
                    CurrentThreadId,
                    _currentTurnId));
                break;

            case "turn/completed":
                var completedTurnId = parameters.TryGetProperty("turn", out var completedTurn)
                    ? ReadString(completedTurn, "id")
                    : _currentTurnId;
                StopContextCompactionWatchdog(completedTurnId);
                var compactionTimedOut = string.Equals(
                    completedTurnId, Volatile.Read(ref _compactionTimeoutInterruptTurnId),
                    StringComparison.Ordinal);
                if (compactionTimedOut)
                    Volatile.Write(ref _compactionTimeoutInterruptTurnId, null);
                var completedStatus = parameters.TryGetProperty("turn", out completedTurn)
                    ? ReadString(completedTurn, "status")
                    : "completed";
                var internallyCompleted = _verifiedCompletionInterruptRequested &&
                                          completedStatus == "interrupted";
                var protocolEndedSuccessfully = completedStatus == "completed" ||
                                                internallyCompleted;
                var wasInterrupted = IsUserInterruptedTurn(
                    _userInterrupted, completedStatus, internallyCompleted, compactionTimedOut);
                var shouldRecover = !protocolEndedSuccessfully &&
                                    !wasInterrupted &&
                                    _lastTurnFailureKind != TurnFailureKind.NonRecoverableInput &&
                                    _automaticRecoveryAttempts < 2 &&
                                    _lastTurnInput is not null;
                TaskCompletionSource<bool>? recoveryReady = null;
                object[]? retryInput = null;
                if (shouldRecover)
                {
                    _automaticRecoveryAttempts++;
                    retryInput = _lastTurnInput!;
                    recoveryReady = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    // Publish recovery before clearing _currentTurnId so
                    // HasActiveTurn never briefly becomes false.
                    Interlocked.Exchange(ref _recoveryReady, recoveryReady);
                }
                if (!shouldRecover)
                {
                    CompleteSemanticContractFromEvidence(wasInterrupted);
                }
                IntentResolution? completedIntent;
                AgentTaskOutcome? verifiedOutcome;
                lock (_semanticContractGate)
                {
                    completedIntent = _activeSemanticIntent;
                    verifiedOutcome = _lastVerifiedTaskOutcome;
                }
                var responseOnly = completedIntent?.TaskType == SemanticTaskType.Answer;
                var protocolCompleted = protocolEndedSuccessfully;
                var outcomeCompleted = protocolCompleted &&
                                       verifiedOutcome?.Succeeded == true;
                var visibleTitle = shouldRecover
                    ? "正在自动恢复"
                    : !protocolCompleted
                        ? "任务已停止"
                        : outcomeCompleted
                            ? responseOnly ? "回复已完成" : "任务已核验完成"
                            : "任务尚有未完成项";
                var visibleDetail = shouldRecover
                    ? "当前路径未完成，回天正在建立精简连接并换一种方式继续。"
                    : !protocolCompleted
                        ? _lastTurnFailureKind == TurnFailureKind.NonRecoverableInput
                            ? "当前输入或本机参数无法处理，回天已停止重复提交同一个无效请求。"
                            : "本次任务已经停止，已完成的内容会保留。"
                        : outcomeCompleted
                            ? responseOnly
                                ? "本轮文字答复已经显示。"
                                : "全部最终交付物均已取得真实核验证据。"
                            : verifiedOutcome?.UnresolvedIssue ??
                              "本轮输出已经结束，但没有取得满足最终交付物合同的证据，不能判定任务完成。";
                EmitActivity(new AgentActivity(
                    $"turn-{completedTurnId}",
                    AgentActivityKind.Status,
                    visibleTitle,
                    visibleDetail,
                    shouldRecover
                        ? AgentActivityStatus.InProgress
                        : outcomeCompleted
                            ? AgentActivityStatus.Completed
                            : AgentActivityStatus.Failed,
                    DateTimeOffset.Now,
                    CurrentThreadId,
                    completedTurnId));
                if (!string.IsNullOrWhiteSpace(completedTurnId))
                {
                    _closedTurnIds[completedTurnId] = 0;
                }

                foreach (var stream in _assistantStreams.Keys)
                {
                    _assistantStreams.TryRemove(stream, out _);
                }
                _currentTurnId = null;
                if (recoveryReady is not null && retryInput is not null)
                {
                    _ = Task.Run(() => RecoverAndRetryAsync(retryInput, recoveryReady));
                }
                break;

            case "turn/plan/updated":
                HandlePlan(parameters);
                break;

            case "item/agentMessage/delta":
                EmitDelta(parameters, AgentActivityKind.AssistantMessage, "回天");
                break;

            case "item/reasoning/summaryTextDelta":
                EmitDelta(parameters, AgentActivityKind.Reasoning, "正在思考");
                break;

            case "item/commandExecution/outputDelta":
                EmitDelta(parameters, AgentActivityKind.Tool, "正在操作电脑");
                break;

            case "item/started":
                if (TryFinalizeVerifiedTurnBeforeAssistant(parameters))
                {
                    break;
                }

                if (IsContextCompaction(parameters))
                {
                    StartContextCompactionWatchdog(parameters);
                    break;
                }
                HandleItem(parameters, AgentActivityStatus.InProgress);
                break;

            case "item/completed":
                if (IsContextCompaction(parameters))
                {
                    var compactedTurnId = ReadString(parameters, "turnId") ?? _currentTurnId;
                    if (!string.Equals(compactedTurnId,
                            Volatile.Read(ref _compactionTimeoutInterruptTurnId),
                            StringComparison.Ordinal))
                    {
                        StopContextCompactionWatchdog(compactedTurnId);
                        EmitActivity(new AgentActivity(
                            $"compaction-{compactedTurnId}",
                            AgentActivityKind.Status,
                            "任务上下文已整理",
                            "回天正在继续执行当前任务。",
                            AgentActivityStatus.Completed,
                            DateTimeOffset.Now,
                            CurrentThreadId,
                            compactedTurnId));
                    }
                    break;
                }
                HandleItem(parameters, AgentActivityStatus.Completed);
                break;

            case "error":
                if (ReadBoolean(parameters, "willRetry"))
                {
                    EmitActivity(new AgentActivity(
                        $"turn-{_currentTurnId}",
                        AgentActivityKind.Status,
                        "正在重新连接本机能力",
                        "回天会自动继续当前任务。",
                        AgentActivityStatus.InProgress,
                        DateTimeOffset.Now,
                        CurrentThreadId,
                        _currentTurnId));
                    break;
                }

                _lastTurnFailureKind = ClassifyTurnFailure(parameters);
                _resetThreadBeforeNextTurn = true;
                if (_lastTurnFailureKind == TurnFailureKind.NonRecoverableInput)
                {
                    EmitActivity(new AgentActivity(
                        $"error-{Guid.NewGuid():N}",
                        AgentActivityKind.Error,
                        "当前输入无法处理",
                        "回天已识别到确定性的图片或任务参数错误，不会重复执行同一个无效请求。请重新选择有效内容后再试。",
                        AgentActivityStatus.Failed,
                        DateTimeOffset.Now,
                        CurrentThreadId,
                        _currentTurnId));
                }
                else
                {
                    var capacityFailure =
                        _lastTurnFailureKind == TurnFailureKind.ContextCapacity;
                    EmitActivity(new AgentActivity(
                        $"error-{Guid.NewGuid():N}",
                        AgentActivityKind.Status,
                        capacityFailure ? "正在精简任务连接" : "正在恢复本机任务",
                        capacityFailure
                            ? "当前任务内容接近本机容量，回天将保留最新要求、建立精简连接并继续。"
                            : "本机能力刚才暂时没有响应，回天将建立新连接并继续当前要求。",
                        AgentActivityStatus.InProgress,
                        DateTimeOffset.Now,
                        CurrentThreadId,
                        _currentTurnId));
                }
                break;
        }
    }

    private async Task RecoverAndRetryAsync(
        object[] retryInput,
        TaskCompletionSource<bool> recoveryReady)
    {
        await _recoveryGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await CreateThreadAsync(_currentRoute).ConfigureAwait(false);
            var attempt = Math.Max(1, _automaticRecoveryAttempts);
            var recoveryInput = retryInput
                .Concat(new object[]
                {
                    new
                    {
                        type = "text",
                        text = $"【自动续办第 {attempt} 次】上一条执行路径没有完成。先重新观察当前真实状态，避免重复已经生效的动作；随后采用一种不同的安全路径继续当前唯一要求。不要回答旧问题。",
                    },
                })
                .ToArray();
            await StartTurnAsync(
                    recoveryInput,
                    CancellationToken.None,
                    effort: _lastTurnEffort,
                    automaticRecovery: true)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            _resetThreadBeforeNextTurn = true;
            EmitActivity(new AgentActivity(
                $"recovery-{Guid.NewGuid():N}",
                AgentActivityKind.Error,
                "当前路径仍未完成",
                "刚才的要求和已经完成的步骤仍保存在对话中；你可以继续补充信息，回天不会丢失当前要求。",
                AgentActivityStatus.Failed,
                DateTimeOffset.Now,
                CurrentThreadId,
                _currentTurnId));
        }
        finally
        {
            _recoveryGate.Release();
            recoveryReady.TrySetResult(true);
            Interlocked.CompareExchange(ref _recoveryReady, null, recoveryReady);
        }
    }

    private void HandlePlan(JsonElement parameters)
    {
        var steps = new List<AgentPlanStep>();
        if (parameters.TryGetProperty("plan", out var plan) &&
            plan.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in plan.EnumerateArray())
            {
                steps.Add(new AgentPlanStep(
                    ReadString(step, "step") ?? "待处理步骤",
                    MapStatus(ReadString(step, "status"))));
            }
        }

        PlanUpdated?.Invoke(this, new AgentPlanEventArgs(
            new AgentPlan(ReadString(parameters, "explanation"), steps)));
    }

    private void EmitDelta(
        JsonElement parameters,
        AgentActivityKind kind,
        string title)
    {
        var notificationTurnId = ReadString(parameters, "turnId") ?? _currentTurnId;
        if (!string.IsNullOrWhiteSpace(notificationTurnId) &&
            _closedTurnIds.ContainsKey(notificationTurnId))
        {
            return;
        }

        var delta = ReadString(parameters, "delta");
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (kind == AgentActivityKind.AssistantMessage &&
            !CanSurfaceNaturalAssistantMessage())
        {
            return;
        }

        var itemId = ReadString(parameters, "itemId") ?? $"delta-{Guid.NewGuid():N}";
        if (kind == AgentActivityKind.AssistantMessage)
        {
            var buffer = _assistantStreams.GetOrAdd(itemId, _ => new StringBuilder());
            lock (buffer)
            {
                buffer.Append(delta);
                delta = ConversationPolicy.SanitizeAssistantText(buffer.ToString());
            }

            if (string.IsNullOrWhiteSpace(delta))
            {
                return;
            }
        }

        EmitActivity(new AgentActivity(
            itemId,
            kind,
            title,
            delta,
            AgentActivityStatus.InProgress,
            DateTimeOffset.Now,
            ReadString(parameters, "threadId") ?? CurrentThreadId,
            notificationTurnId));
    }

    private static bool IsContextCompaction(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("item", out var item) &&
        string.Equals(ReadString(item, "type"), "contextCompaction", StringComparison.Ordinal);

    internal static bool IsUserInterruptedTurn(
        bool userInterrupted,
        string? completedStatus,
        bool internallyCompleted,
        bool compactionTimedOut) =>
        userInterrupted || completedStatus == "interrupted" &&
        !internallyCompleted && !compactionTimedOut;

    private void StartContextCompactionWatchdog(JsonElement parameters)
    {
        var turnId = ReadString(parameters, "turnId") ?? _currentTurnId;
        var threadId = ReadString(parameters, "threadId") ?? CurrentThreadId;
        if (string.IsNullOrWhiteSpace(turnId) ||
            !string.Equals(turnId, _currentTurnId, StringComparison.Ordinal)) return;

        CancellationTokenSource watchdog;
        lock (_compactionGate)
        {
            if (_compactionWatchdog is not null &&
                string.Equals(_compactionWatchdogTurnId, turnId, StringComparison.Ordinal)) return;
            _compactionWatchdog?.Cancel();
            _compactionWatchdog?.Dispose();
            watchdog = new CancellationTokenSource();
            _compactionWatchdog = watchdog;
            _compactionWatchdogTurnId = turnId;
        }

        EmitActivity(new AgentActivity(
            $"compaction-{turnId}",
            AgentActivityKind.Status,
            "正在整理任务上下文",
            "先前步骤较多，回天正在整理执行记录，完成后继续当前任务。",
            AgentActivityStatus.InProgress,
            DateTimeOffset.Now,
            threadId,
            turnId));
        _ = MonitorContextCompactionAsync(threadId, turnId, watchdog);
    }

    private void StopContextCompactionWatchdog(string? expectedTurnId = null)
    {
        CancellationTokenSource? watchdog;
        lock (_compactionGate)
        {
            if (expectedTurnId is not null &&
                !string.Equals(expectedTurnId, _compactionWatchdogTurnId,
                    StringComparison.Ordinal)) return;
            watchdog = _compactionWatchdog;
            _compactionWatchdog = null;
            _compactionWatchdogTurnId = null;
        }

        watchdog?.Cancel();
        watchdog?.Dispose();
    }

    private async Task MonitorContextCompactionAsync(
        string? threadId,
        string turnId,
        CancellationTokenSource watchdog)
    {
        try
        {
            await Task.Delay(ContextCompactionTimeout, watchdog.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_compactionGate)
        {
            if (_disposed || watchdog.IsCancellationRequested ||
                !ReferenceEquals(_compactionWatchdog, watchdog) ||
                !string.Equals(_currentTurnId, turnId, StringComparison.Ordinal) ||
                !string.Equals(CurrentThreadId, threadId, StringComparison.Ordinal) ||
                _userInterrupted) return;
            Volatile.Write(ref _compactionTimeoutInterruptTurnId, turnId);
        }

        EmitActivity(new AgentActivity(
            $"compaction-{turnId}",
            AgentActivityKind.Status,
            "上下文整理超时，正在恢复",
            "回天会停止这次整理，重新观察当前状态，核对已完成的动作后继续。",
            AgentActivityStatus.InProgress,
            DateTimeOffset.Now,
            threadId,
            turnId));

        try
        {
            // This is an internal timeout, not a user stop. The matching
            // interrupted completion is eligible for the existing bounded
            // recovery path, which starts a fresh thread from the same goal.
            await SendRequestAsync("turn/interrupt", new { threadId, turnId },
                    watchdog.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A wedged App Server may not acknowledge the interrupt. The
            // grace period below still terminates the visible task.
            if (_disposed) return;
        }

        try
        {
            await Task.Delay(ContextCompactionInterruptGrace, watchdog.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_compactionGate)
        {
            if (_disposed || watchdog.IsCancellationRequested ||
                !ReferenceEquals(_compactionWatchdog, watchdog) ||
                !string.Equals(Volatile.Read(ref _compactionTimeoutInterruptTurnId), turnId,
                    StringComparison.Ordinal) ||
                !string.Equals(_currentTurnId, turnId, StringComparison.Ordinal) ||
                !string.Equals(CurrentThreadId, threadId, StringComparison.Ordinal)) return;
            Volatile.Write(ref _compactionTimeoutInterruptTurnId, null);
            _closedTurnIds[turnId] = 0;
            _currentTurnId = null;
            _resetThreadBeforeNextTurn = true;
        }

        StopContextCompactionWatchdog(turnId);
        RegisterTaskOutcome(new AgentTaskOutcome(
            false,
            "任务上下文整理超时，已保留此前完成的步骤。",
            [],
            "本机任务核心未能在超时后确认停止；再次提交时会建立新连接。"));
        EmitActivity(new AgentActivity(
            $"turn-{turnId}",
            AgentActivityKind.Status,
            "当前任务已停止",
            "本机任务核心未能及时完成整理。已保留此前步骤；再次提交时会建立新连接。",
            AgentActivityStatus.Failed,
            DateTimeOffset.Now,
            threadId,
            turnId));
    }

    private void HandleItem(JsonElement parameters, AgentActivityStatus eventStatus)
    {
        if (!parameters.TryGetProperty("item", out var item))
        {
            return;
        }

        var type = ReadString(item, "type") ?? string.Empty;
        if (type == "agentMessage" &&
            !CanSurfaceNaturalAssistantMessage())
        {
            if (eventStatus == AgentActivityStatus.Completed &&
                ReadString(item, "id") is { Length: > 0 } hiddenMessageId)
            {
                _assistantStreams.TryRemove(hiddenMessageId, out _);
            }

            return;
        }
        var id = ReadString(item, "id") ?? $"item-{Guid.NewGuid():N}";
        var notificationTurnId = ReadString(parameters, "turnId") ?? _currentTurnId;
        if (!string.IsNullOrWhiteSpace(notificationTurnId) &&
            _closedTurnIds.ContainsKey(notificationTurnId))
        {
            return;
        }

        var status = eventStatus == AgentActivityStatus.Completed
            ? MapStatus(ReadString(item, "status"), AgentActivityStatus.Completed)
            : eventStatus;
        var (kind, title, detail) = type switch
        {
            "userMessage" => (
                AgentActivityKind.UserMessage,
                "您",
                ExtractUserText(item)),
            "agentMessage" => (
                AgentActivityKind.AssistantMessage,
                string.Empty,
                ConversationPolicy.SanitizeAssistantText(ReadString(item, "text"))),
            "reasoning" => (
                AgentActivityKind.Reasoning,
                "正在思考",
                ReadString(item, "summary") ?? "正在分析当前任务。"),
            "commandExecution" => (
                AgentActivityKind.Tool,
                status == AgentActivityStatus.Completed ? "电脑操作已完成" : "正在操作电脑",
                ReadString(item, "aggregatedOutput") ?? FriendlyCommandDetail(item)),
            "fileChange" => (
                AgentActivityKind.FileChange,
                status == AgentActivityStatus.Completed ? "文件处理已完成" : "正在处理文件",
                FriendlyFileChangeDetail(item)),
            "dynamicToolCall" => (
                AgentActivityKind.Tool,
                FriendlyToolTitle(ResolveDisplayToolName(item), status),
                ExtractToolResult(item)),
            _ => (
                AgentActivityKind.Status,
                "正在处理",
                "回天正在完成当前步骤。"),
        };

        if (eventStatus == AgentActivityStatus.InProgress &&
            type is "agentMessage" or "userMessage" &&
            string.IsNullOrEmpty(detail))
        {
            return;
        }

        if (type == "agentMessage" && eventStatus == AgentActivityStatus.Completed)
        {
            _assistantStreams.TryRemove(id, out _);
            RecordAssistantResponseEvidence(detail);
        }

        EmitActivity(new AgentActivity(
            id,
            kind,
            title,
            detail,
            status,
            DateTimeOffset.Now,
            ReadString(parameters, "threadId") ?? CurrentThreadId,
            notificationTurnId));
    }

    private void RecordAssistantResponseEvidence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_semanticContractGate)
        {
            _turnToolEvidence.Add(new ToolExecutionEvidence(
                "assistant_response",
                "{}",
                Success: true,
                RequiresVerification: false,
                Detail: CompactEvidence(text),
                ResultCode: "response_delivered",
                ArtifactPaths: []));
        }
    }

    private bool TryFinalizeVerifiedTurnBeforeAssistant(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item) ||
            !string.Equals(ReadString(item, "type"), "agentMessage", StringComparison.Ordinal) ||
            _currentRoute != AgentTaskRoute.Composite)
        {
            return false;
        }

        IntentResolution? intent;
        ToolExecutionEvidence[] evidence;
        lock (_semanticContractGate)
        {
            if (_taskOutcomeEmitted || _activeSemanticIntent is null ||
                _turnToolEvidence.Count == 0)
            {
                return false;
            }

            intent = _activeSemanticIntent;
            evidence = _turnToolEvidence.ToArray();
        }

        var verification = DeliverableCompletionVerifier.Verify(
            intent.Deliverables,
            evidence.Select(ToDeliverableEvidence).ToArray());
        if (!verification.Succeeded)
        {
            return false;
        }

        var artifactPaths = evidence
            .Where(entry => entry.Success && !entry.RequiresVerification)
            .SelectMany(entry => entry.ArtifactPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var summary = artifactPaths.Length > 0
            ? "已完成并核验。产物位置：" + string.Join("；", artifactPaths)
            : "当前要求已经完成并取得真实核验证据。";
        RegisterTaskOutcome(new AgentTaskOutcome(
            true,
            summary,
            verification.Verified,
            null));

        if (!_responseDeliveredByTool)
        {
            EmitActivity(new AgentActivity(
                $"verified-response-{Guid.NewGuid():N}",
                AgentActivityKind.AssistantMessage,
                string.Empty,
                summary,
                AgentActivityStatus.Completed,
                DateTimeOffset.Now,
                CurrentThreadId,
                _currentTurnId));
        }

        _responseDeliveredByTool = true;
        _verifiedCompletionInterruptRequested = true;
        var threadId = ReadString(parameters, "threadId") ?? CurrentThreadId;
        var turnId = ReadString(parameters, "turnId") ?? _currentTurnId;
        if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
        {
            _ = Task.Run(() => InterruptVerifiedCompletionAsync(threadId, turnId));
        }

        return true;
    }

    private async Task InterruptVerifiedCompletionAsync(string threadId, string turnId)
    {
        try
        {
            await SendRequestAsync(
                    "turn/interrupt",
                    new { threadId, turnId },
                    _lifetime?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TraceProtocol($"! verified completion interrupt failed: {exception.GetType().Name}");
        }
    }

    private bool CanSurfaceNaturalAssistantMessage()
    {
        if (_responseDeliveredByTool)
        {
            return false;
        }

        // Every Composite turn has one structured exit.  Before that exit is
        // selected, model prose is only an unverified preamble (and can even
        // claim that a file already exists).  Pure text answers must use
        // huitian_respond.  A natural summary is legitimate only after a real
        // non-text deliverable has already been verified.
        if (_currentRoute != AgentTaskRoute.Composite)
        {
            return true;
        }

        IntentResolution? intent;
        ToolExecutionEvidence[] evidence;
        lock (_semanticContractGate)
        {
            intent = _activeSemanticIntent;
            evidence = _turnToolEvidence.ToArray();
        }

        if (intent is null)
        {
            return false;
        }

        var independentlyVerifiable = intent.Deliverables
            .Where(item => item.Kind is not (
                IntentDeliverableKind.ConversationalAnswer or
                IntentDeliverableKind.ResearchResult))
            .ToArray();
        if (independentlyVerifiable.Length == 0 ||
            !DeliverableCompletionVerifier.Verify(
                    independentlyVerifiable,
                    evidence.Select(ToDeliverableEvidence).ToArray())
                .Succeeded)
        {
            return false;
        }

        return !intent.Deliverables.Any(item =>
                   item.Kind == IntentDeliverableKind.ResearchResult) ||
               HasUsableResearchSource(evidence);
    }

    private static bool HasUsableResearchSource(
        IReadOnlyCollection<ToolExecutionEvidence> evidence) =>
        evidence.Any(item =>
            item.Success && !item.RequiresVerification &&
            (item.ToolName is
                "huitian_search_web" or "huitian_search_files" or
                "huitian_read_text_file" or "huitian_read_document") &&
            !string.Equals(item.ResultCode, "no_results", StringComparison.Ordinal));

    private void EmitActivity(AgentActivity activity) =>
        ActivityReceived?.Invoke(this, new AgentActivityEventArgs(activity));

    private void FailConnection(Exception exception)
    {
        foreach (var completion in _pendingRequests.Values)
        {
            completion.TrySetException(exception);
        }

        ConnectionChanged?.Invoke(this, new AgentConnectionEventArgs(
            new AgentConnectionState(
                IsConnected: false,
                IsBrainReady: false,
                UserFacingStatus: "任务核心暂时不可用")));
        EmitActivity(new AgentActivity(
            $"connection-{Guid.NewGuid():N}",
            AgentActivityKind.Error,
            "任务核心暂时不可用",
            "回天已经保留界面状态，可以重新启动后继续。",
            AgentActivityStatus.Failed,
            DateTimeOffset.Now,
            CurrentThreadId,
            _currentTurnId));
    }

    private static void ConfigureLocalBackendProxy(ProcessStartInfo startInfo, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback) return;

        // reqwest can inherit the Windows system proxy even for localhost.
        // Keep local model traffic local, preserving existing exclusions and
        // all proxy/security settings outside this owned child process.
        startInfo.Environment.TryGetValue("NO_PROXY", out var inherited);
        var exclusions = (inherited ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(new[] { endpoint.DnsSafeHost, "127.0.0.1", "localhost", "::1" })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        startInfo.Environment["NO_PROXY"] = string.Join(',', exclusions);
    }

    private async Task WriteConfigurationAsync(CancellationToken cancellationToken)
    {
        // The App Server must know the same real window used by the local
        // runtime.  Advertising 16K while the Huang runtime had 8K delayed
        // compaction until the backend rejected the request at 8198 tokens.
        var compactLimit = Math.Max(3_072, _modelContextTokens * 3 / 4);
        var configuration = $$"""
                              model = "{{_backend.ModelName}}"
                              model_provider = "{{_backend.ProviderName}}"
                              model_context_window = {{_modelContextTokens}}
                              model_auto_compact_token_limit = {{compactLimit}}

                              [model_providers.{{_backend.ProviderName}}]
                              name = "HuiTian Local"
                              base_url = "{{_backend.ResponsesBaseUrl}}"
                              wire_api = "responses"
                              requires_openai_auth = false
                              request_max_retries = 2
                              stream_max_retries = 2
                              stream_idle_timeout_ms = 120000

                              [analytics]
                              enabled = false

                              [feedback]
                              enabled = false

                              [features]
                              apps = false
                              plugins = false

                              [skills]
                              include_instructions = false

                              [skills.bundled]
                              enabled = false
                              """;
        var path = Path.Combine(_paths.AgentHome, "config.toml");
        await File.WriteAllTextAsync(path, configuration + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
    }

    private static object[] CreateDynamicToolDefinitions(AgentTaskRoute route)
    {
        // MainWindow creates a fresh App Server thread for each completed user
        // turn, so registering every product tool for a natural follow-up is no
        // longer necessary.  Register only the selected route's tools up front:
        // this keeps unrelated schemas and descriptions out of the local 27B
        // prefill instead of constructing a huge generic-agent prompt first.
        var definitions = CreateAllDynamicToolDefinitions();
        if (route == AgentTaskRoute.Composite)
        {
            // Keep lifecycle definitions registered for backwards protocol
            // compatibility. The HTTP bridge hides them and exposes only the
            // compact executor; concrete adapters remain server-side.
            return definitions.Where(definition =>
                    GetDynamicToolName(definition) is
                        "huitian_execute" or "huitian_begin_task" or "huitian_finish_task")
                .ToArray();
        }

        return definitions
            .Where(definition =>
                GetDynamicToolName(definition) is { } name &&
                ConversationPolicy.IsToolAllowed(name, route))
            .ToArray();
    }

    private static string? GetDynamicToolName(object definition) =>
        definition.GetType().GetProperty("name")?.GetValue(definition) as string;

    private static object[] CreateAllDynamicToolDefinitions()
    {
    object[] definitions =
    [
        new
        {
            type = "function",
            name = "huitian_execute",
            description = "唯一语义出口。d=整项最终结果：ans/research/doc/sheet/slides/img/audio/video/file/modified/state/sent；c=当前能力；a=参数。签名：respond{text}；write_text_file{path,content}；create_document{title,content,format?,fileName?,orientation?}；create_spreadsheet{title,sheetName?,headers,rows,fileName?}；create_presentation{title,slides}；modify_file{path,replacements?,newContent?,cellUpdates?,outputFileName?}；search_files{query,root?}；read_text_file/read_document{path}；open_application{application}；open_browser{url}；find_media{query,kind?}；search_web{query}；move_file{source,destinationFolder}；send_chat_message{platform,contact,message}；send_chat_files{platform,contact,filePaths}。创建/修改能力返回已核验即可信，不要重复读取；生成后发送须保持文件代码和 sent，取得真实路径后再发。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    c = new
                    {
                        type = "string",
                        @enum = new[]
                        {
                            "request_user", "respond", "search_files", "read_text_file", "read_document", "write_text_file", "create_document", "create_spreadsheet", "modify_file",
                            "open_application", "open_browser", "find_media", "search_web", "move_file",
                            "inspect_windows", "activate_window", "click_control", "type_text",
                            "scroll_window", "capture_screen", "click_point", "type_active",
                            "press_keys", "send_chat_message", "send_chat_files", "screen_social_candidate", "read_social_candidates", "create_presentation",
                        },
                    },
                    a = new
                    {
                        type = "object",
                        description = "只放 c 所需的具名参数。",
                        additionalProperties = true,
                    },
                    d = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 6,
                        description = "整项任务全部最终结果代码；每一步保持相同，不能把中间步骤当结果。",
                        items = new
                        {
                            anyOf = new object[]
                            {
                                new { type = "string", @enum = new[] { "ans", "research", "doc", "sheet", "slides", "img", "audio", "video", "file", "modified", "state", "sent" } },
                                new { type = "object", properties = new {
                                    kind = new { type = "string", @enum = new[] { "ans", "research", "doc", "sheet", "slides", "img", "audio", "video", "file", "modified", "state", "sent" } },
                                    artifactKey = new { type = "string" }, format = new { type = "string" },
                                    destination = new { type = "string" }, description = new { type = "string" } },
                                    required = new[] { "kind", "artifactKey" }, additionalProperties = false },
                            },
                        },
                    },
                },
                required = new[] { "c", "a" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function", name = "huitian_respond",
            description = "仅提交用户需要的文字答案；不能替代文件或电脑任务的最终交付物。",
            inputSchema = new { type = "object", properties = new { text = new { type = "string" } },
                required = new[] { "text" }, additionalProperties = false },
        },
        new
        {
            type = "function", name = "huitian_find_media",
            description = "寻找匹配影视或音乐的正版播放来源；搜索页不代表已经播放。",
            inputSchema = new { type = "object", properties = new { query = new { type = "string" },
                kind = new { type = "string", @enum = new[] { "video", "audio" } } },
                required = new[] { "query" }, additionalProperties = false },
        },
        new
        {
            type = "function",
            name = "huitian_begin_task",
            description = "仅当当前要求需要真实操作电脑时，必须作为第一项调用；登记完整目标后再执行。纯回答、解释、闲聊、评价或只写一段文字时不要调用。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    goal = new { type = "string", description = "结合上下文解析后的最终结果，不复述步骤。" },
                    taskType = new
                    {
                        type = "string",
                        @enum = new[] { "computer_action", "computer_task", "hybrid", "multi_app_task" },
                    },
                    domain = new
                    {
                        type = "string",
                        @enum = new[] { "research", "file", "office", "browser", "communication", "media", "system", "development", "other", "multi_app" },
                    },
                    actions = new { type = "array", items = new { type = "string" }, maxItems = 6 },
                    applications = new { type = "array", items = new { type = "string" }, maxItems = 4 },
                    @object = new { type = "string", description = "被处理的内容、文件或上下文对象；没有则省略。" },
                    target = new { type = "string", description = "目标位置、状态或对象；没有则省略。" },
                    recipient = new { type = "string", description = "对外沟通的接收者；没有则省略。" },
                    constraints = new { type = "array", items = new { type = "string" }, maxItems = 6 },
                    success = new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = 5 },
                    risk = new
                    {
                        type = "string",
                        @enum = new[] { "none", "reversible", "external_effect", "high_impact" },
                    },
                },
                required = new[] { "goal", "taskType", "domain", "actions", "success", "risk" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_finish_task",
            description = "电脑任务结束前必须调用一次。只有实际观察到用户目标已达成时才能 succeeded=true；工具返回成功本身不是完整任务证据。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    succeeded = new { type = "boolean" },
                    summary = new { type = "string", description = "已完成结果的简短事实总结。" },
                    evidence = new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = 6 },
                    unresolvedIssue = new { type = "string", description = "失败时的真实外部阻碍；成功时省略。" },
                },
                required = new[] { "succeeded", "summary", "evidence" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_search_files",
            description = "在 Windows 本机按文件名搜索；整理或打开未知位置的文件前使用。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "文件名关键词。" },
                    root = new { type = "string", description = "可选的 Windows 绝对文件夹路径。" },
                },
                required = new[] { "query" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_read_text_file",
            description = "读取用户选择的本机文字、代码、CSV、JSON、Markdown 或日志文件，最大 2 MB；必须使用绝对路径。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Windows 文件绝对路径。" },
                },
                required = new[] { "path" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_read_document",
            description = "读取真实文档。PPTX直接按页提取，无需启动Office，默认5页最多6000字符；startPage/maxPages/maxChars控制分页。整体格式修改无需读全文。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Windows 文档绝对路径。" },
                    startPage = new { type = "integer", minimum = 1 },
                    maxPages = new { type = "integer", minimum = 1, maximum = 20 },
                    maxChars = new { type = "integer", minimum = 1000, maximum = 20000 },
                },
                required = new[] { "path" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_write_text_file",
            description = "立即为用户创建新的文字或代码文件；自动创建父文件夹、绝不覆盖已有文件，写入后会在本机重新读取并逐字核验，再返回保存绝对路径。用户已给目标路径时不要先搜索，也不要仅为核验再调用读取工具。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "目标文件绝对路径。" },
                    content = new { type = "string", description = "完整文件内容。" },
                },
                required = new[] { "path", "content" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_create_document",
            description = "创建可打印、可编辑的正式 Word 文档，或在本机 Word 可用时同时导出 PDF。内部会重新打开 DOCX 包并核验标题和完整正文；返回真实产物路径。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "正式文档标题。" },
                    content = new { type = "string", description = "完整正文；可用 #/## 标题、空行预留书写区域、[分页] 强制分页。" },
                    format = new { type = "string", @enum = new[] { "docx", "pdf" }, description = "默认 docx。" },
                    fileName = new { type = "string", description = "可选的安全文件名，不含目录。" },
                    orientation = new { type = "string", @enum = new[] { "portrait", "landscape" }, description = "默认 portrait。" },
                },
                required = new[] { "title", "content" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_open_application",
            description = "打开已安装的 Windows 程序；普通打开操作自动执行，不需要确认。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    application = new { type = "string", description = "程序显示名称。" },
                },
                required = new[] { "application" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_open_browser",
            description = "在用户默认浏览器打开 http 或 https 网址；普通网页导航自动执行。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", description = "完整的 http 或 https 网址。" },
                },
                required = new[] { "url" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_search_web",
            description = "直接读取大陆网络可访问的公开搜索结果并返回标题、摘要和来源；只有直读失败时才打开浏览器作为一次性后备。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "最新信息搜索词。" },
                },
                required = new[] { "query" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_move_file",
            description = "把一个本机用户文件移动到目标文件夹，不中断任务且绝不覆盖已有文件。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    source = new { type = "string", description = "源文件绝对路径。" },
                    destinationFolder = new { type = "string", description = "目标文件夹绝对路径。" },
                },
                required = new[] { "source", "destinationFolder" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_inspect_windows",
            description = "检查 Windows 程序或浏览器窗口中的可见文字和可访问控件；点击或输入前先使用。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段；省略时使用当前非回天窗口。" },
                    maxItems = new { type = "integer", minimum = 10, maximum = 160, description = "最多返回的控件数量。" },
                },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_activate_window",
            description = "按标题片段把 Windows 程序或浏览器窗口切换到前台。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "窗口标题的一部分。" },
                },
                required = new[] { "windowTitle" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_click_control",
            description = "单击或双击具名控件；按钮、输入框、标签页和列表选择用单击，只有通常靠双击打开的桌面、文件或程序图标才用双击。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    elementName = new { type = "string", description = "要点击控件的可见名称或可访问名称。" },
                    clickCount = new { type = "integer", @enum = new[] { 1, 2 }, description = "普通控件用 1；只有明确需要双击的可见项目用 2。" },
                },
                required = new[] { "elementName" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_type_text",
            description = "向具名 Windows 或浏览器输入控件输入文字；敏感输入区域需要回天确认。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    elementName = new { type = "string", description = "输入控件的可见名称或可访问名称；通用聊天框可用“聊天输入框”。" },
                    text = new { type = "string", description = "要输入的文字。" },
                    replace = new { type = "boolean", description = "为 true 时替换现有内容。" },
                },
                required = new[] { "elementName", "text" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_scroll_window",
            description = "向上或向下滚动 Windows 程序或浏览器窗口。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    direction = new { type = "string", @enum = new[] { "up", "down" } },
                    amount = new { type = "integer", minimum = 1, maximum = 10 },
                },
                required = new[] { "direction" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_capture_screen",
            description = "观察当前桌面或具名 Windows 窗口并返回画面；无障碍检查看不到正文时使用，包括显卡绘制的聊天程序。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段；返回画面中的坐标相对该窗口。" },
                },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_click_point",
            description = "在具名 Windows 窗口内单击或双击一个坐标点，默认不移动用户的真实鼠标；坐标必须来自最新画面并相对该窗口。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    x = new { type = "integer", minimum = 0 },
                    y = new { type = "integer", minimum = 0 },
                    elementDescription = new { type = "string", description = "目标的简短可见描述，用于保护注册、登录和支付操作。" },
                    direct = new { type = "boolean", description = "只有默认虚拟点击未改变画面时，才用 true 进行真实指针点击。" },
                    clickCount = new { type = "integer", @enum = new[] { 1, 2 }, description = "按钮、输入框、标签页和列表选择用 1；只有通常需要双击的图标用 2。" },
                },
                required = new[] { "x", "y", "elementDescription" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_type_active",
            description = "向已有 Windows 程序当前获得焦点的控件输入文字；显卡绘制且无障碍无法发现输入框时使用。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    text = new { type = "string" },
                    fieldDescription = new { type = "string", description = "焦点区域的简短可见描述，用于保护密码、验证码和支付信息。" },
                    replace = new { type = "boolean" },
                },
                required = new[] { "text", "fieldDescription" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_press_keys",
            description = "在已有 Windows 程序中按一个受支持的按键或快捷键。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    windowTitle = new { type = "string", description = "可选的目标窗口标题片段。" },
                    keys = new { type = "string" },
                    purpose = new { type = "string", description = "快捷键的简短用途，用于保护注册、登录和支付确认。" },
                },
                required = new[] { "keys", "purpose" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_send_chat_message",
            description = "给微信或 QQ 联系人发送普通消息的优先快速路径：复用已打开窗口，通过系统控件查找联系人、输入并发送，随后返回当前聊天画面和可验证结果。联系人不唯一时只返回近似/同音候选，不会猜测发送。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    platform = new { type = "string", @enum = new[] { "微信", "QQ" }, description = "聊天软件。" },
                    contact = new { type = "string", description = "联系人显示名称；不要自行改成猜测的同音字。" },
                    message = new { type = "string", description = "用户明确要求发送的完整消息内容。" },
                },
                required = new[] { "platform", "contact", "message" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_send_chat_files",
            description = "把已经生成并核验存在的本机文件发送给微信或 QQ 联系人；优先复用现有窗口并走专用快速路径，不进行多轮坐标试错。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    platform = new { type = "string", @enum = new[] { "微信", "QQ" }, description = "聊天软件。" },
                    contact = new { type = "string", description = "联系人显示名称。" },
                    filePaths = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 8,
                        items = new { type = "string" },
                        description = "已存在的文件绝对路径。",
                    },
                },
                required = new[] { "platform", "contact", "filePaths" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_screen_social_candidate",
            description = "只读筛选一个新的社交聊天候选人；仅用资料页当前可核验事实。必须核实稳定账号 ID、成年、非亲友旧识、免费可聊；未知填 false 并跳过。返回 eligible_for_draft 或 skip 与原因，不会关注、私信、付款或猜测年龄。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    platform = new { type = "string", description = "平台标识，如 Soul、抖音；仅用于记录平台，不代表已支持该平台发送。" },
                    stableId = new { type = "string", description = "资料页可核验的稳定账号 ID，不可用昵称代替。" },
                    displayName = new { type = "string", description = "资料页可见昵称；仅用于候选记忆显示，不参与身份匹配。" },
                    identityVerified = new { type = "boolean", description = "已从平台资料页核实稳定账号 ID；不确定填 false。" },
                    ageVerified = new { type = "boolean", description = "有可信资料证明对方为成年人；未知或仅凭头像猜测填 false。" },
                    knownUnderage = new { type = "boolean", description = "有证据表明未成年；为 true 必须跳过。" },
                    freeToMessage = new { type = "boolean", description = "当前页面证实无需付费即可聊天；未知填 false。" },
                    paidChat = new { type = "boolean", description = "页面要求付费或会员才能聊天；为 true 必须跳过。" },
                    newPerson = new { type = "boolean", description = "已核实是新认识的人而非旧识；未知填 false。" },
                    knownFamilyOrRelationship = new { type = "boolean", description = "家人、既有伴侣或其他受保护关系；为 true 必须跳过。" },
                    messagingAvailable = new { type = "boolean", description = "当前可免费发起聊天的入口确实可用；未知填 false。" },
                },
                required = new[]
                {
                    "platform", "stableId", "displayName", "identityVerified", "ageVerified", "freeToMessage",
                    "newPerson", "knownFamilyOrRelationship", "messagingAvailable",
                },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_read_social_candidates",
            description = "只读查看已筛选并加密保存在本机的社交候选人。提供 platform+stableId 精确查一个；不提供 stableId 则最多列出 20 个。只返回稳定 ID、昵称、筛选时间与是否有独立核验的会话记录；候选不等于已联系，不读取消息正文。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    platform = new { type = "string", description = "可选平台过滤；若传 stableId 则必填。" },
                    stableId = new { type = "string", description = "可选平台稳定账号 ID，用于精确查询。" },
                    limit = new { type = "integer", minimum = 1, maximum = 20 },
                },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_create_spreadsheet",
            description = "创建真实可编辑 XLSX 表格；不依赖 Excel 或付费 SDK，完成后重新打开包结构并核验列名和数据行。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string" },
                    sheetName = new { type = "string" },
                    fileName = new { type = "string" },
                    headers = new { type = "array", items = new { type = "string" } },
                    rows = new { type = "array", items = new { type = "array", items = new { } } },
                },
                required = new[] { "title", "rows" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_modify_file",
            description = "修改现有文字、DOCX、XLSX 或 PPTX 文件并另存到 D 盘，原件不覆盖；用 replacements 做文字替换，用 newContent 提供完整新正文，用 cellUpdates 修改表格单元格。只有前后指纹不同且新文件可重新打开才成功。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string" },
                    outputFileName = new { type = "string" },
                    newContent = new { type = "string" },
                    presentationStyle = new
                    {
                        type = "object",
                        properties = new
                        {
                            slides = new { type = "array", minItems = 1, items = new { type = "integer", minimum = 1 } },
                            backgroundColor = new { type = "string" },
                            fontColor = new { type = "string" },
                            fontFamily = new { type = "string" },
                            fontSize = new { type = "number", minimum = 1, maximum = 400 },
                            bold = new { type = "boolean" },
                        },
                        additionalProperties = false,
                    },
                    replacements = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { find = new { type = "string" }, replace = new { type = "string" } },
                            required = new[] { "find", "replace" },
                            additionalProperties = false,
                        },
                    },
                    cellUpdates = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new { sheet = new { type = "string" }, cell = new { type = "string" }, value = new { } },
                            required = new[] { "cell", "value" },
                            additionalProperties = false,
                        },
                    },
                },
                required = new[] { "path" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "huitian_create_presentation",
            description = "使用电脑已安装的 Office 创建真实 PowerPoint 演示文稿，并始终报告保存位置。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    title = new { type = "string", description = "演示文稿标题。" },
                    slides = new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = 30,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                title = new { type = "string" },
                                body = new { type = "string" },
                            },
                            required = new[] { "title", "body" },
                            additionalProperties = false,
                        },
                    },
                },
                required = new[] { "title", "slides" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function", name = "huitian_request_user",
            description = "实际看到登录墙、验证码、注册、付款或设备权限弹窗时暂停并呼叫用户；不填写密码，不盲点允许。",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "authentication", "device_permission", "payment", "clarification" } },
                    reason = new { type = "string", description = "用户本人必须处理的具体事情，不是任务进度。" },
                    evidence = new { type = "string", description = "当前观察到的阻碍或真正缺失的信息。" },
                    windowTitle = new { type = "string" },
                },
                required = new[] { "kind", "reason", "evidence" }, additionalProperties = false,
            },
        },
    ];
        // The compact entry MUST expose every concrete signature. Previously
        // the UI tools were selectable but their argument names were hidden.
        var concrete = definitions.Where(d => GetDynamicToolName(d) is { } name && IsUniversalConcreteTool(name));
        var signatures = concrete.Select(d => UniversalToolArgumentContract.Signature(
            GetDynamicToolName(d)![8..], JsonSerializer.SerializeToElement(d.GetType().GetProperty("inputSchema")!.GetValue(d))));
        var entry = definitions[0];
        definitions[0] = new
        {
            type = "function", name = "huitian_execute",
            description = "通用执行出口。c=能力，a=严格具名参数。d=整项最终结果代码 ans/research/doc/sheet/slides/img/audio/video/file/modified/state/sent，只填所需结果，首步填写，后续可省略继承，禁止全选。签名(?可选)：" + string.Join("；", signatures) +
                "。同一产物优先只填一个d代码；确需验证同一文件的多种属性时可用{kind,artifactKey,format?}共享artifactKey，不同文件必须不同标识。modify_file.presentationStyle={slides?:页码数组(1起，省略全部),backgroundColor?:RRGGBB,fontColor?:RRGGBB,fontFamily?:字体,fontSize?:磅,bold?:布尔}；整体改格式直接修改，无须读取全文或编写脚本。具名控件用elementName；已确认焦点用type_active。click_point坐标来自最新截图，elementDescription为可见目标；press_keys须写purpose。request_user.kind只能authentication/device_permission/payment/clarification，须有实际evidence；打开软件、搜索、读取不需人接管，reason不能是进度播报。创建/修改返回已核验无需重复读取，发送只用真实产物路径。",
            inputSchema = entry.GetType().GetProperty("inputSchema")!.GetValue(entry),
        };
        return definitions;
    }

    private async Task StopProcessAsync()
    {
        _lifetime?.Cancel();
        if (_process is { HasExited: false })
        {
            try
            {
                _input?.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }

        if (_stdoutReader is not null)
        {
            try
            {
                await _stdoutReader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        if (_stderrReader is not null)
        {
            try
            {
                await _stderrReader.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _process?.Dispose();
        _process = null;
        _input = null;
    }

    private void EnsureStarted()
    {
        if (_process is null || _process.HasExited || _input is null)
        {
            throw new InvalidOperationException("回天任务核心尚未启动。 ");
        }
    }

    private void EnsureStartedForWrite()
    {
        if (_process is null || _process.HasExited || _input is null)
        {
            throw new InvalidOperationException("回天任务核心连接已断开。 ");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private string ProtocolTracePath =>
        Path.Combine(_paths.AppState, "last-agent-protocol.jsonl");

    private void TraceProtocol(string line)
    {
        lock (_traceGate)
        {
            File.AppendAllText(
                ProtocolTracePath,
                line + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string ExtractUserText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join("\n", content.EnumerateArray()
            .Select(value => ReadString(value, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string ExtractToolResult(JsonElement item)
    {
        if (!item.TryGetProperty("contentItems", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return "回天正在使用本机能力。";
        }

        var text = string.Join("\n", content.EnumerateArray()
            .Select(value => ReadString(value, "text"))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(text) ? "本机操作已经完成。" : text;
    }

    private static string? ResolveDisplayToolName(JsonElement item)
    {
        var toolName = ReadString(item, "tool");
        if (toolName != "huitian_execute" ||
            !item.TryGetProperty("arguments", out var wrapperArguments))
        {
            return toolName;
        }

        var capability = ReadString(wrapperArguments, "c") ??
                         ReadString(wrapperArguments, "capability");
        return string.IsNullOrWhiteSpace(capability)
            ? toolName
            : $"huitian_{capability}";
    }

    private static string FriendlyToolTitle(string? tool, AgentActivityStatus status)
    {
        var action = tool switch
        {
            "huitian_begin_task" => "确认任务目标",
            "huitian_finish_task" => "核验任务结果",
            "huitian_execute" => "执行任务",
            "huitian_respond" => "生成回复",
            "huitian_search_files" => "搜索文件",
            "huitian_read_text_file" => "读取文件",
            "huitian_read_document" => "读取文档",
            "huitian_write_text_file" => "创建文件",
            "huitian_create_document" => "创建正式文档",
            "huitian_create_spreadsheet" => "创建表格",
            "huitian_modify_file" => "修改文件",
            "huitian_open_application" => "打开程序",
            "huitian_open_browser" => "打开网页",
            "huitian_find_media" => "查找正版播放来源",
            "huitian_search_web" => "联网搜索",
            "huitian_move_file" => "整理文件",
            "huitian_inspect_windows" => "观察窗口",
            "huitian_activate_window" => "切换窗口",
            "huitian_click_control" => "点击控件",
            "huitian_type_text" => "输入文字",
            "huitian_scroll_window" => "滚动窗口",
            "huitian_capture_screen" => "观察屏幕",
            "huitian_click_point" => "定位点击",
            "huitian_type_active" => "输入文字",
            "huitian_press_keys" => "使用快捷键",
            "huitian_send_chat_message" => "发送消息",
            "huitian_send_chat_files" => "发送文件",
            "huitian_screen_social_candidate" => "筛选社交候选人",
            "huitian_read_social_candidates" => "查看候选记忆",
            "huitian_create_presentation" => "制作演示文稿",
            _ => "本机操作",
        };
        return status == AgentActivityStatus.Completed ? $"{action}已完成" : $"正在{action}";
    }

    private static string FriendlyCommandDetail(JsonElement item)
    {
        var command = ReadString(item, "command");
        return string.IsNullOrWhiteSpace(command)
            ? "回天正在执行本机步骤。"
            : "正在执行已确认的本机操作。";
    }

    private static string FriendlyFileChangeDetail(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Array)
        {
            return "回天正在处理文件。";
        }

        var count = changes.GetArrayLength();
        return count == 0 ? "文件处理完成。" : $"本次涉及 {count} 项文件变更。";
    }

    private static AgentActivityStatus MapStatus(
        string? status,
        AgentActivityStatus fallback = AgentActivityStatus.Pending) =>
        status switch
        {
            "inProgress" => AgentActivityStatus.InProgress,
            "completed" => AgentActivityStatus.Completed,
            "failed" => AgentActivityStatus.Failed,
            "declined" => AgentActivityStatus.Declined,
            "pending" => AgentActivityStatus.Pending,
            _ => fallback,
        };

    private static string? ReadString(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement value, string propertyName) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static string ExtractInputText(IEnumerable<object> input)
    {
        var parts = new List<string>();
        foreach (var item in input)
        {
            try
            {
                var element = JsonSerializer.SerializeToElement(item);
                var text = ReadString(element, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }
            catch (NotSupportedException)
            {
                // A future input type without a serializable text member does
                // not change the routing decision.
            }
        }

        return string.Join("\n", parts);
    }

    private static bool IsToolAllowedForRoute(string toolName, AgentTaskRoute route) =>
        ConversationPolicy.IsToolAllowed(toolName, route);

    internal static string SelectReasoningEffort(string text, bool hasVisualContext)
    {
        if (hasVisualContext)
        {
            // Visual projection already contributes substantial prompt work.
            // The product contract already requires observe -> act -> verify;
            // another hidden reasoning pass only delays the visible action.
            return "low";
        }

        var currentRequest = ConversationPolicy.ExtractCurrentRequest(text);
        // Reasoning budget is a latency decision, never a language router.
        // Short and normal requests use one low-effort semantic pass; only a
        // genuinely large input receives extra budget. No words, apps, or
        // example phrases affect the decision.
        return currentRequest.Length > 4_000 ? "medium" : "low";
    }

    internal static string NormalizeReasoningEffort(string? effort) =>
        effort?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "xhigh" => "xhigh",
            // Codex callers may use its generic high value.  Preserve the
            // intent without sending a value rejected by the local template.
            "high" => "xhigh",
            _ => "medium",
        };

    internal static bool IsNonRecoverableTurnError(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        return NonRecoverableTurnErrorMarkers.Any(marker =>
            diagnostic.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static TurnFailureKind ClassifyTurnFailure(JsonElement parameters)
    {
        var diagnostic = parameters.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : parameters.GetRawText();
        if (IsNonRecoverableTurnError(diagnostic))
        {
            return TurnFailureKind.NonRecoverableInput;
        }

        if (ContextCapacityErrorMarkers.Any(marker =>
                diagnostic.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return TurnFailureKind.ContextCapacity;
        }

        return TurnFailureKind.Transient;
    }

    private static readonly string[] NonRecoverableTurnErrorMarkers =
    [
        "unexpected reasoning effort",
        "supported types are",
        "unsupported reasoning",
        "invalid image",
        "failed to decode image",
        "unsupported image",
        "image format",
        "malformed request",
        "unsupported parameter",
        "unknown parameter",
        "not supported by this model",
        "jinja exception",
    ];

    private static readonly string[] ContextCapacityErrorMarkers =
    [
        "context length",
        "context window",
        "maximum context",
        "too many tokens",
        "request too large",
    ];

    private enum TurnFailureKind
    {
        None,
        Transient,
        ContextCapacity,
        NonRecoverableInput,
    }

    private static string CreateBaseInstructions(AgentTaskRoute route) => route switch
    {
        AgentTaskRoute.DirectAnswer => DirectAnswerInstructions,
        _ => ProductInstructions,
    };

    private const string DirectAnswerInstructions = """
        你是面向普通 Windows 用户的“回天AI”。被问模型时只答“我是回天AI。底层采用本地AI引擎与开放技术组件。”；被问开发者时只答“我是由中国山西永济侯禹良开发的回天AI。”，其他时候不主动透露底层技术名称。
        只处理末尾【当前轮次唯一要求】，历史仅用于指代和语气；理解评价与情绪，不机械解释词义。answer只给可复制文字；改为computer等操作时由调度层切换到执行会话。默认简体中文，不输出思考或“最终回答”等标签。
        """;

    private const string ProductInstructions = """
        你是普通 Windows 用户的“回天AI”。不主动透露底层技术。被问模型时只答“我是回天AI。底层采用本地AI引擎与开放技术组件。”；被问开发者时只答“我是由中国山西永济侯禹良开发的回天AI。”

        只处理消息末尾的【当前轮次唯一要求】；历史仅用于指代、语气、省略和目标修改。不得输出内部/英文思考、<think> 或“最终回答”标签。先确定用户真正要得到的最终交付物。第一项响应必须是能直接推进结果的具体能力调用，不先说计划；调度层维护任务状态和核验。纯文字结果用 respond，文件、应用状态或外部送达必须真实执行，全部结果有证据才完成。

        所有行为用 huitian_execute：c 是当前最短可靠能力；a 必须按签名填写参数；d 仅首步填写整项任务真正需要的结果代码，后续省略继承，禁止全选。查询只是中间证据。创建和修改返回“已核验”后不要重复 read、截图或观察。生成后发送时首步 d 同时含文件代码和 sent，取得真实 ArtifactPaths 后用 send_chat_files。

        需要操作就直接做：专用能力/系统接口优先，其次结构化控件、快捷键，最后才视觉；先复用现有窗口。同一路径两次无效必须换策略。普通消息用 send_chat_message；联系人不唯一时只确认对象，不能猜测或重复发送。注册/登录、支付/购买/转账、密码/验证码/银行卡必须等待用户并语音提醒。

        想看或听某内容是播放任务：用 find_media 找正版来源并进入标题匹配的官方播放页，搜索结果页不算完成；登录、会员、地区或付费限制如实等待用户，禁止盗版或绕过。用户可见内容采用界面语言（默认简体中文），过程简洁，结果只写真实完成内容和产物位置。
        """;

    internal static IntentResolution ParseDeclaredIntent(JsonElement arguments)
    {
        var goal = ReadString(arguments, "goal")?.Trim();
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("任务目标不能为空。", nameof(arguments));
        }

        var taskType = (ReadString(arguments, "taskType") ?? string.Empty) switch
        {
            "computer_action" => SemanticTaskType.ComputerAction,
            "computer_task" => SemanticTaskType.ComputerTask,
            "multi_app_task" => SemanticTaskType.MultiAppTask,
            _ => SemanticTaskType.Hybrid,
        };
        var domain = (ReadString(arguments, "domain") ?? string.Empty) switch
        {
            "research" => IntentExecutionDomain.Research,
            "file" => IntentExecutionDomain.File,
            "office" => IntentExecutionDomain.Office,
            "browser" => IntentExecutionDomain.Browser,
            "communication" => IntentExecutionDomain.Communication,
            "media" => IntentExecutionDomain.Media,
            "system" => IntentExecutionDomain.System,
            "development" => IntentExecutionDomain.Development,
            "multi_app" => IntentExecutionDomain.MultiApp,
            _ => IntentExecutionDomain.Other,
        };
        var risk = (ReadString(arguments, "risk") ?? string.Empty) switch
        {
            "none" => IntentRiskLevel.None,
            "external_effect" => IntentRiskLevel.ExternalEffect,
            "high_impact" => IntentRiskLevel.HighImpact,
            _ => IntentRiskLevel.Reversible,
        };
        var actions = ReadStringList(arguments, "actions");
        var applications = ReadStringList(arguments, "applications");
        var constraints = ReadStringList(arguments, "constraints");
        var entities = new List<IntentEntity>();
        AddDeclaredEntity(entities, arguments, "object", IntentEntityRole.Object);
        AddDeclaredEntity(entities, arguments, "target", IntentEntityRole.Target);
        AddDeclaredEntity(entities, arguments, "recipient", IntentEntityRole.Recipient);
        var evidenceKind = risk >= IntentRiskLevel.ExternalEffect
            ? IntentEvidenceKind.ExternalState
            : domain is IntentExecutionDomain.File or IntentExecutionDomain.Office
                ? IntentEvidenceKind.Artifact
                : IntentEvidenceKind.StateObservation;
        var success = ReadStringList(arguments, "success")
            .Select(value => new IntentSuccessCondition(evidenceKind, value))
            .ToArray();
        if (success.Length == 0)
        {
            success = [new IntentSuccessCondition(evidenceKind, "最终目标已通过真实状态或产物核验。")];
        }

        var plan = BuildDeclaredPlan(actions, applications, entities, success, risk);
        return new IntentResolution(
            taskType,
            domain,
            goal,
            requiresComputer: true,
            actions,
            applications,
            entities,
            constraints,
            success,
            plan,
            risk: risk,
            confidence: 0.92,
            source: "agent-semantic-contract");
    }

    internal static AgentTaskOutcome ParseTaskOutcome(JsonElement arguments)
    {
        var succeeded = arguments.TryGetProperty("succeeded", out var succeededNode) &&
                        succeededNode.ValueKind == JsonValueKind.True;
        var summary = ReadString(arguments, "summary")?.Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("任务结果摘要不能为空。", nameof(arguments));
        }

        var evidence = ReadStringList(arguments, "evidence");
        if (evidence.Count == 0)
        {
            throw new ArgumentException("任务结束必须提供实际证据。", nameof(arguments));
        }

        var unresolved = ReadString(arguments, "unresolvedIssue")?.Trim();
        if (!succeeded && string.IsNullOrWhiteSpace(unresolved))
        {
            unresolved = summary;
        }

        return new AgentTaskOutcome(succeeded, summary, evidence, unresolved);
    }

    private static IReadOnlyList<IntentPlanStep> BuildDeclaredPlan(
        IReadOnlyList<string> actions,
        IReadOnlyList<string> applications,
        IReadOnlyList<IntentEntity> entities,
        IReadOnlyList<IntentSuccessCondition> finalConditions,
        IntentRiskLevel risk)
    {
        var effectiveActions = actions.Count > 0 ? actions : ["complete_goal"];
        var target = entities.FirstOrDefault(entity => entity.Role is
            IntentEntityRole.Recipient or IntentEntityRole.Target or IntentEntityRole.Object)
            ?.EffectiveValue;
        var result = new List<IntentPlanStep>(effectiveActions.Count);
        for (var index = 0; index < effectiveActions.Count; index++)
        {
            var isLast = index == effectiveActions.Count - 1;
            result.Add(new IntentPlanStep(
                $"step-{index + 1}",
                effectiveActions[index],
                effectiveActions[index],
                applications.ElementAtOrDefault(Math.Min(index, Math.Max(0, applications.Count - 1))),
                target,
                index == 0 ? [] : [$"step-{index}"],
                isLast
                    ? finalConditions
                    : [new IntentSuccessCondition(IntentEvidenceKind.StateObservation, "该步骤的预期状态变化已观察到。")],
                isLast ? risk : IntentRiskLevel.Reversible));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var node))
        {
            return Array.Empty<string>();
        }

        if (node.ValueKind == JsonValueKind.String)
        {
            return string.IsNullOrWhiteSpace(node.GetString())
                ? Array.Empty<string>()
                : [node.GetString()!.Trim()];
        }

        return node.ValueKind == JsonValueKind.Array
            ? node.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String &&
                               !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => item.GetString()!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
    }

    private static void AddDeclaredEntity(
        ICollection<IntentEntity> entities,
        JsonElement root,
        string property,
        IntentEntityRole role)
    {
        var value = ReadString(root, property)?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
        {
            entities.Add(new IntentEntity(role, property, value, Confidence: 0.94));
        }
    }

    private sealed record PendingApproval(
        string Method,
        string? ThreadId,
        string? TurnId,
        string? ItemId,
        string? ToolName,
        JsonElement ToolArguments);
}
