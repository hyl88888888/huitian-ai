using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed record LocalBrainInstallProgress(
    string Stage,
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record LocalBrainInstallation(
    CapabilityTierId Tier,
    string BrainPath,
    string VisionPath,
    int ContextTokens,
    int PreferredGpuLayers);

public sealed class LocalBrainInstaller
{
    private const string BrainFileName = "huitian-brain.bin";
    private const string VisionFileName = "huitian-vision.bin";

    private readonly HuiTianPaths _paths;
    private readonly IDownloadEngine _downloadEngine;

    public LocalBrainInstaller(HuiTianPaths paths, IDownloadEngine downloadEngine)
    {
        _paths = paths;
        _downloadEngine = downloadEngine;
    }

    public async Task<LocalBrainInstallation> InstallAsync(
        CapabilityTierId tier,
        IProgress<LocalBrainInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var package = LocalBrainCatalog.ForTier(tier);
        var tierDirectory = Path.Combine(_paths.Models, GetTierDirectoryName(tier));
        Directory.CreateDirectory(tierDirectory);

        var brainPath = Path.Combine(tierDirectory, BrainFileName);
        var visionPath = Path.Combine(tierDirectory, VisionFileName);
        var totalBytes = checked(package.Brain.Length + package.Vision.Length);

        await DownloadAssetAsync(
                package.Brain,
                brainPath,
                completedBefore: 0,
                totalBytes,
                "正在准备回天核心",
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        await DownloadAssetAsync(
                package.Vision,
                visionPath,
                completedBefore: package.Brain.Length,
                totalBytes,
                "正在准备屏幕理解能力",
                progress,
                cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new LocalBrainInstallProgress(
            "回天核心准备完成",
            totalBytes,
            totalBytes,
            0));

        return new LocalBrainInstallation(
            tier,
            brainPath,
            visionPath,
            package.ContextTokens,
            package.PreferredGpuLayers);
    }

    public LocalBrainInstallation GetInstalled(CapabilityTierId tier)
    {
        var package = LocalBrainCatalog.ForTier(tier);
        var tierDirectory = Path.Combine(_paths.Models, GetTierDirectoryName(tier));
        var brainPath = Path.Combine(tierDirectory, BrainFileName);
        var visionPath = Path.Combine(tierDirectory, VisionFileName);

        ValidateInstalledAsset(brainPath, package.Brain.Length, "回天核心");
        ValidateInstalledAsset(visionPath, package.Vision.Length, "屏幕理解组件");

        return new LocalBrainInstallation(
            tier,
            brainPath,
            visionPath,
            package.ContextTokens,
            package.PreferredGpuLayers);
    }

    private async Task DownloadAssetAsync(
        LocalBrainAsset asset,
        string destinationPath,
        long completedBefore,
        long totalBytes,
        string stage,
        IProgress<LocalBrainInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assetProgress = progress is null
            ? null
            : new Progress<DownloadProgress>(current => progress.Report(
                new LocalBrainInstallProgress(
                    stage,
                    completedBefore + current.DownloadedBytes,
                    totalBytes,
                    current.BytesPerSecond)));

        await _downloadEngine.DownloadAsync(
                new DownloadRequest(
                    asset.AssetId,
                    destinationPath,
                    asset.Length,
                    asset.Sha256,
                    asset.Sources,
                    SegmentSizeBytes: 16 * 1024 * 1024,
                    MaxConcurrency: 6,
                    RetryCountPerSegment: 6),
                assetProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetTierDirectoryName(CapabilityTierId tier) => tier switch
    {
        CapabilityTierId.Huang => "黄阶-初启",
        CapabilityTierId.Xuan => "玄阶-通明",
        CapabilityTierId.Di => "地阶-纵横",
        CapabilityTierId.Tian => "天阶-无极",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, null),
    };

    private static void ValidateInstalledAsset(
        string path,
        long expectedLength,
        string displayName)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength)
        {
            throw new FileNotFoundException($"{displayName}尚未完整安装。", path);
        }
    }
}
