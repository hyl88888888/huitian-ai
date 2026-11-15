using System.IO;
using System.Text;
using NAudio;
using NAudio.Wave;
using SherpaOnnx;

namespace HuiTian.App;

/// <summary>
/// Low-latency, fully local Mandarin recognition. Audio never leaves the PC.
/// The small streaming model lives on D: and the Windows recognizer remains a
/// fallback when it is unavailable or another language is selected.
/// </summary>
internal sealed class NeuralSpeechRecognizer : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly object _sync = new();
    private readonly string[] _modelDirectories;
    private WaveInEvent? _microphone;
    private OnlineRecognizer? _recognizer;
    private OnlineStream? _stream;
    private string _lastPartial = string.Empty;
    private bool _active;
    private bool _disposed;

    public NeuralSpeechRecognizer(params string[] modelDirectories)
    {
        _modelDirectories = modelDirectories;
    }

    public event EventHandler<string>? PartialTextChanged;

    public event EventHandler<string>? TextRecognized;

    public event EventHandler<int>? AudioLevelChanged;

    public event EventHandler<string>? Failed;

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public bool IsAvailable => ResolveModelFiles() is not null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_active)
            {
                return;
            }

            if (!IsAvailable)
            {
                throw new InvalidOperationException("本地实时语音识别资源尚未准备完成。");
            }

            try
            {
                EnsureRecognizerLocked();
                _stream = _recognizer!.CreateStream();
                _microphone = new WaveInEvent
                {
                    DeviceNumber = 0,
                    WaveFormat = new WaveFormat(SampleRate, 16, 1),
                    BufferMilliseconds = 40,
                    NumberOfBuffers = 3,
                };
                _microphone.DataAvailable += Microphone_OnDataAvailable;
                _microphone.RecordingStopped += Microphone_OnRecordingStopped;
                _lastPartial = string.Empty;
                _active = true;
                _microphone.StartRecording();
            }
            catch
            {
                DisposeCaptureLocked();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!_active && _microphone is null)
            {
                return;
            }

            DisposeCaptureLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeCaptureLocked();
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private void Microphone_OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        string? partial = null;
        string? final = null;
        var level = 0;
        try
        {
            lock (_sync)
            {
                if (!_active || _recognizer is null || _stream is null)
                {
                    return;
                }

                var sampleCount = args.BytesRecorded / 2;
                var samples = new float[sampleCount];
                var peak = 0f;
                for (var index = 0; index < sampleCount; index++)
                {
                    var value = BitConverter.ToInt16(args.Buffer, index * 2) / 32768f;
                    samples[index] = value;
                    peak = Math.Max(peak, Math.Abs(value));
                }

                level = Math.Clamp((int)Math.Round(Math.Sqrt(peak) * 100), 0, 100);
                _stream.AcceptWaveform(SampleRate, samples);
                while (_recognizer.IsReady(_stream))
                {
                    _recognizer.Decode(_stream);
                }

                var text = _recognizer.GetResult(_stream).Text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text) &&
                    !text.Equals(_lastPartial, StringComparison.Ordinal))
                {
                    _lastPartial = text;
                    partial = text;
                }

                if (_recognizer.IsEndpoint(_stream))
                {
                    final = text;
                    _recognizer.Reset(_stream);
                    _lastPartial = string.Empty;
                }
            }

            AudioLevelChanged?.Invoke(this, level);
            if (!string.IsNullOrWhiteSpace(partial))
            {
                PartialTextChanged?.Invoke(this, partial);
            }

            if (!string.IsNullOrWhiteSpace(final))
            {
                TextRecognized?.Invoke(this, final);
            }
        }
        catch (Exception exception)
        {
            Failed?.Invoke(this, exception.Message);
        }
    }

    private void Microphone_OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null && _active)
        {
            Failed?.Invoke(this, args.Exception.Message);
        }
    }

    private void EnsureRecognizerLocked()
    {
        if (_recognizer is not null)
        {
            return;
        }

        var files = ResolveModelFiles() ?? throw new InvalidOperationException(
            "本地实时语音识别资源尚未准备完成。");
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = files.Encoder;
        config.ModelConfig.Transducer.Decoder = files.Decoder;
        config.ModelConfig.Transducer.Joiner = files.Joiner;
        config.ModelConfig.Tokens = files.Tokens;
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "modified_beam_search";
        config.MaxActivePaths = 4;
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 0.90f;
        config.Rule2MinTrailingSilence = 0.36f;
        config.Rule3MinUtteranceLength = 20f;
        var hotwordsPath = Path.Combine(
            Path.GetDirectoryName(files.Tokens)!,
            "huitian-hotwords.txt");
        File.WriteAllText(
            hotwordsPath,
            string.Join('\n',
                "回天", "你好小天", "回天回天", "微信", "联系人", "浏览器",
                "文件", "演示文稿", "人工智能"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        config.HotwordsFile = hotwordsPath;
        config.HotwordsScore = 2.0f;

        _recognizer = new OnlineRecognizer(config);
    }

    private void DisposeCaptureLocked()
    {
        _active = false;
        var microphone = _microphone;
        _microphone = null;
        if (microphone is not null)
        {
            microphone.DataAvailable -= Microphone_OnDataAvailable;
            microphone.RecordingStopped -= Microphone_OnRecordingStopped;
            try
            {
                microphone.StopRecording();
            }
            catch (MmException)
            {
            }

            microphone.Dispose();
        }

        _stream?.Dispose();
        _stream = null;
        _lastPartial = string.Empty;
    }

    private ModelFiles? ResolveModelFiles()
    {
        foreach (var directory in _modelDirectories.Where(Directory.Exists))
        {
            var candidates = new[]
            {
                new ModelFiles(
                    Path.Combine(directory, "encoder.int8.onnx"),
                    Path.Combine(directory, "decoder.onnx"),
                    Path.Combine(directory, "joiner.int8.onnx"),
                    Path.Combine(directory, "tokens.txt")),
                new ModelFiles(
                    Path.Combine(directory, "encoder-epoch-99-avg-1.int8.onnx"),
                    Path.Combine(directory, "decoder-epoch-99-avg-1.int8.onnx"),
                    Path.Combine(directory, "joiner-epoch-99-avg-1.int8.onnx"),
                    Path.Combine(directory, "tokens.txt")),
            };
            var match = candidates.FirstOrDefault(candidate =>
                File.Exists(candidate.Encoder) && File.Exists(candidate.Decoder) &&
                File.Exists(candidate.Joiner) && File.Exists(candidate.Tokens));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private sealed record ModelFiles(
        string Encoder,
        string Decoder,
        string Joiner,
        string Tokens);
}
