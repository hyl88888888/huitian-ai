using HuiTian.Core;
using System.Text.Json;

var evidence = new CompletionEvidenceRequirement(
    CompletionEvidenceKind.StateObservation,
    "  exported file exists  ");
var contract = new ComputerTaskContract(
    mode: ComputerTaskMode.Operate,
    goal: "  Export the active report  ",
    operation: "  export  ",
    target: "  quarterly report  ",
    app: "  Spreadsheet  ",
    source: "  active workbook  ",
    constraints: [" preserve formulas ", "PRESERVE FORMULAS", " do not overwrite "],
    completionEvidence: [evidence],
    risk: ComputerTaskRisk.Medium,
    duration: TimeSpan.FromMinutes(2),
    stopCondition: "  artifact verified  ");

Assert(contract.Mode == ComputerTaskMode.Operate, "mode should be retained");
Assert(contract.Goal == "Export the active report", "goal should be normalized");
Assert(contract.Target == "quarterly report", "target should be retained");
Assert(contract.App == "Spreadsheet", "app should be retained");
Assert(contract.Source == "active workbook", "source should be retained");
Assert(contract.Operation == "export", "operation should be normalized");
Assert(
    contract.Constraints.SequenceEqual(["preserve formulas", "do not overwrite"]),
    "constraints should be trimmed and de-duplicated");
Assert(
    contract.CompletionEvidence.Count == 1 &&
    contract.CompletionEvidence[0].Description == "exported file exists",
    "completion evidence should be explicit and immutable");
Assert(contract.Risk == ComputerTaskRisk.Medium, "risk should be retained");
Assert(contract.Duration == TimeSpan.FromMinutes(2), "duration should be retained");
Assert(contract.StopCondition == "artifact verified", "stop condition should be retained");
Assert(!contract.Ambiguity.RequiresClarification, "a resolved task should not be ambiguous");
Assert(contract.IsReadyForExecution, "a complete one-shot contract should be executable");

var routeDefaults = new[]
{
    (AgentTaskRoute.Research, ComputerTaskMode.Observe, ComputerTaskOperations.Research,
        CompletionEvidenceKind.ReturnValue),
    (AgentTaskRoute.FileWork, ComputerTaskMode.Operate, ComputerTaskOperations.FileWork,
        CompletionEvidenceKind.Artifact),
    (AgentTaskRoute.Presentation, ComputerTaskMode.Operate, ComputerTaskOperations.Presentation,
        CompletionEvidenceKind.Artifact),
    (AgentTaskRoute.ComputerControl, ComputerTaskMode.Operate, ComputerTaskOperations.ComputerControl,
        CompletionEvidenceKind.StateObservation),
    (AgentTaskRoute.Composite, ComputerTaskMode.Mixed, ComputerTaskOperations.Composite,
        CompletionEvidenceKind.StateObservation),
};
foreach (var (agentRoute, expectedMode, expectedOperation, expectedEvidence) in routeDefaults)
{
    var mapped = ComputerTaskContract.FromRoute(agentRoute, "  keep this exact goal  ");
    Assert(
        mapped.Mode == expectedMode &&
        mapped.Goal == "keep this exact goal" &&
        mapped.Operation == expectedOperation &&
        mapped.Source == "conversation" &&
        mapped.CompletionEvidence.Single().Kind == expectedEvidence &&
        mapped.IsReadyForExecution,
        $"{agentRoute} should map to a complete contract without parsing goal text");
}
AssertThrows<ArgumentException>(
    () => ComputerTaskContract.FromRoute(AgentTaskRoute.DirectAnswer, "answer only"),
    "direct answers should not be mislabeled as computer tasks");

