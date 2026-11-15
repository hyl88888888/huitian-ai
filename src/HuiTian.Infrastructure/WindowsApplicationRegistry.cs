using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace HuiTian.Infrastructure;

public enum ApplicationLaunchKind
{
    Executable,
    Shortcut,
    Uri,
}

public sealed record WindowsApplicationRecord(
    string Id,
    string DisplayName,
    string LaunchTarget,
    ApplicationLaunchKind LaunchKind,
    string? ProcessName,
    string Source,
    IReadOnlyList<string> Aliases,
    DateTimeOffset DiscoveredAt);

/// <summary>
/// A D-drive persisted index of launchable Windows applications.  Discovery is
/// done outside the user task; launch requests use an in-memory lookup and do
/// not recursively scan Start Menu folders on every turn.
/// </summary>
public sealed class WindowsApplicationRegistry
{
    private const int CacheVersion = 1;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Dictionary<string, WindowsApplicationRecord> _records =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _refreshedAt;

    public WindowsApplicationRegistry(HuiTianPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _path = Path.Combine(paths.AppState, "application-registry.json");
        Load();
    }

    public static WindowsApplicationRegistry Default { get; } = new(new HuiTianPaths());

    public DateTimeOffset? RefreshedAt
    {
        get
        {
            lock (_sync) return _refreshedAt;
        }
    }

