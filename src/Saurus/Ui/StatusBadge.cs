using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Saurus.Config;
using Saurus.Guardrails;
using Saurus.Interop;

namespace Saurus.Ui;

/// <summary>
/// Small always-on-top badge: proof the app is alive, today's spend at a glance, and a
/// click target for the history window.
///
/// Non-activating like everything else, so it can sit on top of whatever you are reading
/// without ever taking focus. Drag it anywhere; the position is written back to config.json.
/// </summary>
public sealed class StatusBadge : Window
{
    private const double BadgeWidth = 92;
    private const double BadgeHeight = 30;
    private const double ShadowPad = 10;
    private const double WindowWidth = BadgeWidth + ShadowPad * 2;
    private const double WindowHeight = BadgeHeight + ShadowPad * 2;
    private const int EdgeGapPx = 18;

    private static readonly Color Accent = Color.FromRgb(0x5A, 0xA9, 0xFF);
    private static readonly Color Idle = Color.FromRgb(0x6A, 0x70, 0x7C);
    private static readonly Color Warn = Color.FromRgb(0xE0, 0x9B, 0x3D);

    private readonly SaurusConfig _cfg;
    private readonly GuardrailChain _guards;

    private readonly Border _badge;
    private readonly Border _dot;
    private readonly TextBlock _label;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly System.Windows.Threading.DispatcherTimer _refresh;

    private Win32.POINT _dragOrigin;
    private Win32.RECT _dragStartRect;
    private bool _dragging;
    private int _dragDistance;

    /// <summary>Clicked (as opposed to dragged).</summary>
    public event Action? Clicked;

    /// <summary>Quit chosen from the badge's context menu.</summary>
    public event Action? QuitRequested;

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var history = new MenuItem { Header = "History" };
        history.Click += (_, _) => Clicked?.Invoke();
        menu.Items.Add(history);

        var pause = new MenuItem { Header = "Pause" };
        pause.Click += (_, _) =>
        {
            if (_guards.Paused) _guards.Resume(); else _guards.Pause("paused from the badge");
            Refresh();
        };
        menu.Items.Add(pause);