using (var agentContractJson = JsonDocument.Parse(contract.FormatForAgent()))
{
    var formatted = agentContractJson.RootElement;
    Assert(
        formatted.GetProperty("mode").GetString() == "operate" &&
        formatted.GetProperty("goal").GetString() == "Export the active report" &&
        formatted.GetProperty("target").GetString() == "quarterly report" &&
        formatted.GetProperty("app").GetString() == "Spreadsheet" &&
        formatted.GetProperty("source").GetString() == "active workbook" &&
        formatted.GetProperty("operation").GetString() == "export" &&
        formatted.GetProperty("constraints").GetArrayLength() == 2 &&
        formatted.GetProperty("completion_evidence")[0]
            .GetProperty("kind").GetString() == "state_observation" &&
        formatted.GetProperty("risk").GetString() == "medium" &&
        formatted.GetProperty("duration").GetString() == "00:02:00" &&
        formatted.GetProperty("stop_condition").GetString() == "artifact verified" &&
        !formatted.GetProperty("ambiguity")
            .GetProperty("requires_clarification").GetBoolean(),
        "agent formatting should expose every contract field as escaped JSON");
}

var unboundedMonitor = CreateTask(
    mode: ComputerTaskMode.Monitor,
    app: "Spreadsheet",
    operation: "watch");
Assert(
    !unboundedMonitor.IsReadyForExecution &&
    unboundedMonitor.GetExecutionBlockers().Any(item =>
        item.Contains("duration or stop condition", StringComparison.Ordinal)),
    "monitoring must never start without a duration or stop condition");

var boundedMonitor = CreateTask(
    mode: ComputerTaskMode.Monitor,
    app: "Spreadsheet",
    operation: "watch",
    stopCondition: "status becomes complete");
Assert(boundedMonitor.IsReadyForExecution, "a bounded monitor should be executable");

AssertThrows<ArgumentOutOfRangeException>(
    () => CreateTask(duration: TimeSpan.Zero),
    "zero duration should be rejected");
AssertThrows<ArgumentException>(
    () => new ComputerTaskContract(
        ComputerTaskMode.Operate,
        " ",
        "export",
        completionEvidence: [evidence]),
    "an empty goal should be rejected");

var autonomyMatrix = new[]
{
    (AutonomyMode.Cautious, ComputerTaskRisk.None, AutonomyDecisionKind.Proceed),
    (AutonomyMode.Cautious, ComputerTaskRisk.Low, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.Cautious, ComputerTaskRisk.Medium, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.Cautious, ComputerTaskRisk.High, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.Cautious, ComputerTaskRisk.Critical, AutonomyDecisionKind.Block),
    (AutonomyMode.Standard, ComputerTaskRisk.None, AutonomyDecisionKind.Proceed),
    (AutonomyMode.Standard, ComputerTaskRisk.Low, AutonomyDecisionKind.Proceed),
    (AutonomyMode.Standard, ComputerTaskRisk.Medium, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.Standard, ComputerTaskRisk.High, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.Standard, ComputerTaskRisk.Critical, AutonomyDecisionKind.Block),
    (AutonomyMode.HighAutonomy, ComputerTaskRisk.None, AutonomyDecisionKind.Proceed),
    (AutonomyMode.HighAutonomy, ComputerTaskRisk.Low, AutonomyDecisionKind.Proceed),
    (AutonomyMode.HighAutonomy, ComputerTaskRisk.Medium, AutonomyDecisionKind.Proceed),
    (AutonomyMode.HighAutonomy, ComputerTaskRisk.High, AutonomyDecisionKind.RequireApproval),
    (AutonomyMode.HighAutonomy, ComputerTaskRisk.Critical, AutonomyDecisionKind.Block),
};
foreach (var (autonomyMode, risk, expectedKind) in autonomyMatrix)
{
    var decision = AutonomyRiskPolicy.Evaluate(autonomyMode, risk);
    Assert(
        decision.Mode == autonomyMode &&
        decision.Risk == risk &&
        decision.Kind == expectedKind &&
        decision.CanExecuteImmediately == (expectedKind == AutonomyDecisionKind.Proceed) &&
        decision.RequiresUserInput ==
            (expectedKind is AutonomyDecisionKind.RequireApproval or
                AutonomyDecisionKind.RequireClarification) &&
        !string.IsNullOrWhiteSpace(decision.Reason),
        $"{autonomyMode}/{risk} should resolve to {expectedKind}");
}

