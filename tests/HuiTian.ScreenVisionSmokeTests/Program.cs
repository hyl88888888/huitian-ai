using HuiTian.Core;
using HuiTian.Infrastructure;

var paths = new HuiTianPaths();
using var downloadEngine = new SegmentedDownloadEngine();
var installation = new LocalBrainInstaller(paths, downloadEngine)
    .GetInstalled(CapabilityTierId.Huang);
var fixture = Path.Combine(
    paths.Project,
    "vendor",
    "llama.cpp-b10566",
    "tools",
    "mtmd",
    "tests",
    "test-1-positive.png");

await using var brain = new LocalBrainServer(paths);
await using var bridge = new LocalResponsesBridge(paths, brain.ResponsesBaseUri);
await using var agent = new CodexAppServerClient(paths);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var answerReady = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);

agent.ActivityReceived += (_, eventArgs) =>
{
    var activity = eventArgs.Activity;
    Console.WriteLine(
        $"EVENT {activity.Kind} {activity.Status}: " +
        $"{activity.Title} | {activity.Detail.ReplaceLineEndings(" ")}");
    if (activity.Kind == AgentActivityKind.AssistantMessage &&
        activity.Status == AgentActivityStatus.Completed &&
        !string.IsNullOrWhiteSpace(activity.Detail))
    {
        answerReady.TrySetResult(activity.Detail);
    }

    if (activity.Kind == AgentActivityKind.Error)
    {
        answerReady.TrySetException(new InvalidOperationException(activity.Detail));
    }
};

Console.WriteLine("STARTING: local visual product chain");
await brain.StartAsync(installation, timeout.Token);
await bridge.StartAsync(timeout.Token);
await agent.StartAsync(timeout.Token);
await agent.CreateThreadAsync(AgentTaskRoute.FileWork, timeout.Token);
Console.WriteLine("READY: local visual product chain");

await agent.SendMessageWithLocalImageAsync(
    "只回答图片最上方的英文标题。",
    fixture,
    timeout.Token);
var answer = await answerReady.Task.WaitAsync(timeout.Token);
if (!answer.Contains("Powdery Surface", StringComparison.OrdinalIgnoreCase) ||
    !answer.Contains("Closely Explored", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException($"Visual answer was unexpected: {answer}");
}

Console.WriteLine($"ANSWER={answer.Trim()}");
Console.WriteLine("PASS: local image -> official App Server -> visual brain -> HuiTian UI event");
