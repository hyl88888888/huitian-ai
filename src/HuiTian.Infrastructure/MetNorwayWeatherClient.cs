using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HuiTian.Infrastructure;

/// <summary>
/// Registration-free, low-volume Locationforecast 2.0 client. Requests carry
/// only the real HuiTian application identity, honour the provider cache
/// headers and store responses on D: so the workflow never falls back to
/// treating arbitrary web snippets as weather observations.
/// </summary>
public sealed class MetNorwayWeatherClient : IWeatherForecastProvider
{
    public const string ApplicationUserAgent = "HuiTian/1.0";
    public const string DefaultCacheDirectory = @"D:\HuiTian\Cache\Weather\met-no";
    public const int MaximumDownloadBytes = 15 * 1024 * 1024;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan MissingExpiryFallback = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new();
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly TimeProvider _timeProvider;

    public MetNorwayWeatherClient(
        HttpClient? httpClient = null,
        string? cacheDirectory = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _cacheDirectory = ValidateDDrivePath(cacheDirectory ?? DefaultCacheDirectory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WeatherForecastFetchResult> GetSevenDayForecastAsync(
        string location,
        CancellationToken cancellationToken = default)
    {
        WeatherLocation resolved;
        try
        {
            if (!GeoNamesChinaCityResolver.TryResolve(location, out resolved))
            {
                return new WeatherForecastFetchResult(
                    WeatherForecastFetchStatus.LocationNotFound,
                    null,
                    "精简城市坐标表无法唯一定位这个地点，天气部分将交由普通联网流程处理。");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            Trace.WriteLine($"GeoNames city data unavailable: {exception.Message}");
            return new WeatherForecastFetchResult(
                WeatherForecastFetchStatus.InvalidData,
                null,
                "本机城市坐标资料暂时不可用，天气部分将交由普通联网流程处理。");
        }

        try
        {
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestBudget.CancelAfter(RequestTimeout);
            var forecast = await GetResolvedForecastAsync(resolved, requestBudget.Token)
                .ConfigureAwait(false);
            return new WeatherForecastFetchResult(
                WeatherForecastFetchStatus.Available,
                forecast,
                $"已取得{resolved.RequestedName}未来 7 天的结构化天气预报。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WeatherForecastFetchResult(
                WeatherForecastFetchStatus.NetworkUnavailable,
                null,
                "真实天气服务连接超时，天气部分将交由普通联网流程处理。");
        }
        catch (HttpRequestException exception)
        {
            Trace.WriteLine($"MET Norway request failed: {exception.Message}");
            return new WeatherForecastFetchResult(
                WeatherForecastFetchStatus.NetworkUnavailable,
                null,
                "真实天气服务暂时不可访问，天气部分将交由普通联网流程处理。");
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"MET Norway data unavailable: {exception.Message}");
            return new WeatherForecastFetchResult(
                WeatherForecastFetchStatus.InvalidData,
                null,
                "天气响应或本机缓存没有通过校验，天气部分将交由普通联网流程处理。");
        }
    }

    private async Task<SevenDayWeatherForecast> GetResolvedForecastAsync(
        WeatherLocation location,
        CancellationToken cancellationToken)
    {
        var sourceUri = BuildSourceUri(location);
        var cacheKey = BuildCacheKey(location);
        var gate = CacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var payloadPath = Path.Combine(_cacheDirectory, cacheKey + ".json");
            var metadataPath = Path.Combine(_cacheDirectory, cacheKey + ".cache.json");
            var cache = await ReadCacheAsync(payloadPath, metadataPath, cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (cache is not null && cache.Metadata.ExpiresUtc > now)
            {
                try
                {
                    return MetNorwayLocationForecastParser.Parse(
                        cache.Payload,
                        location,
                        sourceUri,
                        cache.Metadata.RetrievedAtUtc);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    Trace.WriteLine($"Ignoring invalid MET Norway cache: {exception.Message}");
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.ParseAdd(ApplicationUserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
            if (cache?.Metadata.LastModifiedUtc is { } lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cache is null)
                {
                    throw new InvalidDataException(
                        "MET Norway returned 304 but there is no local cached payload.");
                }

                var refreshedMetadata = cache.Metadata with
                {
                    ExpiresUtc = ReadExpiry(response, now),
                    RetrievedAtUtc = now,
                };
                await WriteMetadataAsync(metadataPath, refreshedMetadata, cancellationToken)
                    .ConfigureAwait(false);
                return MetNorwayLocationForecastParser.Parse(
                    cache.Payload,
                    location,
                    sourceUri,
                    now);
            }

            response.EnsureSuccessStatusCode();
            if ((int)response.StatusCode == 203)
            {
                Trace.WriteLine("MET Norway Locationforecast endpoint returned deprecated status 203.");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is <= 0 or > MaximumDownloadBytes)
            {
                throw new InvalidDataException(
                    $"MET Norway response declared an invalid size: {declaredLength}.");
            }

            var payload = await ReadLimitedUtf8Async(response.Content, cancellationToken)
                .ConfigureAwait(false);
            var retrievedAt = _timeProvider.GetUtcNow();
            var parsed = MetNorwayLocationForecastParser.Parse(
                payload,
                location,
                sourceUri,
                retrievedAt);
            var metadata = new CacheMetadata(
                retrievedAt,
                ReadExpiry(response, retrievedAt),
                response.Content.Headers.LastModified,
                sourceUri.AbsoluteUri);
            await WriteCacheAsync(
                    payloadPath,
                    metadataPath,
                    payload,
                    metadata,
                    cancellationToken)
                .ConfigureAwait(false);
            return parsed;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Uri BuildSourceUri(WeatherLocation location)
    {
        var latitude = TruncateFourDecimals(location.Latitude)
            .ToString("0.####", CultureInfo.InvariantCulture);
        var longitude = TruncateFourDecimals(location.Longitude)
            .ToString("0.####", CultureInfo.InvariantCulture);
        var altitude = location.Elevation is > -500 and < 9000
            ? $"&altitude={location.Elevation.Value.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        return new Uri(
            "https://api.met.no/weatherapi/locationforecast/2.0/compact" +
            $"?lat={latitude}&lon={longitude}{altitude}",
            UriKind.Absolute);
    }

    private static string BuildCacheKey(WeatherLocation location)
    {
        var latitude = TruncateFourDecimals(location.Latitude)
            .ToString("0.0000", CultureInfo.InvariantCulture)
            .Replace('-', 'm')
            .Replace('.', '_');
        var longitude = TruncateFourDecimals(location.Longitude)
            .ToString("0.0000", CultureInfo.InvariantCulture)
            .Replace('-', 'm')
            .Replace('.', '_');
        return $"{location.GeoNameId}-{latitude}-{longitude}";
    }

    private static double TruncateFourDecimals(double value) =>
        Math.Truncate(value * 10_000d) / 10_000d;

    private async Task<CachedPayload?> ReadCacheAsync(
        string payloadPath,
        string metadataPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(payloadPath) || !File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            var payloadInfo = new FileInfo(payloadPath);
            var metadataInfo = new FileInfo(metadataPath);
            if (payloadInfo.Length is <= 0 or > MaximumDownloadBytes ||
                metadataInfo.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }

            var payload = await File.ReadAllTextAsync(payloadPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            var metadataJson = await File.ReadAllTextAsync(metadataPath, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(metadataJson);
            return metadata is null ? null : new CachedPayload(payload, metadata);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            Trace.WriteLine($"Unable to read MET Norway cache: {exception.Message}");
            return null;
        }
    }

    private static async Task<string> ReadLimitedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumDownloadBytes)
            {
                throw new InvalidDataException(
                    "MET Norway response exceeded the 15 MB download limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static DateTimeOffset ReadExpiry(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        var expiry = response.Content.Headers.Expires;
        if (expiry is null && response.Headers.CacheControl?.MaxAge is { } maximumAge)
        {
            expiry = now + maximumAge;
        }

        return expiry is null || expiry <= now
            ? now + MissingExpiryFallback
            : expiry.Value;
    }

    private static async Task WriteCacheAsync(
        string payloadPath,
        string metadataPath,
        string payload,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        var payloadTemporaryPath = payloadPath + ".tmp";
        await File.WriteAllTextAsync(
                payloadTemporaryPath,
                payload,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(payloadTemporaryPath, payloadPath, overwrite: true);
        await WriteMetadataAsync(metadataPath, metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteMetadataAsync(
        string metadataPath,
        CacheMetadata metadata,
        CancellationToken cancellationToken)
    {
        var temporaryPath = metadataPath + ".tmp";
        var json = JsonSerializer.Serialize(metadata);
        await File.WriteAllTextAsync(
                temporaryPath,
                json,
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, metadataPath, overwrite: true);
    }

    private static string ValidateDDrivePath(string value)
    {
        var fullPath = Path.GetFullPath(value);
        if (!string.Equals(
                Path.GetPathRoot(fullPath),
                @"D:\",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Weather cache paths must remain on the D: drive.",
                nameof(value));
        }

        return fullPath;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private sealed record CacheMetadata(
        DateTimeOffset RetrievedAtUtc,
        DateTimeOffset ExpiresUtc,
        DateTimeOffset? LastModifiedUtc,
        string SourceUri);

    private sealed record CachedPayload(string Payload, CacheMetadata Metadata);
}
