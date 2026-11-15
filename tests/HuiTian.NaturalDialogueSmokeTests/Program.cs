using HuiTian.Core;
using HuiTian.Infrastructure;

var paths = new HuiTianPaths();
using var downloadEngine = new SegmentedDownloadEngine();
var installation = new LocalBrainInstaller(paths, downloadEngine)
    .GetInstalled(CapabilityTierId.Huang);

await using var brain = new LocalBrainServer(paths);
await using var bridge = new LocalResponsesBridge(paths, brain.ResponsesBaseUri);
await using var agent = new CodexAppServerClient(paths);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
var answerReady = new TaskCompletionSource<string>(
    TaskCreationOptions.RunContinuationsAsynchronously);

agent.ActivityReceived += (_, eventArgs) =>
{
    var activity = eventArgs.Activity;
    if (activity.Kind == AgentActivityKind.AssistantMessage &&
        activity.Status == AgentActivityStatus.Completed &&
        !string.IsNullOrWhiteSpace(activity.Detail))
    {
        answerReady.TrySetResult(ConversationPolicy.SanitizeAssistantText(activity.Detail));
    }

    if (activity.Kind == AgentActivityKind.Error)
    {
        answerReady.TrySetException(new InvalidOperationException(activity.Detail));
    }
};

await brain.StartAsync(installation, timeout.Token);
await bridge.StartAsync(timeout.Token);
await agent.StartAsync(timeout.Token);
await agent.CreateThreadAsync(AgentTaskRoute.DirectAnswer, timeout.Token);

const string previousAnswer = "山西历史悠久、风景优美，欢迎大家前来游览。";
var prompt = $$"""
    以下是本对话最近几条内容，仅用于理解指代，不代表本轮仍要执行：
    用户：给山西文旅写一句自然、有画面感的宣传语
    回天：{{previousAnswer}}

    {{ConversationPolicy.BuildTurnContract(
        "太生硬了，不够自然，像宣传口号。",
        AgentTaskRoute.DirectAnswer)}}
    """;
await agent.SendMessageAsync(prompt, timeout.Token);

var answer = await answerReady.Task.WaitAsync(timeout.Token);
if (string.IsNullOrWhiteSpace(answer) ||
    string.Equals(answer.Trim(), previousAnswer, StringComparison.Ordinal) ||
    answer.Contains("不够自然是指", StringComparison.Ordinal) ||
    answer.Contains("“不够自然”指", StringComparison.Ordinal) ||
    answer.Contains("这句话的意思", StringComparison.Ordinal))
{
    throw new InvalidDataException($"反馈没有被理解为改写要求：{answer}");
}

Console.WriteLine("ANSWER=" + answer.ReplaceLineEndings(" "));
Console.WriteLine("PASS: criticism was understood as a contextual rewrite instead of a literal definition");
