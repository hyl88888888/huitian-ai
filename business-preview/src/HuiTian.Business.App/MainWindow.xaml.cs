using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HuiTian.Business.Adapters;
using HuiTian.Business.Core;
using HuiTian.Business.Runtime;
using Microsoft.Win32;

namespace HuiTian.Business.App;

public partial class MainWindow : Window
{
    private readonly BusinessPaths _paths;
    private readonly ConversationStore _store;
    private readonly LocalRuntimeManager _runtime;
    private readonly DesktopToolRegistry _tools;
    private readonly LocalModelClient _model;
    private readonly AgentEngine _engine;
    private readonly WorkspaceViewModel _view = new();
    private readonly Dictionary<string, RunContext> _runs = [];
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _preview;
    private readonly Func<IProgress<string>?, CancellationToken, Task> _prepareRuntime;
    private readonly Func<bool>? _confirmCloseOverride;
    private bool _changingConversation;
    private bool _nearBottom = true;
    private bool _closing;
    private bool _closeReady;
    private bool _disposed;
    private Conversation? _displayed;
    private Task? _refreshTask;
    private Task? _disposeTask;
    private LocalToolsWindow? _localToolsWindow;

    public MainWindow(BusinessPaths paths, bool preview = false, ITaskPlanner? plannerOverride = null,
        Func<IProgress<string>?, CancellationToken, Task>? prepareRuntimeOverride = null, Func<bool>? confirmCloseOverride = null)
    {
        _paths = paths;
        _preview = preview;
        _store = new(paths);
        _runtime = new(paths);
        _tools = new(paths);
        _model = new LocalModelClient("http://127.0.0.1:18280/v1/chat/completions", "huitian-business-local");
        _engine = new(plannerOverride ?? new SemanticTaskPlanner(_model), _tools, paths);
        _prepareRuntime = prepareRuntimeOverride ?? ((progress, ct) => _runtime.EnsureReadyAsync(progress, ct));
        _confirmCloseOverride = confirmCloseOverride;
        InitializeComponent();
        DataContext = _view;
        CapabilitiesList.ItemsSource = _tools.Definitions.Select(CapabilityDisplay.FromTool).ToList();
        if (!preview)
            foreach (var conversation in _store.Load()) _view.Conversations.Add(conversation);
        if (_view.Conversations.Count == 0) NewConversation();
        else _view.Selected = _view.Conversations[0];
        DisplaySelectedConversation();
        if (!preview) RefreshRecoveryNotice();
        if (preview)
        {
            _view.RuntimeTitle = "尚未检查运行环境";
            _view.RuntimeDetail = "界面预览不启动模型或执行任务。";
            _view.ModelText = "打开应用后读取本机模型";
            _view.HardwareText = "打开应用后读取本机资源";
        }
        else Loaded += async (_, _) => await RefreshRuntimeAsync();
        Closing += Window_Closing;
    }

    private sealed class RunContext(Conversation conversation, CancellationTokenSource cancellation)
    {
        public Conversation Conversation { get; } = conversation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
    }

