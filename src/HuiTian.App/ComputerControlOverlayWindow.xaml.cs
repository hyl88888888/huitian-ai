using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace HuiTian.App;

public partial class ComputerControlOverlayWindow : Window
{
    private const double PointerHotspotX = 1.5;
    private const double PointerHotspotY = 1.5;
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private HwndSource? _windowSource;
    private CancellationTokenSource? _hideDelay;
    private string _language = "zh-CN";
    private double? _lastPointerLeft;
    private double? _lastPointerTop;
    private long _pointerGeneration;
    private NativeRectangle? _monitorBounds;
    private bool _positioning;

    public ComputerControlOverlayWindow()
    {
        InitializeComponent();
        // Initial WPF dimensions are only a fallback. Native monitor bounds below
        // are physical pixels, including monitors left/above the primary display.
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        SourceInitialized += Overlay_OnSourceInitialized;
        Closed += Overlay_OnClosed;
    }

    public void ShowControl(string? detail = null)
    {
        _hideDelay?.Cancel();
        _hideDelay?.Dispose();
        _hideDelay = null;
        // Pointer events call ShowControl again. Keep the active app-specific
        // status instead of replacing it with a generic message on every move.
        if (!string.IsNullOrWhiteSpace(detail))
            ControlMessage.Text = detail;
        else if (!IsVisible)
            ControlMessage.Text = UiLocalizer.Translate(_language, "回天正在操作电脑");
        if (!IsVisible)
        {
            ResetPointer();
            SelectMonitor(MonitorFromWindow(GetForegroundWindow(), 2));
            Show();
            PositionOnMonitor();
            ((Storyboard)Resources["BorderBreathing"]).Begin(this, true);
        }
        else if (!string.IsNullOrWhiteSpace(detail))
        {
            // A new app operation can move to another monitor without any
            // pointer event (for example, a keyboard shortcut or app launch).
            // Bare ShowControl() calls from ShowPointer keep their point-based
            // monitor selection below instead of snapping back to foreground.
            SelectMonitor(MonitorFromWindow(GetForegroundWindow(), 2));
            PositionOnMonitor();
        }

        Topmost = true;
    }

    public void HideControl()
    {
        _hideDelay?.Cancel();
        _hideDelay?.Dispose();
        _hideDelay = null;
        ((Storyboard)Resources["BorderBreathing"]).Remove(this);
        ResetPointer();
        if (IsVisible)
        {
            Hide();
        }
    }

