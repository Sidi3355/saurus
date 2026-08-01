using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Saurus.Config;
using Saurus.Guardrails;
using Saurus.Interop;
using Saurus.Storage;

namespace Saurus.Ui;

/// <summary>
/// The side panel: slides in from the right.
///
/// Opaque, not an AllowsTransparency window. Rounded corners and the drop shadow come from
/// DWM instead, so the whole slide is GPU-composited; a full-height per-pixel-alpha window
/// would be software-rendered every frame and stutter. The slide is a window-position
/// animation on a 16ms ticker that stops the moment it lands, so an open panel costs nothing.
///
/// Focus policy matches the popup: WS_EX_NOACTIVATE until the user clicks the follow-up box,
/// at which point the flag is dropped. Capture is long finished by then, and keeping the
/// foreground app active means its selection highlight stays blue rather than going grey.
///
/// Hosts three views: the live answer, the history list, and a historical entry.
/// </summary>
public sealed class SidePanel : Window, IAnswerSurface
{
    /// <summary>
    /// History is two levels deep now: documents, then the lookups inside one. That mirrors
    /// how threads actually work — you go looking for "what did I ask about that paper", not
    /// for "what did I ask at 14:32".
    /// </summary>
    private enum View { Answer, Threads, ThreadQueries, Detail, Stats }

    // One padding scale for the whole panel. These were drifting apart by two or four pixels
    // per region, which is exactly the sort of thing that reads as "uneven" without being
    // obvious about which edge is wrong.
    private const double PadX = 20;      // left edge to content
    private const double PadY = 16;      // any horizontal divider to content
    private const double PadScroll = 10; // right edge, tightened to leave room for a scrollbar

    private readonly SaurusConfig _cfg;
    private readonly Database _db;
    private readonly GuardrailChain _guards;
    private readonly DimOverlay? _overlay;

    // chrome
    private readonly TextBlock _subject;
    private readonly TextBlock _source;
    private readonly TextBlock _status;
    private readonly ProgressBar _stream;
    private readonly Button _historyButton;

    // answer view
    private readonly Grid _answerView;
    private readonly RichTextBox _answer;
    private readonly Border _footer;
    private readonly TextBox _input;
    private readonly Button _expand;

    // history view
    private readonly Grid _historyView;
    private readonly ListBox _threadList;
    private readonly ListBox _historyList;
    private readonly TextBlock _historySummary;
    private readonly Button _clearAll;
    private readonly Button _statsButton;
    private bool _clearArmed;
    private ThreadSummary? _openThread;

    // stats view
    private readonly StatsPane _stats;
    private readonly Border _statsView;

    private readonly StringBuilder _buffer = new();

    /// <summary>Answers already completed in this conversation, as markdown.</summary>
    private readonly StringBuilder _transcript = new();

    /// <summary>The follow-up currently being answered; empty for a first lookup.</summary>
    private string _pendingPrompt = "";
    private readonly DispatcherTimer _renderTimer;
    private readonly DispatcherTimer _slideTimer;

    private bool _dirty;
    private bool _hasFocusRights;
    private View _view = View.Answer;
    private string _liveSubject = "";
    private string _liveSource = "";

    private readonly Grid _root;

    private double _slideFrom, _slideTo;
    private bool _slidingOut;
    private bool _open;
    private int _panelY, _panelW, _panelH;

    /// <summary>
    /// Supplies the rectangle the panel grows out of — the badge, always, whatever opened
    /// the panel. Asked at open time rather than set per call, because the badge can be
    /// dragged and its position at the moment of opening is the only one that matters.
    /// Returns null when there is no badge, in which case the panel slides in from the edge.
    /// </summary>
    internal Func<Win32.RECT?>? MorphOriginProvider { get; set; }

    private bool _morphing;
    private Win32.RECT _morphFrom, _morphTo;

    /// <summary>Raised as the panel starts opening, so the badge can get out of the way.</summary>
    public event Action? Opening;

    /// <summary>
    /// A past conversation was opened and should become the live one. The argument is the
    /// root query id; the orchestrator reloads its selection, context and thread so that a
    /// follow-up typed here continues that conversation.
    /// </summary>
    public event Action<long>? ResumeRequested;

    /// <summary>Right edge of the monitor's work area: the off-screen end of the travel.</summary>
    private int _edgeX;

    /// <summary>
    /// Wall-clock rather than a per-tick counter. Counting ticks means a dropped frame
    /// silently lengthens the animation, so in and out drift apart under load even when
    /// they are configured identically.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _slideClock = new();

    public event Action<string>? FollowUpSubmitted;
    public event Action? ExpandRequested;
    public event Action? Dismissed;

    private static object Res(string key) => Application.Current.FindResource(key);
    private static Brush B(string key) => (Brush)Res(key);
    private static Style S(string key) => (Style)Res(key);