var mediumRiskTask = CreateTask(risk: ComputerTaskRisk.Medium);
Assert(
    AutonomyRiskPolicy.Evaluate(AutonomyMode.Standard, mediumRiskTask).Kind ==
        AutonomyDecisionKind.RequireApproval &&
    AutonomyRiskPolicy.Evaluate(AutonomyMode.HighAutonomy, mediumRiskTask).Kind ==
        AutonomyDecisionKind.Proceed,
    "contract evaluation should apply the selected autonomy threshold");

var ambiguousAutonomyTask = CreateTask(
    risk: ComputerTaskRisk.None,
    ambiguity: ComputerTaskAmbiguity.Unresolved(
        "multiple targets match",
        ["first target", "second target"],
        "Which target should be used?"));
var clarificationDecision = AutonomyRiskPolicy.Evaluate(
    AutonomyMode.HighAutonomy,
    ambiguousAutonomyTask);
Assert(
    clarificationDecision.Kind == AutonomyDecisionKind.RequireClarification &&
    clarificationDecision.Reason == "Which target should be used?" &&
    !clarificationDecision.CanExecuteImmediately &&
    clarificationDecision.RequiresUserInput,
    "high autonomy must not guess through task ambiguity");

var invalidAutonomyDecision = AutonomyRiskPolicy.Evaluate(
    AutonomyMode.HighAutonomy,
    unboundedMonitor);
Assert(
    invalidAutonomyDecision.Kind == AutonomyDecisionKind.Block &&
    invalidAutonomyDecision.Reason.Contains(
        "duration or stop condition",
        StringComparison.Ordinal) &&
    !invalidAutonomyDecision.CanExecuteImmediately,
    "high autonomy must not bypass contract execution blockers");

AssertThrows<ArgumentNullException>(
    () => AutonomyRiskPolicy.Evaluate(AutonomyMode.Standard, null!),
    "a missing task contract should be rejected");
AssertThrows<ArgumentOutOfRangeException>(
    () => AutonomyRiskPolicy.Evaluate((AutonomyMode)999, ComputerTaskRisk.Low),
    "an unknown autonomy mode should be rejected");
AssertThrows<ArgumentOutOfRangeException>(
    () => AutonomyRiskPolicy.Evaluate(AutonomyMode.Standard, (ComputerTaskRisk)999),
    "an unknown risk should be rejected");

var registry = new ApplicationCapabilityRegistry();
registry.Register(new ApplicationCapability(
    "visual-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.Visual,
    "visual.executor"));
registry.Register(new ApplicationCapability(
    "shortcut-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.Shortcut,
    "shortcut.executor"));
registry.Register(new ApplicationCapability(
    "dom-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.DomOrUia,
    "uia.executor"));
registry.Register(new ApplicationCapability(
    "adapter-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.AppAdapter,
    "adapter.executor"));
registry.Register(new ApplicationCapability(
    "native-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.Native,
    "native.executor"));
registry.Register(new ApplicationCapability(
    "api-export",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.Api,
    "api.executor"));

var orderedChannels = registry.FindCapabilities(contract)
    .Select(capability => capability.Channel)
    .ToArray();
Assert(
    orderedChannels.SequenceEqual(
    [
        ComputerTaskChannel.Api,
        ComputerTaskChannel.Native,
        ComputerTaskChannel.AppAdapter,
        ComputerTaskChannel.DomOrUia,
        ComputerTaskChannel.Shortcut,
        ComputerTaskChannel.Visual,
    ]),
    "registry lookup must prefer API, native, app adapter, DOM/UIA, shortcut, then visual");

