using System.Xml.Linq;

var projectRoot = FindProjectRoot();
var mainWindowPath = Path.Combine(projectRoot, "src", "HuiTian.App", "MainWindow.xaml.cs");
var appServerClientPath = Path.Combine(
    projectRoot,
    "src",
    "HuiTian.Infrastructure",
    "CodexAppServerClient.cs");
var xamlPath = Path.Combine(projectRoot, "src", "HuiTian.App", "MainWindow.xaml");
var localizerPath = Path.Combine(projectRoot, "src", "HuiTian.App", "UiLocalizer.cs");
var statePath = Path.Combine(projectRoot, "src", "HuiTian.App", "ProductStateStore.cs");
var source = File.ReadAllText(mainWindowPath);
var appServerClientSource = File.ReadAllText(appServerClientPath);

Require(source.Contains("ResolveProcessGroup(activity)", StringComparison.Ordinal),
    "Process events are not grouped into one turn timeline.");
Require(source.Contains("GenericReasoningVisual", StringComparison.Ordinal),
    "Generic reasoning rows are not being collapsed.");
Require(source.Contains("presentation.IsRecovery", StringComparison.Ordinal),
    "Transient recovery notices are not separated from task steps.");
Require(source.Contains("AwaitingRecoveryTurn", StringComparison.Ordinal) &&
        source.Contains("recovering.Value.BoundTurnId = activity.TurnId", StringComparison.Ordinal),
    "A recovery turn is not rebound to the unfinished process group.");
Require(source.Contains("group.AwaitingRecoveryTurn = presentation.IsRecovery", StringComparison.Ordinal) &&
        source.Contains("group.AwaitingRecoveryTurn = false", StringComparison.Ordinal),
    "Only an explicitly recovering task may reuse its process group.");
Require(source.Contains("NormalizeAssistantAnswer", StringComparison.Ordinal),
    "Assistant answers are not separated from reasoning text.");
Require(!source.Contains("Text = \"回天\",", StringComparison.Ordinal),
    "The final answer still renders a repeated HuiTian label.");
Require(source.Contains("rawDetail: activity.Detail", StringComparison.Ordinal),
    "Streaming answer state is not isolated per activity.");
Require(source.Contains("_activityViewGeneration", StringComparison.Ordinal) &&
        source.Contains("_detachedFromActiveTurn", StringComparison.Ordinal) &&
        source.Contains("HandleBackgroundActivity(e.Activity)", StringComparison.Ordinal),
    "A background turn can still overwrite the newly selected task view.");
Require(source.Contains("_sendWhenBackgroundEnds", StringComparison.Ordinal) &&
        source.Contains("新任务已排队，上一项结束后自动发送", StringComparison.Ordinal) &&
        source.Contains("await SendCurrentMessageAsync();", StringComparison.Ordinal),
    "A message entered in a new task can still be ignored while the previous task finishes.");
Require(source.Contains("ActivityScroll_OnScrollChanged", StringComparison.Ordinal) &&
        source.Contains("if (shouldFollowTail)", StringComparison.Ordinal) &&
        source.Contains("IsActivityAtTail", StringComparison.Ordinal),
    "Streaming content still forces the conversation to the bottom while the user reads history.");
Require(source.Contains("IsReadOnlyCaretVisible = true", StringComparison.Ordinal) &&
        source.Contains("Cursor = Cursors.IBeam", StringComparison.Ordinal),
    "Conversation text is not mouse-selectable.");
Require(source.Contains("TryNavigateInputHistory", StringComparison.Ordinal) &&
        source.Contains("GetLineIndexFromCharacterIndex", StringComparison.Ordinal),
    "The composer does not provide multiline-safe Up/Down input history.");
Require(source.Contains("精简任务连接", StringComparison.Ordinal) &&
        source.Contains("group.FinalDuration", StringComparison.Ordinal) &&
        source.Contains("_userRequestedStop", StringComparison.Ordinal),
    "Recovery/stop rows can still duplicate or keep counting after the turn stops.");
Require(source.Contains("ConversationPolicy.BuildNaturalTaskTitle", StringComparison.Ordinal) &&
        !source.Contains("等 {distinctRequests.Length} 项", StringComparison.Ordinal),
    "Recent-task titles still use a mechanical item counter.");
Require(source.Contains("TryHandleApplicationFastPathAsync(displayText)", StringComparison.Ordinal) &&
        source.Contains("WindowsApplicationFastPath", StringComparison.Ordinal) &&
        source.IndexOf("TryHandleApplicationFastPathAsync(displayText)", StringComparison.Ordinal) <
        source.IndexOf("if (!_agentConnected || !_brainReady)", StringComparison.Ordinal),
    "A single application launch still waits for model planning instead of using the product fast path.");
