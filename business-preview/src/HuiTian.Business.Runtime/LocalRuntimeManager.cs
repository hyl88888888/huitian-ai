using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using HuiTian.Business.Core;

namespace HuiTian.Business.Runtime;

public record RuntimeStatus(bool IsInstalled, bool IsRunning, string Message, string ModelName, double MemoryGiB);
public sealed class LocalRuntimeManager : IAsyncDisposable
{
    public const string Alias = "huitian-business-local";
    public const int Port = 18280;
    private readonly BusinessPaths _paths;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _owned;
    private SafeFileHandle? _processJob;
    private Task? _stdout;
    private Task? _stderr;
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private string? _verifiedModel;
    private bool _disposed;
    public LocalRuntimeManager(BusinessPaths paths) => _paths = paths;
    public static IReadOnlyList<ModelAsset> Models { get; } =
    [
        new("Qwen3.5-2B-Q4_K_M.gguf", "轻量 2B · 本地文字", 1280835840,
            "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223")
    ];
    private ModelAsset? SelectModel()
    {
        // Selection is hardware/resource policy, not a user-language router.
        var config = Path.Combine(_paths.Data, "model-selection.json");
        if (File.Exists(config))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(config));
                var name = json.RootElement.GetProperty("fileName").GetString();
                var chosen = Models.FirstOrDefault(x => x.FileName == name);
                if (chosen is not null && File.Exists(Path.Combine(_paths.Models, chosen.FileName))) return chosen;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
        }
        return Models.FirstOrDefault(x => File.Exists(Path.Combine(_paths.Models, x.FileName)));
    }
    public async Task<RuntimeStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var model = SelectModel();
        var installed = model is not null && File.Exists(Path.Combine(_paths.Runtime, "llama", "llama-server.exe"));
        var running = installed && await IsReadyAsync(cancellationToken);
        return new(installed, running, running ? "本地模型已就绪 · CPU 模式" : installed ? "本地模型已安装 · 首次使用时启动" : "尚未安装本地模型，请运行产品目录内的安装脚本", model?.Label ?? "未安装", Memory().Total);
    }
    private async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/v1/models");
            var keyPath = Path.Combine(_paths.Data, "local-model.key");
            if (File.Exists(keyPath)) request.Headers.Authorization = new("Bearer", (await File.ReadAllTextAsync(keyPath, ct)).Trim());
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.GetProperty("data").EnumerateArray().Any(x => x.GetProperty("id").GetString() == Alias);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { ct.ThrowIfCancellationRequested(); return false; }
    }
    public async Task EnsureReadyAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsReadyAsync(cancellationToken)) return;
            _paths.EnsureDirectories();
            var keyPath = Path.Combine(_paths.Data, "local-model.key");
            if (!File.Exists(keyPath)) await File.WriteAllTextAsync(keyPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), cancellationToken);
            var model = SelectModel() ?? throw new InvalidOperationException("本地模型尚未安装。请运行 D 盘产品目录内的 Project/scripts/Install-Models.ps1；不会连接付费云端。");
            var memory = Memory();
            if (memory.Total > 0 && memory.Total < 7.0) throw new InvalidOperationException("本预览版要求至少 8 GB 系统内存，未启动模型。推荐 16 GB＋SSD。");
            var minimumFree = 2.0;
            if (memory.Available > 0 && memory.Available < minimumFree) throw new InvalidOperationException($"当前可用内存只有 {memory.Available:F1} GB，请先关闭不使用的大型软件；没有强行加载模型。");
            var modelPath = Path.Combine(_paths.Models, model.FileName);
            var executable = Path.Combine(_paths.Runtime, "llama", "llama-server.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("本地推理组件尚未安装，请运行产品安装脚本。");
            if (_verifiedModel != modelPath)
            {
                progress?.Report("正在校验本地模型完整性");
                await using var file = File.OpenRead(modelPath);
                if (file.Length != model.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模型文件校验失败，未启动。请重新下载，原自用版不受影响。");
                _verifiedModel = modelPath;
            }
            if (_owned is not null) await StopOwnedAsync();
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            foreach (var argument in new[] {
                "--model", modelPath, "--alias", Alias, "--host", "127.0.0.1", "--port", Port.ToString(),
                "--ctx-size", "8192", "--gpu-layers", "0", "--parallel", "1",
                "--threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
                "--threads-batch", Math.Clamp(Environment.ProcessorCount / 2, 2, 12).ToString(),
                "--batch-size", "256", "--ubatch-size", "128", "--jinja",
                "--reasoning", "off", "--reasoning-budget", "0", "--offline", "--no-webui", "--no-warmup",
                "--api-key-file", keyPath, "--cors-origins", "http://huitian.local"
            }) info.ArgumentList.Add(argument);
            info.Environment["TEMP"] = _paths.Temp;
            info.Environment["TMP"] = _paths.Temp;
            progress?.Report("正在启动轻量本地模型 · 不加载 27B / 视觉 / 语音模型");
            _owned = Process.Start(info) ?? throw new InvalidOperationException("本地模型进程未能启动。");
            _processJob = CreateOwnedProcessJob(_owned);
            _stdout = DrainAsync(_owned.StandardOutput);
            _stderr = DrainAsync(_owned.StandardError);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_owned.HasExited) throw new InvalidOperationException($"本地模型启动失败（退出码 {_owned.ExitCode}），详情保存在 Logs/runtime.log。");
                if (await IsReadyAsync(cancellationToken))
                {
                    progress?.Report("本地模型已就绪 · 正在理解任务");
                    return;
                }
                await Task.Delay(300, cancellationToken);
            }
            throw new TimeoutException("本地模型启动超过 90 秒，已停止本次启动。请检查可用内存和磁盘。");
        }
        catch { await StopOwnedAsync(); throw; }
        finally { _gate.Release(); }
    }
    private async Task DrainAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await _logGate.WaitAsync();
                try { await File.AppendAllTextAsync(Path.Combine(_paths.Logs, "runtime.log"), $"{DateTimeOffset.Now:O} {line}\n"); }
                finally { _logGate.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private async Task StopOwnedAsync()
    {
        var process = _owned;
        _owned = null;
        if (process is null) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); _processJob?.Dispose(); _processJob = null; }
        if (_stdout is not null) await _stdout;
        if (_stderr is not null) await _stderr;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; await StopOwnedAsync(); _http.Dispose(); }
        finally { _gate.Release(); }
    }
    public static (double Total, double Available) Memory()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var value = new MemoryInfo();
        return GlobalMemoryStatusEx(value) ? (value.TotalPhysical / 1073741824d, value.AvailablePhysical / 1073741824d) : (0, 0);
    }
    // The OS terminates only this app's model job if the app crashes or is force-closed.
    // Existing local runtimes discovered by health checks are never assigned to our job.
    private static SafeFileHandle? CreateOwnedProcessJob(Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立本地模型进程生命周期保护。");
        try
        {
            var limits = new JobExtendedLimits { Basic = new JobBasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtendedLimits>()) ||
                !AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法绑定本地模型进程生命周期。");
            return job;
        }
        catch { job.Dispose(); throw; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessTime, PerJobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public JobIoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryInfo memory);
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryInfo
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryInfo>();
        public uint Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
public record ModelAsset(string FileName, string Label, long Bytes, string Sha256);
