namespace HuiTian.Business.Core;

/// <summary>An opt-in experimental composition; it never replaces the original planner implicitly.</summary>
public sealed class RoutedTaskPlanner : ITaskPlanner, IObservationTaskPlanner
{
    private readonly SemanticIntentRouter router;
    private readonly SemanticTaskPlanner planner;

    public RoutedTaskPlanner(IModelClient model) : this(new SemanticIntentRouter(model), new SemanticTaskPlanner(model)) { }

    public RoutedTaskPlanner(SemanticIntentRouter router, SemanticTaskPlanner planner)
    {
        this.router = router;
        this.planner = planner;
    }

    public async Task<TaskPlan> PlanAsync(string request, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
    {
        var intent = await router.RouteAsync(request, history, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (intent.Mode != "task")
            return new() { Mode = intent.Mode, Answer = intent.Answer, Goal = intent.Goal, CompletionKind = "text" };
        if (intent.CapabilityGroups.Count == 0)
            return Clarify(intent.Goal, "尚未确定执行本轮完整目标所需的受支持能力，请补充要处理的对象或具体操作。");

        var selectedGroups = intent.CapabilityGroups.ToHashSet(StringComparer.Ordinal);
        foreach (var group in selectedGroups)
            if (!tools.Any(t => GroupsForTool(t.Name).Contains(group, StringComparer.Ordinal)))
                return Clarify(intent.Goal, "当前没有注册本轮目标所需的全部能力，无法生成完整执行计划。请调整目标或先启用相应工具。");
        var selectedTools = tools.Where(t => GroupsForTool(t.Name).Any(selectedGroups.Contains)).ToArray();
        // Pass the complete original request and history; the router's short goal is never substituted for them.
        var plan = await planner.PlanAsync(request, history, selectedTools, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Mode == "answer")
            return Clarify(intent.Goal, "本轮被识别为需要实际执行，但尚未生成可执行计划，不能用说明代替执行结果。请补充或调整任务要求。");
        PlanValidator.Validate(plan, selectedTools);
        return plan;
    }

    public Task<TaskReview> ReviewAsync(string request, IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolDefinition> tools, TaskPlan originalPlan, IReadOnlyDictionary<string, ToolResult> observations,
        int remainingContinuations, CancellationToken cancellationToken)
        // Observations may establish a need for another capability; the original reviewer sees all registered tools.
        => planner.ReviewAsync(request, history, tools, originalPlan, observations, remainingContinuations, cancellationToken);

    private static TaskPlan Clarify(string goal, string answer) => new()
    { Mode = "clarify", Goal = goal, Answer = answer, CompletionKind = "text" };

    private static IReadOnlyList<string> GroupsForTool(string name) => name switch
    {
        "list_files" or "search_files" => ["files"],
        "read_file" => ["files", "documents"],
        "create_document" or "change_presentation_style" or "deduplicate_worksheet" => ["documents"],
        _ when name.StartsWith("browser_", StringComparison.Ordinal) => ["browser"],
        "list_apps" or "open_app" => ["desktop"],
        _ when name.StartsWith("desktop_", StringComparison.Ordinal) => ["desktop"],
        _ => []
    };
}