Require(source.Contains("TryHandleEverydayFastPathAsync(displayText)", StringComparison.Ordinal) &&
        source.IndexOf("TryHandleEverydayFastPathAsync(displayText)", StringComparison.Ordinal) <
        source.IndexOf("if (!_agentConnected || !_brainReady)", StringComparison.Ordinal) &&
        source.Contains("unfamiliar wording or multi-step goals continue", StringComparison.Ordinal),
    "Bounded browser, folder, media-key and volume actions do not start immediately or lack a universal fallback.");
Require(source.Contains("var quickTaskCancellation = new CancellationTokenSource", StringComparison.Ordinal) &&
        source.Contains("ReferenceEquals(_quickTaskCancellation, quickTaskCancellation)", StringComparison.Ordinal) &&
        source.Contains("_detachedFromActiveTurn = hasAgentBackgroundTurn", StringComparison.Ordinal),
    "A detached product fast path can still clear a newer task's cancellation source or steal its progress events.");
Require(source.Contains("ConversationPolicy.BuildUniversalTurnContract(displayText)", StringComparison.Ordinal) &&
        source.Contains("_agentClient.IntentDeclared += AgentClient_OnIntentDeclared", StringComparison.Ordinal) &&
        source.Contains("var context = priorMessages.Count > 0", StringComparison.Ordinal) &&
        source.Contains("AgentTaskRoute.DirectAnswer ? 900 : 1600", StringComparison.Ordinal),
    "The universal semantic turn can lose recent context or fail to accept the agent-declared task contract.");
Require(source.Contains("_currentIntentResolution is { RequiresComputer: true }", StringComparison.Ordinal),
    "A declared computer action can still skip the screen-operation preparation path.");

var mainMessageEntry = SliceBetween(
    source,
    "private async Task SendCurrentMessageAsync(",
    "private static TaskGraph CreateTaskGraphSafely(");
Require(mainMessageEntry.Contains("ResolveTurnRouteAsync(displayText, priorMessages, selectedFiles)", StringComparison.Ordinal) &&
        mainMessageEntry.Contains("CreateThreadAsync(_currentTurnRoute)", StringComparison.Ordinal) &&
        mainMessageEntry.Contains("if (_currentTurnRoute != AgentTaskRoute.DirectAnswer)", StringComparison.Ordinal),
    "The tested semantic decision must be wired to the real message entry, thread route and optional desktop read.");
var semanticEntry = File.ReadAllText(Path.Combine(projectRoot, "src", "HuiTian.App", "MainWindow.SemanticRouting.cs"));
Require(mainMessageEntry.Contains("var submittedTask = _currentTask;", StringComparison.Ordinal) &&
        mainMessageEntry.Contains("submittedTask.ThreadId = _agentClient.CurrentThreadId;", StringComparison.Ordinal),
    "A tool-upgrade thread must be persisted on the submitted task, not a newly selected task.");
Require(mainMessageEntry.IndexOf("var submittedTask = _currentTask;", StringComparison.Ordinal) <
        mainMessageEntry.IndexOf("await _taskContextBootstrapper.CaptureAsync", StringComparison.Ordinal) &&
        mainMessageEntry.Contains("if (!IsCurrentSubmission()) return;", StringComparison.Ordinal) &&
        SliceBetween(source, "private async void NewTaskButton_OnClick", "private async void SendButton_OnClick")
            .Contains("Interlocked.Increment(ref _submissionGeneration)", StringComparison.Ordinal) &&
        SliceBetween(source, "private async void StopButton_OnClick", "private async void ApproveButton_OnClick")
            .Contains("Interlocked.Increment(ref _submissionGeneration)", StringComparison.Ordinal),
    "Preparation must retain the submitted task and invalidate it on new task/Stop, not just cancel classification.");
Require(semanticEntry.Contains("ReferenceEquals(task, _currentTask)", StringComparison.Ordinal) &&
        semanticEntry.Contains("cancellation.Token", StringComparison.Ordinal) &&
        source.Contains("_intentCancellation?.Cancel()", StringComparison.Ordinal),
    "A late intent result must not restart work in a newly selected task or ignore Stop.");
AssertNoLegacyNaturalLanguageRouting(
    mainMessageEntry,
    "SendCurrentMessageAsync mainline");

var createThread = SliceBetween(
    appServerClientSource,
    "public async Task<string> CreateThreadAsync(",
    "public async Task SendMessageAsync(");
AssertNoLegacyNaturalLanguageRouting(createThread, "CreateThreadAsync");

var steer = SliceBetween(
    appServerClientSource,
    "public async Task SteerAsync(",
    "private async Task StartTurnAsync(");
AssertNoLegacyNaturalLanguageRouting(steer, "SteerAsync");
Require(steer.Contains("ConversationPolicy.FindDeclaredRoute(steerText)", StringComparison.Ordinal) &&
        steer.Contains("AgentTaskRoute.Composite", StringComparison.Ordinal),
    "Steering no longer defaults unseen natural language to the universal semantic route.");

var sendMessageFallback = SliceBetween(
    appServerClientSource,
    "public async Task SendMessageAsync(",
    "public async Task SendMessageWithLocalImageAsync(");
