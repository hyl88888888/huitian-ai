using System.Diagnostics;
using NAudio.Wave;
using SherpaOnnx;

const string kokoroRoot = @"D:\HuiTian\Runtime\Voice\Kokoro";
const string meloRoot = @"D:\HuiTian\Cache\VoiceCandidates\MeloTTS\extracted\vits-melo-tts-zh_en";
const string zipVoiceRoot = @"D:\HuiTian\Cache\VoiceCandidates\ZipVoice\extracted\sherpa-onnx-zipvoice-distill-int8-zh-en-emilia";
const string zipVoiceVocoder = @"D:\HuiTian\Cache\VoiceCandidates\ZipVoice\vocos_24khz.onnx";
const string outputRoot = @"D:\HuiTian\Build\VoiceComparison";
const string referenceText = "你好，我是回天。以后我会用更自然、更温暖的声音陪你交流，也会尽量更快地回答你的问题。";
const string targetText = "很高兴认识你。今天有什么事情想让我帮你完成？我会尽量快速、准确地处理。";

Directory.CreateDirectory(outputRoot);

var referencePath = Path.Combine(outputRoot, "参考-清和.wav");
Benchmark(
    "reference-melo",
    CreateMelo,
    sid: 0,
    referenceText,
    referencePath);

Benchmark(
    "current-kokoro",
    CreateKokoro,
    sid: 10,
    targetText,
    Path.Combine(outputRoot, "现有-云岚.wav"));

Benchmark(
    "candidate-melo",
    CreateMelo,
    sid: 0,
    targetText,
    Path.Combine(outputRoot, "候选-清和.wav"));

BenchmarkZipVoice(
    referencePath,
    referenceText,
    targetText,
    Path.Combine(outputRoot, "候选-拟人增强-清和.wav"));

static OfflineTts CreateKokoro()
{
    var config = new OfflineTtsConfig();
    config.Model.Kokoro.Model = Path.Combine(kokoroRoot, "model.int8.onnx");
    config.Model.Kokoro.Voices = Path.Combine(kokoroRoot, "voices.bin");
    config.Model.Kokoro.Tokens = Path.Combine(kokoroRoot, "tokens.txt");
    config.Model.Kokoro.DataDir = Path.Combine(kokoroRoot, "espeak-ng-data");
    config.Model.Kokoro.DictDir = Path.Combine(kokoroRoot, "dict");
    config.Model.Kokoro.Lexicon = string.Join(",",
        Path.Combine(kokoroRoot, "lexicon-us-en.txt"),
        Path.Combine(kokoroRoot, "lexicon-zh.txt"));
    config.Model.NumThreads = 4;
    config.Model.Provider = "cpu";
    config.MaxNumSentences = 1;
    config.SilenceScale = 0.2f;
    config.RuleFsts = string.Join(",",
        Path.Combine(kokoroRoot, "phone-zh.fst"),
        Path.Combine(kokoroRoot, "date-zh.fst"),
        Path.Combine(kokoroRoot, "number-zh.fst"));
    return new OfflineTts(config);
}

static OfflineTts CreateMelo()
{
    var config = new OfflineTtsConfig();
    config.Model.Vits.Model = Path.Combine(meloRoot, "model.onnx");
    config.Model.Vits.Lexicon = Path.Combine(meloRoot, "lexicon.txt");
    config.Model.Vits.Tokens = Path.Combine(meloRoot, "tokens.txt");
    config.Model.Vits.NoiseScale = 0.667f;
    config.Model.Vits.NoiseScaleW = 0.8f;
    config.Model.Vits.LengthScale = 1.0f;
    config.Model.NumThreads = 4;
    config.Model.Provider = "cpu";
    config.MaxNumSentences = 1;
    config.SilenceScale = 0.2f;
    config.RuleFsts = string.Join(",",
        Path.Combine(meloRoot, "date.fst"),
        Path.Combine(meloRoot, "number.fst"),
        Path.Combine(meloRoot, "phone.fst"));
    return new OfflineTts(config);
}

