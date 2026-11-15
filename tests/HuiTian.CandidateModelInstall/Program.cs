using HuiTian.Core;
using HuiTian.Infrastructure;

const string repo = "JonathanColetti/Qwen3.8-27B-Uncensored-GGUF";
const string candidateRoot =
    @"D:\HuiTian\Cache\ModelCandidates\JonathanColetti-Qwen3.8-27B-Uncensored-IQ2_M";

Directory.CreateDirectory(candidateRoot);
using var engine = new SegmentedDownloadEngine();

var assets = new[]
{
    new
    {
        Id = "uncensored-iq2-m",
        FileName = "Qwen3.8-27B-Uncensored-IQ2_M.gguf",
        Length = 10_624_771_968L,
        Sha256 = "28e0f88eea09438220a086c2a1e5180ad83764c748856a28fd63ce1c0fbef187",
    },
    new
    {
        Id = "uncensored-vision-f16",
        FileName = "Qwen3.8-27B-Uncensored-vision-f16.gguf",
        Length = 927_606_912L,
        Sha256 = "5ac423f8a29059dc24e51bc6a43e9380dcd57a9347f28b62591e0b3f60b7081c",
    },
};

foreach (var asset in assets)
{
    var destination = Path.Combine(candidateRoot, asset.FileName);
    var lastPrintedAt = DateTimeOffset.MinValue;
    var progress = new Progress<DownloadProgress>(current =>
    {
        var now = DateTimeOffset.Now;
        if (current.Percentage < 100 && now - lastPrintedAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        lastPrintedAt = now;
        Console.WriteLine(
            $"{asset.Id}: {current.Percentage:F2}% " +
            $"({current.DownloadedBytes / 1_073_741_824d:F2}/" +
            $"{current.TotalBytes / 1_073_741_824d:F2} GiB) " +
            $"{current.BytesPerSecond / 1_048_576d:F2} MiB/s");
    });

    var sources = new[]
    {
        new DownloadSource(
            "国内免费镜像",
            new Uri($"https://hf-mirror.com/{repo}/resolve/main/{asset.FileName}")),
        new DownloadSource(
            "上游备用源",
            new Uri($"https://huggingface.co/{repo}/resolve/main/{asset.FileName}")),
    };

    await engine.DownloadAsync(
        new DownloadRequest(
            asset.Id,
            destination,
            asset.Length,
            asset.Sha256,
            sources,
            SegmentSizeBytes: 16 * 1024 * 1024,
            MaxConcurrency: 6,
            RetryCountPerSegment: 6),
        progress,
        CancellationToken.None);

    Console.WriteLine($"VERIFIED {asset.Id}: {destination}");
}

Console.WriteLine("PASS: candidate model and matching visual projector downloaded and SHA256 verified");
