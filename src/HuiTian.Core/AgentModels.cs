namespace HuiTian.Core;

public enum AgentActivityKind
{
    UserMessage,
    AssistantMessage,
    Reasoning,
    Plan,
    Tool,
    FileChange,
    Approval,
    Status,
    Error,
}

public enum AgentActivityStatus
{
    Pending,
    InProgress,
    WaitingForApproval,
    Completed,
    Failed,
    Declined,
}

public sealed record AgentActivity(
    string Id,
    AgentActivityKind Kind,
    string Title,
    string Detail,
    AgentActivityStatus Status,
    DateTimeOffset Timestamp,
    string? ThreadId = null,
    string? TurnId = null);

public sealed record AgentPlanStep(
    string Text,
    AgentActivityStatus Status);

public sealed record AgentPlan(
    string? Explanation,
    IReadOnlyList<AgentPlanStep> Steps);

public sealed record AgentApproval(
    long RequestId,
    string Title,
    string Detail,
    string Method,
    string? ThreadId,
    string? TurnId,
    string? ItemId);

public sealed record AgentThread(
    string Id,
    string Title,
    DateTimeOffset UpdatedAt,
    bool IsActive);

public sealed record AgentConnectionState(
    bool IsConnected,
    bool IsBrainReady,
    string UserFacingStatus);

public sealed record AgentComputerAction(int X, int Y, string Detail, bool IsClick = false);

public sealed class AgentActivityEventArgs(AgentActivity activity) : EventArgs
{
    public AgentActivity Activity { get; } = activity;
}

public sealed class AgentPlanEventArgs(AgentPlan plan) : EventArgs
{
    public AgentPlan Plan { get; } = plan;
}

public sealed class AgentApprovalEventArgs(AgentApproval approval) : EventArgs
{
    public AgentApproval Approval { get; } = approval;
}

public sealed class AgentConnectionEventArgs(AgentConnectionState state) : EventArgs
{
    public AgentConnectionState State { get; } = state;
}

public sealed class AgentComputerActionEventArgs(AgentComputerAction action) : EventArgs
{
    public AgentComputerAction Action { get; } = action;
}

public sealed class AgentIntentDeclaredEventArgs(IntentResolution intent) : EventArgs
{
    public IntentResolution Intent { get; } = intent;
}

public sealed record AgentTaskOutcome(
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> Evidence,
    string? UnresolvedIssue = null);

public sealed class AgentTaskFinishedEventArgs(AgentTaskOutcome outcome) : EventArgs
{
    public AgentTaskOutcome Outcome { get; } = outcome;
}

public interface IAgentSessionClient : IAsyncDisposable
{
    event EventHandler<AgentActivityEventArgs>? ActivityReceived;

    event EventHandler<AgentPlanEventArgs>? PlanUpdated;

    event EventHandler<AgentApprovalEventArgs>? ApprovalRequested;

    event EventHandler<AgentConnectionEventArgs>? ConnectionChanged;

    event EventHandler<AgentComputerActionEventArgs>? ComputerAction;

    /// <summary>
    /// Raised when the agent has semantically compiled a real computer task.
    /// Pure conversational answers do not raise this event.
    /// </summary>
    event EventHandler<AgentIntentDeclaredEventArgs>? IntentDeclared;

    event EventHandler<AgentTaskFinishedEventArgs>? TaskFinished;

    string? CurrentThreadId { get; }

    bool HasActiveTurn { get; }

    void ConfigureAutonomy(AutonomyMode mode);

    void ConfigureContextWindow(int contextTokens);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<string> CreateThreadAsync(
        AgentTaskRoute route,
        CancellationToken cancellationToken = default);

    Task SendMessageAsync(string text, CancellationToken cancellationToken = default);

    Task SendMessageWithLocalImageAsync(
        string text,
        string localImagePath,
        CancellationToken cancellationToken = default);

    Task SendMessageWithAttachmentsAsync(
        string text,
        IReadOnlyList<string> localImagePaths,
        CancellationToken cancellationToken = default);

    Task SteerAsync(
        string text,
        IReadOnlyList<string> localImagePaths,
        CancellationToken cancellationToken = default);

    Task InterruptAsync(CancellationToken cancellationToken = default);

    Task RespondToApprovalAsync(
        long requestId,
        bool approved,
        bool rememberForSession,
        CancellationToken cancellationToken = default);
}