static void Benchmark(
    string name,
    Func<OfflineTts> create,
    int sid,
    string text,
    string outputPath)
{
    var total = Stopwatch.StartNew();
    using var tts = create();
    var loadMs = total.ElapsedMilliseconds;
    var generation = Stopwatch.StartNew();
    long firstCallbackMs = -1;
    var callback = new OfflineTtsCallbackProgressWithArg((_, _, _, _) =>
    {
        Interlocked.CompareExchange(ref firstCallbackMs, generation.ElapsedMilliseconds, -1);
        return 1;
    });
    var audio = tts.GenerateWithConfig(
        text,
        new OfflineTtsGenerationConfig
        {
            Sid = sid,
            Speed = 1.0f,
            SilenceScale = 0.2f,
        },
        callback);
    generation.Stop();
    try
    {
        if (audio.NumSamples <= 0 || !audio.SaveToWaveFile(outputPath))
        {
            throw new InvalidDataException($"{name} produced no audio.");
        }

        var duration = TimeSpan.FromSeconds((double)audio.NumSamples / audio.SampleRate);
        var realTimeFactor = generation.Elapsed.TotalSeconds / duration.TotalSeconds;
        Console.WriteLine(
            $"{name}|load_ms={loadMs}|first_callback_ms={firstCallbackMs}|" +
            $"generate_ms={generation.ElapsedMilliseconds}|" +
            $"audio_seconds={duration.TotalSeconds:F3}|rtf={realTimeFactor:F3}|" +
            $"sample_rate={audio.SampleRate}|path={outputPath}");
    }
    finally
    {
        audio.Dispose();
    }
}

static void BenchmarkZipVoice(
    string referencePath,
    string referenceText,
    string text,
    string outputPath)
{
    var samples = new List<float>();
    int referenceSampleRate;
    using (var reader = new AudioFileReader(referencePath))
    {
        if (reader.WaveFormat.Channels != 1)
        {
            throw new InvalidDataException("The reference voice must be mono.");
        }

        referenceSampleRate = reader.WaveFormat.SampleRate;
        var buffer = new float[8192];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, count).ToArray());
        }
    }

    var config = new OfflineTtsConfig();
    config.Model.ZipVoice.Tokens = Path.Combine(zipVoiceRoot, "tokens.txt");
    config.Model.ZipVoice.Encoder = Path.Combine(zipVoiceRoot, "encoder.int8.onnx");
    config.Model.ZipVoice.Decoder = Path.Combine(zipVoiceRoot, "decoder.int8.onnx");
    config.Model.ZipVoice.Vocoder = zipVoiceVocoder;
    config.Model.ZipVoice.DataDir = Path.Combine(zipVoiceRoot, "espeak-ng-data");
    config.Model.ZipVoice.Lexicon = Path.Combine(zipVoiceRoot, "lexicon.txt");
    config.Model.NumThreads = 2;
    config.Model.Provider = "cpu";
    config.MaxNumSentences = 1;
    config.SilenceScale = 0.2f;

    var total = Stopwatch.StartNew();
    using var tts = new OfflineTts(config);
    var loadMs = total.ElapsedMilliseconds;
    var generation = Stopwatch.StartNew();
    long firstCallbackMs = -1;
    var generationConfig = new OfflineTtsGenerationConfig
    {
        ReferenceAudio = samples.ToArray(),
        ReferenceSampleRate = referenceSampleRate,
        ReferenceText = referenceText,
        NumSteps = 4,
        SilenceScale = 0.2f,
        Speed = 1.0f,
        Sid = 0,
    };
    generationConfig.Extra["min_char_in_sentence"] = "10";
    var callback = new OfflineTtsCallbackProgressWithArg((_, _, _, _) =>
    {
        Interlocked.CompareExchange(ref firstCallbackMs, generation.ElapsedMilliseconds, -1);
        return 1;
    });
    var audio = tts.GenerateWithConfig(text, generationConfig, callback);
    generation.Stop();
    try
    {
        if (audio.NumSamples <= 0 || !audio.SaveToWaveFile(outputPath))
        {
            throw new InvalidDataException("ZipVoice produced no audio.");
        }

        var duration = TimeSpan.FromSeconds((double)audio.NumSamples / audio.SampleRate);
        var realTimeFactor = generation.Elapsed.TotalSeconds / duration.TotalSeconds;
        Console.WriteLine(
            $"candidate-zipvoice|load_ms={loadMs}|first_callback_ms={firstCallbackMs}|" +
            $"generate_ms={generation.ElapsedMilliseconds}|audio_seconds={duration.TotalSeconds:F3}|" +
            $"rtf={realTimeFactor:F3}|sample_rate={audio.SampleRate}|path={outputPath}");
    }
    finally
    {
        audio.Dispose();
    }
}
