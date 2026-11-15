using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using HuiTian.Core;

namespace HuiTian.Infrastructure;

public sealed class SegmentedDownloadEngine : IDownloadEngine, IDisposable
{
    private const int ProbeBytes = 1024 * 1024;
    private const int SourceProbeAttempts = 3;
    private static readonly TimeSpan SegmentAttemptTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public SegmentedDownloadEngine(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HuiTian-Download/1.0");
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        var startedAt = Stopwatch.StartNew();
        var destination = Path.GetFullPath(request.DestinationPath);
        var destinationDirectory = Path.GetDirectoryName(destination)
                                   ?? throw new InvalidOperationException("下载目录无效。");
        Directory.CreateDirectory(destinationDirectory);

        if (File.Exists(destination) &&
            new FileInfo(destination).Length == request.ExpectedLength)
        {
            var existingHash = await ComputeSha256Async(destination, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(existingHash, request.ExpectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new DownloadResult(
                    destination, existingHash, request.ExpectedLength, startedAt.Elapsed);
            }
        }

        var partPath = destination + ".part";
        var statePath = partPath + ".state.json";
        var segmentCount = checked((int)Math.Ceiling(
            request.ExpectedLength / (double)request.SegmentSizeBytes));
        var state = await LoadOrCreateStateAsync(
                request, partPath, statePath, segmentCount, cancellationToken)
            .ConfigureAwait(false);

        var sourceOrder = await RankSourcesAsync(request, cancellationToken)
            .ConfigureAwait(false);
        var pending = Enumerable.Range(0, segmentCount)
            .Where(index => !state.CompletedSegments.Contains(index))
            .ToArray();
        var completedBytes = state.CompletedSegments.Sum(index =>
            GetSegmentLength(index, request.ExpectedLength, request.SegmentSizeBytes));
        var completedSegments = state.CompletedSegments.Count;
        var stateGate = new SemaphoreSlim(1, 1);
        var transferClock = Stopwatch.StartNew();

        await using var partStream = new FileStream(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (partStream.Length != request.ExpectedLength)
        {
            partStream.SetLength(request.ExpectedLength);
        }

        SafeFileHandle handle = partStream.SafeFileHandle;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = request.MaxConcurrency,
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(pending, parallelOptions, async (segment, token) =>
        {
            var segmentLength = GetSegmentLength(
                segment, request.ExpectedLength, request.SegmentSizeBytes);
            await DownloadSegmentWithRetryAsync(
                    request, sourceOrder, handle, segment, segmentLength, token)
                .ConfigureAwait(false);

            var bytesNow = Interlocked.Add(ref completedBytes, segmentLength);
            var segmentsNow = Interlocked.Increment(ref completedSegments);

            await stateGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                state.CompletedSegments.Add(segment);
                await SaveStateAsync(statePath, state, token).ConfigureAwait(false);
            }
            finally
            {
                stateGate.Release();
            }

            var seconds = Math.Max(transferClock.Elapsed.TotalSeconds, 0.001);
            progress?.Report(new DownloadProgress(
                bytesNow,
                request.ExpectedLength,
                Math.Max(0, (bytesNow - state.InitialCompletedBytes) / seconds),
                segmentsNow,
                segmentCount));
        }).ConfigureAwait(false);

        await partStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        partStream.Flush(flushToDisk: true);
        await partStream.DisposeAsync().ConfigureAwait(false);

        var actualHash = await ComputeSha256Async(partPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(actualHash, request.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            var suffix = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
            var failedPart = partPath + $".corrupt-{suffix}";
            var failedState = statePath + $".corrupt-{suffix}";
            File.Move(partPath, failedPart, overwrite: false);
            if (File.Exists(statePath))
            {
                File.Move(statePath, failedState, overwrite: false);
            }

            throw new InvalidDataException(
                $"完整性校验失败。预期 {request.ExpectedSha256}，实际 {actualHash}。");
        }

        File.Move(partPath, destination, overwrite: true);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        progress?.Report(new DownloadProgress(
            request.ExpectedLength,
            request.ExpectedLength,
            request.ExpectedLength / Math.Max(startedAt.Elapsed.TotalSeconds, 0.001),
            segmentCount,
            segmentCount));

        return new DownloadResult(
            destination, actualHash, request.ExpectedLength, startedAt.Elapsed);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadSegmentWithRetryAsync(
        DownloadRequest request,
        IReadOnlyList<DownloadSource> sources,
        SafeFileHandle destination,
        int segmentIndex,
        long expectedSegmentLength,
        CancellationToken cancellationToken)
    {
        var start = checked((long)segmentIndex * request.SegmentSizeBytes);
        var end = checked(start + expectedSegmentLength - 1);
        Exception? lastError = null;
        var attempts = Math.Max(request.RetryCountPerSegment, sources.Count);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            // Every segment starts with the fastest probe result. Slower mirrors are
            // fallbacks, not round-robin peers that can stall half of a healthy download.
            var source = sources[attempt % sources.Count];
            try
            {
                using var attemptTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(SegmentAttemptTimeout);
                var attemptToken = attemptTimeout.Token;
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, source.Uri);
                httpRequest.Headers.Range = new RangeHeaderValue(start, end);
                using var response = await _httpClient.SendAsync(
                        httpRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptToken)
                    .ConfigureAwait(false);
                ValidateRangeResponse(response, start, end, request.ExpectedLength);

                await using var input = await response.Content
                    .ReadAsStreamAsync(attemptToken)
                    .ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    long written = 0;
                    while (written < expectedSegmentLength)
                    {
                        var remaining = expectedSegmentLength - written;
                        var read = await input.ReadAsync(
                                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                                attemptToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new EndOfStreamException(
                                $"分块 {segmentIndex} 提前结束。仅收到 {written} 字节。");
                        }

                        await RandomAccess.WriteAsync(
                                destination,
                                buffer.AsMemory(0, read),
                                start + written,
                                attemptToken)
                            .ConfigureAwait(false);
                        written += read;
                    }

                    if (await input.ReadAsync(buffer.AsMemory(0, 1), attemptToken)
                            .ConfigureAwait(false) != 0)
                    {
                        throw new InvalidDataException($"分块 {segmentIndex} 长度超出预期。");
                    }

                    return;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastError = exception;
                var delay = TimeSpan.FromMilliseconds(Math.Min(4000, 300 * Math.Pow(2, attempt)));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException($"分块 {segmentIndex} 多次重试后仍未完成。", lastError);
    }

    private async Task<IReadOnlyList<DownloadSource>> RankSourcesAsync(
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var probes = await Task.WhenAll(request.Sources.Select(source =>
            ProbeSourceWithRetryAsync(source, request.ExpectedLength, cancellationToken)))
            .ConfigureAwait(false);

        var working = probes
            .Where(probe => probe.Error is null)
            .OrderByDescending(probe => probe.Score)
            .Select(probe => probe.Source)
            .ToArray();
        if (working.Length == 0)
        {
            throw new HttpRequestException("所有下载源均未通过分块下载测试。");
        }

        return working;
    }

    private async Task<(DownloadSource Source, double Score, Exception? Error)>
        ProbeSourceWithRetryAsync(
            DownloadSource source,
            long expectedLength,
            CancellationToken cancellationToken)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt < SourceProbeAttempts; attempt++)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var probeTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(SourceProbeTimeout);
                var probeToken = probeTimeout.Token;
                var end = Math.Min(expectedLength, ProbeBytes) - 1;
                using var probe = new HttpRequestMessage(HttpMethod.Get, source.Uri);
                probe.Headers.Range = new RangeHeaderValue(0, end);
                using var response = await _httpClient.SendAsync(
                        probe,
                        HttpCompletionOption.ResponseHeadersRead,
                        probeToken)
                    .ConfigureAwait(false);
                ValidateRangeResponse(response, 0, end, expectedLength);
                await using var stream = await response.Content
                    .ReadAsStreamAsync(probeToken)
                    .ConfigureAwait(false);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                long readTotal = 0;
                try
                {
                    while (readTotal <= end)
                    {
                        var read = await stream.ReadAsync(
                                buffer.AsMemory(0, (int)Math.Min(
                                    buffer.Length, end + 1 - readTotal)), probeToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        readTotal += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (readTotal != end + 1)
                {
                    throw new EndOfStreamException("测速分块不完整。");
                }

                return (source, readTotal /
                    Math.Max(watch.Elapsed.TotalSeconds, 0.001), null);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastError = exception;
                if (attempt + 1 < SourceProbeAttempts)
                {
                    var delay = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return (source, 0d, lastError);
    }

    private async Task<DownloadState> LoadOrCreateStateAsync(
        DownloadRequest request,
        string partPath,
        string statePath,
        int segmentCount,
        CancellationToken cancellationToken)
    {
        if (File.Exists(statePath))
        {
            try
            {
                await using var input = File.OpenRead(statePath);
                var existing = await JsonSerializer.DeserializeAsync<DownloadState>(
                        input, _jsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null &&
                    File.Exists(partPath) &&
                    new FileInfo(partPath).Length == request.ExpectedLength &&
                    existing.ExpectedLength == request.ExpectedLength &&
                    existing.SegmentSizeBytes == request.SegmentSizeBytes &&
                    string.Equals(existing.ExpectedSha256, request.ExpectedSha256,
                        StringComparison.OrdinalIgnoreCase) &&
                    existing.CompletedSegments.All(index => index >= 0 && index < segmentCount))
                {
                    existing.InitialCompletedBytes = existing.CompletedSegments.Sum(index =>
                        GetSegmentLength(
                            index, request.ExpectedLength, request.SegmentSizeBytes));
                    return existing;
                }
            }
            catch (JsonException)
            {
                // A damaged sidecar is replaced. The .part payload remains recoverable.
            }
        }

        var created = new DownloadState
        {
            AssetId = request.AssetId,
            ExpectedLength = request.ExpectedLength,
            ExpectedSha256 = request.ExpectedSha256.ToLowerInvariant(),
            SegmentSizeBytes = request.SegmentSizeBytes,
            CompletedSegments = [],
        };
        await SaveStateAsync(statePath, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private async Task SaveStateAsync(
        string statePath,
        DownloadState state,
        CancellationToken cancellationToken)
    {
        var temporaryPath = statePath + ".tmp";
        await using (var output = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(
                    output, state, _jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporaryPath, statePath, overwrite: true);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void ValidateRangeResponse(
        HttpResponseMessage response,
        long expectedStart,
        long expectedEnd,
        long expectedTotal)
    {
        var reportedRange = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.OK &&
            reportedRange?.From == expectedStart &&
            reportedRange.To == expectedEnd &&
            reportedRange.Length == expectedTotal)
        {
            // A few domestic object-storage gateways preserve a correct
            // Content-Range while rewriting the HTTP status from 206 to 200.
            // Exact range metadata plus the byte-count check performed by the
            // caller is sufficient to accept this response safely.
            return;
        }

        // Some mirrors correctly honor Range for large files but return the
        // complete payload with HTTP 200 when the requested range already
        // covers the whole (usually small) file. That response is safe for a
        // one-segment transfer and should not make the source look broken.
        if (response.StatusCode == HttpStatusCode.OK &&
            expectedStart == 0 &&
            expectedEnd == expectedTotal - 1 &&
            (response.Content.Headers.ContentLength is null ||
             response.Content.Headers.ContentLength == expectedTotal))
        {
            return;
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException(
                $"下载源未返回 HTTP 206，而是 {(int)response.StatusCode}。");
        }

        var range = response.Content.Headers.ContentRange;
        if (range?.From != expectedStart || range.To != expectedEnd ||
            range.Length != expectedTotal)
        {
            throw new HttpRequestException("下载源返回的分块范围或文件总长度不匹配。");
        }
    }

    private static long GetSegmentLength(int index, long totalLength, int segmentSize)
    {
        var start = checked((long)index * segmentSize);
        return Math.Min(segmentSize, totalLength - start);
    }

    private static void Validate(DownloadRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AssetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        if (request.ExpectedLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ExpectedLength));
        }

        if (request.ExpectedSha256.Length != 64 ||
            !request.ExpectedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("SHA256 必须是 64 位十六进制字符串。",
                nameof(request.ExpectedSha256));
        }

        if (request.Sources.Count == 0)
        {
            throw new ArgumentException("至少需要一个下载源。", nameof(request.Sources));
        }

        if (request.SegmentSizeBytes < 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request.SegmentSizeBytes));
        }

        if (request.MaxConcurrency is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxConcurrency));
        }
    }

    private sealed class DownloadState
    {
        public string AssetId { get; set; } = string.Empty;

        public long ExpectedLength { get; set; }

        public string ExpectedSha256 { get; set; } = string.Empty;

        public int SegmentSizeBytes { get; set; }

        public HashSet<int> CompletedSegments { get; set; } = [];

        [System.Text.Json.Serialization.JsonIgnore]
        public long InitialCompletedBytes { get; set; }
    }
}
