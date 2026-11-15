using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HuiTian.Business.Core;

namespace HuiTian.Business.App;

public partial class App : Application
{
    private BusinessPaths? _paths;
    private bool _headless;
    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnexpected(args.Exception);
            args.Handled = true;
            if (!_headless) MessageBox.Show("工作台遇到未预期的问题，详细记录已保存在产品 Logs 目录。请重新打开工作台。", "回天 · 工作台", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogUnexpected(args.ExceptionObject as Exception);
    }
    private void LogUnexpected(Exception? error)
    {
        try
        {
            var directory = (_paths ?? BusinessPaths.Discover()).Logs;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "app-errors.log"), $"{DateTimeOffset.UtcNow:O}\n{error}\n");
        }
        catch { /* The last-resort handler cannot assume the disk is writable. */ }
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = _paths = BusinessPaths.Discover();
        paths.EnsureDirectories();
        Environment.SetEnvironmentVariable("TEMP", paths.Temp);
        Environment.SetEnvironmentVariable("TMP", paths.Temp);
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", Path.Combine(paths.Runtime, "Browsers"));
        var previewIndex = Array.IndexOf(e.Args, "--render-preview");
        var isPreview = previewIndex >= 0 && previewIndex + 1 < e.Args.Length;
        var verificationIndex = Array.IndexOf(e.Args, "--verify-workspace");
        var localToolsVerificationIndex = Array.IndexOf(e.Args, "--verify-local-tools");
        _headless = isPreview || verificationIndex >= 0 || localToolsVerificationIndex >= 0;
        if (localToolsVerificationIndex >= 0 && localToolsVerificationIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await LocalToolsVerification.RunAsync(paths, e.Args[localToolsVerificationIndex + 1]);
            Shutdown(result ? 0 : 1);
            return;
        }
        if (verificationIndex >= 0 && verificationIndex + 1 < e.Args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await WorkspaceVerification.RunAsync(paths, e.Args[verificationIndex + 1]);
            Shutdown(result ? 0 : 1);
            return;
        }
        var window = new MainWindow(paths, isPreview);
        MainWindow = window;
        if (isPreview)
        {
            // Render the real WPF layout without opening or capturing the user's desktop.
            window.Width = 1440;
            window.Height = 960;
            var localPreview = e.Args.Contains("--local-tools");
            var localWindow = localPreview ? new LocalToolsWindow(paths) : null;
            var previewWindow = (Window?)localWindow ?? window;
            var content = (FrameworkElement)previewWindow.Content;
            previewWindow.Content = null;
            var width = localPreview ? 860 : 1440;
            var height = localPreview ? 800 : 960;
            var visual = new System.Windows.Controls.Border { Background = previewWindow.Background, Child = content };
            previewWindow.Content = visual;
            visual.Width = width;
            visual.Height = height;
            visual.Measure(new Size(width, height));
            visual.Arrange(new Rect(0, 0, width, height));
            visual.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(e.Args[previewIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(path)) encoder.Save(stream);
            await window.DisposeServicesAsync();
            if (localWindow is not null) await localWindow.DisposeServicesAsync();
            Shutdown();
            return;
        }
        window.Show();
    }
}
