using System.Net.Http.Json;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

var paths = new HuiTianPaths();
using var downloadEngine = new SegmentedDownloadEngine();
var installer = new LocalBrainInstaller(paths, downloadEngine);
var installation = installer.GetInstalled(CapabilityTierId.Huang);

await using var server = new LocalBrainServer(paths);
using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
Console.WriteLine("STARTING: local brain");
await server.StartAsync(installation, startupTimeout.Token);
Console.WriteLine("READY: local brain");

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
        instructions = "你是回天AI。用户问你是什么模型时，只回答：我是回天AI。底层采用本地AI引擎与开放技术组件。",
        input = "你是什么模型？",
        max_output_tokens = 96,
    });
var body = await response.Content.ReadAsStringAsync();
var tracePath = Path.Combine(paths.AppState, "last-brain-response.json");
await File.WriteAllTextAsync(tracePath, body);
response.EnsureSuccessStatusCode();

using var document = JsonDocument.Parse(body);
var outputText = ExtractOutputText(document.RootElement);
if (string.IsNullOrWhiteSpace(outputText))
{
    throw new InvalidDataException($"The local response contained no text. See {tracePath}");
}

Console.WriteLine($"ANSWER={outputText.Trim()}");
Console.WriteLine("PASS: local Responses API text inference");
Console.WriteLine($"TRACE={tracePath}");

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
        instructions = "请准确读取图片中的醒目英文标题，只输出标题文字。",
        input = new object[]
        {
            new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = "图片最上方的英文标题是什么？" },
                    new
                    {
                        type = "input_image",
                        image_url = $"data:image/png;base64,{imageData}",
                    },
                },
            },
        },
        max_output_tokens = 128,
    });
var visionBody = await visionResponse.Content.ReadAsStringAsync();
var visionTracePath = Path.Combine(paths.AppState, "last-brain-vision-response.json");
await File.WriteAllTextAsync(visionTracePath, visionBody);
visionResponse.EnsureSuccessStatusCode();
using var visionDocument = JsonDocument.Parse(visionBody);
var visionText = ExtractOutputText(visionDocument.RootElement);
if (!visionText.Contains("Powdery Surface", StringComparison.OrdinalIgnoreCase) ||
    !visionText.Contains("Closely Explored", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidDataException(
        $"The visual title was not recognized. Actual: '{visionText}'. See {visionTracePath}");
}

Console.WriteLine($"VISION_ANSWER={visionText.Trim()}");
Console.WriteLine("PASS: local visual understanding");
Console.WriteLine($"VISION_TRACE={visionTracePath}");

static string ExtractOutputText(JsonElement root)
{
    if (!root.TryGetProperty("output", out var output) ||
        output.ValueKind != JsonValueKind.Array)
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
