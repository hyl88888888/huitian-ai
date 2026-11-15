using System.Diagnostics;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

if (args.Contains("--chat-entry-live")) { await ChatEntryLiveProbe.RunAsync(); return; }

await CommunicationResolverRegression.RunAsync();
if (args.Contains("--communication-contract-only")) return;

await RouterContractRegression.RunAsync();
if (args.Contains("--router-contract-only")) return;

static void Check(bool condition, string detail)
{
    if (!condition) throw new InvalidDataException(detail);
}

var answer = new SemanticIntentDecision(SemanticIntentMode.Answer, SemanticIntentDomain.Office, .9, 0, "test");
Check(SemanticTurnRouting.Decide(answer, false, false) is
    { Route: AgentTaskRoute.DirectAnswer, AllowProductFastPath: false, NeedsComputerContext: false },
    "An explanation mentioning Office must reach the actual zero-tool answer entry.");
Check(SemanticTurnRouting.Decide(answer, true, false).NeedsComputerContext,
    "Answering about a local attachment must retain file-reading capability.");
Check(SemanticTurnRouting.Decide(null, false, false) is
    { Route: AgentTaskRoute.Composite, AllowProductFastPath: false },
    "An inconclusive classifier must not trigger an unrelated fast action.");
Check(!SemanticTurnRouting.Decide(answer with { Confidence = .2 }, false, false).AllowProductFastPath,
    "Low confidence must not authorize deterministic actions.");
Check(SemanticTurnRouting.Decide(answer, false, true).Route == AgentTaskRoute.Composite,
    "Steering must not remove the running task's tools.");
Check(CommunicationIntentResolver.TryParse(
    """{"a":"chat","p":"wechat","r":"小陈","m":20,"v":"了解工作状况，按对方回复交流","i":"同事"}""", out var chat) &&
    chat.CommunicationPlan is { MaximumDuration.TotalMinutes: 20, RequiresReplyObservation: true } && chat.Recipient == "小陈",
    "The semantic slots must reach the bounded continuation executor without parsing user language again.");
Check(!CommunicationIntentResolver.TryParse(
    """{"a":"chat","p":"wechat","r":"unknown","m":20,"v":"交流"}""", out _),
    "An unresolved person must not become a real chat destination.");
Check(CommunicationIntentResolver.TryParse(
    """{"a":"send","p":"qq","r":"小陈","m":0,"v":"A  B? 12:30，不改。"}""", out var literal) &&
    literal.Message == "A  B? 12:30，不改。" && !literal.ContinuousConversationRequested,
    "A dictated message must preserve content rather than being rewritten as a purpose.");

