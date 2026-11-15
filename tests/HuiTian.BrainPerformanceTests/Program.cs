using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using HuiTian.Core;
using HuiTian.Infrastructure;

// Offline, isolated benchmark. Never starts a second resident model or sends
// messages to another person. Each trial owns and cleans up its own server.
if (Process.GetProcessesByName("llama-server").Length > 0 ||
    Process.GetProcessesByName("llama-bench").Length > 0 ||
    Process.GetProcessesByName("HuiTian.App").Length > 0)
    throw new InvalidOperationException("Close HuiTian and other model benchmarks before this isolated trial.");
var threads = args.Length > 0 ? int.Parse(args[0]) : 16;
var batchThreads = args.Length > 1 ? int.Parse(args[1]) : 24;
var draft = args.Length > 2 ? int.Parse(args[2]) : 2;
var loadMode = args.Length > 3 ? args[3] : "auto";
if (threads < 1 || threads > Environment.ProcessorCount || batchThreads < 1 ||
    batchThreads > Environment.ProcessorCount || draft is < 0 or > 3 || loadMode is not ("auto" or "none"))
    throw new ArgumentOutOfRangeException(nameof(args));
var diagnostic = args.Length > 4 && args[4] == "diagnostic";
var paths = new HuiTianPaths();
var output = Path.Combine(paths.Cache, "Tests", $"brain-trial-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-t{threads}-b{batchThreads}-d{draft}-{loadMode}");
Directory.CreateDirectory(output);
using var downloader = new SegmentedDownloadEngine();
var installation = new LocalBrainInstaller(paths, downloader).GetInstalled(CapabilityTierId.Huang);
var start = new ProcessStartInfo
{
    FileName = Path.Combine(paths.BrainRuntime, "b10566", "llama-server.exe"),
    WorkingDirectory = output, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true,
};
typeof(LocalBrainServer).GetMethod("AddArguments", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, [start, installation, draft > 0]);
SetArgument("--threads", threads.ToString());
SetArgument("--threads-batch", batchThreads.ToString());
if (draft > 0) SetArgument("--spec-draft-n-max", draft.ToString());
start.ArgumentList.Add("--load-mode");
start.ArgumentList.Add(loadMode);
if (diagnostic) { start.ArgumentList.Add("--verbosity"); start.ArgumentList.Add("4"); }
start.Environment["TEMP"] = paths.Temp;
start.Environment["TMP"] = paths.Temp;
using var timer = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
var startup = Stopwatch.StartNew();
using var process = Process.Start(start) ?? throw new InvalidOperationException("No benchmark server.");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
var results = new List<object>();
try
{
    while (true)
    {
        timer.Token.ThrowIfCancellationRequested();
        if (process.HasExited) throw new InvalidOperationException("Benchmark server exited during startup.");
        try
        {
            using var health = await http.GetAsync("http://127.0.0.1:18180/health", timer.Token);
            if (health.IsSuccessStatusCode) break;
        }
        catch (HttpRequestException) { }
        await Task.Delay(500, timer.Token);
    }
    var startupMs = startup.ElapsedMilliseconds;
    if (diagnostic) await CaptureGpuAsync(output, "ready");
    Console.WriteLine($"READY threads={threads} batch={batchThreads} draft={draft} startup={startupMs}ms");
    (string Id, string System, string User)[] cases =
    [
        ("plain", "用简体中文直接回答，不输出思考过程。", "用两句话解释为什么整理文件前要先留一份备份。"),
        ("reply", "结合对方最新消息认真回复，不虚构承诺或经历。只输出发给对方的自然中文，完整表达，最多两句话。",
            "私下目的：了解同事进展并给予支持。最近对话：我方：今天项目进度怎么样？对方：需求又改了，我怕这周赶不完，有点着急。"),
        ("slots", "提取用户要求，输出JSON：{\"action\":动作,\"target\":联系人,\"minutes\":分钟}，不要解释。",
            "用微信和同事小陈沟通一下，持续二十分钟，了解下他手头那件事的进展。"),
    ];
    foreach (var sample in cases)
    {
        var elapsed = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync("http://127.0.0.1:18180/v1/chat/completions", new
        {
            model = "huitian-local", messages = new[] { new { role = "system", content = sample.System }, new { role = "user", content = sample.User } },
            temperature = 0, seed = 42, max_tokens = 80, stream = false, cache_prompt = false,
            chat_template_kwargs = new { enable_thinking = false },
        }, timer.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timer.Token));
        var choice = document.RootElement.GetProperty("choices")[0];
        var content = choice.GetProperty("message").GetProperty("content").GetString();
        var finish = choice.GetProperty("finish_reason").GetString();
        JsonElement? timings = document.RootElement.TryGetProperty("timings", out var measured) ? measured.Clone() : null;
        process.Refresh();
        var result = new { id = sample.Id, milliseconds = elapsed.ElapsedMilliseconds, finish, content, timings, workingSetBytes = process.WorkingSet64 };
        results.Add(result);
        if (diagnostic) await CaptureGpuAsync(output, sample.Id);
        Console.WriteLine($"RESULT {sample.Id}: {elapsed.ElapsedMilliseconds}ms finish={finish} {content}");
    }
    var report = Path.Combine(output, "report.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { threads, batchThreads, draft, loadMode, startupMs, results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"REPORT={report}");
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(output, "server-out.log"), await stdout);
    await File.WriteAllTextAsync(Path.Combine(output, "server-error.log"), await stderr);
}

void SetArgument(string name, string value)
{
    var index = start.ArgumentList.IndexOf(name);
    if (index < 0) throw new InvalidOperationException($"Missing production option {name}");
    start.ArgumentList[index + 1] = value;
}

static async Task CaptureGpuAsync(string output, string phase)
{
    using var gpu = new Process
    {
        StartInfo = new ProcessStartInfo(@"C:\Windows\System32\nvidia-smi.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "--query-gpu=name,memory.total,memory.used,utilization.gpu,temperature.gpu,clocks.sm,power.draw", "--format=csv,noheader" }
        }
    };
    if (!gpu.Start()) return;
    var text = gpu.StandardOutput.ReadToEndAsync();
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    try
    {
        await gpu.WaitForExitAsync(limit.Token);
        await File.AppendAllTextAsync(Path.Combine(output, "gpu-samples.txt"), $"{DateTimeOffset.Now:O} {phase}: {await text}\n");
    }
    catch (OperationCanceledException) { if (!gpu.HasExited) gpu.Kill(); }
}