using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Saurus.Interop;

namespace Saurus.Ui;

/// <summary>
/// The small affordance that appears next to the cursor after a selection.
///
/// Never takes focus: WS_EX_NOACTIVATE plus ShowActivated=false. If it activated, the
/// foreground app would lose its selection and the capture we already performed would be
/// the last one we could ever get.
///
/// The window is deliberately larger than the visible pill. The extra margin is dead space
/// for the drop shadow and the grow-on-hover transform, which would otherwise be clipped at
/// the window edge.
/// </summary>
public sealed class PillWindow : Window
{
    private const double PillWidth = 40;
    private const double PillHeight = 30;
    private const double ShadowPad = 10;                       // room for shadow + hover scale
    private const double WindowWidth = PillWidth + ShadowPad * 2;
    private const double WindowHeight = PillHeight + ShadowPad * 2;
    private const int CursorOffsetPx = 12;

    private static readonly Color Accent = Color.FromRgb(0x5A, 0xA9, 0xFF);
    private static readonly Color AccentDeep = Color.FromRgb(0x2F, 0x6F, 0xD6);

    private readonly Border _pill;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DropShadowEffect _glow;

    private DispatcherTimerLite? _autoHide;
    private int _timeoutMs = 6000;

    /// <summary>Raised when the user clicks the pill.</summary>
    public event Action? Fired;

    public PillWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Width = WindowWidth;
        Height = WindowHeight;

