using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

var seen = BuildSeenSet();
Assert(IntentUnderstandingEngine.TryParseResolution(
    """{"t":"ct","d":"o","g":"修改已有演示稿","c":true,"a":["modify"],"p":[],"v":[{"k":"modified","x":"修改副本","f":"pptx","id":"edited"}],"r":"r","l":false,"cf":0.9}""",
    1, "artifact-identity-test", out var identityContract) && identityContract.Deliverables[0].ArtifactKey == "edited",
    "semantic contract must retain explicit artifact identity");
var unseen = BuildUnseenSet();
var deliveryUnseen = BuildUnseenDeliverySet();
Assert(seen.Count >= 20, "Seen Set must retain representative development checks.");
Assert(unseen.Count >= 200, "Unseen Set must contain at least 200 held-out expressions.");
Assert(
    deliveryUnseen.Count >= 36,
    "Delivery Unseen Set must contain at least 36 long-form outcome checks.");
Assert(
    unseen.Select(item => item.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count() == unseen.Count,
    "Unseen Set expressions must be unique.");
Assert(
    deliveryUnseen.Select(item => item.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count() ==
    deliveryUnseen.Count,
    "Delivery Unseen Set expressions must be unique.");
Assert(
    !seen.Select(item => item.Text).Intersect(
        unseen.Select(item => item.Text),
        StringComparer.OrdinalIgnoreCase).Any(),
    "Seen and Unseen sets must be disjoint.");
Assert(
    !seen.Select(item => item.Text).Concat(unseen.Select(item => item.Text)).Intersect(
        deliveryUnseen.Select(item => item.Text),
        StringComparer.OrdinalIgnoreCase).Any(),
    "Delivery Unseen Set must remain disjoint from the existing corpora.");
Assert(
    unseen.SelectMany(item => item.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 15,
    "Unseen Set must cover colloquial, typo, ASR, ellipsis, context and multi-task cases.");
Assert(
    deliveryUnseen.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 6,
    "Delivery Unseen Set must cover documents, sheets, presentations, modifications, sends and answers.");
Assert(
    deliveryUnseen.All(item =>
        !item.Text.Contains("教师资格", StringComparison.OrdinalIgnoreCase) &&
        !item.Text.Contains("科目三", StringComparison.OrdinalIgnoreCase)),
    "The reported example must not leak into held-out delivery tests.");

Assert(
    IntentUnderstandingEngine.TryParseResolution(
        """
        {
          "task_type":"multi_app_task",
          "domain":"multi_app",
          "goal":"produce a verified workbook and deliver it to the intended workspace",
          "requires_computer":true,
          "actions":["build_workbook","deliver"],
          "applications":["spreadsheet","communication"],
          "deliverables":[
            {"kind":"spreadsheet","description":"editable calculated workbook","format":"xlsx","destination":"project folder"},
            {"kind":"external_delivery","description":"the verified workbook is present for the intended recipient","format":null,"destination":"collaboration workspace"}
          ],
          "success_conditions":[
            {"kind":"artifact","description":"the workbook exists and can be reopened"},
            {"kind":"external_state","description":"the same workbook is visible at the intended destination"}
          ],
          "plan":[
            {"id":"step-1","title":"build result","action":"build_workbook","app":"spreadsheet","target":"workbook","depends_on":[],"success_conditions":[{"kind":"artifact","description":"workbook verified"}],"risk":"reversible","estimated_latency_ms":1000},
            {"id":"step-2","title":"deliver result","action":"deliver","app":"communication","target":"recipient","depends_on":["step-1"],"success_conditions":[{"kind":"external_state","description":"delivery verified"}],"risk":"external_effect","estimated_latency_ms":1000}
          ],
          "risk":"external_effect",
          "clarification":{"required":false,"reason":null,"question":null},
          "confidence":0.94
        }
        """,
        8,
        "delivery-contract-test",
        out var parsedDeliveryContract) &&
    parsedDeliveryContract.Deliverables.Select(item => item.Kind).SequenceEqual(
        [IntentDeliverableKind.Spreadsheet, IntentDeliverableKind.ExternalDelivery]) &&
    parsedDeliveryContract.Deliverables[0].Format == "xlsx" &&
    parsedDeliveryContract.Deliverables[1].Destination == "collaboration workspace" &&
    parsedDeliveryContract.SuccessConditions.Select(item => item.Kind).SequenceEqual(
        [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
    "structured semantic output must retain multiple final deliverables separately from actions");

Assert(
    IntentUnderstandingEngine.TryParseResolution(
        """
        {"t":"m","d":"u","g":"创建表格并发送","c":true,"a":["create","send"],"p":["Excel","微信"],"v":[{"k":"spreadsheet","x":"可编辑表格","f":"xlsx","z":null},{"k":"send","x":"表格送达","f":"xlsx","z":"联系人"}],"s":[{"k":"art","x":"表格可重开"},{"k":"ext","x":"同一文件已送达"}],"q":[{"i":"1","x":"创建表格","a":"create","p":"Excel","o":"workbook","u":[],"s":[{"k":"art","x":"可重开"}],"r":"r"},{"i":"2","x":"发送表格","a":"send","p":"微信","o":"联系人","u":["1"],"s":[{"k":"ext","x":"已送达"}],"r":"e"}],"r":"e","l":false,"cf":0.93}
        """,
        12,
        "compact-semantic-contract-test",
        out var compactContract) &&
    compactContract.TaskType == SemanticTaskType.MultiAppTask &&
    compactContract.Domain == IntentExecutionDomain.MultiApp &&
    compactContract.Deliverables.Select(item => item.Kind).SequenceEqual(
        [IntentDeliverableKind.Spreadsheet, IntentDeliverableKind.ExternalDelivery]) &&
    compactContract.SuccessConditions.Select(item => item.Kind).SequenceEqual(
        [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]) &&
    compactContract.Plan.Count == 2,
    "the compact low-latency semantic contract must preserve deliverables, evidence and dependencies");

var deliverableOnlyIntent = new IntentResolution(
    SemanticTaskType.ComputerTask,
    IntentExecutionDomain.Office,
    "produce an editable formal document",
    requiresComputer: true,
    actions: ["compose"],
    deliverables:
    [
        new IntentDeliverableRequirement(
            IntentDeliverableKind.Document,
            "editable formal document",
            "docx"),
    ]);
Assert(
    deliverableOnlyIntent.ToComputerTaskContract().CompletionEvidence.Any(item =>
        item.Kind == CompletionEvidenceKind.Artifact),
    "a file deliverable must compile to artifact evidence even when no separate success condition was supplied");

var contradictoryAnswerIntent = new IntentResolution(
    SemanticTaskType.Answer,
    IntentExecutionDomain.Answer,
    "analyze the supplied text only",
    requiresComputer: true,
    deliverables:
    [
        new IntentDeliverableRequirement(
            IntentDeliverableKind.ConversationalAnswer,
            "analysis shown in the conversation"),
    ]);
Assert(
    !contradictoryAnswerIntent.RequiresComputer,
    "an Answer contract must not launch computer actions merely because a local model emitted a contradictory boolean");

var runLiveIntent = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_LIVE_INTENT_EVAL"),
    "1",
    StringComparison.Ordinal);
var runLiveDelivery = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_LIVE_DELIVERY_EVAL"),
    "1",
    StringComparison.Ordinal);

if (!runLiveIntent && !runLiveDelivery)
{
    Console.WriteLine(
        $"PASS: intent corpus integrity; Seen={seen.Count}, Unseen={unseen.Count}, " +
        $"DeliveryUnseen={deliveryUnseen.Count}. Set HUITIAN_LIVE_INTENT_EVAL=1 or " +
        "HUITIAN_LIVE_DELIVERY_EVAL=1 to evaluate the running local brain.");
    return;
}

var maximum = int.TryParse(
    Environment.GetEnvironmentVariable("HUITIAN_LIVE_INTENT_MAX"),
    out var parsedMaximum)
    ? Math.Clamp(parsedMaximum, 1, unseen.Count)
    : unseen.Count;
var paths = new HuiTianPaths();
await using var router = new LocalIntentRouterServer(paths);
var useMainBrain = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_LIVE_DELIVERY_USE_MAIN_BRAIN"),
    "1",
    StringComparison.Ordinal);
var routerReady = useMainBrain || await router.StartIfInstalledAsync();
Assert(routerReady, "The local semantic router could not be started.");
using var engine = new IntentUnderstandingEngine(
    AgentBackendConfiguration.ProductDefault,
    paths,
    useMainBrain
        ? new Uri("http://127.0.0.1:18180/v1/")
        : router.ResponsesBaseUri);

if (runLiveDelivery)
{
    var deliveryMaximum = int.TryParse(
        Environment.GetEnvironmentVariable("HUITIAN_LIVE_DELIVERY_MAX"),
        out var parsedDeliveryMaximum)
        ? Math.Clamp(parsedDeliveryMaximum, 1, deliveryUnseen.Count)
        : deliveryUnseen.Count;
    var deliveryEvaluations = new List<DeliveryEvaluation>();
    foreach (var test in SelectBalancedDelivery(deliveryUnseen, deliveryMaximum))
    {
        var resolution = await engine.ResolveAsync(
            test.Text,
            IntentContextSnapshot.Empty,
            AgentTaskRoute.DirectAnswer,
            hasAttachments: test.HasAttachments);
        var evidence = resolution.SuccessConditions.Select(item => item.Kind).ToHashSet();
        var deliverables = resolution.Deliverables.Select(item => item.Kind).ToHashSet();
        var inconclusive = IsInconclusive(resolution.Source);
        var passed = !inconclusive &&
                     resolution.RequiresComputer == test.RequiresComputer &&
                     test.AcceptedDomains.Contains(resolution.Domain) &&
                     test.AcceptedTaskTypes.Contains(resolution.TaskType) &&
                     test.RequiredDeliverables.All(deliverables.Contains) &&
                     (!test.RequiresArtifactDeliverable || deliverables.Any(IsArtifactDeliverable)) &&
                     test.RequiredEvidence.All(evidence.Contains) &&
                     test.ForbiddenEvidence.All(kind => !evidence.Contains(kind)) &&
                     resolution.Plan.Count >= test.MinimumPlanSteps;
        deliveryEvaluations.Add(new DeliveryEvaluation(
            test.Id,
            test.Category,
            passed,
            resolution.TaskType,
            resolution.Domain,
            resolution.RequiresComputer,
            resolution.Deliverables.Select(item => item.Kind).ToArray(),
            resolution.SuccessConditions.Select(item => item.Kind).ToArray(),
            resolution.Plan.Count,
            resolution.ElapsedMilliseconds,
            resolution.Source,
            inconclusive));
        Console.WriteLine(
            $"{(inconclusive ? "INCONCLUSIVE" : passed ? "PASS" : "FAIL")} {test.Id}/{test.Category}: " +
            $"{resolution.TaskType}/{resolution.Domain}/computer={resolution.RequiresComputer}/" +
            $"deliverables=[{string.Join(',', deliverables)}]/" +
            $"evidence=[{string.Join(',', evidence)}]/plan={resolution.Plan.Count}/" +
            $"{resolution.ElapsedMilliseconds}ms/{resolution.Source}");
    }

    var conclusiveDelivery = deliveryEvaluations.Where(item => !item.Inconclusive).ToArray();
    var deliverySuccessRate = conclusiveDelivery.Length == 0
        ? 0
        : conclusiveDelivery.Count(item => item.Passed) / (double)conclusiveDelivery.Length;
    var deliveryPath = Path.Combine(
        paths.AppState,
        $"delivery-intent-eval-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    Directory.CreateDirectory(Path.GetDirectoryName(deliveryPath)!);
    await File.WriteAllTextAsync(
        deliveryPath,
        JsonSerializer.Serialize(new
        {
            at = DateTimeOffset.Now,
            set = "DeliveryUnseen",
            samples = deliveryEvaluations.Count,
            conclusiveSamples = conclusiveDelivery.Length,
            inconclusiveSamples = deliveryEvaluations.Count - conclusiveDelivery.Length,
            successRate = deliverySuccessRate,
            results = deliveryEvaluations,
        }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(
        $"LIVE DELIVERY: Unseen={deliveryEvaluations.Count}, conclusive={conclusiveDelivery.Length}, " +
        $"accuracy={deliverySuccessRate:P1}, report={deliveryPath}");
    if (conclusiveDelivery.Length == 0)
    {
        throw new InvalidDataException(
            "Delivery evaluation was inconclusive: the semantic service returned no completed resolutions.");
    }
    if (deliverySuccessRate < 0.75)
    {
        throw new InvalidDataException(
            $"Held-out delivery accuracy {deliverySuccessRate:P1} is below the current 75% gate.");
    }
}

if (!runLiveIntent)
{
    return;
}

var evaluations = new List<Evaluation>();
foreach (var test in SelectBalanced(unseen, maximum))
{
    var context = test.Context is null
        ? IntentContextSnapshot.Empty
        : new IntentContextSnapshot(
            test.Context,
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            new Dictionary<string, string>());
    var started = DateTimeOffset.Now;
    var resolution = await engine.ResolveAsync(
        test.Text,
        context,
        AgentTaskRoute.DirectAnswer,
        hasAttachments: test.HasAttachments);
    var inconclusive = IsInconclusive(resolution.Source);
    var passed = !inconclusive &&
                 resolution.RequiresComputer == test.RequiresComputer &&
                 test.AcceptedDomains.Contains(resolution.Domain) &&
                 test.AcceptedTaskTypes.Contains(resolution.TaskType);
    evaluations.Add(new Evaluation(
        test.Id,
        passed,
        resolution.TaskType,
        resolution.Domain,
        resolution.RequiresComputer,
        resolution.Confidence,
        resolution.ElapsedMilliseconds,
        resolution.Source,
        inconclusive,
        test.Tags));
    Console.WriteLine(
        $"{(inconclusive ? "INCONCLUSIVE" : passed ? "PASS" : "FAIL")} {test.Id}: " +
        $"{resolution.TaskType}/{resolution.Domain}/computer={resolution.RequiresComputer}/" +
        $"{resolution.ElapsedMilliseconds}ms/{resolution.Source}");
}

var conclusiveEvaluations = evaluations.Where(item => !item.Inconclusive).ToArray();
var successRate = conclusiveEvaluations.Length == 0
    ? 0
    : conclusiveEvaluations.Count(item => item.Passed) / (double)conclusiveEvaluations.Length;
var evaluationPath = Path.Combine(
    paths.AppState,
    $"intent-eval-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
Directory.CreateDirectory(Path.GetDirectoryName(evaluationPath)!);
await File.WriteAllTextAsync(
    evaluationPath,
    JsonSerializer.Serialize(new
    {
        at = DateTimeOffset.Now,
        set = "Unseen",
        samples = evaluations.Count,
        conclusiveSamples = conclusiveEvaluations.Length,
        inconclusiveSamples = evaluations.Count - conclusiveEvaluations.Length,
        successRate,
        p50Milliseconds = Percentile(evaluations.Select(item => item.ElapsedMilliseconds), 0.50),
        p95Milliseconds = Percentile(evaluations.Select(item => item.ElapsedMilliseconds), 0.95),
        results = evaluations,
    }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(
    $"LIVE: Unseen={evaluations.Count}, conclusive={conclusiveEvaluations.Length}, accuracy={successRate:P1}, " +
    $"report={evaluationPath}");
if (conclusiveEvaluations.Length == 0)
{
    throw new InvalidDataException(
        "Semantic evaluation was inconclusive: the semantic service returned no completed resolutions.");
}
if (successRate < 0.75)
{
    throw new InvalidDataException(
        $"Held-out semantic accuracy {successRate:P1} is below the current 75% gate.");
}

static IReadOnlyList<IntentCase> BuildSeenSet() =>
[
    Case("seen-001", "启动指定的桌面程序", true, [IntentExecutionDomain.Other], WorkTypes(), "direct"),
    Case("seen-002", "解释一个财务概念", false, [IntentExecutionDomain.Answer], AnswerTypes(), "answer"),
    Case("seen-003", "按给定材料创建演示文件", true, [IntentExecutionDomain.Office], WorkTypes(), "office"),
    Case("seen-004", "查公开资料后在对话里总结", false, [IntentExecutionDomain.Research], HybridAnswerTypes(), "research"),
    Case("seen-005", "修改当前文档并保存", true, [IntentExecutionDomain.File, IntentExecutionDomain.Office], WorkTypes(), "file"),
    Case("seen-006", "在通讯软件中把成品交给指定对象", true, [IntentExecutionDomain.Communication], WorkTypes(), "communication"),
    Case("seen-007", "评价上一段回答的语气", false, [IntentExecutionDomain.Answer], AnswerTypes(), "feedback"),
    Case("seen-008", "从网页读取资料并填写本地表格", true, [IntentExecutionDomain.MultiApp, IntentExecutionDomain.Browser], MultiTypes(), "multi"),
    Case("seen-009", "让当前媒体停止播放", true, [IntentExecutionDomain.Media, IntentExecutionDomain.System], WorkTypes(), "media"),
    Case("seen-010", "说明怎样在表格里求平均值", false, [IntentExecutionDomain.Answer, IntentExecutionDomain.Office], AnswerTypes(), "howto"),
    Case("seen-011", "把已打开表格的平均值算出来", true, [IntentExecutionDomain.Office, IntentExecutionDomain.File], WorkTypes(), "context"),
    Case("seen-012", "检查代码项目并让它成功运行", true, [IntentExecutionDomain.Development, IntentExecutionDomain.MultiApp], WorkTypes(), "development"),
    Case("seen-013", "只改写一段可复制文字", false, [IntentExecutionDomain.Answer], CreateAnswerTypes(), "create"),
    Case("seen-014", "把附件转成另一种文件格式", true, [IntentExecutionDomain.File], WorkTypes(), "attachment", hasAttachments: true),
    Case("seen-015", "调整电脑的输出音量", true, [IntentExecutionDomain.System, IntentExecutionDomain.Media], WorkTypes(), "system"),
    Case("seen-016", "分析合同并把发现的问题直接改掉", true, [IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp], MultiTypes(), "hybrid"),
    Case("seen-017", "寻找今天新下载的文件", true, [IntentExecutionDomain.File], WorkTypes(), "search"),
    Case("seen-018", "打开网页并把表单准备好", true, [IntentExecutionDomain.Browser], WorkTypes(), "browser"),
    Case("seen-019", "说说你对这件事的看法", false, [IntentExecutionDomain.Answer], AnswerTypes(), "opinion"),
    Case("seen-020", "读取数据、生成图表，再交付到外部会话", true, [IntentExecutionDomain.MultiApp], MultiTypes(), "multi"),
];

static IReadOnlyList<IntentCase> BuildUnseenSet()
{
    // Hand-authored held-out cases cover phenomena that mechanical paraphrase
    // generators usually miss. They never appear in product routing code.
    var manual = new List<IntentCase>
    {
        Case("unseen-manual-001", "别跟我讲教程，眼前这份东西你收拾利索就行", true, [IntentExecutionDomain.File, IntentExecutionDomain.Office, IntentExecutionDomain.Other], WorkTypes(), "colloquial", "result-only"),
        Case("unseen-manual-002", "刚那个给他也弄一份", true, [IntentExecutionDomain.Communication, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp], WorkTypes(), "ellipsis", "pronoun", context: Context("user", "已完成季度报告并打开了与项目负责人的会话")),
        Case("unseen-manual-003", "这句话听起来冷冰冰的，你懂我意思吧", false, [IntentExecutionDomain.Answer], AnswerTypes(), "emotion", "implicit-feedback"),
        Case("unseen-manual-004", "把屏幕里那坨数字捋顺，重的别留", true, [IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.Other], WorkTypes(), "colloquial", "typo"),
        Case("unseen-manual-005", "去通讯那边瞅瞅刚是谁找我", true, [IntentExecutionDomain.Communication], WorkTypes(), "dialect", "no-app-name"),
        Case("unseen-manual-006", "我不想自己点，附件上写啥网页里就照着办", true, [IntentExecutionDomain.Browser, IntentExecutionDomain.MultiApp], MultiTypes(), "result-only", "attachment", hasAttachments: true),
        Case("unseen-manual-007", "先别交出去，改成便携文档之后再说", true, [IntentExecutionDomain.File, IntentExecutionDomain.Office], WorkTypes(), "mid-course-change", context: Context("user", "准备发送当前演示文件")),
        Case("unseen-manual-008", "上一个继续，从断的地方来", true, [IntentExecutionDomain.Other, IntentExecutionDomain.MultiApp, IntentExecutionDomain.File], WorkTypes(), "resume", "ellipsis", context: Context("assistant", "文件转换在第二步暂停")),
        Case("unseen-manual-009", "这报错是想表达啥，不用碰我的项目", false, [IntentExecutionDomain.Answer, IntentExecutionDomain.Development], AnswerTypes(), "negative-action", "development"),
        Case("unseen-manual-010", "给我整点能看的，别光列片名", true, [IntentExecutionDomain.Media, IntentExecutionDomain.Browser], WorkTypes(), "casual", "result-only"),
        Case("unseen-manual-011", "音象有点大，往下搁两格", true, [IntentExecutionDomain.System, IntentExecutionDomain.Media], WorkTypes(), "asr-error", "dialect"),
        Case("unseen-manual-012", "那两页别动格式，只换成新数", true, [IntentExecutionDomain.Office, IntentExecutionDomain.File], WorkTypes(), "context", "constraint", context: Context("user", "当前打开的是预算演示文稿")),
        Case("unseen-manual-013", "能撤回的你就直接干，涉及钱的停住喊我", true, [IntentExecutionDomain.System, IntentExecutionDomain.Other], WorkTypes(), "preference", "risk"),
        Case("unseen-manual-014", "不是让你告诉我怎么弄，是要你给我弄完", true, [IntentExecutionDomain.Other, IntentExecutionDomain.File, IntentExecutionDomain.Office], WorkTypes(), "correction", "intent-boundary"),
        Case("unseen-manual-015", "这段挺好，不过不像我平常说话，再自然点", false, [IntentExecutionDomain.Answer], CreateAnswerTypes(), "feedback", "style"),
        Case("unseen-manual-016", "上午落下来的那份找着，瘦身后丢到当前窗口", true, [IntentExecutionDomain.MultiApp, IntentExecutionDomain.File, IntentExecutionDomain.Communication], MultiTypes(), "multi", "no-app-name"),
        Case("unseen-manual-017", "销售里退了的单独拿出来，原表别碰", true, [IntentExecutionDomain.Office, IntentExecutionDomain.File], WorkTypes(), "ellipsis", "constraint"),
        Case("unseen-manual-018", "浏览器现在这页的数字跟附件核一下，有差的标出来", true, [IntentExecutionDomain.MultiApp, IntentExecutionDomain.Browser, IntentExecutionDomain.File], MultiTypes(), "hybrid", "attachment", hasAttachments: true),
        Case("unseen-manual-019", "程序跑不起来你自己看错误接着修，别卡第一下", true, [IntentExecutionDomain.Development, IntentExecutionDomain.MultiApp], WorkTypes(), "recovery", "development"),
        Case("unseen-manual-020", "我就是吐槽一下刚才太慢，没让你重做", false, [IntentExecutionDomain.Answer], AnswerTypes(), "feedback", "negative-action"),
    };

    // Automatically generated held-out paraphrases combine semantic seeds with
    // discourse forms. These strings are test data only and never participate
    // in routing or prompt construction.
    var seeds = new[]
    {
        Seed("launch", "把{0}给我调到前面来", new[] { "日历程序", "图片查看器", "终端窗口" }, true, Domains(IntentExecutionDomain.Other, IntentExecutionDomain.System), WorkTypes(), "app"),
        Seed("file-clean", "{0}里的重复项清干净并保留原格式", new[] { "当前表格", "下载目录那份清单", "附件数据" }, true, Domains(IntentExecutionDomain.File, IntentExecutionDomain.Office), WorkTypes(), "file"),
        Seed("find", "替我找出{0}", new[] { "昨天改过的最大视频", "本周新增的合同", "最近保存的截图" }, true, Domains(IntentExecutionDomain.File), WorkTypes(), "search"),
        Seed("write", "围绕{0}写一段能直接复制的内容", new[] { "新品上市", "活动延期", "客户致谢" }, false, Domains(IntentExecutionDomain.Answer), CreateAnswerTypes(), "create"),
        Seed("office", "把{0}整理成正式演示文件", new[] { "这些会议笔记", "附件里的调研", "当前网页资料" }, true, Domains(IntentExecutionDomain.Office, IntentExecutionDomain.MultiApp), WorkTypes(), "office"),
        Seed("browser", "去网页里把{0}办好", new[] { "资料下载", "现有表单", "后台查询" }, true, Domains(IntentExecutionDomain.Browser), WorkTypes(), "browser"),
        Seed("communication", "让{0}看到刚完成的结果", new[] { "项目联系人", "当前会话对方", "协作群" }, true, Domains(IntentExecutionDomain.Communication, IntentExecutionDomain.MultiApp), WorkTypes(), "communication"),
        Seed("media", "我想看{0}，直接给我弄到能播放", new[] { "自然纪录片", "公开课程", "一段轻音乐" }, true, Domains(IntentExecutionDomain.Media, IntentExecutionDomain.Browser), WorkTypes(), "media"),
        Seed("system", "把电脑的{0}调整一下", new[] { "声音输出", "显示亮度", "无线连接" }, true, Domains(IntentExecutionDomain.System, IntentExecutionDomain.Media), WorkTypes(), "system"),
        Seed("answer", "{0}到底是什么意思", new[] { "这句提示", "这个概念", "上一段话" }, false, Domains(IntentExecutionDomain.Answer), AnswerTypes(), "answer"),
        Seed("hybrid", "检查{0}的问题，发现以后直接修好", new[] { "当前合同", "这份代码", "附件报告" }, true, Domains(IntentExecutionDomain.MultiApp, IntentExecutionDomain.File, IntentExecutionDomain.Office, IntentExecutionDomain.Development), MultiTypes(), "hybrid"),
        Seed("multi", "把{0}分析完做成图，再交给当前协作对象", new[] { "月度数据", "订单清单", "投放明细" }, true, Domains(IntentExecutionDomain.MultiApp), MultiTypes(), "multi"),
        Seed("convert", "将{0}转成方便传输的格式", new[] { "这批图片", "昨天的视频", "当前文档" }, true, Domains(IntentExecutionDomain.File), WorkTypes(), "convert"),
        Seed("develop", "把{0}检查明白并运行起来", new[] { "这个仓库", "附件项目", "当前代码目录" }, true, Domains(IntentExecutionDomain.Development, IntentExecutionDomain.MultiApp), WorkTypes(), "development"),
        Seed("research", "查清{0}再给我结论", new[] { "今天的公开信息", "现行规则", "近期价格" }, false, Domains(IntentExecutionDomain.Research), HybridAnswerTypes(), "research"),
    };
    var wrappers = new Func<string, string>[]
    {
        value => value,
        value => "麻烦你，" + value,
        value => "别只说步骤，" + value,
        value => value + "，弄完告诉我结果",
        value => "这事交给你了：" + value,
    };
    var generated = new List<IntentCase>();
    var sequence = 0;
    foreach (var seed in seeds)
    {
        foreach (var argument in seed.Arguments)
        {
            foreach (var wrapper in wrappers)
            {
                sequence++;
                generated.Add(Case(
                    $"unseen-auto-{sequence:000}",
                    wrapper(string.Format(seed.Template, argument)),
                    seed.RequiresComputer,
                    seed.Domains,
                    seed.TaskTypes,
                    "auto-paraphrase",
                    seed.Tag));
            }
        }
    }

    return manual.Concat(generated).ToArray();
}

static IReadOnlyList<DeliveryCase> BuildUnseenDeliverySet() =>
[
    Delivery(
        "delivery-document-001",
        "document",
        "把我发来的物业检修安排整理成一份能贴在各单元门口的正式通知，要有醒目的日期、受影响区域、联系人和住户签字回执区，版式适合A4黑白打印，最后给我一个可以直接打印的文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-document-002",
        "document",
        "根据附件里的内部规定做一本新员工入职手册，正文要有分级标题、自动目录、页码、注意事项框和每章后的练习空白，内容整理完整后生成正式文档，不要只把正文贴在聊天里。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-document-003",
        "document",
        "把这段杂乱的会议记录改成正式纪要，按议题列出决定、责任人和截止日期，添加公司抬头、页眉页脚和签字栏，保留一份可继续编辑的版本并放到今天的会议资料目录。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-document-004",
        "document",
        "利用我附上的经历和项目材料制作一份两页以内的求职简历，结构清晰、打印时不跑版，同时保留可编辑源文件和便于投递的PDF版本，两个文件都要实际生成。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-document-005",
        "document",
        "把仓库盘点流程写成现场能直接使用的标准作业手册，步骤旁边加勾选框，异常情况留记录位置，末页做负责人确认表，完成后产出排版好的可打印文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-document-006",
        "document",
        "为周末亲子活动做一份报名表，包含孩子信息、紧急联系人、过敏情况、授权签名和工作人员核验区，一页内排好并生成可以双面打印的正式表单。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),

    Delivery(
        "delivery-sheet-001",
        "sheet",
        "把附件中的三个月报销明细整理成月度费用台账，统一日期和类别、检查重复票据、补上合计公式，再做一张按部门和费用类型汇总的图表，最终交付可继续筛选的表格文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-sheet-002",
        "sheet",
        "把下载目录最新的销售清单清洗一下，订单号重复的只保留正确记录，金额和日期格式统一，然后新建汇总页做地区透视和趋势图，原文件不要覆盖，另存成整理后的工作簿。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-sheet-003",
        "sheet",
        "根据员工名单和可上班时间排出下个月值班表，每人班次数尽量均衡，连续夜班和时间冲突用颜色标出来，统计每个人的总班次并生成实际表格。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-sheet-004",
        "sheet",
        "读取当前库存表，把今天的盘点数写回去，自动算出差异和库存金额，再把低于安全库存的物品单独整理到一个新页，保存一份带今天日期的结果文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-sheet-005",
        "sheet",
        "将这批问卷结果做成可交付的数据分析表，先清除无效回答，再按年龄段和渠道统计，添加百分比、图表和结论摘要页，最后留下能复核原始数据的工作簿。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-sheet-006",
        "sheet",
        "按我给的收入、固定成本和三种增长假设建立预算测算表，各方案可以修改参数并自动重算，关键差异做成对比图，别只告诉我公式，直接把模型文件做出来。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),

    Delivery(
        "delivery-presentation-001",
        "presentation",
        "用附件里的经营数据制作一份管理层月度复盘演示，先提炼三个关键结论，再用图表说明原因和风险，每页控制信息密度，附上演讲备注，生成16比9的成品演示文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-presentation-002",
        "presentation",
        "为工厂安全培训做一套半小时能讲完的幻灯片，包含事故情景、操作规范、互动提问和最后测验，还要适合导出成每页三张的讲义，完成后给我实际演示文件。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-presentation-003",
        "presentation",
        "把新品上市计划整理成一份对外汇报用的演示，覆盖目标人群、传播节奏、渠道预算和风险预案，统一视觉风格并加入时间轴，做完保存为可编辑的幻灯片。",
        true,
        Domains(IntentExecutionDomain.Office),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-presentation-004",
        "presentation",
        "把我发来的科普材料改造成十二页左右的课堂演示，重要概念配示意图，每四页安排一个互动问题，末尾有复习清单，最后产出能直接播放的文件而不是聊天提纲。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-presentation-005",
        "presentation",
        "根据这批照片和行程笔记做一份旅行回顾演示，自动挑选清晰图片、按日期排序、每站写简短说明，封面和结束页要完整，输出一份可以现场播放的成品。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.Media, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-presentation-006",
        "presentation",
        "把季度绩效数据和项目进展合成一套董事会简报，图表数字必须来自附件，问题与下一步行动要对应，页脚标注数据日期，生成演示稿并同时导出一份PDF备份。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),

    Delivery(
        "delivery-modify-001",
        "modify",
        "把桌面最新版报价表里的含税单价统一提高百分之八，数量、公式和原有格式都不能变，核对总价后另存一个带修订日期的副本，原件留着。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-modify-002",
        "modify",
        "在当前合同中把交付日期调整到下个月十五号，所有相关条款同步更新并打开修订记录，修改后做一次前后矛盾检查，再保存为新的可编辑文档。",
        true,
        Domains(IntentExecutionDomain.Office, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-modify-003",
        "modify",
        "把这份扫描材料的页面按编号重新排好，删掉空白页，在每页右下角加内部使用水印，最后合成一个新的PDF，原始扫描件不要动。",
        true,
        Domains(IntentExecutionDomain.File, IntentExecutionDomain.Office),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact],
        hasAttachments: true),
    Delivery(
        "delivery-modify-004",
        "modify",
        "将选中的产品照片按货号批量重命名，最长边缩到一千六百像素并保持比例，压缩后放进新文件夹，源图保持不变，处理结果要真实存在。",
        true,
        Domains(IntentExecutionDomain.File, IntentExecutionDomain.Media),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-modify-005",
        "modify",
        "把昨天录的视频前后空白剪掉，保留原分辨率，把文件压到适合手机发送的大小，并生成一个新的视频文件，不要只写处理步骤。",
        true,
        Domains(IntentExecutionDomain.Media, IntentExecutionDomain.File),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact]),
    Delivery(
        "delivery-modify-006",
        "modify",
        "把这个项目的测试环境地址换成附件里给的新地址，保持生产配置不变，运行现有检查确认没有破坏功能，然后把修改后的项目和检查结果保留下来。",
        true,
        Domains(IntentExecutionDomain.Development, IntentExecutionDomain.File, IntentExecutionDomain.MultiApp),
        ArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.StateObservation],
        minimumPlanSteps: 2,
        hasAttachments: true),

    Delivery(
        "delivery-send-001",
        "send",
        "整理附件中的验收记录，生成一份正式PDF报告，然后通过现有邮箱发给供应商联系人，主题写本周验收结果，附件名称清楚，发送后确认邮件已经出现在已发送中。",
        true,
        Domains(IntentExecutionDomain.MultiApp, IntentExecutionDomain.Communication),
        SendArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 2,
        hasAttachments: true),
    Delivery(
        "delivery-send-002",
        "send",
        "把当前排班表里冲突的地方修好并另存，再把修订版传给协作软件中的门店主管，附一句请大家今晚前确认，只有文件确实发到正确会话才算结束。",
        true,
        Domains(IntentExecutionDomain.MultiApp, IntentExecutionDomain.Communication),
        SendArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 2),
    Delivery(
        "delivery-send-003",
        "send",
        "根据项目资料制作进度汇报演示，完成后发到当前项目群并提醒负责人看风险页，不能只把演示内容作为文字回复，成品和发送结果都要核验。",
        true,
        Domains(IntentExecutionDomain.MultiApp, IntentExecutionDomain.Communication, IntentExecutionDomain.Office),
        SendArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 2,
        hasAttachments: true),
    Delivery(
        "delivery-send-004",
        "send",
        "把我刚才打开的那份说明书作为附件发给最近联系的维修师傅，消息里说明故障发生时间，先核对联系人和文件，再完成实际发送。",
        true,
        Domains(IntentExecutionDomain.Communication, IntentExecutionDomain.MultiApp),
        SendTypes(),
        requiredEvidence: [IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 1),
    Delivery(
        "delivery-send-005",
        "send",
        "把最新发票导出成PDF，登录当前已经打开的供应链后台，上传到本月对账页面并填写金额，提交后确认页面显示上传成功。",
        true,
        Domains(IntentExecutionDomain.MultiApp, IntentExecutionDomain.Browser, IntentExecutionDomain.Office),
        SendArtifactTypes(),
        requiredEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 2),
    Delivery(
        "delivery-send-006",
        "send",
        "根据附件信息把报名网站当前表单完整填好，上传证件文件并提交，遇到验证码时停下来叫我，其余步骤继续，最终以网页出现提交成功编号为准。",
        true,
        Domains(IntentExecutionDomain.Browser, IntentExecutionDomain.MultiApp),
        SendTypes(),
        requiredEvidence: [IntentEvidenceKind.ExternalState],
        minimumPlanSteps: 2,
        hasAttachments: true),

    Delivery(
        "delivery-answer-001",
        "answer",
        "只在这里分析我贴出的经营数据为什么毛利率下降，列出最可能的三个原因和还需要确认的信息，不要创建文件，也不要改动电脑上的任何内容。",
        false,
        Domains(IntentExecutionDomain.Answer, IntentExecutionDomain.Research),
        AnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
    Delivery(
        "delivery-answer-002",
        "answer",
        "评价一下我上一段回复是不是显得不耐烦，并给出两种更温和的说法就行，这次不需要替我发送，也不用生成文档。",
        false,
        Domains(IntentExecutionDomain.Answer),
        CreateAnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
    Delivery(
        "delivery-answer-003",
        "answer",
        "阅读我附上的短文后在聊天中概括作者的核心观点、论据漏洞和一个反例，只要给我可复制的文字总结，不要把它做成文件。",
        false,
        Domains(IntentExecutionDomain.Answer, IntentExecutionDomain.Research),
        CreateAnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState],
        hasAttachments: true),
    Delivery(
        "delivery-answer-004",
        "answer",
        "比较这三个采购方案的优缺点，按成本、风险和交付速度给出建议，结论直接写在对话中，我现在只是想听分析，不要操作任何软件。",
        false,
        Domains(IntentExecutionDomain.Answer),
        AnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
    Delivery(
        "delivery-answer-005",
        "answer",
        "解释这段错误信息通常代表什么以及我可以先检查哪三处，但别运行命令、别修改项目，只回答排查思路。",
        false,
        Domains(IntentExecutionDomain.Answer, IntentExecutionDomain.Development),
        AnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
    Delivery(
        "delivery-answer-006",
        "answer",
        "帮我拟一条向客户说明延期的消息，语气诚恳但别过度道歉，给我一段方便复制的文字即可，不要打开聊天软件，更不要替我发出去。",
        false,
        Domains(IntentExecutionDomain.Answer, IntentExecutionDomain.Communication),
        CreateAnswerTypes(),
        requiredEvidence: [IntentEvidenceKind.ReturnValue],
        forbiddenEvidence: [IntentEvidenceKind.Artifact, IntentEvidenceKind.ExternalState]),
];

static IEnumerable<DeliveryCase> SelectBalancedDelivery(
    IReadOnlyList<DeliveryCase> source,
    int maximum)
{
    var selected = source
        .GroupBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Take(maximum)
        .ToList();
    selected.AddRange(source.Where(item => !selected.Contains(item)).Take(maximum - selected.Count));
    return selected;
}

static IEnumerable<IntentCase> SelectBalanced(
    IReadOnlyList<IntentCase> source,
    int maximum)
{
    var selected = new List<IntentCase>();
    foreach (var group in source.GroupBy(item => item.Tags.Last()))
    {
        if (selected.Count >= maximum) break;
        selected.Add(group.First());
    }

    selected.AddRange(source.Where(item => !selected.Contains(item)).Take(maximum - selected.Count));
    return selected;
}

static IntentCase Case(
    string id,
    string text,
    bool requiresComputer,
    IReadOnlyList<IntentExecutionDomain> domains,
    IReadOnlyList<SemanticTaskType> taskTypes,
    string tag,
    string? secondTag = null,
    bool hasAttachments = false,
    IReadOnlyList<ConversationMessageSnapshot>? context = null) => new(
    id,
    text,
    requiresComputer,
    domains,
    taskTypes,
    secondTag is null ? [tag] : [tag, secondTag],
    hasAttachments,
    context);

static DeliveryCase Delivery(
    string id,
    string category,
    string text,
    bool requiresComputer,
    IReadOnlyList<IntentExecutionDomain> domains,
    IReadOnlyList<SemanticTaskType> taskTypes,
    IReadOnlyList<IntentEvidenceKind> requiredEvidence,
    IReadOnlyList<IntentEvidenceKind>? forbiddenEvidence = null,
    int minimumPlanSteps = 1,
    bool hasAttachments = false)
{
    var requiredDeliverables = category switch
    {
        "document" => new[] { IntentDeliverableKind.Document },
        "sheet" => [IntentDeliverableKind.Spreadsheet],
        "presentation" => [IntentDeliverableKind.Presentation],
        "modify" => [IntentDeliverableKind.ModifiedFile],
        "send" => [IntentDeliverableKind.ExternalDelivery],
        "answer" => [IntentDeliverableKind.ConversationalAnswer],
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
    return new DeliveryCase(
        id,
        category,
        text,
        requiresComputer,
        domains,
        taskTypes,
        requiredDeliverables,
        requiredEvidence.Contains(IntentEvidenceKind.Artifact),
        requiredEvidence,
        forbiddenEvidence ?? Array.Empty<IntentEvidenceKind>(),
        requiresComputer ? Math.Max(1, minimumPlanSteps) : 0,
        hasAttachments);
}

static bool IsArtifactDeliverable(IntentDeliverableKind kind) => kind is
    IntentDeliverableKind.Document or
    IntentDeliverableKind.Spreadsheet or
    IntentDeliverableKind.Presentation or
    IntentDeliverableKind.Image or
    IntentDeliverableKind.Audio or
    IntentDeliverableKind.Video or
    IntentDeliverableKind.File or
    IntentDeliverableKind.ModifiedFile;

static SeedCase Seed(
    string id,
    string template,
    IReadOnlyList<string> arguments,
    bool requiresComputer,
    IReadOnlyList<IntentExecutionDomain> domains,
    IReadOnlyList<SemanticTaskType> taskTypes,
    string tag) => new(id, template, arguments, requiresComputer, domains, taskTypes, tag);

static IReadOnlyList<IntentExecutionDomain> Domains(params IntentExecutionDomain[] domains) => domains;

static IReadOnlyList<SemanticTaskType> AnswerTypes() =>
    [SemanticTaskType.Answer, SemanticTaskType.Clarify];

static IReadOnlyList<SemanticTaskType> CreateAnswerTypes() =>
    [SemanticTaskType.Answer, SemanticTaskType.Create];

static IReadOnlyList<SemanticTaskType> ArtifactTypes() =>
    [
        SemanticTaskType.Create,
        SemanticTaskType.ComputerTask,
        SemanticTaskType.Hybrid,
        SemanticTaskType.MultiAppTask,
    ];

static IReadOnlyList<SemanticTaskType> SendTypes() =>
    [
        SemanticTaskType.ComputerAction,
        SemanticTaskType.ComputerTask,
        SemanticTaskType.Hybrid,
        SemanticTaskType.MultiAppTask,
    ];

static IReadOnlyList<SemanticTaskType> SendArtifactTypes() =>
    [SemanticTaskType.Hybrid, SemanticTaskType.MultiAppTask, SemanticTaskType.ComputerTask];

static IReadOnlyList<SemanticTaskType> WorkTypes() =>
    [SemanticTaskType.ComputerAction, SemanticTaskType.ComputerTask, SemanticTaskType.Hybrid, SemanticTaskType.MultiAppTask];

static IReadOnlyList<SemanticTaskType> MultiTypes() =>
    [SemanticTaskType.Hybrid, SemanticTaskType.MultiAppTask, SemanticTaskType.ComputerTask];

static IReadOnlyList<SemanticTaskType> HybridAnswerTypes() =>
    [SemanticTaskType.Answer, SemanticTaskType.Hybrid, SemanticTaskType.Create];

static IReadOnlyList<ConversationMessageSnapshot> Context(string role, string text) =>
    [new ConversationMessageSnapshot(role, text)];

static double Percentile(IEnumerable<long> values, double percentile)
{
    var sorted = values.Select(value => (double)value).Order().ToArray();
    if (sorted.Length == 0) return 0;
    var rank = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
    var low = (int)Math.Floor(rank);
    var high = (int)Math.Ceiling(rank);
    return low == high
        ? sorted[low]
        : sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
}

static bool IsInconclusive(string source) =>
    source.Contains("inconclusive", StringComparison.OrdinalIgnoreCase) ||
    source.Contains("unavailable", StringComparison.OrdinalIgnoreCase);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

internal sealed record IntentCase(
    string Id,
    string Text,
    bool RequiresComputer,
    IReadOnlyList<IntentExecutionDomain> AcceptedDomains,
    IReadOnlyList<SemanticTaskType> AcceptedTaskTypes,
    IReadOnlyList<string> Tags,
    bool HasAttachments,
    IReadOnlyList<ConversationMessageSnapshot>? Context);

internal sealed record SeedCase(
    string Id,
    string Template,
    IReadOnlyList<string> Arguments,
    bool RequiresComputer,
    IReadOnlyList<IntentExecutionDomain> Domains,
    IReadOnlyList<SemanticTaskType> TaskTypes,
    string Tag);

internal sealed record DeliveryCase(
    string Id,
    string Category,
    string Text,
    bool RequiresComputer,
    IReadOnlyList<IntentExecutionDomain> AcceptedDomains,
    IReadOnlyList<SemanticTaskType> AcceptedTaskTypes,
    IReadOnlyList<IntentDeliverableKind> RequiredDeliverables,
    bool RequiresArtifactDeliverable,
    IReadOnlyList<IntentEvidenceKind> RequiredEvidence,
    IReadOnlyList<IntentEvidenceKind> ForbiddenEvidence,
    int MinimumPlanSteps,
    bool HasAttachments);

internal sealed record DeliveryEvaluation(
    string Id,
    string Category,
    bool Passed,
    SemanticTaskType ActualTaskType,
    IntentExecutionDomain ActualDomain,
    bool ActualRequiresComputer,
    IReadOnlyList<IntentDeliverableKind> ActualDeliverables,
    IReadOnlyList<IntentEvidenceKind> ActualEvidence,
    int PlanSteps,
    long ElapsedMilliseconds,
    string Source,
    bool Inconclusive);

internal sealed record Evaluation(
    string Id,
    bool Passed,
    SemanticTaskType ActualTaskType,
    IntentExecutionDomain ActualDomain,
    bool ActualRequiresComputer,
    double Confidence,
    long ElapsedMilliseconds,
    string Source,
    bool Inconclusive,
    IReadOnlyList<string> Tags);
