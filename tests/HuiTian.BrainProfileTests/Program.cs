using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HuiTian.Infrastructure;

if (args.Length > 0)
{
    Capture(args);
    return;
}

var passed = 0;
await CheckAsync("missing profile skips hardware detection", async fixture =>
{
    File.Delete(fixture.ProfilePath);
    AssertDefault(await fixture.LoadAsync());
    Assert(fixture.HardwareReads == 0, "A missing profile performed hardware detection.");
});

foreach (var invalidJson in new[] { "{broken", "null", "[]", "{}" })
{
    await CheckAsync($"invalid profile: {invalidJson}", async fixture =>
    {
        File.WriteAllText(fixture.ProfilePath, invalidJson);
        AssertDefault(await fixture.LoadAsync());
        Assert(fixture.HardwareReads == 0, "Invalid JSON performed hardware detection.");
    });
}

foreach (var missingProperty in new[]
         { "schemaVersion", "measuredAtUtc", "sourceReport", "fingerprint", "useEmbeddedMtp" })
{
    await CheckAsync($"omitted {missingProperty} stays at the default", async fixture =>
    {
        var json = JsonNode.Parse(File.ReadAllText(fixture.ProfilePath))!.AsObject();
        json.Remove(missingProperty);
        File.WriteAllText(fixture.ProfilePath, json.ToJsonString());
        AssertDefault(await fixture.LoadAsync());
        Assert(fixture.HardwareReads == 0, "An incomplete profile performed hardware detection.");
    });
}