Console.WriteLine("PASS: production semantic entry decisions and structured communication handoff");
if (args.Contains("--steer-upgrade") || args.Contains("--completed-upgrade") || args.Contains("--failed-upgrade"))
{
    // Real App Server, capture-only HTTP backend: no model or computer action.
    using var capture = new System.Net.HttpListener();
    capture.Prefixes.Add("http://127.0.0.1:18293/");
    capture.Start();
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    using (var readinessClient = new HttpClient(new SocketsHttpHandler { UseProxy = false }))
    {
        var ready = readinessClient.GetAsync("http://127.0.0.1:18293/fixture-ready", limit.Token);
        var readyContext = await capture.GetContextAsync().WaitAsync(limit.Token);
        Check(readyContext.Request.RawUrl == "/fixture-ready", "Fixture readiness must reach the actual capture listener.");
        readyContext.Response.StatusCode = 200;
        readyContext.Response.Close();
        using var response = await ready;
        Check(response.IsSuccessStatusCode, "Fixture must be ready before App Server starts.");
    }
    var requestWatch = Stopwatch.StartNew();
    var backend = new AgentBackendConfiguration("huitian-local", "steer_capture", "http://127.0.0.1:18293/v1", false);
    await using var agent = new CodexAppServerClient(new HuiTianPaths(), backend: backend);
    var activities = new System.Collections.Concurrent.ConcurrentQueue<AgentActivity>();
    agent.ActivityReceived += (_, e) => activities.Enqueue(e.Activity);
    var clientFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var writeGate = (SemaphoreSlim)typeof(CodexAppServerClient).GetField("_writeGate", clientFlags)!.GetValue(agent)!;
    var supersededTurns = (System.Collections.Concurrent.ConcurrentDictionary<string, byte>)typeof(CodexAppServerClient)
        .GetField("_supersededTurnIds", clientFlags)!.GetValue(agent)!;
    await agent.StartAsync(limit.Token);
    var original = await agent.CreateThreadAsync(AgentTaskRoute.DirectAnswer, limit.Token);
    await agent.SendMessageAsync(ConversationPolicy.BuildTurnContract("解释备份的作用。", AgentTaskRoute.DirectAnswer), limit.Token);
    var first = await capture.GetContextAsync().WaitAsync(limit.Token);
    Console.WriteLine($"First local App Server request: {requestWatch.ElapsedMilliseconds} ms (fixture readiness verified)");
    using var firstBody = new MemoryStream();
    await first.Request.InputStream.CopyToAsync(firstBody, limit.Token);
    using var firstJson = JsonDocument.Parse(LocalResponsesBridge.NormalizeResponsesRequest(firstBody.ToArray()));
    Check(firstJson.RootElement.GetProperty("tools").GetArrayLength() == 0, "The original answer must be tool-free.");
    var originalTurn = activities.Last(activity => activity.Kind == AgentActivityKind.Status &&
        activity.Id.StartsWith("turn-", StringComparison.Ordinal)).TurnId!;
    int TerminalCount(string turnId) => activities.Count(activity =>
        activity.Kind == AgentActivityKind.Status && activity.TurnId == turnId &&
        activity.Status != AgentActivityStatus.InProgress);
    try
    {
        if (args.Contains("--failed-upgrade"))
        {
            using var failedUpgrade = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            await writeGate.WaitAsync(limit.Token);
            try
            {
                var upgrading = agent.SteerAsync(ConversationPolicy.BuildUniversalTurnContract("生成交接表文件。"), [], failedUpgrade.Token);
                Check(supersededTurns.ContainsKey(originalTurn), "The replacement must be registered before interrupt is sent.");
                failedUpgrade.Cancel();
                try
                {
                    await upgrading;
                    throw new InvalidDataException("A cancelled upgrade must fail visibly to its caller.");
                }
                catch (OperationCanceledException) when (failedUpgrade.IsCancellationRequested) { }
                Check(supersededTurns.IsEmpty && agent.HasActiveTurn,
                    "A failed upgrade must release suppression while retaining the still-running original turn.");
            }
            finally { writeGate.Release(); }
            await agent.InterruptAsync(limit.Token);
            while (agent.HasActiveTurn) await Task.Delay(20, limit.Token);
            Check(TerminalCount(originalTurn) == 1, "A real user stop after an upgrade failure must still reach the UI.");
            Console.WriteLine("PASS failed upgrade: cancellation propagates, suppression is cleared, and a real user stop remains visible");
            return;
        }
        if (args.Contains("--completed-upgrade"))
        {
            await agent.InterruptAsync(limit.Token);
            // Model the race where the answer ends before steering arrives.
            // The interrupt acknowledgement precedes turn/completed.
            while (agent.HasActiveTurn) await Task.Delay(50, limit.Token);
            Check(!agent.HasActiveTurn, "The old answer has not stopped for the completion-race check.");
            Check(TerminalCount(originalTurn) == 1, "A real user stop must retain its terminal UI event.");
        }
        var oldTerminalCount = TerminalCount(originalTurn);
        var upgradeText = ConversationPolicy.BuildUniversalTurnContract("改为生成一个可打印的交接表文件。");
        if (args.Contains("--steer-upgrade"))
        {
            // Hold outgoing traffic to force old completion while the old
            // thread is still selected. The actual App Server then repeats
            // that completion after receiving the queued interrupt.
            await writeGate.WaitAsync(limit.Token);
            Task upgrading;
            try
            {
                upgrading = agent.SteerAsync(upgradeText, [], limit.Token);
                Check(agent.CurrentThreadId == original && supersededTurns.ContainsKey(originalTurn),
                    "The old turn must be superseded before the interrupt or new thread can complete.");
                var notify = typeof(CodexAppServerClient).GetMethod("HandleNotification", clientFlags)!;
                using var completed = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    @params = new { threadId = original, turn = new { id = originalTurn, status = "interrupted" } },
                }));
                notify.Invoke(agent, ["turn/completed", completed.RootElement]);
                Check(TerminalCount(originalTurn) == oldTerminalCount && agent.HasActiveTurn,
                    "Internal answer replacement must not publish a terminal UI event or become idle before its new turn starts.");
            }
            finally { writeGate.Release(); }
            await upgrading;
        }
        else
        {
            await agent.SteerAsync(upgradeText, [], limit.Token);
        }
        Check(supersededTurns.IsEmpty, "A successful upgrade must clear temporary suppression.");
        var second = await capture.GetContextAsync().WaitAsync(limit.Token);
        try
        {
            using var secondBody = new MemoryStream();
            await second.Request.InputStream.CopyToAsync(secondBody, limit.Token);
            using var secondJson = JsonDocument.Parse(LocalResponsesBridge.NormalizeResponsesRequest(secondBody.ToArray()));
            var hasExecutor = secondJson.RootElement.GetProperty("tools").EnumerateArray().Any(x =>
                x.TryGetProperty("name", out var name) && name.GetString() == "huitian_execute");
            Check(agent.CurrentThreadId != original && hasExecutor,
                "Steering into a real deliverable must create a new thread with the actual universal executor.");
            await Task.Delay(150, limit.Token);
            Check(agent.HasActiveTurn, "An old interrupted event cleared the new active turn.");
            Check(TerminalCount(originalTurn) == oldTerminalCount,
                "An old interrupted event must never close the newly submitted UI requirement.");
            var newTurn = activities.Last(activity => activity.Kind == AgentActivityKind.Status &&
                activity.Id.StartsWith("turn-", StringComparison.Ordinal)).TurnId!;
            await agent.InterruptAsync(limit.Token);
            while (agent.HasActiveTurn) await Task.Delay(20, limit.Token);
            Check(TerminalCount(newTurn) == 1, "A real user stop of the replacement task must remain visible.");
            Console.WriteLine("PASS live steering: real executor retained; superseded terminal cannot close the new UI requirement; real user stop preserved");
        }
        finally { second.Response.Abort(); }
    }
    finally { first.Response.Abort(); }
    return;
}
if (args.Contains("--direct-answer"))
{
    var paths = new HuiTianPaths();
    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    // Optional headless, owned core: useful when launching the desktop app
    // is unavailable. Never start a duplicate resident model.
    await using var ownedCore = args.Contains("--start-local-core") ? new LocalBrainServer(paths) : null;
    if (ownedCore is not null)
    {
        Check(Process.GetProcessesByName("HuiTian.App").Length == 0 &&
              Process.GetProcessesByName("llama-server").Length == 0,
            "An existing app or model must not be duplicated by this isolated test.");
        using var downloader = new SegmentedDownloadEngine();
        var installation = new LocalBrainInstaller(paths, downloader).GetInstalled(CapabilityTierId.Huang);
        await ownedCore.StartAsync(installation, budget.Token);
    }
    await using var bridge = new LocalResponsesBridge(paths, new Uri("http://127.0.0.1:18180/v1/"), 18282);
    await bridge.StartAsync(budget.Token);
    var backend = new AgentBackendConfiguration("huitian-local", "semantic_entry_smoke", "http://127.0.0.1:18282/v1", IsProductDefault: true);
    await using var agent = new CodexAppServerClient(paths, backend: backend);
    using var routeModel = new SemanticIntentRouter(backend, paths);
    const string request = "解释一下什么叫机会成本，两句话就行，别用术语。";
    var clock = Stopwatch.StartNew();
    var resolution = await routeModel.ClassifyAsync(request, [], budget.Token);
    var route = SemanticTurnRouting.Decide(resolution, false, false).Route;
    Check(route == AgentTaskRoute.DirectAnswer, "Live ordinary question did not reach the direct answer entry.");
    var intentMs = clock.ElapsedMilliseconds;
    var tools = 0;
    long? firstTextMs = null;
    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var terminal = new TaskCompletionSource<AgentActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
    agent.ActivityReceived += (_, e) =>
    {
        if (e.Activity.Kind == AgentActivityKind.Tool) Interlocked.Increment(ref tools);
        if (e.Activity.Kind == AgentActivityKind.AssistantMessage && !string.IsNullOrWhiteSpace(e.Activity.Detail))
        {
            firstTextMs ??= clock.ElapsedMilliseconds;
            if (e.Activity.Status == AgentActivityStatus.Completed) completion.TrySetResult(e.Activity.Detail);
        }
        if (e.Activity.Kind == AgentActivityKind.Error) completion.TrySetException(new InvalidOperationException(e.Activity.Detail));
        if (e.Activity.Kind == AgentActivityKind.Status && e.Activity.Id.StartsWith("turn-", StringComparison.Ordinal) &&
            e.Activity.Status != AgentActivityStatus.InProgress) terminal.TrySetResult(e.Activity);
    };
    await agent.StartAsync(budget.Token);
    await agent.CreateThreadAsync(route, budget.Token);
    await agent.SendMessageAsync(ConversationPolicy.BuildTurnContract(request, route), budget.Token);
    var text = await completion.Task.WaitAsync(budget.Token);
    Check(tools == 0 && !string.IsNullOrWhiteSpace(text), "Ordinary answer must be visible and must not perform a tool action.");
    var outcome = await terminal.Task.WaitAsync(budget.Token);
    Check(outcome.Status == AgentActivityStatus.Completed,
        "A fully delivered answer must pass its conversational deliverable verification, not remain pending or failed.");
    var report = Path.Combine(paths.AppState, $"direct-answer-live-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { intentMs, firstTextMs, totalMs = clock.ElapsedMilliseconds, tools,
        verified = true, outcome = outcome.Title, text }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"PASS direct-answer intent={intentMs} first-text={firstTextMs} total={clock.ElapsedMilliseconds} tools={tools} report={report}");
    return;
}
if (args.Contains("--heldout"))
{
    // Independent cases authored by the audit worker, not production rules.
    // This first evaluation is the holdout; later runs are regressions.
    using var heldoutRouter = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, new HuiTianPaths());
    using var suite = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Combine("tests", "HuiTian.SemanticRoutingTests", "unseen-routing.json")));
    var outcomes = new List<object>();
    var durations = new List<long>();
    var correct = 0;
    foreach (var sample in suite.RootElement.EnumerateArray())
    {
        var history = sample.GetProperty("priorMessages").EnumerateArray().Select(x =>
            new ConversationMessageSnapshot(x.GetProperty("role").GetString()!, x.GetProperty("text").GetString()!, [])).ToArray();
        var timer = Stopwatch.StartNew();
        var result = await heldoutRouter.ClassifyAsync(sample.GetProperty("text").GetString()!, history);
        durations.Add(timer.ElapsedMilliseconds);
        var modes = sample.GetProperty("expectedModes").EnumerateArray().Select(x => x.GetString()).ToArray();
        var domains = sample.GetProperty("expectedDomains").EnumerateArray().Select(x => x.GetString()).ToArray();
        var passed = result is not null && modes.Contains(result.Mode.ToString()) &&
                     (domains.Length == 0 || domains.Contains(result.Domain.ToString()));
        if (passed) correct++;
        var id = sample.GetProperty("id").GetString();
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {id}: {result?.Mode}/{result?.Domain} {timer.ElapsedMilliseconds}ms");
        outcomes.Add(new { id, passed, milliseconds = timer.ElapsedMilliseconds, mode = result?.Mode.ToString(), domain = result?.Domain.ToString() });
    }
    durations.Sort();
    var report = Path.Combine(new HuiTianPaths().AppState, $"semantic-holdout-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        total = outcomes.Count, passed = correct, p50 = durations[(durations.Count - 1) / 2],
        p95 = durations[(int)Math.Ceiling(durations.Count * .95) - 1], outcomes,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"HOLDOUT {correct}/{outcomes.Count}: {report}");
    Check(correct == outcomes.Count, "Independent holdout has failures; do not claim complete generalization.");
    return;
}
if (!args.Contains("--live")) return;

