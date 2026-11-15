namespace HuiTian.Core;

public sealed record DownloadSource(string Name, Uri Uri);

public sealed record DownloadRequest(
    string AssetId,
    string DestinationPath,
    long ExpectedLength,
    string ExpectedSha256,
    IReadOnlyList<DownloadSource> Sources,
    int SegmentSizeBytes = 8 * 1024 * 1024,
    int MaxConcurrency = 4,
    int RetryCountPerSegment = 4);

public sealed record DownloadProgress(
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    int CompletedSegments,
    int TotalSegments)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(DownloadedBytes * 100d / TotalBytes, 0, 100);
}

public sealed record DownloadResult(
    string FilePath,
    string Sha256,
    long Length,
    TimeSpan Elapsed);

public interface IDownloadEngine
{
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
