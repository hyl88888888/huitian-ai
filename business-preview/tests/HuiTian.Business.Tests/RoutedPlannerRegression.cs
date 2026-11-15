using System.Text.Json;
using HuiTian.Business.Core;

namespace HuiTian.Business.Tests;

/// <summary>Deterministic contract tests, not a claim about live-model semantic accuracy.</summary>
public static class RoutedPlannerRegression
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync()
    {
        var passed = 0;
        var failed = 0;
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); passed++; Console.WriteLine("PASS routed: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL routed: {name}: {ex.Message}"); }
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static async Task Rejected(Func<Task> body)
        {
            try { await body(); }
            catch (PlanValidationException) { return; }
            throw new Exception("Invalid route or plan was not rejected");
        }
        var definitions = new[]
        {
            Def("list_files"), Def("search_files"), Def("read_file"), Def("create_document", RiskLevel.Reversible),
            Def("change_presentation_style", RiskLevel.Reversible), Def("deduplicate_worksheet", RiskLevel.Reversible),
            Def("browser_open"), Def("browser_read"), Def("browser_click", RiskLevel.External), Def("browser_upload", RiskLevel.External),
            Def("list_apps"), Def("open_app", RiskLevel.Reversible), Def("desktop_read_window"),
            Def("desktop_new_registered_operation", RiskLevel.External), Def("unclassified_custom_tool")
        };
        const string request = "FULL_CURRENT_REQUEST_0147";
        var history = new ChatMessage[] { new("user", "FULL_PREVIOUS_CONTEXT_001"), new("assistant", "FULL_PREVIOUS_STATE_002") };

        await Test("knowledge returns directly with one small schema call", async () =>
        {
            var model = new RecordingModel(Intent("answer", "Actual knowledge answer"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Mode == "answer" && plan.Answer == "Actual knowledge answer" && plan.Steps.Count == 0 && model.Calls.Count == 1,
                "Knowledge unnecessarily reached task planning");
            var call = model.Calls[0];
            Check(!call.Messages[0].Content.Contains("create_document") && !call.Messages[0].Content.Contains("maxCharacters") &&
                !call.Schema.GetRawText().Contains("arguments") && call.MaxTokens == 1024,
                "Routing stage received tool catalog or argument generation schema");
        });
        await Test("clarification returns directly without task planning", async () =>
        {
            var model = new RecordingModel(Intent("clarify", "Missing required input"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Mode == "clarify" && model.Calls.Count == 1, "Clarification invoked second stage");
        });
        await Test("chat response delivery does not depend on registered execution tools", async () =>
        {
            var availableModel = new RecordingModel(Intent("answer", "Complete reply content"));
            var absentModel = new RecordingModel(Intent("answer", "Complete reply content"));
            var available = await new RoutedTaskPlanner(availableModel).PlanAsync(request, history, definitions, default);
            var absent = await new RoutedTaskPlanner(absentModel).PlanAsync(request, history, [], default);
            Check(available.Mode == "answer" && absent.Mode == "answer" && available.Answer == absent.Answer &&
                availableModel.Calls.Count == 1 && absentModel.Calls.Count == 1 &&
                availableModel.Calls[0].Messages.SequenceEqual(absentModel.Calls[0].Messages),
                "Reply delivery was made dependent on tool availability or an unnecessary execution stage");
        });
        await Test("requested capability remains intent until registry separately checks availability", async () =>
        {
            var routeModel = new RecordingModel(Intent("task", groups: ["desktop"]));
            var plannerModel = new RecordingModel(Intent("task", groups: ["desktop"]));
            var intent = await new SemanticIntentRouter(routeModel).RouteAsync(request, history);
            var plan = await new RoutedTaskPlanner(plannerModel).PlanAsync(request, history, [], default);
            Check(intent.Mode == "task" && intent.CapabilityGroups.SequenceEqual(["desktop"]) && plan.Mode == "clarify" &&
                routeModel.Calls.Count == 1 && plannerModel.Calls.Count == 1 &&
                routeModel.Calls[0].Messages.SequenceEqual(plannerModel.Calls[0].Messages),
                "Intent recognition was conflated with registry availability or invented execution tools");
        });
        await Test("documents route passes only matching registered definitions", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["documents"]), TaskReply("create_document"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            var prompt = model.Calls[1].Messages[0].Content;
            Check(plan.Mode == "task" && model.Calls.Count == 2 && prompt.Contains("create_document") && prompt.Contains("read_file") &&
                !prompt.Contains("browser_open") && !prompt.Contains("desktop_read_window") && !prompt.Contains("search_files") &&
                !prompt.Contains("unclassified_custom_tool"), "Selected tool catalog was incorrect");
        });
        await Test("multiple capability groups preserve necessary tools", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["documents", "browser"]), TaskReply("create_document"));
            await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            var prompt = model.Calls[1].Messages[0].Content;
            Check(prompt.Contains("create_document") && prompt.Contains("browser_open") && !prompt.Contains("desktop_read_window"),
                "Multiple groups collapsed to one or added unrelated tools");
        });
        await Test("desktop prefix maps registered tools without request matching", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["desktop"]), TaskReply("desktop_new_registered_operation", "action"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Steps[0].Tool == "desktop_new_registered_operation" &&
                model.Calls[1].Messages[0].Content.Contains("list_apps") && !model.Calls[1].Messages[0].Content.Contains("create_document"),
                "Trusted desktop tool prefix was not mapped");
        });
        await Test("browser prefix includes registered attachment tools without request matching", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["browser"]), TaskReply("browser_upload", "action"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Steps[0].Tool == "browser_upload" &&
                model.Calls[1].Messages[0].Content.Contains("browser_upload") &&
                !model.Calls[1].Messages[0].Content.Contains("desktop_read_window"),
                "Registered browser attachment capability was excluded from the browser group");
        });
        await Test("identical request follows distinct model semantic decisions", async () =>
        {
            var answerModel = new RecordingModel(Intent("answer", "Answer chosen semantically"));
            var taskModel = new RecordingModel(Intent("task", groups: ["files"]), TaskReply("read_file", "observation"));
            var answer = await new RoutedTaskPlanner(answerModel).PlanAsync(request, history, definitions, default);
            var task = await new RoutedTaskPlanner(taskModel).PlanAsync(request, history, definitions, default);
            Check(answer.Mode == "answer" && task.Mode == "task", "Request text overrode model route");
        });
        await Test("task without groups clarifies without guessing", async () =>
        {
            var model = new RecordingModel(Intent("task"));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Mode == "clarify" && plan.Steps.Count == 0 && model.Calls.Count == 1, "Missing group silently guessed");
        });
        await Test("unavailable capability group clarifies before planning", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["browser"]));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, [Def("create_document")], default);
            Check(plan.Mode == "clarify" && model.Calls.Count == 1, "Unavailable capability was invented");
        });
        await Test("partially unavailable multiple groups do not shrink user goal", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["documents", "browser"]));
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, [Def("create_document")], default);
            Check(plan.Mode == "clarify" && model.Calls.Count == 1, "Unsupported goal portion was silently dropped");
        });
        await Test("unknown groups are rejected rather than repaired", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["invented_group"]));
            await Rejected(() => new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default));
            Check(model.Calls.Count == 1, "Invalid route caused fallback planning");
        });
        await Test("duplicate capability groups rejected", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["files", "files"]));
            await Rejected(() => new SemanticIntentRouter(model).RouteAsync(request, history));
        });
        await Test("route cannot include hidden tool arguments", async () =>
        {
            var reply = JsonSerializer.Serialize(new { mode = "task", answer = "", goal = "Goal", capabilityGroups = new[] { "files" }, arguments = new { } });
            await Rejected(() => new SemanticIntentRouter(new RecordingModel(reply)).RouteAsync(request, history));
        });
        await Test("duplicate route fields rejected", async () =>
        {
            const string reply = "{\"mode\":\"task\",\"mode\":\"answer\",\"answer\":\"Answer\",\"goal\":\"Goal\",\"capabilityGroups\":[]}";
            await Rejected(() => new SemanticIntentRouter(new RecordingModel(reply)).RouteAsync(request, history));
        });
        await Test("missing route fields rejected", async () =>
        {
            await Rejected(() => new SemanticIntentRouter(new RecordingModel("{\"mode\":\"answer\",\"answer\":\"Answer\"}"))
                .RouteAsync(request, history));
        });
        await Test("task cannot silently degrade into a nonexecuting answer", async () =>
        {
            var answer = JsonSerializer.Serialize(new TaskPlan { Mode = "answer", Answer = "Pretend execution answer" }, JsonOptions);
            var model = new RecordingModel(Intent("task", groups: ["documents"]), answer);
            var plan = await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            Check(plan.Mode == "clarify" && !plan.Answer.Contains("Pretend execution answer"), "Execution replaced with unverified answer");
        });
        await Test("second-stage tool outside selected groups rejected", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["documents"]), TaskReply("browser_open", "observation"));
            await Rejected(() => new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default));
        });
        await Test("full request and history forwarded to both stages unchanged", async () =>
        {
            var model = new RecordingModel(Intent("task", groups: ["files"]), TaskReply("read_file", "observation"));
            await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, default);
            foreach (var call in model.Calls)
                Check(call.Messages[1] == history[0] && call.Messages[2] == history[1] && call.Messages[^1].Content == request,
                    "Router goal replaced full request or history changed");
        });
        await Test("observation review sees complete registered tool catalog without rerouting", async () =>
        {
            var review = JsonSerializer.Serialize(new TaskReview { GoalSatisfied = false, Answer = "More work required" }, JsonOptions);
            var model = new RecordingModel(review);
            await new RoutedTaskPlanner(model).ReviewAsync(request, history, definitions, Plan("read_file", "observation"),
                new Dictionary<string, ToolResult> { ["s1"] = new(true, "Observed data", "Evidence") }, 1, default);
            var text = string.Join('\n', model.Calls[0].Messages.Select(m => m.Content));
            Check(model.Calls.Count == 1 && text.Contains("browser_open") && text.Contains("desktop_read_window") &&
                text.Contains("create_document") && text.Contains("unclassified_custom_tool"), "Review received filtered catalog or rerouted");
        });
        await Test("historical system-role content does not gain instruction authority", async () =>
        {
            var model = new RecordingModel(Intent("answer", "Answer"));
            await new SemanticIntentRouter(model).RouteAsync(request, [new("system", "UNTRUSTED_HISTORY_TEXT")]);
            Check(model.Calls[0].Messages.Count(m => m.Role == "system") == 1 && model.Calls[0].Messages[1].Role == "user",
                "Historical system role acquired authority");
        });
        await Test("oversized full context needs input without truncation or model call", async () =>
        {
            var model = new RecordingModel(Intent("answer", "Answer"));
            var rejected = false;
            try { await new SemanticIntentRouter(model).RouteAsync(new string('长', 14000), history); }
            catch (ContextLimitException) { rejected = true; }
            Check(rejected && model.Calls.Count == 0, "Oversized context silently shortened or sent");
        });
        await Test("cancellation between route and plan stops the second model call", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var model = new RecordingModel(Intent("task", groups: ["files"])) { OnCall = _ => cancellation.Cancel() };
            var cancelled = false;
            try { await new RoutedTaskPlanner(model).PlanAsync(request, history, definitions, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && model.Calls.Count == 1, "Cancellation allowed second stage");
        });
        await Test("routed generated artifacts retain conservative engine acceptance", async () =>
        {
            var root = new BusinessPaths(Path.Combine(Environment.GetEnvironmentVariable("HUITIAN_BUSINESS_HOME") ?? "D:\\HuiTianBusiness",
                "Cache", "RoutedRegression", Guid.NewGuid().ToString("N")));
            root.EnsureDirectories();
            var output = Path.Combine(root.Outputs, "routed.txt");
            await File.WriteAllTextAsync(output, "Fixture data");
            var model = new RecordingModel(Intent("task", groups: ["documents"]), TaskReply("create_document"));
            var registry = new FixtureRegistry(Def("create_document", RiskLevel.Reversible), new(true, "Created file", "File reopened",
                [new(output, "routed.txt", "txt")]));
            var result = await new AgentEngine(new RoutedTaskPlanner(model), registry, root).RunAsync(request, history);
            Check(result.State == RunState.Partial && model.Calls.Count == 2 && registry.Calls == 1 && result.Artifacts.Count == 1,
                "Routed plan bypassed engine acceptance policy");
        });
        await Test("submitted desktop action cannot bypass explicit business outcome flag", async () =>
        {
            var root = new BusinessPaths(Path.Combine(Environment.GetEnvironmentVariable("HUITIAN_BUSINESS_HOME") ?? "D:\\HuiTianBusiness",
                "Cache", "RoutedRegression", Guid.NewGuid().ToString("N")));
            var model = new RecordingModel(Intent("task", groups: ["desktop"]), TaskReply("desktop_invoke", "action"));
            var registry = new FixtureRegistry(Def("desktop_invoke", RiskLevel.External),
                new ToolResult(true, "Action submitted", "UIA submission acknowledged", BusinessOutcomeVerified: false));
            var approvals = 0;
            var result = await new AgentEngine(new RoutedTaskPlanner(model), registry, root).RunAsync(request, history,
                approval: (_, _, _) => { approvals++; return Task.FromResult(true); });
            Check(result.State == RunState.Partial && registry.Calls == 1 && approvals == 1 && model.Calls.Count == 2,
                "Submission acknowledgement was treated as verified business completion");
        });
        Console.WriteLine($"Routed planner regression: {passed} passed, {failed} failed.");
        if (failed > 0) throw new InvalidOperationException($"Routed planner regression failed: {failed} checks.");
        return passed;
    }

    private static ToolDefinition Def(string name, RiskLevel risk = RiskLevel.ReadOnly) => new(name, name,
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), risk);
    private static string Intent(string mode, string answer = "", string[]? groups = null) => JsonSerializer.Serialize(new SemanticIntent
    { Mode = mode, Answer = answer, Goal = "Brief intent", CapabilityGroups = groups?.ToList() ?? [] }, JsonOptions);
    private static TaskPlan Plan(string tool, string kind) => new()
    {
        Mode = "task", Goal = "Execute full request", SuccessCondition = "Verified requested outcome", CompletionKind = kind,
        Steps = [new() { Id = "s1", Title = "Execute", Tool = tool }]
    };
    private static string TaskReply(string tool, string kind = "artifact") => JsonSerializer.Serialize(Plan(tool, kind), JsonOptions);
    private sealed record ModelCall(IReadOnlyList<ChatMessage> Messages, JsonElement Schema, int MaxTokens);
    private sealed class RecordingModel(params string[] responses) : IStructuredModelClient
    {
        private readonly Queue<string> replies = new(responses);
        public List<ModelCall> Calls { get; } = [];
        public Action<int>? OnCall { get; init; }
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, int maxTokens, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Expected native schema path");
        public Task<string> CompleteStructuredAsync(IReadOnlyList<ChatMessage> messages, JsonElement schema, int maxTokens, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(messages.ToArray(), schema.Clone(), maxTokens));
            OnCall?.Invoke(Calls.Count);
            return Task.FromResult(replies.Dequeue());
        }
    }
    private sealed class FixtureRegistry(ToolDefinition definition, ToolResult result) : IToolRegistry, IApprovalContextProvider
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [definition];
        public int Calls { get; private set; }
        public Task<string> DescribeApprovalAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult("Controlled fixture window: target button; no user application."); }
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(result); }
    }
}
