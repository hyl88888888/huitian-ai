using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HuiTian.App;
using Application = System.Windows.Application;
using MediaColor = System.Windows.Media.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Windows.Point;

namespace HuiTian.OverlayVisualTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--offscreen"))
        {
            RenderOffscreen();
            return;
        }
        const string output = @"D:\HuiTian\Build\Validation-0.3.4\control-overlay.png";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new ComputerControlOverlayWindow();
        overlay.ApplyLanguage("zh-CN");
        overlay.ShowControl("回天正在操作电脑");
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        overlay.ShowPointer(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }

            bitmap.Save(output, ImageFormat.Png);
            overlay.Close();
            application.Shutdown();
        };
        timer.Start();
        application.Run();
        Console.WriteLine($"PASS: desktop control overlay => {output}");
    }

    private static void RenderOffscreen()
    {
        // Render our own WPF tree only. Never call ShowControl, ShowPointer, Show,
        // EnsureHandle, CopyFromScreen or any real input operation in this path.
        const string outputDirectory = @"D:\HuiTian\Build\Validation-0.4.9";
        Directory.CreateDirectory(outputDirectory);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new ComputerControlOverlayWindow();
        try
        {
            overlay.ApplyLanguage("zh-CN");
            var pointer = (Grid)overlay.FindName("VirtualPointer");
            var banner = (Border)overlay.FindName("ControlBanner");
            var message = (TextBlock)overlay.FindName("ControlMessage");
            var edge = (Border)overlay.FindName("ControlBorder");
            var glow = (Border)overlay.FindName("OuterGlow");
            var rotation = (RotateTransform)overlay.FindName("GradientRotation");
            var clickRing = (System.Windows.Shapes.Ellipse)overlay.FindName("PointerRing");
            var root = (Grid)overlay.Content;
            Require(pointer.Width is > 0 and <= 24 && pointer.Height is > 0 and <= 30,
                "Pointer must stay small (at most 24 x 30 DIPs).");
            Require(pointer.Visibility == Visibility.Collapsed, "An idle overlay must not show a stale pointer.");
            Require(clickRing.Opacity == 0 && clickRing.StrokeThickness >= 1.5,
                "The click ring must stay hidden until an action, then render a visible outline.");
            Require(!root.IsHitTestVisible && !overlay.Focusable && !overlay.ShowActivated,
                "The overlay must neither intercept WPF input nor request focus.");
            Require(edge.BorderThickness.Left >= 4 && edge.BorderThickness.Top >= 4 &&
                    edge.BorderThickness.Right >= 4 && edge.BorderThickness.Bottom >= 4,
                "Every edge must retain a visible 4-DIP core.");
            Require(glow.BorderThickness.Left >= 18 && glow.BorderThickness.Top >= 18 &&
                    glow.BorderThickness.Right >= 18 && glow.BorderThickness.Bottom >= 18,
                "Every edge must retain the widened soft glow.");
            Require(banner.MinWidth >= 320 && banner.MinHeight >= 60 && message.FontSize >= 20,
                "The status pill must remain at least 320 x 60 DIPs with 20-DIP text.");
            Require(message.Foreground is SolidColorBrush { Color.A: 255, Opacity: 1 } && message.Opacity == 1,
                "Status text must use an opaque foreground for reliable contrast.");
            var textColor = ((SolidColorBrush)message.Foreground).Color;

            var breathing = (Storyboard)overlay.Resources["BorderBreathing"];
            Require(breathing.AutoReverse && breathing.RepeatBehavior == RepeatBehavior.Forever,
                "The border must keep its continuous breathing animation.");
            var edgePulse = FindAnimation(breathing, "ControlBorder");
            var glowPulse = FindAnimation(breathing, "OuterGlow");
            Require(Minimum(edgePulse) >= 0.78 && Maximum(edgePulse) <= 1 &&
                    Minimum(glowPulse) >= 0.68 && Maximum(glowPulse) <= 1 &&
                    Maximum(edgePulse) > Minimum(edgePulse) && Maximum(glowPulse) > Minimum(glowPulse),
                "Breathing must never fade the edge/glow below 0.78/0.68 opacity.");
            var gradientSweep = FindAnimation(breathing, "GradientRotation");
            var pointerPulse = (Storyboard)overlay.Resources["PointerPulse"];
            Require(FindAnimation(pointerPulse, "PointerRing").From >= 0.9 &&
                    FindAnimation(pointerPulse, "PointerRing").To == 0,
                "A click must briefly show then remove its location ring.");
            VerifyNativeInputMessages(overlay);

            pointer.Visibility = Visibility.Visible;
            Canvas.SetLeft(pointer, 620);
            Canvas.SetTop(pointer, 370);
            overlay.Content = null;
            var panel = new Grid { Width = 1280, Height = 800 };
            panel.Children.Add(root);
            var results = new List<object>();
            // Exercise both gradient endpoints at the lowest opacity. These are
            // conservative visibility bounds, not a claim to capture timed frames.
            foreach (var (theme, background) in new[]
                     {
                         ("light", MediaColor.FromRgb(241, 243, 245)),
                         ("dark", MediaColor.FromRgb(24, 29, 38)),
                     })
            foreach (var (phase, angle) in new[]
                     {
                         ("minimum-start", Minimum(gradientSweep)),
                         ("minimum-end", Maximum(gradientSweep)),
                     })
            {
                edge.Opacity = Minimum(edgePulse);
                glow.Opacity = Minimum(glowPulse);
                rotation.Angle = angle;
                panel.Background = new SolidColorBrush(background);
                panel.Measure(new System.Windows.Size(1280, 800));
                panel.Arrange(new Rect(0, 0, 1280, 800));
                panel.UpdateLayout();
                Require(banner.ActualWidth >= 320 && banner.ActualHeight >= 60 &&
                        banner.ActualWidth <= 640 && message.ActualWidth >= message.DesiredSize.Width,
                    "The laid-out status pill must keep its size and fit the default message.");
                var bannerOrigin = banner.TranslatePoint(new Point(), panel);
                Require(Math.Abs(bannerOrigin.X + banner.ActualWidth / 2 - 640) <= 1 &&
                        bannerOrigin.Y >= 12 && bannerOrigin.Y + banner.ActualHeight < 100,
                    "The status pill must stay near the top center.");
                foreach (var point in new[]
                         {
                             new Point(2, 400), new Point(640, 2), new Point(1277, 400),
                             new Point(640, 797), new Point(640, 45), new Point(626, 380),
                             new Point(640, 400),
                         })
                {
                    var hit = HitTestOffscreen(panel, point);
                    Require(ReferenceEquals(hit, panel),
                        $"Overlay element intercepted filtered visual hit-test at {point}.");
                }

                var bitmap = new RenderTargetBitmap(1280, 800, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var pixels = new byte[1280 * 800 * 4];
                bitmap.CopyPixels(pixels, 1280 * 4, 0);
                // Sample the visible core on all four sides, away from corners
                // and the pill. RGB distance measures color separation here;
                // it is not a WCAG text contrast ratio.
                var edgeDistances = new List<double>();
                foreach (var fraction in new[] { 0.15, 0.35, 0.65, 0.85 })
                {
                    var x = (int)(1279 * fraction);
                    var y = (int)(799 * fraction);
                    edgeDistances.Add(ColorDistance(PixelAt(pixels, x, 1), background));
                    edgeDistances.Add(ColorDistance(PixelAt(pixels, x, 798), background));
                    edgeDistances.Add(ColorDistance(PixelAt(pixels, 1, y), background));
                    edgeDistances.Add(ColorDistance(PixelAt(pixels, 1278, y), background));
                }
                foreach (var corner in new[]
                         {
                             new Point(1, 1), new Point(1278, 1),
                             new Point(1, 798), new Point(1278, 798),
                         })
                {
                    edgeDistances.Add(ColorDistance(PixelAt(pixels, (int)corner.X, (int)corner.Y), background));
                }
                var minimumEdgeDistance = edgeDistances.Min();
                Require(minimumEdgeDistance >= 55,
                    $"A border edge became too faint on {theme}: RGB distance {minimumEdgeDistance:F1} < 55.");
                var pillBackground = PixelAt(pixels, (int)bannerOrigin.X + 36,
                    (int)bannerOrigin.Y + (int)banner.ActualHeight - 10);
                var textContrast = ContrastRatio(textColor, pillBackground);
                Require(textContrast >= 7, $"Status text contrast dropped below 7:1 on {theme}.");
                Require(ColorDistance(PixelAt(pixels, 640, 400), background) <= 1,
                    "The overlay must leave the central work area transparent.");
                var output = Path.Combine(outputDirectory, $"control-overlay-{theme}-{phase}.png");
                SaveBitmap(bitmap, output);
                results.Add(new
                {
                    theme, phase, output, minimumEdgeRgbDistance = minimumEdgeDistance,
                    textContrastRatio = textContrast,
                    bannerWidth = banner.ActualWidth, bannerHeight = banner.ActualHeight,
                    pointerWidth = pointer.Width, pointerHeight = pointer.Height,
                    edgeOpacity = edge.Opacity, glowOpacity = glow.Opacity,
                });
                Console.WriteLine($"PASS {theme}/{phase}: edge distance {minimumEdgeDistance:F1}; " +
                                  $"text contrast {textContrast:F1}:1; pill {banner.ActualWidth} x {banner.ActualHeight}; {output}");
            }

            Require(!overlay.IsVisible && new WindowInteropHelper(overlay).Handle == IntPtr.Zero,
                "Offscreen validation must not create a native overlay window.");
            File.WriteAllText(Path.Combine(outputDirectory, "control-overlay-validation.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    mode = "offscreen WPF tree only; no desktop capture, input or native window",
                    nativeHitTest = "HTTRANSPARENT", nativeMouseActivate = "MA_NOACTIVATE",
                    wpfVisualHits = "all seven sampled points reach the underlying panel when filtered by IsHitTestVisible",
                    limitations = "No live WPF input routing, desktop, mixed-DPI monitor placement, native style application, or end-to-end OS click-through test.",
                    results,
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS small pointer, visible pulse floor, layout, filtered WPF visual hits, native message contract, and no HWND.");
        }
        finally
        {
            overlay.Close();
            app.Shutdown();
        }
    }

    private static DoubleAnimation FindAnimation(Storyboard storyboard, string target) =>
        storyboard.Children.OfType<DoubleAnimation>().Single(animation => Storyboard.GetTargetName(animation) == target);

    private static double Minimum(DoubleAnimation animation) => Math.Min(animation.From!.Value, animation.To!.Value);
    private static double Maximum(DoubleAnimation animation) => Math.Max(animation.From!.Value, animation.To!.Value);

    private static DependencyObject? HitTestOffscreen(Grid panel, Point point)
    {
        // An unhosted tree has IsVisible=false, so InputHitTest cannot exercise
        // live input routing here. Test visual geometry with WPF's explicit
        // IsHitTestVisible filter instead, without manufacturing a native window.
        Require(VisualTreeHelper.HitTest(panel, point) is not null,
            $"No rendered geometry exists at test point {point}.");
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(panel,
            node => node is UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                hit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            }, new PointHitTestParameters(point));
        return hit;
    }

    private static void VerifyNativeInputMessages(ComputerControlOverlayWindow overlay)
    {
        // Invoke the pure hit-test branches directly without allocating an HWND.
        var procedure = typeof(ComputerControlOverlayWindow).GetMethod("OverlayWindowProc",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("Missing native input hook.");
        foreach (var (message, expected) in new[] { (0x0084, -1), (0x0021, 3) })
        {
            object[] arguments = [IntPtr.Zero, message, IntPtr.Zero, IntPtr.Zero, false];
            var result = (IntPtr)procedure.Invoke(overlay, arguments)!;
            Require(result.ToInt64() == expected && (bool)arguments[4],
                $"Native message 0x{message:X4} must be handled with result {expected}.");
        }
    }

    private static MediaColor PixelAt(byte[] pixels, int x, int y)
    {
        var offset = (y * 1280 + x) * 4;
        Require(pixels[offset + 3] == 255, "Synthetic background must render opaque.");
        return MediaColor.FromRgb(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static double ColorDistance(MediaColor first, MediaColor second) => Math.Sqrt(
        Math.Pow(first.R - second.R, 2) + Math.Pow(first.G - second.G, 2) + Math.Pow(first.B - second.B, 2));

    private static double ContrastRatio(MediaColor first, MediaColor second)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(MediaColor color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void SaveBitmap(BitmapSource bitmap, string output)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(output)) encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
