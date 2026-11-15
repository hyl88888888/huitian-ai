using System.Diagnostics;
using System.Text.Json;
using HuiTian.Business.Core;
using HuiTian.Business.Runtime;

namespace HuiTian.Business.Tests;

// Development-visible examples, never a business routing dictionary or an unseen acceptance set.
internal static class IntentBenchmark
{
    private sealed record Sample(string Text, string Mode, string[] Groups, ChatMessage[] History);

    public static async Task RunAsync(BusinessPaths root, bool precise = false)
    {
        var samples = new Sample[]
        {
            new("什么是净利润？说得通俗点。", "answer", [], []),
            new("教我怎么在表格里算平均数，先别动电脑。", "answer", [], []),
            new("给我想三个校园广播站招新的标题，直接写在这里。", "answer", [], []),
            new("去浏览器里给我找一首能听的稻香，放出来。", "task", ["browser"], []),
            new("今晚想看电影，看看正规网站哪里能播放千与千寻。", "task", ["browser"], []),
            new("到外卖网站替我挑一份清淡的晚餐，预算三十，先加入购物车。", "task", ["browser"], []),
            new("我预算两千，搜一下适合大学生用的显示器，把候选的优缺点整理给我。", "task", ["browser"], []),
            new("这位招聘人员问我何时到岗，替我在微信里回她我下周一可以。", "task", ["desktop"], []),
            new("把微信调到前面就行。", "task", ["desktop"], []),
            new("D:\\实验资料里面昨天下载的论文帮我找找。", "task", ["files"], []),
            new("按这些开销给我做个能打开的Excel：车费二十，饭钱十五。", "task", ["documents"], []),
            // documents already includes read_file; known exact source paths do not require file discovery.
            new("有一份客户说明在D:\\资料\\介绍.docx，先读它，再做一份三页的演示文件。", "task", ["documents"], []),
            new("再短一点，口吻别太正式。", "answer", [],
                [new("user", "在这里帮我写一句生日祝福。"), new("assistant", "祝你生日快乐，愿你新的一岁万事顺遂、平安喜乐。")]),
            new("把它发给老李。", "task", ["desktop"],
                [new("user", "要处理的是D:\\资料\\报价.xlsx，联系人是微信里的老李。"), new("assistant", "已记录这份报价表和微信联系人老李，尚未发送。")]),
            new("帮我查杭州新媒体运营岗位，联系三家公司，询问能否视频面试。", "task", ["browser"], []),
            new("替我在微信和几个朋友分别聊聊近况，别把一个人的话发给另一个人。", "task", ["desktop"], [])
        };
        await using var runtime = new LocalRuntimeManager(root);
        await runtime.EnsureReadyAsync(new Progress<string>(Console.WriteLine), CancellationToken.None);
        var sampling = precise ? new ModelSampling(0.2, 1.0, 40, 0) : new ModelSampling();
        using var model = new LocalModelClient("http://127.0.0.1:18280/v1/chat/completions", LocalRuntimeManager.Alias, sampling: sampling);
        var router = new SemanticIntentRouter(model);
        var rows = new List<object>();
        var timings = new List<long>();
        var passed = 0;
        foreach (var sample in samples)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var timer = Stopwatch.StartNew();
            SemanticIntent? intent = null;
            string? error = null;
            try { intent = await router.RouteAsync(sample.Text, sample.History, deadline.Token); }
            catch (Exception ex) { error = ex.Message; }
            var elapsed = timer.ElapsedMilliseconds;
            timings.Add(elapsed);
            var pass = intent is not null && intent.Mode == sample.Mode &&
                sample.Groups.All(g => intent.CapabilityGroups.Contains(g, StringComparer.Ordinal));
            if (pass) passed++;
            rows.Add(new { sample.Text, expectedMode = sample.Mode, expectedGroups = sample.Groups,
                history = sample.History, pass, elapsedMs = elapsed, intent, error });
            Console.WriteLine($"INTENT {rows.Count}/{samples.Length} {(pass ? "PASS" : "FAIL")} {elapsed}ms");
        }
        timings.Sort();
        long Percentile(double p) => timings[Math.Clamp((int)Math.Ceiling(p * timings.Count) - 1, 0, timings.Count - 1)];
        var report = new { dataset = "development_visible_routing_only", model = "fixed_2B_CPU", sampling,
            passed, total = samples.Length, p50Ms = Percentile(.50), p95Ms = Percentile(.95),
            note = "仅检查输出模式和所需能力组；不证明完整语义准确率、事实正确性、任务可执行性或外部送达。没有操作用户软件或发送消息。",
            cases = rows };
        var reportPath = Path.Combine(root.Root, "Evidence", "intent-benchmark-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"INTENT {passed}/{samples.Length}; P50={Percentile(.50)}ms P95={Percentile(.95)}ms; {reportPath}");
        Environment.ExitCode = passed == samples.Length ? 0 : 1;
    }
}
