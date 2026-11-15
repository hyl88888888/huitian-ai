using System.Diagnostics;
using System.Windows;
using HuiTian.Core;
using HuiTian.Infrastructure;

namespace HuiTian.App;

public partial class MainWindow
{
    /// <summary>
    /// Only semantically separated goals for named, existing WeChat/QQ contacts
    /// enter this path. Discovery and an undifferentiated compound request stay
    /// with the general agent; neither may be broadcast as one message.
    /// </summary>
    private async Task<bool> TryHandleMultiChatFastPathAsync(
        CommunicationTargetSlots targets,
        SavedTask task)
    {
        if (targets.FindNewPeople || targets.Action != "chat" ||
            targets.Platforms.Count != 1 || targets.UseCurrentConversation ||
            targets.Platforms[0] is not ("微信" or "QQ") ||
            !targets.HasDistinctInstructionsForEveryTarget ||
            targets.TargetNames.Any(name => targets.ExcludedNames.Contains(
                name, StringComparer.OrdinalIgnoreCase)))
            return false;

        var platform = targets.Platforms[0];
        var plans = targets.TargetNames.Select(name =>
        {
            var instruction = targets.TargetInstructions.Single(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            var plan = CommunicationPlanCompiler.CompileGoal(
                platform, name, instruction.Goal, instruction.Persona,
                continueConversation: true, targets.RequestedDuration);
            return plan with
            {
                // The compiled template is never a message to another person.
                OpeningMessage = string.Empty,
                Objectives = [new CommunicationObjective(
                    1, CommunicationObjectiveKind.Other,
                    instruction.Goal, instruction.Goal)],
                UserStyleSamples = _explicitSocialStyleSamples,
            };
        }).ToArray();

        var activityId = $"multi-chat-{Guid.NewGuid():N}";
        var started = DateTimeOffset.Now;
        var performance = _currentPerformanceSession;
        var maximumDuration = plans.Max(plan => plan.MaximumDuration);
        using var cancellation = new CancellationTokenSource(maximumDuration + TimeSpan.FromMinutes(2));
        var active = new ActiveCommunicationSession(task.Id, cancellation);
        var registeredKeys = new List<string>();
        foreach (var plan in plans)
        {
            var key = plan.Platform + "\u001f" + plan.Recipient;
            if (_activeCommunicationSessions.TryAdd(key, active))
            {
                registeredKeys.Add(key);
                continue;
            }

            foreach (var registered in registeredKeys)
                _activeCommunicationSessions.TryRemove(
                    new KeyValuePair<string, ActiveCommunicationSession>(registered, active));
            const string duplicate = "其中一位联系人已有持续聊天在运行。为防止重复发送，本次多人会话没有启动。";
            CompleteTaskSubtasks(task, false, duplicate);
            AddLocalAssistantMessage(duplicate);
            SetTaskIdle("会话已在运行");
            await SaveCurrentTaskAsync();
            return true;
        }

        _quickTaskCancellation = cancellation;
        _taskStartedAt = started;
        _lastTaskDuration = null;
        CurrentStatusText.Text = L($"正在准备与 {plans.Length} 位联系人分别沟通");
        SendButton.IsEnabled = true;
        SendButton.Content = L("追加");
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        EnsureControlOverlay().ShowControl(L($"回天正在操作{platform}"));
        AddOrUpdateActivity(new AgentActivity(
            activityId, AgentActivityKind.Tool, "正在准备多人会话",
            "每位联系人有独立目标；发送前分别核对会话，不会群发同一条内部指令",
            AgentActivityStatus.InProgress, started));

        var results = Array.Empty<CommunicationSessionResult>();
        var summary = string.Empty;
        var succeeded = false;
        try
        {
            // Opening the already installed app is independent of composing
            // individual openings. Both can start without a screenshot loop.
            var appPreparation = _applicationFastPath.OpenAsync(platform, cancellation.Token);
            var openings = await Task.WhenAll(plans.Select(async plan =>
            {
                var opening = await _communicationTurnComposer.ComposeOpeningAsync(
                    plan, cancellation.Token);
                return string.IsNullOrWhiteSpace(opening)
                    ? null
                    : plan with
                    {
                        OpeningMessage = opening,
                        OpeningIsSemanticallyComposed = true,
                    };
            }));
            var launch = await appPreparation;
            performance?.RecordPhase(
                "multi_chat_application", launch.ElapsedMilliseconds);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!launch.Success)
            {
                summary = $"未能打开或调出{platform}，本次没有向任何联系人发送消息。请确认客户端已安装并登录。";
            }
            else if (openings.Any(plan => plan is null))
            {
                summary = "至少一位联系人的自然开场未能可靠生成；本次没有向任何人发送消息。请确认本机智能核心已就绪后重试。";
            }
            else
            {
                var prepared = openings.Select(plan => plan!).ToArray();
                var progress = new Progress<CommunicationContactProgress>(update =>
                {
                    if (!ReferenceEquals(_currentTask, task)) return;
                    CurrentStatusText.Text = L($"{update.Recipient}：{update.Progress.Stage}");
                    AddOrUpdateActivity(new AgentActivity(
                        $"{activityId}-{update.Platform}-{update.Recipient}",
                        AgentActivityKind.Tool,
                        $"{update.Recipient}：{update.Progress.Stage}",
                        $"{update.Progress.Detail} · 已发送 {update.Progress.OutgoingMessageCount} 条",
                        AgentActivityStatus.InProgress, DateTimeOffset.Now));
                });
                var executor = new CommunicationMultiSessionExecutor(_chatConversationTransport);
                var composers = prepared.ToDictionary(
                    plan => plan.Recipient,
                    _ => new LocalCommunicationTurnComposer(_backendConfiguration),
                    StringComparer.OrdinalIgnoreCase);
                try
                {
                    results = (await executor.ExecuteAsync(
                        prepared,
                        plan => new FastCommunicationTurnComposer(
                            composers[plan.Recipient]),
                        cancellation.Token,
                        progress)).ToArray();
                }
                finally
                {
                    foreach (var composer in composers.Values) composer.Dispose();
                }
                if (ReferenceEquals(_currentTask, task))
                {
                    foreach (var result in results)
                    {
                        AddOrUpdateActivity(new AgentActivity(
                            $"{activityId}-{result.Plan.Platform}-{result.Plan.Recipient}",
                            AgentActivityKind.Tool,
                            $"{result.Plan.Recipient}：会话已结束",
                            result.Detail,
                            result.Status == CommunicationSessionStatus.Completed
                                ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
                            DateTimeOffset.Now));
                    }
                }
                succeeded = results.Length == prepared.Length &&
                    results.All(result => result.Status == CommunicationSessionStatus.Completed);
                summary = string.Join("\n", results.Select(result =>
                    $"{result.Plan.Platform}联系人“{result.Plan.Recipient}”：" +
                    BuildCommunicationSessionAnswer(result)));
            }
        }
        catch (OperationCanceledException)
        {
            summary = "多人持续聊天已停止；未能核对的后续消息不会继续发送。";
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"Multi chat failed: {exception}");
            summary = "多人持续聊天发生异常，已停止；没有把未核对的内容继续发送。详细原因已写入本机诊断记录。";
        }
        finally
        {
            await cancellation.CancelAsync();
            foreach (var plan in plans)
                await _chatConversationTransport.EndSessionAsync(plan.Platform, plan.Recipient);
            foreach (var key in registeredKeys)
                _activeCommunicationSessions.TryRemove(
                    new KeyValuePair<string, ActiveCommunicationSession>(key, active));
            if (ReferenceEquals(_quickTaskCancellation, cancellation))
                _quickTaskCancellation = null;
            _lastTaskDuration = DateTimeOffset.Now - started;
        }

        CompleteTaskSubtasks(task, succeeded, summary);
        if (ReferenceEquals(_currentTask, task))
        {
            AddOrUpdateActivity(new AgentActivity(
                activityId, AgentActivityKind.Status,
                succeeded ? "多人沟通已完成" : "多人沟通需要处理",
                summary, succeeded ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
                started));
            AddLocalAssistantMessage(summary);
            SetTaskIdle(succeeded ? "沟通完成" : "沟通已停止");
            await SaveCurrentTaskAsync();
        }
        else
        {
            task.Messages.Add(new SavedMessage
            {
                Role = "assistant",
                Text = ConversationPolicy.SanitizeAssistantText(summary),
                Timestamp = DateTimeOffset.Now,
            });
            await SaveDetachedTaskAsync(task);
        }
        _ = performance?.CompleteAsync(succeeded);
        return true;
    }
}
