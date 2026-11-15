using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SherpaOnnx;

namespace HuiTian.App;

/// <summary>
/// Optional fully-local natural voice. It is loaded only when the verified
/// model files exist on D:, so the product always retains the system voice as
/// a zero-download fallback.
/// </summary>
internal sealed class NaturalVoiceSynthesizer : IDisposable
{
    private const float DefaultOutputGain = 3.2f;
    private const float LimiterKnee = 0.78f;
    private const float LimiterCeiling = 0.985f;
    private readonly string _modelDirectory;
    private readonly float _outputGain;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _playbackSync = new();
    private OfflineTts? _tts;
    private CancellationTokenSource? _speechCancellation;
    private WaveOutEvent? _player;
    private bool _disposed;

    public NaturalVoiceSynthesizer(string modelDirectory, float? outputGain = null)
    {
        _modelDirectory = modelDirectory;
        _outputGain = Math.Clamp(outputGain ?? ReadOutputGain(), 1f, 6f);
    }

    public bool IsSpeaking { get; private set; }

    public bool IsAvailable =>
        File.Exists(Path.Combine(_modelDirectory, "model.onnx")) &&
        File.Exists(Path.Combine(_modelDirectory, "tokens.txt")) &&
        File.Exists(Path.Combine(_modelDirectory, "lexicon.txt"));

    public Task SpeakAsync(string text, int speakerId, CancellationToken cancellationToken)
    {
        if (_disposed || !IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        Cancel();
        _speechCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _speechCancellation.Token;
        IsSpeaking = true;
        return Task.Run(() => GenerateAndPlayAsync(text, speakerId, token), token);
    }

    public async Task WarmUpAsync()
    {
        if (_disposed || !IsAvailable)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureInitialized();
        }
        catch
        {
            // Voice remains optional; the Windows voice fallback is kept if
            // initialization fails on a particular computer.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Cancel()
    {
        _speechCancellation?.Cancel();
        _speechCancellation?.Dispose();
        _speechCancellation = null;
        lock (_playbackSync)
        {
            try
            {
                _player?.Stop();
            }
            catch (InvalidOperationException)
            {
            }

            _player?.Dispose();
            _player = null;
        }
        IsSpeaking = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        _tts?.Dispose();
        _gate.Dispose();
    }

    private async Task GenerateAndPlayAsync(string text, int speakerId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            foreach (var sentence in SplitForLowLatency(text))
            {
                token.ThrowIfCancellationRequested();
                await GenerateAndPlaySegmentAsync(sentence, speakerId, token)
                    .ConfigureAwait(false);
            }

            if (!token.IsCancellationRequested)
            {
                IsSpeaking = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            IsSpeaking = false;
        }
        finally
        {
            IsSpeaking = false;
            _gate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (_tts is not null)
        {
            return;
        }

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = Path.Combine(_modelDirectory, "model.onnx");
        config.Model.Vits.Tokens = Path.Combine(_modelDirectory, "tokens.txt");
        config.Model.Vits.Lexicon = Path.Combine(_modelDirectory, "lexicon.txt");
        config.Model.Vits.NoiseScale = 0.667f;
        config.Model.Vits.NoiseScaleW = 0.8f;
        config.Model.Vits.LengthScale = 1.0f;
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        config.MaxNumSentences = 1;
        config.SilenceScale = 0.2f;
        config.RuleFsts = string.Join(",",
            new[]
            {
                Path.Combine(_modelDirectory, "phone.fst"),
                Path.Combine(_modelDirectory, "date.fst"),
                Path.Combine(_modelDirectory, "number.fst"),
            }.Where(File.Exists));
        _tts = new OfflineTts(config);
    }

    private async Task GenerateAndPlaySegmentAsync(
        string text,
        int speakerId,
        CancellationToken token)
    {
        var provider = new BufferedWaveProvider(new WaveFormat(_tts!.SampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(90),
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        var player = new WaveOutEvent
        {
            DesiredLatency = 80,
            NumberOfBuffers = 3,
        };
        player.Init(provider);
        lock (_playbackSync)
        {
            token.ThrowIfCancellationRequested();
            _player = player;
        }

        var playbackStarted = 0;
        var callback = new OfflineTtsCallbackProgressWithArg((samples, count, _, _) =>
        {
            if (token.IsCancellationRequested || count <= 0)
            {
                return 0;
            }

            try
            {
                var values = new float[count];
                Marshal.Copy(samples, values, 0, count);
                var pcm = ConvertToPcm16(values, _outputGain);
                provider.AddSamples(pcm, 0, pcm.Length);
                if (Interlocked.Exchange(ref playbackStarted, 1) == 0)
                {
                    player.Play();
                }

                return token.IsCancellationRequested ? 0 : 1;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            catch (InvalidOperationException)
            {
                return 0;
            }
        });

        var audio = _tts.GenerateWithConfig(
            text,
            new OfflineTtsGenerationConfig
            {
                Sid = Math.Clamp(speakerId, 0, Math.Max(0, _tts.NumSpeakers - 1)),
                Speed = 1.0f,
                SilenceScale = 0.2f,
            },
            callback);
        try
        {
            while (!token.IsCancellationRequested && provider.BufferedBytes > 0)
            {
                await Task.Delay(15, token).ConfigureAwait(false);
            }

            if (!token.IsCancellationRequested && Volatile.Read(ref playbackStarted) != 0)
            {
                await Task.Delay(80, token).ConfigureAwait(false);
            }
        }
        finally
        {
            audio.Dispose();
            lock (_playbackSync)
            {
                if (ReferenceEquals(_player, player))
                {
                    _player = null;
                }
            }

            try
            {
                player.Stop();
            }
            catch (InvalidOperationException)
            {
            }

            player.Dispose();
        }
    }

    internal static byte[] ConvertToPcm16(float[] samples, float gain)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        for (var index = 0; index < samples.Length; index++)
        {
            var amplified = ApplyGainWithSoftLimiter(samples[index], gain);
            var value = (short)Math.Round(
                amplified * short.MaxValue);
            bytes[index * 2] = (byte)(value & 0xff);
            bytes[index * 2 + 1] = (byte)((value >> 8) & 0xff);
        }

        return bytes;
    }

    internal static float ApplyGainWithSoftLimiter(float sample, float gain)
    {
        var amplified = sample * Math.Clamp(gain, 1f, 6f);
        var magnitude = Math.Abs(amplified);
        if (magnitude <= LimiterKnee)
        {
            return amplified;
        }

        // Preserve normal speech linearly, then compress only the loud peaks.
        // The exponential soft knee approaches the ceiling without hard clipping.
        var headroom = LimiterCeiling - LimiterKnee;
        var limitedMagnitude = LimiterKnee +
                               headroom * (1f - MathF.Exp(-(magnitude - LimiterKnee) / headroom));
        return MathF.CopySign(Math.Min(limitedMagnitude, LimiterCeiling), amplified);
    }

    private static float ReadOutputGain()
    {
        var configured = Environment.GetEnvironmentVariable("HUITIAN_TTS_OUTPUT_GAIN");
        return float.TryParse(
            configured,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var gain)
            ? Math.Clamp(gain, 1f, 6f)
            : DefaultOutputGain;
    }

    private static IEnumerable<string> SplitForLowLatency(string text)
    {
        var parts = Regex.Split(text, @"(?<=[。！？!?；;\n])")
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            yield return text.Trim();
            yield break;
        }

        foreach (var part in parts)
        {
            for (var offset = 0; offset < part.Length; offset += 90)
            {
                yield return part.Substring(offset, Math.Min(90, part.Length - offset));
            }
        }
    }
}
