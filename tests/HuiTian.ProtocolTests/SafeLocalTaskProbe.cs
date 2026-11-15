using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using HuiTian.Core;
using HuiTian.Infrastructure;

internal static class SafeLocalTaskProbe
{
    internal static async Task RunAsync()
    {
        if (Process.GetProcessesByName("HuiTian.App").Length > 0 || Process.GetProcessesByName("llama-server").Length > 0)
            throw new InvalidOperationException("Existing app/model found; refusing a duplicate model or interference.");
        var paths = new HuiTianPaths();
        var report = Path.Combine(paths.Cache, "Tests", "safe-local-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var rows = new List<object>();
        async Task Save() => await File.WriteAllTextAsync(report, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        await using var core = new LocalBrainServer(paths);
        using var downloader = new SegmentedDownloadEngine();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var clock = Stopwatch.StartNew();
        Console.WriteLine("Starting installed model only. No network service, desktop input, send, or account actions.");
        try
        {
            await core.StartAsync(new LocalBrainInstaller(paths, downloader).GetInstalled(PersonalBrainModelPolicy.SelectedTier), startup.Token);
            rows.Add(new { stage = "startup", milliseconds = clock.ElapsedMilliseconds });
            Console.WriteLine($"STARTUP {clock.ElapsedMilliseconds}ms");
            await Save();
            using var router = new SemanticIntentRouter(AgentBackendConfiguration.ProductDefault, paths);
            var requests = new[]
            {
                "只用两句话解释备份的用途，不需要保存文件。",
                "我只是想听听备份能解决什么问题，直接在这里回答就行。",
                "做一个可打印的Word交接单，标题为晨间交接，正文分别写设备编号HT-204、状态待检查、负责人小林。保存为docx文件，不发送。",
                "请把晨间交接排成一份能打印的docx：设备编号HT-204，状态待检查，负责人小林。我要实际文件，不要只在聊天里贴文字，也不用发给别人。"
            };
            for (var i = 0; i < requests.Length; i++)
            {
                clock.Restart();
                var decision = await router.ClassifyAsync(requests[i], []);
                var route = SemanticTurnRouting.Decide(decision, false, false);
                var expected = i < 2 ? AgentTaskRoute.DirectAnswer : AgentTaskRoute.Composite;
                rows.Add(new { stage = "classification", index = i, request = requests[i], decision, route, expected, passed = route.Route == expected, milliseconds = clock.ElapsedMilliseconds });
                Console.WriteLine($"CLASSIFY {i}: {route.Route == expected} {clock.ElapsedMilliseconds}ms mode={decision?.Mode} route={route.Route} source={decision?.Source}");
                await Save();
            }
            // Product classifier has been measured above. This next-step harness
            // uses the production universal tool schema, but only two local file
            // generations are dispatched. It is NOT the full App Server loop.
            using var http = new HttpClient { BaseAddress = core.ResponsesBaseUri, Timeout = TimeSpan.FromSeconds(120) };
            var definitions = (object[])typeof(CodexAppServerClient).GetMethod("CreateAllDynamicToolDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            var definition = JsonSerializer.SerializeToElement(definitions[0]);
            var function = new { name = "huitian_execute", description = definition.GetProperty("description").GetString(), parameters = definition.GetProperty("inputSchema") };
            for (var i = 2; i < requests.Length; i++)
            {
                clock.Restart();
                try
                {
                    using var response = await http.PostAsJsonAsync("chat/completions", new
                    {
                        model = "huitian-local", stream = false, temperature = 0, max_tokens = 450,
                        chat_template_kwargs = new { enable_thinking = false },
                        messages = new[] { new { role = "system", content = "你是回天本地助手。按照用户要求执行下一步，使用真实工具创建文件，不得口头冒充完成。只输出一次工具调用。" }, new { role = "user", content = requests[i] } },
                        tools = new[] { new { type = "function", function } }, tool_choice = "required", parallel_tool_calls = false
                    });
                    response.EnsureSuccessStatusCode();
                    var data = await response.Content.ReadFromJsonAsync<JsonElement>();
                    var arguments = data.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!;
                    var call = CodexAppServerClient.ResolveUniversalToolCall(JsonDocument.Parse(arguments).RootElement);
                    var generationMs = clock.ElapsedMilliseconds;
                    // Hard allowlist at the dispatch boundary; never run any other
                    // model-selected tool. DOCX avoids launching Office for PDF.
                    if (call.ToolName != "huitian_create_document" ||
                        (call.Arguments.TryGetProperty("format", out var format) && !string.Equals(format.GetString(), "docx", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Model did not select the allowed DOCX creation operation: " + call.ToolName);
                    clock.Restart();
                    var result = await new WindowsToolBridge().ExecuteAsync(call.ToolName, call.Arguments, CancellationToken.None);
                    var executionMs = clock.ElapsedMilliseconds;
                    var file = result.ArtifactPaths?.SingleOrDefault();
                    var contentOk = false;
                    if (file is not null && File.Exists(file))
                    {
                        using var zip = ZipFile.OpenRead(file);
                        using var stream = zip.GetEntry("word/document.xml")!.Open();
                        var xml = XDocument.Load(stream);
                        var text = string.Concat(xml.Descendants(XName.Get("t", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")).Select(n => n.Value));
                        contentOk = new[] { "晨间交接", "HT-204", "待检查", "小林" }.All(text.Contains);
                    }
                    var verified = DeliverableCompletionVerifier.Verify([new(IntentDeliverableKind.Document, "printable handover", "docx")],
                        [new(call.ToolName, result.Success, result.RequiresVerification, result.ArtifactPaths ?? [], ResultCode: result.ResultCode, ArgumentsJson: call.Arguments.GetRawText())]);
                    rows.Add(new { stage = "generation-and-execution", index = i, generationMs, executionMs, arguments, file, result.Success, verified = verified.Succeeded, contentOk });
                    Console.WriteLine($"EXECUTE {i}: generation={generationMs}ms execution={executionMs}ms verified={verified.Succeeded} requestedContent={contentOk} file={file}");
                }
                catch (Exception ex)
                {
                    rows.Add(new { stage = "generation-and-execution", index = i, milliseconds = clock.ElapsedMilliseconds, error = ex.GetType().Name + ": " + ex.Message });
                    Console.WriteLine($"EXECUTE {i}: FAIL {clock.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
                }
                await Save();
            }
        }
        catch (Exception ex)
        {
            rows.Add(new { stage = "probe-error", milliseconds = clock.ElapsedMilliseconds, error = ex.GetType().Name + ": " + ex.Message });
            await Save();
            throw;
        }
        finally { Console.WriteLine("REPORT " + report); }
    }
}
