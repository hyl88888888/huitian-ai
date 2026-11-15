using System.Net;
using System.Text;
using System.Text.Json;
using HuiTian.Business.Core;

namespace HuiTian.Business.Tests;

public static class EngineRegression
{
    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("HUITIAN_BUSINESS_HOME") ?? "D:\\HuiTianBusiness",
            "Cache", "EngineRegression", Guid.NewGuid().ToString("N"));
        var paths = new BusinessPaths(directory);
        paths.EnsureDirectories();
        var artifactPath = Path.Combine(paths.Outputs, "verified.txt");
        await File.WriteAllTextAsync(artifactPath, "verified fixture");
        var failed = 0;
        var passed = 0;
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); passed++; Console.WriteLine("PASS engine: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL engine: {name}: {ex.Message}"); }
        }
        void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        var read = Definition("inspect", RiskLevel.ReadOnly);
        var write = Definition("produce", RiskLevel.Reversible);
        var external = Definition("submit", RiskLevel.External);
        var critical = Definition("erase", RiskLevel.Critical);
        var evidence = new ToolResult(true, "Observed fixture", "Verified local fixture bytes");
        var fileResult = new ToolResult(true, "Created verified.txt", "File exists with 16 characters",
            [new(artifactPath, "verified.txt", "txt")]);

        await Test("semantic answer preserves complete relevant history", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(new TaskPlan { Mode = "answer", Answer = "这是一个说明。" }));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync(
                "先别动文件，解释这种安排的原因。", [new("user", "Earlier private context"), new("assistant", "Acknowledged")]);
            Check(result.State == RunState.Answered && registry.Calls.Count == 0 &&
                result.Summary.StartsWith("本轮仅生成文字答复，未执行电脑操作。"), "Answer must not claim computer-task completion");
            Check(model.Calls == 1 && model.Messages![1].Content == "Earlier private context" &&
                model.Messages[^1].Content == "先别动文件，解释这种安排的原因。", "History or current intent was dropped");
        });
        await Test("model-only claim of external success is never a completed computer task", async () =>
        {
            var registry = new FakeRegistry([external]);
            var result = await new AgentEngine(new FixedPlanner(new()
                { Mode = "answer", Answer = "已将资料发给联系人。" }), registry, paths)
                .RunAsync("请处理这份资料。", []);
            Check(result.State == RunState.Answered && registry.Calls.Count == 0 &&
                result.Summary.StartsWith("本轮仅生成文字答复，未执行电脑操作。"),
                "Model text was treated as external execution evidence");
        });
        await Test("fresh task after question is model routed without sentence lookup", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(Plan("observation", Step("r", "inspect"))),
                JsonSerializer.Serialize(new TaskReview { GoalSatisfied = true, Answer = "用户要求的观察结果已完整返回。" }));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync(
                "请对它实施刚才说的第一项处理，完成后给结果。", [new("user", "解释这个概念"), new("assistant", "概念答复")]);
            Check(result.State == RunState.Completed && registry.Calls.Count == 1 && model.Calls == 2,
                "Fresh action must be executed from semantic plan");
        });
        await Test("long history keeps recent whole turns and explicit boundary", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(new TaskPlan { Mode = "answer", Answer = "Current answer" }));
            var history = Enumerable.Range(0, 30).Select(i => new ChatMessage(i % 2 == 0 ? "user" : "assistant",
                $"History-{i}:" + new string('资', 300))).ToList();
            history.Add(new("assistant", "Recent completed state: verified.txt exists."));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), new FakeRegistry([read]), paths)
                .RunAsync("Current request remains complete", history);
            Check(result.State == RunState.Answered && model.Messages!.Count < history.Count &&
                model.Messages[0].Content.Contains("上下文边界") &&
                model.Messages[^2].Content == "Recent completed state: verified.txt exists." &&
                model.Messages[^1].Content == "Current request remains complete", "Context bounds lost current intent or recent state");
        });
        await Test("oversized current request needs input before model or tools", async () =>
        {
            var model = new FakeModel("{}");
            var registry = new FakeRegistry([read]);
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths)
                .RunAsync(new string('长', 12000), []);
            Check(result.State == RunState.NeedsInput && model.Calls == 0 && registry.Calls.Count == 0,
                "Oversized request was silently truncated or executed");
        });
        await Test("clarification cannot imply task completion", async () =>
        {
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new FixedPlanner(new() { Mode = "clarify", Answer = "请提供路径。" }), registry, paths)
                .RunAsync("Read it", []);
            Check(result.State == RunState.NeedsInput && registry.Calls.Count == 0, "Clarify must need input");
        });
        await Test("empty task rejected without false success", async () =>
        {
            var result = await new AgentEngine(new FixedPlanner(Plan("observation")), new FakeRegistry([read]), paths).RunAsync("Do work", []);
            Check(result.State == RunState.Failed, "Empty task was completed");
        });
        await Test("unknown tools rejected", async () =>
        {
            var registry = new FakeRegistry([read]);
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("a", "imaginary_shell"))), registry, paths).RunAsync("Do work", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 0, "Unknown tool executed");
        });
        await Test("duplicate IDs rejected", async () =>
        {
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("a", "inspect"), Step("a", "inspect"))), new FakeRegistry([read]), paths)
                .RunAsync("Do work", []);
            Check(result.State == RunState.Failed, "Duplicate IDs accepted");
        });
        await Test("cycles rejected before execution", async () =>
        {
            var registry = new FakeRegistry([read]);
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("a", "inspect", ["b"]), Step("b", "inspect", ["a"]))), registry, paths)
                .RunAsync("Do work", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 0, "Cycle executed");
        });
        await Test("schema errors rejected before execution", async () =>
        {
            var registry = new FakeRegistry([read]);
            var step = Step("a", "inspect");
            step.Arguments = JsonSerializer.SerializeToElement(new { unexpected = true });
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", step)), registry, paths).RunAsync("Do work", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 0, "Unknown parameter accepted");
        });
        await Test("success flag without evidence fails and read recovery bounded", async () =>
        {
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(new ToolResult(true, "Pretended success")));
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("r", "inspect"))), registry, paths).RunAsync("Do work", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 2, "Evidence or retry bound violated");
        });
        await Test("mutations never automatically retried", async () =>
        {
            var registry = new FakeRegistry([write], (_, _, _) => Task.FromResult(new ToolResult(false, "Write failed")));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("w", "produce"))), registry, paths).RunAsync("Make file", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 1, "Mutation retried");
        });
        await Test("nonexistent artifact never marked complete", async () =>
        {
            var registry = new FakeRegistry([write], (_, _, _) => Task.FromResult(new ToolResult(true, "Claimed output", "Claimed evidence",
                [new(Path.Combine(paths.Outputs, "missing.docx"), "missing.docx", "docx")])));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("w", "produce"))), registry, paths).RunAsync("Make file", []);
            Check(result.State == RunState.Failed && result.Artifacts.Count == 0, "Missing output marked complete");
        });
        await Test("verified artifact completes without extra model call", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(Plan("artifact", Step("w", "produce"))));
            var registry = new FakeRegistry([write], (_, _, _) => Task.FromResult(fileResult));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Make file", []);
            Check(result.State == RunState.Completed && result.Artifacts.Count == 1 && model.Calls == 1,
                "Single verified artifact should complete");
        });
        await Test("AI create_document artifact remains partial with usable output", async () =>
        {
            var create = Definition("create_document", RiskLevel.Reversible);
            var model = new FakeModel(JsonSerializer.Serialize(Plan("artifact", Step("w", "create_document"))));
            var registry = new FakeRegistry([create], (_, _, _) => Task.FromResult(fileResult));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Generate a requested document", []);
            Check(result.State == RunState.Partial && result.Artifacts.Count == 1 &&
                await File.ReadAllTextAsync(result.Artifacts[0].Path) == "verified fixture" && model.Calls == 1 &&
                result.Summary.StartsWith("文件已生成，内容待核对，尚未核验完整要求。"),
                "Generated file was treated as semantically accepted or output was lost");
            using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "Runs", result.RunId + ".json")));
            Check(checkpoint.RootElement.GetProperty("State").GetString() == "Partial", "Checkpoint retained false completion");
        });
        await Test("placeholder-only create_document never completes", async () =>
        {
            var placeholderPath = Path.Combine(paths.Outputs, "placeholder-only.txt");
            await File.WriteAllTextAsync(placeholderPath, "[待补充]");
            var create = Definition("create_document", RiskLevel.Reversible);
            var output = new ToolResult(true, "File created and reopened", "Structure verified",
                [new(placeholderPath, "placeholder-only.txt", "txt")]);
            var registry = new FakeRegistry([create], (_, _, _) => Task.FromResult(output));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("w", "create_document"))), registry, paths)
                .RunAsync("Generate a requested document", []);
            Check(result.State == RunState.Partial && result.Artifacts.Count == 1 && File.Exists(result.Artifacts[0].Path),
                "Placeholder-only file falsely completed or was removed");
        });
        await Test("model review cannot upgrade AI document semantic acceptance", async () =>
        {
            var create = Definition("create_document", RiskLevel.Reversible);
            var plan = Plan("artifact", Step("w", "create_document"));
            plan.RequiresObservationReview = true;
            var registry = new FakeRegistry([create], (_, _, _) => Task.FromResult(fileResult));
            var result = await new AgentEngine(new FixedPlanner(plan), registry, paths).RunAsync("Generate a requested document", []);
            Check(result.State == RunState.Partial && result.Artifacts.Count == 1 &&
                !result.Summary.Contains("Verified complete requested result"),
                "Model's completion claim bypassed provisional content acceptance policy");
        });
        await Test("missing final deliverable overrides model completion claim", async () =>
        {
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("r", "inspect"))), registry, paths).RunAsync("Make file", []);
            Check(result.State == RunState.Partial, "Tool output treated as final artifact");
        });
        await Test("capability observation cannot stand in for actual action", async () =>
        {
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("r", "inspect"))), registry, paths).RunAsync("Actually act", []);
            Check(result.State == RunState.Partial, "Read-only capability success was treated as completed action");
        });
        await Test("read-only observation cannot bypass full-goal review", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(Plan("observation", Step("r", "inspect"))),
                JsonSerializer.Serialize(new TaskReview { GoalSatisfied = false, Answer = "这里只观察了数据，原始交付目标仍未满足。" }));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Deliver the requested result", []);
            Check(result.State == RunState.Partial && model.Calls == 2 && registry.Calls.Count == 1,
                "Observation was treated as completed user goal without reviewing it");
        });
        await Test("capabilities-only cannot be declared a completed task", async () =>
        {
            var capability = Definition("capabilities", RiskLevel.ReadOnly);
            var registry = new FakeRegistry([capability], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("r", "capabilities"))), registry, paths)
                .RunAsync("Deliver an outcome", []);
            Check(result.State == RunState.Partial, "Capability catalog accepted as final task deliverable");
        });
        await Test("failed dependencies never execute", async () =>
        {
            var registry = new FakeRegistry([read, write], (_, _, _) => Task.FromResult(new ToolResult(false, "Cannot read")));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("r", "inspect"), Step("w", "produce", ["r"]))), registry, paths)
                .RunAsync("Read then make", []);
            Check(result.State == RunState.Failed && registry.Calls.All(c => c.Name == "inspect"), "Dependent write executed");
        });
        await Test("external and critical require explicit approval", async () =>
        {
            foreach (var definition in new[] { external, critical })
            {
                var registry = new FakeRegistry([definition], (_, _, _) => Task.FromResult(evidence));
                var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", definition.Name))), registry, paths).RunAsync("Act", []);
                Check(result.State == RunState.NeedsInput && registry.Calls.Count == 0, "Unapproved action executed");
            }
        });
        await Test("approval denial stops independent later operations", async () =>
        {
            var registry = new FakeRegistry([external, write], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"), Step("w", "produce"))), registry, paths)
                .RunAsync("Act", [], approval: (_, _, _) => Task.FromResult(false));
            Check(result.State == RunState.NeedsInput && registry.Calls.Count == 0, "Work continued after denial");
        });
        await Test("explicit approval permits verified external action", async () =>
        {
            var registry = new FakeRegistry([external], (_, _, _) => Task.FromResult(evidence));
            var approvals = 0;
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"))), registry, paths)
                .RunAsync("Act", [], approval: (_, _, _) => { approvals++; return Task.FromResult(true); });
            Check(result.State == RunState.Completed && approvals == 1 && registry.Calls.Count == 1, "Approved action failed");
        });
        await Test("approval includes observed target instead of only model description", async () =>
        {
            const string observed = "Observed window fixture 17; actual control fixture 29; intended value fixture.";
            foreach (var definition in new[] { external, critical })
            {
                var registry = new FakeRegistry([definition], (_, _, _) => Task.FromResult(evidence),
                    (name, _, _) =>
                    {
                        Check(name == definition.Name, "Wrong target requested for approval");
                        return Task.FromResult(observed);
                    });
                var approvals = 0;
                var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", definition.Name))), registry, paths)
                    .RunAsync("Act", [], approval: (actual, step, _) =>
                    {
                        Check(actual.Description == definition.Description + "\n\n实际观察的操作目标：\n" + observed,
                            "Observed target not passed to approval UI");
                        Check(actual.Risk == definition.Risk && actual.Name == definition.Name && step.Tool == definition.Name,
                            "Approval changed action identity or risk");
                        approvals++;
                        return Task.FromResult(true);
                    });
                Check(result.State == RunState.Completed && approvals == 1 && registry.ApprovalContextCalls == 1 &&
                    registry.Calls.Count == 1, "Observed approval did not precede the action");
                using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "Runs", result.RunId + ".json")));
                Check(checkpoint.RootElement.GetProperty("Steps")[0].GetProperty("ApprovalContext").GetString() == observed,
                    "Approval target was not checkpointed");
            }
        });
        await Test("expired approval target prevents both approval and all actions", async () =>
        {
            var registry = new FakeRegistry([external, write], (_, _, _) => Task.FromResult(evidence),
                (_, _, _) => throw new InvalidOperationException("Observed target expired"));
            var approvals = 0;
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"), Step("w", "produce"))), registry, paths)
                .RunAsync("Act", [], approval: (_, _, _) => { approvals++; return Task.FromResult(true); });
            Check(result.State == RunState.NeedsInput && approvals == 0 && registry.Calls.Count == 0,
                "Invalid target fell back to model-only approval or allowed later actions");
        });
        await Test("empty approval target is not a usable confirmation", async () =>
        {
            var registry = new FakeRegistry([external], (_, _, _) => Task.FromResult(evidence),
                (_, _, _) => Task.FromResult(" \n "));
            var approvals = 0;
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"))), registry, paths)
                .RunAsync("Act", [], approval: (_, _, _) => { approvals++; return Task.FromResult(true); });
            Check(result.State == RunState.NeedsInput && approvals == 0 && registry.Calls.Count == 0,
                "Empty target description accepted");
        });
        await Test("missing observed-target provider blocks external operations", async () =>
        {
            var registry = new FakeRegistry([external], (_, _, _) => Task.FromResult(evidence));
            var approvals = 0;
            var result = await new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"))), new WithoutApprovalContext(registry), paths)
                .RunAsync("Act", [], approval: (_, _, _) => { approvals++; return Task.FromResult(true); });
            Check(result.State == RunState.NeedsInput && approvals == 0 && registry.Calls.Count == 0,
                "Missing approval context silently downgraded to model-only approval");
        });
        await Test("planning cancellation persisted as cancelled", async () =>
        {
            using var cancellation = new CancellationTokenSource(30);
            var model = new FakeModel(async (_, token) => { await Task.Delay(5000, token); return "{}"; });
            var registry = new FakeRegistry([read]);
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Cancel", [], ct: cancellation.Token);
            Check(result.State == RunState.Cancelled && registry.Calls.Count == 0, "Planning cancel failed");
            using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "Runs", result.RunId + ".json")));
            Check(checkpoint.RootElement.GetProperty("State").GetString() == "Cancelled", "Cancel not checkpointed");
        });
        await Test("tool cancellation stops immediately without retry", async () =>
        {
            using var cancellation = new CancellationTokenSource(40);
            var registry = new FakeRegistry([read], async (_, _, token) => { await Task.Delay(5000, token); return evidence; });
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("r", "inspect"))), registry, paths)
                .RunAsync("Cancel", [], ct: cancellation.Token);
            Check(result.State == RunState.Cancelled && registry.Calls.Count == 1, "Cancellation was retried");
        });
        await Test("submitted action cancellation preserves uncertainty and stops all later work", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            const string uncertainEvidence = "PRIVATE-UNCERTAIN-ACTION-940721: submitted=true; finalOutcome=unknown";
            var registry = new FakeRegistry([external, read, write], (name, _, token) =>
            {
                if (name != "submit") return Task.FromResult(fileResult);
                cancellation.Cancel();
                throw new ActionOutcomeUncertainException("Action submitted before cancellation", uncertainEvidence, token);
            });
            var plan = Plan("action", Step("p", "produce"), Step("x", "submit", ["p"]),
                Step("dependent", "inspect", ["x"]), Step("independent", "produce"));
            var result = await new AgentEngine(new FixedPlanner(plan), registry, paths)
                .RunAsync("Act", [], approval: (_, _, _) => Task.FromResult(true), ct: cancellation.Token);
            Check(result.State == RunState.NeedsInput && registry.Calls.Count == 2 && result.Artifacts.Count == 1,
                "Uncertain submitted action became ordinary cancellation, retried, continued, or lost prior output");
            Check(result.Summary.Contains("操作可能稍后生效") && result.Summary.Contains("不会自动重试") &&
                result.Summary.Contains("不要重复提交"), "Uncertain outcome warning missing");
            using var checkpoint = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(paths.Data, "Runs", result.RunId + ".json")));
            var record = checkpoint.RootElement;
            var uncertain = record.GetProperty("Steps")[1];
            Check(record.GetProperty("State").GetString() == "NeedsInput" && record.GetProperty("RequiresManualReconciliation").GetBoolean() &&
                record.GetProperty("UncertainActionEvidence").GetString() == uncertainEvidence,
                "Run uncertainty or evidence was not durably saved");
            Check(uncertain.GetProperty("State").GetString() == "outcome_uncertain" &&
                uncertain.GetProperty("Evidence").GetString() == uncertainEvidence &&
                uncertain.GetProperty("ActionMayStillComplete").GetBoolean() && uncertain.GetProperty("NoAutomaticRetry").GetBoolean() &&
                !uncertain.GetProperty("BusinessOutcomeVerified").GetBoolean() && uncertain.GetProperty("Attempts").GetInt32() == 1,
                "Uncertain step was rewritten as cancelled or lost action evidence");
            Check(record.GetProperty("Steps")[2].GetProperty("State").GetString() == "skipped" &&
                record.GetProperty("Steps")[3].GetProperty("State").GetString() == "skipped", "Later steps not stopped");
            Check(!(await File.ReadAllTextAsync(Path.Combine(paths.Logs, "performance.jsonl"))).Contains(uncertainEvidence),
                "Private action evidence leaked to performance log");
        });
        await Test("internal action timeout uncertainty is not a retryable tool error", async () =>
        {
            var registry = new FakeRegistry([read], (_, _, token) =>
                throw new ActionOutcomeUncertainException("Provider timeout after submission", "Pending provider operation fixture", token));
            var result = await new AgentEngine(new FixedPlanner(Plan("observation", Step("r", "inspect"))), registry, paths)
                .RunAsync("Observe", []);
            Check(result.State == RunState.NeedsInput && registry.Calls.Count == 1,
                "Uncertain action retried because caller token was not cancelled");
        });
        await Test("finished uncertain action survives engine recreation without replay", async () =>
        {
            var registry = new FakeRegistry([external], (_, _, token) =>
                throw new ActionOutcomeUncertainException("Submitted", "Fixture receipt pending", token));
            var planner = new FixedPlanner(Plan("action", Step("x", "submit")));
            var result = await new AgentEngine(planner, registry, paths).RunAsync("Act", [],
                approval: (_, _, _) => Task.FromResult(true));
            var file = Path.Combine(paths.Data, "Runs", result.RunId + ".json");
            var before = await File.ReadAllBytesAsync(file);
            var recovered = new AgentEngine(planner, registry, paths).GetIncompleteRuns().Single(r => r.RunId == result.RunId);
            Check(recovered.RequiresManualReconciliation && !recovered.RecordUnreadable &&
                recovered.LastState == RunState.NeedsInput && registry.Calls.Count == 1,
                "Finished uncertain action disappeared or was replayed");
            var after = await File.ReadAllBytesAsync(file);
            Check(before.SequenceEqual(after), "Discovery changed the durable record");
        });
        await Test("completed normal run excluded from recovery", async () =>
        {
            var registry = new FakeRegistry([write], (_, _, _) => Task.FromResult(fileResult));
            var engine = new AgentEngine(new FixedPlanner(Plan("artifact", Step("w", "produce"))), registry, paths);
            var result = await engine.RunAsync("Produce", []);
            Check(result.State == RunState.Completed && !engine.GetIncompleteRuns().Any(r => r.RunId == result.RunId),
                "Completed normal run incorrectly needs recovery");
        });
        await Test("corrupt and null records remain visible for manual inspection", async () =>
        {
            var directory = Path.Combine(paths.Data, "Runs");
            await File.WriteAllTextAsync(Path.Combine(directory, "corrupt-fixture.json"), "{incomplete");
            await File.WriteAllTextAsync(Path.Combine(directory, "null-fixture.json"), "null");
            var records = new AgentEngine(new FixedPlanner(Plan("action", Step("x", "submit"))),
                new FakeRegistry([external]), paths).GetIncompleteRuns();
            Check(new[] { "corrupt-fixture", "null-fixture" }.All(id => records.Any(r => r.RunId == id &&
                r.RecordUnreadable && r.RequiresManualReconciliation)), "Unreadable records silently hidden");
        });
        await Test("reference output resolved before tool invocation", async () =>
        {
            var pathTool = new ToolDefinition("consume", "Consume output", JsonSerializer.SerializeToElement(new
            {
                type = "object", required = new[] { "path" }, additionalProperties = false,
                properties = new { path = new { type = "string" } }
            }), RiskLevel.ReadOnly);
            var consume = Step("c", "consume", ["p"]);
            consume.Arguments = JsonSerializer.SerializeToElement(new { path = "$p.path" });
            var registry = new FakeRegistry([write, pathTool], (name, args, _) =>
            {
                if (name == "consume") Check(args.GetProperty("path").GetString() == artifactPath, "Reference unresolved");
                return Task.FromResult(name == "produce" ? fileResult : evidence);
            });
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("p", "produce"), consume)), registry, paths).RunAsync("Produce then consume", []);
            Check(result.State == RunState.Completed && registry.Calls.Count == 2, "References failed");
        });
        await Test("undeclared output dependency rejected", async () =>
        {
            var schema = new ToolDefinition("consume", "Read", JsonSerializer.SerializeToElement(new { type = "object" }), RiskLevel.ReadOnly);
            var step = Step("b", "consume"); step.Arguments = JsonSerializer.SerializeToElement(new { path = "$a.path" });
            var registry = new FakeRegistry([write, schema]);
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("a", "produce"), step)), registry, paths).RunAsync("Do work", []);
            Check(result.State == RunState.Failed && registry.Calls.Count == 0, "Undeclared dependency accepted");
        });
        await Test("observe then generate actual artifact via bounded continuation", async () =>
        {
            var first = Plan("artifact", Step("r", "inspect")); first.RequiresObservationReview = true;
            var next = Plan("artifact", Step("w", "produce", ["r"]));
            var model = new FakeModel(JsonSerializer.Serialize(first), JsonSerializer.Serialize(new TaskReview
            {
                GoalSatisfied = false, Answer = "已读到材料，仍需生成交付文件。", Continuation = next
            }));
            var registry = new FakeRegistry([read, write], (name, _, _) => Task.FromResult(name == "inspect" ? evidence : fileResult));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Read source and make report", []);
            Check(result.State == RunState.Completed && registry.Calls.Count == 2 && model.Calls == 2 && result.Artifacts.Count == 1,
                "Continuation did not create final deliverable");
            Check(model.Messages!.Any(m => m.Content.Contains("不可信观察数据")), "Observations were not marked untrusted");
        });
        await Test("text analysis requires actual post-observation response", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(Plan("text", Step("r", "inspect"))),
                JsonSerializer.Serialize(new TaskReview { GoalSatisfied = true, Answer = "根据已读取材料，实际分析结论如下。" }));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Analyze source", []);
            Check(result.State == RunState.Completed && model.Calls == 2 && result.Summary.StartsWith("根据已读取材料"),
                "Read success replaced analytical deliverable");
        });
        await Test("oversized observations never treated as completed analysis", async () =>
        {
            var model = new FakeModel(JsonSerializer.Serialize(Plan("text", Step("r", "inspect"))));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(new ToolResult(true, "Read material", new string('材', 12000))));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Analyze material", []);
            Check(result.State == RunState.NeedsInput && model.Calls == 1 && registry.Calls.Count == 1,
                "Oversized observation was truncated into a completion claim");
        });
        await Test("mixed verified and failed operations report partial", async () =>
        {
            var registry = new FakeRegistry([read, write], (name, _, _) =>
                Task.FromResult(name == "inspect" ? evidence : new ToolResult(false, "No output")));
            var result = await new AgentEngine(new FixedPlanner(Plan("artifact", Step("r", "inspect"), Step("w", "produce"))), registry, paths)
                .RunAsync("Inspect and produce", []);
            Check(result.State == RunState.Partial, "Partial work misreported");
        });
        await Test("interrupted checkpoints discoverable without replaying effects", async () =>
        {
            var runId = "interrupted-fixture";
            await File.WriteAllTextAsync(Path.Combine(paths.Data, "Runs", runId + ".json"),
                JsonSerializer.Serialize(new { RunId = runId, State = "Running", UpdatedUtc = DateTimeOffset.UtcNow }));
            var registry = new FakeRegistry([write]);
            var incomplete = new AgentEngine(new FixedPlanner(Plan("artifact", Step("w", "produce"))), registry, paths).GetIncompleteRuns();
            Check(incomplete.Any(r => r.RunId == runId) && registry.Calls.Count == 0, "Recovery replayed writes or lost checkpoint");
        });
        await Test("continuation model budget capped at three total calls", async () =>
        {
            var first = Plan("text", Step("a", "inspect"));
            var model = new FakeModel(JsonSerializer.Serialize(first),
                JsonSerializer.Serialize(new TaskReview { Answer = "Need more data", Continuation = Plan("text", Step("b", "inspect")) }),
                JsonSerializer.Serialize(new TaskReview { Answer = "Need more data", Continuation = Plan("text", Step("c", "inspect")) }));
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(evidence));
            var result = await new AgentEngine(new SemanticTaskPlanner(model), registry, paths).RunAsync("Analyze", []);
            Check(result.State == RunState.Partial && model.Calls == 3 && registry.Calls.Count == 3, "Budget not enforced");
        });
        await Test("performance records redact request and results", async () =>
        {
            const string secret = "PRIVATE-MESSAGE-DO-NOT-LOG-910274";
            var registry = new FakeRegistry([read], (_, _, _) => Task.FromResult(new ToolResult(true, secret, secret)));
            await new AgentEngine(new FixedPlanner(Plan("observation", Step("r", "inspect"))), registry, paths).RunAsync(secret, []);
            var performance = await File.ReadAllTextAsync(Path.Combine(paths.Logs, "performance.jsonl"));
            Check(!performance.Contains(secret), "Private content leaked to performance log");
            foreach (var line in performance.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            { using var json = JsonDocument.Parse(line); Check(json.RootElement.TryGetProperty("event_type", out _), "Invalid performance JSONL"); }
        });
        await Test("model endpoint cannot access cloud or file URI", () =>
        {
            foreach (var endpoint in new[] { "https://api.example.com/v1/chat/completions", "http://192.168.1.1/", "file:///D:/secret" })
            {
                var rejected = false;
                try { using var client = new LocalModelClient(endpoint); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "Nonloopback endpoint accepted");
            }
            return Task.CompletedTask;
        });
        await Test("model uses nonthinking JSON mode and local alias", async () =>
        {
            var handler = new FakeHandler(async (request, _) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("model").GetString() == "huitian-business-local", "Wrong alias");
                Check(!json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), "Thinking enabled");
                Check(json.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object", "JSON mode missing");
                return ModelResponse("{}", "stop");
            });
            using var client = new LocalModelClient(handler: handler);
            Check(await client.CompleteAsync([new("user", "test")], true, 512, default) == "{}", "Model content lost");
        });
        await Test("local bearer credential applied without becoming message data", async () =>
        {
            const string key = "test-only-credential";
            using var client = new LocalModelClient(handler: new FakeHandler(async (request, _) =>
            {
                Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == key,
                    "Bearer credential missing");
                Check(!(await request.Content!.ReadAsStringAsync()).Contains(key), "Credential leaked into model messages");
                return ModelResponse("{}", "stop");
            }), apiKeyProvider: () => key);
            await client.CompleteAsync([new("user", "test")], true, 512, default);
        });
        await Test("native schema binds exact tool arguments without prompt inflation", async () =>
        {
            using var client = new LocalModelClient(handler: new FakeHandler(async (request, _) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var format = json.RootElement.GetProperty("response_format");
                Check(format.GetProperty("type").GetString() == "json_schema", "Native grammar not used");
                var schema = format.GetProperty("json_schema").GetProperty("schema");
                var choice = schema.GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("anyOf")[0];
                Check(choice.GetProperty("properties").GetProperty("tool").GetProperty("enum")[0].GetString() == "inspect",
                    "Tool name grammar missing");
                Check(!choice.GetProperty("properties").GetProperty("arguments").GetProperty("additionalProperties").GetBoolean(),
                    "Exact argument grammar missing");
                return ModelResponse(JsonSerializer.Serialize(new TaskPlan { Mode = "answer", Answer = "Answer" }), "stop");
            }), apiKeyProvider: () => null);
            var result = await new SemanticTaskPlanner(client).PlanAsync("Explain", [], [read], default);
            Check(result.Mode == "answer", "Structured model adapter failed");
        });
        await Test("truncated model output rejected even if JSON parses", async () =>
        {
            using var client = new LocalModelClient(handler: new FakeHandler((_, _) => Task.FromResult(ModelResponse("{}", "length"))));
            var rejected = false;
            try { await client.CompleteAsync([new("user", "test")], true, 512, default); }
            catch (ModelException ex) { rejected = ex.Code == "truncated_response"; }
            Check(rejected, "Truncated output accepted");
        });
        await Test("model timeout distinguished from user cancellation", async () =>
        {
            using var client = new LocalModelClient(timeout: TimeSpan.FromMilliseconds(30), handler: new FakeHandler(async (_, token) =>
            { await Task.Delay(5000, token); return ModelResponse("{}", "stop"); }));
            var timeout = false;
            try { await client.CompleteAsync([new("user", "test")], true, 512, default); }
            catch (ModelException ex) { timeout = ex.Code == "timeout"; }
            Check(timeout, "Timeout was misreported");
        });
        Console.WriteLine($"Engine regression: {passed} passed, {failed} failed. Evidence: {directory}");
        if (failed > 0) throw new InvalidOperationException($"Engine regression failed: {failed} checks.");
        return passed;
    }

    private static ToolDefinition Definition(string name, RiskLevel risk) => new(name, name,
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), risk);
    private static PlanStep Step(string id, string tool, List<string>? dependencies = null) => new()
    { Id = id, Tool = tool, Title = "Execute " + tool, DependsOn = dependencies ?? [] };
    private static TaskPlan Plan(string kind, params PlanStep[] steps) => new()
    { Mode = "task", Goal = "Complete requested outcome", SuccessCondition = "Requested result is independently verified", CompletionKind = kind, Steps = steps.ToList() };
    private static HttpResponseMessage ModelResponse(string content, string reason) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = reason } } }), Encoding.UTF8, "application/json") };

    private sealed class FixedPlanner(TaskPlan plan) : ITaskPlanner, IObservationTaskPlanner
    {
        public Task<TaskPlan> PlanAsync(string request, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
            => Task.FromResult(plan);
        public Task<TaskReview> ReviewAsync(string request, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools,
            TaskPlan originalPlan, IReadOnlyDictionary<string, ToolResult> observations, int remainingContinuations, CancellationToken cancellationToken)
            => Task.FromResult(new TaskReview { GoalSatisfied = true, Answer = "Verified complete requested result" });
    }
    private sealed class FakeRegistry(IReadOnlyList<ToolDefinition> definitions,
        Func<string, JsonElement, CancellationToken, Task<ToolResult>>? execute = null,
        Func<string, JsonElement, CancellationToken, Task<string>>? describeApproval = null) : IToolRegistry, IApprovalContextProvider
    {
        public IReadOnlyList<ToolDefinition> Definitions => definitions;
        public List<(string Name, JsonElement Arguments)> Calls { get; } = [];
        public int ApprovalContextCalls { get; private set; }
        public Task<string> DescribeApprovalAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            ApprovalContextCalls++;
            return describeApproval?.Invoke(name, arguments, cancellationToken)
                ?? Task.FromResult("Observed fixture target for " + name);
        }
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        { Calls.Add((name, arguments.Clone())); return execute?.Invoke(name, arguments, cancellationToken) ?? Task.FromResult(new ToolResult(false, "No result configured")); }
    }
    private sealed class WithoutApprovalContext(FakeRegistry registry) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => registry.Definitions;
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
            => registry.ExecuteAsync(name, arguments, cancellationToken);
    }
    private sealed class FakeModel : IModelClient
    {
        private readonly Queue<string>? responses;
        private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>>? callback;
        public int Calls { get; private set; }
        public IReadOnlyList<ChatMessage>? Messages { get; private set; }
        public FakeModel(params string[] replies) => responses = new(replies);
        public FakeModel(Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> action) => callback = action;
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, int maxTokens, CancellationToken cancellationToken)
        { Calls++; Messages = messages; return callback?.Invoke(messages, cancellationToken) ?? Task.FromResult(responses!.Dequeue()); }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
