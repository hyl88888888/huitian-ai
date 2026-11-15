using HuiTian.Core;
using HuiTian.Infrastructure;

var paths = new HuiTianPaths();
paths.EnsureDirectories();

using var engine = new SegmentedDownloadEngine();
var installer = new LocalBrainInstaller(paths, engine);
var lastPrintedAt = DateTimeOffset.MinValue;
var lastPercentage = -1d;

var progress = new Progress<LocalBrainInstallProgress>(current =>
{
    var now = DateTimeOffset.Now;
    if (current.Percentage < 100 &&
        now - lastPrintedAt < TimeSpan.FromSeconds(5) &&
        current.Percentage - lastPercentage < 1)
    {
        return;
    }

    lastPrintedAt = now;
    lastPercentage = current.Percentage;
    Console.WriteLine(
        $"PROGRESS {current.Percentage,6:F2}% | " +
        $"{current.DownloadedBytes / 1_073_741_824d:F2}/" +
        $"{current.TotalBytes / 1_073_741_824d:F2} GiB | " +
        $"{current.BytesPerSecond / 1_048_576d:F2} MiB/s | {current.Stage}");
});

var installation = await installer.InstallAsync(
    CapabilityTierId.Huang,
    progress,
    CancellationToken.None);

Console.WriteLine("PASS: local brain package installed and SHA256 verified");
Console.WriteLine($"BRAIN={installation.BrainPath}");
Console.WriteLine($"VISION={installation.VisionPath}");
Console.WriteLine($"CONTEXT={installation.ContextTokens}");
Console.WriteLine($"GPU_LAYERS={installation.PreferredGpuLayers}");
