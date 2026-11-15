using System.Diagnostics;
using System.Text.Json;
using HuiTian.Business.Core;
using HuiTian.Business.Adapters;

namespace HuiTian.Business.Tests;

public static class ToolBenchmark
{
    public static async Task RunAsync(BusinessPaths root)
    {
        var paths = new BusinessPaths(Path.Combine(root.Root, "Cache", "Tests", "tool-benchmark-" + Guid.NewGuid().ToString("N")));
        paths.EnsureDirectories();
        await using var tools = new DesktopToolRegistry(paths);
        var timings = new List<object>();
        async Task<ToolResult> Execute(string tool, object parameters)
        {
            var watch = Stopwatch.StartNew();
            var result = await tools.ExecuteAsync(tool, JsonSerializer.SerializeToElement(parameters), CancellationToken.None);
            timings.Add(new { tool, elapsedMs = watch.Elapsed.TotalMilliseconds, result.Success, result.Evidence });
            if (!result.Success) throw new InvalidOperationException(result.Summary);
            return result;
        }
        var rows = Enumerable.Range(0, 100).Select(i => new[] { "项目" + (i % 50), (i % 50).ToString() }).Prepend(["项目", "金额"]).ToArray();
        var table = await Execute("create_document", new { kind = "xlsx", fileName = "原始数据.xlsx", title = "测试", content = "", rows, slides = Array.Empty<object>() });
        await Execute("deduplicate_worksheet", new { path = table.Artifacts![0].Path, fileName = "去重结果.xlsx", sheetName = "Sheet1", keyColumns = new[] { "A", "B" } });
        var deck = await Execute("create_document", new { kind = "pptx", fileName = "原始演示.pptx", title = "测试", content = "", rows = Array.Empty<object>(), slides = new[] { new { title = "项目安排", body = "整理材料\n核对结果" }, new { title = "验收标准", body = "文件存在\n内容正确" } } });
        await Execute("change_presentation_style", new { path = deck.Artifacts![0].Path, fileName = "新版演示.pptx", backgroundColor = "FFFFFF", fontFamily = "Microsoft YaHei", fontColor = "000000" });
        await Execute("read_file", new { path = deck.Artifacts[0].Path, maxCharacters = 64000 });
        var report = Path.Combine(root.Root, "Evidence", "tool-benchmark.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { modelCalls = 0, networkRequests = 0, machine = Environment.MachineName, fixtures = paths.Root, timings }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var timing in timings) Console.WriteLine(JsonSerializer.Serialize(timing));
        Console.WriteLine("TOOL BENCHMARK: " + report);
    }
}
