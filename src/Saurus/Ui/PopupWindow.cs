using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Saurus.Interop;

namespace Saurus.Ui;

/// <summary>
/// The answer popup. Borderless, anchored near the cursor, clamped to the monitor work area.
///
/// Focus policy: the window is created with WS_EX_NOACTIVATE so showing it cannot disturb
/// the foreground app. That flag is dropped — and only then — when the user clicks into the
/// follow-up box, because a non-activating window cannot receive keyboard input. By that
/// point capture is long finished, so taking focus is harmless. The invariant that matters
/// is "no focus change before capture", not "never".
/// </summary>
public sealed class PopupWindow : Window, IAnswerSurface
{
    private const double PopupWidth = 460;
    private const double MaxPopupHeight = 420;
    private const int CursorOffsetPx = 18;

    private readonly RichTextBox _answer;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly Button _expand;
    private readonly ProgressBar _spinner;

    private readonly StringBuilder _buffer = new();
    private readonly DispatcherTimer _renderTimer;
    private bool _dirty;
    private bool _pendingFollowUp;
    private bool _hasFocusRights;

    public event Action<string>? FollowUpSubmitted;
    public event Action? ExpandRequested;
    public event Action? Dismissed;

    /// <summary>Full markdown received so far.</summary>
    public string CurrentText => _buffer.ToString();

    public PopupWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = PopupWidth;
        SizeToContent = SizeToContent.Manual;
        Height = 200;

        // ---- layout ---------------------------------------------------------
        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // input

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetRow(_status, 0);
        grid.Children.Add(_status);

