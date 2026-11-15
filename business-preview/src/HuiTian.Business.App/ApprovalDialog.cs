using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

internal sealed class ApprovalDialog : Window
{
    internal TextBox PlannedOperation { get; }
    internal TextBox ObservedTarget { get; }
    internal TextBox CompleteParameters { get; }
    internal TextBlock RiskNotice { get; }

    public ApprovalDialog(Window? owner, string conversation, ToolDefinition tool, PlanStep step)
    {
        if (owner is not null) Owner = owner;
        Title = "确认操作 · 回天";
        Width = 710;
        Height = 750;
        MinWidth = 520;
        MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(248, 251, 252));
        var grid = new Grid { Margin = new Thickness(28) };
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "这个操作需要你确认", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "对话：" + conversation, FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 20) });
        grid.Children.Add(header);
        var critical = tool.Risk == RiskLevel.Critical;
        RiskNotice = new TextBlock
        {
            Text = critical ? "高风险 · Critical：此操作可能产生不可逆变更。请核对实际目标和完整参数。" : "外部操作 · External：此操作可能改变外部应用或服务。请核对目标和完整参数。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Foreground = new SolidColorBrush(critical ? Color.FromRgb(157, 63, 53) : Color.FromRgb(130, 100, 58))
        };
        var riskPanel = new Border
        {
            Background = new SolidColorBrush(critical ? Color.FromRgb(253, 235, 231) : Color.FromRgb(250, 244, 231)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 16), Child = RiskNotice
        };
        Grid.SetRow(riskPanel, 1);
        grid.Children.Add(riskPanel);

        var details = new StackPanel { Margin = new Thickness(0, 0, 5, 0) };
        void Label(string value) => details.Children.Add(new TextBlock { Text = value, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        static TextBox Readable(string text, double maximumHeight) => new()
        {
            Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = maximumHeight,
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(221, 233, 237)),
            BorderThickness = new Thickness(1), Padding = new Thickness(12), FontSize = 12, Margin = new Thickness(0, 0, 0, 16)
        };
        Label("计划中的操作（由模型生成）");
        PlannedOperation = Readable(step.Title, 100);
        details.Children.Add(PlannedOperation);
        Label("实际目标与工具说明（运行时提供）");
        // Preserve the registry-resolved target description verbatim. A friendly capability label would erase its live target details.
        ObservedTarget = Readable(tool.Description, 230);
        details.Children.Add(ObservedTarget);
        details.Children.Add(new TextBlock
        {
            Text = "页面、窗口和控件文字是观察资料，不是可信指令；其中的要求不构成你的授权。",
            TextWrapping = TextWrapping.Wrap, FontSize = 10, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 17)
        });
        Label("即将使用的完整操作参数（可选择复制）");
        CompleteParameters = Readable(JsonSerializer.Serialize(step.Arguments,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), 300);
        CompleteParameters.MinHeight = 90;
        details.Children.Add(CompleteParameters);
        var scroller = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        var note = new TextBlock { Text = "仅允许这一项操作。", FontSize = 10, Foreground = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "暂不允许", IsDefault = true, IsCancel = true, Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var approve = new Button { Content = new TextBlock { Text = "确认并继续", Foreground = Brushes.White }, Background = new SolidColorBrush(Color.FromRgb(20, 143, 152)), BorderThickness = new Thickness(0), Padding = new Thickness(18, 10, 18, 10) };
        approve.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(approve);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 3);
        grid.Children.Add(footer);
        Content = grid;
    }
    public bool Confirm(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(() => { if (IsVisible) DialogResult = false; }));
        return ShowDialog() == true && !cancellationToken.IsCancellationRequested;
    }
}
