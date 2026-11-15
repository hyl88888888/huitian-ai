using System.Diagnostics;
using System.Text.Json;
using HuiTian.Business.Core;
using HuiTian.Business.Adapters;
using HuiTian.Business.Runtime;
using HuiTian.Business.Tests;

var root = BusinessPaths.Discover();
root.EnsureDirectories();
if (args.Contains("--intent-live") || args.Contains("--intent-live-precise"))
{
    await IntentBenchmark.RunAsync(root, args.Contains("--intent-live-precise"));
    return;
}
if (args.Contains("--tools-benchmark"))
{
    await ToolBenchmark.RunAsync(root);
    return;
}
if (args.Contains("--live") || args.Contains("--routed-live"))
{
    var routed = args.Contains("--routed-live");
    await using var runtime = new LocalRuntimeManager(root);
    var startup = Stopwatch.StartNew();
    await runtime.EnsureReadyAsync(new Progress<string>(Console.WriteLine), CancellationToken.None);
    var startupMs = startup.ElapsedMilliseconds;
    Console.WriteLine($"Runtime ready in {startupMs} ms");
    using var model = new LocalModelClient("http://127.0.0.1:18280/v1/chat/completions", LocalRuntimeManager.Alias);
    var fixturePaths = new BusinessPaths(Path.Combine(root.Root, "Cache", "LiveValidation", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    fixturePaths.EnsureDirectories();
    await using var registry = new DesktopToolRegistry(fixturePaths);
    var recorded = new RecordingModel(model, fixturePaths.Logs);
    ITaskPlanner planner = routed ? new RoutedTaskPlanner(recorded) : new SemanticTaskPlanner(recorded);
    var engine = new AgentEngine(planner, registry, fixturePaths);
    var testCases = new[] {
        (Text: "用一句话讲清楚什么叫现金流，不用生成文件。", Expected: "answer"),
        (Text: "给我们新来的同事做一份能在Word里打开的入职清单，三条就够，保存成新人清单.docx。", Expected: "docx"),
        (Text: "把下面两笔开销做成Excel文件给我：公交12元，午餐25元。列名是项目和金额，文件名本日支出.xlsx。", Expected: "xlsx"),
        (Text: "我想在会议上放两页演示，介绍团队的本周安排：第一页讲客户回访，第二页讲产品测试。请直接给PPTX文件，叫周会安排.pptx。", Expected: "pptx")
    };
    var rows = new List<object>();
    var failures = 0;
    foreach (var sample in testCases)
    {
        var timer = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = await engine.RunAsync(sample.Text, [], new Progress<AgentEvent>(e=>Console.WriteLine($"{e.State}: {e.Message}")), (_,_,_)=>Task.FromResult(false), deadline.Token);
        var hasExpectedOutput = (sample.Expected == "answer"
            ? result.Artifacts.Count == 0
            : result.Artifacts.Any(a=>Path.GetExtension(a.Path).Equals("."+sample.Expected,StringComparison.OrdinalIgnoreCase)&&File.Exists(a.Path)));
        var pass = result.State == (sample.Expected == "answer" ? RunState.Answered : RunState.Completed) && hasExpectedOutput;
        var contentCheck = "not_applicable";
        if (hasExpectedOutput && sample.Expected != "answer")
        {
            var file = result.Artifacts.First(a=>Path.GetExtension(a.Path).Equals("."+sample.Expected,StringComparison.OrdinalIgnoreCase));
            var read = await registry.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = file.Path, maxCharacters = 64000 }), deadline.Token);
            using var evidence = JsonDocument.Parse(read.Evidence);
            var actual = evidence.RootElement.GetProperty("text").GetString() ?? "";
            // Fixture-specific assertions belong only to tests, never to natural-language routing.
            var contentPass = sample.Expected switch
            {
                "docx" => actual.Contains("1.") && actual.Contains("2.") && actual.Contains("3.") && !actual.Contains("[待补充]"),
                "xlsx" => actual.Contains("项目\t金额") && actual.Contains("公交\t12") && actual.Contains("午餐\t25"),
                "pptx" => actual.Contains("[幻灯片1]") && actual.Contains("[幻灯片2]") && !actual.Contains("[幻灯片3]") && actual.Contains("客户回访") && actual.Contains("产品测试") && !actual.Contains("[待补充]") && !actual.Contains("V2.1") && !actual.Contains("A、B、C公司"),
                _ => false
            };
            pass &= read.Success && !evidence.RootElement.GetProperty("truncated").GetBoolean() && contentPass;
            // These developer-visible checks only match fragments; they cannot establish factual fidelity.
            contentCheck = contentPass ? "fixture_fragments_pass_semantics_unverified" : "fixture_fragments_fail";
        }
        rows.Add(new { sample.Text, sample.Expected, pass, hasExpectedOutput, contentCheck, elapsedMs=timer.ElapsedMilliseconds, result });
        if (!pass) failures++;
        Console.WriteLine($"LIVE {(pass?"PASS":"FAIL")} {sample.Expected}: {timer.ElapsedMilliseconds} ms");
    }
    var reportPath = Path.Combine(fixturePaths.Data,"live-report.json");
    var memory = LocalRuntimeManager.Memory();
    await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(new{planner=routed?"routed_prototype":"monolithic",
        evaluationScope="Developer-visible samples; structural and fragment checks only. No independent semantic or business acceptance.",
        hardware=new { totalMemory=memory.Total, availableMemory=memory.Available },startupMs,failures,cases=rows},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"LIVE REPORT: {reportPath}");
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}
var count = await EngineRegression.RunAsync();
count += await AdapterRegression.RunAsync();
count += await RoutedPlannerRegression.RunAsync();
Console.WriteLine($"TOTAL {count} regression checks passed.");

sealed class RecordingModel(LocalModelClient inner, string directory) : IStructuredModelClient
{
    private int sequence;
    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, bool json, int maxTokens, CancellationToken token)
    {
        var id = Interlocked.Increment(ref sequence);
        var reply = await inner.CompleteAsync(messages, json, maxTokens, token);
        await File.WriteAllTextAsync(Path.Combine(directory, $"model-{id:D2}.json"), JsonSerializer.Serialize(new { messages, reply }, new JsonSerializerOptions { WriteIndented = true }), token);
        return reply;
    }
    public async Task<string> CompleteStructuredAsync(IReadOnlyList<ChatMessage> messages, JsonElement schema, int maxTokens, CancellationToken token)
    {
        var id = Interlocked.Increment(ref sequence);
        var reply = await inner.CompleteStructuredAsync(messages, schema, maxTokens, token);
        await File.WriteAllTextAsync(Path.Combine(directory, $"model-{id:D2}.json"), JsonSerializer.Serialize(new { messages, schema, reply }, new JsonSerializerOptions { WriteIndented = true }), token);
        return reply;
    }
}
