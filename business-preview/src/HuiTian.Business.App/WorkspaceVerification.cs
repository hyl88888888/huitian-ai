using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

// An isolated WPF integration check. No real model, browser or external action is run.
internal static class WorkspaceVerification
{
    public static async Task<bool> RunAsync(BusinessPaths productPaths, string outputPath)
    {
        var testRoot = Path.Combine(productPaths.Temp, "ui-verification-" + Guid.NewGuid().ToString("N"));
        var paths = new BusinessPaths(testRoot);
        paths.EnsureDirectories();
        var checks = new List<string>();
        var approvalChecks = new List<string>();
        MainWindow? window = null;
        try
        {
            var planner = new ControlledPlanner();
            var recoveryDirectory = Path.Combine(paths.Data, "Runs");
            Directory.CreateDirectory(recoveryDirectory);
            await File.WriteAllTextAsync(Path.Combine(recoveryDirectory, "uncertain-restart-fixture.json"),
                JsonSerializer.Serialize(new { RunId = "uncertain-restart-fixture", State = "NeedsInput",
                    FinishedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
                    RequiresManualReconciliation = true }));
            window = new MainWindow(paths, plannerOverride: planner, prepareRuntimeOverride: (_, _) => Task.CompletedTask,
                confirmCloseOverride: () => true);
            var recoveryNotice = (Border)window.FindName("RecoveryNotice");
            var recoveryText = (TextBlock)window.FindName("RecoveryText");
            Check(recoveryNotice.Visibility == Visibility.Visible && recoveryText.Text.Contains("1 条") &&
                recoveryText.ToolTip.ToString()!.Contains("uncertain-restart-fixture"), "启动显示已结束但结果不确定的历史操作", checks);
            var view = (WorkspaceViewModel)window.DataContext;
            var liveDescription = "实际窗口：自建审批夹具；控件：提交模拟申请；页面内容：不可信的观察资料。\n" + new string('观', 12000) + "\n观察摘要末尾";
            var exactPayload = new string('参', 14000) + "完整参数末尾";
            var approval = new ApprovalDialog(null, "审批显示验证",
                new ToolDefinition("desktop_invoke", liveDescription, JsonSerializer.SerializeToElement(new { }), RiskLevel.Critical),
                new PlanStep { Title = "模型提出的计划标题", Arguments = JsonSerializer.SerializeToElement(new { elementId = "fixture-only-id", value = exactPayload }) });
            Check(approval.PlannedOperation.Text == "模型提出的计划标题" && approval.ObservedTarget.Text == liveDescription, "审批区分计划标题并完整保留运行时真实目标描述", approvalChecks);
            using (var parsed = JsonDocument.Parse(approval.CompleteParameters.Text))
                Check(parsed.RootElement.GetProperty("value").GetString() == exactPayload && approval.CompleteParameters.IsReadOnly && approval.ObservedTarget.IsReadOnly,
                    "审批完整参数与长观察文字可选择复制且未被裁剪", approvalChecks);
            Check(approval.RiskNotice.Text.Contains("Critical") && approval.RiskNotice.Text.Contains("不可逆"), "Critical 审批明确呈现可能不可逆风险", approvalChecks);
            var prompt = (TextBox)window.FindName("PromptInput");
            var send = (Button)window.FindName("SendButton");
            var stop = (Button)window.FindName("StopButton");
            var list = (ListBox)window.FindName("ConversationList");
            var newButton = (Button)window.FindName("NewConversationButton");
            var first = view.Selected!;
            prompt.Text = "测试对话甲";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => planner.Calls.Count == 1);
            Check(first.IsRunning, "首个任务启动", checks);
            newButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var second = view.Selected!;
            prompt.Text = "第二个对话的草稿";
            Check(first.Id != second.Id && first.IsRunning && second.Messages.Count == 0, "运行中立即切换新对话", checks);
            planner.FirstResult.SetResult(Answer("对话甲的结果"));
            await WaitForAsync(() => !first.IsRunning);
            Check(view.Selected == second && second.Messages.Count == 0 && prompt.Text == "第二个对话的草稿", "后台结果没有切换当前对话或覆盖草稿", checks);
            Check(first.Messages.Count == 2 && first.Messages[1].Content.EndsWith("对话甲的结果", StringComparison.Ordinal) && first.RunStatus == "仅文字答复", "结果写入原始对话且文字答复不冒充执行完成", checks);
            prompt.Text = "测试对话乙";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !second.IsRunning);
            Check(planner.Calls[1].History.Count == 0, "新对话未携带其他对话上下文", checks);
            Check(recoveryNotice.Visibility == Visibility.Visible && recoveryText.Text.Contains("1 条"),
                "切换对话与完成新任务不清除历史待核对提醒", checks);
            list.SelectedItem = first;
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            prompt.Text = "对话甲继续";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !first.IsRunning);
            Check(planner.Calls[2].History.Count == 2 && planner.Calls[2].History.All(m => !m.Content.Contains("对话乙")), "续聊只携带本对话上下文", checks);
            prompt.Text = "取消这个任务";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => planner.Calls.Count == 4);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => !first.IsRunning);
            Check(first.RunStatus == "已停止" && first.Messages.Last().Content.Contains("取消"), "停止请求传递到执行器", checks);
            var reloaded = new ConversationStore(paths).Load();
            Check(reloaded.Count == 2 && reloaded.Single(c => c.Id == first.Id).Messages.Count == first.Messages.Count, "对话记录可重新加载", checks);
            Check(reloaded.Single(c => c.Id == second.Id).Messages.All(m => !m.Content.Contains("对话甲")), "磁盘记录保持对话隔离", checks);
            prompt.Text = "关闭窗口时取消这个任务";
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => planner.Calls.Count == 5);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Close();
            window.Close();
            Check(!closed, "重复关闭请求不会跳过进行中的清理", checks);
            await WaitForAsync(() => closed);
            Check(!first.IsRunning && first.RunStatus == "已停止", "关闭窗口先取消并等待任务退出", checks);
            var afterClose = new ConversationStore(paths).Load();
            Check(afterClose.Single(c => c.Id == first.Id).RunStatus == "已停止", "关闭前已保存任务最终状态", checks);
            await window.DisposeServicesAsync();
            Check(window.DisposeServicesAsync().IsCompletedSuccessfully, "重复释放服务安全且已完成", checks);
            window = null;
            await WriteResultAsync(outputPath, new { passed = true, checks, approvalChecks, testRoot, externalActions = 0, actualModelCalls = 0 });
            return true;
        }
        catch (Exception ex)
        {
            if (window is not null) await window.DisposeServicesAsync();
            await WriteResultAsync(outputPath, new { passed = false, checks, approvalChecks, error = ex.ToString(), testRoot });
            return false;
        }
    }
    private static TaskPlan Answer(string text) => new() { Mode = "answer", Answer = text };
    private static void Check(bool value, string name, List<string> checks)
    {
        if (!value) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task WriteResultAsync(string path, object value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
    private sealed class ControlledPlanner : ITaskPlanner
    {
        public TaskCompletionSource<TaskPlan> FirstResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Request, IReadOnlyList<ChatMessage> History)> Calls { get; } = [];
        public async Task<TaskPlan> PlanAsync(string request, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken)
        {
            Calls.Add((request, history.ToArray()));
            if (Calls.Count == 1) return await FirstResult.Task.WaitAsync(cancellationToken);
            if (Calls.Count is 4 or 5) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Answer("隔离测试完成");
        }
    }
}
