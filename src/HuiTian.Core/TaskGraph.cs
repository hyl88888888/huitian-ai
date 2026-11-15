using System.Text.Json;
using System.Text.Json.Serialization;

namespace HuiTian.Core;

public enum TaskGraphStatus
{
    Pending,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Cancelled,
}

public enum TaskStepStatus
{
    Pending,
    Ready,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Blocked,
}

public sealed record TaskStepAttempt(
    int Number,
    string Strategy,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Succeeded,
    string? Detail);

public sealed class TaskGraphStep
{
    internal TaskGraphStep(IntentPlanStep definition)
    {
        Definition = definition;
    }

    public IntentPlanStep Definition { get; }

    public TaskStepStatus Status { get; internal set; } = TaskStepStatus.Pending;

    public string? Result { get; internal set; }

    public string? VerificationEvidence { get; internal set; }

    public string? Failure { get; internal set; }

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? FinishedAt { get; internal set; }

    public IReadOnlyList<TaskStepAttempt> Attempts => _attempts.AsReadOnly();

    internal List<TaskStepAttempt> MutableAttempts => _attempts;

    private readonly List<TaskStepAttempt> _attempts = [];
}

/// <summary>
/// Persistent, revisioned execution state.  A step can start only when all of
/// its dependencies are verified as successful.  Failed attempts are kept so
/// recovery must select a different strategy instead of repeating forever.
/// </summary>
public sealed class TaskGraph
{
    private readonly Dictionary<string, TaskGraphStep> _steps;
    private readonly object _sync = new();

    public TaskGraph(string id, string goal, IReadOnlyList<IntentPlanStep> steps)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Task id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("Task goal is required.", nameof(goal));
        if (steps is not { Count: > 0 }) throw new ArgumentException("At least one task step is required.", nameof(steps));

