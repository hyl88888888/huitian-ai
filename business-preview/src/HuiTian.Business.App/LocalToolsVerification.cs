using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using HuiTian.Business.Adapters;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

internal static class LocalToolsVerification
{
    public static async Task<bool> RunAsync(BusinessPaths productPaths, string reportPath)
    {
        var paths = new BusinessPaths(Path.Combine(productPaths.Temp, "local-tools-verification-" + Guid.NewGuid().ToString("N")));
        paths.EnsureDirectories();
        var checks = new List<string>();
        try
        {
            await using var fixtures = new DesktopToolRegistry(paths);
            async Task<string> Fixture(string kind, string fileName, string content = "", string[][]? rows = null, object[]? slides = null)
            {
                var result = await fixtures.ExecuteAsync("create_document", JsonSerializer.SerializeToElement(new { kind, fileName, title = "", content, rows = rows ?? [], slides = slides ?? [] }), CancellationToken.None);
                if (!result.Success) throw new InvalidOperationException(result.Summary);
                return result.Artifacts![0].Path;
            }
            var pptx = await Fixture("pptx", "sample.pptx", slides: [new { title = "本地演示样例", body = "演示正文测试" }, new { title = "第二页", body = "保留第二页" }]);
            var xlsx = await Fixture("xlsx", "sample.xlsx", rows: [["姓名", "城市"], ["甲", "西安"], ["甲", "西安"], ["乙", "北京"]]);
            var docx = await Fixture("docx", "sample.docx", "文档正文测试");
            var txt = await Fixture("txt", "sample.txt", "文本正文测试");
            var longTxt = await Fixture("txt", "long-sample.txt", new string('文', 300));
            var sourceHashes = new[] { pptx, xlsx, docx, txt, longTxt }.ToDictionary(p => p, Hash);
            await using var runner = new LocalToolsRunner(paths);
            var style = await runner.ExecuteAsync(new(LocalOperation.PresentationStyle, pptx, "style-copy.pptx", "#E1F3F4", "Microsoft YaHei", "#173A40"), null, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(style.Artifacts[0].Path))
            {
                using var stream = archive.GetEntry("ppt/slides/slide1.xml")!.Open();
                var xml = XDocument.Load(stream);
                XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
                Check(xml.Descendants(drawing + "srgbClr").Any(n => (string?)n.Attribute("val") == "E1F3F4") && xml.Descendants(drawing + "ea").Any(n => (string?)n.Attribute("typeface") == "Microsoft YaHei"), "PPTX 新副本背景与字体已改变", checks);
            }
            var dedup = await runner.ExecuteAsync(new(LocalOperation.WorksheetDeduplication, xlsx, "dedup-copy.xlsx", SheetName: "Sheet1", KeyColumns: "A,B"), null, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(dedup.Artifacts[0].Path))
            {
                using var stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var xml = XDocument.Load(stream);
                XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var rows = xml.Descendants(sheet + "row").ToList();
                Check(rows.Count == 3 && rows[0].Value.Contains("姓名") && rows[1].Value.Contains("甲"), "XLSX 去重保留表头及首次出现的数据", checks);
            }
            foreach (var (source, expected) in new[] { (docx, "文档正文测试"), (xlsx, "西安"), (pptx, "演示正文测试"), (txt, "文本正文测试") })
            {
                var result = await runner.ExecuteAsync(new(LocalOperation.ExtractText, source, "extracted-" + Path.GetExtension(source)[1..] + ".txt"), null, CancellationToken.None);
                Check(!result.Partial && File.ReadAllText(result.Artifacts[0].Path).Contains(expected), Path.GetExtension(source) + " 文字提取来自实际文件", checks);
            }
            var partial = await runner.ExecuteAsync(new(LocalOperation.ExtractText, longTxt, "partial.txt", MaxCharacters: 20), null, CancellationToken.None);
            Check(partial.Partial && File.ReadAllText(partial.Artifacts[0].Path).StartsWith("【部分提取】") && partial.Summary.Contains("不代表完整"), "截断结果在状态与 TXT 中同时标明部分提取", checks);
            var outputHash = Hash(style.Artifacts[0].Path);
            await ExpectFailureAsync(() => runner.ExecuteAsync(new(LocalOperation.PresentationStyle, pptx, "style-copy.pptx"), null, CancellationToken.None), "目标文件已存在");
            Check(Hash(style.Artifacts[0].Path) == outputHash, "已有输出文件未被覆盖", checks);
            await ExpectFailureAsync(() => runner.ExecuteAsync(new(LocalOperation.WorksheetDeduplication, xlsx, "bad-sheet.xlsx", SheetName: "不存在"), null, CancellationToken.None), "找不到指定工作表");
            Check(!File.Exists(Path.Combine(paths.Outputs, "bad-sheet.xlsx")), "参数错误明确返回且不遗留失败副本", checks);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await ExpectCancelledAsync(() => runner.ExecuteAsync(new(LocalOperation.ExtractText, txt, "cancelled.txt"), null, cancelled.Token));
                Check(!File.Exists(Path.Combine(paths.Outputs, "cancelled.txt")), "开始前取消未创建输出", checks);
            }
            Check(sourceHashes.All(pair => Hash(pair.Key) == pair.Value), "全部原文件保持原始字节", checks);
            var slow = new ControlledRegistry(style.Artifacts[0]);
            var window = new LocalToolsWindow(paths, new LocalToolsRunner(paths, slow));
            window.SetSourceFile(pptx);
            ((Button)window.FindName("ExecuteOperation")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => slow.Started.Task.IsCompleted);
            var completed = false;
            window.Closed += (_, _) => completed = true;
            window.Close();
            window.Close();
            Check(!completed && !slow.Disposed, "窗口关闭等待同步文件步骤而不提前释放", checks);
            slow.Complete.SetResult();
            await WaitForAsync(() => completed);
            Check(slow.Disposed && ((TextBox)window.FindName("ResultDescription")).Text.Contains("真实文件") && ((ItemsControl)window.FindName("ResultArtifacts")).Items.Count == 1, "取消期间已生成的真实输出仍展示且服务已释放", checks);
            await window.DisposeServicesAsync();
            await WriteAsync(reportPath, new { passed = true, checks, testRoot = paths.Root, actualModelCalls = 0, externalActions = 0 });
            return true;
        }
        catch (Exception ex)
        {
            await WriteAsync(reportPath, new { passed = false, checks, testRoot = paths.Root, error = ex.ToString() });
            return false;
        }
    }
    private static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task ExpectFailureAsync(Func<Task<LocalOperationResult>> action, string expected)
    {
        try { await action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(expected)) { return; }
        throw new InvalidOperationException("未明确拒绝：" + expected);
    }
    private static async Task ExpectCancelledAsync(Func<Task<LocalOperationResult>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("未响应取消。");
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task WriteAsync(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    private sealed class ControlledRegistry(Artifact artifact) : IToolRegistry, IAsyncDisposable
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Complete.Task; // Mirrors a synchronous file step that has already started.
            return new(true, "已创建独立测试副本。", "{\"reopened\":true}", [artifact]);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
