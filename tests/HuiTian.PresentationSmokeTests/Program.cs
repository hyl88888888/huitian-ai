using System.Text.Json;
using HuiTian.Infrastructure;

const string validationRoot = @"D:\HuiTian\Build\Validation-0.3.1\presentation";
Directory.CreateDirectory(validationRoot);
var payload = JsonSerializer.SerializeToElement(new
{
    title = "回天 AI 产品体验升级方案",
    slides = new[]
    {
        new
        {
            title = "用户真正需要什么",
            body = "打开即用，不需要理解技术名词\n对话、语音与电脑操作自然衔接\n所有大文件、缓存和产物优先保存在 D 盘",
        },
        new
        {
            title = "核心体验闭环",
            body = "理解目标：先判断用户真正要交付的结果\n主动执行：观察屏幕并选择最快路径\n持续纠错：遇到问题重新观察并尝试安全替代方式\n清晰交付：回答、过程和产物各自分层展示",
        },
        new
        {
            title = "语音体验升级",
            body = "本地流式识别，边说边显示\n自然中文音色，回复更柔和\n右侧紧凑浮窗，三个操作一眼可见\n语音内容留在本机，不产生接口费用",
        },
        new
        {
            title = "第一版验收标准",
            body = "核心链路真实可用，而非只展示界面\n任务失败能够自动恢复\n文件与非文字产物在对话中直接可见\n每项能力都可以让普通用户亲自测试",
        },
    },
});

var bridge = new WindowsToolBridge();
var result = await bridge.ExecuteAsync(
    "huitian_create_presentation",
    payload,
    CancellationToken.None);
if (!result.Success)
{
    throw new InvalidDataException(result.Text);
}

var presentationPath = result.ArtifactPaths?.SingleOrDefault()
                       ?? throw new InvalidDataException("Presentation artifact path was not returned.");
if (!File.Exists(presentationPath))
{
    throw new FileNotFoundException("Presentation output was not created.", presentationPath);
}

dynamic? powerPoint = null;
dynamic? presentation = null;
try
{
    var type = Type.GetTypeFromProgID("PowerPoint.Application")
               ?? throw new InvalidOperationException("PowerPoint is unavailable.");
    powerPoint = Activator.CreateInstance(type)!;
    presentation = powerPoint.Presentations.Open(presentationPath, WithWindow: false);
    if ((int)presentation.Slides.Count != 5)
    {
        throw new InvalidDataException("Expected a cover and four content slides.");
    }

    presentation.Slides.Item(1).Export(Path.Combine(validationRoot, "cover.png"), "PNG", 1600, 900);
    presentation.Slides.Item(2).Export(Path.Combine(validationRoot, "content.png"), "PNG", 1600, 900);
}
finally
{
    try { presentation?.Close(); } catch { }
    try { powerPoint?.Quit(); } catch { }
}

Console.WriteLine($"PASS: polished 16:9 presentation => {presentationPath}");
