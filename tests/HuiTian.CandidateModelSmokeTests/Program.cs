using System.Net.Http.Json;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

const string candidateRoot =
    @"D:\HuiTian\Cache\ModelCandidates\JonathanColetti-Qwen3.8-27B-Uncensored-IQ2_M";
var paths = new HuiTianPaths();
var installation = new LocalBrainInstallation(
    CapabilityTierId.Huang,
    Path.Combine(candidateRoot, "Qwen3.8-27B-Uncensored-IQ2_M.gguf"),
    Path.Combine(candidateRoot, "Qwen3.8-27B-Uncensored-vision-f16.gguf"),
    16_384,
    24);

await using var server = new LocalBrainServer(paths);
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
Console.WriteLine("STARTING: isolated candidate brain");
await server.StartAsync(installation, startupTimeout.Token);
Console.WriteLine("READY: isolated candidate brain");

using var http = new HttpClient
{
    BaseAddress = server.ResponsesBaseUri,
    Timeout = TimeSpan.FromMinutes(5),
};

using var response = await http.PostAsJsonAsync(
    "responses",
    new
    {
        model = "huitian-local",
        instructions = "始终用简体中文回答。你是回天AI。用户问你是什么模型时，只回答：我是回天AI。底层采用本地AI引擎与开放技术组件。",
        input = "你是什么模型？",
        max_output_tokens = 96,
    });
var body = await response.Content.ReadAsStringAsync();
response.EnsureSuccessStatusCode();
using var document = JsonDocument.Parse(body);
var outputText = ExtractOutputText(document.RootElement).Trim();
const string expected = "我是回天AI。底层采用本地AI引擎与开放技术组件。";
if (!string.Equals(outputText, expected, StringComparison.Ordinal))
{
    throw new InvalidDataException($"Candidate identity failed: '{outputText}'");
}

Console.WriteLine($"ANSWER={outputText}");
Console.WriteLine("PASS: candidate Chinese identity");

var visionFixture = Path.Combine(
    paths.Project,
    "vendor",
    "llama.cpp-b10566",
    "tools",
    "mtmd",
    "tests",
    "test-1-positive.png");
var imageData = Convert.ToBase64String(await File.ReadAllBytesAsync(visionFixture));
using var visionResponse = await http.PostAsJsonAsync(
    "responses",
    new
    {
        model = "huitian-local",
        instructions = "准确读取图片中最上方的醒目英文标题，只输出标题。",
        input = new object[]
        {
            new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = "最上方的英文标题是什么？" },
                    new { type = "input_image", image_url = $"data:image/png;base64,{imageData}" },
                },
            },
        },
        max_output_tokens = 128,
    });
var visionBody = await visionResponse.Content.ReadAsStringAsync();
visionResponse.EnsureSuccessStatusCode();
using var visionDocument = JsonDocument.Parse(visionBody);
var visionText = ExtractOutputText(visionDocument.RootElement).Trim();
if (!visionText.Contains("Powdery Surface", StringComparison.OrdinalIgnoreCase) ||
    !visionText.Contains("Closely Explored", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException($"Candidate vision failed: '{visionText}'");
}

Console.WriteLine($"VISION_ANSWER={visionText}");
Console.WriteLine("PASS: candidate matching visual projector");

static string ExtractOutputText(JsonElement root)
{
    if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    var parts = new List<string>();
    foreach (var item in output.EnumerateArray())
    {
        if (!item.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var type) &&
                type.GetString() == "output_text" &&
                part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }
    }

    return string.Concat(parts);
}