// Authored separately from the production prompts. Once run these become a
// fixed regression set; do not claim they remain unseen after tuning on them.
var samples = new (string Id, string Text, bool Action, ConversationMessageSnapshot[] Context)[]
{
    ("answer-1", "你刚才说得太绕了，我就想听个通俗解释。", false, []),
    ("answer-2", "Excel 的透视表到底有什么用？先给我讲明白。", false, []),
    ("answer-3", "下雨天有点提不起劲，陪我说两句吧。", false, []),
    ("answer-4", "给客户写一句周末问候，文字放这儿我自己复制。", false, []),
    ("answer-5", "这段开头不够抓人，重新给我三个版本，别创建文件。", false, [new("assistant", "欢迎来到今天的直播。", [])]),
    ("action-1", "和微信里的胖虎聊天  聊20分钟", true, []),
    ("action-2", "那个绿色聊天软件切过来，我看看刚谁找我。", true, []),
    ("action-3", "帮我把这堆表弄成能上台讲的几页演示。", true, []),
    ("action-4", "这资料给我排成能拿去印的练习册，答题的空地方多留点。", true, []),
    ("action-5", "桌面堆的这些票据按月份归到一起。", true, []),
    ("action-6", "把刚才做好的那份递给她吧。", true, [new("user", "微信里的王老师需要月报。", []),new("assistant", "月报已保存在 D:\\HuiTian\\UserContent\\月报.xlsx。", [])]),
    ("action-7", "就陪他唠二十分钟，看看他最近过得咋样。", true, [new("user", "我要用微信联系小陈。", []),new("assistant", "当前沟通对象是小陈。", [])]),
    ("action-8", "我今晚想追《山海情》，能看的页面给我放起来。", true, []),
    ("action-9", "比照刚才那份格式再做个下月的，完了给群里一份。", true, [new("assistant", "本月预算表已保存在本地。", [])]),
    ("answer-6", "我不是叫你真的联系他，只要教我怎样说比较合适。", false, [new("user", "微信中有同事小周。", [])]),
    ("answer-7", "别人说把文件发给我，这句话英文怎么说？", false, []),
};
using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, new HuiTianPaths());
var results = new List<object>();
var passes = 0;
foreach (var sample in samples)
{
    var watch = Stopwatch.StartNew();
    var decision = await router.ClassifyAsync(sample.Text, sample.Context);
    var route = SemanticTurnRouting.Decide(decision, false, false);
    var actualAction = route.Route != AgentTaskRoute.DirectAnswer;
    var passed = decision is not null && actualAction == sample.Action;
    if (passed) passes++;
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {sample.Id}: {decision?.Mode}/{decision?.Domain} {watch.ElapsedMilliseconds}ms");
    results.Add(new { id = sample.Id, passed, milliseconds = watch.ElapsedMilliseconds, mode = decision?.Mode.ToString(), domain = decision?.Domain.ToString() });
}
using var resolver = new CommunicationIntentResolver(AgentBackendConfiguration.ProductDefault);
var omitted = samples.Single(x => x.Id == "action-7");
var extraction = Stopwatch.StartNew();
var resolved = await resolver.ResolveAsync(omitted.Text, omitted.Context, false);
var slotsPassed = resolved is { Recipient: "小陈", Platform: ChatFastPathPlatform.WeChat } &&
                  resolved.CommunicationPlan is { MaximumDuration.TotalMinutes: 20, RequiresReplyObservation: true };
Console.WriteLine($"{(slotsPassed ? "PASS" : "FAIL")} omitted-slots: {extraction.ElapsedMilliseconds}ms");
var reportPath = Path.Combine(new HuiTianPaths().AppState, $"semantic-entry-eval-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { samples = samples.Length, passed = passes, slotsPassed, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"LIVE: {passes}/{samples.Length}, slots={slotsPassed}, report={reportPath}");
Check(passes == samples.Length && slotsPassed, "Live entry regression failed; inspect measured results before release.");