    public async void RequestHide()
    {
        _hideDelay?.Cancel();
        _hideDelay?.Dispose();
        _hideDelay = new CancellationTokenSource();
        var token = _hideDelay.Token;
        try
        {
            // A completed action must not appear to keep operating for 5–12 seconds.
            await Task.Delay(TimeSpan.FromMilliseconds(260), token);
            if (!token.IsCancellationRequested && IsVisible)
            {
                HideControl();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ShowPointer(int screenX, int screenY, bool isClick = false)
    {
        ShowControl();
        SelectMonitor(MonitorFromPoint(new NativePoint(screenX, screenY), 2));
        PositionOnMonitor();
        var localPoint = PointFromScreen(new System.Windows.Point(screenX, screenY));
        var left = Math.Clamp(
            localPoint.X - PointerHotspotX,
            -PointerHotspotX,
            Math.Max(-PointerHotspotX, ActualWidth - PointerHotspotX));
        var top = Math.Clamp(
            localPoint.Y - PointerHotspotY,
            -PointerHotspotY,
            Math.Max(-PointerHotspotY, ActualHeight - PointerHotspotY));
        // Start at the currently displayed point, not the previous target: another
        // real action can arrive before the short visual transition has finished.
        var previousLeft = System.Windows.Controls.Canvas.GetLeft(VirtualPointer);
        var previousTop = System.Windows.Controls.Canvas.GetTop(VirtualPointer);
        var hadPointer = _lastPointerLeft.HasValue && _lastPointerTop.HasValue;
        var generation = ++_pointerGeneration;
        VirtualPointer.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
        VirtualPointer.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, null);
        VirtualPointer.BeginAnimation(OpacityProperty, null);
        VirtualPointer.Opacity = 1;
        System.Windows.Controls.Canvas.SetLeft(VirtualPointer, left);
        System.Windows.Controls.Canvas.SetTop(VirtualPointer, top);
        if (hadPointer && double.IsFinite(previousLeft) && double.IsFinite(previousTop))
        {
            var distance = Math.Sqrt(
                Math.Pow(left - previousLeft, 2) +
                Math.Pow(top - previousTop, 2));
            var duration = TimeSpan.FromMilliseconds(Math.Clamp(55 + distance * 0.075, 55, 180));
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var horizontal = new DoubleAnimation(previousLeft, left, duration)
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            };
            horizontal.Completed += (_, _) =>
            {
                if (generation == _pointerGeneration && IsVisible && isClick)
                    ((Storyboard)Resources["PointerPulse"]).Begin(this, true);
            };
            VirtualPointer.BeginAnimation(
                System.Windows.Controls.Canvas.LeftProperty,
                horizontal);
            VirtualPointer.BeginAnimation(
                System.Windows.Controls.Canvas.TopProperty,
                new DoubleAnimation(previousTop, top, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                });
        }
        else if (isClick)
        {
            ((Storyboard)Resources["PointerPulse"]).Begin(this, true);
        }

        _lastPointerLeft = left;
        _lastPointerTop = top;
        VirtualPointer.Visibility = Visibility.Visible;
        // Do not leave an old target looking like the current cursor while the
        // model waits/thinks. Only another reported action makes it reappear.
        VirtualPointer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            BeginTime = TimeSpan.FromSeconds(1.5),
        });
    }

    private void ResetPointer()
    {
        _pointerGeneration++;
        _lastPointerLeft = null;
        _lastPointerTop = null;
        ((Storyboard)Resources["PointerPulse"]).Remove(this);
        VirtualPointer.BeginAnimation(System.Windows.Controls.Canvas.LeftProperty, null);
        VirtualPointer.BeginAnimation(System.Windows.Controls.Canvas.TopProperty, null);
        VirtualPointer.BeginAnimation(OpacityProperty, null);
        VirtualPointer.Visibility = Visibility.Collapsed;
    }

    private void SelectMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return;
        if (_monitorBounds is { } old && old.Equals(info.Monitor)) return;
        _monitorBounds = info.Monitor;
        ResetPointer();
    }

    private void PositionOnMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (_positioning || handle == IntPtr.Zero || _monitorBounds is not { } bounds) return;
        _positioning = true;
        try
        {
            // SetWindowPos uses physical screen coordinates, unlike WPF Left/Top
            // on mixed-DPI desktops. Cover the active monitor, not the enclosing
            // virtual rectangle whose edges may lie between disconnected screens.
            SetWindowPos(handle, new IntPtr(-1), bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0010);
        }
        finally { _positioning = false; }
    }

    public void ApplyLanguage(string language)
    {
        _language = language;
        ControlMessage.Text = UiLocalizer.Translate(language, "回天正在操作电脑");
    }

    private void Overlay_OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));

        // WS_EX_TRANSPARENT is not sufficient for every layered-window path.
        // Returning HTTRANSPARENT at the native hit-test boundary guarantees
        // that the border, banner and virtual pointer remain presentation-only.
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OverlayWindowProc);
        PositionOnMonitor();
    }

    private void Overlay_OnClosed(object? sender, EventArgs e)
    {
        _hideDelay?.Cancel();
        _hideDelay?.Dispose();
        _hideDelay = null;
        _windowSource?.RemoveHook(OverlayWindowProc);
        _windowSource = null;
    }

    private IntPtr OverlayWindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        if (message is WmDpiChanged or WmDisplayChange && !_positioning)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SelectMonitor(MonitorFromWindow(hwnd, 2));
                PositionOnMonitor();
            });
        }

        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, value);
}