        var autoStart = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = Core.AutoStart.IsEnabled,
            IsEnabled = Core.AutoStart.IsSupported
        };
        autoStart.Click += (_, _) =>
        {
            Core.AutoStart.Toggle();
            autoStart.IsChecked = Core.AutoStart.IsEnabled;
        };
        menu.Items.Add(autoStart);

        menu.Items.Add(new Separator());

        var hide = new MenuItem { Header = "Hide this badge (until restart)" };
        hide.Click += (_, _) => HideBadge();
        menu.Items.Add(hide);

        var quit = new MenuItem { Header = "Quit SAURUS" };
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quit);

        // Menu state is only correct at the moment it opens.
        menu.Opened += (_, _) =>
        {
            pause.Header = _guards.Paused ? "Resume" : "Pause";
            autoStart.IsChecked = Core.AutoStart.IsEnabled;
        };

        return menu;
    }

    public StatusBadge(SaurusConfig cfg, GuardrailChain guards)
    {
        _cfg = cfg;
        _guards = guards;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = WindowWidth;
        Height = WindowHeight;

        _dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(4.5),
            Background = new SolidColorBrush(Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 9, ShadowDepth = 0, Opacity = 0.9, Color = Accent
            }
        };

        _label = new TextBlock
        {
            Text = "0",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0xCB, 0xD6)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _dot, _label }
        };

        _badge = new Border
        {
            Width = BadgeWidth,
            Height = BadgeHeight,
            CornerRadius = new CornerRadius(BadgeHeight / 2),
            Background = new LinearGradientBrush(
                Color.FromArgb(0xF2, 0x22, 0x26, 0x2F),
                Color.FromArgb(0xF2, 0x14, 0x17, 0x1D),
                new Point(0, 0), new Point(0, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x50, 0x58, 0x66)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "SAURUS - click for history, drag to move",
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14, ShadowDepth = 2, Direction = 270,
                Opacity = 0.5, Color = Color.FromRgb(0x04, 0x0A, 0x16)
            },
            Child = row
        };

        Content = new Grid { Children = { _badge } };

        // Right-click makes the badge a complete control surface on its own, so the tray is
        // never the only way to stop the app.
        _badge.ContextMenu = BuildContextMenu();

        _badge.MouseEnter += (_, _) => AnimateScale(1.07);
        _badge.MouseLeave += (_, _) => AnimateScale(1.0);
        _badge.MouseLeftButtonDown += OnDragStart;
        _badge.MouseMove += OnDragMove;
        _badge.MouseLeftButtonUp += OnDragEnd;

        SourceInitialized += (_, _) => Win32.MakeNonActivating(new WindowInteropHelper(this).Handle);

        _refresh = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _refresh.Tick += (_, _) => Refresh();
        _guards.StateChanged += Refresh;
    }

    // ------------------------------------------------------------------ lifecycle

    public void ShowBadge()
    {
        Opacity = 0;
        Show();

        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = Win32.ScaleFor(hwnd);
        var w = (int)Math.Round(WindowWidth * scale);
        var h = (int)Math.Round(WindowHeight * scale);

        int x, y;
        if (_cfg.BadgeX is int cx && _cfg.BadgeY is int cy)
        {
            x = cx;
            y = cy;
        }
        else
        {
            // Default: bottom-right of the monitor containing the cursor at startup.
            Win32.GetCursorPos(out var cursor);
            var work = Win32.WorkAreaForPoint(cursor);
            var gap = (int)Math.Round(EdgeGapPx * scale);
            var inset = (int)Math.Round(ShadowPad * scale);
            x = work.Right - w - gap + inset;
            y = work.Bottom - h - gap + inset;
        }

        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, w, h,
                           Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220)
        });

        Refresh();
        _refresh.Start();
    }

    public void Refresh()
    {
        try
        {
            var u = _guards.Usage();

            _label.Text = _guards.Paused
                ? "paused"
                : _cfg.OfflineMode
                    ? "offline"
                    : $"{Compact(u.DayTotalTokens)}  ${u.DayCostUsd:F2}";

            var pct = _cfg.Limits.DailyTokenBudget > 0
                ? (double)u.DayTotalTokens / _cfg.Limits.DailyTokenBudget
                : 0;

            var colour = _guards.Paused ? Idle
                       : pct >= 0.8 ? Warn
                       : Accent;

            _dot.Background = new SolidColorBrush(colour);
            if (_dot.Effect is DropShadowEffect fx) fx.Color = colour;
        }
        catch (Exception ex)
        {
            Core.Log.Error("badge refresh failed", ex);
        }
    }

    /// <summary>12400 -> "12.4k". Keeps the badge narrow enough to ignore.</summary>
    private static string Compact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M"
        : n >= 1_000 ? $"{n / 1_000.0:F1}k"
        : n.ToString();

    // ------------------------------------------------------------------ drag / click

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Win32.GetCursorPos(out _dragOrigin)) return;
        if (!Win32.GetWindowRect(hwnd, out _dragStartRect)) return;

        _dragging = true;
        _dragDistance = 0;
        _badge.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!Win32.GetCursorPos(out var now)) return;

        var dx = now.X - _dragOrigin.X;
        var dy = now.Y - _dragOrigin.Y;
        _dragDistance = Math.Max(_dragDistance, Math.Abs(dx) + Math.Abs(dy));

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST,
                           _dragStartRect.Left + dx, _dragStartRect.Top + dy, 0, 0,
                           Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _badge.ReleaseMouseCapture();
        e.Handled = true;

        // A press that barely moved is a click, not a drag.
        if (_dragDistance <= 4)
        {
            Clicked?.Invoke();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Win32.GetWindowRect(hwnd, out var r)) return;

        _cfg.BadgeX = r.Left;
        _cfg.BadgeY = r.Top;
        try { _cfg.Save(); }
        catch (Exception ex) { Core.Log.Error("could not save badge position", ex); }
    }

    private void AnimateScale(double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(130),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    /// <summary>
    /// Bounds of the visible badge in physical pixels, excluding the transparent shadow
    /// margin. The panel grows out of this rectangle, so it has to be the badge you can
    /// actually see, not the window around it.
    /// </summary>
    internal Win32.RECT? VisibleBounds
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return null;

            var inset = (int)Math.Round(ShadowPad * Win32.ScaleFor(hwnd));
            return new Win32.RECT
            {
                Left = r.Left + inset,
                Top = r.Top + inset,
                Right = r.Right - inset,
                Bottom = r.Bottom - inset
            };
        }
    }

    /// <summary>
    /// Hides the badge while the panel is standing in for it. Instant rather than faded:
    /// the panel is already covering this spot by the first frame, so a fade would only
    /// show through as a flicker underneath.
    /// </summary>
    public void Conceal()
    {
        if (IsVisible) Hide();
    }

    public void Reveal()
    {
        if (IsVisible) return;
        Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160)
        });
        Refresh();
    }

    /// <summary>Physical-pixel bounds of the visible badge, for the click-outside test.</summary>
    public bool ContainsPoint(int x, int y)
    {
        if (!IsVisible) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return false;
        return Win32.RectContains(r, x, y);
    }

    public void HideBadge()
    {
        _refresh.Stop();
        Hide();
    }
}
