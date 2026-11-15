using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using HuiTian.Core;
using HuiTian.Infrastructure;

namespace HuiTian.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!e.Args.Contains("--verify-package", StringComparer.Ordinal))
        {
            base.OnStartup(e);
            return;
        }

        // Read-only packaged-runtime smoke mode: no window, microphone, model
        // process, account connection or product-state save is started.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        var checks = new List<object>();
        var ok = true;
        try
        {
            foreach (var name in new[] { "HuiTian.App.dll", "HuiTian.Core.dll", "HuiTian.Infrastructure.dll", "coreclr.dll", "hostfxr.dll", "PresentationFramework.dll", "System.Speech.dll", "sherpa-onnx.dll", "sherpa-onnx-c-api.dll", "onnxruntime.dll" })
            {
                var exists = File.Exists(Path.Combine(AppContext.BaseDirectory, name));
                checks.Add(new { name, exists });
                ok &= exists;
            }
            var compiledUi = Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("HuiTian.App.g.resources");
            checks.Add(new { name = "compiled-wpf-resources", exists = compiledUi });
            ok &= compiledUi;
            using var downloads = new SegmentedDownloadEngine();
            var installed = new LocalBrainInstaller(new HuiTianPaths(), downloads).GetInstalled(PersonalBrainModelPolicy.SelectedTier);
            var modelExists = File.Exists(installed.BrainPath) && File.Exists(installed.VisionPath);
            checks.Add(new { name = "existing-local-model", exists = modelExists });
            ok &= modelExists;
        }
        catch (Exception error)
        {
            ok = false;
            checks.Add(new { name = "package-check-error", error = error.GetType().Name });
        }
        try
        {
            var root = Path.Combine(new HuiTianPaths().Cache, "Tests");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "package-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"),
                JsonSerializer.Serialize(new { succeeded = ok, executableDirectory = AppContext.BaseDirectory,
                    version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { ok = false; }
        Shutdown(ok ? 0 : 1);
    }
}