var specificityRegistry = new ApplicationCapabilityRegistry();
specificityRegistry.Register(new ApplicationCapability(
    "generic-api",
    ApplicationCapability.Any,
    ApplicationCapability.Any,
    ComputerTaskChannel.Api,
    "generic.executor"));
specificityRegistry.Register(new ApplicationCapability(
    "exact-api",
    "spreadsheet",
    "EXPORT",
    ComputerTaskChannel.Api,
    "exact.executor"));
specificityRegistry.Register(new ApplicationCapability(
    "low-risk-native",
    "Spreadsheet",
    "export",
    ComputerTaskChannel.Native,
    "safe.executor",
    task => task.Risk <= ComputerTaskRisk.Medium));
Assert(
    specificityRegistry.FindCapabilities(contract)[0].Id == "exact-api",
    "exact app and operation matches should win within a channel");
var highRiskContract = CreateTask(
    app: "Spreadsheet",
    operation: "export",
    risk: ComputerTaskRisk.High);
Assert(
    specificityRegistry.FindCapabilities(highRiskContract)
        .All(capability => capability.Id != "low-risk-native"),
    "a capability predicate should be able to enforce runtime and risk constraints");
AssertThrows<InvalidOperationException>(
    () => specificityRegistry.Register(new ApplicationCapability(
        "EXACT-API",
        "Spreadsheet",
        "export",
        ComputerTaskChannel.Api,
        "duplicate.executor")),
    "capability ids should be unique case-insensitively");

IComputerTaskChannelSelector selector =
    new PriorityComputerTaskChannelSelector(registry);
var attempts = new ComputerTaskAttemptState();
var route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Api, "api-export", 0);

Assert(attempts.RecordFailure(route) == 1, "the first API failure should be recorded");
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Api, "api-export", 1);

Assert(attempts.RecordFailure(route) == 2, "the second API failure should be recorded");
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Native, "native-export", 0);

ExhaustChannel(selector, contract, attempts, ComputerTaskChannel.Native);
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.AppAdapter, "adapter-export", 0);

ExhaustChannel(selector, contract, attempts, ComputerTaskChannel.AppAdapter);
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.DomOrUia, "dom-export", 0);

ExhaustChannel(selector, contract, attempts, ComputerTaskChannel.DomOrUia);
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Shortcut, "shortcut-export", 0);

ExhaustChannel(selector, contract, attempts, ComputerTaskChannel.Shortcut);
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Visual, "visual-export", 0);

ExhaustChannel(selector, contract, attempts, ComputerTaskChannel.Visual);
route = selector.Select(contract, attempts);
Assert(
    route.Status == ComputerTaskRouteStatus.ChannelsExhausted &&
    route.Capability is null,
    "two failures on every matching channel should stop instead of retrying visually forever");

attempts.RecordSuccess(ComputerTaskChannel.Api);
route = selector.Select(contract, attempts);
AssertSelected(route, ComputerTaskChannel.Api, "api-export", 0);

var ambiguousTask = CreateTask(
    app: "Spreadsheet",
    operation: "export",
    ambiguity: ComputerTaskAmbiguity.Unresolved(
        "multiple workbooks match",
        ["Budget", "Budget copy"],
        "Which workbook should be exported?"));
route = selector.Select(ambiguousTask, new ComputerTaskAttemptState());
Assert(
    route.Status == ComputerTaskRouteStatus.RequiresClarification &&
    route.Detail == "Which workbook should be exported?" &&
    route.Capability is null,
    "ambiguity must block execution before capability selection");

route = selector.Select(unboundedMonitor, new ComputerTaskAttemptState());
Assert(
    route.Status == ComputerTaskRouteStatus.InvalidContract,
    "an invalid execution contract should not reach an executor");

route = selector.Select(
    CreateTask(app: "UnknownApp", operation: "unknown-operation"),
    new ComputerTaskAttemptState());