    public IReadOnlyList<WindowsApplicationRecord> Snapshot()
    {
        lock (_sync)
        {
            return Array.AsReadOnly(_records.Values
                .OrderBy(record => record.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        }
    }

    public bool TryResolve(string requestedName, out WindowsApplicationRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(requestedName)) return false;
        var requested = Normalize(requestedName);
        lock (_sync)
        {
            var match = _records.Values
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = Score(candidate, requested),
                })
                .Where(result => result.Score > 0)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Candidate.DisplayName.Length)
                .FirstOrDefault();
            if (match is null) return false;
            record = match.Candidate;
            return true;
        }
    }

    public void RegisterOrUpdate(WindowsApplicationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        lock (_sync)
        {
            _records[record.Id] = NormalizeRecord(record);
        }
    }

    public async Task RefreshIfStaleAsync(
        TimeSpan? maximumAge = null,
        CancellationToken cancellationToken = default)
    {
        var age = maximumAge ?? TimeSpan.FromHours(24);
        lock (_sync)
        {
            if (_records.Count > 0 && _refreshedAt is { } refreshed &&
                DateTimeOffset.Now - refreshed <= age)
            {
                return;
            }
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await Task.Run(
                    () => Discover(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            var refreshedAt = DateTimeOffset.Now;
            lock (_sync)
            {
                foreach (var record in discovered)
                {
                    _records[record.Id] = record;
                }

                _refreshedAt = refreshedAt;
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken = default)
    {
        RegistryCache cache;
        lock (_sync)
        {
            cache = new RegistryCache(
                CacheVersion,
                _refreshedAt ?? DateTimeOffset.Now,
                _records.Values.ToArray());
        }

        return SaveCoreAsync(cache, cancellationToken);
    }

    private async Task SaveCoreAsync(
        RegistryCache cache,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            var json = JsonSerializer.Serialize(cache, JsonOptions);
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
            // An application launch can still use the in-memory snapshot.
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var cache = JsonSerializer.Deserialize<RegistryCache>(
                File.ReadAllText(_path),
                JsonOptions);
            if (cache is null || cache.Version != CacheVersion) return;
            var valid = cache.Records
                .Where(IsUsable)
                .Select(NormalizeRecord)
                .ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                _records = valid;
                _refreshedAt = cache.RefreshedAt;
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A corrupt cache is replaced by the next background discovery.
        }
    }

    private static IReadOnlyList<WindowsApplicationRecord> Discover(
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, WindowsApplicationRecord>(
            StringComparer.OrdinalIgnoreCase);
        DiscoverShortcuts(records, cancellationToken);
        DiscoverAppPaths(records, cancellationToken);
        DiscoverRunningApplications(records, cancellationToken);
        return records.Values.ToArray();
    }

    private static void DiscoverShortcuts(
        IDictionary<string, WindowsApplicationRecord> records,
        CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                             root,
                             "*.lnk",
                             SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(path).Trim();
                    if (name.Length == 0) continue;
                    var id = "shortcut:" + Normalize(path);
                    records[id] = new WindowsApplicationRecord(
                        id,
                        name,
                        path,
                        ApplicationLaunchKind.Shortcut,
                        null,
                        "windows-shortcut",
                        BuildAliases(name, path),
                        DateTimeOffset.Now);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    private static void DiscoverAppPaths(
        IDictionary<string, WindowsApplicationRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var appPaths = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths is null) continue;
                    foreach (var subKeyName in appPaths.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var subKey = appPaths.OpenSubKey(subKeyName);
                        var target = subKey?.GetValue(null) as string;
                        target = target?.Trim().Trim('"');
                        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target)) continue;
                        var displayName = Path.GetFileNameWithoutExtension(subKeyName);
                        var id = "app-path:" + Normalize(target);
                        records[id] = new WindowsApplicationRecord(
                            id,
                            displayName,
                            target,
                            ApplicationLaunchKind.Executable,
                            Path.GetFileNameWithoutExtension(target),
                            "registry-app-path",
                            BuildAliases(displayName, target),
                            DateTimeOffset.Now);
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                }
            }
        }
    }

    private static void DiscoverRunningApplications(
        IDictionary<string, WindowsApplicationRecord> records,
        CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    var name = process.ProcessName;
                    var id = "process:" + Normalize(path);
                    records[id] = new WindowsApplicationRecord(
                        id,
                        name,
                        path,
                        ApplicationLaunchKind.Executable,
                        process.ProcessName,
                        "running-process",
                        BuildAliases(name, path, process.MainWindowTitle),
                        DateTimeOffset.Now);
                }
                catch (Exception exception) when (exception is
                    System.ComponentModel.Win32Exception or
                    InvalidOperationException or
                    NotSupportedException)
                {
                }
            }
        }
    }

    private static int Score(WindowsApplicationRecord record, string requested)
    {
        var candidates = record.Aliases
            .Append(record.DisplayName)
            .Append(record.ProcessName ?? string.Empty)
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var best = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Equals(requested, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 1000);
            }
            else if (candidate.StartsWith(requested, StringComparison.OrdinalIgnoreCase) ||
                     requested.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 760 - Math.Abs(candidate.Length - requested.Length));
            }
            else if (candidate.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                     requested.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, 520 - Math.Abs(candidate.Length - requested.Length));
            }
        }

        return best;
    }

    private static IReadOnlyList<string> BuildAliases(
        string name,
        string path,
        string? windowTitle = null) => new[]
        {
            name,
            Path.GetFileNameWithoutExtension(path),
            windowTitle ?? string.Empty,
        }
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static WindowsApplicationRecord NormalizeRecord(
        WindowsApplicationRecord record) => record with
    {
        Id = record.Id.Trim(),
        DisplayName = record.DisplayName.Trim(),
        LaunchTarget = record.LaunchTarget.Trim(),
        ProcessName = string.IsNullOrWhiteSpace(record.ProcessName)
            ? null
            : record.ProcessName.Trim(),
        Source = record.Source.Trim(),
        Aliases = record.Aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
    };

    private static void Validate(WindowsApplicationRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.Id) ||
            string.IsNullOrWhiteSpace(record.DisplayName) ||
            string.IsNullOrWhiteSpace(record.LaunchTarget) ||
            string.IsNullOrWhiteSpace(record.Source))
        {
            throw new ArgumentException("Application registry fields cannot be empty.");
        }
    }

    private static bool IsUsable(WindowsApplicationRecord record) =>
        !string.IsNullOrWhiteSpace(record.Id) &&
        !string.IsNullOrWhiteSpace(record.DisplayName) &&
        !string.IsNullOrWhiteSpace(record.LaunchTarget) &&
        (record.LaunchKind == ApplicationLaunchKind.Uri ||
         File.Exists(record.LaunchTarget));

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private sealed record RegistryCache(
        int Version,
        DateTimeOffset RefreshedAt,
        IReadOnlyList<WindowsApplicationRecord> Records);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