    private void RefreshRecoveryNotice()
    {
        try
        {
            var records = _engine.GetIncompleteRuns();
            RecoveryNotice.Visibility = records.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            RecoveryText.Text = $"有 {records.Count} 条历史操作记录需要核对。程序不会自动重放；再次发送或提交前，请先核对实际状态。";
            RecoveryText.ToolTip = string.Join("\n", records.Select(r =>
                $"{r.RunId}：{(r.RecordUnreadable ? "记录无法读取" : r.RequiresManualReconciliation ? "操作结果不确定" : "运行中断")}\n{r.CheckpointPath}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecoveryNotice.Visibility = Visibility.Visible;
            RecoveryText.Text = "无法检查历史操作记录。再次发送或提交前，请先核对实际状态和记录目录权限。";
        }
    }

    private void RecoveryDetails_Click(object sender, RoutedEventArgs e)
    {
        RefreshRecoveryNotice();
        var detail = new TextBox
        {
            Text = RecoveryText.Text + "\n\n" + RecoveryText.ToolTip,
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20)
        };
        new Window { Owner = this, Title = "待核对的历史操作", Width = 720, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = detail }.ShowDialog();
    }

    private void NewConversation_Click(object sender, RoutedEventArgs e) => NewConversation();
    private void NewConversation()
    {
        SaveDraft();
        var conversation = new Conversation();
        _view.Conversations.Insert(0, conversation);
        _view.Selected = conversation;
        ShowSection("workspace");
        DisplaySelectedConversation();
        if (!_preview) Persist(conversation);
        PromptInput?.Focus();
    }

    private void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingConversation || PromptInput is null) return;
        SaveDraft();
        ShowSection("workspace");
        DisplaySelectedConversation();
    }

    private void DisplaySelectedConversation()
    {
        _changingConversation = true;
        _displayed = _view.Selected;
        PromptInput.Text = _displayed?.Draft ?? "";
        _changingConversation = false;
        _nearBottom = true;
        UpdateRunControls();
        ClearNotice();
        Dispatcher.BeginInvoke(() => ChatScroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private void SaveDraft()
    {
        if (_displayed is null || PromptInput is null || _changingConversation) return;
        _displayed.Draft = PromptInput.Text;
        if (!_preview) Persist(_displayed);
    }

    private void PromptInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (InputPlaceholder is not null) InputPlaceholder.Visibility = string.IsNullOrEmpty(PromptInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_changingConversation && _displayed is not null) _displayed.Draft = PromptInput.Text;
    }

    private void PromptInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            BeginSend();
        }
    }
    private void Send_Click(object sender, RoutedEventArgs e) => BeginSend();

    private void BeginSend()
    {
        if (_preview || _closing || _disposed || _view.Selected is not { } conversation || conversation.IsRunning) return;
        var input = PromptInput.Text.Trim();
        if (input.Length == 0) { Notice("先写下你想完成的任务。"); return; }
        if (input.Length > 20000) { Notice("本次输入较长，请缩短到 20,000 字以内，或把材料作为文件添加。"); return; }
        ClearNotice();
        // Snapshot only this conversation before adding the new request.
        var history = conversation.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToArray();
        var attachments = conversation.Attachments.ToArray();
        var request = input;
        if (attachments.Length > 0)
            request += "\n\n用户提供的本机材料（只读引用；请用已注册工具读取，不要猜测内容）：\n" + string.Join("\n", attachments.Select(p => "- " + p));
        if (conversation.Messages.Count == 0)
            conversation.Title = input.Length > 22 ? input[..22] + "…" : input;
        conversation.Messages.Add(new() { Role = "user", Content = request });
        conversation.Draft = "";
        conversation.Attachments.Clear();
        conversation.Updated = DateTimeOffset.Now;
        conversation.IsRunning = true;
        conversation.RunStatus = "等待执行";
        conversation.Refresh();
        PromptInput.Clear();
        _nearBottom = true;
        ScrollConversationIfAppropriate(conversation);
        UpdateRunControls();
        Persist(conversation);
        var context = new RunContext(conversation, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _runs.Add(conversation.Id, context);
        context.Task = ExecuteAsync(context, request, history);
    }

    private async Task ExecuteAsync(RunContext context, string request, IReadOnlyList<ChatMessage> history)
    {
        var conversation = context.Conversation;
        var ct = context.Cancellation.Token;
        var gateAcquired = false;
        try
        {
            AddTimeline(conversation, "已接收", "任务已加入本机执行队列。");
            await _executionGate.WaitAsync(ct);
            gateAcquired = true;
            ct.ThrowIfCancellationRequested();
            conversation.RunStatus = "正在准备模型";
            AddTimeline(conversation, "准备环境", "正在检查本地模型；首次加载可能需要一些时间。");
            var preparation = new Progress<string>(message =>
            {
                if (!conversation.IsRunning) return;
                AddTimeline(conversation, "本地运行时", message);
                _view.RuntimeTitle = "正在准备模型";
                _view.RuntimeDetail = message;
            });
            await _prepareRuntime(preparation, ct);
            await RefreshRuntimeAsync();
            ct.ThrowIfCancellationRequested();
            var progress = new Progress<AgentEvent>(item =>
            {
                // Progress owns the originating conversation. Switching the sidebar never changes this target.
                if (!conversation.IsRunning) return;
                conversation.RunStatus = StateLabel(item.State);
                AddTimeline(conversation, StateLabel(item.State), item.Message);
                if (item.Artifact is not null) AddArtifact(conversation, item.Artifact);
                if (_view.Selected?.Id == conversation.Id) UpdateRunControls();
            });
            var result = await _engine.RunAsync(request, history, progress,
                (tool, step, token) => RequestApprovalAsync(conversation, tool, step, token), ct);
            foreach (var artifact in result.Artifacts) AddArtifact(conversation, artifact);
            conversation.RunStatus = StateLabel(result.State);
            conversation.Messages.Add(new() { Role = "assistant", Content = result.Summary });
            AddTimeline(conversation, StateLabel(result.State), "本次运行结束，结果已保存到本地对话。");
        }
        catch (OperationCanceledException)
        {
            conversation.RunStatus = "已停止";
            conversation.Messages.Add(new() { Role = "assistant", Content = "任务已停止。已经产生的文件和执行记录保留在这次对话中。" });
            AddTimeline(conversation, "已停止", "已响应停止请求。");
        }
        catch (Exception ex)
        {
            conversation.RunStatus = "未完成";
            conversation.Messages.Add(new() { Role = "assistant", Content = "这次任务未能完成。\n\n" + ex.Message });
            AddTimeline(conversation, "未完成", ex.Message);
            await RefreshRuntimeAsync();
        }
        finally
        {
            if (gateAcquired) _executionGate.Release();
            conversation.IsRunning = false;
            conversation.Updated = DateTimeOffset.Now;
            conversation.Refresh();
            Persist(conversation);
            ScrollConversationIfAppropriate(conversation);
            _runs.Remove(conversation.Id);
            RefreshRecoveryNotice();
            context.Cancellation.Dispose();
            if (_view.Selected?.Id == conversation.Id) UpdateRunControls();
        }
    }

    private async Task<bool> RequestApprovalAsync(Conversation conversation, ToolDefinition tool, PlanStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        conversation.RunStatus = "等待确认";
        var accepted = await Dispatcher.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) return false;
            return new ApprovalDialog(this, conversation.Title, tool, step).Confirm(ct);
        });
        ct.ThrowIfCancellationRequested();
        AddTimeline(conversation, accepted ? "已确认" : "未授权", step.Title);
        return accepted;
    }

    private static string StateLabel(RunState state) => state switch
    {
        RunState.Planning => "规划中", RunState.Running => "执行中", RunState.AwaitingApproval => "等待确认",
        RunState.NeedsInput => "需要补充", RunState.Completed => "已完成", RunState.Answered => "仅文字答复", RunState.Partial => "部分完成",
        RunState.Failed => "未完成", RunState.Cancelled => "已停止", _ => "等待开始"
    };

    private void AddTimeline(Conversation conversation, string label, string detail)
    {
        conversation.Timeline.Add(new() { Label = label, Detail = detail });
        conversation.Refresh();
        Persist(conversation);
    }
    private void AddArtifact(Conversation conversation, Artifact artifact)
    {
        if (!conversation.Artifacts.Any(a => string.Equals(a.Path, artifact.Path, StringComparison.OrdinalIgnoreCase))) conversation.Artifacts.Add(artifact);
        conversation.Refresh();
    }
    private void UpdateRunControls()
    {
        var running = _view.Selected?.IsRunning == true;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = running && _view.Selected?.RunStatus != "正在停止";
        SendButton.IsEnabled = !running;
    }
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Selected is { } selected && _runs.TryGetValue(selected.Id, out var context))
        {
            selected.RunStatus = "正在停止";
            context.Cancellation.Cancel();
            StopButton.IsEnabled = false;
        }
    }

    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0) _nearBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 72;
        else if (_nearBottom) ChatScroll.ScrollToEnd();
    }
    private void ScrollConversationIfAppropriate(Conversation conversation)
    {
        if (_view.Selected?.Id == conversation.Id && _nearBottom)
            Dispatcher.BeginInvoke(() => { if (_view.Selected?.Id == conversation.Id && _nearBottom) ChatScroll.ScrollToEnd(); }, DispatcherPriority.Loaded);
    }

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string suggestion }) { PromptInput.Text = suggestion; PromptInput.Focus(); PromptInput.CaretIndex = PromptInput.Text.Length; }
    }
    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string content })
            try { Clipboard.SetText(content); } catch (Exception) { Notice("剪贴板暂时不可用，可以直接选中文字复制。"); }
    }
    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_view.Selected is not { } conversation) return;
        var dialog = new OpenFileDialog { Title = "选择任务材料", Multiselect = true, CheckFileExists = true, Filter = "支持读取的文档与数据|*.txt;*.md;*.csv;*.tsv;*.docx;*.xlsx;*.pptx" };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
            if (!conversation.Attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) conversation.Attachments.Add(path);
        Persist(conversation);
        Notice("已添加本机文件路径；实际内容会在任务需要时由可用工具读取。");
    }
    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && _view.Selected is { } conversation) { conversation.Attachments.Remove(path); Persist(conversation); }
    }

    private void Artifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Artifact artifact }) return;
        try
        {
            var fullPath = Path.GetFullPath(artifact.Path);
            var outputRoot = Path.GetFullPath(_paths.Outputs).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            if (!fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase) || extension is not (".docx" or ".pptx" or ".xlsx" or ".txt" or ".md"))
                throw new InvalidOperationException("只能从这里打开交付目录中的 Word、PowerPoint、Excel 和文本文件。");
            if (!File.Exists(fullPath)) throw new FileNotFoundException("文件已移动或不再存在。", fullPath);
            for (var item = new FileInfo(fullPath) as FileSystemInfo; item is not null; item = item is FileInfo file ? file.Directory : (item as DirectoryInfo)?.Parent)
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("此路径包含文件链接，请先在交付目录中确认原始文件。");
                if (string.Equals(item.FullName.TrimEnd(Path.DirectorySeparatorChar), _paths.Outputs.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
            }
            Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开交付文件", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void OpenOutputs_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_paths.Outputs); Process.Start(new ProcessStartInfo("explorer.exe", _paths.Outputs) { UseShellExecute = true }); }
        catch (Exception ex) { Notice(ex.Message); }
    }

    private void Workspace_Click(object sender, RoutedEventArgs e) => ShowSection("workspace");
    private void LocalTools_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || _disposed) return;
        if (_localToolsWindow is not null) { _localToolsWindow.Activate(); return; }
        var window = new LocalToolsWindow(_paths) { Owner = this };
        _localToolsWindow = window;
        window.Closed += (_, _) => { if (_localToolsWindow == window) _localToolsWindow = null; };
        window.Show();
    }
    private void Capabilities_Click(object sender, RoutedEventArgs e) => ShowSection("capabilities");
    private void Files_Click(object sender, RoutedEventArgs e)
    {
        var artifacts = _view.Conversations.SelectMany(c => c.Artifacts).DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList();
        AllFilesList.ItemsSource = artifacts;
        NoFilesLabel.Visibility = artifacts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSection("files");
    }
    private void ShowSection(string section)
    {
        if (WorkspaceArea is null) return;
        WorkspaceArea.Visibility = section == "workspace" ? Visibility.Visible : Visibility.Collapsed;
        CapabilitiesArea.Visibility = section == "capabilities" ? Visibility.Visible : Visibility.Collapsed;
        FilesArea.Visibility = section == "files" ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = section switch { "capabilities" => "能力与连接", "files" => "交付文件", _ => "工作台" };
        WorkspaceNavigation.Background = new SolidColorBrush(section == "workspace" ? Color.FromRgb(35, 68, 82) : Colors.Transparent);
        CapabilitiesNavigation.Background = new SolidColorBrush(section == "capabilities" ? Color.FromRgb(35, 68, 82) : Colors.Transparent);
        FilesNavigation.Background = new SolidColorBrush(section == "files" ? Color.FromRgb(35, 68, 82) : Colors.Transparent);
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) => await RefreshRuntimeAsync();
    private Task RefreshRuntimeAsync()
    {
        if (_preview || _disposed) return Task.CompletedTask;
        if (_refreshTask is { IsCompleted: false }) return _refreshTask;
        return _refreshTask = ReadRuntimeStatusAsync();
    }
    private async Task ReadRuntimeStatusAsync()
    {
        try
        {
            var status = await _runtime.GetStatusAsync(_lifetime.Token);
            _view.RuntimeTitle = !status.IsInstalled ? "本地模型未安装" : status.IsRunning ? "本地服务运行中" : "模型已安装 · 待启动";
            _view.RuntimeDetail = status.IsInstalled ? status.Message : $"请运行 {Path.Combine(_paths.Root, "Project", "scripts", "Install-Models.ps1")} 安装本地模型。";
            _view.ModelText = string.IsNullOrWhiteSpace(status.ModelName) ? "未发现可用模型" : status.ModelName;
            _view.HardwareText = status.MemoryGiB > 0 ? $"{status.MemoryGiB:0.#} GB 内存 · {Environment.ProcessorCount} 个逻辑处理器" : $"{Environment.ProcessorCount} 个逻辑处理器 · 内存未能读取";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _view.RuntimeTitle = "环境检查未完成"; _view.RuntimeDetail = ex.Message; }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRuntimeAsync();
        var dialog = new Window
        {
            Title = "设置与本机信息 · 回天", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 600, Height = 510, ResizeMode = ResizeMode.NoResize, Background = new SolidColorBrush(Color.FromRgb(248, 251, 252))
        };
        var stack = new StackPanel { Margin = new Thickness(30) };
        stack.Children.Add(new TextBlock { Text = "本机运行环境", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        foreach (var line in new[] { $"状态：{_view.RuntimeTitle}", $"模型：{_view.ModelText}", $"硬件：{_view.HardwareText}", "连接：本地推理；未连接云端模型", $"数据目录：{_paths.Data}", $"交付目录：{_paths.Outputs}", $"安装说明：{Path.Combine(_paths.Root, "README.md")}" })
            stack.Children.Add(new TextBlock { Text = line, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 13) });
        stack.Children.Add(new TextBlock { Text = _view.RuntimeDetail, Foreground = new SolidColorBrush(Color.FromRgb(103, 134, 148)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 15) });
        var close = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 82 };
        close.Click += (_, _) => dialog.Close();
        stack.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.ShowDialog();
    }

    private void Persist(Conversation conversation)
    {
        if (_preview) return;
        try { _store.Save(conversation); }
        catch (Exception ex) { Notice("本地记录保存失败：" + ex.Message); }
    }
    private void Notice(string text) { TransientNotice.Text = text; TransientNotice.Visibility = Visibility.Visible; }
    private void ClearNotice() { TransientNotice.Text = ""; TransientNotice.Visibility = Visibility.Collapsed; }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady || _preview) return;
        e.Cancel = true;
        // A second close request must not bypass the first request's asynchronous cleanup.
        if (_closing) return;
        if (_runs.Count > 0 && !(_confirmCloseOverride?.Invoke() ??
            MessageBox.Show(this, "仍有任务正在执行。关闭工作台会请求停止任务，并保存现有记录。", "关闭回天", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)) return;
        _closing = true;
        SaveDraft();
        IsEnabled = false;
        await DisposeServicesAsync();
        await Dispatcher.Yield(DispatcherPriority.Background);
        _closeReady = true;
        Close();
    }
    public Task DisposeServicesAsync() => _disposeTask ??= DisposeServicesCoreAsync();

    private async Task DisposeServicesCoreAsync()
    {
        _disposed = true;
        _lifetime.Cancel();
        var localToolsCleanup = _localToolsWindow?.DisposeServicesAsync();
        // Keep services alive until all accepted runs have observed cancellation and persisted their final state.
        try { await Task.WhenAll(_runs.Values.Select(r => r.Task ?? Task.CompletedTask).ToArray()); }
        catch (Exception ex) { LogCleanupFailure("task-drain", ex); }
        if (localToolsCleanup is not null)
        {
            try { await localToolsCleanup; }
            catch (Exception ex) { LogCleanupFailure("local-tools", ex); }
        }
        try { _model.Dispose(); }
        catch (Exception ex) { LogCleanupFailure("model-client", ex); }
        try { await _tools.DisposeAsync(); }
        catch (Exception ex) { LogCleanupFailure("tool-registry", ex); }
        try { await _runtime.DisposeAsync(); }
        catch (Exception ex) { LogCleanupFailure("local-runtime", ex); }
        _lifetime.Dispose();
        _executionGate.Dispose();
    }

    private void LogCleanupFailure(string resource, Exception error)
    {
        try { File.AppendAllText(Path.Combine(_paths.Logs, "app-errors.log"), $"{DateTimeOffset.UtcNow:O} cleanup {resource}\n{error}\n"); }
        catch { /* Cleanup must continue even when logging is unavailable. */ }
    }
}