await CheckAsync("unsupported schema", async fixture =>
{
    fixture.Save(fixture.Profile with { SchemaVersion = 2 });
    AssertDefault(await fixture.LoadAsync());
    Assert(fixture.HardwareReads == 0, "An unsupported schema performed hardware detection.");
});
await CheckAsync("future measurement", async fixture =>
{
    fixture.Save(fixture.Profile with { MeasuredAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("explicit MTP=true uses the default without detecting hardware", async fixture =>
{
    fixture.Save(fixture.Profile with { UseEmbeddedMtp = true });
    AssertDefault(await fixture.LoadAsync());
    Assert(fixture.HardwareReads == 0, "Default decoding performed hardware detection.");
});
await CheckAsync("exact match disables MTP", async fixture =>
{
    var result = await fixture.LoadAsync();
    Assert(!result.UseEmbeddedMtp && result.ProfileApplied, "A matching profile was not applied.");
    Assert(fixture.HardwareReads == 1, "A matching profile must verify current hardware once.");
    Assert(result.Message.Contains(fixture.Profile.SourceReport), "The log omitted the source report.");
});
await CheckAsync("another machine", async fixture =>
{
    fixture.Save(fixture.Profile with { Fingerprint = fixture.Fingerprint(machineName: "fixture-other-machine") });
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("another processor count", async fixture =>
{
    fixture.Save(fixture.Profile with { Fingerprint = fixture.Fingerprint(processorCount: Environment.ProcessorCount + 1) });
    AssertDefault(await fixture.LoadAsync());
});

foreach (var otherHardware in new[]
         {
             LocalBrainDecodeProfile.CreateHardwareSignature("other CPU", "fixture GPU", 32, 8),
             LocalBrainDecodeProfile.CreateHardwareSignature("fixture CPU", "other GPU", 32, 8),
             LocalBrainDecodeProfile.CreateHardwareSignature("fixture CPU", "fixture GPU", 64, 8),
             LocalBrainDecodeProfile.CreateHardwareSignature("fixture CPU", "fixture GPU", 32, 16),
         })
{
    await CheckAsync("CPU/GPU/RAM/VRAM hardware mismatch", async fixture =>
        AssertDefault(await fixture.LoadAsync(_ => Task.FromResult(otherHardware))));
}

foreach (var asset in new[] { "brain.bin", "vision.bin", "llama-server.exe", "ggml-cuda.dll" })
{
    await CheckAsync($"changed {asset} length", async fixture =>
    {
        File.AppendAllText(fixture.Asset(asset), "changed");
        AssertDefault(await fixture.LoadAsync());
    });
    await CheckAsync($"changed {asset} timestamp", async fixture =>
    {
        var path = fixture.Asset(asset);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        AssertDefault(await fixture.LoadAsync());
    });
}

await CheckAsync("added runtime DLL", async fixture =>
{
    File.WriteAllText(fixture.Asset("extra.dll"), "fixture");
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("removed runtime DLL", async fixture =>
{
    File.Delete(fixture.Asset("ggml-cuda.dll"));
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("renamed runtime DLL", async fixture =>
{
    File.Move(fixture.Asset("ggml-cuda.dll"), fixture.Asset("renamed.dll"));
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("missing model fails safely", async fixture =>
{
    File.Delete(fixture.BrainPath);
    AssertDefault(await fixture.LoadAsync());
});
await CheckAsync("unrelated runtime text does not invalidate metadata", async fixture =>
{
    File.WriteAllText(fixture.Asset("notes.txt"), "fixture");
    Assert((await fixture.LoadAsync()).ProfileApplied, "A non-binary invalidated the profile.");
});
await CheckAsync("context mismatch", async fixture =>
    AssertDefault(await fixture.LoadAsync(contextTokens: 4096)));
await CheckAsync("hardware error fails safely", async fixture =>
    AssertDefault(await fixture.LoadAsync(_ => Task.FromException<string>(new IOException("fixture")))));
await CheckAsync("empty hardware signature fails safely", async fixture =>
    AssertDefault(await fixture.LoadAsync(_ => Task.FromResult(""))));
await CheckAsync("incomplete hardware fails safely", async fixture =>
    AssertDefault(await fixture.LoadAsync(_ => Task.FromResult(
        LocalBrainDecodeProfile.CreateHardwareSignature("fixture CPU", "fixture GPU", 32, 0)))));
await CheckAsync("detector cancellation fails safely", async fixture =>
    AssertDefault(await fixture.LoadAsync(_ => Task.FromCanceled<string>(new CancellationToken(true)))));
await CheckAsync("caller cancellation before loading is propagated", async fixture =>
{
    await AssertCanceledAsync(() => fixture.LoadAsync(cancellationToken: new CancellationToken(true)));
    Assert(fixture.HardwareReads == 0, "A cancelled load performed hardware detection.");
});
await CheckAsync("caller cancellation reaches hardware detection", async fixture =>
{
    using var callerCancellation = new CancellationTokenSource();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationToken hardwareToken = default;
    var loading = fixture.LoadAsync(async token =>
    {
        hardwareToken = token;
        started.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Fixture.HardwareSignature;
    }, callerCancellation.Token);
    await started.Task;
    callerCancellation.Cancel();
    await AssertCanceledAsync(() => loading);
    Assert(hardwareToken.IsCancellationRequested, "The detector was not cancelled with the caller.");
});
await CheckAsync("three-second timeout cancels hardware detection and restores the default", async fixture =>
{
    CancellationToken hardwareToken = default;
    var result = await fixture.LoadAsync(async token =>
    {
        hardwareToken = token;
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Fixture.HardwareSignature;
    });
    AssertDefault(result);
    Assert(hardwareToken.IsCancellationRequested, "The timeout did not cancel the detector.");
    Assert(result.Message.Contains("超时"), "A timeout was not reported in the load result.");
});

Console.WriteLine($"PASS: {passed} isolated decode-profile checks; no models or hardware probes were run.");

async Task CheckAsync(string name, Func<Fixture, Task> check)
{
    using var fixture = new Fixture();
    try
    {
        await check(fixture);
        passed++;
    }
    catch (Exception exception)
    {
        throw new InvalidOperationException($"FAIL: {name}", exception);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertDefault(LocalBrainDecodeSettings result) =>
    Assert(result.UseEmbeddedMtp && !result.ProfileApplied, "The load did not retain default MTP=true.");

static async Task AssertCanceledAsync(Func<Task<LocalBrainDecodeSettings>> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Caller cancellation was swallowed.");
}

static void Capture(string[] arguments)
{
    if (arguments.Length != 8 || arguments[0] != "--capture")
    {
        throw new ArgumentException("Usage: --capture <server.exe> <brain> <vision> <contextTokens> " +
            "<hardware.json> <sourceReport> <measuredAtUtc>. Reads metadata only; emits JSON to stdout.");
    }

    using var hardware = JsonDocument.Parse(File.ReadAllText(arguments[5]));
    var root = hardware.RootElement;
    var signature = LocalBrainDecodeProfile.CreateHardwareSignature(
        root.GetProperty("cpuName").GetString()!,
        root.GetProperty("gpuName").GetString()!,
        root.GetProperty("physicalMemoryBytes").GetInt64(),
        root.GetProperty("videoMemoryBytes").GetInt64());
    var reportPath = Path.GetFullPath(arguments[6]);
    if (!File.Exists(reportPath)) throw new FileNotFoundException("The measurement report must exist.", reportPath);
    var profile = new LocalBrainDecodeProfile
    {
        SchemaVersion = 1,
        MeasuredAtUtc = DateTimeOffset.Parse(arguments[7], CultureInfo.InvariantCulture),
        SourceReport = reportPath,
        UseEmbeddedMtp = false,
        Fingerprint = LocalBrainDecodeProfile.CaptureFingerprint(arguments[1], arguments[2], arguments[3],
            int.Parse(arguments[4], CultureInfo.InvariantCulture), signature),
    };
    Console.WriteLine(JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    }));
}

sealed class Fixture : IDisposable
{
    private static readonly string FixtureParent = @"D:\HuiTian\Temp\BrainProfileTests";
    public static readonly string HardwareSignature =
        LocalBrainDecodeProfile.CreateHardwareSignature("fixture CPU", "fixture GPU", 32, 8);
    private readonly string _directory = Path.GetFullPath(Path.Combine(FixtureParent, Guid.NewGuid().ToString("N")));
    public string ProfilePath => Path.Combine(_directory, "brain-decode-profile.json");
    public string BrainPath => Path.Combine(_directory, "brain.bin");
    public string VisionPath => Path.Combine(_directory, "vision.bin");
    public string Executable => Asset("llama-server.exe");
    public LocalBrainDecodeProfile Profile { get; }
    public int HardwareReads { get; private set; }

    public Fixture()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "runtime"));
        foreach (var path in new[] { BrainPath, VisionPath, Executable, Asset("ggml-cuda.dll") })
            File.WriteAllText(path, "fixture");
        Profile = new LocalBrainDecodeProfile
        {
            SchemaVersion = 1,
            MeasuredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            SourceReport = Path.Combine(_directory, "benchmark-report.json"),
            UseEmbeddedMtp = false,
            Fingerprint = Fingerprint(),
        };
        Save(Profile);
    }

    public string Asset(string name) =>
        name is "brain.bin" or "vision.bin" ? Path.Combine(_directory, name) : Path.Combine(_directory, "runtime", name);

    public string Fingerprint(string? machineName = null, int? processorCount = null) =>
        LocalBrainDecodeProfile.CaptureFingerprint(Executable, BrainPath, VisionPath, 8192,
            HardwareSignature, machineName, processorCount);

    public void Save(LocalBrainDecodeProfile profile) => File.WriteAllText(ProfilePath,
        JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public Task<LocalBrainDecodeSettings> LoadAsync(
        Func<CancellationToken, Task<string>>? readHardware = null,
        CancellationToken cancellationToken = default,
        int contextTokens = 8192) =>
        LocalBrainDecodeProfile.LoadAsync(ProfilePath, Executable, BrainPath, VisionPath, contextTokens,
            token =>
            {
                HardwareReads++;
                return readHardware is null ? Task.FromResult(HardwareSignature) : readHardware(token);
            }, cancellationToken);

    public void Dispose()
    {
        // Only the unique directory created by this fixture can be removed.
        if (!string.Equals(Path.GetDirectoryName(_directory), FixtureParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(_directory), "N", out _))
            throw new InvalidOperationException("Refusing to remove a path outside the test fixture.");
        Directory.Delete(_directory, recursive: true);
    }
}
