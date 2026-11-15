using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HuiTian.Core;
using HuiTian.Infrastructure;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;

namespace HuiTian.App;

public partial class MainWindow : Window
{
    private static readonly Brush SelectedCardBrush =
        new SolidColorBrush(Color.FromRgb(48, 43, 30));
    private static readonly Brush DefaultCardBrush =
        new SolidColorBrush(Color.FromRgb(17, 24, 33));
    private static readonly Brush SelectedBorderBrush =
        new SolidColorBrush(Color.FromRgb(216, 182, 106));
    private static readonly Brush DefaultBorderBrush =
        new SolidColorBrush(Color.FromRgb(37, 48, 61));
    private static readonly Regex WindowsPathRegex = new(
        @"[A-Za-z]:\\[^\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp",
    };

    private readonly HuiTianPaths _paths = new();
    private readonly ProductStateStore _stateStore;
    private readonly ProductState _state;
    private readonly AgentBackendConfiguration _backendConfiguration;
    private readonly IHardwareDetector _hardwareDetector;
    private readonly IAgentSessionClient _agentClient;
    private readonly SegmentedDownloadEngine _downloadEngine;
    private readonly LocalBrainInstaller _brainInstaller;
    private readonly LocalBrainServer _brainServer;
    private readonly LocalResponsesBridge _responsesBridge;
    private readonly TaskPerformanceProfiler _performanceProfiler;
    private readonly SemanticIntentRouter _chatSemanticRouter;
    private readonly CommunicationIntentResolver _communicationIntentResolver;
    private readonly SocialConversationMemoryStore _socialMemory;
    private CancellationTokenSource? _intentCancellation;
    private readonly WindowsChatFastPath _chatFastPath = new();
    private readonly WindowsChatConversationTransport _chatConversationTransport;
    private readonly CommunicationConversationExecutor _communicationExecutor;
    private readonly LocalCommunicationTurnComposer _communicationTurnComposer;
    private readonly WindowsApplicationFastPath _applicationFastPath = new();
    private readonly WindowsEverydayFastPath _everydayFastPath = new();
    private readonly WindowsMediaConsumptionFastPath _mediaConsumptionFastPath = new();
    private readonly CompositeWorkflowFastPath _compositeFastPath = new();
    private readonly TaskContextBootstrapper _taskContextBootstrapper = new();
    private readonly VoiceConversationController _voiceController = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _selfEvolutionTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, ActivityVisual> _activityVisuals = new();
    private readonly Dictionary<string, ProcessGroupVisual> _processGroups = new();
    private readonly HashSet<string> _activeComputerItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> _savedActivityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _backgroundSavedActivityIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _artifactPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _inlineArtifactPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _completedActionTitles = [];
    private readonly List<string> _attachedPaths = [];
    private readonly Dictionary<string, DateTimeOffset> _performanceActivityStarts =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ActiveCommunicationSession> _activeCommunicationSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SocialDiscoveryTaskContext> _socialDiscoveryTasks =
        new(StringComparer.Ordinal);

    private HardwareProfile? _currentHardware;
    private SavedTask? _currentTask;
    private SavedTask? _backgroundTask;
    private IntentResolution? _backgroundIntentResolution;
    private TaskGraph? _backgroundTaskGraph;
    private VoiceSessionWindow? _voiceWindow;
    private ComputerControlOverlayWindow? _controlOverlay;
    private DateTimeOffset? _taskStartedAt;
    private TimeSpan? _lastTaskDuration;
    private string _currentTurnRequest = string.Empty;
    private AgentTaskRoute _currentTurnRoute = AgentTaskRoute.DirectAnswer;
    private SemanticIntentDecision? _currentSemanticIntent;
    private CommunicationTargetSlots? _currentCommunicationTargets;
    private IntentResolution? _currentIntentResolution;
    private TaskGraph? _currentTaskGraph;
    private TaskPerformanceSession? _currentPerformanceSession;
    private readonly ConcurrentDictionary<Guid, (TaskPerformanceSession Session, long ViewGeneration)>
        _modelRequests = new();
    private long? _pendingApprovalId;
    private string _pendingTheme;
    private bool _isScanning;
    private bool _agentConnected;
    private bool _brainReady;
    private bool _startupInProgress;
    private bool _isClosing;
    private bool _isRestoringHistory;
    private bool _detachedFromActiveTurn;
    private bool _userRequestedStop;
    private bool _followActivityTail = true;
    private bool _isProgrammaticActivityScroll;
    private bool _applyingInputHistory;
    private bool _sendWhenBackgroundEnds;
    private int _activityViewGeneration;
    private int _submissionGeneration;
    private int _inputHistoryIndex = -1;
    private string _inputHistoryDraft = string.Empty;
    private IReadOnlyList<string> _explicitSocialStyleSamples = [];
    private bool _selfEvolutionRunning;
    private bool _isRecordingWakePhrase;
    private CancellationTokenSource? _approvalAttentionCancellation;
    private CancellationTokenSource? _wakeRecordingCancellation;
    private CancellationTokenSource? _quickTaskCancellation;

    private sealed record ActiveCommunicationSession(
        string TaskId,
        CancellationTokenSource Cancellation);

