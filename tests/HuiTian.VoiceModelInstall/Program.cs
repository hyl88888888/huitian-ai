using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using HuiTian.Core;
using HuiTian.Infrastructure;

const string repository = "gomodels/sherpa";
const string modelRoot = "kokoro-int8-multi-lang-v1_1";
const string destinationRoot = @"D:\HuiTian\Runtime\Voice\Kokoro";

using var metadataClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var downloadEngine = new SegmentedDownloadEngine();
var files = new List<ModelFile>();
var pendingDirectories = new Queue<string>();
pendingDirectories.Enqueue(modelRoot);
while (pendingDirectories.Count > 0)
{
    var root = pendingDirectories.Dequeue();
    var metadataUri =
        $"https://www.modelscope.cn/api/v1/models/{repository}/repo/files?Revision=master&Root={Uri.EscapeDataString(root)}";
    var response = await metadataClient.GetFromJsonAsync<ModelScopeResponse>(metadataUri)
                   ?? throw new InvalidDataException("国内语音源没有返回文件清单。 ");
    if (!response.Success || response.Data?.Files is null)
    {
        throw new InvalidDataException(response.Message ?? "国内语音源文件清单无效。 ");
    }

    foreach (var item in response.Data.Files)
    {
        if (item.Type == "tree")
        {
            pendingDirectories.Enqueue(item.Path);
        }
        else if (item.Size > 0 && item.Sha256.Length == 64)
        {
            files.Add(item);
        }
    }
}

var totalBytes = files.Sum(file => file.Size);
long completedBytes = 0;
Console.WriteLine($"VOICE_FILES={files.Count} VOICE_TOTAL_MB={totalBytes / 1024d / 1024d:0.0}");
foreach (var file in files.OrderByDescending(file => file.Size))
{
    var relativePath = file.Path[(modelRoot.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
    var destination = Path.Combine(destinationRoot, relativePath);
    if (await MatchesAsync(destination, file.Size, file.Sha256))
    {
        completedBytes += file.Size;
        continue;
    }

    var encodedPath = string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
    var source = new Uri($"https://modelscope.cn/models/{repository}/resolve/master/{encodedPath}");
    var lastReportedBucket = -1;
    var fileBaseBytes = completedBytes;
    var progress = new Progress<DownloadProgress>(update =>
    {
        var overall = (fileBaseBytes + update.DownloadedBytes) * 100d / totalBytes;
        var bucket = (int)(overall / 5);
        if (bucket != lastReportedBucket)
        {
            lastReportedBucket = bucket;
            Console.WriteLine($"VOICE_PROGRESS={overall:0.0}% FILE={relativePath}");
        }
    });
    await downloadEngine.DownloadAsync(
        new DownloadRequest(
            "voice-" + Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(file.Path)))[..16],
            destination,
            file.Size,
            file.Sha256,
            [new DownloadSource("国内语音节点", source)],
            SegmentSizeBytes: file.Size >= 16 * 1024 * 1024
                ? 4 * 1024 * 1024
                : (int)Math.Max(1024 * 1024, file.Size),
            MaxConcurrency: file.Size >= 16 * 1024 * 1024 ? 4 : 1,
            RetryCountPerSegment: 4),
        progress);
    completedBytes += file.Size;
}

Console.WriteLine("PASS: verified natural voice model installed on D drive");

static async Task<bool> MatchesAsync(string path, long size, string expectedSha256)
{
    if (!File.Exists(path) || new FileInfo(path).Length != size)
    {
        return false;
    }

    await using var input = File.OpenRead(path);
    var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input));
    return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ModelScopeResponse(
    [property: JsonPropertyName("Success")] bool Success,
    [property: JsonPropertyName("Message")] string? Message,
    [property: JsonPropertyName("Data")] ModelScopeData? Data);

internal sealed record ModelScopeData(
    [property: JsonPropertyName("Files")] List<ModelFile>? Files);

internal sealed record ModelFile(
    [property: JsonPropertyName("Path")] string Path,
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("Sha256")] string Sha256);
