using System.Diagnostics;
using System.Net;

namespace HuiTian.Infrastructure;

public sealed class LocalBrainServer : IAsyncDisposable
{
    public const int DefaultPort = 18180;

    private readonly HuiTianPaths _paths;
    private readonly HttpClient _healthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };
    private readonly SemaphoreSlim _logGate = new(1, 1);
    private Process? _process;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private string? _logPath;

    public LocalBrainServer(HuiTianPaths paths)
    {
        _paths = paths;
    }

    public Uri ResponsesBaseUri => new($"http://127.0.0.1:{DefaultPort}/v1/");

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(
        LocalBrainInstallation installation,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        _paths.EnsureDirectories();
        var executable = ResolveServerExecutable();
        ValidateInstallation(installation);
        _logPath = Path.Combine(_paths.AppState, "brain-server.log");
        var decodeSettings = await LocalBrainDecodeProfile.LoadAsync(
                Path.Combine(_paths.AppState, "brain-decode-profile.json"),
                executable,
                installation.BrainPath,
                installation.VisionPath,
                installation.ContextTokens,
                async token =>
                {
                    var hardware = await new WindowsHardwareDetector(_paths).DetectAsync(token)
                        .ConfigureAwait(false);
                    return LocalBrainDecodeProfile.CreateHardwareSignature(
                        hardware.CpuName, hardware.GpuName,
                        hardware.PhysicalMemoryBytes, hardware.VideoMemoryBytes);
                },
                cancellationToken)
            .ConfigureAwait(false);
        await AppendLogAsync(decodeSettings.Message).ConfigureAwait(false);

        try
        {
            StartProcess(executable, installation, decodeSettings.UseEmbeddedMtp);
            try
            {
                await WaitUntilReadyAsync(TimeSpan.FromMinutes(8), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch when (decodeSettings.UseEmbeddedMtp && !cancellationToken.IsCancellationRequested)
            {
                await AppendLogAsync("内置解码加速未能启动，正在自动回退兼容模式。")
                    .ConfigureAwait(false);
                await StopAsync().ConfigureAwait(false);
                StartProcess(executable, installation, useEmbeddedMtp: false);
                await WaitUntilReadyAsync(TimeSpan.FromMinutes(8), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void StartProcess(
        string executable,
        LocalBrainInstallation installation,
        bool useEmbeddedMtp)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };

        AddArguments(startInfo, installation, useEmbeddedMtp);
        startInfo.Environment["TEMP"] = _paths.Temp;
        startInfo.Environment["TMP"] = _paths.Temp;
        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("回天本地核心未能启动。");
        _stdoutPump = PumpOutputAsync(_process.StandardOutput);
        _stderrPump = PumpOutputAsync(_process.StandardError);
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process already ended between the state check and shutdown.
        }

        if (_stdoutPump is not null || _stderrPump is not null)
        {
            await Task.WhenAll(
                    _stdoutPump ?? Task.CompletedTask,
                    _stderrPump ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }

        process.Dispose();
        _stdoutPump = null;
        _stderrPump = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _healthClient.Dispose();
        _logGate.Dispose();
    }

    private string ResolveServerExecutable()
    {
        var candidates = Directory.Exists(_paths.BrainRuntime)
            ? Directory.EnumerateFiles(
                    _paths.BrainRuntime,
                    "llama-server.exe",
                    SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        return candidates.FirstOrDefault()
               ?? throw new FileNotFoundException("回天本地运行组件尚未安装。");
    }

    private static void ValidateInstallation(LocalBrainInstallation installation)
    {
        if (!File.Exists(installation.BrainPath))
        {
            throw new FileNotFoundException("回天核心文件不存在。", installation.BrainPath);
        }

        if (!File.Exists(installation.VisionPath))
        {
            throw new FileNotFoundException("屏幕理解组件不存在。", installation.VisionPath);
        }
    }

    private static void AddArguments(
        ProcessStartInfo startInfo,
        LocalBrainInstallation installation,
        bool useEmbeddedMtp)
    {
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 4, 16);
        var arguments = new[]
        {
            "--model", installation.BrainPath,
            "--mmproj", installation.VisionPath,
            "--host", "127.0.0.1",
            "--port", DefaultPort.ToString(),
            "--ctx-size", installation.ContextTokens.ToString(),
            "--gpu-layers", "auto",
            "--threads", threads.ToString(),
            "--threads-batch", Math.Min(Environment.ProcessorCount, 24).ToString(),
            "--parallel", "1",
            "--fit", "on",
            "--fit-target", "768",
            "--fit-ctx", "4096",
            "--cache-type-k", "q8_0",
            "--cache-type-v", "q8_0",
            "--jinja",
            // The product shows task progress separately.  Letting the local
            // template emit a long <think> monologue delayed the first real
            // action by more than a minute and leaked English reasoning into
            // the conversation.  Disable it at the runtime boundary instead
            // of relying only on a per-request hint.
            "--reasoning", "off",
            "--reasoning-budget", "0",
            "--offline",
            "--no-webui",
            "--cors-origins", "localhost",
            // Qwen-VL grounding requires at least 1024 image tokens.  Using
            // 512 made the runtime warn that coordinate/region recognition
            // could be inaccurate, which is exactly the failure mode a
            // desktop-control agent cannot tolerate.
            "--image-min-tokens", "1024",
            "--log-timestamps",
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (useEmbeddedMtp)
        {
            startInfo.ArgumentList.Add("--spec-type");
            startInfo.ArgumentList.Add("draft-mtp");
            startInfo.ArgumentList.Add("--spec-draft-n-max");
            // Two draft tokens benchmark faster for this quantized brain's
            // short Chinese dialogue than the runtime's generic value of three
            // (three reduced draft acceptance on the target laptop).
            startInfo.ArgumentList.Add("2");
        }
    }

    private async Task AppendLogAsync(string line)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            return;
        }

        await _logGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(
                    _logPath,
                    $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}")
                .ConfigureAwait(false);
        }
        finally
        {
            _logGate.Release();
        }
    }

    private async Task WaitUntilReadyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var healthUri = new Uri(ResponsesBaseUri, "../health");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
            {
                throw new InvalidOperationException(
                    $"回天本地核心启动后意外退出。请查看 {_logPath}");
            }

            try
            {
                using var response = await _healthClient.GetAsync(healthUri, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The server socket is not open while the model is loading.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Individual health probes may time out during startup.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException($"回天本地核心启动超时。请查看 {_logPath}");
    }

    private async Task PumpOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(_logPath))
            {
                continue;
            }

            await _logGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(
                        _logPath,
                        $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}")
                    .ConfigureAwait(false);
            }
            finally
            {
                _logGate.Release();
            }
        }
    }
}
