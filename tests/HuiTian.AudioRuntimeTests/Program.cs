using NAudio.Wave;
using SherpaOnnx;

using System.Text;

const string asrRoot = @"D:\HuiTian\Runtime\Voice\ASR\sherpa-onnx-streaming-zipformer-zh-int8-2025-06-30";
const string asrSampleRoot = @"D:\HuiTian\Runtime\Voice\ASR\sherpa-onnx-streaming-zipformer-zh-14M-2023-02-23";
const string ttsRoot = @"D:\HuiTian\Runtime\Voice\Kokoro";
const string previewPath = @"D:\HuiTian\Build\VoicePreview.wav";

var recognizerConfig = new OnlineRecognizerConfig();
recognizerConfig.FeatConfig.SampleRate = 16_000;
recognizerConfig.FeatConfig.FeatureDim = 80;
recognizerConfig.ModelConfig.Transducer.Encoder = Path.Combine(asrRoot, "encoder.int8.onnx");
recognizerConfig.ModelConfig.Transducer.Decoder = Path.Combine(asrRoot, "decoder.onnx");
recognizerConfig.ModelConfig.Transducer.Joiner = Path.Combine(asrRoot, "joiner.int8.onnx");
recognizerConfig.ModelConfig.Tokens = Path.Combine(asrRoot, "tokens.txt");
recognizerConfig.ModelConfig.NumThreads = 2;
recognizerConfig.ModelConfig.Provider = "cpu";
recognizerConfig.DecodingMethod = "modified_beam_search";
recognizerConfig.MaxActivePaths = 4;
recognizerConfig.EnableEndpoint = 1;
var hotwordsPath = Path.Combine(asrRoot, "huitian-hotwords.txt");
File.WriteAllText(
    hotwordsPath,
    string.Join('\n', "回天", "你好小天", "回天回天", "微信", "联系人"),
    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
recognizerConfig.HotwordsFile = hotwordsPath;
recognizerConfig.HotwordsScore = 2.0f;

using (var recognizer = new OnlineRecognizer(recognizerConfig))
using (var stream = recognizer.CreateStream())
using (var reader = new AudioFileReader(Path.Combine(asrSampleRoot, "test_wavs", "0.wav")))
{
    if (reader.WaveFormat.SampleRate != 16_000 || reader.WaveFormat.Channels != 1)
    {
        throw new InvalidDataException("ASR test audio must be 16 kHz mono.");
    }

    var buffer = new float[1_600];
    var pieces = new List<string>();
    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        stream.AcceptWaveform(16_000, buffer[..read]);
        while (recognizer.IsReady(stream))
        {
            recognizer.Decode(stream);
        }

        if (recognizer.IsEndpoint(stream))
        {
            var text = recognizer.GetResult(stream).Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                pieces.Add(text);
            }

            recognizer.Reset(stream);
        }
    }

    stream.AcceptWaveform(16_000, new float[16_000]);
    stream.InputFinished();
    while (recognizer.IsReady(stream))
    {
        recognizer.Decode(stream);
    }

    var tail = recognizer.GetResult(stream).Text?.Trim();
    if (!string.IsNullOrWhiteSpace(tail))
    {
        pieces.Add(tail);
    }

    var recognized = string.Join(string.Empty, pieces);
    if (string.IsNullOrWhiteSpace(recognized))
    {
        throw new InvalidDataException("Local streaming ASR produced no text.");
    }

    Console.WriteLine($"PASS: streaming ASR => {recognized}");
}

var ttsConfig = new OfflineTtsConfig();
ttsConfig.Model.Kokoro.Model = Path.Combine(ttsRoot, "model.int8.onnx");
ttsConfig.Model.Kokoro.Voices = Path.Combine(ttsRoot, "voices.bin");
ttsConfig.Model.Kokoro.Tokens = Path.Combine(ttsRoot, "tokens.txt");
ttsConfig.Model.Kokoro.DataDir = Path.Combine(ttsRoot, "espeak-ng-data");
ttsConfig.Model.Kokoro.DictDir = Path.Combine(ttsRoot, "dict");
ttsConfig.Model.Kokoro.Lexicon = string.Join(",",
    Path.Combine(ttsRoot, "lexicon-us-en.txt"),
    Path.Combine(ttsRoot, "lexicon-zh.txt"));
ttsConfig.Model.NumThreads = 3;
ttsConfig.Model.Provider = "cpu";
ttsConfig.MaxNumSentences = 1;
ttsConfig.RuleFsts = string.Join(",",
    Path.Combine(ttsRoot, "phone-zh.fst"),
    Path.Combine(ttsRoot, "date-zh.fst"),
    Path.Combine(ttsRoot, "number-zh.fst"));

Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
using (var tts = new OfflineTts(ttsConfig))
{
    var audio = tts.GenerateWithConfig(
        "你好，我是回天。现在可以开始语音对话。",
        new OfflineTtsGenerationConfig { Sid = 10, Speed = 1.0f, SilenceScale = 0.2f },
        null);
    try
    {
        if (audio.NumSamples <= 0 || !audio.SaveToWaveFile(previewPath))
        {
            throw new InvalidDataException("Natural TTS produced no audio.");
        }

        Console.WriteLine($"PASS: natural TTS => {previewPath} ({audio.SampleRate} Hz, {audio.NumSamples} samples)");
    }
    finally
    {
        audio.Dispose();
    }
}