Assert(
    route.Status == ComputerTaskRouteStatus.NoCapability,
    "a valid but unsupported contract should report no capability");

Assert(
    specificityRegistry.Unregister("EXACT-api") &&
    specificityRegistry.Snapshot().All(item => item.Id != "exact-api"),
    "capabilities should be removable when an executor becomes unavailable");

var semanticIntent = new IntentResolution(
    SemanticTaskType.MultiAppTask,
    IntentExecutionDomain.MultiApp,
    "Prepare the active report and deliver it to the intended recipient",
    requiresComputer: true,
    actions: ["analyze", "export", "send"],
    applications: ["Spreadsheet", "Communication"],
    entities:
    [
        new IntentEntity(IntentEntityRole.File, "spreadsheet", "active report"),
        new IntentEntity(IntentEntityRole.Recipient, "person", "recipient"),
    ],
    constraints: ["preserve formulas", "use the active conversation"],
    successConditions:
    [
        new IntentSuccessCondition(IntentEvidenceKind.Artifact, "The report artifact exists."),
        new IntentSuccessCondition(IntentEvidenceKind.ExternalState, "The artifact is visible in the intended conversation."),
    ],
    plan:
    [
        new IntentPlanStep(
            "analyze",
            "Analyze the active report",
            "analyze",
            "Spreadsheet",
            "active report",
            [],
            [new IntentSuccessCondition(IntentEvidenceKind.ReturnValue, "Analysis is available.")]),
        new IntentPlanStep(
            "export",
            "Export the verified report",
            "export",
            "Spreadsheet",
            "active report",
            ["analyze"],
            [new IntentSuccessCondition(IntentEvidenceKind.Artifact, "Exported report exists.")]),
        new IntentPlanStep(
            "deliver",
            "Deliver the report",
            "send",
            "Communication",
            "recipient",
            ["export"],
            [new IntentSuccessCondition(IntentEvidenceKind.ExternalState, "Correct report is visible in the correct conversation.")],
            IntentRiskLevel.ExternalEffect),
    ],
    risk: IntentRiskLevel.ExternalEffect,
    confidence: 0.94,
    source: "test-semantic-engine",
    elapsedMilliseconds: 18);

Assert(semanticIntent.ToAgentRoute() == AgentTaskRoute.Composite,
    "multi-app semantic intent must map to the complete tool route");
var semanticContract = semanticIntent.ToComputerTaskContract();
Assert(
    semanticContract.Goal == semanticIntent.Goal &&
    semanticContract.App == "Spreadsheet" &&
    semanticContract.Target == "recipient" &&
    semanticContract.Operation == "analyze" &&
    semanticContract.Risk == ComputerTaskRisk.Medium &&
    semanticContract.CompletionEvidence.Count == 2,
    "semantic fields must flow into the execution contract without reparsing user text");
using (var semanticJson = JsonDocument.Parse(semanticIntent.FormatForAgent()))
{
    Assert(
        semanticJson.RootElement.GetProperty("task_type").GetString() == "multi_app_task" &&
        semanticJson.RootElement.GetProperty("goal").GetString() == semanticIntent.Goal &&
        semanticJson.RootElement.GetProperty("entities").GetArrayLength() == 2,
        "semantic contract must serialize as structured data");
}

var graph = TaskGraph.FromIntent(semanticIntent, "semantic-flow");
Assert(
    graph.Status == TaskGraphStatus.Pending &&
    graph.GetReadySteps().Select(step => step.Definition.Id).SequenceEqual(["analyze"]),
    "only dependency-free steps should initially be ready");
graph.StartStep("analyze", "structured-api");
graph.CompleteStep("analyze", "analysis", "analysis return value captured");
Assert(
    graph.GetReadySteps().Select(step => step.Definition.Id).SequenceEqual(["export"]),
    "verified completion should unlock the next dependency");