var attachmentFallback = SliceBetween(
    appServerClientSource,
    "public async Task SendMessageWithAttachmentsAsync(",
    "public async Task SteerAsync(");
AssertNoLegacyNaturalLanguageRouting(sendMessageFallback, "SendMessageAsync thread fallback");
AssertNoLegacyNaturalLanguageRouting(attachmentFallback, "attachment thread fallback");
Require(sendMessageFallback.Contains("ConversationPolicy.FindDeclaredRoute(text)", StringComparison.Ordinal) &&
        sendMessageFallback.Contains("AgentTaskRoute.Composite", StringComparison.Ordinal) &&
        attachmentFallback.Contains("ConversationPolicy.FindDeclaredRoute(text)", StringComparison.Ordinal) &&
        attachmentFallback.Contains("AgentTaskRoute.Composite", StringComparison.Ordinal),
    "A new thread fallback can still classify natural language instead of using the universal route.");

_ = XDocument.Load(xamlPath);
var xaml = File.ReadAllText(xamlPath);
Require(xaml.Contains("x:Name=\"StartupStatusPanel\"", StringComparison.Ordinal) &&
        xaml.Contains("x:Name=\"SendButton\"", StringComparison.Ordinal) &&
        xaml.Contains("Click=\"SendButton_OnClick\" IsEnabled=\"False\"", StringComparison.Ordinal),
    "Startup readiness is not visible or sending is enabled before the core is ready.");
Require(xaml.Contains("ScrollChanged=\"ActivityScroll_OnScrollChanged\"", StringComparison.Ordinal),
    "The conversation scroll viewer is not tracking user scroll position.");
Require(xaml.Contains("聊天与电脑执行已合并，无需切换模式；这里只选择执行前确认的严格程度。", StringComparison.Ordinal) &&
        !xaml.Contains("这是回天的固定能力说明", StringComparison.Ordinal) &&
        xaml.Contains("无论选择哪种模式，付款、转账和账号安全等高风险操作都必须确认。", StringComparison.Ordinal),
    "Autonomy settings do not clearly explain unified chat/computer work and confirmation scope.");
var localizerSource = File.ReadAllText(localizerPath);
Require(Count(localizerSource, "[\"执行自主程度\"]") == 2 &&
        Count(localizerSource, "[\"谨慎模式（更多操作会询问）\"]") == 2 &&
        Count(localizerSource, "[\"标准模式（普通操作直接完成）\"]") == 2 &&
        Count(localizerSource, "[\"高自主模式（推荐）\"]") == 2,
    "Autonomy settings are not localized for every currently supported non-Chinese language.");
var stateSource = File.ReadAllText(statePath);
Require(stateSource.Contains("Theme { get; set; } = \"Dark\"", StringComparison.Ordinal) &&
        stateSource.Contains("VisualThemeVersion", StringComparison.Ordinal) &&
        stateSource.Contains("state.VisualThemeVersion < 1", StringComparison.Ordinal),
    "The personal edition's dark appearance or one-time legacy theme migration is missing.");
Require(source.ReplaceLineEndings("\n").Contains(
            "_pendingTheme = _state.Theme;\n        ApplyTheme(_state.Theme);",
            StringComparison.Ordinal) &&
        source.Contains("brush.Color = color;", StringComparison.Ordinal) &&
        source.Contains("ApplyTitleBarTheme(theme);", StringComparison.Ordinal),
    "Theme cancellation, existing card brushes, or the native title bar can diverge from the selected palette.");

Console.WriteLine("PASS: compact process timeline, isolated final answer, and personal dark UI contracts.");

static string FindProjectRoot()
{
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "HuiTian.sln")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "HuiTian.sln")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate HuiTian.sln.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}

static int Count(string source, string value)
{
    var count = 0;
    var offset = 0;
    while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += value.Length;
    }

    return count;
}

static string SliceBetween(string source, string startMarker, string endMarker)
{
    var start = source.IndexOf(startMarker, StringComparison.Ordinal);
    if (start < 0)
    {
        throw new InvalidDataException($"Missing source marker: {startMarker}");
    }

    var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
    if (end < 0)
    {
        throw new InvalidDataException($"Missing source marker: {endMarker}");
    }

    return source[start..end];
}

static void AssertNoLegacyNaturalLanguageRouting(string methodSource, string boundaryName)
{
    string[] forbidden =
    [
        "ConversationPolicy.Classify(",
        "ConversationPolicy.TryParseCompositeTask(",
        "TryHandleMediaFastPathAsync(",
        "TryHandleCompositeFastPathAsync(",
        "EverydayTaskParser.TryParse(",
        "MediaConsumptionIntent.TryParse(",
        "ChatFastPathParser.TryParse(",
    ];

    foreach (var token in forbidden)
    {
        Require(!methodSource.Contains(token, StringComparison.Ordinal),
            $"{boundaryName} calls legacy natural-language router: {token}");
    }
}
