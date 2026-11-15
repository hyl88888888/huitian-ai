namespace HuiTian.Core;

public enum ComputerTaskRouteStatus
{
    Selected,
    RequiresClarification,
    InvalidContract,
    NoCapability,
    ChannelsExhausted,
}

/// <summary>
/// A lightweight hand-off from policy/UI routing to a concrete executor.
/// </summary>
public sealed class ComputerTaskRoute
{
    private ComputerTaskRoute(
        ComputerTaskRouteStatus status,
        ApplicationCapability? capability,
        int priorFailuresOnChannel,
        string detail)
    {
        Status = status;
        Capability = capability;
        PriorFailuresOnChannel = priorFailuresOnChannel;
        Detail = detail;
    }

    public ComputerTaskRouteStatus Status { get; }

    public ApplicationCapability? Capability { get; }

    public ComputerTaskChannel? Channel => Capability?.Channel;

    public int PriorFailuresOnChannel { get; }

    public string Detail { get; }

    public bool IsSelected => Status == ComputerTaskRouteStatus.Selected;

    internal static ComputerTaskRoute Selected(
        ApplicationCapability capability,
        int priorFailures) =>
        new(
            ComputerTaskRouteStatus.Selected,
            capability,
            priorFailures,
            $"Selected capability '{capability.Id}' via {capability.Channel}.");

    internal static ComputerTaskRoute NotSelected(
        ComputerTaskRouteStatus status,
        string detail) =>
        new(status, null, 0, detail);
}

/// <summary>
/// Per-task failure state. A channel remains eligible after its first failure
/// and is excluded after its second, preventing a third same-channel attempt.
/// </summary>
public sealed class ComputerTaskAttemptState
{
    public const int MaximumFailuresPerChannel = 2;

    private readonly object _sync = new();
    private readonly Dictionary<ComputerTaskChannel, int> _failureCounts = [];

    public int GetFailureCount(ComputerTaskChannel channel)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            return _failureCounts.GetValueOrDefault(channel);
        }
    }

    public bool CanAttempt(ComputerTaskChannel channel) =>
        GetFailureCount(channel) < MaximumFailuresPerChannel;

    public int RecordFailure(ComputerTaskChannel channel)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            var failures = Math.Min(
                MaximumFailuresPerChannel,
                _failureCounts.GetValueOrDefault(channel) + 1);
            _failureCounts[channel] = failures;
            return failures;
        }
    }

    public int RecordFailure(ComputerTaskRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Channel is not { } channel)
        {
            throw new InvalidOperationException(
                "Only a selected route can record a channel failure.");
        }

        return RecordFailure(channel);
    }

    public void RecordSuccess(ComputerTaskChannel channel)
    {
        ValidateChannel(channel);
        lock (_sync)
        {
            _failureCounts.Remove(channel);
        }
    }

    public void RecordSuccess(ComputerTaskRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Channel is not { } channel)
        {
            throw new InvalidOperationException(
                "Only a selected route can record channel success.");
        }

        RecordSuccess(channel);
    }

    private static void ValidateChannel(ComputerTaskChannel channel)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}

/// <summary>
/// MainWindow or a future ConversationPolicy adapter can depend on this
/// interface without learning executor details.
/// </summary>
public interface IComputerTaskChannelSelector
{
    ComputerTaskRoute Select(
        ComputerTaskContract task,
        ComputerTaskAttemptState attemptState);
}

public sealed class PriorityComputerTaskChannelSelector : IComputerTaskChannelSelector
{
    private readonly IApplicationCapabilityRegistry _registry;

    public PriorityComputerTaskChannelSelector(
        IApplicationCapabilityRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ComputerTaskRoute Select(
        ComputerTaskContract task,
        ComputerTaskAttemptState attemptState)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(attemptState);

        if (task.Ambiguity.RequiresClarification)
        {
            return ComputerTaskRoute.NotSelected(
                ComputerTaskRouteStatus.RequiresClarification,
                task.Ambiguity.ClarificationQuestion ??
                task.Ambiguity.Reason ??
                "The task requires clarification.");
        }

        var blockers = task.GetExecutionBlockers();
        if (blockers.Count > 0)
        {
            return ComputerTaskRoute.NotSelected(
                ComputerTaskRouteStatus.InvalidContract,
                string.Join(" ", blockers));
        }

        var candidates = _registry.FindCapabilities(task);
        if (candidates.Count == 0)
        {
            return ComputerTaskRoute.NotSelected(
                ComputerTaskRouteStatus.NoCapability,
                $"No capability is registered for app '{task.App ?? ApplicationCapability.Any}' " +
                $"and operation '{task.Operation}'.");
        }

        foreach (var capability in candidates)
        {
            if (!attemptState.CanAttempt(capability.Channel))
            {
                continue;
            }

            return ComputerTaskRoute.Selected(
                capability,
                attemptState.GetFailureCount(capability.Channel));
        }

        return ComputerTaskRoute.NotSelected(
            ComputerTaskRouteStatus.ChannelsExhausted,
            "Every matching execution channel has failed twice.");
    }
}
