using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Diagnostics;
using System.Threading.Channels;
using System.Text.RegularExpressions;

namespace HuiTian.App;

internal sealed record VoiceOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

internal sealed class VoiceConversationController : IDisposable
{
    private const int FirstStreamingSpeechChunkLength = 24;
    private const int FollowingStreamingSpeechChunkLength = 44;

    private static readonly IReadOnlyList<VoiceOption> CuratedNaturalVoices =
    [
        new VoiceOption("huitian-natural:0", "清和（女声·自然）"),
    ];

    private enum NeuralRecognitionMode
    {
        None,
        Wake,
        Conversation,
        CaptureWakePhrase,
    }

    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly NaturalVoiceSynthesizer _naturalSynthesizer = new(
        @"D:\HuiTian\Runtime\Voice\MeloTTS");
    private readonly NeuralSpeechRecognizer _neuralRecognizer = new(
        @"D:\HuiTian\Runtime\Voice\ASR\sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30",
        @"D:\HuiTian\Runtime\Voice\ASR\sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23");
    private readonly object _utteranceSync = new();
    private SpeechRecognitionEngine? _wakeRecognizer;
    private SpeechRecognitionEngine? _conversationRecognizer;
    private SpeechRecognitionEngine? _captureRecognizer;
    private bool _sessionActive;
    private bool _muted;
    private bool _wakeEnabled = true;
    private bool _resumeConversationAfterSpeech;
    private bool _resumeWakeAfterSpeech;
    private bool _disposed;
    private string _wakePhrase = "你好小天";
    private string _language = "zh-CN";
    private int _naturalSpeakerId = -1;
    private NeuralRecognitionMode _neuralMode;
    private bool _neuralRecognitionUnavailable;
    private CancellationTokenSource? _speechCancellation;
    private Channel<string>? _speechQueue;
    private int _speechGeneration;
    private string? _streamResponseId;
    private string _streamResponseText = string.Empty;
    private string _streamPendingText = string.Empty;
    private bool _streamSpeechStarted;
    private CancellationTokenSource? _utteranceSilenceCancellation;
    private string _pendingUtterance = string.Empty;
    private int _utteranceGeneration;
    private long _utteranceStartedTimestamp;
    private CancellationTokenSource? _wakeListeningRetryCancellation;
    private CancellationTokenSource? _wakePhraseSilenceCancellation;
    private string _pendingWakePhrase = string.Empty;
    private int _wakePhraseGeneration;
    private long _wakePhraseStartedTimestamp;

    public VoiceConversationController()
    {
        _synthesizer.Rate = 1;
        _synthesizer.Volume = 100;
        _neuralRecognizer.PartialTextChanged += NeuralRecognizer_OnPartialTextChanged;
        _neuralRecognizer.TextRecognized += NeuralRecognizer_OnTextRecognized;
        _neuralRecognizer.AudioLevelChanged += NeuralRecognizer_OnAudioLevelChanged;
        _neuralRecognizer.Failed += NeuralRecognizer_OnFailed;
        _ = _naturalSynthesizer.WarmUpAsync();
    }

    public event EventHandler<string>? PartialTextChanged;

    public event EventHandler<string>? TextRecognized;

    public event EventHandler<string>? StateChanged;

    public event EventHandler<string>? WakeWordDetected;

    public event EventHandler<string>? WakePhraseCaptured;

    public event EventHandler<int>? AudioLevelChanged;

