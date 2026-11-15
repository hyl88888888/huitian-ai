using System.Diagnostics;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

// Capture-only model test. This class never constructs a sender or desktop tool.
internal static class ChatEntryLiveProbe
{
    internal static async Task RunAsync()
    {
        var paths = new HuiTianPaths(Path.Combine(@"D:\HuiTian\Cache\Tests", "chat-entry-live-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        var config = AgentBackendConfiguration.ProductDefault;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var router = new SemanticIntentRouter(config, paths);
        using var resolver = new CommunicationIntentResolver(config, diagnosticsDirectory: paths.AppState);
        var cases = new[] {
            new { Id = "h1", Text = "微信那边的测试联系人甲，你接着跟她交流，听她说完再回应，我还有点事。", Action = true,
                Contact = "测试联系人甲", Duration = (double?)null },
            new { Id = "h2", Text = "这回别给谁发，我要的是一段措辞建议：如何礼貌地追问进度？", Action = false,
                Contact = "", Duration = (double?)null },
            new { Id = "h3", Text = "替我在QQ上陪测试联系人乙说会儿话，半小时就行，不要自己编我的近况。", Action = true,
                Contact = "测试联系人乙", Duration = (double?)30 },
            new { Id = "h4", Text = "刚才让你跟她谈，不是让我自己照着文案发，你到微信里接着聊十分钟。", Action = true,
                Contact = "测试联系人丙", Duration = (double?)10 },
        };
        var results = new List<object>();
        var failed = 0;
        foreach (var sample in cases)
        {
            using var slots = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:18180/slots"));
            var states = slots.RootElement.ValueKind == JsonValueKind.Array
                ? slots.RootElement.EnumerateArray().ToArray() : [slots.RootElement];
            if (states.Any(s => s.TryGetProperty("is_processing", out var busy) && busy.GetBoolean()))
                throw new InvalidOperationException("Local model is in use; probe stopped without interrupting the active task.");
            ConversationMessageSnapshot[] context = sample.Id == "h4" ? [new("user", "微信中刚才讨论的对象是测试联系人丙。", [])] : [];
            var watch = Stopwatch.StartNew();
            var decision = await router.ClassifyAsync(sample.Text, context);
            var classificationMs = watch.ElapsedMilliseconds;
            ChatFastPathIntent? intent = null;
            if (sample.Action && decision is { Mode: SemanticIntentMode.Operate, Domain: SemanticIntentDomain.Chat })
                intent = await resolver.ResolveAsync(sample.Text, context, false);
            var passed = sample.Action
                ? intent is not null && intent.Recipient == sample.Contact && intent.ContinuousConversationRequested &&
                    intent.CommunicationPlan?.RequestedDuration?.TotalMinutes == sample.Duration && intent.CommunicationGoal == sample.Text
                : decision?.Mode == SemanticIntentMode.Answer;
            if (!passed) failed++;
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {sample.Id}: {decision?.Mode}/{decision?.Domain}, route={classificationMs}ms, slots={watch.ElapsedMilliseconds - classificationMs}ms");
            results.Add(new { sample.Id, passed, routeMilliseconds = classificationMs, totalMilliseconds = watch.ElapsedMilliseconds,
                mode = decision?.Mode.ToString(), domain = decision?.Domain.ToString(),
                contactMatched = intent?.Recipient == sample.Contact, continuous = intent?.ContinuousConversationRequested,
                requestedMinutes = intent?.CommunicationPlan?.RequestedDuration?.TotalMinutes });
        }
        Directory.CreateDirectory(paths.AppState);
        var output = Path.Combine(paths.AppState, "report.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { scope = "routing and semantic slots only; no actual sending", failed, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Capture-only held-out probe: {cases.Length - failed}/{cases.Length}. Report: {output}");
        if (failed > 0) Environment.ExitCode = 1;
    }
}