    public MainWindow()
    {
        AssertConversationPresentationRules();
        InitializeComponent();
        _paths.EnsureDirectories();
        _stateStore = new ProductStateStore(_paths.AppState);
        _state = _stateStore.Load();
        _pendingTheme = _state.Theme;
        ApplyTheme(_state.Theme);
        SourceInitialized += (_, _) => ApplyTitleBarTheme(_state.Theme);
        _backendConfiguration = BuildBackendConfiguration(_state);
        _chatConversationTransport = new WindowsChatConversationTransport(_chatFastPath);
        _communicationExecutor = new CommunicationConversationExecutor(
            _chatConversationTransport);
        _communicationTurnComposer = new LocalCommunicationTurnComposer(
            _backendConfiguration);
        _hardwareDetector = new WindowsHardwareDetector(_paths);
        _downloadEngine = new SegmentedDownloadEngine();
        _brainInstaller = new LocalBrainInstaller(_paths, _downloadEngine);
        _brainServer = new LocalBrainServer(_paths);
        _responsesBridge = new LocalResponsesBridge(_paths, _brainServer.ResponsesBaseUri);
        _responsesBridge.ModelRequestStarted += (_, e) =>
        {
            if (_currentPerformanceSession is { IsCompleted: false } session)
            {
                _modelRequests[e.RequestId] =
                    (session, Volatile.Read(ref _activityViewGeneration));
                session.RecordLlmCall();
            }
        };
        _responsesBridge.ModelRequestProgress += (_, e) =>
        {
            if (!_modelRequests.TryGetValue(e.RequestId, out var request)) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (!_modelRequests.ContainsKey(e.RequestId) ||
                    _detachedFromActiveTurn ||
                    request.ViewGeneration != _activityViewGeneration ||
                    !ReferenceEquals(request.Session, _currentPerformanceSession) ||
                    request.Session.IsCompleted ||
                    ApprovalPanel.Visibility == Visibility.Visible) return;
                CurrentStatusText.Text =
                    $"本地模型正在分析下一步，已等待 {(int)e.Elapsed.TotalSeconds} 秒；尚未执行新动作";
            });
        };
        _responsesBridge.ModelRequestFinished += (_, e) =>
            _modelRequests.TryRemove(e.RequestId, out var removedRequest);
        _performanceProfiler = new TaskPerformanceProfiler(_paths);
        _chatSemanticRouter = new SemanticIntentRouter(_backendConfiguration, _paths);
        _communicationIntentResolver = new CommunicationIntentResolver(_backendConfiguration);
        _socialMemory = new SocialConversationMemoryStore(_paths);
        _agentClient = new CodexAppServerClient(_paths, backend: _backendConfiguration);
        _agentClient.ConfigureAutonomy(ParseAutonomyMode(_state.AutonomyMode));
        _agentClient.ActivityReceived += AgentClient_OnActivityReceived;
        _agentClient.PlanUpdated += AgentClient_OnPlanUpdated;
        _agentClient.ApprovalRequested += AgentClient_OnApprovalRequested;
        _agentClient.ConnectionChanged += AgentClient_OnConnectionChanged;
        _agentClient.ComputerAction += AgentClient_OnComputerAction;
        _agentClient.IntentDeclared += AgentClient_OnIntentDeclared;
        _agentClient.TaskFinished += AgentClient_OnTaskFinished;
        _voiceController.SelectVoice(_state.VoiceName);
        _voiceController.WakeWordDetected += VoiceController_OnWakeWordDetected;
        _voiceController.WakePhraseCaptured += VoiceController_OnWakePhraseCaptured;
        _voiceController.StateChanged += VoiceController_OnStateChanged;
        _voiceController.AudioLevelChanged += VoiceController_OnAudioLevelChanged;
        PopulateSettingsControls();
        RenderRecentTasks();
        ApplyArtifactPaneState();
        UiLocalizer.Apply(this, _state.Language);
        _clockTimer.Tick += ClockTimer_OnTick;
        _clockTimer.Start();
        _selfEvolutionTimer.Tick += SelfEvolutionTimer_OnTick;
        _selfEvolutionTimer.Start();
        WelcomePanel.SizeChanged += (_, _) => CharacterPanel.Visibility =
            CharacterPortrait.HasPortrait && WelcomePanel.ActualWidth >= 650 ? Visibility.Visible : Visibility.Collapsed;
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        // This installation is personal-use. Keep the configured model and
        // skip the old first-run hardware grading screen entirely.
        AdaptationView.Visibility = Visibility.Collapsed;
        try { _explicitSocialStyleSamples = await _socialMemory.ReadExplicitUserStyleSamplesAsync(); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { _explicitSocialStyleSamples = []; }
        SettingsHardwareText.Text = L("个人版固定使用已配置的本机模型；硬件信息可按需手动查看。");
        // Model/service initialization starts before image decode. Visuals never
        // extend startup and cannot turn an unready service into a ready one.
        var startupTask = InitializeProductCoreAsync();
        var appearance = Path.Combine(_paths.UserContent, "Appearance");
        var opening = CharacterOpeningOptions.Load(Path.Combine(appearance, "opening.json"));
        var portraitPath = Path.Combine(appearance, opening.ImageFileName);
        if (!File.Exists(portraitPath)) portraitPath = Path.Combine(_paths.Root, opening.ImageFileName);
        CharacterStartupPortrait.InitialFramingScale = opening.InitialScale;
        CharacterStartupPortrait.EyeAnchorX = opening.EyeAnchorX;
        CharacterStartupPortrait.EyeAnchorY = opening.EyeAnchorY;
        CharacterStartupPortrait.DurationSeconds = opening.DurationSeconds;
        if (CharacterPortrait.LoadPortrait(portraitPath))
        {
            CharacterPortrait.Height = Math.Clamp(220 / CharacterPortrait.NaturalAspectRatio, 124, 330);
            CharacterPortrait.CompleteReveal();
            CharacterPanel.Visibility = WelcomePanel.ActualWidth >= 650 ? Visibility.Visible : Visibility.Collapsed;
            if (!_brainReady && CharacterStartupPortrait.LoadPortrait(portraitPath))
            {
                CharacterStartupOverlay.Visibility = Visibility.Visible;
                if (startupTask.IsCompleted) CharacterStartupPortrait.CompleteReveal();
                else CharacterStartupPortrait.StartReveal(opening.ReduceMotion);
            }
        }
        await startupTask;
        _ = WindowsApplicationRegistry.Default.RefreshIfStaleAsync();
        _voiceController.ConfigureWakeWord(
            _state.WakeWordEnabled,
            _state.WakePhrase,
            _state.Language);
        MessageInput.Focus();
    }

    private void ReduceCharacterMotion_OnClick(object sender, RoutedEventArgs e)
    {
        CharacterPortrait.CompleteReveal();
        CharacterStartupPortrait.CompleteReveal();
    }

    private void SkipCharacterOpening_OnClick(object sender, RoutedEventArgs e)
    {
        CharacterStartupPortrait.CompleteReveal();
        CharacterStartupOverlay.Visibility = Visibility.Collapsed;
        // Main controls retain their actual readiness flags.
    }

    private async void RetryStartupButton_OnClick(object sender, RoutedEventArgs e)
    {
        await InitializeProductCoreAsync();
    }

    private void StartupLogsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_paths.AppState))
            Process.Start(new ProcessStartInfo("explorer.exe", _paths.AppState) { UseShellExecute = true });
    }

    private async Task InitializeProductCoreAsync()
    {
        // A retry must never race the initial startup or another retry.
        if (_startupInProgress || (_agentConnected && _brainReady)) return;
        _startupInProgress = true;
        RetryStartupButton.Visibility = Visibility.Collapsed;
        StartupLogsButton.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = false;
        VoiceButton.IsEnabled = false;
        var startupStage = "local-model";
        try
        {
            if (_backendConfiguration.IsProductDefault)
            {
                var tier = PersonalBrainModelPolicy.SelectedTier;
                // Personal edition uses the existing installation only. A missing
                // file must not trigger an unrequested multi-gigabyte download.
                var installation = _brainInstaller.GetInstalled(tier);
                ShowBrainPreparation("正在唤醒回天", indeterminate: true);

                BrainStatusText.Text = L("本机智能核心正在启动");
                CurrentStatusText.Text = L("正在唤醒回天");
                await _brainServer.StartAsync(installation);
                await _responsesBridge.StartAsync();
                _agentClient.ConfigureContextWindow(installation.ContextTokens);
            }
            else
            {
                ShowBrainPreparation("正在连接您的本机智能服务", indeterminate: true);
                BrainStatusText.Text = L("正在连接您的本机智能服务");
            }

            startupStage = "task-service";
            ShowBrainPreparation(_backendConfiguration.IsProductDefault ? "语言核心已启动，正在连接电脑任务服务" : "正在连接本机任务服务", indeterminate: true);
            await _agentClient.StartAsync();
            _brainReady = true;
            // A thread is deliberately not created here: the first user
            // request has not been classified yet.  Its route determines the
            // exact (often empty) tool set registered with App Server.
            _currentTask = new SavedTask();
            await Dispatcher.InvokeAsync(() =>
            {
                _agentConnected = true;
                ConnectionStatusText.Text = L("回天已就绪");
                ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(112, 214, 161));
                BrainStatusText.Text = L(_backendConfiguration.IsProductDefault
                    ? "本机智能核心已就绪"
                    : "您的本机智能服务已连接");
                CurrentStatusText.Text = L("准备接收任务");
                BrainProgressBar.Visibility = Visibility.Collapsed;
                BrainProgressText.Visibility = Visibility.Collapsed;
                CharacterPortrait.CompleteReveal();
                CharacterStartupPortrait.CompleteReveal();
                CharacterStartupOverlay.Visibility = Visibility.Collapsed;
                StartupStatusPanel.Visibility = Visibility.Collapsed;
                EnterButton.Visibility = Visibility.Visible;
                NewTaskButton.IsEnabled = true;
                SendButton.IsEnabled = true;
                FileButton.IsEnabled = true;
                VoiceButton.IsEnabled = true;
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _agentConnected = false;
                _brainReady = false;
                ConnectionStatusText.Text = L("本机能力待修复");
                ConnectionDot.Fill = new SolidColorBrush(Color.FromRgb(214, 121, 112));
                BrainStatusText.Text = L("本机能力暂时未能启动");
                CurrentStatusText.Text = L("当前仍可查看界面和本机任务记录");
                BrainProgressBar.IsIndeterminate = false;
                BrainProgressBar.Visibility = Visibility.Visible;
                BrainProgressBar.Value = 0;
                BrainProgressText.Visibility = Visibility.Visible;
                BrainProgressText.Text = L(FriendlyStartupError(exception));
                StartupStatusPanel.Visibility = Visibility.Visible;
                StartupStatusText.Text = L(FriendlyStartupError(exception));
                StartupProgressBar.IsIndeterminate = false;
                StartupProgressBar.Value = 0;
                CharacterPortrait.CompleteReveal();
                CharacterStartupPortrait.CompleteReveal();
                RetryStartupButton.Visibility = Visibility.Visible;
                StartupLogsButton.Visibility = Visibility.Visible;
                EnterButton.Visibility = Visibility.Visible;
                NewTaskButton.IsEnabled = true;
                SendButton.IsEnabled = false;
                FileButton.IsEnabled = false;
                VoiceButton.IsEnabled = false;
            });
            try
            {
                Directory.CreateDirectory(_paths.AppState);
                await File.AppendAllTextAsync(Path.Combine(_paths.AppState, "startup-status.log"),
                    $"{DateTimeOffset.UtcNow:O} stage={startupStage} error={exception.GetType().Name}\n");
            }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            _startupInProgress = false;
        }
    }

    private void ShowBrainPreparation(string text, bool indeterminate)
    {
        if (AdaptationView.Visibility == Visibility.Visible)
        {
            BrainProgressBar.Visibility = Visibility.Visible;
            BrainProgressBar.IsIndeterminate = indeterminate;
            BrainProgressBar.Value = 0;
            BrainProgressText.Visibility = Visibility.Visible;
            BrainProgressText.Text = L(text);
        }

        ConnectionStatusText.Text = L(text);
        StartupStatusPanel.Visibility = Visibility.Visible;
        StartupStatusText.Text = L(text);
        StartupProgressBar.IsIndeterminate = indeterminate;
        if (!indeterminate)
        {
            StartupProgressBar.Value = BrainProgressBar.Value;
        }
    }

    private async Task RunHardwareDiagnosticAsync()
    {
        if (_isScanning)
        {
            return;
        }

        _isScanning = true;
        try
        {
            var hardware = await _hardwareDetector.DetectAsync();
            _currentHardware = hardware;
            // Diagnostic only. The personal model policy is independent of
            // the detected CPU, GPU, RAM and available storage.
            SettingsHardwareText.Text = FormatHardwareDiagnostic(hardware);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsHardwareText.Text = _state.Language switch
            {
                "en-US" => "Hardware diagnostics unavailable. The configured local model has not changed.",
                "ja-JP" => "ハードウェア情報を取得できません。設定済みのローカルモデルは変更されていません。",
                _ => "暂时无法读取硬件信息；已配置的本机模型不受影响。",
            };
        }
        finally
        {
            _isScanning = false;
        }
    }

    private async Task AnimateTierSweepAsync(CapabilityTierId selected, bool animate)
    {
        var states = new[] { TierHuangState, TierXuanState, TierDiState, TierTianState };
        for (var index = 0; index < states.Length; index++)
        {
            states[index].Text = index <= (int)selected ? L("能力适配 ✓") : L("更高配置可达 · 点击查看");
            if (animate) await Task.Delay(110);
        }

        states[(int)selected].Text = L("当前适配 ✓");
    }

    private void HighlightTier(CapabilityTierId selected)
    {
        var cards = new[] { TierHuang, TierXuan, TierDi, TierTian };
        foreach (var card in cards)
        {
            card.Background = DefaultCardBrush;
            card.BorderBrush = DefaultBorderBrush;
            card.BorderThickness = new Thickness(1);
        }

        var selectedCard = cards[(int)selected];
        selectedCard.Background = SelectedCardBrush;
        selectedCard.BorderBrush = SelectedBorderBrush;
        selectedCard.BorderThickness = new Thickness(1.5);
    }

    private void ResetTierCards()
    {
        TierHuangState.Text = L("等待检测");
        TierXuanState.Text = L("等待检测");
        TierDiState.Text = L("等待检测");
        TierTianState.Text = L("等待检测");
        foreach (var card in new[] { TierHuang, TierXuan, TierDi, TierTian })
        {
            card.Background = DefaultCardBrush;
            card.BorderBrush = DefaultBorderBrush;
            card.BorderThickness = new Thickness(1);
        }
    }

    private void TierCard_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } ||
            !Enum.TryParse<CapabilityTierId>(tag, out var tierId))
        {
            return;
        }

        var tier = CapabilityTiers.Get(tierId);
        TierRequirementName.Text = tier.Name;
        TierRequirementText.Text = FormatTierRequirement(tier);
    }

    private async void EnterButton_OnClick(object sender, RoutedEventArgs e)
    {
        AdaptationView.Visibility = Visibility.Collapsed;
        _state.HasCompletedFirstRun = true;
        await SaveStateQuietlyAsync();
        MessageInput.Focus();
    }

    private async void NewTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _submissionGeneration);
        _intentCancellation?.Cancel();
        if (!_agentConnected)
        {
            AddLocalAssistantMessage("本机能力尚未就绪，请重新启动回天后再试。 ");
            return;
        }

        try
        {
            await SaveCurrentTaskAsync();
            var hasAgentBackgroundTurn = _agentClient.HasActiveTurn;
            var hasQuickBackgroundTurn = _quickTaskCancellation is not null ||
                _activeCommunicationSessions.Values.Any(session =>
                    session.TaskId == _currentTask?.Id);
            var hasBackgroundTurn = hasAgentBackgroundTurn || hasQuickBackgroundTurn;
            if (hasBackgroundTurn && !_detachedFromActiveTurn)
            {
                _backgroundTask = _currentTask;
                _backgroundIntentResolution = _currentIntentResolution;
                _backgroundTaskGraph = _currentTaskGraph;
                _backgroundSavedActivityIds.Clear();
                // Only App Server events need the detached routing flag. A
                // product fast path saves itself against its captured task,
                // so leaving this flag on would incorrectly route a new
                // foreground agent's progress into the old task.
                _detachedFromActiveTurn = hasAgentBackgroundTurn;

                // A product fast path owns its own local cancellation source
                // while it finishes in the background.  Once the user opens
                // a new task, the global slot must represent only the new
                // foreground task; otherwise its Stop button can cancel the
                // old background job instead of the task now on screen.
                if (hasQuickBackgroundTurn)
                {
                    _quickTaskCancellation = null;
                }
            }

            Interlocked.Increment(ref _activityViewGeneration);
            _sendWhenBackgroundEnds = false;
            _currentTask = new SavedTask();
            _currentIntentResolution = null;
            _currentSemanticIntent = null;
            _currentCommunicationTargets = null;
            _currentTaskGraph = null;
            _currentTurnRoute = AgentTaskRoute.Composite;
            ResetTaskView();
            if (hasBackgroundTurn)
            {
                CurrentStatusText.Text = L("上一项任务正在后台完成");
                SendButton.IsEnabled = true;
                SendButton.Content = L(hasAgentBackgroundTurn ? "排队发送" : "发送");
            }
        }
        catch
        {
            AddLocalAssistantMessage("新任务暂时没有建立成功，当前任务记录仍会保留。 ");
        }
    }

    private async void SendButton_OnClick(object sender, RoutedEventArgs e) =>
        await SendCurrentMessageAsync();

    private async void MessageInput_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (TryNavigateInputHistory(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await SendCurrentMessageAsync();
        }
    }

    private void MessageInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingInputHistory)
        {
            return;
        }

        _inputHistoryIndex = -1;
        _inputHistoryDraft = string.Empty;
    }

    private bool TryNavigateInputHistory(Key key)
    {
        if (key is not (Key.Up or Key.Down) || Keyboard.Modifiers != ModifierKeys.None)
        {
            return false;
        }

        var history = _currentTask?.Messages
            .Where(message => message.Role == "user")
            .Select(message => message.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray() ?? [];
        if (history.Length == 0)
        {
            return false;
        }

        var activeNavigation = _inputHistoryIndex >= 0;
        var caretLine = MessageInput.GetLineIndexFromCharacterIndex(MessageInput.CaretIndex);
        var onFirstLine = caretLine <= 0;
        var onLastLine = caretLine >= Math.Max(0, MessageInput.LineCount - 1);
        if (!activeNavigation &&
            ((key == Key.Up && !onFirstLine) || (key == Key.Down && !onLastLine)))
        {
            return false;
        }

        if (!activeNavigation)
        {
            _inputHistoryDraft = MessageInput.Text;
            _inputHistoryIndex = history.Length;
        }

        if (key == Key.Up)
        {
            _inputHistoryIndex = Math.Max(0, _inputHistoryIndex - 1);
            ApplyInputHistoryText(history[_inputHistoryIndex], placeCaretAtStart: true);
            return true;
        }

        _inputHistoryIndex = Math.Min(history.Length, _inputHistoryIndex + 1);
        var nextText = _inputHistoryIndex == history.Length
            ? _inputHistoryDraft
            : history[_inputHistoryIndex];
        ApplyInputHistoryText(nextText, placeCaretAtStart: false);
        if (_inputHistoryIndex == history.Length)
        {
            _inputHistoryIndex = -1;
            _inputHistoryDraft = string.Empty;
        }

        return true;
    }

    private void ApplyInputHistoryText(string text, bool placeCaretAtStart)
    {
        _applyingInputHistory = true;
        try
        {
            MessageInput.Text = text;
            MessageInput.CaretIndex = placeCaretAtStart ? 0 : text.Length;
        }
        finally
        {
            _applyingInputHistory = false;
        }
    }

    private async Task SendCurrentMessageAsync(string? suppliedText = null, bool fromVoice = false)
    {
        if (_detachedFromActiveTurn && _agentClient.HasActiveTurn)
        {
            if (!string.IsNullOrWhiteSpace(suppliedText))
            {
                MessageInput.Text = suppliedText;
                MessageInput.CaretIndex = MessageInput.Text.Length;
            }

            if (!string.IsNullOrWhiteSpace(MessageInput.Text) || _attachedPaths.Count > 0)
            {
                _sendWhenBackgroundEnds = true;
                CurrentStatusText.Text = L("新任务已排队，上一项结束后自动发送");
                SendButton.Content = L("已排队");
            }
            else
            {
                CurrentStatusText.Text = L("上一项任务正在后台完成");
            }
            return;
        }

        var displayText = (suppliedText ?? MessageInput.Text).Trim();
        if (string.IsNullOrEmpty(displayText) && _attachedPaths.Count > 0)
        {
            displayText = "请查看并处理我添加的文件。";
        }

        if (string.IsNullOrEmpty(displayText))
        {
            return;
        }

        // Own the complete preparation phase, not only the semantic request.
        // A new task, Stop, or newer submission invalidates every old await.
        var submissionGeneration = Interlocked.Increment(ref _submissionGeneration);

        if (suppliedText is null || fromVoice)
        {
            MessageInput.Clear();
        }

        _inputHistoryIndex = -1;
        _inputHistoryDraft = string.Empty;

        WelcomePanel.Visibility = Visibility.Collapsed;
        var selectedFiles = _attachedPaths.ToArray();
        if (selectedFiles.Length == 0 &&
            ConversationPolicy.ReferencesPriorAttachments(displayText))
        {
            selectedFiles = _currentTask?.Messages
                .AsEnumerable()
                .Reverse()
                .Select(message => message.Attachments
                    .Where(File.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .TakeLast(4)
                    .ToArray())
                .FirstOrDefault(paths => paths.Length > 0) ?? [];
        }
        var userDisplay = selectedFiles.Length == 0
            ? displayText
            : displayText + "\n" + string.Join("\n", selectedFiles.Select(path => $"📎 {Path.GetFileName(path)}"));
        AddOrUpdateActivity(new AgentActivity(
            $"local-user-{Guid.NewGuid():N}",
            AgentActivityKind.UserMessage,
            string.Empty,
            userDisplay,
            AgentActivityStatus.Completed,
            DateTimeOffset.Now));

        var isSteering = _agentClient.HasActiveTurn;
        if (!isSteering || _currentPerformanceSession is null ||
            _currentPerformanceSession.IsCompleted)
        {
            _currentPerformanceSession = _performanceProfiler.StartSession(
                _currentTask?.Id ?? Guid.NewGuid().ToString("N"));
            _performanceActivityStarts.Clear();
        }
        var priorMessages = _currentTask?.Messages
            .Select(message => new ConversationMessageSnapshot(
                message.Role,
                message.Text,
                message.Attachments.Where(File.Exists).ToArray()))
            .ToArray() ?? [];
        _currentTurnRequest = displayText;
        // Clear the preceding turn's intent before resolving this request.
        // The short semantic envelope selects the real entry path below;
        // computer execution still declares and verifies every deliverable.
        _currentTurnRoute = AgentTaskRoute.Composite;
        _currentSemanticIntent = null;
        _currentCommunicationTargets = null;
        _currentIntentResolution = null;
        _currentTaskGraph = null;
        CurrentStatusText.Text = L("正在理解您的目标");
        var planningStopwatch = Stopwatch.StartNew();
        planningStopwatch.Stop();
        _currentPerformanceSession?.RecordPhase(
            "planning",
            planningStopwatch.ElapsedMilliseconds);
        RememberUserPreferences(displayText);
        await EnsureCurrentTaskForMessageAsync(displayText, selectedFiles);
        if (submissionGeneration != _submissionGeneration) return;
        var submittedTask = _currentTask;
        bool IsCurrentSubmission() => submissionGeneration == _submissionGeneration &&
            ReferenceEquals(submittedTask, _currentTask);
        if (ProductIdentity.TryAnswer(displayText, out var identityAnswer))
        {
            AddLocalAssistantMessage(identityAnswer);
            CompleteOpenSubtasks(succeeded: true, identityAnswer);
            ClearAttachments();
            return;
        }

        if (!isSteering && _brainReady &&
            !await ResolveTurnRouteAsync(displayText, priorMessages, selectedFiles))
        {
            return;
        }
        if (!IsCurrentSubmission()) return;

        // Opening one explicitly named installed application is a bounded
        // product action.  Do it immediately without asking the local model to
        // plan, inspect windows, or take screenshots.  The strict parser
        // declines any request that contains a second operation.
        if (!isSteering && selectedFiles.Length == 0 &&
            AllowsProductFastPath(IntentExecutionDomain.Other, IntentExecutionDomain.System) &&
            await TryHandleApplicationFastPathAsync(displayText))
        {
            if (IsCurrentSubmission()) ClearAttachments();
            return;
        }
        if (!IsCurrentSubmission()) return;

        // A deliberately small set of unambiguous Windows actions can finish
        // through deterministic OS APIs in milliseconds. This is only a
        // performance path: unfamiliar wording or multi-step goals continue
        // to the universal semantic agent below.
        if (!isSteering && selectedFiles.Length == 0 &&
            AllowsProductFastPath(
                IntentExecutionDomain.Browser,
                IntentExecutionDomain.System,
                IntentExecutionDomain.Media) &&
            await TryHandleEverydayFastPathAsync(displayText))
        {
            if (IsCurrentSubmission()) ClearAttachments();
            return;
        }
        if (!IsCurrentSubmission()) return;

        // Start a clearly expressed watch/listen request immediately.  This
        // only opens a licensed-source search; the universal agent continues
        // in the same turn to select the matching official result and verify
        // that a real player page is visible.  Opening the search is not
        // treated as completion.
        MediaConsumptionIntent? primedMediaIntent = null;
        if (!isSteering && selectedFiles.Length == 0 &&
            _currentSemanticIntent is { Mode: SemanticIntentMode.Operate, Domain: SemanticIntentDomain.Media })
        {
            primedMediaIntent = await TryPrimeMediaFastPathAsync(displayText);
        }
        if (!IsCurrentSubmission()) return;

        if (!_agentConnected || !_brainReady)
        {
            AddLocalAssistantMessage("本机能力尚未准备完成，任务内容已经保存在本机。 ");
            CompleteOpenSubtasks(succeeded: false, "本机能力尚未准备完成");
            ClearAttachments();
            _voiceController.ResumeListening();
            return;
        }

        // Do one real, privacy-minimal local read before asking the 27B brain
        // to generate a tool call.  On this hardware the model can need many
        // seconds before its first token; window/file identity is available in
        // milliseconds and prevents the full agent from beginning every task
        // with another blind inspect/screenshot cycle.  This snapshot is
        // context only and is never accepted as completion evidence.
        TaskContextBootstrapSnapshot? bootstrapContext = null;
        if (_currentTurnRoute != AgentTaskRoute.DirectAnswer)
        {
        var bootstrapActivityId = $"context-bootstrap-{Guid.NewGuid():N}";
        var bootstrapStartedAt = DateTimeOffset.Now;
        AddOrUpdateActivity(new AgentActivity(
            bootstrapActivityId,
            AgentActivityKind.Tool,
            "正在读取当前任务环境",
            "正在核对已打开程序和本轮相关文件",
            AgentActivityStatus.InProgress,
            bootstrapStartedAt));
        try
        {
            var recentArtifacts = _artifactPaths
                .Concat(_currentTask?.Artifacts ?? [])
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(SafeLastWriteTimeUtc)
                .Take(6)
                .ToArray();
            bootstrapContext = await _taskContextBootstrapper.CaptureAsync(
                selectedFiles,
                recentArtifacts,
                Environment.ProcessId);
            if (!IsCurrentSubmission()) return;
            _currentPerformanceSession?.RecordPhase(
                "context-bootstrap",
                bootstrapContext.ElapsedMilliseconds);
            AddOrUpdateActivity(new AgentActivity(
                bootstrapActivityId,
                AgentActivityKind.Tool,
                "当前任务环境已就绪",
                $"已读取 {bootstrapContext.Windows.Count} 个可见窗口和 {bootstrapContext.Files.Count} 个相关文件",
                AgentActivityStatus.Completed,
                bootstrapStartedAt));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSubmission()) return;
            bootstrapContext = new TaskContextBootstrapSnapshot([], [], 0, 0);
            AddOrUpdateActivity(new AgentActivity(
                bootstrapActivityId,
                AgentActivityKind.Tool,
                "继续处理当前任务",
                "即时环境读取未返回，已直接进入任务执行",
                AgentActivityStatus.Failed,
                bootstrapStartedAt));
        }

        }

        // High-confidence communication requests use the persistent chat
        // adapter immediately: locate/verify the contact once, retain the
        // reply watcher, and generate each continuation without loading the
        // full computer-agent tool set. Unfamiliar language still falls
        // through to the universal semantic agent below.
        if (!isSteering && _currentTurnRoute != AgentTaskRoute.DirectAnswer &&
            await TryHandleChatFastPathAsync(displayText, selectedFiles))
        {
            if (IsCurrentSubmission()) ClearAttachments();
            return;
        }
        if (!IsCurrentSubmission()) return;

        var agentText = BuildAgentText(
            displayText,
            selectedFiles,
            priorMessages,
            bootstrapContext);
        if (primedMediaIntent is not null)
        {
            agentText += $"\n已为本轮立即打开“{primedMediaIntent.Query}”的大陆可访问正版来源搜索。" +
                         "这只是中间步骤，不是任务完成。请观察当前浏览器，打开标题匹配的官方播放页并确认播放器真实出现；" +
                         "若平台要求登录、会员或付费，只说明该真实阻碍，不得寻找盗版或绕过。";
        }
        var imagePaths = selectedFiles
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .ToList();
        try
        {
            _userRequestedStop = false;
            // A clean App Server thread per completed user turn prevents old
            // tool traces and old answers from filling the local context.  A
            // bounded conversation digest below preserves normal follow-ups.
            if (!isSteering)
            {
                // Establish every completed turn with the route already
                // known, including the very first turn.  Direct answers now
                // create an App Server thread with zero product tools.
                var threadId = await _agentClient.CreateThreadAsync(_currentTurnRoute);
                if (submittedTask is not null)
                {
                    submittedTask.ThreadId = threadId;
                }
                if (!IsCurrentSubmission()) return;
            }

            StopButton.Visibility = Visibility.Visible;
            SendButton.IsEnabled = true;
            SendButton.Content = L("追加");
            CurrentStatusText.Text = L(isSteering ? "已追加新要求，正在调整任务" : "正在理解任务");
            if (!isSteering)
            {
                _taskStartedAt = DateTimeOffset.Now;
                _lastTaskDuration = null;
                _completedActionTitles.Clear();
            }
            if (_currentIntentResolution is { RequiresComputer: true } &&
                (_currentTurnRoute == AgentTaskRoute.ComputerControl ||
                 _currentTurnRoute == AgentTaskRoute.Composite ||
                 ShouldObserveScreen(displayText)))
            {
                // Let the task tools activate and inspect the intended target
                // window. Hiding the whole HuiTian window just to take an
                // eager screenshot caused a distracting disappear/reappear
                // flash and often captured the wrong application.
                agentText += "\n本任务需要界面操作：请先检查并激活目标窗口，再主动观察当前画面；每次界面变化后重新观察。";
            }

            ClearAttachments();
            if (isSteering)
            {
                await _agentClient.SteerAsync(agentText, imagePaths);
            }
            else if (imagePaths.Count > 0)
            {
                await _agentClient.SendMessageWithAttachmentsAsync(agentText, imagePaths);
            }
            else
            {
                await _agentClient.SendMessageAsync(agentText);
            }
            // Steering from a tool-free answer can replace its backend
            // thread. Persist the new identity on the submitted task, not
            // whichever task the user may have selected while awaiting it.
            if (submittedTask is not null)
            {
                submittedTask.ThreadId = _agentClient.CurrentThreadId;
                await SaveStateQuietlyAsync();
            }
        }
        catch (Exception)
        {
            if (!IsCurrentSubmission()) return;
            SetTaskIdle("任务未能开始");
            CompleteOpenSubtasks(succeeded: false, "任务连接未能建立");
            AddLocalAssistantMessage("刚才的要求已经保留。回天正在重建本机任务连接，你可以直接继续发送。 ");
            _voiceController.ResumeListening();
        }
    }

    private static TaskGraph CreateTaskGraphSafely(IntentResolution intent)
    {
        try
        {
            return TaskGraph.FromIntent(intent);
        }
        catch (ArgumentException)
        {
            var target = intent.Entities
                .OrderByDescending(entity => entity.Confidence)
                .Select(entity => entity.EffectiveValue)
                .FirstOrDefault();
            return new TaskGraph(
                Guid.NewGuid().ToString("N"),
                intent.Goal,
                [
                    new IntentPlanStep(
                        "complete-goal",
                        intent.Goal,
                        intent.Actions.FirstOrDefault() ?? "complete-goal",
                        intent.Applications.FirstOrDefault(),
                        target,
                        [],
                        intent.SuccessConditions,
                        intent.Risk),
                ]);
        }
    }

    private bool AllowsProductFastPath(params IntentExecutionDomain[] domains)
    {
        if (_currentIntentResolution is null)
        {
            if (_currentSemanticIntent is not { IsConfident: true, Mode: SemanticIntentMode.Operate } decision)
                return !_brainReady;
            var domain = decision.Domain switch
            {
                SemanticIntentDomain.Application => IntentExecutionDomain.Other,
                SemanticIntentDomain.System => IntentExecutionDomain.System,
                SemanticIntentDomain.Browser => IntentExecutionDomain.Browser,
                SemanticIntentDomain.Media => IntentExecutionDomain.Media,
                _ => IntentExecutionDomain.Answer,
            };
            return domains.Contains(domain);
        }

        return _currentIntentResolution.RequiresComputer &&
               domains.Contains(_currentIntentResolution.Domain);
    }

    private string BuildAgentText(
        string displayText,
        IReadOnlyList<string> selectedFiles,
        IReadOnlyList<ConversationMessageSnapshot> priorMessages,
        TaskContextBootstrapSnapshot? bootstrapContext = null)
    {
        var text = string.Empty;
        // Every completed App Server turn starts from a clean, bounded thread.
        // Always carry the latest exchange so natural conversation, emotion,
        // corrections and omitted objects remain understandable even when the
        // user does not literally say “刚才” or “这个”. The current request is
        // still placed last and remains authoritative.
        var context = priorMessages.Count > 0
            ? ConversationPolicy.BuildResumeContext(
                priorMessages,
                _currentTurnRoute == AgentTaskRoute.DirectAnswer ? 900 : 1600)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(context))
        {
            text += "以下是本对话最近几条内容，仅用于理解指代，不代表本轮仍要执行：\n" +
                    context + "\n\n";
        }

        if (selectedFiles.Count > 0)
        {
            text += "用户通过“+文件”明确附加了以下本机文件。需要读取文字或 Office 文件时使用文档读取能力；图像已随任务发送：\n" +
                    string.Join("\n", selectedFiles.Select(path => "- " + path)) + "\n\n";
        }

        if (bootstrapContext is not null)
        {
            text += bootstrapContext.FormatForAgent() + "\n\n";
        }

        text += _currentTurnRoute == AgentTaskRoute.DirectAnswer
            ? _state.Language switch
            {
                "en-US" => "\n\nAnswer in English.",
                "ja-JP" => "\n\n日本語で回答してください。",
                _ => "\n\n只用简体中文回答。",
            }
            : _state.Language switch
            {
                "en-US" => "\n\nThe whole product language is English. Use English for every user-visible plan, progress note, tool explanation, and answer; keep only proper nouns in their original language.",
                "ja-JP" => "\n\n製品全体の言語は日本語です。計画、進捗、操作説明、回答など、ユーザーに見えるすべての文章を日本語で書いてください。固有名詞だけ原文のままで構いません。",
                _ => "\n\n产品当前语言是简体中文。计划、思考摘要、操作说明和答复等所有面向用户的文字必须使用简体中文；专有名词除外，不得无故切换成英文。",
            };
        var memory = BuildRelevantMemory(
            displayText,
            includeLearnedPaths: _currentTurnRoute != AgentTaskRoute.DirectAnswer);
        if (!string.IsNullOrWhiteSpace(memory))
        {
            text += "\n\n以下是只保存在本机的用户偏好与成功经验，请在不违背本次明确要求的前提下优先采用：\n" + memory;
        }

        if (_currentIntentResolution is { } semanticIntent)
        {
            text += "\n\n【结构化意图】\n" + semanticIntent.FormatForAgent();
            text += semanticIntent.TaskType switch
            {
                SemanticTaskType.ComputerAction or SemanticTaskType.ComputerTask =>
                    "\n\n本轮语义判定为真实操作请求。用户期待电脑或应用产生可核验变化，不能只给文字建议。",
                SemanticTaskType.Hybrid or SemanticTaskType.MultiAppTask =>
                    "\n\n本轮语义判定为复合请求。先完成必要的理解、查询或内容组织，再继续完成文件/应用/发送等真实动作，不得中途停在文字草稿。",
                SemanticTaskType.Clarify =>
                    "\n\n本轮信息不足以安全确定唯一目标。只询问一个最关键的缺失信息，不要进行猜测性操作。",
                _ =>
                    "\n\n本轮语义判定为对话回答，不要因为提到了软件或文件名就擅自操作电脑。",
            };
        }

        if (_currentCommunicationTargets is { } communicationTargets &&
            _currentTurnRoute != AgentTaskRoute.DirectAnswer)
        {
            var platforms = communicationTargets.Platforms.Count == 0
                ? "未确定" : string.Join("、", communicationTargets.Platforms);
            var targets = communicationTargets.TargetNames.Count == 0
                ? "尚未选定" : string.Join("、", communicationTargets.TargetNames);
            text += $"\n\n【本轮社交目标槽位】平台：{platforms}；已指明对象：{targets}；" +
                    $"寻找新对象：{communicationTargets.FindNewPeople}；" +
                    $"排除对象或关系：{string.Join("、", communicationTargets.ExcludedNames)}；" +
                    $"择人条件：{communicationTargets.Criteria ?? "未指定"}。" +
                    "这些名称只是用户意图，不是已核验的账号。必须先核对站内真实资料、唯一身份和聊天资格；" +
                    "付费才能聊天的对象直接跳过，不要购买或绕过；不得向家人、用户明确排除的人、身份或年龄不明的人开展恋爱搭讪。" +
                    "若需要扫码、验证码或本人登录，立即请用户接手；未登录不得声称已经聊天。" +
                    "每个对象分别保存上下文，不得把一个人的回复当成另一个人的回复；发送后核对会话标题和新消息，无法核对则停下。" +
                    "语气依据用户真实聊天样本，不能编造个人经历、承诺关系结果或群发相同话术。";
            if (communicationTargets.FindNewPeople)
                text += SocialDiscoveryExecutionContract.BuildAgentGuidance(
                    communicationTargets, _explicitSocialStyleSamples.Count > 0) +
                    "\n进入草稿阶段前，先用 huitian_read_social_candidates 查已筛选的候选；" +
                    "从真实资料页取得稳定账号 ID 与核验证据，再调用 huitian_screen_social_candidate。" +
                    "返回 skip 就换人；候选记录仅代表已筛选，绝不等于已联系或已回复。";
            if (_explicitSocialStyleSamples.Count > 0)
            {
                text += "\n【用户本人提供的说话样本，仅用于语气，不得原样群发】\n" +
                        string.Join("\n", _explicitSocialStyleSamples.Take(6).Select(sample =>
                            "- " + (sample.Length <= 180 ? sample : sample[..180])));
            }
        }

        if (_currentTurnRoute != AgentTaskRoute.DirectAnswer &&
            _currentIntentResolution is not null)
        {
            var executionContract = _currentIntentResolution.ToComputerTaskContract();
            text += "\n\n【可执行任务合同】\n" +
                    executionContract.FormatForAgent() +
                    "\n以合同中的最终目标和完成证据为验收标准。先选最快的结构化通道；同一通道两次没有取得完成证据后必须换通道，不能原样重复。";
            var verifiedPaths = ExecutionMemoryStore.Default
                .Find(executionContract, 4)
                .Where(entry => entry.IsTrusted)
                .ToArray();
            if (verifiedPaths.Length > 0)
            {
                text += "\n\n【同类任务已核验路径】\n" + string.Join(
                    "\n",
                    verifiedPaths.Select(entry =>
                        $"- app={entry.App}; operation={entry.Operation}; channel={entry.Channel}; " +
                        $"strategy={entry.StrategyId}; success={entry.VerifiedSuccesses}; " +
                        $"confidence={entry.Confidence:0.00}; avg_ms={entry.AverageDurationMilliseconds:0}")) +
                    "\n应用版本和当前状态匹配时优先复用；若核验失败，立即降级到下一种通道。";
            }
            if (_currentTaskGraph is not null)
            {
                text += "\n\n【持久任务图】\n" +
                        _currentTaskGraph.FormatForAgent() +
                        "\n只执行依赖已经完成的 ready 步骤；每一步必须观察并核验成功证据后才能解锁下一步。失败时保留已完成结果并更换执行通道。";
            }
        }
        else if (_currentTurnRoute != AgentTaskRoute.DirectAnswer)
        {
            text += "\n\n【通用语义入口】\n先判断用户最终能使用、打开、打印、编辑、观看或收到什么；" +
                    "必须通过通用语义出口声明完整目标和全部最终交付物。纯对话结果选择 respond；需要电脑产生真实变化时选择当前最能推进最终交付物的能力。" +
                    "搜索、阅读、计划和草稿只是中间步骤，不能据此结案；不要用用户原句做关键词匹配，也不要把真实执行要求降成教程或聊天文字。";
        }

        if (_currentIntentResolution is
            {
                Domain: IntentExecutionDomain.Communication,
                RequiresComputer: true,
            } communicationIntent)
        {
            var applications = communicationIntent.Applications.Count > 0
                ? string.Join("、", communicationIntent.Applications)
                : "当前通讯应用";
            text += $"\n\n本次是通讯任务，目标应用为：{applications}。先检查任务栏、进程和当前窗口并激活已运行实例，确认没有运行时才启动新实例。" +
                    "优先使用应用适配器和无障碍控件；结构化信息不足时才截图观察。定位联系人后重新观察并核对会话标题。" +
                    "输入消息后必须再次观察，确认文字真实出现在聊天输入框内，发送后再确认聊天区出现新消息；未经这两次复核不得声称输入或发送成功。" +
                    "不得打开记事本或其他无关程序试验。";
        }

        // Put the current request last so a small local model cannot confuse
        // an older answer or a learned path with the user's newest request.
        text += "\n\n" + (_currentIntentResolution is null && _currentTurnRoute != AgentTaskRoute.DirectAnswer
            ? ConversationPolicy.BuildUniversalTurnContract(displayText)
            : ConversationPolicy.BuildTurnContract(displayText, _currentTurnRoute));

        return text;
    }

    private async Task<bool> TryHandleApplicationFastPathAsync(string displayText)
    {
        var parsed = ConversationPolicy.TryParseApplicationLaunch(
            displayText,
            out var intent);
        if (!parsed &&
            _currentIntentResolution is
            {
                TaskType: SemanticTaskType.ComputerAction,
                Applications.Count: 1,
            } semanticIntent &&
            semanticIntent.Actions.Any(action => action is
                "open" or "launch" or "activate" or "switch-to"))
        {
            intent = new ApplicationLaunchIntent(semanticIntent.Applications[0]);
            parsed = true;
        }

        if (!parsed || _currentTask is null)
        {
            return false;
        }

        var task = _currentTask;
        var startedAt = DateTimeOffset.Now;
        var activityId = $"application-fast-{Guid.NewGuid():N}";
        _taskStartedAt = startedAt;
        _lastTaskDuration = null;
        CurrentStatusText.Text = L($"正在打开{intent.Application}");
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            $"正在打开{intent.Application}",
            "正在查找已打开窗口或本机已安装程序",
            AgentActivityStatus.InProgress,
            startedAt));

        ApplicationLaunchFastPathResult result;
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            result = await _applicationFastPath.OpenAsync(intent.Application, budget.Token);
        }

        _lastTaskDuration = TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
        var answer = result.Success
            ? result.Detail
            : $"没有成功打开“{intent.Application}”。{result.Detail}";
        CompleteTaskSubtasks(task, result.Success, answer);
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            result.Success ? $"已打开{intent.Application}" : $"未能打开{intent.Application}",
            result.Success ? "已完成快速打开" : result.Detail,
            result.Success ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
            startedAt));
        _ = _currentPerformanceSession?.CompleteAsync(result.Success);

        if (ReferenceEquals(_currentTask, task))
        {
            AddLocalAssistantMessage(answer);
            SetTaskIdle(result.Success ? "程序已打开" : "程序未打开");
            await SaveCurrentTaskAsync();
        }
        else
        {
            task.Messages.Add(new SavedMessage
            {
                Role = "assistant",
                Text = ConversationPolicy.SanitizeAssistantText(answer),
                Timestamp = DateTimeOffset.Now,
            });
            await SaveDetachedTaskAsync(task);
        }

        if (result.Success)
        {
            _completedActionTitles.Add($"快速打开{intent.Application}");
            RememberTaskLearning(succeeded: true);
        }

        _voiceController.ResumeListening();
        return true;
    }

    private async Task<bool> TryHandleEverydayFastPathAsync(string displayText)
    {
        if (!EverydayTaskParser.TryParse(displayText, out var intent) ||
            _currentTask is null)
        {
            return false;
        }

        var task = _currentTask;
        var startedAt = DateTimeOffset.Now;
        var activityId = $"everyday-fast-{Guid.NewGuid():N}";
        _taskStartedAt = startedAt;
        _lastTaskDuration = null;
        CurrentStatusText.Text = L(intent.DisplayName);
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            intent.DisplayName,
            "正在使用本机快捷能力",
            AgentActivityStatus.InProgress,
            startedAt));

        EverydayActionResult result;
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            result = await _everydayFastPath.ExecuteAsync(intent, budget.Token);
        }

        _lastTaskDuration = TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
        CompleteTaskSubtasks(task, result.Success, result.Detail);
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            result.Success ? intent.DisplayName : $"未能{intent.DisplayName}",
            result.Detail,
            result.Success ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
            startedAt));
        _ = _currentPerformanceSession?.CompleteAsync(result.Success);

        if (ReferenceEquals(_currentTask, task))
        {
            AddLocalAssistantMessage(result.Detail);
            SetTaskIdle(result.Success ? "操作已完成" : "操作未完成");
            await SaveCurrentTaskAsync();
        }
        else
        {
            task.Messages.Add(new SavedMessage
            {
                Role = "assistant",
                Text = ConversationPolicy.SanitizeAssistantText(result.Detail),
                Timestamp = DateTimeOffset.Now,
            });
            await SaveDetachedTaskAsync(task);
        }

        if (result.Success)
        {
            _completedActionTitles.Add(intent.DisplayName);
            RememberTaskLearning(succeeded: true);
        }

        _voiceController.ResumeListening();
        return true;
    }

    private async Task<MediaConsumptionIntent?> TryPrimeMediaFastPathAsync(string displayText)
    {
        if (!MediaConsumptionIntentParser.TryParse(displayText, out var intent) ||
            _currentTask is null)
        {
            return null;
        }

        var startedAt = DateTimeOffset.Now;
        var activityId = $"media-fast-{Guid.NewGuid():N}";
        _taskStartedAt = startedAt;
        _lastTaskDuration = null;
        CurrentStatusText.Text = L(intent.DisplayName);
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            intent.DisplayName,
            "正在查找大陆可直接打开的正版来源",
            AgentActivityStatus.InProgress,
            startedAt));

        MediaConsumptionResult result;
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            result = await _mediaConsumptionFastPath.ExecuteAsync(intent, budget.Token);
        }

        _lastTaskDuration = TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            result.Success ? "正版来源搜索已打开" : "媒体来源搜索未能打开",
            result.Success
                ? "搜索页已经出现，回天将继续选择官方结果并核验播放器"
                : result.Detail,
            result.Success ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
            startedAt));

        if (result.Success)
        {
            _completedActionTitles.Add("快速打开正版来源搜索");
            return intent;
        }

        return null;
    }

    private async Task<bool> TryHandleCompositeFastPathAsync(string displayText)
    {
        if (!ConversationPolicy.TryParseCompositeTask(displayText, out var intent) ||
            _currentTask is null)
        {
            return false;
        }

        var task = _currentTask;
        var startedAt = DateTimeOffset.Now;
        var activityId = $"composite-fast-{Guid.NewGuid():N}";
        _taskStartedAt = startedAt;
        _lastTaskDuration = null;
        _userRequestedStop = false;
        var quickTaskCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _quickTaskCancellation = quickTaskCancellation;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        SendButton.IsEnabled = true;
        SendButton.Content = L("追加");
        CurrentStatusText.Text = L("正在并行查询资料");

        var progress = new Progress<CompositeWorkflowProgress>(update =>
        {
            // A user may start another task while this workflow continues.
            // Never pull the view back to the background task.
            if (!ReferenceEquals(_currentTask, task))
            {
                return;
            }

            if (update.Stage == CompositeWorkflowStage.Delivery)
            {
                var overlay = EnsureControlOverlay();
                overlay.ApplyLanguage(_state.Language);
                overlay.ShowControl(L($"回天正在操作{intent.Platform}"));
            }

            AddOrUpdateActivity(new AgentActivity(
                activityId + "-" + update.Stage,
                AgentActivityKind.Tool,
                update.Title,
                update.Detail,
                update.Stage == CompositeWorkflowStage.Completed
                    ? AgentActivityStatus.Completed
                    : AgentActivityStatus.InProgress,
                startedAt));
        });

        CompositeWorkflowResult result;
        try
        {
            result = await _compositeFastPath.RunAsync(
                intent,
                progress,
                quickTaskCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            result = new CompositeWorkflowResult(
                CompositeWorkflowStatus.Failed,
                "本次任务已停止；已经生成的文件会保留，也不会在后台继续发送。",
                null,
                (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds);
        }
        finally
        {
            // The user may have detached this task and started another fast
            // task while this one was completing.  Never dispose or clear the
            // newer foreground task's cancellation source from this older
            // continuation.
            if (ReferenceEquals(_quickTaskCancellation, quickTaskCancellation))
            {
                _quickTaskCancellation = null;
            }

            quickTaskCancellation.Dispose();
        }

        // If direct extraction was unavailable, hand the untouched request to
        // the general agent skeleton. This is the only case where the fast
        // path deliberately declines ownership.
        if (result.Status == CompositeWorkflowStatus.FastResearchUnavailable &&
            ReferenceEquals(_currentTask, task))
        {
            AddOrUpdateActivity(new AgentActivity(
                activityId,
                AgentActivityKind.Status,
                "快速资料通道暂不可用，正在切换普通联网流程",
                "任务内容已经保留，不需要重新输入",
                AgentActivityStatus.Completed,
                startedAt));
            return false;
        }

        _lastTaskDuration = TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
        var succeeded = result.Status == CompositeWorkflowStatus.Completed;
        if (ReferenceEquals(_currentTask, task) &&
            result.DeliveryResult is { PointerX: { } pointerX, PointerY: { } pointerY })
        {
            EnsureControlOverlay().ShowPointer(pointerX, pointerY);
        }

        if (result.ArtifactCreated && result.ArtifactPath is { } artifactPath)
        {
            task.Artifacts.RemoveAll(path =>
                path.Equals(artifactPath, StringComparison.OrdinalIgnoreCase));
            task.Artifacts.Add(artifactPath);
        }

        if (ReferenceEquals(_currentTask, task))
        {
            CompleteTaskSubtasks(task, succeeded, result.Message);
            if (result.ArtifactCreated && result.ArtifactPath is { } visibleArtifactPath &&
                _artifactPaths.Add(visibleArtifactPath))
            {
                AddInlineArtifactCard(visibleArtifactPath);
                RenderArtifacts();
            }

            AddOrUpdateActivity(new AgentActivity(
                activityId,
                AgentActivityKind.Status,
                succeeded ? "复合任务已完成" : "复合任务部分完成",
                result.ArtifactCreated
                    ? $"产物已保存在：{result.ArtifactPath}"
                    : result.Message,
                succeeded ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
                startedAt));
            AddLocalAssistantMessage(result.Message);
            SetTaskIdle(succeeded ? "任务已完成" : "任务部分完成");
        }
        else
        {
            CompleteTaskSubtasks(task, succeeded, result.Message);
            task.Messages.Add(new SavedMessage
            {
                Role = "assistant",
                Text = ConversationPolicy.SanitizeAssistantText(result.Message),
                Timestamp = DateTimeOffset.Now,
            });
            await SaveDetachedTaskAsync(task);
            if (ReferenceEquals(_backgroundTask, task))
            {
                _backgroundTask = null;
                _backgroundIntentResolution = null;
                _backgroundTaskGraph = null;
                _detachedFromActiveTurn = false;
                _backgroundSavedActivityIds.Clear();
            }
        }

        _ = _currentPerformanceSession?.CompleteAsync(succeeded);

        if (succeeded)
        {
            _completedActionTitles.Add("并行查资料、生成演示文稿并快速发送");
            RememberTaskLearning(succeeded: true);
        }

        return true;
    }

    private async Task<bool> TryHandleChatFastPathAsync(
        string displayText,
        IReadOnlyList<string> selectedFiles)
    {
        if (_currentTask is null)
        {
            return false;
        }

        var hasFiles = selectedFiles.Any(File.Exists);
        var semanticDecision = _currentSemanticIntent;
        if (semanticDecision is not { IsConfident: true, Mode: SemanticIntentMode.Operate, Domain: SemanticIntentDomain.Chat })
            return false;
        ChatFastPathIntent intent;
        // Semantic slots are authoritative. The legacy sentence parser is not
        // the production gate for deciding who, what, or whether to keep chatting.
        {
            CurrentStatusText.Text = L("正在理解沟通目标");
            var taskBeforeResolution = _currentTask;
            var generationBeforeResolution = _submissionGeneration;
            var semanticContext = taskBeforeResolution.Messages
                .Where(message => !string.IsNullOrWhiteSpace(message.Text))
                .TakeLast(4)
                .Select(message => new ConversationMessageSnapshot(
                    message.Role,
                    message.Text,
                    message.Attachments.Where(File.Exists).ToArray()))
                .ToArray();
            using var resolutionCancellation = new CancellationTokenSource();
            _intentCancellation = resolutionCancellation;
            CommunicationIntentResolution resolution;
            var resolutionWatch = Stopwatch.StartNew();
            var performance = _currentPerformanceSession;
            var reusedSemanticSlots = CommunicationIntentResolver.TryResolveFromSemantic(
                semanticDecision, displayText, out resolution);
            try
            {
                if (!reusedSemanticSlots)
                {
                    resolution = await _communicationIntentResolver.ResolveDetailedAsync(
                        displayText, semanticContext, hasFiles, resolutionCancellation.Token);
                }
            }
            catch (OperationCanceledException) { return true; }
            finally
            {
                performance?.RecordPhase(
                    reusedSemanticSlots ? "communication_slots_reused" : "communication_slots",
                    resolutionWatch.ElapsedMilliseconds);
                if (!reusedSemanticSlots) performance?.RecordLlmCall();
                if (ReferenceEquals(_intentCancellation, resolutionCancellation))
                    _intentCancellation = null;
            }
            if (!ReferenceEquals(_currentTask, taskBeforeResolution) || resolutionCancellation.IsCancellationRequested ||
                generationBeforeResolution != _submissionGeneration)
                return true;
            _currentCommunicationTargets = resolution.Targets;
            if (resolution.FastPathIntent is null)
            {
                if (resolution.Targets is { } targets)
                {
                    if (await TryHandleMultiChatFastPathAsync(targets, taskBeforeResolution))
                        return true;
                    AddOrUpdateActivity(new AgentActivity(
                        $"social-targets-{Guid.NewGuid():N}",
                        AgentActivityKind.Status,
                        "已理解沟通对象与平台",
                        targets.FindNewPeople
                            ? "需要先核对新对象及平台条件，尚未向任何人发送消息"
                            : "多个对象或平台不能共用当前会话发送，已转入逐个核对流程",
                        AgentActivityStatus.Completed,
                        DateTimeOffset.Now));
                    if (targets.FindNewPeople)
                    {
                        _socialDiscoveryTasks[taskBeforeResolution.Id] = new SocialDiscoveryTaskContext(
                            targets, DateTimeOffset.UtcNow);
                        await TryPrimeSocialPlatformAsync(targets);
                        AddOrUpdateActivity(new AgentActivity(
                            $"social-entry-check-{Guid.NewGuid():N}",
                            AgentActivityKind.Status,
                            "已进入平台核对阶段",
                            "接下来检查实际登录状态、候选资料和可用聊天功能；公开页面不是完成证据",
                            AgentActivityStatus.Completed,
                            DateTimeOffset.Now));
                    }
                }

                return false;
            }

            intent = resolution.FastPathIntent;
        }

        var task = _currentTask;
        var files = intent.ReferencesFiles
            ? ResolveChatFastPathFiles(intent, selectedFiles)
            : [];
        if (intent.ReferencesFiles && files.Count == 0)
        {
            const string missing = "没有找到您所指的成品文件。请把文件拖进输入框，或在任务产物中明确选择后再发送。";
            CompleteOpenSubtasks(succeeded: false, missing);
            AddLocalAssistantMessage(missing);
            SetTaskIdle("等待您选择文件");
            await SaveCurrentTaskAsync();
            _voiceController.ResumeListening();
            return true;
        }

        var destination = intent.UseCurrentConversation
            ? $"{intent.PlatformName}当前会话"
            : $"{intent.PlatformName}联系人“{intent.Recipient}”";
        var conversationPlan = files.Count == 0
            ? intent.CommunicationPlan
            : null;
        if (conversationPlan is not null)
        {
            conversationPlan = conversationPlan with
            {
                UserStyleSamples = _explicitSocialStyleSamples,
            };
        }
        var continuousConversation =
            conversationPlan?.RequiresReplyObservation == true;
        var activityId = $"chat-fast-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.Now;
        _taskStartedAt = startedAt;
        _lastTaskDuration = null;
        CurrentStatusText.Text = L($"正在发送到{destination}");
        SendButton.IsEnabled = continuousConversation;
        SendButton.Content = L(continuousConversation ? "追加" : "发送");
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        var controlOverlay = EnsureControlOverlay();
        controlOverlay.ApplyLanguage(_state.Language);
        controlOverlay.ShowControl(L($"回天正在操作{intent.PlatformName}"));
        AddOrUpdateActivity(new AgentActivity(
            activityId,
            AgentActivityKind.Tool,
            $"正在发送到{destination}",
            intent.ReferencesFiles ? "正在核对联系人和文件" : "正在核对联系人和消息",
            AgentActivityStatus.InProgress,
            startedAt));

        ChatFastPathResult? result = null;
        CommunicationSessionResult? sessionResult = null;
        var answer = "本次操作没有完成。";
        var activityDetail = "本次操作没有完成";
        var succeeded = false;
        var semanticOpeningUnavailable = false;
        var chatPerformance = _currentPerformanceSession;
        using var conversationCancellation = new CancellationTokenSource(
            continuousConversation && conversationPlan is not null
                ? conversationPlan.MaximumDuration + TimeSpan.FromMinutes(2)
                : TimeSpan.FromSeconds(60));
        var sessionKey = continuousConversation
            ? intent.PlatformName + "\u001f" + (intent.Recipient ?? "@current")
            : null;
        ActiveCommunicationSession? activeSession = null;
        if (sessionKey is not null)
        {
            activeSession = new ActiveCommunicationSession(task.Id, conversationCancellation);
            if (!_activeCommunicationSessions.TryAdd(sessionKey, activeSession))
            {
                const string alreadyActive = "这个联系人已有一轮持续聊天在运行。为避免重复或串发，本次没有启动第二轮；请先停止原会话再重新安排。";
                CompleteTaskSubtasks(task, false, alreadyActive);
                AddLocalAssistantMessage(alreadyActive);
                SetTaskIdle("会话已在运行");
                await SaveCurrentTaskAsync();
                return true;
            }
        }
        _quickTaskCancellation = conversationCancellation;
        Task<ApplicationLaunchFastPathResult>? preparingApplication = null;
        async Task<ApplicationLaunchFastPathResult> PrepareApplicationAsync()
        {
            var preparationStarted = DateTimeOffset.Now;
            AddOrUpdateActivity(new AgentActivity(activityId + "-application", AgentActivityKind.Tool,
                $"正在打开{intent.PlatformName}", "优先调出已打开窗口，同时准备消息", AgentActivityStatus.InProgress, preparationStarted));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(conversationCancellation.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            var launch = await _applicationFastPath.OpenAsync(intent.PlatformName, limit.Token);
            chatPerformance?.RecordPhase("chat_application", launch.ElapsedMilliseconds);
            if (ReferenceEquals(_currentTask, task) && !conversationCancellation.IsCancellationRequested)
                AddOrUpdateActivity(new AgentActivity(activityId + "-application", AgentActivityKind.Tool,
                    launch.Success ? $"已打开{intent.PlatformName}" : "应用准备未完成",
                    launch.Detail, launch.Success ? AgentActivityStatus.Completed : AgentActivityStatus.Failed, DateTimeOffset.Now));
            return launch;
        }
        try
        {
            // App activation is independent of message composition. Do not wait
            // for the model before showing the requested app. This never sends.
            if (!intent.UseCurrentConversation) preparingApplication = PrepareApplicationAsync();
            if (conversationPlan is not null &&
                intent.MessageComposedFromGoal &&
                CommunicationOpeningPolicy.RequiresSemanticComposition(conversationPlan))
            {
                var openingStartedAt = DateTimeOffset.Now;
                CurrentStatusText.Text = L("正在理解身份和沟通目的");
                AddOrUpdateActivity(new AgentActivity(
                    activityId + "-opening",
                    AgentActivityKind.Reasoning,
                    "正在拟定自然开场",
                    "身份和目的只用于指导措辞，不会原样发给联系人",
                    AgentActivityStatus.InProgress,
                    openingStartedAt));
                var opening = await _communicationTurnComposer.ComposeOpeningAsync(
                        conversationPlan,
                        conversationCancellation.Token);
                conversationCancellation.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(opening))
                {
                    semanticOpeningUnavailable = true;
                    answer = "这次没有可靠生成适合当前身份和目的的自然开场，因此没有向联系人发送内部指令或不确定内容。请确认本机智能核心已就绪后重试。";
                    activityDetail = "自然开场没有生成，已阻止内部沟通目的被直接外发";
                    AddOrUpdateActivity(new AgentActivity(
                        activityId + "-opening",
                        AgentActivityKind.Reasoning,
                        "自然开场未能准备",
                        activityDetail,
                        AgentActivityStatus.Failed,
                        DateTimeOffset.Now));
                }
                else
                {
                    conversationPlan = conversationPlan with
                    {
                        OpeningMessage = opening,
                        OpeningIsSemanticallyComposed = true,
                    };
                    intent = intent with
                    {
                        Message = opening,
                        CommunicationPlan = conversationPlan,
                    };
                    AddOrUpdateActivity(new AgentActivity(
                        activityId + "-opening",
                        AgentActivityKind.Reasoning,
                        "自然开场已准备",
                        "已选用本次不重复且不泄露内部目的的自然开场",
                        AgentActivityStatus.Completed,
                        DateTimeOffset.Now));
                }
            }

            if (preparingApplication is not null) await preparingApplication;
            conversationCancellation.Token.ThrowIfCancellationRequested();
            if (semanticOpeningUnavailable)
            {
                _lastTaskDuration = DateTimeOffset.Now - startedAt;
            }
            else if (continuousConversation && conversationPlan is not null)
            {
                CurrentStatusText.Text = L($"正在与{destination}持续沟通");
                var progressSequence = 0;
                string? liveProgressId = null;
                string? liveProgressStage = null;
                string? liveProgressDetail = null;
                var liveProgressStartedAt = DateTimeOffset.Now;
                var initialSendCompleted = false;
                var conversationWatchdogStarted = false;

                var communicationProgress = new Progress<CommunicationSessionProgress>(update =>
                {
                    if (!conversationWatchdogStarted && update.OutgoingMessageCount > 0 &&
                        ReferenceEquals(_quickTaskCancellation, conversationCancellation) &&
                        !conversationCancellation.IsCancellationRequested)
                    {
                        conversationWatchdogStarted = true;
                        // Also applies while the user is viewing a different task.
                        conversationCancellation.CancelAfter(conversationPlan.MaximumDuration + TimeSpan.FromSeconds(15));
                    }
                    if (!ReferenceEquals(_currentTask, task))
                    {
                        return;
                    }

                    CurrentStatusText.Text = L(update.Stage);
                    var now = DateTimeOffset.Now;
                    if (!initialSendCompleted &&
                        update.OutgoingMessageCount > 0 &&
                        update.Stage == "等待回复")
                    {
                        initialSendCompleted = true;
                        AddOrUpdateActivity(new AgentActivity(
                            activityId,
                            AgentActivityKind.Tool,
                            "开场发送步骤已结束",
                            update.Detail,
                            AgentActivityStatus.Completed,
                            now));
                    }

                    if (!string.Equals(liveProgressStage, update.Stage, StringComparison.Ordinal))
                    {
                        if (liveProgressId is not null)
                        {
                            AddOrUpdateActivity(new AgentActivity(
                                liveProgressId,
                                AgentActivityKind.Reasoning,
                                liveProgressStage ?? "处理完成",
                                liveProgressDetail ?? string.Empty,
                                AgentActivityStatus.Completed,
                                now));
                        }

                        liveProgressStage = update.Stage;
                        liveProgressDetail = $"{update.Detail} · 已发送 {update.OutgoingMessageCount} 条";
                        liveProgressStartedAt = now;
                        liveProgressId = $"{activityId}-conversation-{++progressSequence}";
                    }
                    else
                    {
                        liveProgressDetail = $"{update.Detail} · 已发送 {update.OutgoingMessageCount} 条";
                    }

                    AddOrUpdateActivity(new AgentActivity(
                        liveProgressId!,
                        AgentActivityKind.Reasoning,
                        update.Stage,
                        liveProgressDetail,
                        AgentActivityStatus.InProgress,
                        liveProgressStartedAt));
                });

                sessionResult = await _communicationExecutor.ExecuteAsync(
                    conversationPlan,
                    new FastCommunicationTurnComposer(_communicationTurnComposer),
                    continueWhenRepliesArrive: true,
                    cancellationToken: conversationCancellation.Token,
                    progress: communicationProgress);
                if (liveProgressId is not null)
                {
                    AddOrUpdateActivity(new AgentActivity(
                        liveProgressId,
                        AgentActivityKind.Reasoning,
                        liveProgressStage ?? "持续聊天已结束",
                        liveProgressDetail ?? sessionResult.Detail,
                        sessionResult.Status == CommunicationSessionStatus.Completed
                            ? AgentActivityStatus.Completed
                            : AgentActivityStatus.Failed,
                        DateTimeOffset.Now));
                }
                result = sessionResult.LastTransportResult;
                succeeded = sessionResult.Status == CommunicationSessionStatus.Completed;
                answer = BuildCommunicationSessionAnswer(sessionResult);
                activityDetail = sessionResult.Detail;
            }
            else
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(conversationCancellation.Token);
                budget.CancelAfter(TimeSpan.FromSeconds(10));
                result = await _chatFastPath.SendAsync(
                    intent.PlatformName,
                    intent.Recipient ?? string.Empty,
                    intent.Message,
                    files,
                    budget.Token);
                succeeded = result.Success;
                answer = BuildChatFastPathAnswer(intent, files, result);
                activityDetail = BuildChatFastPathActivityDetail(result);
            }

            if (result is { PointerX: { } pointerX, PointerY: { } pointerY })
            {
                controlOverlay.ShowPointer(pointerX, pointerY);
            }

            if (!semanticOpeningUnavailable)
            {
                _lastTaskDuration = continuousConversation
                    ? DateTimeOffset.Now - startedAt
                    : TimeSpan.FromMilliseconds(result?.ElapsedMilliseconds ?? 0);
            }
        }
        catch (OperationCanceledException)
        {
            _lastTaskDuration = DateTimeOffset.Now - startedAt;
            var sentCount = sessionResult?.OutgoingMessageCount ?? 0;
            answer = continuousConversation
                ? $"本轮持续聊天已停止，共发送 {sentCount} 条消息；不会在后台继续发送。"
                : conversationCancellation.IsCancellationRequested
                    ? "本次操作已停止，尚未获得可确认的发送结果，不会自动重复发送。"
                    : $"这次没有在 10 秒内安全核对好{destination}，因此没有继续发送，也不会在后台重复操作。请确认{intent.PlatformName}已登录并保持主窗口可见后再试。";
            activityDetail = continuousConversation
                ? "持续聊天已停止"
                : conversationCancellation.IsCancellationRequested ? "操作已停止" : "联系人定位超过 10 秒，发送没有执行";
        }
        catch (Exception exception)
        {
            _lastTaskDuration = DateTimeOffset.Now - startedAt;
            answer = $"没有完成向{destination}的发送。本次已停止，不会重复发送；请确认软件已登录且联系人名称完整。";
            activityDetail = "快速发送通道出现异常，具体原因已写入诊断记录";
            Trace.WriteLine($"Chat fast path failed: {exception}");
        }
        finally
        {
            conversationCancellation.Cancel();
            // Observe the parallel operation even when composition was stopped.
            if (preparingApplication is not null) await preparingApplication;
            if (continuousConversation && conversationPlan is not null)
            {
                await _chatConversationTransport.EndSessionAsync(
                    conversationPlan.Platform,
                    conversationPlan.Recipient);
            }

            if (ReferenceEquals(_quickTaskCancellation, conversationCancellation))
            {
                _quickTaskCancellation = null;
            }
            if (sessionKey is not null && activeSession is not null)
                _activeCommunicationSessions.TryRemove(
                    new KeyValuePair<string, ActiveCommunicationSession>(sessionKey, activeSession));
        }

        CompleteTaskSubtasks(task, succeeded, answer);
        if (ReferenceEquals(_currentTask, task))
        {
            AddOrUpdateActivity(new AgentActivity(
                activityId,
                AgentActivityKind.Status,
                succeeded
                    ? (continuousConversation ? "沟通目标已完成" : "发送完成")
                    : (continuousConversation ? "持续聊天已结束" : "发送未完成"),
                activityDetail,
                succeeded ? AgentActivityStatus.Completed : AgentActivityStatus.Failed,
                startedAt));
            AddLocalAssistantMessage(answer);
            SetTaskIdle(succeeded
                ? (continuousConversation ? "沟通完成" : "发送完成")
                : (continuousConversation ? "持续聊天已结束" : "发送未完成"));
            await SaveCurrentTaskAsync();
        }
        else
        {
            task.Messages.Add(new SavedMessage
            {
                Role = "assistant",
                Text = ConversationPolicy.SanitizeAssistantText(answer),
                Timestamp = DateTimeOffset.Now,
            });
            await SaveDetachedTaskAsync(task);
            if (ReferenceEquals(_backgroundTask, task))
            {
                _backgroundTask = null;
                _backgroundIntentResolution = null;
                _backgroundTaskGraph = null;
                _detachedFromActiveTurn = false;
                _backgroundSavedActivityIds.Clear();
            }
        }

        _ = _currentPerformanceSession?.CompleteAsync(succeeded);

        if (succeeded)
        {
            _completedActionTitles.Add(continuousConversation
                ? $"与{intent.PlatformName}联系人完成持续沟通"
                : $"{intent.PlatformName}快速发送并核对结果");
            RememberTaskLearning(succeeded: true);
        }

        _voiceController.ResumeListening();
        return true;
    }

    private async Task TryPrimeSocialPlatformAsync(CommunicationTargetSlots targets)
    {
        // Prefer a known installed client without searching the whole desktop.
        // Otherwise open only the public page. Neither route logs in, pays,
        // follows a person or sends a message.
        var platform = targets.Platforms.FirstOrDefault();
        var appNames = platform switch
        {
            "Soul" => new[] { "Soul" },
            "抖音" => new[] { "抖音聊天", "抖音" },
            "小红书" => new[] { "小红书" },
            _ => [],
        };
        foreach (var name in appNames)
        {
            if (!WindowsApplicationRegistry.Default.TryResolve(name, out _)) continue;
            var launched = await _applicationFastPath.OpenAsync(name);
            if (!launched.Success) continue;
            AddOrUpdateActivity(new AgentActivity(
                $"social-platform-{Guid.NewGuid():N}",
                AgentActivityKind.Tool,
                $"已调出{name}",
                "只打开已安装客户端；尚未登录、筛选对象或发送消息",
                AgentActivityStatus.Completed,
                DateTimeOffset.Now));
            return;
        }

        var uri = platform switch
        {
            "Soul" => "https://home.soulapp.cn/",
            "抖音" => "https://www.douyin.com/",
            "小红书" => "https://www.xiaohongshu.com/",
            _ => null,
        };
        if (uri is null) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            AddOrUpdateActivity(new AgentActivity(
                $"social-platform-{Guid.NewGuid():N}",
                AgentActivityKind.Tool,
                "已打开社交平台入口",
                "公开页面不是可用聊天通道；接下来需检查实际登录状态和平台可用功能，尚未筛选或发送",
                AgentActivityStatus.Completed,
                DateTimeOffset.Now));
        }
        catch
        {
            // The general agent may use an installed application instead.
        }
    }

    private List<string> ResolveChatFastPathFiles(
        ChatFastPathIntent intent,
        IReadOnlyList<string> selectedFiles)
    {
        var directlySelected = selectedFiles
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directlySelected.Count > 0)
        {
            return intent.ReferencesMultipleFiles
                ? directlySelected.Take(8).ToList()
                : [directlySelected[0]];
        }

        var candidates = _artifactPaths
            .Concat(_currentTask?.Artifacts ?? [])
            .Concat(_state.RecentTasks
                .OrderByDescending(task => task.UpdatedAt)
                .SelectMany(task => task.Artifacts))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(path => MatchesRequestedArtifactType(path, intent.PayloadText))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(SafeLastWriteTimeUtc)
            .ToList();
        return intent.ReferencesMultipleFiles
            ? candidates.Take(8).ToList()
            : candidates.Take(1).ToList();
    }

    private static bool MatchesRequestedArtifactType(string path, string request)
    {
        var extension = Path.GetExtension(path);
        if (request.Contains("PPT", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("演示文稿", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("幻灯片", StringComparison.OrdinalIgnoreCase))
        {
            return extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase);
        }

        if (request.Contains("Word", StringComparison.OrdinalIgnoreCase))
        {
            return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".doc", StringComparison.OrdinalIgnoreCase);
        }

        if (request.Contains("Excel", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("表格", StringComparison.OrdinalIgnoreCase))
        {
            return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".csv", StringComparison.OrdinalIgnoreCase);
        }

        if (request.Contains("图片", StringComparison.OrdinalIgnoreCase) ||
            request.Contains("照片", StringComparison.OrdinalIgnoreCase))
        {
            return ImageExtensions.Contains(extension);
        }

        return !request.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string FormatConversationDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            var hours = (int)Math.Floor(duration.TotalHours);
            var minutes = duration.Minutes;
            return minutes == 0
                ? $"{hours} 小时"
                : $"{hours} 小时 {minutes} 分钟";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} 分钟";
    }

    private static string BuildChatFastPathAnswer(
        ChatFastPathIntent intent,
        IReadOnlyList<string> files,
        ChatFastPathResult result)
    {
        var destination = intent.UseCurrentConversation
            ? $"{intent.PlatformName}当前打开的会话"
            : $"{intent.PlatformName}联系人“{intent.Recipient}”";
        var elapsed = Math.Max(0.1, result.ElapsedMilliseconds / 1000d);
        if (result.Status == ChatFastPathStatus.Completed)
        {
            return files.Count > 0
                ? $"已把{FormatFileNames(files)}发送到{destination}，用时 {elapsed:0.0} 秒。"
                : $"消息已发送到{destination}，用时 {elapsed:0.0} 秒。";
        }

        if (result.Status == ChatFastPathStatus.ResultVerificationRequired)
        {
            return files.Count > 0
                ? $"已把{FormatFileNames(files)}提交到{destination}。当前客户端没有提供可读取的回执，为避免重复，本次没有再次发送。"
                : $"已向{destination}执行发送。当前客户端没有提供可读取的回执，为避免重复，本次没有再次发送。";
        }

        if (result.Status == ChatFastPathStatus.TargetVerificationRequired)
        {
            return $"已经找到可能的联系人，但当前页面没有提供足够信息来核对为{destination}，因此本次没有发送。请把联系人备注或群名说得更完整。";
        }

        if (result.Status == ChatFastPathStatus.RecipientNotFound)
        {
            return $"在{intent.PlatformName}搜索结果中没有识别到与“{intent.Recipient}”一致的联系人，因此本次没有发送。请检查联系人备注名是否完整，或先在{intent.PlatformName}中打开该会话后再说“发送到当前会话”。";
        }

        if (result.Status == ChatFastPathStatus.RecipientAmbiguous)
        {
            return $"{intent.PlatformName}搜索结果里实际出现了两个或以上可点击的相近联系人行，暂时无法唯一定位“{intent.Recipient}”。本次没有发送，避免发给同名或相似联系人；请使用完整备注名，或先打开目标会话后说“发送到当前会话”。";
        }

        if (result.Status == ChatFastPathStatus.TargetMismatch)
        {
            return $"已经打开一个搜索结果，但页面顶部识别出的会话名称与“{intent.Recipient}”不一致，因此本次没有发送。请先在{intent.PlatformName}中打开正确会话，或提供完整备注名后再试。";
        }

        if (result.Status == ChatFastPathStatus.RecognitionUnavailable)
        {
            return $"本机暂时不能读取{intent.PlatformName}联系人页面，因此没有执行发送。请确认 Windows 已安装简体中文文字识别语言组件后再试。";
        }

        if (result.Status == ChatFastPathStatus.ClipboardUnavailable)
        {
            return "系统剪贴板当时正被其他程序占用，联系人搜索没有完成，本次也没有发送。请稍后直接重试一次。";
        }

        if (result.Status == ChatFastPathStatus.TimedOut)
        {
            return $"联系人页面在快速时限内没有识别完成，本次没有发送。请保持{intent.PlatformName}主窗口可见后重试。";
        }

        if (result.Status == ChatFastPathStatus.NotReady)
        {
            return $"没有发现可用的{intent.PlatformName}主窗口，本次没有发送。请先登录并打开{intent.PlatformName}后再试。";
        }

        var reason = BuildSafeChatFailureReason(result.Detail);
        return $"没有完成向{destination}的发送。本次没有在后台继续操作。{reason}";
    }

    private static string BuildCommunicationSessionAnswer(
        CommunicationSessionResult session)
    {
        var count = session.OutgoingMessageCount;
        return session.Status switch
        {
            CommunicationSessionStatus.Completed =>
                $"本轮对话已结束，共发送 {count} 条消息。{session.Detail}",
            CommunicationSessionStatus.NeedsUserAttention =>
                $"已自动沟通并发送 {count} 条消息；随后出现需要您本人处理的内容，已暂停。{session.Detail}",
            CommunicationSessionStatus.WaitingForReply =>
                $"已发送 {count} 条消息。收到的内容暂时无法可靠生成下一句，会话记录已保留，没有猜测发送。{session.Detail}",
            CommunicationSessionStatus.TurnLimitReached =>
                $"本轮自动沟通已达到消息数量上限，共发送 {count} 条；为避免过度打扰，已停止继续发送。",
            CommunicationSessionStatus.TimedOut when count > 0 =>
                $"本轮持续聊天已停止，共发送 {count} 条消息，不会在后台继续发送。{session.Detail}",
            CommunicationSessionStatus.TimedOut =>
                $"本轮持续聊天已停止，首条消息没有可靠发送。{session.Detail}",
            _ =>
                $"持续聊天没有完成，共确认发送 {count} 条消息。{session.Detail}",
        };
    }

    private static string BuildChatFastPathActivityDetail(ChatFastPathResult result) =>
        result.Status switch
        {
            ChatFastPathStatus.Completed => "联系人和发送结果已核对",
            ChatFastPathStatus.ResultVerificationRequired => "发送动作已执行，客户端未提供可读取回执",
            ChatFastPathStatus.RecipientNotFound => "搜索结果中没有识别到指定联系人",
            ChatFastPathStatus.RecipientAmbiguous => "搜索结果中存在多个可点击的同名或相近联系人行",
            ChatFastPathStatus.TargetMismatch => "打开后的会话名称与指定联系人不一致",
            ChatFastPathStatus.RecognitionUnavailable => "本机联系人页面识别组件不可用",
            ChatFastPathStatus.ClipboardUnavailable => "系统剪贴板被其他程序占用",
            ChatFastPathStatus.TimedOut => "联系人页面识别超过快速时限",
            ChatFastPathStatus.NotReady => "没有发现已登录并打开的聊天主窗口",
            ChatFastPathStatus.TargetVerificationRequired => "当前页面信息不足，无法核对联系人",
            _ => "发送通道没有完成，具体原因已写入诊断记录",
        };

    internal static string BuildSafeChatFailureReason(string detail)
    {
        // Unknown failures may contain PowerShell CLIXML, compiler diagnostics,
        // accessibility trees or arbitrary exception text. None of it belongs
        // in the product conversation; the full redacted detail is kept only in
        // the local diagnostic log.
        return "发送通道暂时没有响应。请保持聊天软件已登录并打开，然后再试一次；详细原因已写入本机诊断记录。";
    }

    private static string FormatFileNames(IReadOnlyList<string> files)
    {
        var names = files.Select(path => $"“{Path.GetFileName(path)}”").ToArray();
        return names.Length <= 3
            ? string.Join("、", names)
            : string.Join("、", names.Take(3)) + $"等 {names.Length} 个文件";
    }

    private async Task EnsureCurrentTaskForMessageAsync(
        string displayText,
        IReadOnlyList<string> attachments)
    {
        _currentTask ??= new SavedTask { ThreadId = _agentClient.CurrentThreadId };
        if (_currentTask.Messages.Count == 0 || _currentTask.Title == "新的本机任务")
        {
            _currentTask.Title = ShortenSingleLine(displayText, 34);
            MainTaskTitle.Text = _currentTask.Title;
        }

        _currentTask.Messages.Add(new SavedMessage
        {
            Role = "user",
            Text = displayText,
            Timestamp = DateTimeOffset.Now,
            Attachments = attachments.ToList(),
        });
        _currentTask.Subtasks.Add(new SavedSubtask
        {
            Request = displayText,
            Status = "pending",
            StartedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        });
        _currentTask.UpdatedAt = DateTimeOffset.Now;
        await SaveCurrentTaskAsync();
    }

    private async void FileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _state.Language switch
            {
                "en-US" => "Choose files for HuiTian",
                "ja-JP" => "回天に渡すファイルを選択",
                _ => "选择要交给回天处理的文件",
            },
            InitialDirectory = _paths.UserContent,
            Multiselect = true,
            CheckFileExists = true,
            Filter = "所有支持的文件|*.txt;*.md;*.json;*.csv;*.log;*.png;*.jpg;*.jpeg;*.webp;*.docx;*.xlsx;*.pptx;*.pdf;*.cs;*.js;*.ts;*.py;*.html;*.css|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames.Select(Path.GetFullPath))
        {
            if (!_attachedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _attachedPaths.Add(path);
            }
        }

        RenderAttachments();
        await Task.CompletedTask;
    }

    private void MainWindow_OnPreviewDragEnter(object sender, DragEventArgs e) =>
        UpdateDropFeedback(e);

    private void MainWindow_OnPreviewDragOver(object sender, DragEventArgs e) =>
        UpdateDropFeedback(e);

    private void MainWindow_OnPreviewDragLeave(object sender, DragEventArgs e) =>
        ResetDropFeedback();

    private void MainWindow_OnDrop(object sender, DragEventArgs e)
    {
        ResetDropFeedback();
        var added = 0;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var path in paths.Where(File.Exists).Select(Path.GetFullPath))
            {
                if (!_attachedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _attachedPaths.Add(path);
                    added++;
                }
            }
        }
        else if (TryReadVirtualFilePath(e.Data, out var virtualPath))
        {
            _attachedPaths.Add(virtualPath);
            added++;
        }
        else if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
                 e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var directory = Path.Combine(_paths.Temp, "Dropped");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"拖入图片-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            _attachedPaths.Add(path);
            added++;
        }

        if (added > 0)
        {
            RenderAttachments();
            CurrentStatusText.Text = L(added == 1 ? "文件已添加" : $"已添加 {added} 个文件");
            MessageInput.Focus();
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            CurrentStatusText.Text = L("没有识别到可添加的本机文件");
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void UpdateDropFeedback(DragEventArgs e)
    {
        var accepted = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                       e.Data.GetDataPresent(DataFormats.Bitmap) ||
                       e.Data.GetFormats().Any(format =>
                           format.Equals("FileNameW", StringComparison.OrdinalIgnoreCase) ||
                           format.Equals("FileName", StringComparison.OrdinalIgnoreCase));
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        if (accepted)
        {
            ComposerBorder.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            ComposerBorder.BorderThickness = new Thickness(2);
            CurrentStatusText.Text = L("松开即可把文件添加到本次对话");
        }

        e.Handled = true;
    }

    private void ResetDropFeedback()
    {
        ComposerBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        ComposerBorder.BorderThickness = new Thickness(1);
    }

    private static bool TryReadVirtualFilePath(IDataObject data, out string path)
    {
        path = string.Empty;
        foreach (var format in new[] { "FileNameW", "FileName" })
        {
            try
            {
                if (!data.GetDataPresent(format) || data.GetData(format) is not string value ||
                    !File.Exists(value))
                {
                    continue;
                }

                path = Path.GetFullPath(value);
                return true;
            }
            catch (Exception exception) when (exception is ExternalException or IOException)
            {
            }
        }

        return false;
    }

    private void RenderAttachments()
    {
        AttachmentItems.Children.Clear();
        foreach (var path in _attachedPaths)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = "📎 " + Path.GetFileName(path),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ResourceBrush("TextBrush"),
            });
            var remove = new Button
            {
                Content = "×",
                Tag = path,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(5, 1, 5, 1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ResourceBrush("MutedBrush"),
                Cursor = Cursors.Hand,
            };
            remove.Click += (_, _) =>
            {
                _attachedPaths.RemoveAll(value => value.Equals(path, StringComparison.OrdinalIgnoreCase));
                RenderAttachments();
            };
            row.Children.Add(remove);
            AttachmentItems.Children.Add(new Border
            {
                Child = row,
                Background = ResourceBrush("PanelRaisedBrush"),
                BorderBrush = ResourceBrush("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 5, 7, 5),
                Margin = new Thickness(0, 0, 7, 4),
            });
        }

        AttachmentTray.Visibility = _attachedPaths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearAttachments()
    {
        _attachedPaths.Clear();
        RenderAttachments();
    }

    private void VoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_voiceWindow is not null)
        {
            if (!_voiceWindow.IsVisible)
            {
                _voiceWindow.ResumeSession();
            }

            _voiceWindow.Activate();
            return;
        }

        _voiceController.SelectVoice(_state.VoiceName);
        _voiceWindow = new VoiceSessionWindow(_voiceController) { Owner = this };
        _voiceWindow.UtteranceRecognized += VoiceWindow_OnUtteranceRecognized;
        _voiceWindow.LiveTranscriptChanged += VoiceWindow_OnLiveTranscriptChanged;
        _voiceWindow.Closed += (_, _) => _voiceWindow = null;
        _voiceWindow.ApplyLanguage(_state.Language);
        _voiceWindow.Show();
        _voiceWindow.DockToRight();
    }

    private void VoiceController_OnWakeWordDetected(object? sender, string phrase) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_voiceWindow is not { IsVisible: true })
            {
                VoiceButton_OnClick(VoiceButton, new RoutedEventArgs());
            }
        });

    private void VoiceController_OnWakePhraseCaptured(object? sender, string phrase) =>
        Dispatcher.InvokeAsync(async () =>
        {
            EndWakePhraseRecording();
            _state.WakePhrase = phrase;
            WakePhraseBox.Text = phrase;
            WakePhraseStatusText.Text = L("新的唤醒词已保存到本机");
            await SaveStateQuietlyAsync();
        });

    private void VoiceController_OnStateChanged(object? sender, string state) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (SettingsView.Visibility == Visibility.Visible && SettingsTabs.SelectedIndex == 2)
            {
                WakePhraseStatusText.Text = UiLocalizer.Translate(_state.Language, state);
            }

            if (_isRecordingWakePhrase &&
                (state.Contains("已录入", StringComparison.Ordinal) ||
                 state.Contains("没有听清", StringComparison.Ordinal) ||
                 state.Contains("不可用", StringComparison.Ordinal)))
            {
                EndWakePhraseRecording();
            }
        });

    private void VoiceController_OnAudioLevelChanged(object? sender, int level) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_isRecordingWakePhrase && WakeRecordingLevelBar is not null)
            {
                WakeRecordingLevelBar.Value = Math.Clamp(level, 0, 100);
            }
        });

    private void VoiceWindow_OnLiveTranscriptChanged(object? sender, string text)
    {
        MessageInput.Text = text;
        MessageInput.CaretIndex = MessageInput.Text.Length;
    }

    private async void VoiceWindow_OnUtteranceRecognized(object? sender, string text)
    {
        _voiceWindow?.ShowWorking(text);
        await SendCurrentMessageAsync(text, fromVoice: true);
    }

    private async void RecordWakePhraseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isRecordingWakePhrase)
        {
            return;
        }

        try
        {
            _isRecordingWakePhrase = true;
            WakeRecordingPanel.Visibility = Visibility.Visible;
            WakeRecordingLevelBar.Value = 0;
            RecordWakePhraseButton.IsEnabled = false;
            WakePhraseStatusText.Text = L("请清楚说出新的唤醒词……");
            _voiceController.BeginWakePhraseCapture();
            _wakeRecordingCancellation = new CancellationTokenSource();
            await Task.Delay(TimeSpan.FromSeconds(15), _wakeRecordingCancellation.Token);
            if (_isRecordingWakePhrase)
            {
                _voiceController.CancelWakePhraseCapture();
                WakePhraseStatusText.Text = L("没有听清，请再录一次");
                EndWakePhraseRecording();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            WakePhraseStatusText.Text = exception.Message;
            EndWakePhraseRecording();
        }
    }

    private void EndWakePhraseRecording()
    {
        _isRecordingWakePhrase = false;
        _wakeRecordingCancellation?.Cancel();
        _wakeRecordingCancellation?.Dispose();
        _wakeRecordingCancellation = null;
        if (WakeRecordingPanel is not null)
        {
            WakeRecordingPanel.Visibility = Visibility.Collapsed;
            WakeRecordingLevelBar.Value = 0;
            RecordWakePhraseButton.IsEnabled = true;
        }
    }

    private void PreviewVoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        _voiceController.SelectVoice((VoiceCombo.SelectedItem as VoiceOption)?.Id);
        var preview = (SelectedTag(LanguageCombo) ?? _state.Language) switch
        {
            "en-US" => "Hello, I am HuiTian. This is my current voice.",
            "ja-JP" => "こんにちは、回天です。現在の音声を試聴しています。",
            _ => "你好，我是回天。这是当前选择的声音。",
        };
        _voiceController.PreviewVoice(preview);
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e)
    {
        Interlocked.Increment(ref _submissionGeneration);
        _userRequestedStop = true;
        StopButton.IsEnabled = false;
        StopButton.Content = L("正在停止");
        CurrentStatusText.Text = L("正在停止当前任务");
        try
        {
            if (_currentTask is { } activeTask)
            {
                var currentSessions = _activeCommunicationSessions.Values
                    .Where(session => session.TaskId == activeTask.Id)
                    .ToArray();
                if (currentSessions.Length > 0)
                {
                    foreach (var session in currentSessions)
                        session.Cancellation.Cancel();
                    return;
                }
            }
            if (_intentCancellation is not null)
            {
                _intentCancellation.Cancel();
                CompleteOpenSubtasks(succeeded: false, "任务已停止");
                SetTaskIdle("任务已停止");
                return;
            }
            if (_quickTaskCancellation is not null)
            {
                _quickTaskCancellation.Cancel();
                return;
            }

            if (!_agentClient.HasActiveTurn)
            {
                CompleteOpenSubtasks(succeeded: false, "任务已停止");
                SetTaskIdle("任务已停止");
                return;
            }

            await _agentClient.InterruptAsync();
        }
        catch
        {
            _userRequestedStop = false;
            StopButton.IsEnabled = true;
            StopButton.Content = L("停止");
            AddLocalAssistantMessage("当前任务暂时无法停止，请稍后再试。 ");
        }
    }

    private async void ApproveButton_OnClick(object sender, RoutedEventArgs e) =>
        await RespondToApprovalAsync(approved: true);

    private async void DeclineApprovalButton_OnClick(object sender, RoutedEventArgs e) =>
        await RespondToApprovalAsync(approved: false);

    private async Task RespondToApprovalAsync(bool approved)
    {
        if (_pendingApprovalId is not { } requestId)
        {
            return;
        }

        ApprovalPanel.Visibility = Visibility.Collapsed;
        _pendingApprovalId = null;
        _approvalAttentionCancellation?.Cancel();
        _approvalAttentionCancellation?.Dispose();
        _wakeRecordingCancellation?.Cancel();
        _wakeRecordingCancellation?.Dispose();
        _approvalAttentionCancellation = null;
        try
        {
            await _agentClient.RespondToApprovalAsync(
                requestId,
                approved,
                RememberApprovalCheckBox.IsChecked == true);
        }
        catch
        {
            AddLocalAssistantMessage("没有成功提交您的选择，这一步已经停止。 ");
        }
        finally
        {
            RememberApprovalCheckBox.IsChecked = false;
        }
    }

    private void AgentClient_OnActivityReceived(object? sender, AgentActivityEventArgs e)
    {
        if (e.Activity.Kind == AgentActivityKind.UserMessage)
        {
            return;
        }

        var viewGeneration = Volatile.Read(ref _activityViewGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (_detachedFromActiveTurn)
            {
                HandleBackgroundActivity(e.Activity);
                return;
            }

            if (viewGeneration != _activityViewGeneration)
            {
                return;
            }

            if (_userRequestedStop &&
                e.Activity.Status == AgentActivityStatus.InProgress &&
                !e.Activity.Id.StartsWith("turn-", StringComparison.Ordinal))
            {
                return;
            }

            AddOrUpdateActivity(e.Activity);
        });
    }

    private void AgentClient_OnPlanUpdated(object? sender, AgentPlanEventArgs e)
    {
        var viewGeneration = Volatile.Read(ref _activityViewGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (!_detachedFromActiveTurn && viewGeneration == _activityViewGeneration)
            {
                RenderPlan(e.Plan);
            }
        });
    }

    private void AgentClient_OnApprovalRequested(object? sender, AgentApprovalEventArgs e)
    {
        var viewGeneration = Volatile.Read(ref _activityViewGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            // A background task may still reach a login or payment boundary.
            // Surface only the confirmation strip; never restore its old
            // conversation or process timeline over the new task.
            if (_detachedFromActiveTurn || viewGeneration == _activityViewGeneration)
            {
                ShowApproval(e.Approval);
            }
        });
    }

    private void AgentClient_OnConnectionChanged(object? sender, AgentConnectionEventArgs e) =>
        Dispatcher.InvokeAsync(() => UpdateConnection(e.State));

    private void AgentClient_OnComputerAction(object? sender, AgentComputerActionEventArgs e)
    {
        var viewGeneration = Volatile.Read(ref _activityViewGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (_detachedFromActiveTurn || viewGeneration != _activityViewGeneration)
            {
                return;
            }

            var overlay = EnsureControlOverlay();
            overlay.ApplyLanguage(_state.Language);
            overlay.ShowPointer(e.Action.X, e.Action.Y, e.Action.IsClick);
        });
    }

    private void AgentClient_OnIntentDeclared(object? sender, AgentIntentDeclaredEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // A task that the user deliberately left in the background must
            // never pull the visible conversation back or overwrite the new
            // foreground task's state.
            if (_detachedFromActiveTurn)
            {
                _backgroundIntentResolution = e.Intent;
                _backgroundTaskGraph = CreateTaskGraphSafely(e.Intent);
                if (_backgroundTask is not null)
                {
                    _backgroundTask.IntentJson = e.Intent.FormatForAgent();
                    _backgroundTask.TaskGraphJson = _backgroundTaskGraph.FormatForAgent();
                    _ = SaveDetachedTaskAsync(_backgroundTask);
                }

                return;
            }

            _currentIntentResolution = e.Intent;
            _currentSemanticIntent = e.Intent.ToLegacyDecision();
            _currentTurnRoute = e.Intent.ToAgentRoute();
            _currentTaskGraph = CreateTaskGraphSafely(e.Intent);
            _currentPerformanceSession?.SetIntent(e.Intent);
            if (_currentTask is not null)
            {
                _currentTask.IntentJson = e.Intent.FormatForAgent();
                _currentTask.TaskGraphJson = _currentTaskGraph.FormatForAgent();
                _currentTask.UpdatedAt = DateTimeOffset.Now;
                _ = SaveCurrentTaskAsync();
            }

            CurrentStatusText.Text = L("目标已理解，正在执行");
        });
    }

    private void AgentClient_OnTaskFinished(object? sender, AgentTaskFinishedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            var task = _detachedFromActiveTurn ? _backgroundTask : _currentTask;
            var graph = _detachedFromActiveTurn ? _backgroundTaskGraph : _currentTaskGraph;
            var outcome = await ConstrainSocialDiscoveryOutcomeAsync(task, e.Outcome);
            if (task is null) return;

            // The user may have opened a new task while the local evidence was
            // read. Preserve the old task without pulling its UI into view.
            if (!ReferenceEquals(_currentTask, task) || _detachedFromActiveTurn)
            {
                ApplyTaskOutcome(graph, outcome);
                task.TaskGraphJson = graph?.FormatForAgent() ?? string.Empty;
                CompleteTaskSubtasks(task, outcome.Succeeded,
                    outcome.Succeeded ? outcome.Summary : outcome.UnresolvedIssue);
                if (e.Outcome.Succeeded && !outcome.Succeeded)
                    task.Messages.Add(new SavedMessage
                    {
                        Role = "assistant",
                        Text = ConversationPolicy.SanitizeAssistantText(outcome.UnresolvedIssue ?? outcome.Summary),
                        Timestamp = DateTimeOffset.Now,
                    });
                _ = SaveDetachedTaskAsync(task);
                return;
            }

            ApplyTaskOutcome(graph, outcome);
            task.TaskGraphJson = graph?.FormatForAgent() ?? string.Empty;
            task.UpdatedAt = DateTimeOffset.Now;

            CompleteOpenSubtasks(
                outcome.Succeeded,
                outcome.Succeeded ? outcome.Summary : outcome.UnresolvedIssue);
            if (e.Outcome.Succeeded && !outcome.Succeeded)
                AddLocalAssistantMessage(outcome.UnresolvedIssue ?? outcome.Summary);
            CurrentStatusText.Text = L(outcome.Succeeded
                ? "任务已核验完成"
                : "任务尚有未完成项");
            _ = SaveCurrentTaskAsync();
        }));
    }

    private async Task<AgentTaskOutcome> ConstrainSocialDiscoveryOutcomeAsync(
        SavedTask? task, AgentTaskOutcome claimed)
    {
        if (task is null || !_socialDiscoveryTasks.Remove(task.Id, out var context) ||
            !claimed.Succeeded)
            return claimed;

        try
        {
            var requestedPlatforms = context.Targets.Platforms
                .Select(SocialContactKey.NormalizePlatform)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = (await _socialMemory.ListCandidatesAsync(limit: 20))
                .Where(candidate => candidate.LastScreenedAt >= context.StartedAt &&
                    (requestedPlatforms.Count == 0 ||
                     requestedPlatforms.Contains(candidate.Contact.Platform)))
                .ToArray();
            var verifiedOutgoing = 0;
            var reciprocalConversations = 0;
            foreach (var candidate in candidates)
            {
                var contact = await _socialMemory.ReadContactAsync(candidate.Contact);
                var currentTurns = contact?.RecentTurns
                    .Where(turn => turn.OccurredAt >= context.StartedAt)
                    .ToArray() ?? [];
                var hasOutgoing = currentTurns.Any(turn =>
                    turn.Role == SocialConversationTurnRole.UserSent);
                if (hasOutgoing)
                    verifiedOutgoing++;
                if (hasOutgoing && currentTurns.Any(turn =>
                        turn.Role == SocialConversationTurnRole.ContactReceived))
                    reciprocalConversations++;
            }

            return SocialDiscoveryExecutionContract.ConstrainClaimedCompletion(
                claimed, context.Targets,
                new SocialDiscoveryExecutionContract.CompletionEvidence(
                    candidates.Length,
                    verifiedOutgoing,
                    reciprocalConversations,
                    DateTimeOffset.UtcNow - context.StartedAt));
        }
        catch
        {
            const string issue = "本轮社交对象与发送证据暂时无法核对，因此不能宣称任务完成；已保留现有记录。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
    }

    private static void ApplyTaskOutcome(TaskGraph? graph, AgentTaskOutcome outcome)
    {
        if (graph is null || graph.Status is
            TaskGraphStatus.Succeeded or TaskGraphStatus.Failed or TaskGraphStatus.Cancelled)
        {
            return;
        }

        var evidence = string.Join(" | ", outcome.Evidence
            .Where(item => !string.IsNullOrWhiteSpace(item)));
        if (outcome.Succeeded)
        {
            // A task-level deliverable report is not step-level evidence.
            // Never smear one final summary across every node of a multi-step
            // graph. A single-step graph is the only safe projection until
            // executor evidence carries an explicit step id.
            if (graph.Steps.Count == 1)
            {
                var step = graph.Steps[0];
                if (step.Status == TaskStepStatus.Ready)
                {
                    graph.StartStep(step.Definition.Id, $"verified-{step.Definition.Action}");
                }

                if (step.Status == TaskStepStatus.Running && !string.IsNullOrWhiteSpace(evidence))
                {
                    graph.CompleteStep(step.Definition.Id, outcome.Summary, evidence);
                }
            }

            return;
        }

        var failedStep = graph.Steps.FirstOrDefault(step => step.Status == TaskStepStatus.Running);
        if (failedStep is not null && failedStep.Status == TaskStepStatus.Running)
        {
            graph.FailStep(
                failedStep.Definition.Id,
                outcome.UnresolvedIssue ?? outcome.Summary);
            graph.MarkUnrecoverable(
                failedStep.Definition.Id,
                outcome.UnresolvedIssue ?? outcome.Summary);
        }
    }

    private async void HandleBackgroundActivity(AgentActivity activity)
    {
        var task = _backgroundTask;
        if (task is null)
        {
            return;
        }

        if (activity.Kind == AgentActivityKind.AssistantMessage &&
            activity.Status == AgentActivityStatus.Completed &&
            !string.IsNullOrWhiteSpace(activity.Detail) &&
            _backgroundSavedActivityIds.Add(activity.Id))
        {
            var answer = ConversationPolicy.SanitizeAssistantText(
                NormalizeAssistantAnswer(activity.Detail, activity.Status));
            if (!string.IsNullOrWhiteSpace(answer))
            {
                task.Messages.Add(new SavedMessage
                {
                    Role = "assistant",
                    Text = answer,
                    Timestamp = DateTimeOffset.Now,
                });
                var pending = task.Subtasks.LastOrDefault(item => item.Status == "pending");
                if (pending is not null)
                {
                    pending.ResultSummary = ShortenSingleLine(answer, 160);
                    pending.UpdatedAt = DateTimeOffset.Now;
                }
            }
        }

        if (activity.Kind != AgentActivityKind.Status ||
            !activity.Id.StartsWith("turn-", StringComparison.Ordinal) ||
            activity.Status == AgentActivityStatus.InProgress)
        {
            return;
        }

        var succeeded = activity.Status == AgentActivityStatus.Completed;
        if (!succeeded)
        {
            CompleteTaskSubtasks(task, succeeded: false, activity.Detail);
        }
        await SaveDetachedTaskAsync(task);
        _backgroundTask = null;
        _backgroundIntentResolution = null;
        _backgroundTaskGraph = null;
        _detachedFromActiveTurn = false;
        _backgroundSavedActivityIds.Clear();
        _userRequestedStop = false;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        StopButton.Visibility = Visibility.Collapsed;
        SendButton.IsEnabled = _agentConnected && _brainReady;
        SendButton.Content = L("发送");
        CurrentStatusText.Text = L("准备接收任务");
        if (_sendWhenBackgroundEnds)
        {
            _sendWhenBackgroundEnds = false;
            await SendCurrentMessageAsync();
        }
    }

    private async Task SaveDetachedTaskAsync(SavedTask task)
    {
        task.Summary = ConversationPolicy.BuildConversationSummary(
            task.Messages.Select(message =>
                new ConversationMessageSnapshot(message.Role, message.Text)));
        task.Title = ConversationPolicy.BuildNaturalTaskTitle(
            task.Subtasks.Select(subtask => subtask.Request));
        task.UpdatedAt = DateTimeOffset.Now;
        var existingIndex = _state.RecentTasks.FindIndex(item => item.Id == task.Id);
        if (existingIndex >= 0)
        {
            _state.RecentTasks[existingIndex] = task;
        }
        else if (task.Messages.Count > 0)
        {
            _state.RecentTasks.Add(task);
        }

        await SaveStateQuietlyAsync();
        RenderRecentTasks(TaskSearchBox.Text);
    }

    private void AddLocalAssistantMessage(string text)
    {
        AddOrUpdateActivity(new AgentActivity(
            $"local-agent-{Guid.NewGuid():N}",
            AgentActivityKind.AssistantMessage,
            string.Empty,
            text,
            AgentActivityStatus.Completed,
            DateTimeOffset.Now));
    }

    private void AddOrUpdateActivity(AgentActivity activity)
    {
        RecordPerformanceActivity(activity);
        var shouldFollowTail = _followActivityTail;
        WelcomePanel.Visibility = Visibility.Collapsed;
        if (IsProcessActivity(activity.Kind))
        {
            AddOrUpdateProcessActivity(activity);
        }
        else
        {
            AddOrUpdateConversationActivity(activity);
        }

        UpdateTaskControls(activity);
        DiscoverArtifacts(activity);
        if (!_isRestoringHistory && activity.Kind == AgentActivityKind.AssistantMessage &&
            _voiceWindow is { IsVisible: true })
        {
            if (_state.SpeakAnswers)
            {
                var visibleText = _activityVisuals.TryGetValue(activity.Id, out var currentVisual)
                    ? currentVisual.Detail.Text
                    : activity.Detail;
                _voiceWindow.StreamResponseText(
                    activity.Id,
                    CleanForSpeech(visibleText),
                    activity.Status == AgentActivityStatus.Completed);
            }
            else if (activity.Status == AgentActivityStatus.Completed)
            {
                _voiceController.ResumeListening();
            }
        }

        if (activity.Kind == AgentActivityKind.AssistantMessage &&
            activity.Status == AgentActivityStatus.Completed &&
            !string.IsNullOrWhiteSpace(activity.Detail) &&
            _savedActivityIds.Add(activity.Id))
        {
            var visibleAnswer = _activityVisuals.TryGetValue(activity.Id, out var answerVisual)
                ? answerVisual.Detail.Text
                : NormalizeAssistantAnswer(activity.Detail, activity.Status);
            visibleAnswer = ConversationPolicy.SanitizeAssistantText(visibleAnswer);
            if (!string.IsNullOrWhiteSpace(visibleAnswer))
            {
                SaveAssistantMessage(visibleAnswer);
            }
        }

        if (shouldFollowTail)
        {
            FollowActivityTailAfterLayout();
        }
    }

    private void RecordPerformanceActivity(AgentActivity activity)
    {
        var session = _currentPerformanceSession;
        if (session is null || session.IsCompleted)
        {
            return;
        }

        var isExecutableActivity = activity.Kind is
            AgentActivityKind.Tool or AgentActivityKind.FileChange;
        if (isExecutableActivity && activity.Status == AgentActivityStatus.InProgress)
        {
            session.MarkFirstAction();
            _performanceActivityStarts.TryAdd(activity.Id, DateTimeOffset.Now);
            return;
        }

        if ((isExecutableActivity || _performanceActivityStarts.ContainsKey(activity.Id)) &&
            activity.Status is
            AgentActivityStatus.Completed or AgentActivityStatus.Failed or
            AgentActivityStatus.Declined)
        {
            var elapsed = _performanceActivityStarts.Remove(activity.Id, out var startedAt)
                ? Math.Max(0, (long)(DateTimeOffset.Now - startedAt).TotalMilliseconds)
                : 0;
            var activityText = activity.Title + " " + activity.Detail;
            var usedVision = activityText.Contains("屏幕", StringComparison.OrdinalIgnoreCase) ||
                             activityText.Contains("截图", StringComparison.OrdinalIgnoreCase) ||
                             activityText.Contains("screen", StringComparison.OrdinalIgnoreCase) ||
                             activityText.Contains("capture", StringComparison.OrdinalIgnoreCase);
            var wasFallback = activityText.Contains("换一种", StringComparison.OrdinalIgnoreCase) ||
                              activityText.Contains("重试", StringComparison.OrdinalIgnoreCase) ||
                              activityText.Contains("恢复", StringComparison.OrdinalIgnoreCase) ||
                              activityText.Contains("fallback", StringComparison.OrdinalIgnoreCase);
            session.RecordTool(
                activity.Title,
                elapsed,
                activity.Status == AgentActivityStatus.Completed,
                usedVision,
                wasFallback);
            return;
        }

        if (activity.Kind == AgentActivityKind.Error &&
            activity.Status == AgentActivityStatus.Failed)
        {
            session.RecordTool("agent-error", 0, succeeded: false);
        }
    }

    private void ActivityScroll_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _followActivityTail = false;
        }
    }

    private void ActivityScroll_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticActivityScroll)
        {
            return;
        }

        // A streaming text update changes only the extent first. That must not
        // be mistaken for a user scroll away from the tail.
        if (Math.Abs(e.VerticalChange) < 0.1 && Math.Abs(e.ExtentHeightChange) > 0.1)
        {
            return;
        }

        _followActivityTail = IsActivityAtTail();
    }

    private bool IsActivityAtTail() =>
        ActivityScroll.ScrollableHeight <= 1 ||
        ActivityScroll.VerticalOffset >= ActivityScroll.ScrollableHeight - 12;

    private void FollowActivityTailAfterLayout()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_followActivityTail)
            {
                return;
            }

            _isProgrammaticActivityScroll = true;
            try
            {
                ActivityScroll.ScrollToEnd();
            }
            finally
            {
                _isProgrammaticActivityScroll = false;
            }
        }));
    }

    private void AddOrUpdateConversationActivity(AgentActivity activity)
    {
        if (_activityVisuals.TryGetValue(activity.Id, out var existing))
        {
            UpdateActivityText(existing, activity);
            existing.LastStatus = activity.Status;
            return;
        }

        var visual = CreateConversationVisual(activity);
        _activityVisuals[activity.Id] = visual;
        ActivityItems.Children.Add(visual.Container);
    }

    private ActivityVisual CreateConversationVisual(AgentActivity activity)
    {
        var visibleConversationText = activity.Kind == AgentActivityKind.AssistantMessage
            ? NormalizeAssistantAnswer(activity.Detail, activity.Status)
            : activity.Detail;
        var detail = new TextBox
        {
            Text = visibleConversationText,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = activity.Kind == AgentActivityKind.AssistantMessage ? 15 : 14,
            Foreground = ResourceBrush("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.IBeam,
        };
        detail.SetValue(
            TextBlock.LineHeightProperty,
            activity.Kind == AgentActivityKind.AssistantMessage ? 26d : 23d);
        // Conversation content belongs to the user and the assistant. Changing
        // the interface language must never rewrite an existing conversation.
        UiLocalizer.SetSkip(detail, true);
        if (activity.Kind == AgentActivityKind.UserMessage)
        {
            var lines = activity.Detail.ReplaceLineEndings("\n").Split('\n');
            var attachmentLines = lines
                .Where(line => line.TrimStart().StartsWith("📎", StringComparison.Ordinal))
                .Select(line => line.Trim())
                .ToArray();
            detail.Text = string.Join("\n", lines
                .Where(line => !line.TrimStart().StartsWith("📎", StringComparison.Ordinal)))
                .TrimEnd();
            detail.Foreground = ResourceBrush("UserBubbleTextBrush");
            var content = new StackPanel();
            content.Children.Add(detail);
            if (attachmentLines.Length > 0)
            {
                var files = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
                foreach (var attachment in attachmentLines)
                {
                    files.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
                        BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(9, 5, 9, 5),
                        Margin = new Thickness(0, 0, 7, 5),
                        Child = new TextBlock
                        {
                            Text = attachment,
                            FontSize = 11,
                            Foreground = ResourceBrush("UserBubbleTextBrush"),
                        },
                    });
                }

                content.Children.Add(files);
            }

            var container = new Border
            {
                Child = content,
                Background = ResourceBrush("UserBubbleBrush"),
                BorderBrush = ResourceBrush("UserBubbleBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(15, 11, 15, 11),
                Margin = new Thickness(100, 0, 0, 18),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 650,
            };
            return new ActivityVisual(
                container,
                null,
                detail,
                null,
                activity.Status,
                activity.Timestamp,
                activity.Kind,
                rawDetail: activity.Detail);
        }

        var accent = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = ResourceBrush("AccentBrush"),
            Opacity = 0.78,
            Margin = new Thickness(0, 3, 15, 3),
        };
        var textPanel = new Grid();
        textPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textPanel.Children.Add(detail);
        var copyAnswer = new Button
        {
            Content = "⧉",
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            FontSize = 12,
            ToolTip = L("复制这段回答"),
        };
        copyAnswer.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(detail.Text))
            {
                Clipboard.SetText(detail.Text);
                copyAnswer.Content = "✓";
            }
        };
        Grid.SetRow(copyAnswer, 1);
        textPanel.Children.Add(copyAnswer);
        var row = new Grid { Margin = new Thickness(0, 2, 62, 26) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(accent);
        Grid.SetColumn(textPanel, 1);
        row.Children.Add(textPanel);
        var assistantContainer = new Border
        {
            Child = row,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return new ActivityVisual(
            assistantContainer,
            null,
            detail,
            null,
            activity.Status,
            activity.Timestamp,
            activity.Kind,
            rawDetail: activity.Detail);
    }

    private void AddOrUpdateProcessActivity(AgentActivity activity)
    {
        var group = ResolveProcessGroup(activity);
        var presentation = PresentProcessActivity(activity);
        if (presentation.IsRecovery && activity.Status != AgentActivityStatus.Failed)
        {
            // Recovery is a transient connection state, not a user task step.
            // Keeping it in the timeline made stale recovery messages appear to
            // run for tens of minutes after normal work had already resumed.
            if (group.Items.Children.Count == 0)
            {
                group.Container.Visibility = Visibility.Collapsed;
            }

            // The turn-level status row used the same activity id as the
            // recovery notification. Keep the transient recovery message out
            // of the timeline and retire the old status row, otherwise it
            // continues counting time while the retry is already running.
            if (_activityVisuals.TryGetValue(activity.Id, out var recoveryStatus))
            {
                recoveryStatus.Container.Visibility = Visibility.Collapsed;
                recoveryStatus.LastStatus = AgentActivityStatus.Completed;
                foreach (var key in _activityVisuals
                             .Where(pair => ReferenceEquals(pair.Value, recoveryStatus))
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _activityVisuals.Remove(key);
                }

                if (ReferenceEquals(group.LastVisual, recoveryStatus))
                {
                    group.LastVisual = null;
                    group.LastMergeKey = null;
                }

                if (ReferenceEquals(group.GenericReasoningVisual, recoveryStatus))
                {
                    group.GenericReasoningVisual = null;
                }
            }

            UpdateProcessGroupState(group, activity, presentation);
            return;
        }

        if (group.Container.Visibility != Visibility.Visible && group.Items.Children.Count == 0)
        {
            group.StartedAt = activity.Timestamp;
            group.Elapsed.Text = FormatElapsed(TimeSpan.Zero);
        }

        group.Container.Visibility = Visibility.Visible;

        if (_activityVisuals.TryGetValue(activity.Id, out var existing))
        {
            UpdateActivityText(existing, activity);
            if (existing.Status is not null)
            {
                existing.Status.Fill = StatusBrush(activity.Status);
            }

            existing.LastStatus = activity.Status;
        }
        else if (presentation.IsGenericReasoning && group.GenericReasoningVisual is { } genericReasoning)
        {
            if (genericReasoning.LastStatus != AgentActivityStatus.InProgress)
            {
                genericReasoning.StartedAt = activity.Timestamp;
            }

            UpdateProcessVisual(genericReasoning, activity, presentation);
            _activityVisuals[activity.Id] = genericReasoning;
        }
        else if (group.LastVisual is { } lastVisual &&
                 CanMergeConsecutiveProcessRows(group, presentation, activity))
        {
            UpdateProcessVisual(lastVisual, activity, presentation);
            _activityVisuals[activity.Id] = lastVisual;
            group.LastMergeKey = presentation.MergeKey;
        }
        else
        {
            var visual = CreateProcessRow(activity, presentation);
            _activityVisuals[activity.Id] = visual;
            group.Items.Children.Add(visual.Container);
            group.LastVisual = visual;
            group.LastMergeKey = presentation.MergeKey;
            if (presentation.IsGenericReasoning)
            {
                group.GenericReasoningVisual = visual;
            }
        }

        UpdateProcessGroupState(group, activity, presentation);

        if (activity.Kind == AgentActivityKind.Tool || activity.Kind == AgentActivityKind.FileChange)
        {
            if (activity.Status == AgentActivityStatus.InProgress)
            {
                _activeComputerItems.Add(activity.Id);
                EnsureControlOverlay().ShowControl(
                    UiLocalizer.Translate(_state.Language, "回天正在操作电脑"));
            }
            else
            {
                if (activity.Status == AgentActivityStatus.Completed)
                {
                    var action = FriendlyActivityTitle(activity);
                    var evidence = ShortenSingleLine(FriendlyActivityDetail(activity), 120);
                    if (!string.IsNullOrWhiteSpace(evidence))
                    {
                        action += "：" + evidence;
                    }
                    if (!_completedActionTitles.Contains(action, StringComparer.OrdinalIgnoreCase))
                    {
                        _completedActionTitles.Add(action);
                    }
                }

                _activeComputerItems.Remove(activity.Id);
            }
        }

    }

    private ProcessGroupVisual ResolveProcessGroup(AgentActivity activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.TurnId) &&
            _processGroups.TryGetValue(activity.TurnId, out var existing))
        {
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(activity.TurnId))
        {
            var recovering = _processGroups.LastOrDefault(pair =>
                pair.Value.AwaitingRecoveryTurn);
            if (recovering.Value is not null)
            {
                // Automatic recovery creates a fresh App Server turn, but it
                // is still the same user task. Rebind the unfinished process
                // group instead of adding a second "task process" card.
                _processGroups.Remove(recovering.Key);
                recovering.Value.BoundTurnId = activity.TurnId;
                recovering.Value.AwaitingRecoveryTurn = false;
                _processGroups[activity.TurnId] = recovering.Value;
                return recovering.Value;
            }

            var pending = _processGroups
                .LastOrDefault(pair => pair.Value.BoundTurnId is null &&
                                       pair.Value.LastStatus == AgentActivityStatus.InProgress);
            if (pending.Value is not null)
            {
                _processGroups.Remove(pending.Key);
                pending.Value.BoundTurnId = activity.TurnId;
                _processGroups[activity.TurnId] = pending.Value;
                return pending.Value;
            }

            var bound = CreateProcessGroup(activity.Timestamp);
            bound.BoundTurnId = activity.TurnId;
            _processGroups[activity.TurnId] = bound;
            ActivityItems.Children.Add(bound.Container);
            return bound;
        }

        if (activity.Id.StartsWith("recovery-", StringComparison.Ordinal))
        {
            var recovering = _processGroups.Values.LastOrDefault(group =>
                group.AwaitingRecoveryTurn);
            if (recovering is not null)
            {
                return recovering;
            }
        }

        var unresolved = _processGroups.Values.LastOrDefault(group =>
            group.BoundTurnId is null && group.LastStatus == AgentActivityStatus.InProgress);
        if (unresolved is not null)
        {
            return unresolved;
        }

        var created = CreateProcessGroup(activity.Timestamp);
        _processGroups[$"pending-{Guid.NewGuid():N}"] = created;
        ActivityItems.Children.Add(created.Container);
        return created;
    }

    private void UpdateProcessGroupState(
        ProcessGroupVisual group,
        AgentActivity activity,
        ProcessActivityPresentation presentation)
    {
        if (activity.Status == AgentActivityStatus.InProgress)
        {
            if (_userRequestedStop)
            {
                group.AwaitingRecoveryTurn = false;
                group.State.Text = L("正在停止");
                group.State.Foreground = StatusBrush(AgentActivityStatus.Pending);
                return;
            }

            var recovering = group.LastStatus == AgentActivityStatus.Failed ||
                             presentation.IsRecovery;
            group.AwaitingRecoveryTurn = presentation.IsRecovery;
            group.LastStatus = AgentActivityStatus.InProgress;
            group.FinalDuration = null;
            group.State.Text = L(recovering ? "正在恢复" : "进行中");
            group.State.Foreground = StatusBrush(AgentActivityStatus.InProgress);
            return;
        }

        if (activity.Status == AgentActivityStatus.Failed || activity.Kind == AgentActivityKind.Error)
        {
            group.AwaitingRecoveryTurn = false;
            group.LastStatus = AgentActivityStatus.Failed;
            group.State.Text = L(
                activity.Kind == AgentActivityKind.Status &&
                activity.Id.StartsWith("turn-", StringComparison.Ordinal)
                    ? "已停止"
                    : "需要处理");
            group.State.Foreground = StatusBrush(AgentActivityStatus.Failed);
            if (activity.Kind == AgentActivityKind.Status &&
                activity.Id.StartsWith("turn-", StringComparison.Ordinal))
            {
                group.FinalDuration = activity.Timestamp - group.StartedAt;
                group.Elapsed.Text = FormatElapsed(group.FinalDuration.Value);
            }
            return;
        }

        if (activity.Kind == AgentActivityKind.Status && activity.Status == AgentActivityStatus.Completed)
        {
            group.AwaitingRecoveryTurn = false;
            group.LastStatus = AgentActivityStatus.Completed;
            group.State.Text = L("已完成");
            group.State.Foreground = StatusBrush(AgentActivityStatus.Completed);
            group.FinalDuration = activity.Timestamp - group.StartedAt;
            group.Elapsed.Text = FormatElapsed(group.FinalDuration.Value);
            _activeComputerItems.Clear();
            _controlOverlay?.RequestHide();
        }
    }

    private ProcessGroupVisual CreateProcessGroup(DateTimeOffset startedAt)
    {
        var title = new TextBlock
        {
            Text = UiLocalizer.Translate(_state.Language, "任务过程"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        var state = new TextBlock
        {
            Text = UiLocalizer.Translate(_state.Language, "进行中"),
            FontSize = 10,
            Foreground = ResourceBrush("AccentBrush"),
            Margin = new Thickness(8, 1, 0, 0),
        };
        var elapsed = new TextBlock
        {
            Text = "0 秒",
            FontSize = 10,
            Foreground = ResourceBrush("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Orientation = Orientation.Horizontal };
        labels.Children.Add(title);
        labels.Children.Add(state);
        header.Children.Add(labels);
        Grid.SetColumn(elapsed, 1);
        header.Children.Add(elapsed);
        var items = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(items);
        var container = new Border
        {
            Child = panel,
            Background = ResourceBrush("PanelBrush"),
            BorderBrush = ResourceBrush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(15, 13, 15, 13),
            Margin = new Thickness(0, 0, 42, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return new ProcessGroupVisual(container, items, elapsed, state, startedAt, AgentActivityStatus.InProgress);
    }

    private ActivityVisual CreateProcessRow(
        AgentActivity activity,
        ProcessActivityPresentation presentation)
    {
        var status = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = StatusBrush(activity.Status),
            Margin = new Thickness(0, 5, 10, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var title = new TextBlock
        {
            Text = UiLocalizer.Translate(_state.Language, presentation.Title),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("SecondaryTextBrush"),
        };
        var elapsed = new TextBlock
        {
            Text = "0 秒",
            FontSize = 10,
            Foreground = ResourceBrush("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var detail = new TextBox
        {
            Text = UiLocalizer.Translate(_state.Language, presentation.Detail),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontSize = 11,
            Foreground = ResourceBrush("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.IBeam,
        };
        detail.SetValue(TextBlock.LineHeightProperty, 18d);
        var textGrid = new Grid();
        textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        textGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        textGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        textGrid.Children.Add(title);
        Grid.SetColumn(elapsed, 1);
        textGrid.Children.Add(elapsed);
        Grid.SetRow(detail, 1);
        Grid.SetColumnSpan(detail, 2);
        textGrid.Children.Add(detail);
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(status);
        Grid.SetColumn(textGrid, 1);
        row.Children.Add(textGrid);
        var container = new Border
        {
            Child = row,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return new ActivityVisual(
            container,
            title,
            detail,
            status,
            activity.Status,
            activity.Timestamp,
            activity.Kind,
            elapsed,
            activity.Detail);
    }

    private void UpdateActivityText(ActivityVisual existing, AgentActivity activity)
    {
        if (activity.Kind == AgentActivityKind.AssistantMessage)
        {
            existing.RawDetail = MergeStreamingText(
                existing.RawDetail,
                activity.Detail,
                activity.Status == AgentActivityStatus.Completed);
            existing.Detail.Text = NormalizeAssistantAnswer(existing.RawDetail, activity.Status);
            return;
        }

        if (IsProcessActivity(activity.Kind))
        {
            UpdateProcessVisual(existing, activity, PresentProcessActivity(activity));
            return;
        }

        existing.RawDetail = activity.Detail;
        existing.Detail.Text = activity.Detail;
    }

    private void UpdateProcessVisual(
        ActivityVisual visual,
        AgentActivity activity,
        ProcessActivityPresentation presentation)
    {
        visual.RawDetail = activity.Detail;
        visual.Title!.Text = UiLocalizer.Translate(_state.Language, presentation.Title);
        visual.Detail.Text = UiLocalizer.Translate(_state.Language, presentation.Detail);
        if (visual.Status is not null)
        {
            visual.Status.Fill = StatusBrush(activity.Status);
        }

        if (visual.Elapsed is not null && activity.Status != AgentActivityStatus.InProgress)
        {
            var completedAt = activity.Timestamp > visual.StartedAt
                ? activity.Timestamp
                : DateTimeOffset.Now;
            visual.Elapsed.Text = FormatElapsed(completedAt - visual.StartedAt);
        }

        visual.LastStatus = activity.Status;
    }

    private void UpdateTaskControls(AgentActivity activity)
    {
        if (activity.Kind == AgentActivityKind.Status)
        {
            CurrentStatusText.Text = L(activity.Title);
            if (activity.Status == AgentActivityStatus.InProgress)
            {
                _taskStartedAt ??= activity.Timestamp;
                StopButton.Visibility = Visibility.Visible;
                StopButton.IsEnabled = !_userRequestedStop;
                StopButton.Content = L(_userRequestedStop ? "正在停止" : "停止");
                SendButton.IsEnabled = !_userRequestedStop;
                SendButton.Content = L(_userRequestedStop ? "请稍候" : "追加");
                if (_userRequestedStop)
                {
                    CurrentStatusText.Text = L("正在停止当前任务");
                }
            }
            else if (activity.Id.StartsWith("turn-", StringComparison.Ordinal))
            {
                if (_taskStartedAt is { } started)
                {
                    _lastTaskDuration = DateTimeOffset.Now - started;
                }

                SetTaskIdle(activity.Title);
                var succeeded = activity.Status == AgentActivityStatus.Completed;
                // A completed protocol turn is only a transport event. The
                // verified TaskFinished event is the sole success writer; a
                // failed/stopped turn may still close its pending local entry.
                if (!succeeded)
                {
                    CompleteOpenSubtasks(succeeded: false, activity.Detail);
                }
                RememberTaskLearning(succeeded);
                _ = SaveCurrentTaskAsync();
            }
        }
        else if (activity.Kind == AgentActivityKind.Error && activity.Status == AgentActivityStatus.Failed)
        {
            if (_agentClient.HasActiveTurn && !_userRequestedStop)
            {
                CurrentStatusText.Text = L("这一步未完成，正在换一种方式继续");
                StopButton.Visibility = Visibility.Visible;
                StopButton.IsEnabled = true;
                StopButton.Content = L("停止");
                SendButton.IsEnabled = true;
                SendButton.Content = L("追加");
            }
            else
            {
                SetTaskIdle("任务未能继续");
                CompleteOpenSubtasks(succeeded: false, activity.Detail);
            }
        }
    }

    private void SetTaskIdle(string status)
    {
        CurrentStatusText.Text = L(status);
        StopButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        SendButton.IsEnabled = _agentConnected && _brainReady;
        SendButton.Content = L("发送");
        _userRequestedStop = false;
        _activeComputerItems.Clear();
        _controlOverlay?.RequestHide();
        _voiceController.ResumeListening();
    }

    private static bool IsProcessActivity(AgentActivityKind kind) => kind is
        AgentActivityKind.Reasoning or AgentActivityKind.Plan or AgentActivityKind.Tool or
        AgentActivityKind.FileChange or AgentActivityKind.Approval or AgentActivityKind.Status or
        AgentActivityKind.Error;

    private static string FriendlyActivityTitle(AgentActivity activity) => activity.Kind switch
    {
        AgentActivityKind.Reasoning => activity.Status switch
        {
            AgentActivityStatus.Completed => "分析完成",
            AgentActivityStatus.Failed => "分析遇到问题",
            _ => "正在分析",
        },
        AgentActivityKind.Plan => activity.Status == AgentActivityStatus.Completed
            ? "计划已更新"
            : "正在规划步骤",
        AgentActivityKind.Tool or AgentActivityKind.FileChange => activity.Title,
        AgentActivityKind.Error => "遇到问题",
        AgentActivityKind.Status => activity.Title,
        _ => activity.Title,
    };

    private string FriendlyActivityDetail(AgentActivity activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.Detail))
        {
            return ProductizeTechnicalActivityText(
                HideForeignReasoning(activity.Detail, activity.Kind),
                activity.Kind,
                activity.Status);
        }

        return activity.Kind switch
        {
            AgentActivityKind.Reasoning => "正在分析任务目标和下一步。",
            AgentActivityKind.Plan => "正在整理接下来的执行步骤。",
            AgentActivityKind.Tool => "正在使用本机能力完成这一步。",
            _ => "正在处理。",
        };
    }

    private ProcessActivityPresentation PresentProcessActivity(AgentActivity activity)
    {
        var source = $"{activity.Title} {activity.Detail}";
        var isRecovery = source.Contains("恢复", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("修复", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("重建", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("重连", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("重新连接", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("精简任务连接", StringComparison.OrdinalIgnoreCase) ||
                         source.Contains("精简连接", StringComparison.OrdinalIgnoreCase);
        var title = RemoveRepeatedAssistantName(FriendlyActivityTitle(activity));
        if (isRecovery)
        {
            title = activity.Status switch
            {
                AgentActivityStatus.Completed => "任务已恢复",
                AgentActivityStatus.Failed => "恢复遇到问题",
                _ => "正在恢复任务",
            };
        }
        else if (activity.Status == AgentActivityStatus.Failed &&
                 activity.Kind is not AgentActivityKind.Reasoning)
        {
            title = activity.Kind == AgentActivityKind.Error ? "遇到问题" : "这一步未完成";
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = activity.Status == AgentActivityStatus.Completed ? "步骤已完成" : "正在处理";
        }

        var detail = CompactTimelineFact(RemoveRepeatedAssistantName(FriendlyActivityDetail(activity)));
        if (string.IsNullOrWhiteSpace(detail) ||
            string.Equals(
                CanonicalizeProcessText(detail),
                CanonicalizeProcessText(title),
                StringComparison.Ordinal))
        {
            detail = activity.Kind switch
            {
                AgentActivityKind.Reasoning => activity.Status == AgentActivityStatus.Completed
                    ? "已明确当前目标和下一步。"
                    : "正在确认目标、上下文和下一步。",
                AgentActivityKind.Plan => "正在整理可执行步骤。",
                AgentActivityKind.Tool or AgentActivityKind.FileChange => activity.Status == AgentActivityStatus.Completed
                    ? "这一步已经完成。"
                    : "正在执行这一步。",
                AgentActivityKind.Error => "已保留当前任务，可继续恢复。",
                _ => activity.Status == AgentActivityStatus.Completed ? "当前任务已完成。" : "正在处理当前任务。",
            };
        }

        var canonicalDetail = CanonicalizeProcessText(detail);
        var isGenericReasoning = activity.Kind == AgentActivityKind.Reasoning &&
                                 (canonicalDetail.Contains("分析当前任务", StringComparison.Ordinal) ||
                                  canonicalDetail.Contains("分析任务目标", StringComparison.Ordinal) ||
                                  canonicalDetail.Contains("确认目标上下文", StringComparison.Ordinal) ||
                                  canonicalDetail.Length <= 10);
        var mergeKey = $"{activity.Kind}:{CanonicalizeProcessText(title)}:{canonicalDetail}";
        return new ProcessActivityPresentation(
            title,
            detail,
            mergeKey,
            isRecovery,
            isGenericReasoning);
    }

    private static bool CanMergeConsecutiveProcessRows(
        ProcessGroupVisual group,
        ProcessActivityPresentation presentation,
        AgentActivity activity)
    {
        if (group.LastVisual is null || group.LastMergeKey is null)
        {
            return false;
        }

        // A recovery deserves its own line after a visible failure so the user
        // can immediately see that work has resumed.
        if (group.LastVisual.LastStatus == AgentActivityStatus.Failed &&
            activity.Status == AgentActivityStatus.InProgress)
        {
            return false;
        }

        if (string.Equals(group.LastMergeKey, presentation.MergeKey, StringComparison.Ordinal))
        {
            return true;
        }

        return group.LastVisual.Kind == AgentActivityKind.Reasoning &&
               activity.Kind == AgentActivityKind.Reasoning &&
               group.LastVisual.LastStatus != AgentActivityStatus.Failed;
    }

    private static string RemoveRepeatedAssistantName(string value)
    {
        var cleaned = value.Trim();
        cleaned = cleaned
            .Replace("回天AI正在", "正在", StringComparison.OrdinalIgnoreCase)
            .Replace("回天 AI 正在", "正在", StringComparison.OrdinalIgnoreCase)
            .Replace("回天正在", "正在", StringComparison.OrdinalIgnoreCase)
            .Replace("回天已经", "已经", StringComparison.OrdinalIgnoreCase)
            .Replace("回天已", "已", StringComparison.OrdinalIgnoreCase)
            .Replace("回天将", "将", StringComparison.OrdinalIgnoreCase)
            .Replace("回天会", "将", StringComparison.OrdinalIgnoreCase)
            .Replace("回天检测到", "检测到", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(cleaned, @"^回天(?:\s*AI)?[\s，,:：-]*", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
    }

    private static string CompactTimelineFact(string value)
    {
        var normalized = Regex.Replace(value.ReplaceLineEndings(" "), @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var end = normalized.IndexOfAny(['。', '！', '？']);
        if (end >= 0)
        {
            normalized = normalized[..(end + 1)];
        }

        return Shorten(normalized, 150);
    }

    private static string CanonicalizeProcessText(string value)
    {
        var canonical = RemoveRepeatedAssistantName(value).ToLowerInvariant();
        canonical = Regex.Replace(
            canonical,
            @"^(?:正在|开始|准备|已经|已)|(?:已完成|完成|成功|进行中)$",
            string.Empty,
            RegexOptions.CultureInvariant);
        return Regex.Replace(canonical, @"[\s，。！？、,:：；;·._\-]+", string.Empty).Trim();
    }

    private static string MergeStreamingText(string current, string incoming, bool isCompleted)
    {
        if (string.IsNullOrEmpty(current))
        {
            return incoming;
        }

        if (string.IsNullOrEmpty(incoming) || current.EndsWith(incoming, StringComparison.Ordinal))
        {
            return current;
        }

        if (incoming.StartsWith(current, StringComparison.Ordinal))
        {
            return incoming;
        }

        if (current.Contains(incoming, StringComparison.Ordinal))
        {
            return current;
        }

        if (isCompleted)
        {
            // Completion notifications normally carry the authoritative full
            // answer. Replacing here prevents a reused stream from appending a
            // previous reply to the current one.
            return incoming;
        }

        var maximumOverlap = Math.Min(current.Length, incoming.Length);
        for (var overlap = maximumOverlap; overlap > 0; overlap--)
        {
            if (current.EndsWith(incoming[..overlap], StringComparison.Ordinal))
            {
                return current + incoming[overlap..];
            }
        }

        return current + incoming;
    }

    private static string NormalizeAssistantAnswer(string value, AgentActivityStatus status)
    {
        var cleaned = value.ReplaceLineEndings("\n");
        cleaned = Regex.Replace(
            cleaned,
            @"(?is)<(?:think|analysis)\b[^>]*>.*?(?:</(?:think|analysis)>|$)",
            string.Empty,
            RegexOptions.CultureInvariant);
        var finalMarkers = Regex.Matches(
            cleaned,
            @"(?im)^[ \t]*(?:#{1,6}[ \t]*)?(?:最终回答|最终答复|Final[ \t]+(?:answer|response))[ \t]*[:：]?[ \t]*",
            RegexOptions.CultureInvariant);
        if (finalMarkers.Count > 0)
        {
            var marker = finalMarkers[^1];
            cleaned = cleaned[(marker.Index + marker.Length)..];
        }

        cleaned = Regex.Replace(
            cleaned,
            @"(?im)^[ \t]*(?:#{1,6}[ \t]*)?(?:最终回答|最终答复|Final[ \t]+(?:answer|response))[ \t]*[:：]?[ \t]*",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"^[ \t]*回天(?:\s*AI)?[ \t]*[:：][ \t]*", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = ProductizeTechnicalActivityText(
            cleaned,
            AgentActivityKind.AssistantMessage,
            status);
        cleaned = cleaned.Trim();
        if (status == AgentActivityStatus.InProgress &&
            Regex.IsMatch(
                cleaned,
                @"^(?:#{1,6}\s*)?(?:思考过程|分析过程|推理过程|Reasoning|Analysis)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return "正在整理答复…";
        }

        return string.IsNullOrWhiteSpace(cleaned) && status == AgentActivityStatus.InProgress
            ? "正在整理答复…"
            : cleaned;
    }

    private static string ProductizeTechnicalActivityText(
        string value,
        AgentActivityKind kind,
        AgentActivityStatus status)
    {
        if (!Regex.IsMatch(
                value,
                @"(?:无障碍树|控件树|TreeScope|RuntimeId|UI\s*Automation|accessibility\s*tree|contextCompaction)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return value;
        }

        if (value.Contains("contextCompaction", StringComparison.OrdinalIgnoreCase))
        {
            return status == AgentActivityStatus.Completed
                ? "任务内容已经整理完成。"
                : "正在整理当前任务内容。";
        }

        if (kind == AgentActivityKind.AssistantMessage)
        {
            var subject = value.Contains("微信", StringComparison.OrdinalIgnoreCase)
                ? "微信窗口已打开，"
                : string.Empty;
            return subject + (status == AgentActivityStatus.Failed
                ? "当前界面暂时无法准确定位目标，这一步没有继续操作。"
                : "正在改用画面识别定位目标。 ");
        }

        return status switch
        {
            AgentActivityStatus.Completed => "已读取当前界面的可操作区域。",
            AgentActivityStatus.Failed => "当前界面读取受限，正在改用画面识别。",
            _ => "正在读取当前界面并定位可操作区域。",
        };
    }

    [Conditional("DEBUG")]
    private static void AssertConversationPresentationRules()
    {
        Debug.Assert(
            RemoveRepeatedAssistantName("回天正在分析当前任务。") == "正在分析当前任务。",
            "Timeline facts must not repeat the product name.");
        Debug.Assert(
            NormalizeAssistantAnswer("思考过程\n检查上下文\n最终回答：\n已经完成。", AgentActivityStatus.Completed) ==
            "已经完成。",
            "Only the final response should appear in the answer region.");
        Debug.Assert(
            MergeStreamingText("已完成第一步", "，正在核对结果。", false) ==
            "已完成第一步，正在核对结果。",
            "Streaming answer fragments must remain ordered.");
        Debug.Assert(
            CompactTimelineFact("已打开目标窗口。接下来将检查内容。") == "已打开目标窗口。",
            "A timeline row should contain one concise fact.");
    }

    private string HideForeignReasoning(string text, AgentActivityKind kind)
    {
        if (!_state.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            kind is not (AgentActivityKind.Reasoning or AgentActivityKind.Plan))
        {
            return text;
        }

        var chinese = text.Count(character => character is >= '\u3400' and <= '\u9fff');
        var latin = text.Count(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (latin >= 6 && chinese * 2 < latin)
        {
            return kind == AgentActivityKind.Plan
                ? "正在整理下一步计划。"
                : "正在分析任务目标并检查下一步。";
        }

        return text;
    }

    private void RenderPlan(AgentPlan plan)
    {
        PlanItems.Children.Clear();
        if (plan.Steps.Count == 0)
        {
            PlanItems.Children.Add(CreatePlanStep("等待任务", AgentActivityStatus.Pending));
            return;
        }

        foreach (var step in plan.Steps)
        {
            PlanItems.Children.Add(CreatePlanStep(
                HideForeignReasoning(step.Text, AgentActivityKind.Plan),
                step.Status));
        }
    }

    private Border CreatePlanStep(string text, AgentActivityStatus status)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = StatusBrush(status),
            Margin = new Thickness(0, 5, 9, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = ResourceBrush("SecondaryTextBrush"),
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return new Border
        {
            Child = row,
            Background = ResourceBrush("PanelBrush"),
            BorderBrush = ResourceBrush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    private void ShowApproval(AgentApproval approval)
    {
        _controlOverlay?.HideControl();
        _pendingApprovalId = approval.RequestId;
        ApprovalTitle.Text = approval.Title;
        ApprovalDetail.Text = approval.Detail;
        ApprovalPanel.Visibility = Visibility.Visible;
        var takeover = approval.Method == "huitian/userIntervention";
        AcceptApprovalButton.Content = takeover ? L("已处理，继续") : L("确认继续");
        DeclineApprovalButton.Content = takeover ? L("停止任务") : L("取消");
        RememberApprovalCheckBox.Visibility = takeover ? Visibility.Collapsed : Visibility.Visible;
        CurrentStatusText.Text = takeover ? approval.Title : L("等待重要操作确认");
        _voiceController.SelectVoice(_state.VoiceName);
        StartApprovalAttention(approval.RequestId, takeover ? $"回天需要你处理：{approval.Title}。请看一下电脑。" : null);
    }

    private void StartApprovalAttention(long requestId, string? announcement = null)
    {
        _approvalAttentionCancellation?.Cancel();
        _approvalAttentionCancellation?.Dispose();
        _approvalAttentionCancellation = new CancellationTokenSource();
        var token = _approvalAttentionCancellation.Token;
        System.Media.SystemSounds.Exclamation.Play();
        _voiceController.SpeakAttention(announcement ?? "回天需要你确认一项重要操作，请看一下电脑。 ");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(9), token);
                if (!token.IsCancellationRequested && _pendingApprovalId == requestId)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        System.Media.SystemSounds.Exclamation.Play();
                        _voiceController.SpeakAttention("这一步需要你本人操作，请看一下电脑。 ");
                    });
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void UpdateConnection(AgentConnectionState state)
    {
        _agentConnected = state.IsConnected;
        if (!state.IsConnected)
        {
            _brainReady = false;
        }
        else if (state.IsBrainReady)
        {
            _brainReady = true;
        }

        ConnectionStatusText.Text = L(state.UserFacingStatus);
        ConnectionDot.Fill = new SolidColorBrush(state.IsConnected
            ? Color.FromRgb(112, 214, 161)
            : Color.FromRgb(214, 121, 112));
        BrainStatusText.Text = L(_brainReady ? "本机智能核心已就绪" : "本机智能核心待准备");
    }

    private void ResetTaskView()
    {
        _followActivityTail = true;
        _inputHistoryIndex = -1;
        _inputHistoryDraft = string.Empty;
        _activityVisuals.Clear();
        _processGroups.Clear();
        _activeComputerItems.Clear();
        _savedActivityIds.Clear();
        _artifactPaths.Clear();
        _inlineArtifactPaths.Clear();
        _completedActionTitles.Clear();
        ActivityItems.Children.Clear();
        ActivityItems.Children.Add(WelcomePanel);
        WelcomePanel.Visibility = Visibility.Visible;
        PlanItems.Children.Clear();
        PlanItems.Children.Add(CreatePlanStep("等待任务", AgentActivityStatus.Pending));
        // A background turn may already be waiting for login. Keep its
        // takeover controls reachable when opening a new task; hiding them
        // strands the pending tool request and queues all future messages.
        if (!_detachedFromActiveTurn || _pendingApprovalId is null)
        {
            ApprovalPanel.Visibility = Visibility.Collapsed;
            _pendingApprovalId = null;
            _approvalAttentionCancellation?.Cancel();
        }
        MainTaskTitle.Text = L("新的本机任务");
        CurrentStatusText.Text = L("准备接收任务");
        StopButton.Visibility = Visibility.Collapsed;
        StopButton.IsEnabled = true;
        StopButton.Content = L("停止");
        SendButton.IsEnabled = _agentConnected && _brainReady && !_detachedFromActiveTurn;
        SendButton.Content = L("发送");
        _taskStartedAt = null;
        _lastTaskDuration = null;
        TaskElapsedText.Text = string.Empty;
        ClearAttachments();
        RenderArtifacts();
        _controlOverlay?.HideControl();
        _isProgrammaticActivityScroll = true;
        try
        {
            ActivityScroll.ScrollToTop();
        }
        finally
        {
            _isProgrammaticActivityScroll = false;
        }
        MessageInput.Focus();
    }

    private async void RecentTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedTask task })
        {
            return;
        }

        if (_agentClient.HasActiveTurn)
        {
            AddLocalAssistantMessage("请先等待当前任务结束，再打开其他任务记录。 ");
            return;
        }

        Interlocked.Increment(ref _submissionGeneration);
        _intentCancellation?.Cancel();
        await SaveCurrentTaskAsync();
        Interlocked.Increment(ref _activityViewGeneration);
        _currentTask = task;
        _currentCommunicationTargets = null;
        _isRestoringHistory = true;
        _followActivityTail = true;
        _inputHistoryIndex = -1;
        _inputHistoryDraft = string.Empty;
        _activityVisuals.Clear();
        _processGroups.Clear();
        _savedActivityIds.Clear();
        ActivityItems.Children.Clear();
        MainTaskTitle.Text = task.Title;
        WelcomePanel.Visibility = Visibility.Collapsed;
        foreach (var message in task.Messages)
        {
            var restoredText = message.Attachments.Count == 0
                ? message.Text
                : message.Text + "\n" + string.Join(
                    "\n",
                    message.Attachments.Select(path => "📎 " + Path.GetFileName(path)));
            AddOrUpdateActivity(new AgentActivity(
                $"saved-{task.Id}-{Guid.NewGuid():N}",
                message.Role == "user" ? AgentActivityKind.UserMessage : AgentActivityKind.AssistantMessage,
                string.Empty,
                restoredText,
                AgentActivityStatus.Completed,
                message.Timestamp));
        }

        _isRestoringHistory = false;
        _artifactPaths.Clear();
        foreach (var path in task.Artifacts.Where(path => File.Exists(path) || Directory.Exists(path)))
        {
            _artifactPaths.Add(path);
            AddInlineArtifactCard(path);
        }

        RenderArtifacts();
        CurrentStatusText.Text = L("已打开本机任务记录");
        var hasActiveConversation = _activeCommunicationSessions.Values.Any(session =>
            session.TaskId == task.Id);
        StopButton.Visibility = hasActiveConversation ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = hasActiveConversation;
        StopButton.Content = L("停止");
        if (hasActiveConversation)
            CurrentStatusText.Text = L("当前任务的持续聊天仍在后台运行");
        FollowActivityTailAfterLayout();
        MessageInput.Focus();
    }

    private void TaskSearchBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        RenderRecentTasks(TaskSearchBox.Text);

    private void RenderRecentTasks(string? filter = null)
    {
        if (RecentTasksPanel is null)
        {
            return;
        }

        RecentTasksPanel.Children.Clear();
        var tasks = _state.RecentTasks
            .Where(task => task.Messages.Count > 0)
            .Where(task => string.IsNullOrWhiteSpace(filter) ||
                           task.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                           task.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(task => task.UpdatedAt)
            .Take(30)
            .ToArray();
        if (tasks.Length == 0)
        {
            NoRecentTasksText = new TextBlock
            {
                Text = L(string.IsNullOrWhiteSpace(filter) ? "完成任务后会自动保存在本机" : "没有找到匹配的任务"),
                Margin = new Thickness(5, 10, 0, 0),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ResourceBrush("MutedBrush"),
            };
            RecentTasksPanel.Children.Add(NoRecentTasksText);
            return;
        }

        foreach (var task in tasks)
        {
            var title = new TextBlock
            {
                Text = task.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var time = new TextBlock
            {
                Text = $"{FriendlyTime(task.UpdatedAt)}  ·  {task.Messages.Count} {L("条消息")}",
                FontSize = 9,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = ResourceBrush("MutedBrush"),
            };
            var panel = new StackPanel();
            panel.Children.Add(title);
            panel.Children.Add(time);

            var openButton = new Button
            {
                Tag = task,
                Content = panel,
                Style = (Style)FindResource("NavButton"),
                ToolTip = task.Summary,
                Margin = new Thickness(0),
                Padding = new Thickness(10, 9, 38, 9),
            };
            openButton.Click += RecentTaskButton_OnClick;

            var deleteButton = new Button
            {
                Tag = task,
                Content = "×",
                ToolTip = L("删除任务"),
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 7, 7, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Style = (Style)FindResource("GhostButton"),
                FontSize = 16,
                Foreground = ResourceBrush("MutedBrush"),
            };
            deleteButton.Click += DeleteRecentTaskButton_OnClick;

            var cardGrid = new Grid();
            cardGrid.Children.Add(openButton);
            cardGrid.Children.Add(deleteButton);
            var card = new Border
            {
                Background = ResourceBrush("PanelRaisedBrush"),
                BorderBrush = ResourceBrush("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(0, 0, 0, 7),
                Child = cardGrid,
            };
            RecentTasksPanel.Children.Add(card);
        }
    }

    private async void DeleteRecentTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SavedTask task })
        {
            return;
        }

        if (_agentClient.HasActiveTurn && _currentTask?.Id == task.Id)
        {
            AddLocalAssistantMessage("当前任务正在执行，结束后即可删除这条记录。 ");
            return;
        }

        _state.RecentTasks.RemoveAll(item => item.Id == task.Id);
        if (_currentTask?.Id == task.Id)
        {
            _currentTask = new SavedTask { ThreadId = _agentClient.CurrentThreadId };
            ResetTaskView();
        }

        await SaveStateQuietlyAsync();
        RenderRecentTasks(TaskSearchBox.Text);
    }

    private async void SaveAssistantMessage(string text)
    {
        if (_isRestoringHistory || _currentTask is null)
        {
            return;
        }

        var cleanText = ConversationPolicy.SanitizeAssistantText(text);
        if (string.IsNullOrWhiteSpace(cleanText))
        {
            return;
        }

        _currentTask.Messages.Add(new SavedMessage
        {
            Role = "assistant",
            Text = cleanText,
            Timestamp = DateTimeOffset.Now,
        });
        var latestPending = _currentTask.Subtasks.LastOrDefault(subtask =>
            subtask.Status == "pending");
        if (latestPending is not null)
        {
            latestPending.ResultSummary = ShortenSingleLine(cleanText, 160);
            latestPending.UpdatedAt = DateTimeOffset.Now;
        }
        _currentTask.UpdatedAt = DateTimeOffset.Now;
        await SaveCurrentTaskAsync();
    }

    private async Task SaveCurrentTaskAsync()
    {
        if (_isRestoringHistory || _currentTask is null || _currentTask.Messages.Count == 0)
        {
            return;
        }

        _currentTask.Summary = ConversationPolicy.BuildConversationSummary(
            _currentTask.Messages.Select(message =>
                new ConversationMessageSnapshot(message.Role, message.Text)));
        var naturalTitle = ConversationPolicy.BuildNaturalTaskTitle(
            _currentTask.Subtasks.Select(subtask => subtask.Request));
        if (!string.IsNullOrWhiteSpace(naturalTitle))
        {
            _currentTask.Title = naturalTitle;
            MainTaskTitle.Text = _currentTask.Title;
        }
        _currentTask.UpdatedAt = DateTimeOffset.Now;
        _currentTask.Artifacts = _artifactPaths.OrderBy(path => path).ToList();
        var existingIndex = _state.RecentTasks.FindIndex(task => task.Id == _currentTask.Id);
        if (existingIndex >= 0)
        {
            _state.RecentTasks[existingIndex] = _currentTask;
        }
        else
        {
            _state.RecentTasks.Add(_currentTask);
        }

        await SaveStateQuietlyAsync();
        RenderRecentTasks(TaskSearchBox.Text);
    }

    private void RememberUserPreferences(string message)
    {
        if (!ConversationPolicy.TryExtractExplicitPreference(message, out var preference))
        {
            return;
        }

        preference = ShortenSingleLine(preference, 180);
        _state.UserPreferences.RemoveAll(value =>
            value.Equals(preference, StringComparison.OrdinalIgnoreCase));
        _state.UserPreferences.Add(preference);
        _ = SaveStateQuietlyAsync();
    }

    private string BuildRelevantMemory(string message, bool includeLearnedPaths = true)
    {
        var preferences = _state.UserPreferences
            .Where(preference => SelfEvolutionEngine.IsPreferenceRelevant(message, preference))
            .TakeLast(3)
            .Select(value => "- 用户偏好：" + ShortenSingleLine(value, 140));
        var learnings = includeLearnedPaths
            ? SelfEvolutionEngine
                .FindRelevant(_state.TaskLearnings, message, 2)
                .Select(item =>
                    $"- 已核验路径（{item.Category}，成功 {item.SuccessCount} 次）：" +
                    $"{ShortenSingleLine(item.Strategy, 160)}；核验：" +
                    ShortenSingleLine(item.VerificationEvidence, 100))
            : [];
        return string.Join("\n", preferences.Concat(learnings));
    }

    private void RememberTaskLearning(bool succeeded)
    {
        if (!succeeded || _currentTask is null || _completedActionTitles.Count == 0 ||
            string.IsNullOrWhiteSpace(_currentTurnRequest) ||
            !SelfEvolutionEngine.IsVerifiedSuccessfulStrategy(
                _currentTurnRequest,
                _completedActionTitles,
                out var evidence))
        {
            return;
        }

        var trigger = ShortenSingleLine(_currentTurnRequest, 120);
        var category = SelfEvolutionEngine.Classify(trigger);
        var strategy = string.Join(" → ", _completedActionTitles.TakeLast(12));
        var existing = _state.TaskLearnings.FirstOrDefault(item =>
            item.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
            item.Strategy.Equals(strategy, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new TaskLearning
            {
                Trigger = trigger,
                Category = category,
                Strategy = strategy,
                VerifiedSuccess = true,
                VerificationEvidence = evidence,
                UpdatedAt = DateTimeOffset.Now,
            };
            _state.TaskLearnings.Add(existing);
        }

        var priorRuns = existing.SuccessCount + existing.FailureCount;
        var durationSeconds = Math.Max(0, _lastTaskDuration?.TotalSeconds ?? 0);
        if (durationSeconds > 0)
        {
            existing.AverageDurationSeconds = priorRuns == 0
                ? durationSeconds
                : (existing.AverageDurationSeconds * priorRuns + durationSeconds) / (priorRuns + 1);
        }

        existing.SuccessCount++;
        existing.FailureCount = 0;
        existing.VerifiedSuccess = true;
        existing.VerificationEvidence = evidence;
        existing.Trigger = trigger;
        existing.UpdatedAt = DateTimeOffset.Now;
        existing.RefreshScore();

        _ = RunSelfEvolutionAsync(manual: false);
    }

    private void CompleteOpenSubtasks(bool succeeded, string? resultSummary)
    {
        if (_currentTask is null)
        {
            _ = _currentPerformanceSession?.CompleteAsync(succeeded);
            return;
        }

        CompleteTaskSubtasks(_currentTask, succeeded, resultSummary);
        _ = _currentPerformanceSession?.CompleteAsync(succeeded);
    }

    private static void CompleteTaskSubtasks(
        SavedTask task,
        bool succeeded,
        string? resultSummary)
    {

        var fallback = task.Messages
            .LastOrDefault(message => message.Role == "assistant")?.Text;
        foreach (var subtask in task.Subtasks.Where(subtask =>
                     subtask.Status == "pending"))
        {
            subtask.Status = succeeded ? "completed" : "failed";
            if (!string.IsNullOrWhiteSpace(resultSummary))
            {
                subtask.ResultSummary = ShortenSingleLine(resultSummary, 160);
            }
            else if (string.IsNullOrWhiteSpace(subtask.ResultSummary) &&
                     !string.IsNullOrWhiteSpace(fallback))
            {
                subtask.ResultSummary = ShortenSingleLine(fallback, 160);
            }

            subtask.UpdatedAt = DateTimeOffset.Now;
        }

        task.UpdatedAt = DateTimeOffset.Now;
    }

    private static bool IsMemoryRelevant(string message, string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return false;
        }

        string[] anchors = ["微信", "浏览器", "文件", "PPT", "Word", "Excel", "图片", "网页", "程序"];
        return anchors.Any(anchor =>
                   message.Contains(anchor, StringComparison.OrdinalIgnoreCase) &&
                   trigger.Contains(anchor, StringComparison.OrdinalIgnoreCase)) ||
               message.Contains(trigger, StringComparison.OrdinalIgnoreCase) ||
               trigger.Contains(ShortenSingleLine(message, 18), StringComparison.OrdinalIgnoreCase);
    }

    private void DiscoverArtifacts(AgentActivity activity)
    {
        var text = activity.Detail;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var isOutputOperation = activity.Kind == AgentActivityKind.FileChange ||
                                activity.Kind == AgentActivityKind.Tool &&
                                (activity.Title.Contains("创建", StringComparison.OrdinalIgnoreCase) ||
                                 activity.Title.Contains("制作", StringComparison.OrdinalIgnoreCase) ||
                                 activity.Title.Contains("整理文件", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("已创建：", StringComparison.OrdinalIgnoreCase) ||
                                 text.Contains("已移动到：", StringComparison.OrdinalIgnoreCase));
        if (!isOutputOperation || activity.Status != AgentActivityStatus.Completed)
        {
            return;
        }

        var changed = false;
        foreach (Match match in WindowsPathRegex.Matches(text))
        {
            var candidate = match.Value.Trim().TrimEnd('。', '，', ',', ';', '；', ')', '）', ']', '】', '"', '\'');
            try
            {
                candidate = Path.GetFullPath(candidate);
                if ((File.Exists(candidate) || Directory.Exists(candidate)) &&
                    IsSafeArtifactPath(candidate) &&
                    _artifactPaths.Add(candidate))
                {
                    changed = true;
                    AddInlineArtifactCard(candidate);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        if (changed)
        {
            RenderArtifacts();
            _ = SaveCurrentTaskAsync();
        }
    }

    private static bool IsSafeArtifactPath(string path)
    {
        string[] internalRoots =
        [
            @"D:\HuiTian\Runtime\",
            @"D:\HuiTian\Models\",
            @"D:\HuiTian\Downloads\",
            @"D:\HuiTian\Cache\",
            @"D:\HuiTian\Project\vendor\",
            Environment.GetFolderPath(Environment.SpecialFolder.Windows) + Path.DirectorySeparatorChar,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + Path.DirectorySeparatorChar,
        ];
        return !internalRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderArtifacts()
    {
        ArtifactItems.Children.Clear();
        if (_artifactPaths.Count == 0)
        {
            NoArtifactsPanel = new Border
            {
                Background = ResourceBrush("PanelBrush"),
                BorderBrush = ResourceBrush("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13, 11, 13, 11),
                Child = new TextBlock
                {
                    Text = L("尚无任务产物"),
                    FontSize = 11,
                    Foreground = ResourceBrush("MutedBrush"),
                },
            };
            ArtifactItems.Children.Add(NoArtifactsPanel);
            return;
        }

        foreach (var path in _artifactPaths.OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
        {
            var button = new Button
            {
                Tag = path,
                Style = (Style)FindResource("NavButton"),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = (Directory.Exists(path) ? "📁 " : "↗ ") + Path.GetFileName(path),
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                        new TextBlock
                        {
                            Text = path,
                            FontSize = 9,
                            Margin = new Thickness(0, 4, 0, 0),
                            Foreground = ResourceBrush("MutedBrush"),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    },
                },
                ToolTip = "点击打开\n" + path,
            };
            button.Click += ArtifactOpenButton_OnClick;
            ArtifactItems.Children.Add(button);
        }
    }

    private void AddInlineArtifactCard(string path)
    {
        if (!_inlineArtifactPaths.Add(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        var extension = Directory.Exists(path)
            ? string.Empty
            : Path.GetExtension(path).ToLowerInvariant();
        var textContent = TryReadArtifactPreview(path, extension);
        var content = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = (Directory.Exists(path) ? "文件夹 · " : FriendlyArtifactKind(extension) + " · ") +
                   Path.GetFileName(path),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResourceBrush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var ready = new TextBlock
        {
            Text = L("已生成"),
            FontSize = 10,
            Foreground = ResourceBrush("AccentBrush"),
            Margin = new Thickness(10, 2, 0, 0),
        };
        Grid.SetColumn(ready, 1);
        heading.Children.Add(ready);
        content.Children.Add(heading);

        if (ImageExtensions.Contains(extension))
        {
            try
            {
                content.Children.Add(new Border
                {
                    Margin = new Thickness(0, 11, 0, 0),
                    Background = ResourceBrush("PanelRaisedBrush"),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6),
                    Child = new Image
                    {
                        Source = LoadUnlockedBitmap(path),
                        MaxHeight = 280,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left,
                    },
                });
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException)
            {
            }
        }
        else if (!string.IsNullOrWhiteSpace(textContent))
        {
            content.Children.Add(new TextBox
            {
                Text = textContent,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 11, 0, 0),
                Padding = new Thickness(11),
                Background = ResourceBrush("PanelRaisedBrush"),
                Foreground = ResourceBrush("TextBrush"),
                BorderBrush = ResourceBrush("LineBrush"),
                BorderThickness = new Thickness(1),
                FontSize = 12,
            });
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = L("产物已保存在本机，点击下方按钮即可打开。"),
                Margin = new Thickness(0, 9, 0, 0),
                FontSize = 11,
                Foreground = ResourceBrush("MutedBrush"),
            });
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 11, 0, 0),
        };
        var open = new Button
        {
            Content = L("打开产物"),
            Tag = path,
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(12, 6, 12, 6),
        };
        open.Click += ArtifactOpenButton_OnClick;
        actions.Children.Add(open);
        var copy = new Button
        {
            Content = L(string.IsNullOrWhiteSpace(textContent) ? "复制位置" : "复制内容"),
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(11, 6, 11, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(textContent) ? path : textContent);
            copy.Content = L("已复制");
        };
        actions.Children.Add(copy);
        content.Children.Add(actions);
        content.Children.Add(new TextBlock
        {
            Text = path,
            FontSize = 9,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = ResourceBrush("MutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        ActivityItems.Children.Add(new Border
        {
            Child = content,
            Background = ResourceBrush("PanelBrush"),
            BorderBrush = ResourceBrush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(40, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });
    }

    private static string? TryReadArtifactPreview(string path, string extension)
    {
        string[] textExtensions =
        [
            ".txt", ".md", ".csv", ".json", ".log", ".html", ".css", ".js", ".ts", ".cs", ".py",
        ];
        if (!File.Exists(path) || !textExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[2400];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var value = new string(buffer, 0, read).Trim();
            return reader.Peek() >= 0 ? value + "\n…" : value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static BitmapSource LoadUnlockedBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static string FriendlyArtifactKind(string extension) => extension switch
    {
        ".pptx" => "演示文稿",
        ".docx" => "文档",
        ".xlsx" => "表格",
        ".pdf" => "PDF",
        ".png" or ".jpg" or ".jpeg" or ".webp" => "图片",
        ".txt" or ".md" => "文字内容",
        _ => "任务产物",
    };

    private void ArtifactOpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            AddLocalAssistantMessage($"暂时无法打开这个产物，但它仍保存在：{path}");
        }
    }

    private async void ArtifactPaneToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        _state.ArtifactsPaneExpanded = !_state.ArtifactsPaneExpanded;
        ApplyArtifactPaneState();
        await SaveStateQuietlyAsync();
    }

    private void ApplyArtifactPaneState()
    {
        if (_state.ArtifactsPaneExpanded)
        {
            ArtifactsColumn.Width = new GridLength(306);
            ArtifactsPane.Visibility = Visibility.Visible;
            ArtifactPaneToggleButton.Content = L("隐藏产物栏");
        }
        else
        {
            ArtifactsPane.Visibility = Visibility.Collapsed;
            ArtifactsColumn.Width = new GridLength(0);
            ArtifactPaneToggleButton.Content = L("显示产物栏");
        }

        UiLocalizer.Apply(ArtifactPaneToggleButton, _state.Language);
    }

    private async void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        PopulateSettingsControls();
        SettingsView.Visibility = Visibility.Visible;
        try
        {
            _explicitSocialStyleSamples = await _socialMemory.ReadExplicitUserStyleSamplesAsync();
            SocialStyleSamplesBox.Text = string.Join(Environment.NewLine, _explicitSocialStyleSamples);
            SocialStyleStatusText.Text = L($"已保存 {_explicitSocialStyleSamples.Count} 条本人样本；仅保存在此电脑。 ");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SocialStyleStatusText.Text = L("本机加密样本读取失败，原文件已保留，请勿在未核对前覆盖。 ");
            SocialStyleSamplesBox.IsEnabled = false;
        }
    }

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Theme buttons preview immediately. Closing without saving must not
        // leave a visual choice that differs from the persisted setting.
        _pendingTheme = _state.Theme;
        ApplyTheme(_state.Theme);
        SettingsView.Visibility = Visibility.Collapsed;
    }

    private void ThemeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string theme })
        {
            _pendingTheme = theme;
            ApplyTheme(theme);
        }
    }

    private void BrainModeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExternalBrainPanel is null)
        {
            return;
        }

        ExternalBrainPanel.Visibility = SelectedTag(BrainModeCombo) == "External"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var requestedMode = SelectedTag(BrainModeCombo) ?? "Automatic";
        if (requestedMode == "External" &&
            !TryValidateLocalServiceUri(ExternalServiceAddressBox.Text, out _))
        {
            MessageBox.Show(
                this,
                "为避免误连收费接口，请填写本机或局域网内的 http/https 服务地址。",
                "回天 AI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (requestedMode == "External" && string.IsNullOrWhiteSpace(ExternalServiceNameBox.Text))
        {
            MessageBox.Show(this, "请填写您的本机服务中的模型名称。", "回天 AI");
            return;
        }

        if (SocialStyleSamplesBox.IsEnabled)
        {
            var styleSamples = SocialStyleSamplesBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(sample => !string.IsNullOrWhiteSpace(sample))
                .ToArray();
            if (styleSamples.Length > 12 || styleSamples.Any(sample => sample.Length > 400))
            {
                MessageBox.Show(this, "本人说话样本最多 12 条，每条不超过 400 字。", "回天 AI");
                return;
            }
            try
            {
                await _socialMemory.SetExplicitUserStyleSamplesAsync(styleSamples);
                _explicitSocialStyleSamples = styleSamples;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                MessageBox.Show(this, "本人说话样本没有安全保存，其他设置也暂未保存。请检查 D 盘空间和当前 Windows 账户。", "回天 AI");
                return;
            }
        }

        var backendChanged = !_state.BrainMode.Equals(requestedMode, StringComparison.Ordinal) ||
                             !_state.ExternalServiceAddress.Equals(ExternalServiceAddressBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                             !_state.ExternalServiceName.Equals(ExternalServiceNameBox.Text.Trim(), StringComparison.Ordinal);
        _state.Theme = _pendingTheme;
        var selectedLanguage = SelectedTag(LanguageCombo) ?? "zh-CN";
        _state.Language = selectedLanguage;
        _state.SpeakAnswers = SpeakAnswersCheckBox.IsChecked == true;
        _state.VoiceName = (VoiceCombo.SelectedItem as VoiceOption)?.Id ?? string.Empty;
        _state.WakeWordEnabled = WakeWordEnabledCheckBox.IsChecked == true;
        _state.WakePhrase = string.IsNullOrWhiteSpace(WakePhraseBox.Text)
            ? "你好小天"
            : WakePhraseBox.Text.Trim();
        _state.IdleSelfEvolutionEnabled = IdleSelfEvolutionCheckBox.IsChecked == true;
        _state.AutonomyMode = SelectedTag(AutonomyModeCombo) ?? nameof(AutonomyMode.HighAutonomy);
        _agentClient.ConfigureAutonomy(ParseAutonomyMode(_state.AutonomyMode));
        _state.BrainMode = requestedMode;
        _state.ExternalServiceAddress = ExternalServiceAddressBox.Text.Trim();
        _state.ExternalServiceName = ExternalServiceNameBox.Text.Trim();
        _voiceController.SelectVoice(_state.VoiceName);
        _voiceController.ConfigureWakeWord(
            _state.WakeWordEnabled,
            _state.WakePhrase,
            _state.Language);
        await SaveStateQuietlyAsync();
        UiLocalizer.Apply(this, _state.Language);
        _voiceWindow?.ApplyLanguage(_state.Language);
        _controlOverlay?.ApplyLanguage(_state.Language);
        RefreshLanguageDependentUi();
        SettingsView.Visibility = Visibility.Collapsed;
        if (backendChanged)
        {
            MessageBox.Show(
                this,
                "智能核心设置已保存，将在下次启动回天时生效。",
                "回天 AI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void RescanFromSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsHardwareText.Text = L("正在重新检测本机配置……");
        await RunHardwareDiagnosticAsync();
    }

    private void PopulateSettingsControls()
    {
        SelectComboTag(LanguageCombo, _state.Language);
        SelectComboTag(AutonomyModeCombo, _state.AutonomyMode);
        SelectComboTag(BrainModeCombo, _state.BrainMode);
        ExternalServiceAddressBox.Text = _state.ExternalServiceAddress;
        ExternalServiceNameBox.Text = _state.ExternalServiceName;
        ExternalBrainPanel.Visibility = _state.BrainMode == "External" ? Visibility.Visible : Visibility.Collapsed;
        SpeakAnswersCheckBox.IsChecked = _state.SpeakAnswers;
        WakeWordEnabledCheckBox.IsChecked = _state.WakeWordEnabled;
        WakePhraseBox.Text = _state.WakePhrase;
        WakePhraseStatusText.Text = FormatWakePhraseStatus();
        IdleSelfEvolutionCheckBox.IsChecked = _state.IdleSelfEvolutionEnabled;
        SelfEvolutionStatusText.Text = FormatSelfEvolutionStatus();
        SocialStyleSamplesBox.IsEnabled = true;
        SocialStyleSamplesBox.Text = string.Join(Environment.NewLine, _explicitSocialStyleSamples);
        SocialStyleStatusText.Text = "样本仅保存在此电脑并按当前 Windows 用户加密。";
        PopulateVoiceOptions();
    }

    private async void StartSelfEvolutionButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunSelfEvolutionAsync(manual: true);

    private async void SelfEvolutionTimer_OnTick(object? sender, EventArgs e)
    {
        if (!_state.IdleSelfEvolutionEnabled || _selfEvolutionRunning ||
            _agentClient.HasActiveTurn || _voiceController.IsSessionActive ||
            GetSystemIdleDuration() < TimeSpan.FromMinutes(5) ||
            _state.LastSelfEvolutionAt is { } lastRun &&
            DateTimeOffset.Now - lastRun < TimeSpan.FromMinutes(30))
        {
            return;
        }

        await RunSelfEvolutionAsync(manual: false);
    }

    private async Task RunSelfEvolutionAsync(bool manual)
    {
        if (_selfEvolutionRunning)
        {
            return;
        }

        _selfEvolutionRunning = true;
        if (StartSelfEvolutionButton is not null)
        {
            StartSelfEvolutionButton.IsEnabled = false;
            SelfEvolutionStatusText.Text = L("正在复盘本机经验并校验能力……");
        }

        try
        {
            // Yield once so the settings panel can visibly enter the running
            // state before the local consolidation pass begins.
            await Task.Yield();
            var result = SelfEvolutionEngine.Run(_state);
            await SaveStateQuietlyAsync();
            if (SelfEvolutionStatusText is not null)
            {
                SelfEvolutionStatusText.Text = FormatSelfEvolutionStatus(result);
            }

            if (manual)
            {
                CurrentStatusText.Text = L("本机自我进化复盘已完成");
            }
        }
        finally
        {
            _selfEvolutionRunning = false;
            if (StartSelfEvolutionButton is not null)
            {
                StartSelfEvolutionButton.IsEnabled = true;
            }
        }
    }

    private void RefreshLanguageDependentUi()
    {
        ApplyArtifactPaneState();
        RenderRecentTasks();
        WakePhraseStatusText.Text = FormatWakePhraseStatus();
        SelfEvolutionStatusText.Text = FormatSelfEvolutionStatus();
        PopulateVoiceOptions();
        if (_currentHardware is not null)
        {
            SettingsHardwareText.Text = FormatHardwareDiagnostic(_currentHardware);
        }
        else
        {
            SettingsHardwareText.Text = L("个人版固定使用已配置的本机模型；硬件信息可按需手动查看。");
        }

        ConnectionStatusText.Text = _agentConnected
            ? L("回天已就绪")
            : L("本机准备中");
        BrainStatusText.Text = _brainReady
            ? L(_backendConfiguration.IsProductDefault
                ? "本机智能核心已就绪"
                : "您的本机智能服务已连接")
            : L("本机智能核心待准备");
        if (ApprovalPanel.Visibility == Visibility.Visible)
        {
            CurrentStatusText.Text = L("等待重要操作确认");
        }
        else
        {
            CurrentStatusText.Text = L(_agentClient.HasActiveTurn ? "任务进行中" : "准备接收任务");
        }

        SendButton.Content = L(_agentClient.HasActiveTurn ? "追加" : "发送");
    }

    private void PopulateVoiceOptions()
    {
        if (VoiceCombo is null)
        {
            return;
        }

        var selectedId = (VoiceCombo.SelectedItem as VoiceOption)?.Id;
        VoiceCombo.Items.Clear();
        foreach (var voice in _voiceController.InstalledVoices)
        {
            VoiceCombo.Items.Add(new VoiceOption(voice.Id, LocalizedVoiceName(voice)));
        }

        if (VoiceCombo.Items.Count == 0)
        {
            VoiceCombo.Items.Add(new VoiceOption(string.Empty, L("本机自然音色")));
        }

        var preferredId = string.IsNullOrWhiteSpace(selectedId) ? _state.VoiceName : selectedId;
        VoiceCombo.SelectedItem = VoiceCombo.Items.Cast<object>()
            .OfType<VoiceOption>()
            .FirstOrDefault(value => value.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
            ?? VoiceCombo.Items[0];
    }

    private string LocalizedVoiceName(VoiceOption voice)
    {
        var parts = voice.DisplayName.Split('（', 2);
        var baseName = parts[0];
        var female = voice.DisplayName.Contains("女声", StringComparison.Ordinal);
        return _state.Language switch
        {
            "en-US" => $"{baseName} ({(female ? "female" : "male")})",
            "ja-JP" => $"{baseName}（{(female ? "女性" : "男性")}）",
            _ => voice.DisplayName,
        };
    }

    private string FormatSelfEvolutionStatus(SelfEvolutionResult? result = null)
    {
        if (_state.SelfEvolutionRunCount == 0)
        {
            return L("尚未完成第一次本机复盘");
        }

        var successful = result?.SuccessfulRoutes ??
                         _state.TaskLearnings.Count(item =>
                             item.VerifiedSuccess && item.SuccessCount > 0);
        return _state.Language switch
        {
            "en-US" when successful == 0 => "Local review complete. Verified workflows will be learned after real tasks succeed.",
            "en-US" => $"Reviewed and retained {successful} verified local workflows.",
            "ja-JP" when successful == 0 => "ローカル確認が完了しました。実タスクで検証した成功手順のみ学習します。",
            "ja-JP" => $"検証済みの成功手順 {successful} 件を保持しています。",
            _ when successful == 0 => "已完成本机自检；完成并核验真实任务后会开始积累成功路径。",
            _ => $"已复盘并保留 {successful} 条经过结果核验的成功路径。",
        };
    }

    private string FormatWakePhraseStatus() => _state.Language switch
    {
        "en-US" when _state.WakeWordEnabled => $"Current wake phrase: {_state.WakePhrase}",
        "en-US" => "Voice wake is off",
        "ja-JP" when _state.WakeWordEnabled => $"現在の起動フレーズ：{_state.WakePhrase}",
        "ja-JP" => "音声起動はオフです",
        _ when _state.WakeWordEnabled => $"当前唤醒词：{_state.WakePhrase}",
        _ => "语音唤醒已关闭",
    };

    private void ApplyTheme(string theme)
    {
        var colors = theme switch
        {
            "Light" => new ThemeColors(
                "#EEF5FA", "#E7F0F7", "#FFFFFF", "#F2F8FC", "#087B96",
                "#13283A", "#3F596C", "#61788A", "#CCDDE7", "#DDF5EB", "#16714E"),
            "Warm" => new ThemeColors(
                "#17131D", "#1E1724", "#261D2C", "#30243A", "#F5A675",
                "#FFF4F2", "#DAC8D1", "#B89FAE", "#513E54", "#15392F", "#A5E5CC"),
            _ => new ThemeColors(
                "#080F1B", "#0B1424", "#101D30", "#14243A", "#56D6F4",
                "#EFF8FF", "#B9CCDC", "#8DA6BA", "#263C54", "#123B40", "#A7F1D2"),
        };
        SetBrushColor("PageBrush", colors.Page);
        SetBrushColor("SidebarBrush", colors.Sidebar);
        SetBrushColor("PanelBrush", colors.Panel);
        SetBrushColor("PanelRaisedBrush", colors.Raised);
        SetBrushColor("AccentBrush", colors.Accent);
        SetBrushColor("GoldBrush", theme switch
        {
            "Light" => "#6656C8",
            "Warm" => "#C4A5FF",
            _ => "#9A7BFF",
        });
        SetBrushColor("GoldSoftBrush", theme switch
        {
            "Light" => "#336656C8",
            "Warm" => "#55C4A5FF",
            _ => "#669A7BFF",
        });
        SetBrushColor("AccentContrastBrush", theme switch
        {
            "Light" => "#FFFFFF",
            "Warm" => "#211724",
            _ => "#091622",
        });
        SetBrushColor("TextBrush", colors.Text);
        SetBrushColor("SecondaryTextBrush", colors.Secondary);
        SetBrushColor("MutedBrush", colors.Muted);
        SetBrushColor("LineBrush", colors.Line);
        SetBrushColor("SuccessPanelBrush", colors.SuccessPanel);
        SetBrushColor("SuccessTextBrush", colors.SuccessText);
        var userBubble = theme == "Light" ? "#18778D" : theme == "Warm" ? "#573348" : "#183B56";
        var userBorder = theme == "Light" ? "#0E6075" : theme == "Warm" ? "#A871A6" : "#3CB9D4";
        var userText = "#FFFFFFFF";
        SetBrushColor("UserBubbleBrush", userBubble);
        SetBrushColor("UserBubbleBorderBrush", userBorder);
        SetBrushColor("UserBubbleTextBrush", userText);
        ApplyTitleBarTheme(theme);
    }

    private void ApplyTitleBarTheme(string theme)
    {
        // Native caption attributes are supported on Windows 11. Keep the
        // ordinary system frame on older Windows instead of custom chrome.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = theme == "Light" ? 0 : 1;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var caption = (Color)ColorConverter.ConvertFromString(theme switch
        {
            "Light" => "#E7F0F7",
            "Warm" => "#1E1724",
            _ => "#0B1424",
        });
        var text = (Color)ColorConverter.ConvertFromString(theme == "Light"
            ? "#13283A"
            : "#EFF8FF");
        var captionColorRef = caption.R | caption.G << 8 | caption.B << 16;
        var textColorRef = text.R | text.G << 8 | text.B << 16;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColorRef, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref textColorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);

    private void SetBrushColor(string key, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        var resources = System.Windows.Application.Current.Resources;
        // Code-created cards keep references returned by ResourceBrush().
        // Replacing a resource only updates DynamicResource consumers and can
        // leave those cards in the previous palette, with unreadable text.
        if (resources[key] is SolidColorBrush { IsFrozen: false } brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private void MainWindow_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            e.Handled = true;
            NewTaskButton_OnClick(NewTaskButton, new RoutedEventArgs());
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            e.Handled = true;
            VoiceButton_OnClick(VoiceButton, new RoutedEventArgs());
        }
        else if (e.Key == Key.Escape && StopButton.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            StopButton_OnClick(StopButton, new RoutedEventArgs());
        }
    }

    private void ClockTimer_OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        foreach (var visual in _activityVisuals.Values.Where(value =>
                     value.LastStatus == AgentActivityStatus.InProgress && value.Elapsed is not null))
        {
            visual.Elapsed!.Text = FormatElapsed(now - visual.StartedAt);
        }

        foreach (var group in _processGroups.Values)
        {
            var duration = group.FinalDuration ?? (now - group.StartedAt);
            group.Elapsed.Text = FormatElapsed(duration);
        }

        if (_taskStartedAt is { } started)
        {
            var duration = _lastTaskDuration ?? now - started;
            TaskElapsedText.Text = $"{L("已用时")} {FormatElapsed(duration)}";
        }
    }

    private ComputerControlOverlayWindow EnsureControlOverlay()
    {
        _controlOverlay ??= new ComputerControlOverlayWindow();
        return _controlOverlay;
    }

    private async Task<string> CaptureCurrentScreenAsync()
    {
        var originalState = WindowState;
        Hide();
        try
        {
            await Task.Delay(280);
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new System.Drawing.Bitmap(
                bounds.Width,
                bounds.Height,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bitmap.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
            var directory = Path.Combine(_paths.Temp, "Screen");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"screen-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        finally
        {
            Show();
            WindowState = originalState;
            Activate();
        }
    }

    private static bool ShouldObserveScreen(string text)
    {
        string[] keywords =
        [
            "屏幕", "窗口", "点击", "输入", "滚动", "浏览器", "网页", "网站", "电脑上",
            "帮我打开", "帮我操作", "关闭程序", "切换到", "Word", "Excel", "PPT", "安装",
            "微信", "聊天", "联系人",
        ];
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static AgentBackendConfiguration BuildBackendConfiguration(ProductState state)
    {
        if (state.BrainMode == "External" &&
            TryValidateLocalServiceUri(state.ExternalServiceAddress, out var uri) &&
            !string.IsNullOrWhiteSpace(state.ExternalServiceName))
        {
            return AgentBackendConfiguration.External(state.ExternalServiceName, uri.AbsoluteUri);
        }

        return AgentBackendConfiguration.ProductDefault;
    }

    private static bool TryValidateLocalServiceUri(string value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            return uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 10 || bytes[0] == 127 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal,
            _ => false,
        };
    }

    private async Task SaveStateQuietlyAsync()
    {
        try
        {
            await _stateStore.SaveAsync(_state);
        }
        catch (IOException)
        {
            // Keep the running task usable; the next state change will retry the local save.
        }
    }

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _clockTimer.Stop();
        _selfEvolutionTimer.Stop();
        foreach (var session in _activeCommunicationSessions.Values)
        {
            try { session.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _quickTaskCancellation?.Cancel();
        _approvalAttentionCancellation?.Cancel();
        _approvalAttentionCancellation?.Dispose();
        await SaveCurrentTaskAsync();
        _voiceWindow?.Close();
        _voiceController.Dispose();
        _controlOverlay?.Close();
        await _agentClient.DisposeAsync();
        _quickTaskCancellation?.Dispose();
        _chatSemanticRouter.Dispose();
        _intentCancellation?.Cancel();
        _communicationIntentResolver.Dispose();
        _communicationTurnComposer.Dispose();
        await _responsesBridge.DisposeAsync();
        await _brainServer.DisposeAsync();
        _downloadEngine.Dispose();
        Close();
    }

    private static TimeSpan GetSystemIdleDuration()
    {
        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>(),
        };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.TickCount);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

    private string L(string source) => UiLocalizer.Translate(_state.Language, source);

    private string FormatHardwareDiagnostic(HardwareProfile hardware) =>
        _state.Language switch
        {
            "en-US" =>
                "Personal model: fixed; hardware shown for diagnostics only.\n" +
                $"{hardware.CpuName} · {hardware.LogicalProcessorCount} threads\n" +
                $"{hardware.GpuName} · VRAM {hardware.VideoMemoryGiB:0.#} GB · " +
                $"Memory {hardware.PhysicalMemoryGiB:0.#} GB · Available {hardware.AvailableStorageGiB:0} GB",
            "ja-JP" =>
                "個人用モデルは固定です。以下は診断情報のみです。\n" +
                $"{hardware.CpuName} · {hardware.LogicalProcessorCount} スレッド\n" +
                $"{hardware.GpuName} · VRAM {hardware.VideoMemoryGiB:0.#} GB · " +
                $"メモリ {hardware.PhysicalMemoryGiB:0.#} GB · 空き容量 {hardware.AvailableStorageGiB:0} GB",
            _ =>
                "个人版模型固定；以下仅供硬件诊断。\n" +
                $"{hardware.CpuName} · {hardware.LogicalProcessorCount} 线程\n" +
                $"{hardware.GpuName} · 显存 {hardware.VideoMemoryGiB:0.#} GB · " +
                $"内存 {hardware.PhysicalMemoryGiB:0.#} GB · 可用空间 {hardware.AvailableStorageGiB:0} GB",
        };

    private string FormatTierRequirement(CapabilityTier tier) => _state.Language switch
    {
        "en-US" => $"Recommended: {TierRecommendedHardware(tier)}. All four tiers have the same features; only speed, accuracy, context length, and complex-task capacity improve.",
        "ja-JP" => $"推奨構成：{TierRecommendedHardware(tier)}。4段階の機能は同じで、速度、精度、コンテキスト、複雑なタスク能力だけが向上します。",
        _ => $"建议配置：{tier.RecommendedHardware}。四阶功能完全相同，只提升速度、精度、上下文和复杂任务能力。",
    };

    private string TierRecommendedHardware(CapabilityTier tier) => (_state.Language, tier.Id) switch
    {
        ("en-US", CapabilityTierId.Huang) => "16+ GB memory | 6–8 GB VRAM | 20+ GB free space",
        ("en-US", CapabilityTierId.Xuan) => "24+ GB memory | 12–16 GB VRAM | 24+ GB free space",
        ("en-US", CapabilityTierId.Di) => "32–64 GB memory | 20–24 GB VRAM | 32+ GB free space",
        ("en-US", CapabilityTierId.Tian) => "64+ GB memory | 32+ GB VRAM | 40+ GB free space",
        ("ja-JP", CapabilityTierId.Huang) => "メモリ16 GB以上｜VRAM 6～8 GB｜空き容量20 GB以上",
        ("ja-JP", CapabilityTierId.Xuan) => "メモリ24 GB以上｜VRAM 12～16 GB｜空き容量24 GB以上",
        ("ja-JP", CapabilityTierId.Di) => "メモリ32～64 GB｜VRAM 20～24 GB｜空き容量32 GB以上",
        ("ja-JP", CapabilityTierId.Tian) => "メモリ64 GB以上｜VRAM 32 GB以上｜空き容量40 GB以上",
        _ => tier.RecommendedHardware,
    };

    private string TierSummary(CapabilityTier tier) => (_state.Language, tier.Id) switch
    {
        ("en-US", CapabilityTierId.Huang) => "Full feature set for everyday use",
        ("en-US", CapabilityTierId.Xuan) => "Faster understanding and multi-step work",
        ("en-US", CapabilityTierId.Di) => "For complex tasks and sustained operation",
        ("en-US", CapabilityTierId.Tian) => "For intensive multitasking and long-running work",
        ("ja-JP", CapabilityTierId.Huang) => "日常利用向けの全機能",
        ("ja-JP", CapabilityTierId.Xuan) => "より速い理解と複数手順の作業",
        ("ja-JP", CapabilityTierId.Di) => "複雑なタスクと継続操作向け",
        ("ja-JP", CapabilityTierId.Tian) => "高負荷の複数タスクと長時間稼働向け",
        _ => tier.Summary,
    };

    private static Brush StatusBrush(AgentActivityStatus status) => new SolidColorBrush(status switch
    {
        AgentActivityStatus.Completed => Color.FromRgb(112, 214, 161),
        AgentActivityStatus.InProgress => Color.FromRgb(216, 182, 106),
        AgentActivityStatus.WaitingForApproval => Color.FromRgb(234, 157, 92),
        AgentActivityStatus.Failed => Color.FromRgb(214, 121, 112),
        AgentActivityStatus.Declined => Color.FromRgb(151, 158, 166),
        _ => Color.FromRgb(101, 113, 126),
    });

    private static string FriendlyStartupError(Exception exception) => exception switch
    {
        FileNotFoundException => "本机能力文件缺失或大小不符。请恢复已有安装后重试；不会自动下载模型。",
        HttpRequestException => "本机服务连接失败，请检查服务状态后重试。",
        InvalidDataException => "本机能力完整性校验未通过，回天已保留诊断记录。",
        _ => "本机能力暂时未能启动，可点击重试；详细状态见本机日志。",
    };

    private string FormatElapsed(TimeSpan duration) => _state.Language switch
    {
        "en-US" => duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s"
            : $"{Math.Max(0, duration.Seconds)}s",
        "ja-JP" => duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes}分 {duration.Seconds}秒"
            : $"{Math.Max(0, duration.Seconds)}秒",
        _ => duration.TotalMinutes >= 1
            ? $"{(int)duration.TotalMinutes} 分 {duration.Seconds} 秒"
            : $"{Math.Max(0, duration.Seconds)} 秒",
    };

    private string FriendlyTime(DateTimeOffset time)
    {
        var difference = DateTimeOffset.Now - time;
        return _state.Language switch
        {
            "en-US" when difference.TotalMinutes < 1 => "Just now",
            "en-US" when difference.TotalHours < 1 => $"{(int)difference.TotalMinutes} min ago",
            "en-US" when difference.TotalDays < 1 => $"{(int)difference.TotalHours} hr ago",
            "en-US" => time.ToString("MMM d HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            "ja-JP" when difference.TotalMinutes < 1 => "たった今",
            "ja-JP" when difference.TotalHours < 1 => $"{(int)difference.TotalMinutes}分前",
            "ja-JP" when difference.TotalDays < 1 => $"{(int)difference.TotalHours}時間前",
            "ja-JP" => time.ToString("M月d日 HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ja-JP")),
            _ when difference.TotalMinutes < 1 => "刚刚",
            _ when difference.TotalHours < 1 => $"{(int)difference.TotalMinutes} 分钟前",
            _ when difference.TotalDays < 1 => $"{(int)difference.TotalHours} 小时前",
            _ => time.ToString("M月d日 HH:mm"),
        };
    }

    private static string CleanForSpeech(string value)
    {
        var cleaned = Regex.Replace(value, @"\[(.*?)\]\([^)]*\)", "$1");
        cleaned = Regex.Replace(cleaned, @"https?://\S+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = cleaned
            .Replace("#", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        cleaned = Regex.Replace(cleaned, @"[\p{Cs}\p{So}\p{Sk}\uFE0F\u200D]", string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?m)^\s*[-+>]\s*", string.Empty);
        return Regex.Replace(cleaned, @"[ \t]+", " ").Trim();
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private static string ShortenSingleLine(string value, int maxLength)
    {
        var normalized = Regex.Replace(value.ReplaceLineEndings(" "), @"\s+", " ").Trim();
        return Shorten(normalized, maxLength);
    }

    private static string? SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private static AutonomyMode ParseAutonomyMode(string? value) =>
        Enum.TryParse<AutonomyMode>(value, true, out var mode)
            ? mode
            : AutonomyMode.HighAutonomy;

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<object>()
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.Cast<object>().OfType<ComboBoxItem>().FirstOrDefault();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint TickCount;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private sealed class ActivityVisual(
        Border container,
        TextBlock? title,
        TextBox detail,
        System.Windows.Shapes.Ellipse? status,
        AgentActivityStatus lastStatus,
        DateTimeOffset startedAt,
        AgentActivityKind kind,
        TextBlock? elapsed = null,
        string rawDetail = "")
    {
        public Border Container { get; } = container;
        public TextBlock? Title { get; } = title;
        public TextBox Detail { get; } = detail;
        public System.Windows.Shapes.Ellipse? Status { get; } = status;
        public AgentActivityStatus LastStatus { get; set; } = lastStatus;
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public AgentActivityKind Kind { get; } = kind;
        public TextBlock? Elapsed { get; } = elapsed;
        public string RawDetail { get; set; } = rawDetail;
    }

    private sealed class ProcessGroupVisual(
        Border container,
        StackPanel items,
        TextBlock elapsed,
        TextBlock state,
        DateTimeOffset startedAt,
        AgentActivityStatus lastStatus)
    {
        public Border Container { get; } = container;
        public StackPanel Items { get; } = items;
        public TextBlock Elapsed { get; } = elapsed;
        public TextBlock State { get; } = state;
        public DateTimeOffset StartedAt { get; set; } = startedAt;
        public AgentActivityStatus LastStatus { get; set; } = lastStatus;
        public string? BoundTurnId { get; set; }
        public ActivityVisual? LastVisual { get; set; }
        public string? LastMergeKey { get; set; }
        public ActivityVisual? GenericReasoningVisual { get; set; }
        public bool AwaitingRecoveryTurn { get; set; }
        public TimeSpan? FinalDuration { get; set; }
    }

    private sealed record ProcessActivityPresentation(
        string Title,
        string Detail,
        string MergeKey,
        bool IsRecovery,
        bool IsGenericReasoning);

    private sealed record SocialDiscoveryTaskContext(
        CommunicationTargetSlots Targets,
        DateTimeOffset StartedAt);

    private sealed record ThemeColors(
        string Page,
        string Sidebar,
        string Panel,
        string Raised,
        string Accent,
        string Text,
        string Secondary,
        string Muted,
        string Line,
        string SuccessPanel,
        string SuccessText);
}