    /// <param name="overlay">Null when the backdrop dim is switched off in config.</param>
    public SidePanel(SaurusConfig cfg, Database db, GuardrailChain guards, DimOverlay? overlay)
    {
        _cfg = cfg;
        _db = db;
        _guards = guards;
        _overlay = overlay;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;          // see class comment
        Background = B("SurfaceBg");
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        if (_overlay is not null) _overlay.Clicked += Dismiss;

        // ---------------------------------------------------------------- header
        // Subject on top, source underneath, actions on the right. The selected term is the
        // single most useful thing on screen, so it gets the largest type and the top line.
        _subject = new TextBlock
        {
            Foreground = B("TextBody"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 52,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 22,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        _source = new TextBlock
        {
            Foreground = B("TextFaint"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var titleStack = new StackPanel { Children = { _subject, _source } };
        Grid.SetColumn(titleStack, 0);

        // Three views, so "back" is not a toggle. From a past entry it must return to the
        // list, not jump to the live answer - which is what it used to do, stranding you.
        // Four views, so back is a stack, not a toggle:
        // Answer -> Threads -> ThreadQueries -> Detail, and back down the same path.
        _historyButton = Button("History", S("FlatButton"));
        _historyButton.Click += (_, _) =>
        {
            switch (_view)
            {
                case View.Answer: ShowThreads(); break;
                case View.Threads: ShowAnswerView(); break;
                case View.ThreadQueries: ShowThreads(); break;
                case View.Detail:
                    if (_openThread is not null) ShowThreadQueries(_openThread);
                    else ShowThreads();
                    break;
                case View.Stats: ShowThreads(); break;
            }
        };
        Grid.SetColumn(_historyButton, 1);

        var close = Button("✕", S("FlatButton"));
        close.FontSize = 13;
        close.Padding = new Thickness(9, 4, 9, 4);
        close.Click += (_, _) => Dismiss();
        Grid.SetColumn(close, 2);

        var header = new Grid { Margin = new Thickness(PadX, PadY, 12, PadY) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titleStack);
        header.Children.Add(_historyButton);
        header.Children.Add(close);

        var headerBar = new Border
        {
            Background = B("SurfaceChrome"),
            BorderBrush = B("Hairline"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = header
        };

        _stream = new ProgressBar
        {
            Style = S("StreamBar"),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Top
        };

        // ---------------------------------------------------------------- answer view
        _answer = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = B("TextBody"),
            Padding = new Thickness(0),
            SelectionBrush = B("Accent"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = MarkdownRenderer.Render(""),
            // Bottom padding matters as much as top: without it the last line of an answer
            // butts straight into the footer divider while there is clear space under the
            // header, which is what made the panel look lopsided.
            Margin = new Thickness(PadX, PadY, PadScroll, PadY)
        };

        _input = new TextBox { Style = S("PanelTextBox") };

        var watermark = new TextBlock
        {
            Text = "Ask a follow-up…",
            Foreground = B("TextFaint"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _input.TextChanged += (_, _) =>
            watermark.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _expand = Button("Expand", S("AccentButton"));
        _expand.IsEnabled = false;
        _expand.Margin = new Thickness(8, 0, 0, 0);
        _expand.Click += (_, _) => { AcquireFocus(); ExpandRequested?.Invoke(); };

        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_input, 0);
        Grid.SetColumn(watermark, 0);
        Grid.SetColumn(_expand, 1);
        inputRow.Children.Add(_input);
        inputRow.Children.Add(watermark);
        inputRow.Children.Add(_expand);

        // Status meta sits with the input rather than in the header: it is about the answer
        // that just arrived, not about the thing you selected.
        _status = new TextBlock
        {
            Foreground = B("TextFaint"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            Margin = new Thickness(2, 0, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var footerStack = new StackPanel { Children = { _status, inputRow } };
        _footer = new Border
        {
            Background = B("SurfaceChrome"),
            BorderBrush = B("Hairline"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(PadX, PadY, PadX, PadY),
            Child = footerStack
        };

        _answerView = new Grid();
        _answerView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _answerView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_answer, 0);
        Grid.SetRow(_footer, 1);
        _answerView.Children.Add(_answer);
        _answerView.Children.Add(_footer);

        // ---------------------------------------------------------------- history view
        _historySummary = new TextBlock
        {
            Foreground = B("TextDim"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Margin = new Thickness(PadX, PadY, PadX, 12),
            TextWrapping = TextWrapping.Wrap
        };

        _historyList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = B("TextBody"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = BuildHistoryItemTemplate(),
            // List items carry their own 10px inset, so the container sits in by the
            // difference to land the text on the same left edge as everything else.
            Margin = new Thickness(PadX - 10, 0, PadScroll - 10, PadY)
        };
        _historyList.SelectionChanged += (_, _) =>
        {
            if (_historyList.SelectedItem is HistoryEntry entry) ShowHistoryDetail(entry);
        };

        // The per-row delete button lives inside the item template, so its Click bubbles up
        // to here. Reading the DataContext off the source is what identifies the row.
        _historyList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is FrameworkElement { DataContext: HistoryEntry entry })
                {
                    e.Handled = true;
                    DeleteEntry(entry);
                }
            }));

        _historyList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _historyList.SelectedItem is HistoryEntry entry)
            {
                e.Handled = true;
                DeleteEntry(entry);
            }
        };

        // Two-step rather than a modal confirm: a MessageBox from a non-activating window is
        // a focus problem waiting to happen, and this is one destructive action on a screen
        // with nothing else to hit by accident.
        _clearAll = Button("Clear all", S("FlatButton"));
        _clearAll.Margin = new Thickness(0);
        _clearAll.Click += (_, _) => ClearAllStep();

        _statsButton = Button("Stats", S("FlatButton"));
        _statsButton.Margin = new Thickness(0, 0, 6, 0);
        _statsButton.Click += (_, _) => ShowStats();

        var historyHeader = new Grid { Margin = new Thickness(PadX, PadY, PadX, 12) };
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        historyHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _historySummary.Margin = new Thickness(0);
        _historySummary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_historySummary, 0);
        Grid.SetColumn(_statsButton, 1);
        Grid.SetColumn(_clearAll, 2);
        historyHeader.Children.Add(_historySummary);
        historyHeader.Children.Add(_statsButton);
        historyHeader.Children.Add(_clearAll);

        _stats = new StatsPane(cfg, db);
        _statsView = new Border
        {
            Padding = new Thickness(PadX, 0, PadScroll, PadY),
            Visibility = Visibility.Collapsed
        };

        // Documents. The top level of history.
        _threadList = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = B("TextBody"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = BuildThreadItemTemplate(),
            Margin = new Thickness(PadX - 10, 0, PadScroll - 10, PadY)
        };
        _threadList.SelectionChanged += (_, _) =>
        {
            if (_threadList.SelectedItem is ThreadSummary t) ShowThreadQueries(t);
        };
        _threadList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is FrameworkElement { DataContext: ThreadSummary t })
                {
                    e.Handled = true;
                    DeleteThread(t);
                }
            }));

