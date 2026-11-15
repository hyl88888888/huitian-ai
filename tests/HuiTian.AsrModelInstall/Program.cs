using HuiTian.Core;
using HuiTian.Infrastructure;

const string fileName = "sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23.tar.bz2";
const long expectedLength = 74_004_050;
const string expectedSha256 = "2cbd71b640d9c37d3784f29367333a4577b0398b62e9deeed418170b081cba8b";
const string destination = @"D:\HuiTian\Cache\VoiceModels\sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23.tar.bz2";

var source = new Uri(
    $"https://modelscope.cn/models/zhaochaoqun/sherpa-onnx-asr-models/resolve/master/{fileName}");
using var engine = new SegmentedDownloadEngine();
var lastBucket = -1;
var progress = new Progress<DownloadProgress>(value =>
{
    var bucket = (int)(value.Percentage / 10);
    if (bucket == lastBucket)
    {
        return;
    }

    lastBucket = bucket;
    Console.WriteLine($"ASR_PROGRESS={value.Percentage:0.0}%");
});

var result = await engine.DownloadAsync(
    new DownloadRequest(
        "huitian-local-speech-recognition-zh",
        destination,
        expectedLength,
        expectedSha256,
        [new DownloadSource("国内语音识别节点", source)],
        SegmentSizeBytes: 4 * 1024 * 1024,
        MaxConcurrency: 4,
        RetryCountPerSegment: 5),
    progress);

Console.WriteLine($"PASS: ASR archive verified {result.Sha256}");
