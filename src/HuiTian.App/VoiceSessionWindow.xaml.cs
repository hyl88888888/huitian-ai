using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace HuiTian.App;

public partial class VoiceSessionWindow : Window
{
    private readonly VoiceConversationController _controller;
    private bool _ended;
    private string _language = "zh-CN";

    internal VoiceSessionWindow(VoiceConversationController controller)
    {
        InitializeComponent();
        _controller = controller;
        _controller.PartialTextChanged += Controller_OnPartialTextChanged;
        _controller.TextRecognized += Controller_OnTextRecognized;
        _controller.StateChanged += Controller_OnStateChanged;
        Loaded += VoiceSessionWindow_OnLoaded;
        Closed += VoiceSessionWindow_OnClosed;
    }

    public event EventHandler<string>? UtteranceRecognized;

    public event EventHandler<string>? LiveTranscriptChanged;

    public void Speak(string text) => _controller.Speak(text);

    public void StreamResponseText(string responseId, string text, bool completed) =>
        _controller.StreamResponseText(responseId, text, completed);

    public void ResumeSession()
    {
        if (_ended)
        {
            return;
        }

        if (!_controller.IsSessionActive)
        {
            _controller.Start();
        }

        Show();
        DockToRight();
    }

    public void ShowWorking(string text)
    {
        VoiceStateText.Text = L("回天正在工作");
        TranscriptText.Text = text;
    }

    private void VoiceSessionWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        DockToRight();
        ((Storyboard)Resources["VoicePulse"]).Begin(this, true);
        try
        {
            _controller.Start();
        }
        catch (Exception exception)
        {
            VoiceStateText.Text = L("语音暂时无法启动");
            TranscriptText.Text = exception.Message;
            MuteButton.IsEnabled = false;
        }
    }

    private void Controller_OnPartialTextChanged(object? sender, string text) =>
        Dispatcher.InvokeAsync(() =>
        {
            TranscriptText.Text = text;
            LiveTranscriptChanged?.Invoke(this, text);
        });

    private void Controller_OnTextRecognized(object? sender, string text) =>
        Dispatcher.InvokeAsync(() => UtteranceRecognized?.Invoke(this, text));

    private void Controller_OnStateChanged(object? sender, string text) =>
        Dispatcher.InvokeAsync(() => VoiceStateText.Text = L(text));

    public void ApplyLanguage(string language)
    {
        _language = language;
        UiLocalizer.Apply(this, language);
        MuteButton.ToolTip = L(_controller.IsMuted ? "打开麦克风" : "关闭麦克风");
        HideVoiceButton.ToolTip = L("关闭语音框");
        StopSpeakingButton.ToolTip = L("停止朗读");
        System.Windows.Automation.AutomationProperties.SetName(
            MuteButton,
            L(_controller.IsMuted ? "打开麦克风" : "关闭麦克风"));
        System.Windows.Automation.AutomationProperties.SetName(
            HideVoiceButton,
            L("关闭语音框"));
        System.Windows.Automation.AutomationProperties.SetName(
            StopSpeakingButton,
            L("停止朗读"));
    }

    private void MuteButton_OnClick(object sender, RoutedEventArgs e)
    {
        _controller.ToggleMute();
        MuteButton.ToolTip = L(_controller.IsMuted ? "打开麦克风" : "关闭麦克风");
        System.Windows.Automation.AutomationProperties.SetName(
            MuteButton,
            L(_controller.IsMuted ? "打开麦克风" : "关闭麦克风"));
        MuteButton.Opacity = _controller.IsMuted ? 0.58 : 1;
    }

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        // A hidden microphone must never keep transcribing into the message
        // box. Voice wake can remain enabled, but the active conversation is
        // stopped before the orb disappears.
        _controller.Stop();
        Hide();
    }

    private void StopSpeakingButton_OnClick(object sender, RoutedEventArgs e) =>
        _controller.StopSpeaking();

    public void DockToRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 12, workArea.Right - Width - 24);
        Top = Math.Max(
            workArea.Top + 12,
            workArea.Top + (workArea.Height - Height) / 2);
    }

    private void Window_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void VoiceSessionWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        _controller.PartialTextChanged -= Controller_OnPartialTextChanged;
        _controller.TextRecognized -= Controller_OnTextRecognized;
        _controller.StateChanged -= Controller_OnStateChanged;
        _controller.Stop();
    }

    private string L(string source) => UiLocalizer.Translate(_language, source);
}
