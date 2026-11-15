using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HuiTian.Business.Core;

/// <summary>A bounded executor. The only automatic retry is one repeat of a read-only operation.</summary>
public sealed class AgentEngine(ITaskPlanner planner, IToolRegistry tools, BusinessPaths paths)
{
    private readonly SemaphoreSlim runGate = new(1, 1);
    private static readonly SemaphoreSlim PerformanceGate = new(1, 1);
    private static readonly JsonSerializerOptions CheckpointJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<RunResult> RunAsync(string request, IReadOnlyList<ChatMessage> history,
        IProgress<AgentEvent>? progress = null, ApprovalHandler? approval = null,
        CancellationToken ct = default)
    {
        var run = new RunCheckpoint
        {
            RunId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8],
            Request = request,
            State = RunState.Planning,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        var watch = Stopwatch.StartNew();
        var results = new Dictionary<string, ToolResult>(StringComparer.Ordinal);
        var artifacts = new List<Artifact>();
        var entered = false;
        var checkpointAvailable = false;

        void Report(RunState state, string message, string? stepId = null, Artifact? artifact = null)
        {
            // A UI callback must not change execution semantics or bypass a checkpoint.
            try { progress?.Report(new(run.RunId, state, message, stepId, artifact)); }
            catch { /* Observers do not own the run. */ }
        }

        async Task<RunResult> Finish(RunState state, string summary)
        {
            if (state == RunState.Completed && results.Values.Any(result => result.BusinessOutcomeVerified == false))
            {
                state = RunState.Partial;
                summary = "操作已提交，但尚未核验最终业务结果，不能据此认定完整任务完成。\n" + summary;
            }
            // Provisional delivery policy: valid generated files do not establish semantic acceptance.
            // Apply centrally so a model review cannot override this limitation. Direct tool calls are unaffected.
            if (state == RunState.Completed && run.Steps.Any(s => s.Tool == "create_document" && results.ContainsKey(s.Id)))
            {
                state = RunState.Partial;
                summary = BuildSummary(run, results, artifacts.Count > 0
                    ? "文件已生成，内容待核对，尚未核验完整要求。"
                    : "生成步骤已执行，但尚未核验内容与完整交付要求。");
            }
            run.State = state;
            run.Summary = summary;
            run.FinishedUtc = DateTimeOffset.UtcNow;
            foreach (var step in run.Steps.Where(s => s.State is "pending" or "running" or "awaiting_approval"))
                step.State = state == RunState.Cancelled ? "cancelled" : "skipped";
            try { await SaveCheckpointAsync(run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Missing durable state is a failure even if a tool has already produced an artifact.
                state = state == RunState.Cancelled ? state : RunState.Failed;
                summary += "\n本地运行记录写入失败，请检查磁盘和目录权限；已发生的操作不会自动撤销。";
            }
            await RecordPerformanceAsync(new
            {
                schema = 1, run_id = run.RunId, event_type = "run", utc = DateTimeOffset.UtcNow,
                elapsed_ms = watch.ElapsedMilliseconds, state = state.ToString(), step_count = run.Steps.Count,
                verified_count = results.Count, artifact_count = artifacts.Count
            });
            Report(state, summary);
            return new(run.RunId, state, summary, artifacts.ToArray());
        }

        try
        {
            paths.EnsureDirectories();
            await SaveCheckpointAsync(run);
            checkpointAvailable = true;
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request))
                return await Finish(RunState.NeedsInput, "请输入要咨询或执行的内容。");
            await runGate.WaitAsync(ct);
            entered = true;
            Report(RunState.Planning, "正在理解本轮意图并检查可用工具。 ");
            var planningWatch = Stopwatch.StartNew();
            var plan = await planner.PlanAsync(request, history, tools.Definitions, ct);
            ct.ThrowIfCancellationRequested();
            PlanValidator.Validate(plan, tools.Definitions);
            run.Plan = plan;
            await RecordPerformanceAsync(new
            {
                schema = 1, run_id = run.RunId, event_type = "plan", utc = DateTimeOffset.UtcNow,
                elapsed_ms = planningWatch.ElapsedMilliseconds, mode = plan.Mode, step_count = plan.Steps.Count
            });
            // Text generation is not evidence of computer execution, regardless of what the model claims.
            if (plan.Mode == "answer") return await Finish(RunState.Answered,
                "本轮仅生成文字答复，未执行电脑操作。\n\n" + plan.Answer);
            if (plan.Mode == "clarify") return await Finish(RunState.NeedsInput, plan.Answer);
            run.Steps = plan.Steps.Select(s => new StepCheckpoint { Id = s.Id, Tool = s.Tool }).ToList();
            run.State = RunState.Running;
            await SaveCheckpointAsync(run);
            var pending = plan.Steps.ToList();
            var failed = new HashSet<string>(StringComparer.Ordinal);
            var requiresHuman = false;
            var reviewCalls = 0;
            var completedWrites = new List<(string Tool, JsonElement Arguments)>();
            var originalPlan = plan;
            while (true)
            {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var step = pending.FirstOrDefault(s => s.DependsOn.All(d => results.ContainsKey(d) || failed.Contains(d)))
                    ?? throw new PlanValidationException("无法解析计划依赖。");
                pending.Remove(step);
                var checkpoint = run.Steps.Single(s => s.Id == step.Id);
                if (step.DependsOn.Any(failed.Contains))
                {
                    failed.Add(step.Id);
                    checkpoint.State = "skipped";
                    checkpoint.Summary = "依赖步骤未通过核验，未执行。";
                    Report(RunState.Running, checkpoint.Summary, step.Id);
                    await SaveCheckpointAsync(run);
                    continue;
                }
                var definition = tools.Definitions.Single(t => t.Name == step.Tool);
                JsonElement arguments;
                try
                {
                    arguments = ResolveArguments(step.Arguments, results);
                    PlanValidator.ValidateShape(arguments, definition.Parameters);
                }
                catch (PlanValidationException ex)
                {
                    failed.Add(step.Id);
                    checkpoint.State = "failed";
                    checkpoint.Summary = ex.Message;
                    Report(RunState.Running, ex.Message, step.Id);
                    await SaveCheckpointAsync(run);
                    continue;
                }
                var resolvedStep = new PlanStep
                {
                    Id = step.Id, Title = step.Title, Tool = step.Tool,
                    Arguments = arguments, DependsOn = step.DependsOn.ToList()
                };
                if (definition.Risk >= RiskLevel.Reversible && completedWrites.Any(w =>
                    w.Tool == step.Tool && JsonElement.DeepEquals(w.Arguments, arguments)))
                {
                    checkpoint.State = "blocked";
                    checkpoint.Summary = "继续规划重复了已经完成的写入或操作，已阻止重复执行。";
                    return await Finish(RunState.Partial, BuildSummary(run, results, "任务尚未完成，需要调整后续步骤。"));
                }
                if (definition.Risk >= RiskLevel.External)
                {
                    run.State = RunState.AwaitingApproval;
                    checkpoint.State = "awaiting_approval";
                    ToolDefinition approvalDefinition;
                    try
                    {
                        if (tools is not IApprovalContextProvider contextProvider)
                            throw new InvalidOperationException("Registry cannot describe the observed approval target.");
                        var details = await contextProvider.DescribeApprovalAsync(step.Tool, arguments, ct);
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(details))
                            throw new InvalidOperationException("Observed approval target is empty.");
                        checkpoint.ApprovalContext = details;
                        approvalDefinition = definition with
                        {
                            Description = definition.Description + "\n\n实际观察的操作目标：\n" + details
                        };
                    }
                    catch (ActionOutcomeUncertainException) { throw; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        checkpoint.State = "blocked";
                        checkpoint.Summary = "无法取得有效的实际操作目标说明，未请求确认，也未执行。请重新观察目标后再继续。";
                        return await Finish(RunState.NeedsInput, BuildSummary(run, results,
                            "审批目标缺失或已失效，所有待执行步骤已停止。"));
                    }
                    await SaveCheckpointAsync(run);
                    Report(RunState.AwaitingApproval, $"此步骤需要确认：{step.Title}", step.Id);
                    var allowed = approval is not null && await approval(approvalDefinition, resolvedStep, ct);
                    ct.ThrowIfCancellationRequested();
                    if (!allowed)
                    {
                        checkpoint.State = "blocked";
                        checkpoint.Summary = "未获得操作确认，未执行。";
                        failed.Add(step.Id);
                        requiresHuman = true;
                        // Stop the run after denial; a later independent write must not slip through.
                        return await Finish(RunState.NeedsInput, BuildSummary(run, results,
                            "需要你的确认才能继续；待执行步骤已停止。"));
                    }
                    checkpoint.Approved = true;
                }
                run.State = RunState.Running;
                ToolResult? outcome = null;
                // Read-only actions may recover once; writes are never repeated automatically.
                var maximumAttempts = definition.Risk == RiskLevel.ReadOnly ? 2 : 1;
                for (var attempt = 1; attempt <= maximumAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    checkpoint.Attempts = attempt;
                    checkpoint.State = "running";
                    await SaveCheckpointAsync(run);
                    Report(RunState.Running, attempt == 1 ? step.Title : $"正在重试只读步骤：{step.Title}", step.Id);
                    var toolWatch = Stopwatch.StartNew();
                    string? errorKind = null;
                    try
                    {
                        outcome = await tools.ExecuteAsync(step.Tool, arguments, ct);
                        ct.ThrowIfCancellationRequested();
                        outcome = VerifyResult(outcome);
                    }
                    // An already-submitted operation may still take effect after cancellation or timeout.
                    // Never convert this into a retryable tool failure or ordinary cancellation.
                    catch (ActionOutcomeUncertainException) { throw; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        errorKind = ex.GetType().Name;
                        outcome = new(false, "工具执行失败，未能核验结果。请检查应用、文件权限或本地服务状态。");
                    }
                    await RecordPerformanceAsync(new
                    {
                        schema = 1, run_id = run.RunId, event_type = "tool", utc = DateTimeOffset.UtcNow,
                        step_index = run.Steps.IndexOf(checkpoint) + 1, tool = definition.Name, elapsed_ms = toolWatch.ElapsedMilliseconds,
                        attempt, success = outcome.Success, error_kind = errorKind
                    });
                    checkpoint.Summary = outcome.Summary;
                    checkpoint.Evidence = outcome.Evidence;
                    checkpoint.BusinessOutcomeVerified = outcome.BusinessOutcomeVerified;
                    checkpoint.Artifacts = outcome.Artifacts?.ToList() ?? [];
                    if (outcome.Success || outcome.NeedsHuman) break;
                }
                checkpoint.State = outcome!.Success ? "verified" : outcome.NeedsHuman ? "blocked" : "failed";
                if (outcome.Success)
                {
                    results.Add(step.Id, outcome);
                    if (definition.Risk >= RiskLevel.Reversible) completedWrites.Add((step.Tool, arguments.Clone()));
                    foreach (var artifact in outcome.Artifacts ?? [])
                    {
                        if (!artifacts.Any(a => string.Equals(a.Path, artifact.Path, StringComparison.OrdinalIgnoreCase)))
                            artifacts.Add(artifact);
                        Report(RunState.Running, outcome.Summary, step.Id, artifact);
                    }
                    Report(RunState.Running, outcome.Summary, step.Id);
                }
                else
                {
                    failed.Add(step.Id);
                    requiresHuman |= outcome.NeedsHuman;
                    Report(RunState.Running, outcome.Summary, step.Id);
                }
                await SaveCheckpointAsync(run);
            }
            ct.ThrowIfCancellationRequested();
            if (failed.Count == 0)
            {
                var needsReview = plan.RequiresObservationReview || plan.CompletionKind == "text" ||
                    plan.Steps.Count > 1 || originalPlan.CompletionKind == "artifact" && artifacts.Count == 0 ||
                    originalPlan.CompletionKind == "action" && completedWrites.Count == 0 ||
                    completedWrites.Count == 0;
                if (needsReview)
                {
                    if (planner is not IObservationTaskPlanner observationPlanner || reviewCalls >= 2)
                        return await Finish(results.Count > 0 ? RunState.Partial : RunState.NeedsInput,
                            BuildSummary(run, results, "步骤已执行，但尚不能核验完整目标。继续规划预算已用尽或当前规划器不支持结果核验。"));
                    run.State = RunState.Planning;
                    Report(RunState.Planning, "正在依据实际结果核验完整目标并规划剩余工作。");
                    await SaveCheckpointAsync(run);
                    var reviewWatch = Stopwatch.StartNew();
                    reviewCalls++;
                    var review = await observationPlanner.ReviewAsync(request, history, tools.Definitions,
                        originalPlan, results, 3 - reviewCalls, ct);
                    ct.ThrowIfCancellationRequested();
                    await RecordPerformanceAsync(new
                    {
                        schema = 1, run_id = run.RunId, event_type = "review", utc = DateTimeOffset.UtcNow,
                        elapsed_ms = reviewWatch.ElapsedMilliseconds, review_call = reviewCalls,
                        satisfied = review.GoalSatisfied, continuation = review.Continuation is not null
                    });
                    if (string.IsNullOrWhiteSpace(review.Answer))
                        throw new PlanValidationException("结果核验未提供实际结论。");
                    run.Reviews.Add(review);
                    if (review.GoalSatisfied)
                    {
                        // Registry introspection is intermediate capability evidence, not a delivered task.
                        if (run.Steps.Count > 0 && run.Steps.All(s => s.Tool == "capabilities"))
                            return await Finish(RunState.Partial, BuildSummary(run, results,
                                "已核实可用能力，但尚未产生用户任务的实际交付结果。"));
                        if (review.Continuation is not null || originalPlan.CompletionKind == "artifact" && artifacts.Count == 0 ||
                            originalPlan.CompletionKind == "action" && completedWrites.Count == 0)
                            return await Finish(RunState.Partial, BuildSummary(run, results,
                                "结果核验未通过：仍缺少用户要求的交付物或实际操作证据。"));
                        return await Finish(RunState.Completed, review.Answer + Environment.NewLine +
                            BuildSummary(run, results, "执行证据："));
                    }
                    if (review.Continuation is null)
                        return await Finish(results.Count > 0 ? RunState.Partial : RunState.NeedsInput,
                            BuildSummary(run, results, review.Answer));
                    var continuation = review.Continuation;
                    PlanValidator.Validate(continuation, tools.Definitions, results.Keys.ToHashSet(StringComparer.Ordinal));
                    if (continuation.Mode != "task" || continuation.CompletionKind != originalPlan.CompletionKind ||
                        run.Steps.Count + continuation.Steps.Count > 24)
                        throw new PlanValidationException("继续规划改变了目标类型或超出总计 24 步的执行预算。");
                    plan = continuation;
                    run.Continuations.Add(continuation);
                    run.Steps.AddRange(plan.Steps.Select(s => new StepCheckpoint { Id = s.Id, Tool = s.Tool }));
                    pending = plan.Steps.ToList();
                    await SaveCheckpointAsync(run);
                    continue;
                }
            }
            var finalState = failed.Count == 0 ? RunState.Completed
                : requiresHuman ? RunState.NeedsInput : results.Count > 0 ? RunState.Partial : RunState.Failed;
            return await Finish(finalState, BuildSummary(run, results,
                finalState == RunState.Completed ? $"已核验 {results.Count} 个执行步骤。"
                : finalState == RunState.NeedsInput ? "需要补充信息或处理权限后才能继续。"
                : results.Count > 0 ? "任务部分完成，以下结果已核验；其余步骤未完成。" : "任务未完成，没有可确认的执行结果。"));
            }
        }
        catch (ActionOutcomeUncertainException ex)
        {
            run.RequiresManualReconciliation = true;
            run.UncertainActionEvidence = ex.Evidence;
            var uncertainStep = run.Steps.LastOrDefault(s => s.State == "running");
            if (uncertainStep is not null)
            {
                uncertainStep.State = "outcome_uncertain";
                uncertainStep.Summary = "操作可能稍后生效，最终结果尚未核验；请先人工核对，不要重复提交。";
                uncertainStep.Evidence = ex.Evidence;
                uncertainStep.BusinessOutcomeVerified = false;
                uncertainStep.ActionMayStillComplete = true;
                uncertainStep.NoAutomaticRetry = true;
            }
            return await Finish(RunState.NeedsInput, BuildSummary(run, results,
                "操作已提交，但取消或超时后无法确认最终结果；操作可能稍后生效。已停止后续执行，不会自动重试。请先人工核对实际状态，不要重复提交。"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await Finish(RunState.Cancelled, BuildSummary(run, results,
                "任务已取消。已完成的操作不会自动撤销，未执行的步骤已停止。"));
        }
        catch (Exception ex)
        {
            var message = ex switch
            {
                ModelException modelError => modelError.Message,
                ContextLimitException context => context.Message,
                PlanValidationException validation => validation.Message,
                IOException or UnauthorizedAccessException => "无法保存本地运行记录，执行已停止。",
                _ => "任务执行遇到错误，未将未核验的结果标记为完成。"
            };
            if (!checkpointAvailable)
            {
                Report(RunState.Failed, message);
                return new(run.RunId, RunState.Failed, message, artifacts.ToArray());
            }
            return await Finish(ex is ContextLimitException ? RunState.NeedsInput : results.Count > 0 ? RunState.Partial : RunState.Failed,
                BuildSummary(run, results, message));
        }
        finally { if (entered) runGate.Release(); }
    }

    public IReadOnlyList<IncompleteRun> GetIncompleteRuns()
    {
        var directory = Path.Combine(paths.Data, "Runs");
        if (!Directory.Exists(directory)) return [];
        var incomplete = new List<IncompleteRun>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var run = JsonSerializer.Deserialize<RunCheckpoint>(File.ReadAllText(file), CheckpointJson);
                if (run is null) throw new JsonException("Empty run checkpoint.");
                if (run.FinishedUtc is null || run.RequiresManualReconciliation)
                    incomplete.Add(new(run.RunId, run.State, run.UpdatedUtc, file, run.RequiresManualReconciliation));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable record cannot establish that earlier actions finished safely.
                incomplete.Add(new(Path.GetFileNameWithoutExtension(file), RunState.NeedsInput,
                    DateTimeOffset.MinValue, file, true, true));
            }
        }
        // Interrupted writes are never automatically replayed after restart.
        return incomplete.OrderByDescending(r => r.UpdatedUtc).ToArray();
    }

    private static ToolResult VerifyResult(ToolResult result)
    {
        if (result is null) return new(false, "工具没有返回结果。");
        if (!result.Success) return result;
        if (result.NeedsHuman) return new(false, result.Summary, result.Evidence, NeedsHuman: true);
        if (string.IsNullOrWhiteSpace(result.Evidence) || string.IsNullOrWhiteSpace(result.Summary))
            return new(false, "工具声称完成，但未提供可核验的结果证据。");
        foreach (var artifact in result.Artifacts ?? [])
        {
            if (artifact is null || string.IsNullOrWhiteSpace(artifact.Path) || !Path.IsPathFullyQualified(artifact.Path) ||
                (!File.Exists(artifact.Path) && !Directory.Exists(artifact.Path)))
                return new(false, "工具返回的产物不存在或路径无效，未通过完成核验。");
        }
        return result;
    }

    private static JsonElement ResolveArguments(JsonElement arguments, IReadOnlyDictionary<string, ToolResult> results)
    {
        JsonNode? Resolve(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String && PlanValidator.TryReference(value.GetString()!, out var reference))
            {
                if (!results.TryGetValue(reference!.StepId, out var result))
                    throw new PlanValidationException("引用步骤尚未产生已核验结果。");
                if (reference.Field == "summary") return JsonValue.Create(result.Summary);
                if (result.Artifacts is null || reference.ArtifactIndex >= result.Artifacts.Count)
                    throw new PlanValidationException("引用步骤没有生成所需文件，后续操作未执行。");
                return JsonValue.Create(result.Artifacts[reference.ArtifactIndex].Path);
            }
            if (value.ValueKind == JsonValueKind.Object)
            {
                var obj = new JsonObject();
                foreach (var property in value.EnumerateObject()) obj.Add(property.Name, Resolve(property.Value));
                return obj;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                var array = new JsonArray();
                foreach (var item in value.EnumerateArray()) array.Add(Resolve(item));
                return array;
            }
            return JsonNode.Parse(value.GetRawText());
        }
        return JsonSerializer.SerializeToElement(Resolve(arguments));
    }

    private static string BuildSummary(RunCheckpoint run, IReadOnlyDictionary<string, ToolResult> results, string heading)
    {
        var lines = new List<string> { heading };
        foreach (var step in run.Steps)
        {
            if (results.TryGetValue(step.Id, out var result))
                lines.Add($"• {result.Summary}\n  核验：{result.Evidence}");
            else if (step.State is "failed" or "blocked" or "outcome_uncertain") lines.Add($"• 未完成：{step.Summary}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private async Task SaveCheckpointAsync(RunCheckpoint run)
    {
        var directory = Path.Combine(paths.Data, "Runs");
        Directory.CreateDirectory(directory);
        run.UpdatedUtc = DateTimeOffset.UtcNow;
        var file = Path.Combine(directory, run.RunId + ".json");
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(run, CheckpointJson));
        File.Move(temporary, file, overwrite: true);
    }

    private async Task RecordPerformanceAsync(object entry)
    {
        await PerformanceGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(paths.Logs);
            await File.AppendAllTextAsync(Path.Combine(paths.Logs, "performance.jsonl"),
                JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { PerformanceGate.Release(); }
    }

    private sealed class RunCheckpoint
    {
        public int SchemaVersion { get; set; } = 1;
        public string RunId { get; set; } = "";
        public string Request { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? FinishedUtc { get; set; }
        public RunState State { get; set; }
        public TaskPlan? Plan { get; set; }
        public List<TaskPlan> Continuations { get; set; } = [];
        public List<TaskReview> Reviews { get; set; } = [];
        public List<StepCheckpoint> Steps { get; set; } = [];
        public bool RequiresManualReconciliation { get; set; }
        public string UncertainActionEvidence { get; set; } = "";
        public string Summary { get; set; } = "";
    }
    private sealed class StepCheckpoint
    {
        public string Id { get; set; } = "";
        public string Tool { get; set; } = "";
        public string State { get; set; } = "pending";
        public int Attempts { get; set; }
        public bool Approved { get; set; }
        public string ApprovalContext { get; set; } = "";
        public bool? BusinessOutcomeVerified { get; set; }
        public bool ActionMayStillComplete { get; set; }
        public bool NoAutomaticRetry { get; set; }
        public string Summary { get; set; } = "";
        public string Evidence { get; set; } = "";
        public List<Artifact> Artifacts { get; set; } = [];
    }
}

public sealed record IncompleteRun(string RunId, RunState LastState, DateTimeOffset UpdatedUtc, string CheckpointPath,
    bool RequiresManualReconciliation = false, bool RecordUnreadable = false);
