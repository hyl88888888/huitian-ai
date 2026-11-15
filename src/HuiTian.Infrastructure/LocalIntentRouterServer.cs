using System.Diagnostics;

namespace HuiTian.Infrastructure;

/// <summary>
/// Hosts a small local semantic compiler beside the product brain.  It is not
/// a second assistant: it can only turn a user goal into the structured intent
/// contract consumed by the 27B brain and the App Server.  Keeping it on CPU
/// prevents the routing preflight from occupying the main model's single slot.
/// </summary>
public sealed class LocalIntentRouterServer : IAsyncDisposable
{
    public const int DefaultPort = 18182;
    public const string ModelFileName = "huitian-intent-q4_k_m.gguf";

    private readonly HuiTianPaths _paths;
    private readonly HttpClient _healthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(2),
    };
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private Process? _process;
    private Task? _stdoutReader;
    private Task? _stderrReader;
    private CancellationTokenSource? _lifetime;

    public LocalIntentRouterServer(HuiTianPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public Uri ResponsesBaseUri => new($"http://127.0.0.1:{DefaultPort}/v1/");

    public string ModelPath => Path.Combine(
        _paths.Models,
        "语义路由",
        ModelFileName);

    public bool IsInstalled => File.Exists(ModelPath);

    public async Task<bool> StartIfInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsInstalled)
        {
            return false;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await StopOwnedProcessAsync().ConfigureAwait(false);
            _paths.EnsureDirectories();
            var executable = ResolveServerExecutable();
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = _paths.BrainRuntime,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var threads = Math.Clamp(Environment.ProcessorCount / 4, 4, 8);
            foreach (var argument in new[]
                     {
                         "--model", ModelPath,
                         "--host", "127.0.0.1",
                         "--port", DefaultPort.ToString(),
                         "--ctx-size", "2048",
                         "--gpu-layers", "0",
                         "--threads", threads.ToString(),
                         "--threads-batch", Math.Min(Environment.ProcessorCount, 16).ToString(),
                         "--parallel", "1",
                         "--batch-size", "256",
                         "--ubatch-size", "128",
                         "--jinja",
                         "--offline",
                         "--no-webui",
                         "--log-disable",
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _lifetime = new CancellationTokenSource();
            _process = Process.Start(startInfo)
                       ?? throw new InvalidOperationException("本机语义路由没有成功启动。");
            _stdoutReader = DrainAsync(_process.StandardOutput, _lifetime.Token);
            _stderrReader = DrainAsync(_process.StandardError, _lifetime.Token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(25);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"本机语义路由意外退出，代码 {_process.ExitCode}。");
                }

                if (await IsHealthyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            await StopOwnedProcessAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _healthClient.GetAsync(
                    new Uri(ResponsesBaseUri, "../health"),
                    cancellationToken)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string ResolveServerExecutable() => Directory
        .EnumerateFiles(_paths.BrainRuntime, "llama-server.exe", SearchOption.AllDirectories)
        .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault()
        ?? throw new FileNotFoundException("回天本地运行组件尚未安装。");

    private static async Task DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                // The lightweight router intentionally keeps no user-content log.
            }
        }
        catch (OperationCanceledException)
        {
            // Expected at product shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Expected when the owned process exits.
        }
    }

    private async Task StopOwnedProcessAsync()
    {
        _lifetime?.Cancel();
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // It exited between the state check and the stop request.
            }
        }

        if (_stdoutReader is not null || _stderrReader is not null)
        {
            try
            {
                await Task.WhenAll(
                        _stdoutReader ?? Task.CompletedTask,
                        _stderrReader ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch
            {
                // Stream draining is best-effort during shutdown.
            }
        }

        _process?.Dispose();
        _process = null;
        _stdoutReader = null;
        _stderrReader = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnedProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
            _startGate.Dispose();
            _healthClient.Dispose();
        }
    }
}
