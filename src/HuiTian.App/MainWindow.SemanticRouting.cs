using System.Diagnostics;
using System.Windows;
using HuiTian.Core;

namespace HuiTian.App;

public partial class MainWindow
{
    private async Task<bool> ResolveTurnRouteAsync(
        string request,
        IReadOnlyList<ConversationMessageSnapshot> context,
        IReadOnlyList<string> attachments)
    {
        _intentCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _intentCancellation = cancellation;
        var task = _currentTask;
        var performance = _currentPerformanceSession;
        var started = Stopwatch.StartNew();
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        try
        {
            // One classification token on the same local model. No tool
            // inventory, desktop inspection or full JSON plan on this path.
            var decision = await _chatSemanticRouter.ClassifyAsync(request, context, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(task, _currentTask)) return false;
            var routing = SemanticTurnRouting.Decide(decision, attachments.Count > 0, isSteering: false);
            _currentSemanticIntent = decision;
            _currentTurnRoute = routing.Route;
            performance?.RecordPhase("intent", started.ElapsedMilliseconds);
            if (decision?.Source != "local-semantic-cache") performance?.RecordLlmCall();
            CurrentStatusText.Text = L(routing.NeedsComputerContext
                ? "正在准备执行您的目标" : "正在回答");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_intentCancellation, cancellation)) _intentCancellation = null;
        }
    }
}
