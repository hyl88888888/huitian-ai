using HuiTian.Core;
using HuiTian.Infrastructure;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var paths = new HuiTianPaths();
var liveUniversal = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_AGENT_LIVE_UNIVERSAL_SMOKE"),
    "1",
    StringComparison.Ordinal);
var liveDocument = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_AGENT_LIVE_DOCUMENT_SMOKE"),
    "1",
    StringComparison.Ordinal);
var liveCapture = string.Equals(
    Environment.GetEnvironmentVariable("HUITIAN_AGENT_LIVE_DIRECT_SMOKE"),
    "1",
    StringComparison.Ordinal);
using var timeout = new CancellationTokenSource(
    liveDocument
        ? TimeSpan.FromMinutes(5)
        : liveUniversal
            ? TimeSpan.FromMinutes(3)
            : TimeSpan.FromSeconds(30));

if (liveDocument)
{
    await RunDocumentLiveSmokeAsync(paths, timeout.Token);
    return;
}

if (liveUniversal)
{
    await RunUniversalLiveSmokeAsync(paths, timeout.Token);
    return;
}

if (!liveCapture)
{
    await using var client = new CodexAppServerClient(paths);
    await client.StartAsync(timeout.Token);
    var threadId = await client.CreateThreadAsync(
        AgentTaskRoute.DirectAnswer,
        timeout.Token);
    if (string.IsNullOrWhiteSpace(threadId))
    {
        throw new InvalidDataException("App Server did not return a thread id.");
    }

    Console.WriteLine("PASS: official App Server initialize + thread/start");
    Console.WriteLine($"THREAD={threadId}");
    Console.WriteLine($"HOME={paths.AgentHome}");
    return;
}

const int capturePort = 18291;
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{capturePort}/");
listener.Start();
var captureTask = CaptureSingleRequestAsync(listener, timeout.Token);
var captureBackend = new AgentBackendConfiguration(
    "huitian-local",
    "huitian_capture",
    $"http://127.0.0.1:{capturePort}/v1",
    IsProductDefault: false);
await using var captureClient = new CodexAppServerClient(paths, backend: captureBackend);
await captureClient.StartAsync(timeout.Token);
var capturedThreadId = await captureClient.CreateThreadAsync(
    AgentTaskRoute.DirectAnswer,
    timeout.Token);
var marker = "DIRECT_ROUTE_SMOKE_" + Guid.NewGuid().ToString("N");
await captureClient.SendMessageAsync(
    ConversationPolicy.BuildTurnContract(marker, AgentTaskRoute.DirectAnswer),
    timeout.Token);

var rawBody = await captureTask.WaitAsync(timeout.Token);
var rawRequest = JsonNode.Parse(rawBody)?.AsObject()
                 ?? throw new JsonException("App Server request was not valid JSON.");
if (!rawRequest.ToJsonString().Contains(marker, StringComparison.Ordinal))
{
    throw new InvalidDataException("Captured the wrong Responses request.");
}

var normalizedBytes = LocalResponsesBridge.NormalizeResponsesRequest(rawBody);
var request = JsonNode.Parse(normalizedBytes)?.AsObject()
              ?? throw new JsonException("Normalized request was not valid JSON.");
var rawToolCount = (rawRequest["tools"] as JsonArray)?.Count ?? 0;
var tools = request["tools"] as JsonArray;
if (tools is not { Count: 0 })
{
    throw new InvalidDataException(
        $"Direct-route request exposed {tools?.Count ?? -1} tools.");
}

