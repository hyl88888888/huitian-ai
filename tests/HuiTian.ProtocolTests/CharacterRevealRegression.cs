using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HuiTian.App;

internal static class CharacterRevealRegression
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Render(); } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static void Render()
    {
        var root = @"D:\HuiTian\Cache\Tests\character-reveal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        Directory.CreateDirectory(root);
        Check(new CharacterOpeningOptions().IsValid(), "default full-face options must be valid");
        Check(!new CharacterOpeningOptions { ImageFileName = "../outside.png" }.IsValid(), "asset paths cannot escape the appearance folder");
        Check(!new CharacterOpeningOptions { EyeAnchorX = double.NaN }.IsValid(), "nonfinite eye anchor must be rejected");
        Check(!new CharacterOpeningOptions { DurationSeconds = 100 }.IsValid(), "animation configuration must be bounded");
        var invalidConfig = Path.Combine(root, "invalid-options.json");
        File.WriteAllText(invalidConfig, "{broken");
        Check(CharacterOpeningOptions.Load(invalidConfig).IsValid(), "corrupt optional configuration must fall back without blocking startup");
        const int width = 128, height = 192;
        var pixels = new byte[width * height * 4];
        for (var y = 8; y < height - 8; y++)
        for (var x = 8; x < width - 8; x++)
        {
            var dx = (x - width / 2d) / 48;
            var dy = (y - height / 2d) / 85;
            if (dx * dx + dy * dy > 1) continue;
            var at = (y * width + x) * 4;
            pixels[at] = (byte)(x % 6 < 3 ? 220 : 35);
            pixels[at + 1] = 170;
            pixels[at + 2] = 80;
            pixels[at + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var input = Path.Combine(root, "synthetic-fixture.png");
        Write(input, bitmap);
        var control = new CharacterRevealView { Width = 220, Height = 330 };
        Check(!control.LoadPortrait(Path.Combine(root, "missing.png")), "missing assets must not be accepted");
        Check(control.LoadPortrait(input), "valid fixture must load");
        Check(control.OutlineSegmentCount > 100 && control.DetailSegmentCount > 0, "strokes must derive from alpha and image edges");
        Check(!control.IsHitTestVisible, "visual cannot steal pointer input");
        control.Measure(new Size(220, 330));
        control.Arrange(new Rect(0, 0, 220, 330));
        var hashes = new HashSet<string>();
        foreach (var progress in new[] { 0d, .25, .65, 1d })
        {
            control.RevealProgress = progress;
            control.UpdateLayout();
            var target = new RenderTargetBitmap(220, 330, 96, 96, PixelFormats.Pbgra32);
            target.Render(control);
            var file = Path.Combine(root, $"synthetic-phase-{progress:0.00}.png");
            Write(file, target);
            hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }
        Check(hashes.Count == 4, "outline/detail/material/completion frames must differ");
        control.RevealProgress = .2;
        control.CompleteReveal();
        Check(control.RevealProgress == 1, "service readiness must finish the visual immediately");
        var finalFrame = new RenderTargetBitmap(220, 330, 96, 96, PixelFormats.Pbgra32);
        finalFrame.Render(control);
        var finalPixels = new byte[220 * 330 * 4];
        finalFrame.CopyPixels(finalPixels, 220 * 4, 0);
        control.RevealProgress = .765;
        control.UpdateLayout();
        var heldFrame = new RenderTargetBitmap(220, 330, 96, 96, PixelFormats.Pbgra32);
        heldFrame.Render(control);
        var heldPixels = new byte[finalPixels.Length];
        heldFrame.CopyPixels(heldPixels, 220 * 4, 0);
        Check(finalPixels.SequenceEqual(heldPixels), "the complete master must hold unchanged from 7.65 s through the end");
        control.RevealProgress = 1;
        control.InitialFramingScale = 3.2;
        control.EyeAnchorX = .7285;
        control.EyeAnchorY = .515;
        control.InvalidateVisual();
        control.UpdateLayout();
        var alternateFinalFrame = new RenderTargetBitmap(220, 330, 96, 96, PixelFormats.Pbgra32);
        alternateFinalFrame.Render(control);
        var alternatePixels = new byte[finalPixels.Length];
        alternateFinalFrame.CopyPixels(alternatePixels, 220 * 4, 0);
        Check(finalPixels.SequenceEqual(alternatePixels), "different eye close-ups must return to the same complete master image");
        var reduced = new CharacterRevealView();
        reduced.StartReveal(reducedMotion: true);
        Check(reduced.RevealProgress == 1, "reduced motion must skip animation");
        Console.WriteLine("PASS character rendering: bounded options, local assets, alpha/detail strokes, four WPF phases, same-master camera endpoints, early completion, reduced motion, pointer passthrough");
        Console.WriteLine("SYNTHETIC TECHNICAL FRAMES ONLY (not approved character or desktop screenshots): " + root);
    }

    private static void Write(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