        _spinner = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 2,
            Margin = new Thickness(0, 0, 0, 6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF)),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_spinner, 1);
        grid.Children.Add(_spinner);

        _answer = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = MarkdownRenderer.BodyFg,
            Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = MarkdownRenderer.Render("")
        };
        Grid.SetRow(_answer, 1);
        grid.Children.Add(_answer);

        var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _input = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x29, 0x30)),
            Foreground = MarkdownRenderer.BodyFg,
            CaretBrush = MarkdownRenderer.BodyFg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 5, 7, 5),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5
        };
        Grid.SetColumn(_input, 0);
        inputRow.Children.Add(_input);

        // Watermark. A TextBlock layered over the TextBox rather than placeholder text in
        // the box itself, so an accidental Enter never submits the prompt text.
        var watermark = new TextBlock
        {
            Text = "Ask a follow-up…",
            Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x70, 0x7C)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetColumn(watermark, 0);
        inputRow.Children.Add(watermark);

        _input.TextChanged += (_, _) =>
            watermark.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _expand = new Button
        {
            Content = "Expand",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(9, 5, 9, 5),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCE, 0xD8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4A)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            IsEnabled = false
        };
        Grid.SetColumn(_expand, 1);
        inputRow.Children.Add(_expand);

        Grid.SetRow(inputRow, 2);
        grid.Children.Add(inputRow);

        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x21)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x42)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5,
                Color = Colors.Black, Direction = 270
            },
            Child = grid
        };

        // ---- behaviour ------------------------------------------------------

        // Clicking the input is the moment we are allowed to take focus.
        _input.PreviewMouseLeftButtonDown += (_, _) => AcquireFocus();
        _expand.Click += (_, _) => { AcquireFocus(); ExpandRequested?.Invoke(); };

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_input.Text))
            {
                e.Handled = true;
                var q = _input.Text.Trim();
                _input.Clear();
                FollowUpSubmitted?.Invoke(q);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
        };

        // Once focused, normal key handling works; before that the global hook covers Esc.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        };

        SourceInitialized += (_, _) => Win32.MakeNonActivating(new WindowInteropHelper(this).Handle);

        // Streaming re-renders the whole document, so it is throttled rather than run per
        // token — parsing markdown 40 times a second would stall the UI thread.
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _renderTimer.Tick += (_, _) => FlushRender();
    }

    // ------------------------------------------------------------------ focus

    /// <summary>
    /// Drops WS_EX_NOACTIVATE and pulls focus. Only ever called from a user gesture on the
    /// input controls, i.e. long after capture completed.
    /// </summary>
    private void AcquireFocus()
    {
        if (_hasFocusRights) return;
        _hasFocusRights = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeActivating(hwnd);
        Win32.SetForegroundWindow(hwnd);
        Activate();
        _input.Focus();
        Keyboard.Focus(_input);
    }

    // ------------------------------------------------------------------ content

    /// <summary>
    /// The anchored popup has no room for a heading, so the subject is folded into the
    /// status line rather than dropped.
    /// </summary>
    /// <summary>
    /// The anchored popup keeps a running transcript too, so a follow-up reads as a
    /// conversation rather than replacing the answer you asked about.
    /// </summary>
    public void BeginFollowUp(string question, string statusLine)
    {
        if (_buffer.Length > 0) _buffer.Append("\n\n---\n\n**").Append(question.Trim()).Append("**\n\n");
        _pendingFollowUp = true;
        _dirty = true;
        _status.Text = statusLine;
        _spinner.Visibility = Visibility.Visible;
        _expand.IsEnabled = false;
        _renderTimer.Start();
        FlushRender();
    }

    public void BeginQuery(string subject, string source, string statusLine)
    {
        _buffer.Clear();
        _pendingFollowUp = false;
        _dirty = true;
        _status.Text = string.IsNullOrWhiteSpace(source) ? statusLine : $"{source} · {statusLine}";
        _spinner.Visibility = Visibility.Visible;
        _expand.IsEnabled = false;
        _answer.Document = MarkdownRenderer.Render("");
        _renderTimer.Start();
    }

    /// <summary>Appends a streamed chunk. Cheap: the actual re-render is throttled.</summary>
    public void AppendDelta(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _buffer.Append(chunk);
        _dirty = true;
        if (_spinner.Visibility == Visibility.Visible) _spinner.Visibility = Visibility.Collapsed;
    }

    /// <summary>Replaces the whole body — used for cache hits, which arrive complete.</summary>
    public void SetText(string markdown)
    {
        // Except mid-conversation, where it appends: a cached follow-up answer still belongs
        // underneath the question that prompted it.
        if (!_pendingFollowUp) _buffer.Clear();
        _buffer.Append(markdown);
        _dirty = true;
        FlushRender();
    }

    public void CompleteQuery(string statusLine)
    {
        _renderTimer.Stop();
        FlushRender();
        _spinner.Visibility = Visibility.Collapsed;
        _status.Text = statusLine;
        _expand.IsEnabled = true;
    }

    /// <summary>
    /// Terminal state for a refusal, a guardrail deny, or a network failure. Always shows
    /// something actionable — a silent no-op would be indistinguishable from a hang.
    /// </summary>
    public void ShowMessage(string statusLine, string body)
    {
        _renderTimer.Stop();
        _spinner.Visibility = Visibility.Collapsed;
        _status.Text = statusLine;
        _buffer.Clear();
        _buffer.Append(body);
        _dirty = true;
        FlushRender();
        _expand.IsEnabled = false;
    }

    private void FlushRender()
    {
        if (!_dirty) return;
        _dirty = false;
        _answer.Document = MarkdownRenderer.Render(_buffer.ToString());
        _answer.ScrollToEnd();
        FitHeight();
    }

    /// <summary>
    /// Grows with the content up to a ceiling, then scrolls.
    ///
    /// Estimated from character count rather than measured from the FlowDocument: document
    /// extents are not valid until layout has run, and this is called on every throttled
    /// render tick during streaming. A rough estimate that never throws beats an exact one
    /// that stalls or misreports mid-stream — the RichTextBox scrolls either way.
    /// </summary>
    private void FitHeight()
    {
        try
        {
            const double charsPerLine = 62;
            const double lineHeight = 19;

            var lines = 0.0;
            foreach (var para in _buffer.ToString().Split('\n'))
                lines += Math.Max(1, Math.Ceiling(para.Length / charsPerLine));

            var desired = Math.Clamp(lines * lineHeight + 88, 150, MaxPopupHeight);
            if (Math.Abs(Height - desired) < 4) return;

            Height = desired;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return;

            var scale = Win32.ScaleFor(hwnd);
            var h = (int)Math.Round(desired * scale);
            var work = Win32.WorkAreaForPoint(new Win32.POINT { X = r.Left, Y = r.Top });
            var y = Math.Min(r.Top, Math.Max(work.Top, work.Bottom - h));

            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, r.Left, y, r.Right - r.Left, h,
                               Win32.SWP_NOACTIVATE);
        }
        catch { /* layout is best-effort; never let it break streaming */ }
    }

    // ------------------------------------------------------------------ placement

    /// <summary>
    /// IAnswerSurface entry point. Only anchors when not already visible, so a follow-up
    /// never makes the window jump away from where the user is typing.
    /// </summary>
    public void ShowFor(int cursorX, int cursorY)
    {
        if (!IsVisible) ShowNear(cursorX, cursorY);
    }

    public void ShowNear(int cursorX, int cursorY)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = Win32.ScaleFor(hwnd);
        var w = (int)Math.Round(PopupWidth * scale);
        var h = (int)Math.Round(Math.Max(Height, 180) * scale);

        var work = Win32.WorkAreaForPoint(new Win32.POINT { X = cursorX, Y = cursorY });
        var offset = (int)Math.Round(CursorOffsetPx * scale);

        var x = cursorX + offset;
        var y = cursorY + offset;

        if (x + w > work.Right) x = cursorX - offset - w;
        if (y + h > work.Bottom) y = cursorY - offset - h;
        x = Math.Max(work.Left, Math.Min(x, work.Right - w));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));

        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, w, h,
                           Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Opacity = 1;
    }

    /// <summary>No confirmation, ever. Esc and click-outside both land here.</summary>
    public void Dismiss()
    {
        if (!IsVisible) return;
        _renderTimer.Stop();
        _hasFocusRights = false;
        Hide();
        // Restore the non-activating style so the next show cannot steal focus.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Win32.MakeNonActivating(hwnd);
        Dismissed?.Invoke();
    }

    public bool ContainsPoint(int x, int y)
    {
        if (!IsVisible) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        return Win32.GetWindowRect(hwnd, out var r) && Win32.RectContains(r, x, y);
    }
}