graph.StartStep("export", "native-adapter");
graph.FailStep("export", "native export did not create an artifact");
Assert(
    graph.Status == TaskGraphStatus.Waiting &&
    graph.Steps.Single(step => step.Definition.Id == "deliver").Status == TaskStepStatus.Blocked,
    "a failed prerequisite must block dependent work");
AssertThrows<InvalidOperationException>(
    () => graph.PrepareRecovery("export", "native-adapter"),
    "recovery must not repeat a failed strategy");
graph.PrepareRecovery("export", "office-api");
graph.StartStep("export", "office-api");
graph.CompleteStep("export", "report path", "artifact exists and opens");
graph.StartStep("deliver", "app-adapter");
graph.CompleteStep("deliver", "delivered", "correct artifact is visible in the intended conversation");
Assert(
    graph.Status == TaskGraphStatus.Succeeded &&
    !string.IsNullOrWhiteSpace(graph.FinalEvidence) &&
    graph.Steps.Single(step => step.Definition.Id == "export").Attempts.Count == 2,
    "the graph should preserve failed history and finish only after verification");
using (var graphJson = JsonDocument.Parse(graph.FormatForAgent()))
{
    Assert(
        graphJson.RootElement.GetProperty("status").GetString() == "succeeded" &&
        graphJson.RootElement.GetProperty("steps").GetArrayLength() == 3,
        "the task graph must serialize its current revision for the agent");
}

var cancellableGraph = TaskGraph.FromIntent(semanticIntent, "cancel-flow");
cancellableGraph.StartStep("analyze", "structured-api");
cancellableGraph.Cancel("user changed the goal");
Assert(
    cancellableGraph.Status == TaskGraphStatus.Cancelled &&
    cancellableGraph.Steps.All(step =>
        step.Status is TaskStepStatus.Cancelled or TaskStepStatus.Succeeded),
    "cancelling a task must stop all remaining work immediately");

Console.WriteLine(
    "PASS: autonomy risk policy, semantic intent, task graph, contract, capability registry, and bounded recovery");

static ComputerTaskContract CreateTask(
    ComputerTaskMode mode = ComputerTaskMode.Operate,
    string app = "Spreadsheet",
    string operation = "export",
    ComputerTaskRisk risk = ComputerTaskRisk.Low,
    TimeSpan? duration = null,
    string? stopCondition = null,
    ComputerTaskAmbiguity? ambiguity = null) =>
    new(
        mode,
        "Complete the requested computer task",
        operation,
        target: "active target",
        app: app,
        source: "test",
        constraints: ["preserve user data"],
        completionEvidence:
        [
            new CompletionEvidenceRequirement(
                CompletionEvidenceKind.StateObservation,
                "expected state is observed"),
        ],
        risk: risk,
        duration: duration,
        stopCondition: stopCondition,
        ambiguity: ambiguity);

static void ExhaustChannel(
    IComputerTaskChannelSelector selector,
    ComputerTaskContract task,
    ComputerTaskAttemptState attempts,
    ComputerTaskChannel expectedChannel)
{
    for (var failure = 0; failure < 2; failure++)
    {
        var selected = selector.Select(task, attempts);
        Assert(
            selected.Channel == expectedChannel,
            $"expected {expectedChannel} before failure {failure + 1}");
        attempts.RecordFailure(selected);
    }
}

static void AssertSelected(
    ComputerTaskRoute route,
    ComputerTaskChannel expectedChannel,
    string expectedCapability,
    int expectedPriorFailures)
{
    Assert(
        route.Status == ComputerTaskRouteStatus.Selected &&
        route.Channel == expectedChannel &&
        route.Capability?.Id == expectedCapability &&
        route.PriorFailuresOnChannel == expectedPriorFailures,
        $"expected {expectedCapability} through {expectedChannel} after " +
        $"{expectedPriorFailures} failures, got {route.Status}/{route.Channel}");
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidDataException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidDataException(message);
    }
}