        _historyView = new Grid { Visibility = Visibility.Collapsed };
        _historyView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _historyView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(historyHeader, 0);
        Grid.SetRow(_threadList, 1);
        Grid.SetRow(_historyList, 1);
        _historyView.Children.Add(historyHeader);
        _historyView.Children.Add(_threadList);
        _historyView.Children.Add(_historyList);

        // ---------------------------------------------------------------- assembly
        var body = new Grid();
        body.Children.Add(_answerView);
        body.Children.Add(_historyView);
        body.Children.Add(_statsView);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(headerBar, 0);
        Grid.SetRow(_stream, 1);
        Grid.SetRow(body, 2);
        root.Children.Add(headerBar);
        root.Children.Add(_stream);
        root.Children.Add(body);

        // The content is given an explicit size at open time and pinned top-left inside a
        // clipping host. That is what makes the morph affordable: as the window grows from
        // the badge to full size the content is *revealed* rather than re-laid-out, so text
        // never reflows and WPF is not measuring a full-height panel on every frame.
        _root = root;
        _root.HorizontalAlignment = HorizontalAlignment.Left;
        _root.VerticalAlignment = VerticalAlignment.Top;

        Content = new Grid { ClipToBounds = true, Children = { _root } };

        // ---------------------------------------------------------------- behaviour
        _input.PreviewMouseLeftButtonDown += (_, _) => AcquireFocus();
        _answer.PreviewMouseLeftButtonDown += (_, _) => AcquireFocus();

        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(_input.Text))
            {
                e.Handled = true;
                var q = _input.Text.Trim();
                _input.Clear();
                ShowAnswerView();
                FollowUpSubmitted?.Invoke(q);
            }
            else if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); }
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeNonActivating(hwnd);
            Win32.RoundCorners(hwnd);
        };

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _renderTimer.Tick += (_, _) => FlushRender();

        _slideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _slideTimer.Tick += (_, _) => SlideStep();
    }

    private static Button Button(string text, Style style) => new()
    {
        Content = text,
        Style = style,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Top
    };

    private static DataTemplate BuildHistoryItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(Grid));
        row.SetValue(MarginProperty, new Thickness(10, 9, 6, 9));

        var textCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        textCol.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var buttonCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        buttonCol.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        row.AppendChild(textCol);
        row.AppendChild(buttonCol);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(Grid.ColumnProperty, 0);

        var headline = new FrameworkElementFactory(typeof(TextBlock));
        headline.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(HistoryEntry.Headline)));
        headline.SetValue(TextBlock.ForegroundProperty, B("TextBody"));
        headline.SetValue(TextBlock.FontSizeProperty, 13.5);
        headline.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(headline);

        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetValue(TextBlock.ForegroundProperty, B("TextFaint"));
        // 11.0 not 11: SetValue takes object, so an int literal boxes as int and WPF rejects
        // it at runtime rather than at compile time.
        meta.SetValue(TextBlock.FontSizeProperty, 11.0);
        meta.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        meta.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        // Time and cost, not time and source: this list only ever shows one document's
        // lookups, so repeating the document on every row says nothing.
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0} · {1}",
            Bindings =
            {
                new System.Windows.Data.Binding(nameof(HistoryEntry.When)),
                new System.Windows.Data.Binding(nameof(HistoryEntry.Usage))
            }
        });
        stack.AppendChild(meta);
        row.AppendChild(stack);

        // Always present rather than revealed on hover: a control that only exists while the
        // pointer is over it is undiscoverable. Ghosted, and the FlatButton style brings it
        // up on hover.
        var del = new FrameworkElementFactory(typeof(Button));
        del.SetValue(Grid.ColumnProperty, 1);
        del.SetValue(ContentControl.ContentProperty, "✕");
        del.SetValue(StyleProperty, S("FlatButton"));
        del.SetValue(OpacityProperty, 0.45);
        del.SetValue(FontSizeProperty, 11.0);
        del.SetValue(PaddingProperty, new Thickness(7, 3, 7, 3));
        del.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        del.SetValue(ToolTipProperty, "Delete this query");
        row.AppendChild(del);

        return new DataTemplate { VisualTree = row };
    }

    /// <summary>
    /// A document row: label, then how many lookups and when it was last touched.
    /// Same shape as the query row so moving between the two levels feels like one list.
    /// </summary>
    private static DataTemplate BuildThreadItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(Grid));
        row.SetValue(MarginProperty, new Thickness(10, 9, 6, 9));

        var textCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        textCol.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var buttonCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        buttonCol.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        row.AppendChild(textCol);
        row.AppendChild(buttonCol);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(Grid.ColumnProperty, 0);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(ThreadSummary.Label)));
        label.SetValue(TextBlock.ForegroundProperty, B("TextBody"));
        label.SetValue(TextBlock.FontSizeProperty, 13.5);
        label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(label);

        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetValue(TextBlock.ForegroundProperty, B("TextFaint"));
        meta.SetValue(TextBlock.FontSizeProperty, 11.0);
        meta.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        meta.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0} · {1}",
            Bindings =
            {
                new System.Windows.Data.Binding(nameof(ThreadSummary.Detail)),
                new System.Windows.Data.Binding(nameof(ThreadSummary.When))
            }
        });
        stack.AppendChild(meta);
        row.AppendChild(stack);

        var del = new FrameworkElementFactory(typeof(Button));
        del.SetValue(Grid.ColumnProperty, 1);
        del.SetValue(ContentControl.ContentProperty, "✕");
        del.SetValue(StyleProperty, S("FlatButton"));
        del.SetValue(OpacityProperty, 0.45);
        del.SetValue(FontSizeProperty, 11.0);
        del.SetValue(PaddingProperty, new Thickness(7, 3, 7, 3));
        del.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        del.SetValue(ToolTipProperty, "Delete this thread and everything in it");
        row.AppendChild(del);

        return new DataTemplate { VisualTree = row };
    }

    // ------------------------------------------------------------------ deletion

    private void DeleteThread(ThreadSummary thread)
    {
        try
        {
            _db.Exec("DELETE FROM queries WHERE thread_id = $id", ("$id", thread.Id));
            _db.Exec("DELETE FROM threads WHERE id = $id", ("$id", thread.Id));
            Core.Log.Info($"thread {thread.Id} deleted ({thread.Label})");
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not delete thread", ex);
        }

        ShowThreads();
    }

    private void DeleteEntry(HistoryEntry entry)
    {
        try
        {
            // Deleting a conversation takes its follow-ups with it; leaving them orphaned
            // would hide them from every view while still counting toward the totals.
            _db.Exec("DELETE FROM queries WHERE id = $id OR root_id = $id", ("$id", entry.Id));
            PruneEmptyThreads();
            Core.Log.Info($"history entry {entry.Id} deleted");
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not delete history entry", ex);
        }

        // Stay where you are. Deleting one lookup should not kick you back out to the
        // document list — you are almost always about to delete another.
        if (_openThread is not null && ThreadStillExists(_openThread.Id))
            ShowThreadQueries(_openThread);
        else
            ShowThreads();
    }

    private bool ThreadStillExists(long id) =>
        _db.Scalar<long>("SELECT COUNT(*) FROM queries WHERE thread_id = $id", ("$id", id)) > 0;

    /// <summary>
    /// First click arms, second click clears. Reverts if you navigate away, so an armed
    /// button cannot be triggered by a stray click on a later visit.
    /// </summary>
    private void ClearAllStep()
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            _clearAll.Content = "Confirm?";
            _clearAll.Style = S("AccentButton");
            return;
        }

        try
        {
            _db.Exec("DELETE FROM queries");
            _db.Exec("DELETE FROM threads");
            _db.Exec("DELETE FROM cache");
            Core.Log.Info("history cleared by the user");
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not clear history", ex);
        }

        DisarmClear();
        ShowThreads();
    }

    private void DisarmClear()
    {
        _clearArmed = false;
        _clearAll.Content = "Clear all";
        _clearAll.Style = S("FlatButton");
        _clearAll.Margin = new Thickness(0);
    }

    /// <summary>
    /// Threads with no surviving queries would otherwise linger and keep matching the
    /// 20-minute rule, silently attaching new questions to a conversation you deleted.
    /// </summary>
    private void PruneEmptyThreads() =>
        _db.Exec("DELETE FROM threads WHERE id NOT IN (SELECT DISTINCT thread_id FROM queries)");

    // ------------------------------------------------------------------ views

    private void ShowAnswerView()
    {
        DisarmClear();
        _view = View.Answer;
        _answerView.Visibility = Visibility.Visible;
        _historyView.Visibility = Visibility.Collapsed;
        _statsView.Visibility = Visibility.Collapsed;
        _footer.Visibility = Visibility.Visible;
        _historyButton.Content = "History";
        _historyList.SelectedItem = null;
        _subject.Text = _liveSubject;
        _source.Text = _liveSource;

        // Restore the live conversation: history detail renders straight to the document,
        // so coming back has to put the transcript on screen again.
        _dirty = true;
        FlushRender();
    }

    /// <summary>Top level of history: one row per document. Reloaded on every open.</summary>
    public void ShowThreads()
    {
        DisarmClear();
        _view = View.Threads;
        _openThread = null;

        _answerView.Visibility = Visibility.Collapsed;
        _historyView.Visibility = Visibility.Visible;
        _statsView.Visibility = Visibility.Collapsed;
        _threadList.Visibility = Visibility.Visible;
        _historyList.Visibility = Visibility.Collapsed;
        _clearAll.Visibility = Visibility.Visible;
        _statsButton.Visibility = Visibility.Visible;
        _historyButton.Content = "Back";
        _stream.Visibility = Visibility.Collapsed;
        _subject.Text = "History";

        try
        {
            var threads = ThreadSummary.Load(_db);
            _threadList.ItemsSource = threads;
            _threadList.SelectedItem = null;

            _source.Text = threads.Count == 1 ? "1 document" : $"{threads.Count} documents";

            var u = _guards.Usage();
            _historySummary.Text =
                $"today {u.DayTotalTokens:N0} tok  ·  ${u.DayCostUsd:F3}  ·  " +
                $"budget {_cfg.Limits.DailyTokenBudget:N0}" +
                (_cfg.OfflineMode ? "   ·   offline" : "");
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not load threads", ex);
            _historySummary.Text = "could not load history - see the log";
        }
    }

    /// <summary>Spend analytics. Rebuilt on open — it is a few dozen elements.</summary>
    public void ShowStats()
    {
        DisarmClear();
        _view = View.Stats;

        _answerView.Visibility = Visibility.Collapsed;
        _historyView.Visibility = Visibility.Collapsed;
        _statsView.Visibility = Visibility.Collapsed;
        _statsView.Visibility = Visibility.Visible;
        _historyButton.Content = "Back";
        _stream.Visibility = Visibility.Collapsed;
        _subject.Text = "Usage";

        try
        {
            _statsView.Child = _stats.Build(out var subtitle);
            _source.Text = subtitle;
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not build stats", ex);
            _source.Text = "could not load stats - see the log";
        }
    }

    /// <summary>The lookups inside one document.</summary>
    private void ShowThreadQueries(ThreadSummary thread)
    {
        DisarmClear();
        _view = View.ThreadQueries;
        _openThread = thread;

        _answerView.Visibility = Visibility.Collapsed;
        _historyView.Visibility = Visibility.Visible;
        _statsView.Visibility = Visibility.Collapsed;
        _threadList.Visibility = Visibility.Collapsed;
        _historyList.Visibility = Visibility.Visible;
        _clearAll.Visibility = Visibility.Collapsed;
        _statsButton.Visibility = Visibility.Collapsed;
        _historyButton.Content = "Back";
        _stream.Visibility = Visibility.Collapsed;

        _subject.Text = thread.Label;

        try
        {
            var rows = HistoryEntry.LoadForThread(_db, thread.Id);
            _historyList.ItemsSource = rows;
            _historyList.SelectedItem = null;

            _source.Text = $"{thread.Detail}  ·  {thread.Kind}";
            _historySummary.Text = thread.TotalTokens > 0
                ? $"{thread.TotalTokens:N0} tokens in this thread"
                : "no tokens recorded for this thread";
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not load thread queries", ex);
            _historySummary.Text = "could not load thread - see the log";
        }
    }

    /// <summary>
    /// Opens a past conversation and makes it the live one.
    ///
    /// The follow-up box stays available: the whole point of keeping conversations is being
    /// able to come back and ask the next question days later. <see cref="ResumeRequested"/>
    /// tells the orchestrator to adopt this conversation, so anything typed here continues
    /// it rather than starting something unrelated.
    /// </summary>
    private void ShowHistoryDetail(HistoryEntry entry)
    {
        DisarmClear();
        _view = View.Detail;
        _answerView.Visibility = Visibility.Visible;
        _historyView.Visibility = Visibility.Collapsed;
        _statsView.Visibility = Visibility.Collapsed;
        _footer.Visibility = Visibility.Visible;
        _historyButton.Content = "Back";
        _stream.Visibility = Visibility.Collapsed;
        _expand.IsEnabled = true;

        _subject.Text = entry.Selection;

        var bits = new List<string> { entry.WhenUtc.ToLocalTime().ToString("ddd d MMM, HH:mm") };
        if (!string.IsNullOrEmpty(entry.Process)) bits.Add(entry.Process);
        bits.Add(entry.Usage);
        _source.Text = string.Join("  ·  ", bits);

        // Rendered as a conversation: the first answer, then each follow-up with its
        // question, the way you asked them.
        var body = new StringBuilder();

        try
        {
            var turns = HistoryEntry.LoadConversation(_db, entry.Id);

            for (var i = 0; i < turns.Count; i++)
            {
                var t = turns[i];
                if (i > 0) body.Append("\n\n---\n\n**").Append(t.Prompt.Trim()).Append("**\n\n");
                body.Append(t.Answer);
            }

            if (turns.Count == 0) body.Append(entry.Answer);
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not load conversation", ex);
            body.Append(entry.Answer);
        }

        // Loaded into the transcript rather than straight to the document: this conversation
        // *becomes* the live one, so a follow-up appends to it exactly as if you had just
        // asked the original question.
        _liveSubject = entry.Selection;
        _liveSource = _source.Text;
        _transcript.Clear();
        _transcript.Append(body);
        _buffer.Clear();
        _pendingPrompt = "";
        _dirty = true;
        FlushRender();
        _answer.ScrollToHome();

        ResumeRequested?.Invoke(entry.Id);
    }

    // ------------------------------------------------------------------ focus

    private void AcquireFocus()
    {
        if (_hasFocusRights) return;
        _hasFocusRights = true;

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeActivating(hwnd);
        Win32.SetForegroundWindow(hwnd);
        Activate();
        if (_footer.Visibility == Visibility.Visible)
        {
            _input.Focus();
            Keyboard.Focus(_input);
        }
    }

    // ------------------------------------------------------------------ IAnswerSurface

    public void BeginQuery(string subject, string source, string statusLine)
    {
        _liveSubject = string.IsNullOrWhiteSpace(subject) ? "SAURUS" : subject.Trim();
        _liveSource = source;

        // A new selection is a new conversation, so the transcript goes with it.
        _transcript.Clear();
        _pendingPrompt = "";

        ShowAnswerView();
        _buffer.Clear();
        _dirty = true;
        _status.Text = statusLine;
        _stream.Visibility = Visibility.Visible;
        _expand.IsEnabled = false;
        _answer.Document = MarkdownRenderer.Render("");
        _renderTimer.Start();
    }

    public void BeginFollowUp(string question, string statusLine)
    {
        // Transcript is untouched: everything already answered stays on screen, the question
        // is shown, and the reply streams in underneath it.
        _pendingPrompt = question;

        ShowAnswerView();
        _buffer.Clear();
        _dirty = true;
        _status.Text = statusLine;
        _stream.Visibility = Visibility.Visible;
        _expand.IsEnabled = false;
        _renderTimer.Start();
        FlushRender();
    }

    public void AppendDelta(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _buffer.Append(chunk);
        _dirty = true;
    }

    public void SetText(string markdown)
    {
        _buffer.Clear();
        _buffer.Append(markdown);
        _dirty = true;
        FlushRender();
    }

    /// <summary>True when there is a conversation on screen to continue.</summary>
    public bool HasLiveConversation => _transcript.Length > 0;

    public void CompleteQuery(string statusLine)
    {
        _renderTimer.Stop();

        // Fold the finished answer into the transcript, so the next follow-up streams below
        // it rather than over it.
        _transcript.Append(PromptHeading(_pendingPrompt)).Append(_buffer);
        _buffer.Clear();
        _pendingPrompt = "";

        _dirty = true;
        FlushRender();
        _stream.Visibility = Visibility.Collapsed;
        _status.Text = statusLine;
        _expand.IsEnabled = true;
    }

    /// <summary>
    /// The heading shown above an answer. Empty for the first one — its subject is already
    /// the panel header, and repeating it immediately underneath reads as a stutter.
    /// </summary>
    private static string PromptHeading(string prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? "" : $"\n\n---\n\n**{prompt.Trim()}**\n\n";

    public void ShowMessage(string statusLine, string body)
    {
        _renderTimer.Stop();
        ShowAnswerView();
        _stream.Visibility = Visibility.Collapsed;
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

        // Everything answered so far, then the question being answered, then the tokens
        // arriving right now.
        _answer.Document = MarkdownRenderer.Render(
            _transcript + PromptHeading(_pendingPrompt) + _buffer);

        _answer.ScrollToEnd();
    }

    // ------------------------------------------------------------------ show / slide

    /// <summary>
    /// Realises the window and parks it beyond every monitor, once, at startup.
    ///
    /// This is what removes the stutter at the start of a slide. Showing a window for the
    /// first time costs an HWND, template expansion, a full measure/arrange pass and the
    /// creation of a render surface — and WPF briefly places it at its default position
    /// before any of our own positioning takes effect, which is the flash you see. Doing all
    /// of that up front, off-screen, means opening the panel afterwards is nothing but
    /// moving a window that is already drawn.
    ///
    /// It then stays shown for the life of the process, parked off-screen when closed.
    /// WS_EX_TOOLWINDOW keeps it out of Alt-Tab and the taskbar.
    /// </summary>
    public void Prewarm()
    {
        // Placed off-screen *before* Show, in DIPs, so WPF's own placement never puts it
        // somewhere visible for a frame. 20000 is beyond any real desktop at any scaling.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 20000;
        Top = 100;
        Width = 520;
        Height = 800;

        // Shown through WPF rather than ShowWindow: WPF will not render into a window it
        // does not believe is visible, so parking it with raw Win32 calls would prewarm the
        // HWND but not the content, which is the expensive half.
        Show();
        UpdateLayout();

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.MakeNonActivating(hwnd);
        Win32.RoundCorners(hwnd);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, Win32.OffscreenParkX(), 100, 0, 0,
                           Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE);

        _open = false;
    }

    public void ShowFor(int cursorX, int cursorY) => SlideIn(cursorX, cursorY);

    /// <summary>
    /// Opens straight onto the history list (the badge click).
    ///
    /// The list is loaded and laid out *before* the slide starts, not after. Reading SQLite
    /// and populating a ListBox is the single most expensive thing this window does, and
    /// doing it on the first frame of the animation was most of the glitchiness.
    /// </summary>
    public void ShowHistoryAt(int cursorX, int cursorY)
    {
        // The badge is the history button. It always opens the document list — continuing a
        // conversation is done by opening it from history, which now works for any of them,
        // not just the most recent.
        ShowThreads();
        UpdateLayout();
        SlideIn(cursorX, cursorY);
    }

    private void SlideIn(int cursorX, int cursorY)
    {
        var already = _open && !_slidingOut;

        var work = Win32.WorkAreaForPoint(new Win32.POINT { X = cursorX, Y = cursorY });
        var mon = Win32.MonitorRectForPoint(new Win32.POINT { X = cursorX, Y = cursorY });

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var scale = Win32.ScaleFor(hwnd);
        var inset = (int)Math.Round(_cfg.Panel.InsetDip * scale);

        // Between a fifth and a quarter of the screen, clamped so it stays usable on a small
        // laptop panel and does not become a billboard on an ultrawide.
        var monWidth = mon.Right - mon.Left;
        var wanted = (int)Math.Round(monWidth * Math.Clamp(_cfg.Panel.WidthFraction, 0.15, 0.40));
        _panelW = Math.Clamp(wanted, (int)Math.Round(420 * scale), (int)Math.Round(760 * scale));

        // Vertical placement is balanced against the *monitor*, not the work area.
        //
        // Insetting inside the work area is symmetric arithmetically and lopsided visually:
        // with a taskbar along the bottom you see `inset` of desktop above the panel and
        // `inset + taskbar` below it. Taking the larger of the two chrome bands and applying
        // it to both ends makes the empty space above and below actually match. With no
        // taskbar on either edge this collapses back to a plain inset.
        var chromeTop = work.Top - mon.Top;
        var chromeBottom = mon.Bottom - work.Bottom;
        var gap = Math.Max(chromeTop, chromeBottom) + inset;

        _panelY = mon.Top + gap;
        _panelH = (mon.Bottom - mon.Top) - gap * 2;

        var finalX = work.Right - _panelW - inset;

        if (_overlay is not null)
        {
            _overlay.Configure(_cfg.Panel.DimOpacity, _cfg.Panel.SlideMs);
            _overlay.FadeIn(cursorX, cursorY);
        }

        if (already)
        {
            // A follow-up on an open panel must not replay the animation.
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, finalX, _panelY, _panelW, _panelH,
                               Win32.SWP_NOACTIVATE);
            return;
        }

        _open = true;
        _slidingOut = false;
        _edgeX = work.Right;                     // just off the right edge of this monitor
        Opening?.Invoke();

        // Content is pinned at final size regardless of the window's current size.
        _root.Width = _panelW / scale;
        _root.Height = _panelH / scale;

        _morphTo = new Win32.RECT
        {
            Left = finalX, Top = _panelY,
            Right = finalX + _panelW, Bottom = _panelY + _panelH
        };

        if (MorphOriginProvider?.Invoke() is { } origin &&
            origin.Right > origin.Left && origin.Bottom > origin.Top)
        {
            // Grow out of whatever was clicked.
            _morphing = true;
            _morphFrom = origin;
            _root.Opacity = 0;

            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST,
                               origin.Left, origin.Top,
                               origin.Right - origin.Left, origin.Bottom - origin.Top,
                               Win32.SWP_NOACTIVATE);
        }
        else
        {
            // Fall back to the edge slide. Size is applied once here; every frame after
            // moves with SWP_NOSIZE, so it never triggers a WM_SIZE mid-animation.
            _morphing = false;
            _root.Opacity = 1;
            _slideFrom = _edgeX;
            _slideTo = finalX;

            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, (int)_slideFrom, _panelY, _panelW, _panelH,
                               Win32.SWP_NOACTIVATE);
        }

        _slideClock.Restart();
        _slideTimer.Start();
    }

    private void SlideStep()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) { _slideTimer.Stop(); return; }

        var duration = Math.Max(1, _cfg.Panel.SlideMs);
        var t = Math.Min(1.0, _slideClock.Elapsed.TotalMilliseconds / duration);

        // Identical curve and identical duration in both directions: ease-out cubic, fast
        // start and a soft landing. Easing *in* on the way out is what made leaving feel
        // slow — the first half of an ease-in covers only a quarter of the distance.
        var eased = 1 - Math.Pow(1 - t, 3);

        if (_morphing)
        {
            var l = Lerp(_morphFrom.Left, _morphTo.Left, eased);
            var top = Lerp(_morphFrom.Top, _morphTo.Top, eased);
            var r = Lerp(_morphFrom.Right, _morphTo.Right, eased);
            var b = Lerp(_morphFrom.Bottom, _morphTo.Bottom, eased);

            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, l, top, Math.Max(1, r - l), Math.Max(1, b - top),
                               Win32.SWP_NOACTIVATE);

            // Content fades over the first two thirds on the way in, and the last two thirds
            // on the way out. Fading in step with the geometry makes the clipped edge obvious;
            // running it ahead of the geometry hides it.
            _root.Opacity = _slidingOut
                ? Math.Clamp(1 - t / 0.45, 0, 1)
                : Math.Clamp((t - 0.15) / 0.5, 0, 1);
        }
        else
        {
            var x = (int)Math.Round(_slideFrom + (_slideTo - _slideFrom) * eased);
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, 0, 0, 0,
                               Win32.SWP_NOACTIVATE | Win32.SWP_NOSIZE);
        }

        if (t < 1.0) return;

        _slideTimer.Stop();
        _slideClock.Stop();

        if (!_slidingOut) return;

        // Park rather than hide. Hiding would throw away the render surface and pay for it
        // again on the next open, which is the stutter this whole design avoids.
        _slidingOut = false;
        _morphing = false;
        _open = false;
        _hasFocusRights = false;
        _root.Opacity = 1;
        Win32.MakeNonActivating(hwnd);
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, Win32.OffscreenParkX(), _panelY,
                           Math.Max(1, _panelW), Math.Max(1, _panelH),
                           Win32.SWP_NOACTIVATE);
        Dismissed?.Invoke();
    }

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    /// <summary>No confirmation, ever. Esc, the close button and the backdrop all land here.</summary>
    public void Dismiss()
    {
        if (!_open || _slidingOut) return;

        _renderTimer.Stop();
        _overlay?.FadeOut();

        var hwnd = new WindowInteropHelper(this).Handle;
        Win32.GetWindowRect(hwnd, out var r);

        // Exactly the reverse of the way in: same geometry, same duration, same curve.
        // Re-asked rather than remembered, so a badge dragged while the panel was open still
        // gets closed back into the right place.
        if (MorphOriginProvider?.Invoke() is { } origin && origin.Right > origin.Left)
        {
            _morphing = true;
            _morphFrom = r;
            _morphTo = origin;
        }
        else
        {
            _morphing = false;
            _slideFrom = r.Left;
            _slideTo = _edgeX;
        }

        _slidingOut = true;
        _slideClock.Restart();
        _slideTimer.Start();
    }

    /// <summary>
    /// Real hit test. With the dim switched off there is no backdrop to catch outside
    /// clicks, so the orchestrator's hook-driven test is the only thing that closes the
    /// panel when you click back into your work. With the dim on, both fire and Dismiss is
    /// idempotent.
    /// </summary>
    public bool ContainsPoint(int x, int y)
    {
        if (!_open) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var r)) return false;
        return Win32.RectContains(r, x, y);
    }

    /// <summary>
    /// The window stays realised and parked when closed, so WPF's own IsVisible is always
    /// true and would be a lie to anyone asking whether the panel is on screen.
    /// </summary>
    bool IAnswerSurface.IsVisible => _open;

    public void CloseAll()
    {
        _slideTimer.Stop();
        _slideClock.Stop();
        _renderTimer.Stop();
        _overlay?.HideNow();
        _open = false;
        Hide();
    }
}
