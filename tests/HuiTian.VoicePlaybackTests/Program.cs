using System.Diagnostics;
using HuiTian.App;

const string modelRoot = @"D:\HuiTian\Runtime\Voice\MeloTTS";

var incompleteDelay = VoiceUtterancePolicy.GetCommitDelay("我想让你", TimeSpan.FromSeconds(2));
var shortDelay = VoiceUtterancePolicy.GetCommitDelay("帮我", TimeSpan.Zero);
var completeDelay = VoiceUtterancePolicy.GetCommitDelay(
    "给微信联系人凉雨发送你好", TimeSpan.FromSeconds(4));
var punctuatedDelay = VoiceUtterancePolicy.GetCommitDelay("请打开微信。", TimeSpan.Zero);
var maximumDelay = VoiceUtterancePolicy.GetCommitDelay(
    "这是一段持续很久的语音", TimeSpan.FromSeconds(30));
if (incompleteDelay < TimeSpan.FromSeconds(3.3) ||
    shortDelay < TimeSpan.FromSeconds(2.9) ||
    completeDelay > TimeSpan.FromSeconds(1.9) ||
    punctuatedDelay > TimeSpan.FromSeconds(1.1) ||
    maximumDelay > TimeSpan.FromMilliseconds(400))
{
    throw new InvalidDataException("Dynamic utterance timing can still split natural speech too early.");
}

if (VoiceUtterancePolicy.CorrectRecognition("有好小天，回填回填") !=
    "你好小天，回天回天")
{
    throw new InvalidDataException("High-confidence wake-word correction failed.");
}

var quietSample = NaturalVoiceSynthesizer.ApplyGainWithSoftLimiter(0.1f, 3.2f);
var loudSample = NaturalVoiceSynthesizer.ApplyGainWithSoftLimiter(0.9f, 3.2f);
var negativeLoudSample = NaturalVoiceSynthesizer.ApplyGainWithSoftLimiter(-0.9f, 3.2f);
if (Math.Abs(quietSample - 0.32f) > 0.0001f ||
    loudSample is <= 0.78f or >= 0.986f ||
    Math.Abs(loudSample + negativeLoudSample) > 0.0001f)
{
    throw new InvalidDataException("Voice gain or soft limiter is outside its safe range.");
}

var limitedPcm = NaturalVoiceSynthesizer.ConvertToPcm16(
    [0.1f, 0.9f, -0.9f, 4f, -4f],
    3.2f);
var limitedValues = Enumerable.Range(0, limitedPcm.Length / sizeof(short))
    .Select(index => BitConverter.ToInt16(limitedPcm, index * sizeof(short)))
    .ToArray();
if (limitedValues.Max() >= short.MaxValue || limitedValues.Min() <= short.MinValue)
{
    throw new InvalidDataException("Soft-limited output clipped at the PCM boundary.");
}

using var voice = new NaturalVoiceSynthesizer(modelRoot);
if (!voice.IsAvailable)
{
    throw new InvalidDataException("The enhanced local voice is unavailable.");
}

var warmup = Stopwatch.StartNew();
await voice.WarmUpAsync();
warmup.Stop();

var playback = Stopwatch.StartNew();
await voice.SpeakAsync("你好，我是回天。新的声音已经准备好了。", 0, CancellationToken.None);
playback.Stop();
if (voice.IsSpeaking || playback.Elapsed < TimeSpan.FromSeconds(1.5))
{
    throw new InvalidDataException("Streaming playback did not complete normally.");
}

var cancellation = Stopwatch.StartNew();
var longSpeech = voice.SpeakAsync(
    "我正在测试停止朗读。点击停止后，声音应该立刻结束，语音对话窗口仍然保持打开。",
    0,
    CancellationToken.None);
await Task.Delay(700);
voice.Cancel();
await longSpeech.WaitAsync(TimeSpan.FromSeconds(5));
cancellation.Stop();
if (voice.IsSpeaking || cancellation.Elapsed > TimeSpan.FromSeconds(5))
{
    throw new InvalidDataException("Voice cancellation was not responsive.");
}

Console.WriteLine(
    $"PASS: 3.2x gain with soft limiter, warmup={warmup.ElapsedMilliseconds}ms, " +
    $"playback={playback.ElapsedMilliseconds}ms, cancel={cancellation.ElapsedMilliseconds}ms");