        _glow = new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.55,
            Color = Color.FromRgb(0x04, 0x0A, 0x16)
        };

        _pill = new Border
        {
            Width = PillWidth,
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),   // full capsule
            Background = new LinearGradientBrush(
                Color.FromRgb(0x25, 0x2A, 0x34), Color.FromRgb(0x16, 0x19, 0x20),
                new Point(0, 0), new Point(0, 1)),
            BorderBrush = new LinearGradientBrush(
                Color.FromArgb(0xCC, Accent.R, Accent.G, Accent.B),
                Color.FromArgb(0x55, AccentDeep.R, AccentDeep.G, AccentDeep.B),
                new Point(0, 0), new Point(0, 1)),
            BorderThickness = new Thickness(1.2),
            Effect = _glow,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            Child = BuildGlyph()
        };

        Content = new Grid { Children = { _pill } };

        // Both plain click and ctrl+click fire. There is no hover trigger: the pill appears
        // right where the cursor already is, so hover-to-fire would misfire constantly.
        // Hover only changes how it looks.
        // Hovering must keep the pill alive. Previously the auto-hide countdown kept running
        // while the cursor sat on it, so reading the pill for a couple of seconds and then
        // moving away made it vanish immediately afterwards, which reads as the hover having
        // dismissed it. The timer is now suspended on enter and restarted from zero on leave.
        _pill.MouseEnter += (_, _) =>
        {
            StopTimer();
            CancelFade();
            AnimateHover(true);
        };

        _pill.MouseLeave += (_, _) =>
        {
            AnimateHover(false);
            RestartTimer();
        };
        _pill.MouseLeftButtonDown += (_, _) => AnimateScale(0.92, 60);
        _pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; Fire(); };

        SourceInitialized += (_, _) => Win32.MakeNonActivating(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// The mark: a soft accent-filled lozenge with a inset highlight, rather than a letter.
    /// A glyph reads as an icon at 30px where a character reads as noise.
    /// </summary>
    private static UIElement BuildGlyph()
    {
        var dot = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(4.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(
                Color.FromRgb(0x8C, 0xC6, 0xFF), AccentDeep,
                new Point(0, 0), new Point(1, 1)),
            Effect = new DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 0, Opacity = 0.85, Color = Accent
            }
        };

        // A short diagonal cut across the lozenge, so it reads as a mark and not a button.
        var slash = new Border
        {
            Width = 2.2,
            Height = 9,
            CornerRadius = new CornerRadius(1.1),
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x0E, 0x14, 0x1E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(28)
        };

        return new Grid { Children = { dot, slash } };
    }

    // ------------------------------------------------------------------ animation

    private void AnimateHover(bool on)
    {
        AnimateScale(on ? 1.12 : 1.0, 120);

        _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation
        {
            To = on ? 22 : 14,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        _glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation
        {
            To = on ? Accent : Color.FromRgb(0x04, 0x0A, 0x16),
            Duration = TimeSpan.FromMilliseconds(140)
        });
    }

    private void AnimateScale(double to, int ms)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>Grows in from slightly small. Fast enough not to feel like a delay.</summary>
    private void AnimateIn()
    {
        _scale.ScaleX = _scale.ScaleY = 0.6;
        AnimateScale(1.0, 170);

        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(110)
        });
    }

    private void Fire()
    {
        StopTimer();
        Hide();
        Fired?.Invoke();
    }

    /// <summary>
    /// Shows the pill next to the cursor, clamped to the work area of the monitor the
    /// cursor is on. Coordinates are physical pixels throughout.
    /// </summary>
    public void ShowAt(int cursorX, int cursorY, int timeoutMs)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = Win32.ScaleFor(hwnd);
        var w = (int)Math.Round(WindowWidth * scale);
        var h = (int)Math.Round(WindowHeight * scale);

        var work = Win32.WorkAreaForPoint(new Win32.POINT { X = cursorX, Y = cursorY });
        var offset = (int)Math.Round(CursorOffsetPx * scale);

        // The visible pill is inset by Margin, so shift by that much to keep the *pill*,
        // not the invisible window, at the intended distance from the cursor.
        var inset = (int)Math.Round(ShadowPad * scale);
        var x = cursorX + offset - inset;
        var y = cursorY + offset - inset;

        // Clamp to the work area; flip to the other side of the cursor if we would overhang.
        if (x + w > work.Right) x = cursorX - offset - w + inset;
        if (y + h > work.Bottom) y = cursorY - offset - h + inset;
        x = Math.Max(work.Left, Math.Min(x, work.Right - w));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));

        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, w, h,
                           Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        AnimateIn();

        _timeoutMs = timeoutMs;
        RestartTimer();
    }

    /// <summary>(Re)starts the auto-hide countdown from zero.</summary>
    private void RestartTimer()
    {
        StopTimer();
        if (_timeoutMs > 0)
            _autoHide = DispatcherTimerLite.Once(TimeSpan.FromMilliseconds(_timeoutMs), FadeOut);
    }

    /// <summary>Aborts an in-flight fade and snaps back to fully visible.</summary>
    private void CancelFade()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    /// <summary>Times out gently rather than vanishing, so it does not read as a glitch.</summary>
    private void FadeOut()
    {
        if (!IsVisible) return;

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => { if (Opacity <= 0.02) Hide(); };
        BeginAnimation(OpacityProperty, fade);
    }

    public void Dismiss()
    {
        StopTimer();
        if (!IsVisible) return;
        BeginAnimation(OpacityProperty, null);   // drop any in-flight fade
        Hide();
    }

    private void StopTimer()
    {
        _autoHide?.Cancel();
        _autoHide = null;
    }

    /// <summary>
    /// Physical-pixel bounds of the *visible pill*, for the click-outside test. The window
    /// is larger than the pill, so testing the window rect would swallow clicks that landed
    /// in the transparent margin.
    /// </summary>
    public bool ContainsPoint(int x, int y)
    {
        if (!IsVisible) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;

        var inset = (int)Math.Round(ShadowPad * Win32.ScaleFor(hwnd));
        var pill = new Win32.RECT
        {
            Left = r.Left + inset,
            Top = r.Top + inset,
            Right = r.Right - inset,
            Bottom = r.Bottom - inset
        };
        return Win32.RectContains(pill, x, y);
    }
}

/// <summary>One-shot dispatcher timer that can be cancelled without leaking a handler.</summary>
internal sealed class DispatcherTimerLite
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _cancelled;

    private DispatcherTimerLite(TimeSpan delay, Action action)
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!_cancelled) action();
        };
        _timer.Start();
    }

    public static DispatcherTimerLite Once(TimeSpan delay, Action action) => new(delay, action);

    public void Cancel()
    {
        _cancelled = true;
        _timer.Stop();
    }
}