        Id = id.Trim();
        Goal = goal.Trim();
        CreatedAt = DateTimeOffset.Now;
        UpdatedAt = CreatedAt;
        _steps = new Dictionary<string, TaskGraphStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in steps)
        {
            if (!_steps.TryAdd(definition.Id, new TaskGraphStep(definition)))
            {
                throw new ArgumentException($"Duplicate task step id '{definition.Id}'.", nameof(steps));
            }
        }

        ValidateDependencies();
        RefreshReadyState();
    }

    public string Id { get; }

    public string Goal { get; }

    public long Revision { get; private set; }

    public TaskGraphStatus Status { get; private set; } = TaskGraphStatus.Pending;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string? FinalEvidence { get; private set; }

    public IReadOnlyList<TaskGraphStep> Steps
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_steps.Values.ToArray());
            }
        }
    }

    public static TaskGraph FromIntent(IntentResolution intent, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var plan = intent.Plan.Count > 0
            ? intent.Plan
            : new[]
            {
                new IntentPlanStep(
                    "step-1",
                    intent.Goal,
                    intent.Actions.FirstOrDefault() ?? "complete-goal",
                    intent.Applications.FirstOrDefault(),
                    intent.Entities.FirstOrDefault()?.EffectiveValue,
                    Array.Empty<string>(),
                    intent.SuccessConditions,
                    intent.Risk),
            };
        return new TaskGraph(id ?? Guid.NewGuid().ToString("N"), intent.Goal, plan);
    }

    public IReadOnlyList<TaskGraphStep> GetReadySteps()
    {
        lock (_sync)
        {
            RefreshReadyState();
            return Array.AsReadOnly(_steps.Values
                .Where(step => step.Status == TaskStepStatus.Ready)
                .ToArray());
        }
    }

    public void StartStep(string stepId, string strategy)
    {
        lock (_sync)
        {
            var step = GetStep(stepId);
            if (step.Status is not (TaskStepStatus.Ready or TaskStepStatus.Failed))
            {
                throw new InvalidOperationException(
                    $"Step '{stepId}' cannot start from state {step.Status}.");
            }

            if (string.IsNullOrWhiteSpace(strategy))
            {
                throw new ArgumentException("An execution strategy is required.", nameof(strategy));
            }

            if (step.MutableAttempts.Any(attempt =>
                    attempt.Strategy.Equals(strategy.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    !attempt.Succeeded))
            {
                throw new InvalidOperationException(
                    $"Failed strategy '{strategy}' cannot be repeated for step '{stepId}'.");
            }

            var now = DateTimeOffset.Now;
            step.Status = TaskStepStatus.Running;
            step.StartedAt ??= now;
            step.FinishedAt = null;
            step.Failure = null;
            step.MutableAttempts.Add(new TaskStepAttempt(
                step.MutableAttempts.Count + 1,
                strategy.Trim(),
                now,
                null,
                false,
                null));
            Status = TaskGraphStatus.Running;
            Touch();
        }
    }

    public void CompleteStep(string stepId, string result, string verificationEvidence)
    {
        lock (_sync)
        {
            var step = GetStep(stepId);
            if (step.Status != TaskStepStatus.Running)
            {
                throw new InvalidOperationException($"Step '{stepId}' is not running.");
            }

            if (string.IsNullOrWhiteSpace(verificationEvidence))
            {
                throw new ArgumentException(
                    "A step cannot succeed without verification evidence.",
                    nameof(verificationEvidence));
            }

            var now = DateTimeOffset.Now;
            step.Status = TaskStepStatus.Succeeded;
            step.Result = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
            step.VerificationEvidence = verificationEvidence.Trim();
            step.FinishedAt = now;
            FinishAttempt(step, succeeded: true, step.VerificationEvidence, now);
            RefreshReadyState();
            if (_steps.Values.All(item =>
                    item.Status is TaskStepStatus.Succeeded or TaskStepStatus.Skipped))
            {
                Status = TaskGraphStatus.Succeeded;
                FinalEvidence = string.Join(" | ", _steps.Values
                    .Where(item => item.Status == TaskStepStatus.Succeeded)
                    .Select(item => item.VerificationEvidence)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            Touch();
        }
    }

    public void FailStep(string stepId, string failure)
    {
        lock (_sync)
        {
            var step = GetStep(stepId);
            if (step.Status != TaskStepStatus.Running)
            {
                throw new InvalidOperationException($"Step '{stepId}' is not running.");
            }

            var now = DateTimeOffset.Now;
            step.Status = TaskStepStatus.Failed;
            step.Failure = string.IsNullOrWhiteSpace(failure)
                ? "The step failed without a diagnostic."
                : failure.Trim();
            step.FinishedAt = now;
            FinishAttempt(step, succeeded: false, step.Failure, now);
            Status = TaskGraphStatus.Waiting;
            BlockDependants(stepId);
            Touch();
        }
    }

    public void PrepareRecovery(string stepId, string nextStrategy)
    {
        lock (_sync)
        {
            var step = GetStep(stepId);
            if (step.Status != TaskStepStatus.Failed)
            {
                throw new InvalidOperationException($"Step '{stepId}' has not failed.");
            }

            if (string.IsNullOrWhiteSpace(nextStrategy) || step.MutableAttempts.Any(attempt =>
                    attempt.Strategy.Equals(nextStrategy.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Recovery must select a new strategy.");
            }

            step.Status = TaskStepStatus.Ready;
            step.Failure = null;
            foreach (var dependant in _steps.Values.Where(candidate =>
                         candidate.Status == TaskStepStatus.Blocked &&
                         candidate.Definition.DependsOn.Contains(
                             stepId,
                             StringComparer.OrdinalIgnoreCase)))
            {
                dependant.Status = TaskStepStatus.Pending;
            }

            Status = TaskGraphStatus.Running;
            Touch();
        }
    }

    public void MarkUnrecoverable(string stepId, string detail)
    {
        lock (_sync)
        {
            var step = GetStep(stepId);
            if (step.Status != TaskStepStatus.Failed)
            {
                throw new InvalidOperationException($"Step '{stepId}' has not failed.");
            }

            step.Failure = string.IsNullOrWhiteSpace(detail) ? step.Failure : detail.Trim();
            Status = TaskGraphStatus.Failed;
            Touch();
        }
    }

    public void Cancel(string reason)
    {
        lock (_sync)
        {
            foreach (var step in _steps.Values.Where(step =>
                         step.Status is TaskStepStatus.Pending or TaskStepStatus.Ready or
                         TaskStepStatus.Running or TaskStepStatus.Blocked))
            {
                step.Status = TaskStepStatus.Cancelled;
                step.Failure = string.IsNullOrWhiteSpace(reason) ? "Cancelled." : reason.Trim();
                step.FinishedAt = DateTimeOffset.Now;
                if (step.MutableAttempts.LastOrDefault() is { FinishedAt: null } attempt)
                {
                    step.MutableAttempts[^1] = attempt with
                    {
                        FinishedAt = step.FinishedAt,
                        Detail = step.Failure,
                    };
                }
            }

            Status = TaskGraphStatus.Cancelled;
            Touch();
        }
    }

    public string FormatForAgent()
    {
        lock (_sync)
        {
            return JsonSerializer.Serialize(new
            {
                id = Id,
                goal = Goal,
                revision = Revision,
                status = ToAgentValue(Status),
                steps = _steps.Values.Select(step => new
                {
                    id = step.Definition.Id,
                    title = step.Definition.Title,
                    action = step.Definition.Action,
                    app = step.Definition.App,
                    target = step.Definition.Target,
                    depends_on = step.Definition.DependsOn,
                    status = ToAgentValue(step.Status),
                    success_conditions = step.Definition.SuccessConditions.Select(condition => new
                    {
                        kind = JsonNamingPolicy.SnakeCaseLower.ConvertName(condition.Kind.ToString()),
                        description = condition.Description,
                    }),
                    prior_failed_strategies = step.Attempts
                        .Where(attempt => !attempt.Succeeded)
                        .Select(attempt => attempt.Strategy),
                }),
            }, JsonOptions);
        }
    }

    private TaskGraphStep GetStep(string stepId)
    {
        if (string.IsNullOrWhiteSpace(stepId) || !_steps.TryGetValue(stepId, out var step))
        {
            throw new KeyNotFoundException($"Unknown task step '{stepId}'.");
        }

        return step;
    }

    private void RefreshReadyState()
    {
        foreach (var step in _steps.Values.Where(step =>
                     step.Status is TaskStepStatus.Pending or TaskStepStatus.Blocked))
        {
            var dependencies = step.Definition.DependsOn.Select(GetStep).ToArray();
            if (dependencies.Any(dependency =>
                    dependency.Status is TaskStepStatus.Failed or TaskStepStatus.Cancelled or
                    TaskStepStatus.Blocked))
            {
                step.Status = TaskStepStatus.Blocked;
            }
            else if (dependencies.All(dependency =>
                         dependency.Status is TaskStepStatus.Succeeded or TaskStepStatus.Skipped))
            {
                step.Status = TaskStepStatus.Ready;
            }
            else
            {
                step.Status = TaskStepStatus.Pending;
            }
        }
    }

    private void BlockDependants(string failedStepId)
    {
        foreach (var dependant in _steps.Values.Where(candidate =>
                     candidate.Definition.DependsOn.Contains(
                         failedStepId,
                         StringComparer.OrdinalIgnoreCase)))
        {
            if (dependant.Status is TaskStepStatus.Pending or TaskStepStatus.Ready)
            {
                dependant.Status = TaskStepStatus.Blocked;
                BlockDependants(dependant.Definition.Id);
            }
        }
    }

    private void ValidateDependencies()
    {
        foreach (var step in _steps.Values)
        {
            foreach (var dependency in step.Definition.DependsOn)
            {
                if (!_steps.ContainsKey(dependency))
                {
                    throw new ArgumentException(
                        $"Step '{step.Definition.Id}' depends on missing step '{dependency}'.");
                }

                if (dependency.Equals(step.Definition.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Step '{step.Definition.Id}' cannot depend on itself.");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in _steps.Values)
        {
            Visit(step.Definition.Id, visiting, visited);
        }
    }

    private void Visit(string stepId, HashSet<string> visiting, HashSet<string> visited)
    {
        if (visited.Contains(stepId)) return;
        if (!visiting.Add(stepId))
        {
            throw new ArgumentException("Task graph contains a dependency cycle.");
        }

        foreach (var dependency in GetStep(stepId).Definition.DependsOn)
        {
            Visit(dependency, visiting, visited);
        }

        visiting.Remove(stepId);
        visited.Add(stepId);
    }

    private static void FinishAttempt(
        TaskGraphStep step,
        bool succeeded,
        string? detail,
        DateTimeOffset finishedAt)
    {
        if (step.MutableAttempts.LastOrDefault() is not { FinishedAt: null } attempt)
        {
            throw new InvalidOperationException("The running step has no active attempt.");
        }

        step.MutableAttempts[^1] = attempt with
        {
            FinishedAt = finishedAt,
            Succeeded = succeeded,
            Detail = detail,
        };
    }

    private void Touch()
    {
        Revision++;
        UpdatedAt = DateTimeOffset.Now;
    }

    private static string ToAgentValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
