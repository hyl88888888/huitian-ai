namespace HuiTian.Core;

/// <summary>
/// Execution channels are ordered from the most structured and verifiable to
/// the least structured fallback.
/// </summary>
public enum ComputerTaskChannel
{
    Api,
    Native,
    AppAdapter,
    DomOrUia,
    Shortcut,
    Visual,
}

public static class ComputerTaskChannelPriority
{
    private static readonly IReadOnlyList<ComputerTaskChannel> PriorityOrder =
        Array.AsReadOnly(
        [
            ComputerTaskChannel.Api,
            ComputerTaskChannel.Native,
            ComputerTaskChannel.AppAdapter,
            ComputerTaskChannel.DomOrUia,
            ComputerTaskChannel.Shortcut,
            ComputerTaskChannel.Visual,
        ]);

    public static IReadOnlyList<ComputerTaskChannel> Ordered => PriorityOrder;

    public static int GetPriority(ComputerTaskChannel channel) => channel switch
    {
        ComputerTaskChannel.Api => 0,
        ComputerTaskChannel.Native => 1,
        ComputerTaskChannel.AppAdapter => 2,
        ComputerTaskChannel.DomOrUia => 3,
        ComputerTaskChannel.Shortcut => 4,
        ComputerTaskChannel.Visual => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}

/// <summary>
/// Describes one executable application operation. ExecutorId is the stable
/// hand-off key that Infrastructure can bind to an API client, native bridge,
/// semantic UI driver, or visual executor.
/// </summary>
public sealed class ApplicationCapability
{
    public const string Any = "*";

    private readonly Func<ComputerTaskContract, bool>? _additionalMatch;

    public ApplicationCapability(
        string id,
        string app,
        string operation,
        ComputerTaskChannel channel,
        string executorId,
        Func<ComputerTaskContract, bool>? additionalMatch = null)
    {
        if (!Enum.IsDefined(channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        Id = RequireText(id, nameof(id));
        App = RequireText(app, nameof(app));
        Operation = RequireText(operation, nameof(operation));
        Channel = channel;
        ExecutorId = RequireText(executorId, nameof(executorId));
        _additionalMatch = additionalMatch;
    }

    public string Id { get; }

    public string App { get; }

    public string Operation { get; }

    public ComputerTaskChannel Channel { get; }

    public string ExecutorId { get; }

    public bool Matches(ComputerTaskContract task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return MatchesValue(App, task.App) &&
               MatchesValue(Operation, task.Operation) &&
               (_additionalMatch?.Invoke(task) ?? true);
    }

    internal int GetSpecificity(ComputerTaskContract task)
    {
        var appSpecificity = App == Any ? 0 : 2;
        var operationSpecificity = Operation == Any ? 0 : 1;
        return appSpecificity + operationSpecificity;
    }

    private static bool MatchesValue(string registered, string? requested) =>
        registered == Any ||
        (requested is not null &&
         registered.Equals(requested, StringComparison.OrdinalIgnoreCase));

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value.Trim();
    }
}

public interface IApplicationCapabilityRegistry
{
    void Register(ApplicationCapability capability);

    bool Unregister(string capabilityId);

    IReadOnlyList<ApplicationCapability> FindCapabilities(
        ComputerTaskContract task);

    IReadOnlyList<ApplicationCapability> Snapshot();
}

/// <summary>
/// A small, thread-safe in-memory registry. Application and operation matching
/// is ordinal and case-insensitive; "*" registers a generic fallback.
/// </summary>
public sealed class ApplicationCapabilityRegistry : IApplicationCapabilityRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RegisteredCapability> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextSequence;

    public void Register(ApplicationCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (_sync)
        {
            if (_capabilities.ContainsKey(capability.Id))
            {
                throw new InvalidOperationException(
                    $"A capability with id '{capability.Id}' is already registered.");
            }

            _capabilities.Add(
                capability.Id,
                new RegisteredCapability(capability, _nextSequence++));
        }
    }

    public bool Unregister(string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
        {
            return false;
        }

        lock (_sync)
        {
            return _capabilities.Remove(capabilityId.Trim());
        }
    }

    public IReadOnlyList<ApplicationCapability> FindCapabilities(
        ComputerTaskContract task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            var matches = _capabilities.Values
                .Where(entry => entry.Capability.Matches(task))
                .OrderBy(entry =>
                    ComputerTaskChannelPriority.GetPriority(entry.Capability.Channel))
                .ThenByDescending(entry => entry.Capability.GetSpecificity(task))
                .ThenBy(entry => entry.Sequence)
                .Select(entry => entry.Capability)
                .ToArray();
            return Array.AsReadOnly(matches);
        }
    }

    public IReadOnlyList<ApplicationCapability> Snapshot()
    {
        lock (_sync)
        {
            var snapshot = _capabilities.Values
                .OrderBy(entry => entry.Sequence)
                .Select(entry => entry.Capability)
                .ToArray();
            return Array.AsReadOnly(snapshot);
        }
    }

    private sealed record RegisteredCapability(
        ApplicationCapability Capability,
        long Sequence);
}
