using HuiTian.Core;
using HuiTian.Infrastructure;

var videoCases = new (string Input, string Query)[]
{
    ("我想看流浪地球", "流浪地球"),
    ("今晚想看电影《霸王别姬》", "霸王别姬"),
    ("流浪地球在哪里能看？", "流浪地球"),
    ("哪里可以看长安三万里", "长安三万里"),
    ("帮我找能看长安三万里的地方并播放", "长安三万里"),
    ("我想看一部电影", "电影"),
    ("给我播放纪录片地球脉动", "地球脉动"),
    ("给我看流浪地球", "流浪地球"),
    ("我现在想追狂飙", "狂飙"),
    ("打开《流浪地球》给我看", "流浪地球"),
};

foreach (var (input, expectedQuery) in videoCases)
{
    Require(
        MediaConsumptionIntentParser.TryParse(input, out var intent) &&
        intent.Kind == MediaConsumptionKind.Video &&
        intent.Query == expectedQuery,
        $"应该识别为观看媒体：{input}");
}

var audioCases = new (string Input, string Query)[]
{
    ("我想听周杰伦的稻香", "周杰伦的稻香"),
    ("给我放一首周杰伦的歌", "周杰伦的歌"),
    ("稻香哪里能听？", "稻香"),
    ("我想听有声书三体", "三体"),
    ("帮我找能听稻香的平台", "稻香"),
};

foreach (var (input, expectedQuery) in audioCases)
{
    Require(
        MediaConsumptionIntentParser.TryParse(input, out var intent) &&
        intent.Kind == MediaConsumptionKind.Audio &&
        intent.Query == expectedQuery,
        $"应该识别为收听媒体：{input}");
}

foreach (var input in new[]
         {
             "你看过流浪地球吗？",
             "流浪地球好看吗？",
             "我已经看过流浪地球了",
             "我想看明天大理的天气",
             "我想听听你的建议",
             "我想看看这份文件",
             "推荐我看一部电影",
             "我想知道流浪地球讲了什么",
         })
{
    Require(!MediaConsumptionIntentParser.TryParse(input, out _),
        $"不应误判为媒体操作：{input}");
}

Require(
    MediaConsumptionIntentParser.TryParse("我想免费看《流浪地球》", out var safeIntent),
    "带越权措辞的观看请求也应安全落到正版来源，而不是交给不受控执行");
var safeUri = WindowsMediaConsumptionFastPath.BuildLicensedSourceSearchUri(safeIntent);
var decodedVideoUri = Uri.UnescapeDataString(safeUri.AbsoluteUri);
Require(
    safeUri.Host.Equals("cn.bing.com", StringComparison.OrdinalIgnoreCase) &&
    decodedVideoUri.Contains("site:v.qq.com", StringComparison.Ordinal) &&
    decodedVideoUri.Contains("site:iqiyi.com", StringComparison.Ordinal) &&
    !decodedVideoUri.Contains("免费", StringComparison.Ordinal) &&
    !decodedVideoUri.Contains("破解", StringComparison.Ordinal),
    "视频发现 URL 必须限定正版平台并清除绕过付费的词");

Require(
    MediaConsumptionIntentParser.TryParse("我想听稻香", out var audioIntent),
    "应识别音频意图");
var audioUri = WindowsMediaConsumptionFastPath.BuildLicensedSourceSearchUri(audioIntent);
var decodedAudioUri = Uri.UnescapeDataString(audioUri.AbsoluteUri);
Require(
    decodedAudioUri.Contains("site:y.qq.com", StringComparison.Ordinal) &&
    decodedAudioUri.Contains("site:music.163.com", StringComparison.Ordinal),
    "音频发现 URL 应限定常见正版音乐平台");

var launcher = new CapturingLauncher();
var handler = new WindowsMediaConsumptionFastPath(launcher);
var result = await handler.ExecuteAsync(videoCases.Length > 0 &&
    MediaConsumptionIntentParser.TryParse(videoCases[0].Input, out var executableIntent)
        ? executableIntent
        : throw new InvalidOperationException("测试意图未能解析"));
Require(
    result.Success &&
    result.ResultCode == "licensed_source_search_opened" &&
    launcher.Opened.Count == 1 &&
    launcher.Opened[0] == result.OpenedUri,
    "执行器应只打开一次正版来源搜索");

Console.WriteLine("PASS: generic media intent, false-positive guards, licensed-source discovery, and bounded execution");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class CapturingLauncher : IMediaTargetLauncher
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri target) => Opened.Add(target);
}
