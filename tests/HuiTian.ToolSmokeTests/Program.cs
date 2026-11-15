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
var toolReady = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var answerReady = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);

agent.ActivityReceived += (_, eventArgs) =>
{
    var activity = eventArgs.Activity;
    Console.WriteLine(
        $"EVENT {activity.Kind} {activity.Status}: " +
        $"{activity.Title} | {activity.Detail.ReplaceLineEndings(" ")}");

    if (activity.Kind == AgentActivityKind.Tool &&
        activity.Status == AgentActivityStatus.Completed &&
        activity.Title == "搜索文件已完成")
    {
        toolReady.TrySetResult(activity.Detail);
    }

    if (activity.Kind == AgentActivityKind.AssistantMessage &&
        activity.Status == AgentActivityStatus.Completed &&
        !string.IsNullOrWhiteSpace(activity.Detail))
    {
        answerReady.TrySetResult(activity.Detail);
    }

    if (activity.Kind == AgentActivityKind.Error)
    {
        var exception = new InvalidOperationException(activity.Detail);
        toolReady.TrySetException(exception);
        answerReady.TrySetException(exception);
    }
};

Console.WriteLine("STARTING: local product chain");
await brain.StartAsync(installation, timeout.Token);
await bridge.StartAsync(timeout.Token);
await agent.StartAsync(timeout.Token);
await agent.CreateThreadAsync(AgentTaskRoute.FileWork, timeout.Token);
Console.WriteLine("READY: local product chain");

await agent.SendMessageAsync(
    "请使用回天的文件搜索能力，在 D:\\HuiTian\\Project 内查找文件名包含“总需求”的文件，并告诉我完整路径。",
    timeout.Token);

var toolResult = await toolReady.Task.WaitAsync(timeout.Token);
var answer = await answerReady.Task.WaitAsync(timeout.Token);
if (!toolResult.Contains("回天AI项目总需求与执行规范.md", StringComparison.Ordinal))
{
    throw new InvalidDataException($"The file-search result was unexpected: {toolResult}");
}

Console.WriteLine($"TOOL_RESULT={toolResult.ReplaceLineEndings(" | ")}");
Console.WriteLine($"ANSWER={answer.Trim()}");
Console.WriteLine("PASS: official App Server -> HuiTian file-search tool -> local brain");
