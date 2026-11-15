using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class ContextCompactionRegression
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        await using (var defaultClient = new CodexAppServerClient(new HuiTianPaths()))
        {
            Check(defaultClient.ContextCompactionTimeout == TimeSpan.FromMinutes(2),
                "the 192-token local checkpoint must have a bounded two-minute watchdog");
        }
        Check(!CodexAppServerClient.IsUserInterruptedTurn(
                  userInterrupted: false, completedStatus: "interrupted", internallyCompleted: false,
                  compactionTimedOut: true) &&
              CodexAppServerClient.IsUserInterruptedTurn(
                  userInterrupted: true, completedStatus: "interrupted", internallyCompleted: false,
                  compactionTimedOut: true) &&
              CodexAppServerClient.IsUserInterruptedTurn(
                  userInterrupted: false, completedStatus: "interrupted", internallyCompleted: false,
                  compactionTimedOut: false),
            "only an internal compaction timeout may recover an interrupted turn; a user stop must remain final");
        await VerifyCompletedCompactionStopsWatchdogAsync();
        await VerifyStalledCompactionTerminatesAsync();
        Console.WriteLine("PASS: context compaction progress, completion and bounded timeout");
    }

    private static async Task VerifyCompletedCompactionStopsWatchdogAsync()
    {
        await using var client = CreateClient("completed-turn");
        client.ContextCompactionTimeout = TimeSpan.FromMilliseconds(500);
        client.ContextCompactionInterruptGrace = TimeSpan.FromMilliseconds(20);
        var activities = new ConcurrentQueue<AgentActivity>();
        client.ActivityReceived += (_, e) => activities.Enqueue(e.Activity);

        Notify(client, "item/started", "completed-turn");
        Check(activities.Any(a => a.Title == "正在整理任务上下文" &&
                                  a.Status == AgentActivityStatus.InProgress),
            "compaction start must say what is happening to the user");
        Notify(client, "item/completed", "completed-turn");
        await Task.Delay(650);
        Check(activities.Any(a => a.Title == "任务上下文已整理" &&
                                  a.Status == AgentActivityStatus.Completed),
            "compaction completion must be visible");
        Check(client.HasActiveTurn && !activities.Any(a => a.Title == "当前任务已停止"),
            "a finished compaction must not be timed out later");
    }

    private static async Task VerifyStalledCompactionTerminatesAsync()
    {
        await using var client = CreateClient("stalled-turn");
        client.ContextCompactionTimeout = TimeSpan.FromMilliseconds(25);
        client.ContextCompactionInterruptGrace = TimeSpan.FromMilliseconds(15);
        var activities = new ConcurrentQueue<AgentActivity>();
        client.ActivityReceived += (_, e) => activities.Enqueue(e.Activity);

        Notify(client, "item/started", "stalled-turn");
        var terminalArrived = SpinWait.SpinUntil(
            () => activities.Any(a => a.Title == "当前任务已停止"),
            TimeSpan.FromSeconds(2));
        Check(terminalArrived, "a stalled compaction must terminate within its timeout and grace window");
        Check(activities.Any(a => a.Title == "上下文整理超时，正在恢复"),
            "a stalled compaction must report a bounded recovery attempt");
        Check(activities.Any(a => a.Title == "当前任务已停止" &&
                                  a.Status == AgentActivityStatus.Failed) &&
              !client.HasActiveTurn,
            "an unresponsive App Server must leave an explicit failed terminal state");

        using var lateCompletion = JsonDocument.Parse("""
            {"params":{"threadId":"fixture-thread","turn":{"id":"stalled-turn","status":"interrupted"}}}
            """);
        Invoke(client, "turn/completed", lateCompletion.RootElement);
        Check(activities.Count(a => a.Title == "当前任务已停止") == 1,
            "late completion must not replace the bounded timeout result");
    }

    private static CodexAppServerClient CreateClient(string turnId)
    {
        var client = new CodexAppServerClient(new HuiTianPaths());
        typeof(CodexAppServerClient).GetProperty("CurrentThreadId")!
            .SetValue(client, "fixture-thread");
        typeof(CodexAppServerClient).GetField("_currentTurnId", Flags)!
            .SetValue(client, turnId);
        return client;
    }

    private static void Notify(CodexAppServerClient client, string method, string turnId)
    {
        var message = JsonSerializer.SerializeToElement(new
        {
            @params = new
            {
                threadId = "fixture-thread",
                turnId,
                item = new { id = "compact-1", type = "contextCompaction" },
            },
        });
        Invoke(client, method, message);
    }

    private static void Invoke(CodexAppServerClient client, string method, JsonElement message) =>
        typeof(CodexAppServerClient).GetMethod("HandleNotification", Flags)!
            .Invoke(client, [method, message]);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
