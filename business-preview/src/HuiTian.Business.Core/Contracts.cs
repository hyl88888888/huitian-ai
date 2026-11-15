using System.Text.Json;

namespace HuiTian.Business.Core;

public enum RiskLevel { ReadOnly, Reversible, External, Critical }
public enum RunState { Planning, Running, AwaitingApproval, NeedsInput, Completed, Partial, Failed, Cancelled, Answered }
public record ChatMessage(string Role, string Content);
public record Artifact(string Path, string Name, string Kind);
public record ToolDefinition(string Name, string Description, JsonElement Parameters, RiskLevel Risk);
public record ToolResult(bool Success, string Summary, string Evidence = "", IReadOnlyList<Artifact>? Artifacts = null, bool NeedsHuman = false, bool? BusinessOutcomeVerified = null);
public record AgentEvent(string RunId, RunState State, string Message, string? StepId = null, Artifact? Artifact = null);
public record RunResult(string RunId, RunState State, string Summary, IReadOnlyList<Artifact> Artifacts);

public sealed class TaskPlan
{
    public string Goal { get; set; } = "";
    public string Mode { get; set; } = "answer";
    public string Answer { get; set; } = "";
    public string SuccessCondition { get; set; } = "";
    public string CompletionKind { get; set; } = "observation";
    public bool RequiresObservationReview { get; set; }
    public List<PlanStep> Steps { get; set; } = [];
}
public sealed class PlanStep
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Tool { get; set; } = "";
    public JsonElement Arguments { get; set; } = JsonSerializer.SerializeToElement(new { });
    public List<string> DependsOn { get; set; } = [];
}
public interface IModelClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, int maxTokens, CancellationToken cancellationToken);
}
public interface IToolRegistry
{
    IReadOnlyList<ToolDefinition> Definitions { get; }
    Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
public interface IApprovalContextProvider
{
    Task<string> DescribeApprovalAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
public sealed class ActionOutcomeUncertainException(string message, string evidence, CancellationToken cancellationToken)
    : OperationCanceledException(message, cancellationToken)
{
    public string Evidence { get; } = evidence;
}
public interface ITaskPlanner
{
    Task<TaskPlan> PlanAsync(string request, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken);
}
public delegate Task<bool> ApprovalHandler(ToolDefinition tool, PlanStep step, CancellationToken cancellationToken);

public sealed class BusinessPaths
{
    public string Root { get; }
    public string Data => System.IO.Path.Combine(Root, "Data");
    public string Logs => System.IO.Path.Combine(Root, "Logs");
    public string Models => System.IO.Path.Combine(Root, "Models");
    public string Runtime => System.IO.Path.Combine(Root, "Runtime");
    public string Outputs => System.IO.Path.Combine(Root, "Outputs");
    public string Temp => System.IO.Path.Combine(Root, "Cache", "Temp");
    public BusinessPaths(string root) => Root = System.IO.Path.GetFullPath(root);
    public void EnsureDirectories()
    {
        foreach (var path in new[] { Root, Data, Logs, Models, Runtime, Outputs, Temp }) Directory.CreateDirectory(path);
    }
    public static BusinessPaths Discover()
    {
        var configured = Environment.GetEnvironmentVariable("HUITIAN_BUSINESS_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured);
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(System.IO.Path.Combine(d.FullName, "product.marker"))) return new(d.FullName);
        return new(System.IO.Path.Combine(AppContext.BaseDirectory, "HuiTianData"));
    }
}
