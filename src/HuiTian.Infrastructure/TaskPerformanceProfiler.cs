using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed record ToolPerformanceSample(
    string Name,
    long ElapsedMilliseconds,
    bool Succeeded,
    bool UsedVision,
    bool WasFallback);

public sealed record TaskPerformanceRecord(
    string Id,
    DateTimeOffset StartedAt,
    string TaskType,
    string Domain,
    long IntentMilliseconds,
    long PlanningMilliseconds,
    long? FirstActionMilliseconds,
    long TotalMilliseconds,
    int LlmCalls,
    int ToolCalls,
    int VisionCalls,
    int Failures,
    int Fallbacks,
    bool Succeeded,
    IReadOnlyDictionary<string, long> Phases,
    IReadOnlyList<ToolPerformanceSample> Tools);

public sealed record TaskPerformanceSummary(
    int Samples,
    double P50FirstActionMilliseconds,
    double P95FirstActionMilliseconds,
    double P50TotalMilliseconds,
    double P95TotalMilliseconds,
    double SuccessRate,
    double AverageVisionCalls,
    double AverageFallbacks);

public sealed class TaskPerformanceSession
{
    private readonly TaskPerformanceProfiler _owner;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, long> _phases =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ToolPerformanceSample> _tools = new();
    private int _completed;
    private int _llmCalls;
    private int _visionCalls;
    private int _failures;
    private int _fallbacks;
    private long _firstActionMilliseconds = -1;

    internal TaskPerformanceSession(
        TaskPerformanceProfiler owner,
        string id,
        DateTimeOffset startedAt)
    {
        _owner = owner;
        Id = id;
        StartedAt = startedAt;
    }

    public string Id { get; }

    public DateTimeOffset StartedAt { get; }

    public string TaskType { get; private set; } = "Unknown";

    public string Domain { get; private set; } = "Unknown";

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public void SetIntent(IntentResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        TaskType = resolution.TaskType.ToString();
        Domain = resolution.Domain.ToString();
        RecordPhase("intent", resolution.ElapsedMilliseconds);
        if (resolution.Source == "local-semantic-engine")
        {
            Interlocked.Increment(ref _llmCalls);
        }

        if (resolution.Source.Contains("fallback", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _fallbacks);
        }
    }

    public void RecordPhase(string name, long elapsedMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(name) || IsCompleted) return;
        _phases[name.Trim()] = Math.Max(0, elapsedMilliseconds);
    }

    public void RecordLlmCall() => Interlocked.Increment(ref _llmCalls);

    public void MarkFirstAction()
    {
        if (IsCompleted) return;
        var elapsed = _total.ElapsedMilliseconds;
        Interlocked.CompareExchange(ref _firstActionMilliseconds, elapsed, -1);
    }

    public void RecordTool(
        string name,
        long elapsedMilliseconds,
        bool succeeded,
        bool usedVision = false,
        bool wasFallback = false)
    {
        if (IsCompleted) return;
        MarkFirstAction();
        _tools.Enqueue(new ToolPerformanceSample(
            string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim(),
            Math.Max(0, elapsedMilliseconds),
            succeeded,
            usedVision,
            wasFallback));
        if (usedVision) Interlocked.Increment(ref _visionCalls);
        if (!succeeded) Interlocked.Increment(ref _failures);
        if (wasFallback) Interlocked.Increment(ref _fallbacks);
    }

    public Task CompleteAsync(bool succeeded)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return Task.CompletedTask;
        }

        _total.Stop();
        var phases = new Dictionary<string, long>(_phases, StringComparer.OrdinalIgnoreCase);
        var tools = _tools.ToArray();
        var record = new TaskPerformanceRecord(
            Id,
            StartedAt,
            TaskType,
            Domain,
            phases.GetValueOrDefault("intent"),
            phases.GetValueOrDefault("planning"),
            Volatile.Read(ref _firstActionMilliseconds) is var first && first >= 0
                ? first
                : null,
            _total.ElapsedMilliseconds,
            Volatile.Read(ref _llmCalls),
            tools.Length,
            Volatile.Read(ref _visionCalls),
            Volatile.Read(ref _failures),
            Volatile.Read(ref _fallbacks),
            succeeded,
            phases,
            tools);
        return _owner.WriteAsync(record);
    }
}

/// <summary>
/// Writes privacy-minimal performance telemetry to D:\HuiTian.  User text,
/// file contents, contact names, and model output are intentionally excluded.
/// </summary>
public sealed class TaskPerformanceProfiler
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public TaskPerformanceProfiler(HuiTianPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.AppState, "task-performance.jsonl");
    }

    public string LogPath => _path;

    public TaskPerformanceSession StartSession(string? id = null) => new(
        this,
        string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim(),
        DateTimeOffset.Now);

    internal async Task WriteAsync(TaskPerformanceRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var line = JsonSerializer.Serialize(record, _options);
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(_path, line + Environment.NewLine)
                    .ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }
        catch
        {
            // Profiling must never slow down or fail the user task.
        }
    }

    public async Task<TaskPerformanceSummary> ReadSummaryAsync(
        int maximumSamples = 500,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return EmptySummary();
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return EmptySummary();
        }

        var records = new List<TaskPerformanceRecord>();
        foreach (var line in lines.TakeLast(Math.Max(1, maximumSamples)))
        {
            try
            {
                var record = JsonSerializer.Deserialize<TaskPerformanceRecord>(line, _options);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // A partially written final line does not invalidate older data.
            }
        }

        if (records.Count == 0) return EmptySummary();
        var firstActions = records
            .Where(record => record.FirstActionMilliseconds.HasValue)
            .Select(record => (double)record.FirstActionMilliseconds!.Value)
            .Order()
            .ToArray();
        var totals = records.Select(record => (double)record.TotalMilliseconds).Order().ToArray();
        return new TaskPerformanceSummary(
            records.Count,
            Percentile(firstActions, 0.50),
            Percentile(firstActions, 0.95),
            Percentile(totals, 0.50),
            Percentile(totals, 0.95),
            records.Count(record => record.Succeeded) / (double)records.Count,
            records.Average(record => record.VisionCalls),
            records.Average(record => record.Fallbacks));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var rank = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high) return sorted[low];
        var fraction = rank - low;
        return sorted[low] + (sorted[high] - sorted[low]) * fraction;
    }

    private static TaskPerformanceSummary EmptySummary() => new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}
