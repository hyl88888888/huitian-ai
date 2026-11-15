using System.Text.Json;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed class ExecutionMemoryEntry
{
    public string App { get; set; } = ApplicationCapability.Any;

    public string Operation { get; set; } = ApplicationCapability.Any;

    public ComputerTaskChannel Channel { get; set; }

    public string StrategyId { get; set; } = string.Empty;

    public string? ApplicationVersion { get; set; }

    public int VerifiedSuccesses { get; set; }

    public int Failures { get; set; }

    public double AverageDurationMilliseconds { get; set; }

    public string VerificationKind { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public double Confidence => Math.Round(
        (VerifiedSuccesses + 1d) / (VerifiedSuccesses + Failures + 2d),
        4);

    public bool IsTrusted => VerifiedSuccesses >= 2 && Confidence >= 0.70;
}

/// <summary>
/// Stores only structured, verified execution outcomes.  It never keys a
/// learned route by the user's sentence and therefore generalizes across new
/// wording that resolves to the same app and operation.
/// </summary>
public sealed class ExecutionMemoryStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private List<ExecutionMemoryEntry> _entries = [];

    public ExecutionMemoryStore(HuiTianPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.AppState, "execution-memory.json");
        Load();
    }

    public static ExecutionMemoryStore Default { get; } = new(new HuiTianPaths());

    public IReadOnlyList<ExecutionMemoryEntry> Find(
        ComputerTaskContract task,
        int maximum = 5)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            return Array.AsReadOnly(_entries
                .Where(entry => Matches(entry.App, task.App) &&
                                Matches(entry.Operation, task.Operation))
                .OrderByDescending(entry => entry.IsTrusted)
                .ThenByDescending(entry => entry.Confidence)
                .ThenBy(entry => entry.AverageDurationMilliseconds)
                .ThenBy(entry => ComputerTaskChannelPriority.GetPriority(entry.Channel))
                .Take(Math.Max(1, maximum))
                .Select(Clone)
                .ToArray());
        }
    }

    public void Record(
        string? app,
        string operation,
        ComputerTaskChannel channel,
        string strategyId,
        bool succeeded,
        bool verified,
        long elapsedMilliseconds,
        string verificationKind,
        string? applicationVersion = null)
    {
        if (string.IsNullOrWhiteSpace(operation) || string.IsNullOrWhiteSpace(strategyId)) return;
        var normalizedApp = string.IsNullOrWhiteSpace(app)
            ? ApplicationCapability.Any
            : app.Trim();
        var normalizedOperation = operation.Trim();
        var normalizedStrategy = strategyId.Trim();
        lock (_sync)
        {
            var entry = _entries.FirstOrDefault(item =>
                item.App.Equals(normalizedApp, StringComparison.OrdinalIgnoreCase) &&
                item.Operation.Equals(normalizedOperation, StringComparison.OrdinalIgnoreCase) &&
                item.Channel == channel &&
                item.StrategyId.Equals(normalizedStrategy, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.ApplicationVersion,
                    applicationVersion,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new ExecutionMemoryEntry
                {
                    App = normalizedApp,
                    Operation = normalizedOperation,
                    Channel = channel,
                    StrategyId = normalizedStrategy,
                    ApplicationVersion = string.IsNullOrWhiteSpace(applicationVersion)
                        ? null
                        : applicationVersion.Trim(),
                };
                _entries.Add(entry);
            }

            var priorRuns = entry.VerifiedSuccesses + entry.Failures;
            if (succeeded && verified)
            {
                entry.VerifiedSuccesses++;
                var elapsed = Math.Max(0, elapsedMilliseconds);
                entry.AverageDurationMilliseconds = priorRuns == 0
                    ? elapsed
                    : (entry.AverageDurationMilliseconds * priorRuns + elapsed) /
                      (priorRuns + 1d);
                entry.VerificationKind = string.IsNullOrWhiteSpace(verificationKind)
                    ? "verified-result"
                    : verificationKind.Trim();
            }
            else
            {
                entry.Failures++;
            }

            entry.UpdatedAt = DateTimeOffset.Now;
            _entries = _entries
                .OrderByDescending(item => item.UpdatedAt)
                .Take(600)
                .ToList();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        ExecutionMemoryEntry[] snapshot;
        lock (_sync)
        {
            snapshot = _entries.Select(Clone).ToArray();
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    new System.Text.UTF8Encoding(false),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<List<ExecutionMemoryEntry>>(
                File.ReadAllText(_path),
                JsonOptions);
            if (entries is null) return;
            lock (_sync)
            {
                _entries = entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.App) &&
                                    !string.IsNullOrWhiteSpace(entry.Operation) &&
                                    !string.IsNullOrWhiteSpace(entry.StrategyId) &&
                                    Enum.IsDefined(entry.Channel))
                    .Take(600)
                    .ToList();
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }
    }

    private static bool Matches(string registered, string? requested) =>
        registered == ApplicationCapability.Any ||
        (!string.IsNullOrWhiteSpace(requested) &&
         registered.Equals(requested, StringComparison.OrdinalIgnoreCase));

    private static ExecutionMemoryEntry Clone(ExecutionMemoryEntry entry) => new()
    {
        App = entry.App,
        Operation = entry.Operation,
        Channel = entry.Channel,
        StrategyId = entry.StrategyId,
        ApplicationVersion = entry.ApplicationVersion,
        VerifiedSuccesses = entry.VerifiedSuccesses,
        Failures = entry.Failures,
        AverageDurationMilliseconds = entry.AverageDurationMilliseconds,
        VerificationKind = entry.VerificationKind,
        UpdatedAt = entry.UpdatedAt,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
