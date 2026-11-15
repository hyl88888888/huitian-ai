using System.Security.Cryptography;
using HuiTian.Core;
using HuiTian.Infrastructure;

var testRoot = @"D:\HuiTian\Archive\Tests\native-download-client";
var destination = Path.Combine(testRoot, "huitian-test-model.bin");
var fixture = Path.Combine(testRoot, "local-range-fixture.bin");

Directory.CreateDirectory(testRoot);
foreach (var file in Directory.EnumerateFiles(testRoot, "huitian-test-model.bin*"))
{
    File.Delete(file);
}

await CreateFixtureAsync(fixture);
const long expectedLength = 6 * 1024 * 1024 + 123;
await using var fixtureInput = File.OpenRead(fixture);
var expectedSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fixtureInput));
await using var localServer = args.Length == 0
    ? LocalRangeServer.Start(fixture)
    : null;
var source = new Uri(args.FirstOrDefault() ?? localServer!.SourceUri.AbsoluteUri);

var request = new DownloadRequest(
    "native-smoke-test",
    destination,
    expectedLength,
    expectedSha,
    [
        new DownloadSource("unavailable-test-source", new Uri("http://127.0.0.1:1/missing")),
        new DownloadSource("local-range-source", source),
    ],
    SegmentSizeBytes: 2 * 1024 * 1024,
    MaxConcurrency: 3,
    RetryCountPerSegment: 5);

using var engine = new SegmentedDownloadEngine();
using var firstPassCancellation = new CancellationTokenSource();
var firstPassProgress = new InlineProgress<DownloadProgress>(update =>
{
    if (update.CompletedSegments >= 1)
    {
        firstPassCancellation.Cancel();
    }
});

try
{
    await engine.DownloadAsync(request, firstPassProgress, firstPassCancellation.Token);
    throw new InvalidOperationException("The first pass should have been cancelled.");
}
catch (OperationCanceledException)
{
    // Expected: .part and sidecar state must remain for the second pass.
}

if (!File.Exists(destination + ".part") ||
    !File.Exists(destination + ".part.state.json"))
{
    throw new InvalidOperationException("Resume files were not preserved after cancellation.");
}

var finalProgress = new InlineProgress<DownloadProgress>(update =>
{
    if (update.CompletedSegments == update.TotalSegments)
    {
        Console.WriteLine($"DOWNLOAD_PROGRESS={update.Percentage:0}%");
    }
});
var result = await engine.DownloadAsync(request, finalProgress);

await using var input = File.OpenRead(result.FilePath);
var actualSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
if (new FileInfo(result.FilePath).Length != expectedLength || actualSha != expectedSha)
{
    throw new InvalidDataException("Final file does not match the source.");
}

var staleDestination = Path.Combine(testRoot, "huitian-stale-state-test-model.bin");
foreach (var file in Directory.EnumerateFiles(testRoot, "huitian-stale-state-test-model.bin*"))
{
    File.Delete(file);
}

var staleRequest = request with
{
    AssetId = "stale-state-smoke-test",
    DestinationPath = staleDestination,
};
using var stalePassCancellation = new CancellationTokenSource();
var stalePassProgress = new InlineProgress<DownloadProgress>(update =>
{
    if (update.CompletedSegments >= 1)
    {
        stalePassCancellation.Cancel();
    }
});

try
{
    await engine.DownloadAsync(staleRequest, stalePassProgress, stalePassCancellation.Token);
    throw new InvalidOperationException("The stale-state setup pass should have been cancelled.");
}
catch (OperationCanceledException)
{
    // Preserve the state sidecar but simulate external cleanup of the .part payload.
}

var stalePart = staleDestination + ".part";
var staleState = stalePart + ".state.json";
if (!File.Exists(stalePart) || !File.Exists(staleState))
{
    throw new InvalidOperationException("Stale-state setup did not preserve resume files.");
}

File.Delete(stalePart);
var recovered = await engine.DownloadAsync(staleRequest);
await using var recoveredInput = File.OpenRead(recovered.FilePath);
var recoveredSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(recoveredInput));
if (new FileInfo(recovered.FilePath).Length != expectedLength || recoveredSha != expectedSha)
{
    throw new InvalidDataException("A stale state sidecar was not safely recovered.");
}

Console.WriteLine("PASS: range + retry + source fallback + resume + sha256 + .part");
Console.WriteLine("PASS: stale sidecar without .part is reset safely");
Console.WriteLine($"RESULT={result.FilePath}");

static async Task CreateFixtureAsync(string path)
{
    const int totalLength = 6 * 1024 * 1024 + 123;
    var buffer = new byte[128 * 1024];
    for (var index = 0; index < buffer.Length; index++)
    {
        buffer[index] = (byte)((index * 31 + 17) % 251);
    }

    await using var output = new FileStream(
        path,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        buffer.Length,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    var remaining = totalLength;
    while (remaining > 0)
    {
        var count = Math.Min(buffer.Length, remaining);
        await output.WriteAsync(buffer.AsMemory(0, count));
        remaining -= count;
    }
}

file sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
