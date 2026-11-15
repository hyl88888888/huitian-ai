using HuiTian.Core;
using HuiTian.Infrastructure;

var paths = new HuiTianPaths();
using var downloadEngine = new SegmentedDownloadEngine();
var installation = new LocalBrainInstaller(paths, downloadEngine)
    .GetInstalled(CapabilityTierId.Huang);

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

Console.WriteLine("STARTING: local brain");
await brain.StartAsync(installation, timeout.Token);
Console.WriteLine("READY: local brain");
await bridge.StartAsync(timeout.Token);
Console.WriteLine("READY: local compatibility bridge");
await agent.StartAsync(timeout.Token);
var threadId = await agent.CreateThreadAsync(AgentTaskRoute.DirectAnswer, timeout.Token);
Console.WriteLine($"READY: official App Server thread {threadId}");
await agent.SendMessageAsync("你是什么模型？", timeout.Token);

var answer = await answerReady.Task.WaitAsync(timeout.Token);
const string expected = "我是回天AI。底层采用本地AI引擎与开放技术组件。";
if (!string.Equals(answer.Trim(), expected, StringComparison.Ordinal))
{
    throw new InvalidDataException(
        $"Identity answer did not match. Expected '{expected}', actual '{answer.Trim()}'.");
}

Console.WriteLine($"ANSWER={answer.Trim()}");
Console.WriteLine("PASS: local brain -> official App Server -> HuiTian event stream");