    public IReadOnlyList<VoiceOption> InstalledVoices
    {
        get
        {
            var duplicateNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var voices = new List<VoiceOption>();
            if (_naturalSynthesizer.IsAvailable)
            {
                voices.AddRange(CuratedNaturalVoices);
                return voices;
            }

            voices.AddRange(_synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice =>
                {
                    var baseName = FriendlyVoiceName(
                        voice.VoiceInfo.Culture,
                        voice.VoiceInfo.Gender);
                    duplicateNames.TryGetValue(baseName, out var count);
                    duplicateNames[baseName] = ++count;
                    return new VoiceOption(
                        voice.VoiceInfo.Name,
                        count == 1 ? baseName : $"{baseName} {count}");
                }));
            return voices;
        }
    }

    public IReadOnlyList<string> InstalledVoiceNames => InstalledVoices
        .Select(voice => voice.Id)
        .ToArray();

    public bool IsMuted => _muted;

    public bool IsSessionActive => _sessionActive;

    public void SelectVoice(string? voiceName)
    {
        if (TryReadNaturalSpeaker(voiceName, out var speakerId) ||
            string.IsNullOrWhiteSpace(voiceName) && _naturalSynthesizer.IsAvailable)
        {
            _naturalSpeakerId = speakerId >= 0 && CuratedNaturalVoices.Any(voice =>
                voice.Id.Equals($"huitian-natural:{speakerId}", StringComparison.OrdinalIgnoreCase))
                ? speakerId
                : 0;
            return;
        }

        _naturalSpeakerId = -1;
        if (!string.IsNullOrWhiteSpace(voiceName) &&
            InstalledVoiceNames.Contains(voiceName, StringComparer.OrdinalIgnoreCase))
        {
            _synthesizer.SelectVoice(voiceName);
        }
    }

    public void ConfigureWakeWord(bool enabled, string? phrase, string? language)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPhrase = NormalizePhrase(phrase);
        var normalizedLanguage = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language;
        var wakeConfigurationChanged = _wakeEnabled != enabled ||
                                       !_wakePhrase.Equals(normalizedPhrase, StringComparison.OrdinalIgnoreCase) ||
                                       !_language.Equals(normalizedLanguage, StringComparison.OrdinalIgnoreCase);
        _wakeEnabled = enabled;
        _wakePhrase = normalizedPhrase;
        _language = normalizedLanguage;
        CancelWakeListeningRetry();
        if (!_wakeEnabled)
        {
            StopAndDispose(ref _wakeRecognizer);
            if (_neuralMode == NeuralRecognitionMode.Wake)
            {
                StopNeuralRecognition();
            }
            return;
        }

        // A Windows grammar is immutable after loading. Restart wake listening
        // so a newly recorded phrase takes effect immediately on every backend.
        if (wakeConfigurationChanged && !_sessionActive)
        {
            StopAndDispose(ref _wakeRecognizer);
            if (_neuralMode == NeuralRecognitionMode.Wake)
            {
                StopNeuralRecognition();
            }
        }

        if (!_sessionActive && _captureRecognizer is null &&
            _neuralMode == NeuralRecognitionMode.None)
        {
            StartWakeListening();
        }
    }

    public void StartWakeListening()
    {
        if (_disposed || !_wakeEnabled || _sessionActive || _captureRecognizer is not null ||
            _wakeRecognizer is not null || _neuralMode != NeuralRecognitionMode.None)
        {
            return;
        }

        if (_synthesizer.State == SynthesizerState.Speaking || _naturalSynthesizer.IsSpeaking)
        {
            ScheduleWakeListeningRetry();
            return;
        }

        CancelWakeListeningRetry();

        if (CanUseNeuralRecognition &&
            TryStartNeuralRecognition(NeuralRecognitionMode.Wake))
        {
            return;
        }

        var info = ResolveRecognizer();
        if (info is null)
        {
            StateChanged?.Invoke(this, "Windows 尚未安装可用的语音识别语言");
            return;
        }

        var phrases = new[] { _wakePhrase, "你好小天", "回天回天" }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var choices = new Choices(phrases);
        var builder = new GrammarBuilder(choices) { Culture = info.Culture };
        var recognizer = CreateRecognizer(info);
        recognizer.LoadGrammar(new Grammar(builder) { Name = "HuiTian-Wake" });
        recognizer.SpeechRecognized += WakeRecognizer_OnSpeechRecognized;
        recognizer.AudioLevelUpdated += Recognizer_OnAudioLevelUpdated;
        recognizer.RecognizeCompleted += WakeRecognizer_OnRecognizeCompleted;
        _wakeRecognizer = recognizer;
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionActive)
        {
            return;
        }

        CancelWakeListeningRetry();
        StopAndDispose(ref _wakeRecognizer);
        StopAndDispose(ref _captureRecognizer);
        StopNeuralRecognition();
        if (CanUseNeuralRecognition &&
            TryStartNeuralRecognition(NeuralRecognitionMode.Conversation))
        {
            _sessionActive = true;
            _muted = false;
            StateChanged?.Invoke(this, "正在聆听");
            return;
        }

        var info = ResolveRecognizer() ?? throw new InvalidOperationException(
            "Windows 尚未安装语音识别语言。请在系统的“时间和语言 → 语音”中安装中文语音后再试。");
        var recognizer = CreateRecognizer(info);
        recognizer.LoadGrammar(new DictationGrammar { Name = $"HuiTian-{info.Culture.Name}" });
        recognizer.SpeechHypothesized += ConversationRecognizer_OnSpeechHypothesized;
        recognizer.SpeechRecognized += ConversationRecognizer_OnSpeechRecognized;
        recognizer.AudioLevelUpdated += Recognizer_OnAudioLevelUpdated;
        recognizer.RecognizeCompleted += ConversationRecognizer_OnRecognizeCompleted;
        _conversationRecognizer = recognizer;
        _sessionActive = true;
        _muted = false;
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
        StateChanged?.Invoke(this, "正在聆听");
    }

    public void BeginWakePhraseCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelWakeListeningRetry();
        CancelPendingWakePhrase();
        StopAndDispose(ref _wakeRecognizer);
        StopAndDispose(ref _captureRecognizer);
        StopNeuralRecognition();
        if (CanUseNeuralRecognition &&
            TryStartNeuralRecognition(NeuralRecognitionMode.CaptureWakePhrase))
        {
            StateChanged?.Invoke(this, "请清楚说出新的唤醒词");
            return;
        }

        var info = ResolveRecognizer() ?? throw new InvalidOperationException(
            "Windows 尚未安装语音识别语言，暂时无法录制唤醒词。");
        var recognizer = CreateRecognizer(info);
        recognizer.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
        recognizer.LoadGrammar(new DictationGrammar { Name = "HuiTian-Capture-Wake" });
        recognizer.SpeechHypothesized += CaptureRecognizer_OnSpeechHypothesized;
        recognizer.SpeechRecognized += CaptureRecognizer_OnSpeechRecognized;
        recognizer.AudioLevelUpdated += Recognizer_OnAudioLevelUpdated;
        recognizer.RecognizeCompleted += CaptureRecognizer_OnRecognizeCompleted;
        _captureRecognizer = recognizer;
        StateChanged?.Invoke(this, "请清楚说出新的唤醒词");
        recognizer.RecognizeAsync(RecognizeMode.Multiple);
    }

    public void CancelWakePhraseCapture()
    {
        CancelPendingWakePhrase();
        StopAndDispose(ref _captureRecognizer);
        if (_neuralMode == NeuralRecognitionMode.CaptureWakePhrase)
        {
            StopNeuralRecognition();
        }

        StartWakeListening();
    }

    public void ToggleMute()
    {
        if (!_sessionActive)
        {
            return;
        }

        _muted = !_muted;
        if (_muted)
        {
            CancelPendingUtterance();
            StopAndDispose(ref _conversationRecognizer);
            if (_neuralMode == NeuralRecognitionMode.Conversation)
            {
                StopNeuralRecognition();
            }
            StateChanged?.Invoke(this, "麦克风已关闭");
        }
        else
        {
            _sessionActive = false;
            Start();
        }
    }

    public void Speak(string text)
    {
        if (!_sessionActive || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        BeginSpeechQueue(resumeConversation: !_muted, resumeWake: false);
        QueueSpeech(CleanSpeechText(text));
        CompleteSpeechQueue();
    }

    public void StreamResponseText(string responseId, string fullText, bool completed)
    {
        if (!_sessionActive || string.IsNullOrWhiteSpace(responseId))
        {
            return;
        }

        var cleaned = CleanSpeechText(fullText);
        if (!string.Equals(_streamResponseId, responseId, StringComparison.Ordinal))
        {
            BeginSpeechQueue(resumeConversation: !_muted, resumeWake: false);
            _streamResponseId = responseId;
            _streamResponseText = string.Empty;
            _streamPendingText = string.Empty;
            _streamSpeechStarted = false;
        }

        if (cleaned.StartsWith(_streamResponseText, StringComparison.Ordinal))
        {
            _streamPendingText += cleaned[_streamResponseText.Length..];
            _streamResponseText = cleaned;
        }
        else if (!cleaned.Equals(_streamResponseText, StringComparison.Ordinal))
        {
            // A backend may occasionally replace a partial sentence instead
            // of extending it. Keep only the not-yet-spoken replacement tail.
            var common = CommonPrefixLength(_streamResponseText, cleaned);
            _streamPendingText += cleaned[common..];
            _streamResponseText = cleaned;
        }

        FlushSpeakableSegments(force: completed);
        if (completed)
        {
            CompleteSpeechQueue();
            _streamResponseId = null;
            _streamResponseText = string.Empty;
            _streamPendingText = string.Empty;
            _streamSpeechStarted = false;
        }
    }

    public void PreviewVoice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        BeginSpeechQueue(
            resumeConversation: _sessionActive && !_muted,
            resumeWake: !_sessionActive && _wakeEnabled);
        QueueSpeech(CleanSpeechText(text));
        CompleteSpeechQueue();
    }

    public void SpeakAttention(string text) => PreviewVoice(text);

    /// <summary>
    /// Stops only the current spoken reply. The voice session and floating
    /// controls remain open, and listening resumes immediately when possible.
    /// </summary>
    public void StopSpeaking()
    {
        if (_disposed)
        {
            return;
        }

        var resumeConversation = _sessionActive && !_muted;
        var resumeWake = !_sessionActive && _wakeEnabled;
        _resumeConversationAfterSpeech = false;
        _resumeWakeAfterSpeech = false;
        CancelSpeechQueue();

        if (resumeConversation)
        {
            try
            {
                if (_conversationRecognizer is null && !_neuralRecognizer.IsActive)
                {
                    _sessionActive = false;
                    Start();
                }

                StateChanged?.Invoke(this, "正在聆听");
            }
            catch (Exception)
            {
                StateChanged?.Invoke(this, "语音识别暂时不可用，请检查麦克风权限");
            }
        }
        else if (resumeWake)
        {
            StartWakeListening();
        }
    }

    public void ResumeListening()
    {
        if (_sessionActive && !_muted && _conversationRecognizer is null &&
            !_neuralRecognizer.IsActive &&
            _synthesizer.State != SynthesizerState.Speaking &&
            !_naturalSynthesizer.IsSpeaking)
        {
            _sessionActive = false;
            Start();
        }
    }

    public void Stop()
    {
        _sessionActive = false;
        _muted = false;
        _resumeConversationAfterSpeech = false;
        _resumeWakeAfterSpeech = false;
        CancelPendingUtterance();
        StopAndDispose(ref _conversationRecognizer);
        StopNeuralRecognition();
        CancelSpeechQueue();
        StateChanged?.Invoke(this, "语音已关闭");
        StartWakeListening();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wakeEnabled = false;
        _sessionActive = false;
        CancelWakeListeningRetry();
        CancelPendingUtterance();
        CancelPendingWakePhrase();
        StopAndDispose(ref _wakeRecognizer);
        StopAndDispose(ref _conversationRecognizer);
        StopAndDispose(ref _captureRecognizer);
        StopNeuralRecognition();
        CancelSpeechQueue();
        _neuralRecognizer.PartialTextChanged -= NeuralRecognizer_OnPartialTextChanged;
        _neuralRecognizer.TextRecognized -= NeuralRecognizer_OnTextRecognized;
        _neuralRecognizer.AudioLevelChanged -= NeuralRecognizer_OnAudioLevelChanged;
        _neuralRecognizer.Failed -= NeuralRecognizer_OnFailed;
        _neuralRecognizer.Dispose();
        _naturalSynthesizer.Dispose();
        _synthesizer.Dispose();
    }

    private static SpeechRecognitionEngine CreateRecognizer(RecognizerInfo info)
    {
        var recognizer = new SpeechRecognitionEngine(info)
        {
            InitialSilenceTimeout = TimeSpan.FromSeconds(12),
            BabbleTimeout = TimeSpan.FromSeconds(3),
            EndSilenceTimeout = TimeSpan.FromMilliseconds(380),
            EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(560),
        };
        recognizer.SetInputToDefaultAudioDevice();
        return recognizer;
    }

    private RecognizerInfo? ResolveRecognizer()
    {
        var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
        var requestedCulture = CultureInfo.GetCultureInfo(_language);
        return recognizers.FirstOrDefault(info =>
                   info.Culture.Name.Equals(_language, StringComparison.OrdinalIgnoreCase))
               ?? recognizers.FirstOrDefault(info =>
                   info.Culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
               ?? recognizers.FirstOrDefault(info =>
                   info.Culture.TwoLetterISOLanguageName.Equals(
                       requestedCulture.TwoLetterISOLanguageName,
                       StringComparison.OrdinalIgnoreCase))
               ?? recognizers.FirstOrDefault();
    }

    private bool CanUseNeuralRecognition =>
        !_neuralRecognitionUnavailable &&
        _language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
        _neuralRecognizer.IsAvailable;

    private bool TryStartNeuralRecognition(NeuralRecognitionMode mode)
    {
        try
        {
            _neuralMode = mode;
            _neuralRecognizer.Start();
            return true;
        }
        catch (Exception)
        {
            _neuralMode = NeuralRecognitionMode.None;
            _neuralRecognitionUnavailable = true;
            _neuralRecognizer.Stop();
            StateChanged?.Invoke(this, "正在切换到兼容语音识别");
            return false;
        }
    }

    private void StopNeuralRecognition()
    {
        _neuralMode = NeuralRecognitionMode.None;
        _neuralRecognizer.Stop();
    }

    private void NeuralRecognizer_OnPartialTextChanged(object? sender, string text)
    {
        if (_neuralMode == NeuralRecognitionMode.Conversation)
        {
            PublishConversationPartial(text);
        }
        else if (_neuralMode == NeuralRecognitionMode.CaptureWakePhrase)
        {
            PublishWakePhrasePartial(text);
        }
    }

    private void NeuralRecognizer_OnTextRecognized(object? sender, string text)
    {
        var mode = _neuralMode;
        text = VoiceUtterancePolicy.CorrectRecognition(text);
        if (mode == NeuralRecognitionMode.Wake)
        {
            if (!IsWakePhraseMatch(text))
            {
                return;
            }

            StopNeuralRecognition();
            WakeWordDetected?.Invoke(this, text);
            return;
        }

        if (mode == NeuralRecognitionMode.CaptureWakePhrase)
        {
            QueueWakePhraseChunk(text);
            return;
        }

        if (mode != NeuralRecognitionMode.Conversation || !_sessionActive || _muted)
        {
            return;
        }

        QueueConversationChunk(text);
    }

    private void NeuralRecognizer_OnAudioLevelChanged(object? sender, int level) =>
        AudioLevelChanged?.Invoke(this, level);

    private void NeuralRecognizer_OnFailed(object? sender, string message)
    {
        var failedMode = _neuralMode;
        if (failedMode == NeuralRecognitionMode.None)
        {
            return;
        }

        _neuralRecognitionUnavailable = true;
        StopNeuralRecognition();
        StateChanged?.Invoke(this, "本地语音正在自动切换兼容模式");
        try
        {
            switch (failedMode)
            {
                case NeuralRecognitionMode.Wake:
                    StartWakeListening();
                    break;
                case NeuralRecognitionMode.Conversation:
                    _sessionActive = false;
                    Start();
                    break;
                case NeuralRecognitionMode.CaptureWakePhrase:
                    BeginWakePhraseCapture();
                    break;
            }
        }
        catch (Exception)
        {
            StateChanged?.Invoke(this, "语音识别暂时不可用，请检查麦克风权限");
        }
    }

    private bool IsWakePhraseMatch(string recognizedText)
    {
        var recognized = NormalizeComparable(recognizedText);
        var candidates = new[] { _wakePhrase, "你好小天", "回天回天" }
            .Select(NormalizeComparable)
            .Where(candidate => candidate.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (recognized.Contains(candidate, StringComparison.OrdinalIgnoreCase) ||
                recognized.Length >= 4 && candidate.Length >= 4 &&
                Math.Abs(recognized.Length - candidate.Length) <= 1 &&
                EditDistance(recognized, candidate) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private void WakeRecognizer_OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!ReferenceEquals(sender, _wakeRecognizer) ||
            e.Result.Confidence < 0.28f || string.IsNullOrWhiteSpace(e.Result.Text))
        {
            return;
        }

        var corrected = VoiceUtterancePolicy.CorrectRecognition(e.Result.Text);
        var recognized = NormalizeComparable(corrected);
        var candidates = new[] { _wakePhrase, "你好小天", "回天回天" }
            .Select(NormalizeComparable);
        if (!candidates.Any(candidate => recognized.Contains(candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        StopAndDispose(ref _wakeRecognizer);
        WakeWordDetected?.Invoke(this, corrected);
    }

    private void WakeRecognizer_OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (ReferenceEquals(sender, _wakeRecognizer) && e.Error is not null)
        {
            StopAndDispose(ref _wakeRecognizer);
            StateChanged?.Invoke(this, "语音唤醒暂时不可用");
        }
    }

    private void ConversationRecognizer_OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (_sessionActive && !_muted && ReferenceEquals(sender, _conversationRecognizer) &&
            !string.IsNullOrWhiteSpace(e.Result.Text))
        {
            PublishConversationPartial(e.Result.Text);
        }
    }

    private void ConversationRecognizer_OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!_sessionActive || _muted || !ReferenceEquals(sender, _conversationRecognizer) ||
            e.Result.Confidence < 0.22f || string.IsNullOrWhiteSpace(e.Result.Text))
        {
            return;
        }

        QueueConversationChunk(e.Result.Text);
    }

    private void ConversationRecognizer_OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (_sessionActive && !_muted && sender is SpeechRecognitionEngine recognizer &&
            ReferenceEquals(recognizer, _conversationRecognizer) &&
            e.Error is null && !e.Cancelled)
        {
            recognizer.RecognizeAsync(RecognizeMode.Multiple);
        }
    }

    private void CaptureRecognizer_OnSpeechHypothesized(object? sender, SpeechHypothesizedEventArgs e)
    {
        if (ReferenceEquals(sender, _captureRecognizer) && !string.IsNullOrWhiteSpace(e.Result.Text))
        {
            PublishWakePhrasePartial(e.Result.Text);
        }
    }

    private void CaptureRecognizer_OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (!ReferenceEquals(sender, _captureRecognizer) ||
            e.Result.Confidence < 0.22f || string.IsNullOrWhiteSpace(e.Result.Text))
        {
            return;
        }

        QueueWakePhraseChunk(e.Result.Text);
    }

    private void CaptureRecognizer_OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (ReferenceEquals(sender, _captureRecognizer) && e.Error is not null)
        {
            StopAndDispose(ref _captureRecognizer);
            CancelPendingWakePhrase();
            StateChanged?.Invoke(this, "没有听清，请再录一次");
            StartWakeListening();
        }
    }

    private void Recognizer_OnAudioLevelUpdated(object? sender, AudioLevelUpdatedEventArgs e) =>
        AudioLevelChanged?.Invoke(this, e.AudioLevel);

    private void BeginSpeechQueue(bool resumeConversation, bool resumeWake)
    {
        CancelPendingUtterance();
        CancelSpeechQueue();
        _resumeConversationAfterSpeech = resumeConversation;
        _resumeWakeAfterSpeech = resumeWake;
        StopAndDispose(ref _wakeRecognizer);
        StopAndDispose(ref _conversationRecognizer);
        StopAndDispose(ref _captureRecognizer);
        if (_neuralMode != NeuralRecognitionMode.None)
        {
            StopNeuralRecognition();
        }

        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var cancellation = new CancellationTokenSource();
        var generation = ++_speechGeneration;
        _speechQueue = queue;
        _speechCancellation = cancellation;
        StateChanged?.Invoke(this, "回天正在回答");
        _ = Task.Run(() => ProcessSpeechQueueAsync(
            queue.Reader,
            generation,
            cancellation.Token));
    }

    private void QueueSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _speechQueue?.Writer.TryWrite(text.Trim());
    }

    private void CompleteSpeechQueue() => _speechQueue?.Writer.TryComplete();

    private async Task ProcessSpeechQueueAsync(
        ChannelReader<string> reader,
        int generation,
        CancellationToken token)
    {
        try
        {
            await foreach (var text in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (_naturalSpeakerId >= 0 && _naturalSynthesizer.IsAvailable)
                {
                    await _naturalSynthesizer
                        .SpeakAsync(text, _naturalSpeakerId, token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await SpeakSystemChunkAsync(text, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (generation == _speechGeneration && !token.IsCancellationRequested)
            {
                _speechQueue = null;
                _speechCancellation?.Dispose();
                _speechCancellation = null;
                ResumeAfterSpeech();
            }
        }
    }

    private async Task SpeakSystemChunkAsync(string text, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SpeakCompletedEventArgs>? handler = null;
        handler = (_, _) => completion.TrySetResult();
        _synthesizer.SpeakCompleted += handler;
        using var registration = token.Register(() =>
        {
            _synthesizer.SpeakAsyncCancelAll();
            completion.TrySetCanceled(token);
        });
        try
        {
            _synthesizer.SpeakAsync(text);
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _synthesizer.SpeakCompleted -= handler;
        }
    }

    private void CancelSpeechQueue()
    {
        _speechGeneration++;
        _speechQueue?.Writer.TryComplete();
        _speechQueue = null;
        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        _speechCancellation = null;
        _naturalSynthesizer.Cancel();
        _synthesizer.SpeakAsyncCancelAll();
        _streamResponseId = null;
        _streamResponseText = string.Empty;
        _streamPendingText = string.Empty;
        _streamSpeechStarted = false;
    }

    private void FlushSpeakableSegments(bool force)
    {
        while (!string.IsNullOrWhiteSpace(_streamPendingText))
        {
            var boundary = FindSpeechBoundary(_streamPendingText);
            var targetLength = _streamSpeechStarted
                ? FollowingStreamingSpeechChunkLength
                : FirstStreamingSpeechChunkLength;
            if (boundary <= 0 && _streamPendingText.Length >= targetLength)
            {
                var searchLength = Math.Min(targetLength, _streamPendingText.Length);
                var softBreak = _streamPendingText.LastIndexOfAny(
                    ['，', ',', '、', ' '],
                    searchLength - 1,
                    searchLength);
                boundary = softBreak >= 8 ? softBreak + 1 : searchLength;
            }

            if (boundary <= 0)
            {
                if (!force)
                {
                    return;
                }

                boundary = _streamPendingText.Length;
            }

            var segment = _streamPendingText[..boundary].Trim();
            _streamPendingText = _streamPendingText[boundary..];
            QueueSpeech(segment);
            if (!string.IsNullOrWhiteSpace(segment))
            {
                _streamSpeechStarted = true;
            }
        }
    }

    private static int FindSpeechBoundary(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if ((text[index] is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '\n') &&
                index >= 3)
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var maximum = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < maximum && left[index] == right[index]) index++;
        return index;
    }

    private static string CleanSpeechText(string value)
    {
        var cleaned = Regex.Replace(value, @"https?://\S+", string.Empty, RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @":[a-zA-Z][a-zA-Z0-9_+-]{1,30}:", string.Empty);
        cleaned = Regex.Replace(cleaned, @"[\p{Cs}\p{So}\p{Sk}\uFE0F\u200D]", string.Empty);
        cleaned = cleaned.Replace("#", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(cleaned, @"[ \t]+", " ").Trim();
    }

    private void ResumeAfterSpeech()
    {
        if (_resumeConversationAfterSpeech && _sessionActive && !_muted)
        {
            _resumeConversationAfterSpeech = false;
            ResumeListening();
            StateChanged?.Invoke(this, "正在聆听");
            return;
        }

        if (_resumeWakeAfterSpeech)
        {
            _resumeWakeAfterSpeech = false;
            StartWakeListening();
        }
    }

    private void PublishConversationPartial(string text)
    {
        var corrected = VoiceUtterancePolicy.CorrectRecognition(text).Trim();
        if (string.IsNullOrWhiteSpace(corrected))
        {
            return;
        }

        string transcript;
        CancellationTokenSource? cancellation = null;
        int generation = 0;
        lock (_utteranceSync)
        {
            transcript = MergeRecognizedText(_pendingUtterance, corrected);
            // A new partial means the user resumed speaking after an endpoint.
            // Postpone the previous commit immediately instead of waiting for
            // the next final ASR chunk, which could arrive after the old timer.
            if (!string.IsNullOrWhiteSpace(_pendingUtterance) &&
                _utteranceSilenceCancellation is not null)
            {
                _utteranceSilenceCancellation.Cancel();
                _utteranceSilenceCancellation.Dispose();
                cancellation = new CancellationTokenSource();
                _utteranceSilenceCancellation = cancellation;
                generation = ++_utteranceGeneration;
            }
        }

        PartialTextChanged?.Invoke(this, transcript);
        if (cancellation is not null)
        {
            var elapsed = _utteranceStartedTimestamp == 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(_utteranceStartedTimestamp);
            var delay = VoiceUtterancePolicy.GetCommitDelay(transcript, elapsed);
            _ = FlushUtteranceAfterSilenceAsync(generation, cancellation, delay);
        }
    }

    private void QueueConversationChunk(string text)
    {
        var corrected = VoiceUtterancePolicy.CorrectRecognition(text).Trim();
        if (string.IsNullOrWhiteSpace(corrected) || !_sessionActive || _muted)
        {
            return;
        }

        CancellationTokenSource cancellation;
        string transcript;
        int generation;
        lock (_utteranceSync)
        {
            if (string.IsNullOrWhiteSpace(_pendingUtterance))
            {
                _utteranceStartedTimestamp = Stopwatch.GetTimestamp();
            }

            _pendingUtterance = MergeRecognizedText(_pendingUtterance, corrected);
            transcript = _pendingUtterance;
            _utteranceSilenceCancellation?.Cancel();
            _utteranceSilenceCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _utteranceSilenceCancellation = cancellation;
            generation = ++_utteranceGeneration;
        }

        PartialTextChanged?.Invoke(this, transcript);
        var elapsed = _utteranceStartedTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_utteranceStartedTimestamp);
        var delay = VoiceUtterancePolicy.GetCommitDelay(transcript, elapsed);
        StateChanged?.Invoke(this, "正在聆听");
        _ = FlushUtteranceAfterSilenceAsync(generation, cancellation, delay);
    }

    private async Task FlushUtteranceAfterSilenceAsync(
        int generation,
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string utterance;
        lock (_utteranceSync)
        {
            if (generation != _utteranceGeneration ||
                !ReferenceEquals(cancellation, _utteranceSilenceCancellation))
            {
                return;
            }

            utterance = _pendingUtterance.Trim();
            _pendingUtterance = string.Empty;
            _utteranceStartedTimestamp = 0;
            _utteranceSilenceCancellation = null;
        }

        cancellation.Dispose();
        if (string.IsNullOrWhiteSpace(utterance) || !_sessionActive || _muted || _disposed)
        {
            return;
        }

        StateChanged?.Invoke(this, "回天正在理解");
        TextRecognized?.Invoke(this, utterance);
    }

    private void CancelPendingUtterance()
    {
        lock (_utteranceSync)
        {
            _utteranceGeneration++;
            _pendingUtterance = string.Empty;
            _utteranceStartedTimestamp = 0;
            _utteranceSilenceCancellation?.Cancel();
            _utteranceSilenceCancellation?.Dispose();
            _utteranceSilenceCancellation = null;
        }
    }

    private void QueueWakePhraseChunk(string text)
    {
        var corrected = VoiceUtterancePolicy.CorrectRecognition(text).Trim();
        if (string.IsNullOrWhiteSpace(corrected) || _disposed)
        {
            return;
        }

        CancellationTokenSource cancellation;
        string transcript;
        int generation;
        lock (_utteranceSync)
        {
            if (string.IsNullOrWhiteSpace(_pendingWakePhrase))
            {
                _wakePhraseStartedTimestamp = Stopwatch.GetTimestamp();
            }

            _pendingWakePhrase = MergeRecognizedText(_pendingWakePhrase, corrected);
            transcript = _pendingWakePhrase;
            _wakePhraseSilenceCancellation?.Cancel();
            _wakePhraseSilenceCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _wakePhraseSilenceCancellation = cancellation;
            generation = ++_wakePhraseGeneration;
        }

        PartialTextChanged?.Invoke(this, transcript);
        var elapsed = _wakePhraseStartedTimestamp == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_wakePhraseStartedTimestamp);
        var delay = VoiceUtterancePolicy.GetCommitDelay(transcript, elapsed);
        _ = CommitWakePhraseAfterSilenceAsync(generation, cancellation, delay);
    }

    private void PublishWakePhrasePartial(string text)
    {
        var corrected = VoiceUtterancePolicy.CorrectRecognition(text).Trim();
        if (string.IsNullOrWhiteSpace(corrected))
        {
            return;
        }

        string transcript;
        CancellationTokenSource? cancellation = null;
        int generation = 0;
        lock (_utteranceSync)
        {
            transcript = MergeRecognizedText(_pendingWakePhrase, corrected);
            if (!string.IsNullOrWhiteSpace(_pendingWakePhrase) &&
                _wakePhraseSilenceCancellation is not null)
            {
                _wakePhraseSilenceCancellation.Cancel();
                _wakePhraseSilenceCancellation.Dispose();
                cancellation = new CancellationTokenSource();
                _wakePhraseSilenceCancellation = cancellation;
                generation = ++_wakePhraseGeneration;
            }
        }

        PartialTextChanged?.Invoke(this, transcript);
        if (cancellation is not null)
        {
            var elapsed = _wakePhraseStartedTimestamp == 0
                ? TimeSpan.Zero
                : Stopwatch.GetElapsedTime(_wakePhraseStartedTimestamp);
            var delay = VoiceUtterancePolicy.GetCommitDelay(transcript, elapsed);
            _ = CommitWakePhraseAfterSilenceAsync(generation, cancellation, delay);
        }
    }

    private async Task CommitWakePhraseAfterSilenceAsync(
        int generation,
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string phrase;
        lock (_utteranceSync)
        {
            if (generation != _wakePhraseGeneration ||
                !ReferenceEquals(cancellation, _wakePhraseSilenceCancellation))
            {
                return;
            }

            phrase = NormalizePhrase(_pendingWakePhrase);
            _pendingWakePhrase = string.Empty;
            _wakePhraseStartedTimestamp = 0;
            _wakePhraseSilenceCancellation = null;
        }

        cancellation.Dispose();
        if (NormalizeComparable(phrase).Length < 2)
        {
            PartialTextChanged?.Invoke(this, phrase);
            StateChanged?.Invoke(this, "唤醒词至少需要两个字，请继续说");
            return;
        }

        _wakePhrase = phrase;
        StopAndDispose(ref _captureRecognizer);
        if (_neuralMode == NeuralRecognitionMode.CaptureWakePhrase)
        {
            StopNeuralRecognition();
        }

        WakePhraseCaptured?.Invoke(this, phrase);
        StateChanged?.Invoke(this, "新的唤醒词已录入");
        StartWakeListening();
    }

    private void CancelPendingWakePhrase()
    {
        lock (_utteranceSync)
        {
            _wakePhraseGeneration++;
            _pendingWakePhrase = string.Empty;
            _wakePhraseStartedTimestamp = 0;
            _wakePhraseSilenceCancellation?.Cancel();
            _wakePhraseSilenceCancellation?.Dispose();
            _wakePhraseSilenceCancellation = null;
        }
    }

    private static string MergeRecognizedText(string accumulated, string next)
    {
        accumulated = accumulated.Trim();
        next = next.Trim();
        if (string.IsNullOrEmpty(accumulated))
        {
            return next;
        }

        if (string.IsNullOrEmpty(next) || accumulated.EndsWith(next, StringComparison.Ordinal))
        {
            return accumulated;
        }

        var maximumOverlap = Math.Min(Math.Min(accumulated.Length, next.Length), 24);
        for (var length = maximumOverlap; length >= 2; length--)
        {
            if (accumulated.EndsWith(next[..length], StringComparison.Ordinal))
            {
                return accumulated + next[length..];
            }
        }

        var needsSpace = IsAsciiWordCharacter(accumulated[^1]) && IsAsciiWordCharacter(next[0]);
        return needsSpace ? $"{accumulated} {next}" : accumulated + next;
    }

    private static bool IsAsciiWordCharacter(char value) =>
        value <= 0x7f && char.IsLetterOrDigit(value);

    private static void StopAndDispose(ref SpeechRecognitionEngine? recognizer)
    {
        var current = recognizer;
        recognizer = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            current.Dispose();
        }
        catch (InvalidOperationException)
        {
            // A recognition callback can race with shutdown. The field has
            // already been detached, so there is nothing left to clean up.
        }
    }

    private static string NormalizePhrase(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "你好小天" : value.Trim();

    private static string NormalizeComparable(string value) => string.Concat(
        value.Where(character => !char.IsWhiteSpace(character) &&
                                 !char.IsPunctuation(character)));

    private static string FriendlyVoiceName(CultureInfo culture, VoiceGender gender)
    {
        var suffix = gender switch
        {
            VoiceGender.Female => "温柔女声",
            VoiceGender.Male => "自然男声",
            _ => "自然声",
        };
        var prefix = culture.TwoLetterISOLanguageName switch
        {
            "zh" when gender == VoiceGender.Male => "明川",
            "zh" => "云舒",
            "ja" when gender == VoiceGender.Male => "青岚",
            "ja" => "樱和",
            "en" when gender == VoiceGender.Male => "远航",
            "en" => "晨星",
            _ => "清和",
        };
        return $"{prefix}（{suffix}）";
    }

    private static bool TryReadNaturalSpeaker(string? value, out int speakerId)
    {
        speakerId = -1;
        const string prefix = "huitian-natural:";
        return value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true &&
               int.TryParse(value[prefix.Length..], out speakerId);
    }

    private void ScheduleWakeListeningRetry()
    {
        if (_disposed || !_wakeEnabled || _sessionActive)
        {
            return;
        }

        CancelWakeListeningRetry();
        var cancellation = new CancellationTokenSource();
        _wakeListeningRetryCancellation = cancellation;
        _ = RetryWakeListeningAsync(cancellation);
    }

    private async Task RetryWakeListeningAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && !_disposed &&
                   _wakeEnabled && !_sessionActive)
            {
                await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
                if (_synthesizer.State == SynthesizerState.Speaking ||
                    _naturalSynthesizer.IsSpeaking)
                {
                    continue;
                }

                if (ReferenceEquals(_wakeListeningRetryCancellation, cancellation))
                {
                    _wakeListeningRetryCancellation = null;
                }

                StartWakeListening();
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_wakeListeningRetryCancellation, cancellation))
            {
                _wakeListeningRetryCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelWakeListeningRetry()
    {
        var cancellation = _wakeListeningRetryCancellation;
        _wakeListeningRetryCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
    }
}
