using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Saurus.Interop;

namespace Saurus.Ui;

/// <summary>
/// Full-monitor dim behind the side panel, snipping-tool style.
///
/// Deliberately NOT a WPF AllowsTransparency window. Per-pixel transparency forces WPF to
/// software-render that window; at full-screen size the fade visibly stutters and costs real
/// frame time. Instead this is an opaque black window made layered via WS_EX_LAYERED, with
/// its alpha driven through SetLayeredWindowAttributes — DWM composites that on the GPU for
/// nothing.
///
/// It covers the whole monitor including the taskbar. Dimming only the work area leaves a
/// bright strip along the bottom that ruins the effect.
///
/// It also swallows clicks, which is how click-outside dismissal works in panel mode: no
/// hit-testing against window rectangles, just a click handler on the backdrop.
/// </summary>
public sealed class DimOverlay : Window
{
    private readonly DispatcherTimer _ticker;
    private double _current;
    private double _target;
    private double _peak = 0.38;
    private int _slideMs = 190;

    /// <summary>The user clicked the dimmed area.</summary>
    public event Action? Clicked;

    public DimOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;          // see class comment
        Background = Brushes.Black;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Opacity = 1;                         // real opacity comes from the layered alpha

        MouseLeftButtonDown += (_, _) => Clicked?.Invoke();
        MouseRightButtonDown += (_, _) => Clicked?.Invoke();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeNonActivating(hwnd);
            Win32.MakeLayered(hwnd);
            Win32.SetLayeredOpacity(hwnd, 0);
        };

        // ~60fps. Only runs during a transition; stopped the rest of the time so an idle
        // popup costs nothing.
        _ticker = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _ticker.Tick += (_, _) => Step();
    }

    public void Configure(double dimOpacity, int slideMs)
    {
        _peak = Math.Clamp(dimOpacity, 0, 0.9);
        _slideMs = Math.Max(0, slideMs);
    }

    /// <summary>Covers the monitor containing the given point and fades up.</summary>
    public void FadeIn(int atX, int atY)
    {
        var rect = Win32.MonitorRectForPoint(new Win32.POINT { X = atX, Y = atY });

        if (!IsVisible) Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST,
                           rect.Left, rect.Top,
                           rect.Right - rect.Left, rect.Bottom - rect.Top,
                           Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        _target = _peak;
        _ticker.Start();
    }

    public void FadeOut()
    {
        if (!IsVisible) return;
        _target = 0;
        _ticker.Start();
    }

    /// <summary>Immediate teardown, no animation. Used on shutdown.</summary>
    public void HideNow()
    {
        _ticker.Stop();
        _current = _target = 0;
        if (IsVisible) Hide();
    }

    private void Step()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) { _ticker.Stop(); return; }

        // Linear over the configured duration; the panel's own ease carries the motion, and
        // a dim that eases differently from the thing it backs reads as two separate events.
        var stepSize = _slideMs <= 0 ? 1.0 : 16.0 / _slideMs * _peak;
        var delta = _target - _current;

        if (Math.Abs(delta) <= stepSize)
        {
            _current = _target;
            _ticker.Stop();
            Win32.SetLayeredOpacity(hwnd, _current);
            if (_current <= 0.001) Hide();
            return;
        }

        _current += Math.Sign(delta) * stepSize;
        Win32.SetLayeredOpacity(hwnd, _current);
    }
}
