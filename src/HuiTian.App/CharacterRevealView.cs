using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace HuiTian.App;

/// <summary>
/// Replaceable local portrait: real alpha silhouette, image-derived detail
/// strokes, then a spatial material reveal. Visual time never signals readiness.
/// No facial motion or speech animation is synthesized by this component.
/// </summary>
public sealed class CharacterRevealView : FrameworkElement
{
    public static readonly DependencyProperty RevealProgressProperty = DependencyProperty.Register(
        nameof(RevealProgress), typeof(double), typeof(CharacterRevealView),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private BitmapSource? _portrait;
    private readonly List<(Point A, Point B)> _outline = [];
    private readonly List<(Point A, Point B)> _details = [];
    private bool _finished;
    private bool _reducedMotion;
    public double RevealProgress { get => (double)GetValue(RevealProgressProperty); set => SetValue(RevealProgressProperty, value); }
    public bool HasPortrait => _portrait is not null;
    public double NaturalAspectRatio => _portrait is null ? 1 : (double)_portrait.PixelWidth / _portrait.PixelHeight;
    public int OutlineSegmentCount => _outline.Count;
    public int DetailSegmentCount => _details.Count;
    // For face-close-up artwork, pull back gently around the upper face.
    // This is framing of a static image, not a facial animation rig.
    public double InitialFramingScale { get; set; } = 1.18;
    public double EyeAnchorX { get; set; } = .5;
    public double EyeAnchorY { get; set; } = .35;
    public int DecodeWidth { get; set; } = 384;
    public double DurationSeconds { get; set; } = 3.2;

    public CharacterRevealView()
    {
        IsHitTestVisible = false;
        Unloaded += (_, _) => CompleteReveal();
    }

    public bool LoadPortrait(string path)
    {
        _portrait = null;
        _outline.Clear();
        _details.Clear();
        InvalidateVisual();
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = Math.Clamp(DecodeWidth, 128, 2048);
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            // Bound processing for accidentally supplied extreme aspect ratios.
            if (bitmap.PixelHeight > 2048 || bitmap.PixelHeight < 32) return false;
            _portrait = bitmap;
            BuildStrokes(bitmap);
            InvalidateVisual();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return false;
        }
    }

    public void StartReveal(bool reducedMotion = false)
    {
        _reducedMotion = reducedMotion || !SystemParameters.ClientAreaAnimation;
        if (_finished || _reducedMotion) { CompleteReveal(); return; }
        BeginAnimation(RevealProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(Math.Clamp(DurationSeconds, 1, 12)))
        {
            FillBehavior = FillBehavior.HoldEnd
        });
    }

    public void CompleteReveal()
    {
        _finished = true;
        BeginAnimation(RevealProgressProperty, null);
        RevealProgress = 1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_portrait is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / _portrait.PixelWidth, ActualHeight / _portrait.PixelHeight);
        var x = (ActualWidth - _portrait.PixelWidth * scale) / 2;
        var y = ActualHeight - _portrait.PixelHeight * scale;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var progress = Math.Clamp(RevealProgress, 0, 1);
        // Use one full-face master for every frame. Start on its configured eyes
        // and continuously move the camera center back to the whole image.
        // Preview timing: close-up until 3.65 s, pull back through 7.65 s,
        // then hold the complete master until 10 s. Readiness still ends early.
        var camera = Math.Clamp((progress - .365) / .4, 0, 1);
        camera = camera < .5 ? 4 * camera * camera * camera : 1 - Math.Pow(-2 * camera + 2, 3) / 2;
        var zoom = 1 + (Math.Clamp(InitialFramingScale, 1, 5) - 1) * (1 - camera);
        var centerX = _portrait.PixelWidth * (EyeAnchorX * (1 - camera) + .5 * camera);
        var centerY = _portrait.PixelHeight * (EyeAnchorY * (1 - camera) + .5 * camera);
        dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom,
            _portrait.PixelWidth / 2d - zoom * centerX, _portrait.PixelHeight / 2d - zoom * centerY));
        var raster = Math.Clamp((progress - .2) / .565, 0, 1);
        if (raster > 0)
        {
            // Reveal spatially outwards from the eyes, not by whole-image opacity.
            var radius = Math.Sqrt((double)_portrait.PixelWidth * _portrait.PixelWidth + (double)_portrait.PixelHeight * _portrait.PixelHeight) * raster;
            dc.PushClip(new EllipseGeometry(new Point(_portrait.PixelWidth * EyeAnchorX, _portrait.PixelHeight * EyeAnchorY), radius, radius));
            dc.DrawImage(_portrait, new Rect(0, 0, _portrait.PixelWidth, _portrait.PixelHeight));
            dc.Pop();
        }
        if (progress < 1)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(210 * (1 - raster)), 105, 224, 237)), 1.1 / scale);
            var outlineCount = (int)(_outline.Count * Math.Clamp(progress / .4, 0, 1));
            for (var i = 0; i < outlineCount; i++) dc.DrawLine(pen, _outline[i].A, _outline[i].B);
            var detailCount = (int)(_details.Count * Math.Clamp((progress - .2) / .4, 0, 1));
            for (var i = 0; i < detailCount; i++) dc.DrawLine(pen, _details[i].A, _details[i].B);
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void BuildStrokes(BitmapSource bitmap)
    {
        var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = rgba.PixelWidth;
        var height = rgba.PixelHeight;
        var pixels = new byte[width * height * 4];
        rgba.CopyPixels(pixels, width * 4, 0);
        var hasTransparency = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 96) { hasTransparency = true; break; }
        Point? previousLeft = null, previousRight = null;
        for (var y = 0; y < height; y += 3)
        {
            var left = -1;
            var right = -1;
            for (var x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 96) { if (left < 0) left = x; right = x; }
            if (left < 0) { previousLeft = previousRight = null; continue; }
            var l = new Point(left, y);
            var r = new Point(right, y);
            if (previousLeft is { } pl) _outline.Add((pl, l));
            if (previousRight is { } pr) _outline.Add((pr, r));
            previousLeft = l;
            previousRight = r;
            // Short actual image edges add face/hair/material detail after the
            // silhouette. These are image-derived marks, not a rigged face.
            for (var x = left + 2; x < right - 3; x += 3)
            {
                var at = (y * width + x) * 4;
                if (pixels[at + 3] < 192 || pixels[at + 15] < 192) continue;
                var delta = Math.Abs(pixels[at] - pixels[at + 12]) + Math.Abs(pixels[at + 1] - pixels[at + 13]) + Math.Abs(pixels[at + 2] - pixels[at + 14]);
                if (delta > 95 && _details.Count < 4000) _details.Add((new Point(x, y), new Point(x + 2, y + 1)));
            }
        }
        // RGB artwork has no alpha silhouette. Never trace the rectangular
        // bitmap border and call it the character outline. Fall back to actual
        // image edges; precise facial paths require artwork-specific authoring.
        if (!hasTransparency)
        {
            _outline.Clear();
            _outline.AddRange(_details);
        }
        // The eye region is drawn first instead of scanning the entire image
        // from its top border. Exact facial contours still need visual tuning.
        double Distance((Point A, Point B) line) =>
            Math.Pow(line.A.X / width - EyeAnchorX, 2) + Math.Pow(line.A.Y / height - EyeAnchorY, 2);
        _outline.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        _details.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
    }
}
