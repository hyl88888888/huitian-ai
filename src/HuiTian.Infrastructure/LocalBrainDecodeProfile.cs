using System.Security.Cryptography;
using System.Text.Json;

namespace HuiTian.Infrastructure;

/// <summary>An optional result of a manual benchmark, valid only for its measured environment.</summary>
public sealed record LocalBrainDecodeProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public int SchemaVersion { get; init; }
    public DateTimeOffset MeasuredAtUtc { get; init; }
    public string SourceReport { get; init; } = "";
    // Omission must never silently opt a machine into different decoding settings.
    public bool UseEmbeddedMtp { get; init; } = true;
    public string Fingerprint { get; init; } = "";

    public static string CreateHardwareSignature(
        string cpuName,
        string gpuName,
        long physicalMemoryBytes,
        long videoMemoryBytes)
    {
        if (string.IsNullOrWhiteSpace(cpuName) || string.IsNullOrWhiteSpace(gpuName) ||
            physicalMemoryBytes <= 0 || videoMemoryBytes <= 0)
        {
            throw new InvalidDataException("硬件信息不完整，不能应用实测解码设置。");
        }

        return Hash(new
        {
            CpuName = cpuName.Trim(),
            GpuName = gpuName.Trim(),
            PhysicalMemoryBytes = physicalMemoryBytes,
            VideoMemoryBytes = videoMemoryBytes,
        });
    }

    /// <summary>Reads file metadata only; it never reads or hashes model contents.</summary>
    public static string CaptureFingerprint(
        string executable,
        string brainPath,
        string visionPath,
        int contextTokens,
        string hardwareSignature,
        string? machineName = null,
        int? processorCount = null)
    {
        if (string.IsNullOrWhiteSpace(hardwareSignature))
        {
            throw new InvalidDataException("缺少本机硬件签名。");
        }

        var runtimeDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        // llama-server.exe can be a tiny wrapper. All adjacent runtime binaries must match.
        var runtimeFiles = Directory.EnumerateFiles(runtimeDirectory)
            .Where(path => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(ReadFileStamp)
            .ToArray();

        return Hash(new
        {
            Version = 1,
            MachineName = (machineName ?? Environment.MachineName).ToUpperInvariant(),
            ProcessorCount = processorCount ?? Environment.ProcessorCount,
            HardwareSignature = hardwareSignature,
            ContextTokens = contextTokens,
            Brain = ReadFileStamp(brainPath),
            Vision = ReadFileStamp(visionPath),
            Server = ReadFileStamp(executable),
            RuntimeFiles = runtimeFiles,
        });
    }

    public static async Task<LocalBrainDecodeSettings> LoadAsync(
        string profilePath,
        string executable,
        string brainPath,
        string visionPath,
        int contextTokens,
        Func<CancellationToken, Task<string>> readHardwareSignature,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(profilePath))
        {
            return Default("未找到本机实测解码设置，使用默认内置解码加速。");
        }

        try
        {
            await using var stream = File.OpenRead(profilePath);
            var profile = await JsonSerializer.DeserializeAsync<LocalBrainDecodeProfile>(
                    stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null || profile.SchemaVersion != 1 ||
                profile.MeasuredAtUtc == default ||
                profile.MeasuredAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
                string.IsNullOrWhiteSpace(profile.SourceReport) ||
                string.IsNullOrWhiteSpace(profile.Fingerprint) || profile.Fingerprint.Length != 64)
            {
                return Default("本机实测解码设置缺少有效的测量时间、来源报告或指纹，使用默认设置。");
            }

            if (profile.UseEmbeddedMtp)
            {
                return Default("本机实测解码设置未请求关闭内置解码加速，使用默认设置。");
            }

            using var hardwareTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hardwareTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            string hardwareSignature;
            try
            {
                hardwareSignature = await readHardwareSignature(hardwareTimeout.Token)
                    .WaitAsync(hardwareTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Default("本机硬件检测超时或取消，使用默认解码设置。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentFingerprint = CaptureFingerprint(
                executable, brainPath, visionPath, contextTokens, hardwareSignature);
            if (!string.Equals(profile.Fingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Default("本机、硬件、模型或运行组件与实测指纹不匹配，使用默认解码设置。");
            }

            return new LocalBrainDecodeSettings(false, true,
                $"已加载本机实测解码设置：关闭内置解码加速；测量时间 {profile.MeasuredAtUtc:O}；" +
                $"来源报告 {profile.SourceReport.Replace('\r', ' ').Replace('\n', ' ')}。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Default($"本机实测解码设置读取失败（{exception.GetType().Name}），使用默认设置。");
        }
    }

    private static LocalBrainDecodeSettings Default(string message) => new(true, false, message);

    private static object ReadFileStamp(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists)
        {
            throw new FileNotFoundException("实测解码设置所需的文件不存在。", file.FullName);
        }

        return new
        {
            Path = file.FullName.ToUpperInvariant(),
            file.Length,
            LastWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
        };
    }

    private static string Hash<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public sealed record LocalBrainDecodeSettings(bool UseEmbeddedMtp, bool ProfileApplied, string Message);
