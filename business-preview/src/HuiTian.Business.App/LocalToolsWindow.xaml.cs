using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using HuiTian.Business.Core;
using Microsoft.Win32;

namespace HuiTian.Business.App;

public partial class LocalToolsWindow : Window
{
    private readonly BusinessPaths _paths;
    private readonly LocalToolsRunner _runner;
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeTask;
    private Task? _disposeTask;
    private bool _closing;
    private bool _closeReady;
    public event Action<LocalOperationResult>? OperationCompleted;
    public bool IsOperationRunning => _activeTask is { IsCompleted: false };
    public LocalToolsWindow(BusinessPaths paths, LocalToolsRunner? runner = null)
    {
        _paths = paths;
        _runner = runner ?? new(paths);
        InitializeComponent();
        OutputDirectory.Text = "保存位置：" + paths.Outputs;
        OperationPicker.SelectedIndex = 0;
        Closing += Window_Closing;
    }
    private void Operation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresentationFields is null) return;
        var operation = (LocalOperation)OperationPicker.SelectedIndex;
        PresentationFields.Visibility = operation == LocalOperation.PresentationStyle ? Visibility.Visible : Visibility.Collapsed;
        WorksheetFields.Visibility = operation == LocalOperation.WorksheetDeduplication ? Visibility.Visible : Visibility.Collapsed;
        ExtractionFields.Visibility = operation == LocalOperation.ExtractText ? Visibility.Visible : Visibility.Collapsed;
        OperationDescription.Text = operation switch
        {
            LocalOperation.PresentationStyle => "在 PPTX 新副本中统一修改背景、字体和文字颜色。原文件保留；处理后复查文件结构，未做 Office 视觉预览。",
            LocalOperation.WorksheetDeduplication => "根据指定列的值去重，保留首行表头与首次出现的数据，生成 XLSX 新副本。请确认列实际存在；比较区分大小写。仅支持普通值表格，公式、合并单元格等复杂结构会明确拒绝。",
            _ => "从 DOCX、XLSX、PPTX 或 TXT 提取可读取的正文，保存到新 TXT。提取结果不保留版式；不含图片识别、PPT 备注等内容。"
        };
        SourcePath.Clear();
        OutputName.Clear();
    }
    private void SelectSource_Click(object sender, RoutedEventArgs e)
    {
        var operation = (LocalOperation)OperationPicker.SelectedIndex;
        var dialog = new OpenFileDialog
        {
            Title = "选择本地源文件", CheckFileExists = true, Multiselect = false,
            Filter = operation switch { LocalOperation.PresentationStyle => "PowerPoint 演示文稿|*.pptx", LocalOperation.WorksheetDeduplication => "Excel 工作簿|*.xlsx", _ => "支持提取文字的文件|*.docx;*.xlsx;*.pptx;*.txt" }
        };
        if (dialog.ShowDialog(this) != true) return;
        SetSourceFile(dialog.FileName);
    }
    internal void SetSourceFile(string path)
    {
        SourcePath.Text = path;
        var operation = (LocalOperation)OperationPicker.SelectedIndex;
        var suffix = operation switch { LocalOperation.PresentationStyle => "样式副本", LocalOperation.WorksheetDeduplication => "去重副本", _ => "文字提取" };
        var extension = operation switch { LocalOperation.PresentationStyle => ".pptx", LocalOperation.WorksheetDeduplication => ".xlsx", _ => ".txt" };
        OutputName.Text = $"{Path.GetFileNameWithoutExtension(path)}_{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
    }
    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationRunning || _closing || _disposeTask is not null) return;
        var limit = 64000;
        if (OperationPicker.SelectedIndex == (int)LocalOperation.ExtractText && !int.TryParse(LimitInput.Text, out limit)) { SetResult("参数需要调整", "文字提取上限请输入 1 至 64,000 之间的整数。", true); return; }
        var request = new LocalOperationRequest((LocalOperation)OperationPicker.SelectedIndex, SourcePath.Text, OutputName.Text.Trim(),
            BackgroundInput.Text, FontFamilyInput.Text, FontColorInput.Text, SheetInput.Text, ColumnsInput.Text, limit);
        _operationCancellation = new();
        _activeTask = RunOperationAsync(request, _operationCancellation.Token);
    }
    private async Task RunOperationAsync(LocalOperationRequest request, CancellationToken cancellationToken)
    {
        OperationForm.IsEnabled = false;
        ExecuteOperation.IsEnabled = false;
        CancelOperation.Visibility = Visibility.Visible;
        CancelOperation.IsEnabled = true;
        ResultArtifacts.ItemsSource = null;
        SetResult("正在处理", "本机工具开始执行。", false);
        try
        {
            var progress = new Progress<string>(message => { if (!cancellationToken.IsCancellationRequested) ResultDescription.Text = message; });
            var result = await _runner.ExecuteAsync(request, progress, cancellationToken);
            var detail = result.Summary;
            if (result.CancellationRequested) detail += "\n取消请求到达时当前文件步骤已完成；已生成的真实文件保留在下方。";
            SetResult(result.Partial ? "部分提取已保存" : "文件已生成并复查", detail, result.Partial);
            ResultArtifacts.ItemsSource = result.Artifacts;
            OperationCompleted?.Invoke(result);
        }
        catch (OperationCanceledException) { SetResult("已停止后续操作", "已响应取消请求；本次未返回可核验的新文件。", false); }
        catch (Exception ex) { SetResult("操作未完成", ex.Message, true); }
        finally
        {
            _operationCancellation?.Dispose();
            _operationCancellation = null;
            OperationForm.IsEnabled = !_closing;
            ExecuteOperation.IsEnabled = !_closing;
            CancelOperation.Visibility = Visibility.Collapsed;
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancellation();
    private void RequestCancellation()
    {
        if (_operationCancellation is null) return;
        _operationCancellation.Cancel();
        CancelOperation.IsEnabled = false;
        SetResult("已请求取消", "正在等待当前文件步骤结束。写入中的步骤不能强行中断；若文件已经生成，会保留并显示实际结果。", false);
    }
    private void SetResult(string heading, string description, bool caution)
    {
        ResultHeading.Text = heading;
        ResultHeading.Foreground = new SolidColorBrush(caution ? Color.FromRgb(154, 107, 58) : Color.FromRgb(42, 117, 125));
        ResultDescription.Text = description;
    }
    private void OpenArtifact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Artifact artifact }) return;
        try
        {
            var file = Path.GetFullPath(artifact.Path);
            var root = Path.GetFullPath(_paths.Outputs).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).ToLowerInvariant() is not (".pptx" or ".xlsx" or ".txt") || !File.Exists(file))
                throw new IOException("只允许打开本地工具交付目录中实际存在的 PPTX、XLSX 或 TXT 文件。");
            for (var current = file; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("此文件路径包含链接，请先检查交付目录。");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开输出", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        await DisposeServicesAsync();
        await Dispatcher.Yield(DispatcherPriority.Background);
        _closeReady = true;
        Close();
    }
    public Task DisposeServicesAsync() => _disposeTask ??= DisposeCoreAsync();
    private async Task DisposeCoreAsync()
    {
        RequestCancellation();
        if (_activeTask is not null) await _activeTask;
        await _runner.DisposeAsync();
    }
}