var normalizedText = request.ToJsonString();
if (normalizedText.Contains("local_shell", StringComparison.OrdinalIgnoreCase) ||
    normalizedText.Contains("shell_command", StringComparison.OrdinalIgnoreCase) ||
    normalizedText.Contains("apply_patch", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException("Direct-route request exposed a native execution tool.");
}

var developerCharacters = DeveloperCharacterCount(request);
if (developerCharacters >= 1_000 ||
    normalizedText.Contains("<permissions instructions>", StringComparison.Ordinal))
{
    throw new InvalidDataException(
        $"Direct-route permission guidance was not removed: {developerCharacters} characters.");
}

await captureClient.InterruptAsync(timeout.Token);
Console.WriteLine("PASS: official App Server direct-route request captured and normalized");
Console.WriteLine($"THREAD={capturedThreadId}");
Console.WriteLine(
    $"RAW_TOOLS={rawToolCount} TOOLS=0 RAW_BYTES={rawBody.Length} " +
    $"NORMALIZED_BYTES={normalizedBytes.Length} " +
    $"DEVELOPER_CHARS={developerCharacters}");

static async Task RunUniversalLiveSmokeAsync(
    HuiTianPaths paths,
    CancellationToken cancellationToken)
{
    const int isolatedBridgePort = 18282;
    var stopwatch = Stopwatch.StartNew();
    var marker = "UNIVERSAL_AGENT_SMOKE_" + Guid.NewGuid().ToString("N");
    var smokeDirectory = @"D:\HuiTian\UserContent\SmokeTests";
    var targetPath = Path.Combine(smokeDirectory, $"{marker}.txt");
    Directory.CreateDirectory(smokeDirectory);

    var intentDeclared = new TaskCompletionSource<IntentResolution>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var taskFinished = new TaskCompletionSource<AgentTaskOutcome>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var turnCompleted = new TaskCompletionSource<AgentActivity>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var activityCount = 0;

    // The desktop app may already own the product bridge on 18181.  Host the
    // bridge compiled by this test on an isolated port so a live result can
    // never accidentally exercise an older published process.
    await using var bridge = new LocalResponsesBridge(
        paths,
        new Uri("http://127.0.0.1:18180/v1/"),
        isolatedBridgePort);
    await bridge.StartAsync(cancellationToken);
    var backend = new AgentBackendConfiguration(
        "huitian-local",
        "huitian_live_smoke",
        $"http://127.0.0.1:{isolatedBridgePort}/v1",
        IsProductDefault: true);
    await using var client = new CodexAppServerClient(paths, backend: backend);
    client.ConfigureAutonomy(AutonomyMode.HighAutonomy);
    client.IntentDeclared += (_, args) => intentDeclared.TrySetResult(args.Intent);
    client.TaskFinished += (_, args) => taskFinished.TrySetResult(args.Outcome);
    client.ActivityReceived += (_, args) =>
    {
        Interlocked.Increment(ref activityCount);
        var activity = args.Activity;
        if (activity.Id.StartsWith("turn-", StringComparison.Ordinal) &&
            activity.Status is AgentActivityStatus.Completed or AgentActivityStatus.Failed)
        {
            turnCompleted.TrySetResult(activity);
        }
    };

    await client.StartAsync(cancellationToken);
    var threadId = await client.CreateThreadAsync(
        AgentTaskRoute.Composite,
        cancellationToken);
    if (string.IsNullOrWhiteSpace(threadId))
    {
        throw new InvalidDataException("Universal smoke did not receive a thread id.");
    }

    var request =
        $"请在 {targetPath} 创建一个纯文本文件，文件内容只能是以下唯一标记（不含引号）：{marker}。" +
        "创建后请重新读取并核对文件存在且内容准确。不要打开任何软件，不要发送消息，不要修改系统设置，也不要执行其他操作。";
    await client.SendMessageAsync(request, cancellationToken);

    try
    {
        await Task.WhenAll(
            intentDeclared.Task,
            taskFinished.Task,
            turnCompleted.Task).WaitAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        stopwatch.Stop();
        throw new TimeoutException(
            "Live universal smoke exceeded its three-minute safety limit. " +
            $"IntentDeclared={intentDeclared.Task.IsCompletedSuccessfully}; " +
            $"FileCreated={File.Exists(targetPath)}; " +
            $"TaskFinished={taskFinished.Task.IsCompletedSuccessfully}; " +
            $"TurnCompleted={turnCompleted.Task.IsCompletedSuccessfully}; " +
            $"Activities={activityCount}; ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    var intent = await intentDeclared.Task;
    var outcome = await taskFinished.Task;
    var terminalActivity = await turnCompleted.Task;
    if (!intent.RequiresComputer || string.IsNullOrWhiteSpace(intent.Goal))
    {
        throw new InvalidDataException(
            "Universal smoke did not declare a valid computer-task intent.");
    }

    if (!File.Exists(targetPath))
    {
        throw new FileNotFoundException(
            "Universal smoke did not create the requested file.", targetPath);
    }

    var actualContent = await File.ReadAllTextAsync(targetPath, cancellationToken);
    if (!string.Equals(actualContent, marker, StringComparison.Ordinal))
    {
        throw new InvalidDataException(
            $"Universal smoke file content mismatch. Expected {marker.Length} characters, " +
            $"received {actualContent.Length}.");
    }

    if (!outcome.Succeeded || outcome.Evidence.Count == 0 ||
        outcome.Evidence.All(string.IsNullOrWhiteSpace))
    {
        throw new InvalidDataException(
            "Universal smoke did not finish with succeeded=true and non-empty evidence.");
    }

    if (terminalActivity.Status != AgentActivityStatus.Completed || client.HasActiveTurn)
    {
        throw new InvalidDataException(
            $"Universal smoke turn did not complete cleanly: {terminalActivity.Status}.");
    }

    stopwatch.Stop();
    Console.WriteLine("PASS: live universal semantic task + verified file result");
    Console.WriteLine($"THREAD={threadId}");
    Console.WriteLine($"FILE={targetPath}");
    Console.WriteLine($"MARKER={marker}");
    Console.WriteLine(
        $"INTENT={intent.TaskType}/{intent.Domain} " +
        $"EVIDENCE={outcome.Evidence.Count} ACTIVITIES={activityCount} " +
        $"ELAPSED_MS={stopwatch.ElapsedMilliseconds}");
}

static async Task RunDocumentLiveSmokeAsync(
    HuiTianPaths paths,
    CancellationToken cancellationToken)
{
    const int isolatedBridgePort = 18283;
    var stopwatch = Stopwatch.StartNew();
    var documentDirectory = @"D:\HuiTian\UserContent\文档";
    Directory.CreateDirectory(documentDirectory);
    var before = Directory.EnumerateFiles(documentDirectory, "*.docx")
        .Select(Path.GetFullPath)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    var intentDeclared = new TaskCompletionSource<IntentResolution>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var taskFinished = new TaskCompletionSource<AgentTaskOutcome>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var turnCompleted = new TaskCompletionSource<AgentActivity>(
        TaskCreationOptions.RunContinuationsAsynchronously);

    await using var bridge = new LocalResponsesBridge(
        paths,
        new Uri("http://127.0.0.1:18180/v1/"),
        isolatedBridgePort);
    await bridge.StartAsync(cancellationToken);
    var backend = new AgentBackendConfiguration(
        "huitian-local",
        "huitian_document_smoke",
        $"http://127.0.0.1:{isolatedBridgePort}/v1",
        IsProductDefault: true);
    await using var client = new CodexAppServerClient(paths, backend: backend);
    client.ConfigureAutonomy(AutonomyMode.HighAutonomy);
    client.IntentDeclared += (_, args) => intentDeclared.TrySetResult(args.Intent);
    client.TaskFinished += (_, args) => taskFinished.TrySetResult(args.Outcome);
    client.ActivityReceived += (_, args) =>
    {
        if (args.Activity.Id.StartsWith("turn-", StringComparison.Ordinal) &&
            args.Activity.Status is AgentActivityStatus.Completed or AgentActivityStatus.Failed)
        {
            turnCompleted.TrySetResult(args.Activity);
        }
    };

    await client.StartAsync(cancellationToken);
    var threadId = await client.CreateThreadAsync(
        AgentTaskRoute.Composite,
        cancellationToken);
    await client.SendMessageAsync(
        "为社区夜间值班设计一份正式、可打印且可继续编辑的交接记录表。" +
        "请生成真正的 Word 文档，标题为“社区夜间值班交接记录”，包含基本信息、事项交接、" +
        "异常记录和接班确认四部分，并为手写内容、双方签名和日期留出清楚的空白位置。" +
        "排版要适合 A4 打印；不要只在聊天框里给模板。",
        cancellationToken);

    try
    {
        await Task.WhenAll(intentDeclared.Task, taskFinished.Task, turnCompleted.Task)
            .WaitAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw new TimeoutException(
            $"Live document smoke exceeded five minutes. ElapsedMs={stopwatch.ElapsedMilliseconds}.");
    }

    var intent = await intentDeclared.Task;
    var outcome = await taskFinished.Task;
    var terminal = await turnCompleted.Task;
    var created = Directory.EnumerateFiles(documentDirectory, "*.docx")
        .Select(Path.GetFullPath)
        .Where(path => !before.Contains(path))
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    if (created is null)
    {
        throw new FileNotFoundException("Document smoke did not create a new DOCX artifact.");
    }

    using var archive = ZipFile.OpenRead(created);
    var documentXml = archive.GetEntry("word/document.xml")
        ?? throw new InvalidDataException("Created DOCX has no Word document body.");
    using var reader = new StreamReader(documentXml.Open(), Encoding.UTF8);
    var xml = await reader.ReadToEndAsync(cancellationToken);
    if (!xml.Contains("社区夜间值班交接记录", StringComparison.Ordinal) ||
        !xml.Contains("接班确认", StringComparison.Ordinal))
    {
        throw new InvalidDataException("Created DOCX does not contain the requested title and sections.");
    }

    if (!intent.Deliverables.Any(item => item.Kind == IntentDeliverableKind.Document) ||
        !outcome.Succeeded || terminal.Status != AgentActivityStatus.Completed)
    {
        throw new InvalidDataException(
            "Document smoke did not finish with a verified document deliverable.");
    }

    stopwatch.Stop();
    Console.WriteLine("PASS: live unseen request + verified printable Word deliverable");
    Console.WriteLine($"THREAD={threadId}");
    Console.WriteLine($"DOCUMENT={created}");
    Console.WriteLine($"ELAPSED_MS={stopwatch.ElapsedMilliseconds}");
}

static async Task<byte[]> CaptureSingleRequestAsync(
    HttpListener listener,
    CancellationToken cancellationToken)
{
    var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
    await using var body = new MemoryStream();
    await context.Request.InputStream.CopyToAsync(body, cancellationToken);
    var response = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"capture complete\"}}");
    context.Response.StatusCode = 500;
    context.Response.ContentType = "application/json";
    context.Response.ContentLength64 = response.Length;
    await context.Response.OutputStream.WriteAsync(response, cancellationToken);
    context.Response.Close();
    return body.ToArray();
}

static int DeveloperCharacterCount(JsonObject request) =>
    request["input"]?.AsArray()
        .OfType<JsonObject>()
        .Where(item => item["role"]?.GetValue<string>() == "developer")
        .SelectMany(item => item["content"]?.AsArray().OfType<JsonObject>() ?? [])
        .Sum(item => item["text"]?.GetValue<string>().Length ?? 0) ?? 0;
