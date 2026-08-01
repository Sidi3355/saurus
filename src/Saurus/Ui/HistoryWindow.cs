using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Saurus.Config;
using Saurus.Guardrails;
using Saurus.Storage;

namespace Saurus.Ui;

public sealed record HistoryEntry(
    long Id,
    DateTime WhenUtc,
    string Selection,
    string Context,
    string Answer,
    long InputTokens,
    long OutputTokens,
    bool FromCache,
    string Process,
    string WindowTitle,
    string? Url,
    string? ThreadLabel = null,
    int FollowUps = 0)
{
    /// <summary>Turns after the first, so a conversation reads as one entry in the list.</summary>
    public string FollowUpNote => FollowUps switch
    {
        0 => "",
        1 => "  ·  1 follow-up",
        _ => $"  ·  {FollowUps} follow-ups"
    };

    public string When => WhenUtc.ToLocalTime().ToString("ddd HH:mm");

    /// <summary>
    /// The thread's document label when there is one — that is what the entry actually
    /// belongs to now that threads are documents rather than time windows.
    /// </summary>
    public string Where =>
        !string.IsNullOrWhiteSpace(ThreadLabel) ? ThreadLabel!
        : !string.IsNullOrEmpty(Url) ? ShortenUrl(Url!)
        : string.IsNullOrEmpty(Process) ? "unknown" : Process;

    private static string ShortenUrl(string url)
    {
        try { return new Uri(url).Host; } catch { return url; }
    }

    /// <summary>
    /// Newest-first read of the query log. Shared by the standalone history window and the
    /// side panel's history view so there is one definition of what "history" means.
    /// </summary>
    public static List<HistoryEntry> Load(Database db, int limit = 300) =>
        LoadWhere(db, "", limit);

    /// <summary>The lookups belonging to one document thread, newest first.</summary>
    public static List<HistoryEntry> LoadForThread(Database db, long threadId, int limit = 300) =>
        LoadWhere(db, "AND q.thread_id = $tid", limit, ("$tid", threadId));

    /// <summary>
    /// Conversation roots only. Follow-ups are folded into their root's totals rather than
    /// listed separately, so asking three questions about one term is one entry, not four
    /// rows all showing the same term.
    /// </summary>
    private static List<HistoryEntry> LoadWhere(
        Database db, string where, int limit, params (string, object?)[] extra) => db.Query(
        $"""
        SELECT q.id, q.created_utc, q.selection, q.context, q.answer,
               COALESCE(SUM(c.input_tokens), q.input_tokens),
               COALESCE(SUM(c.output_tokens), q.output_tokens),
               q.from_cache,
               t.process, t.window_title, t.url, t.source_label,
               COUNT(c.id) - 1
        FROM queries q
        JOIN threads t ON t.id = q.thread_id
        LEFT JOIN queries c ON c.root_id = q.id
        WHERE q.root_id = q.id
        {where}
        GROUP BY q.id
        ORDER BY q.created_utc DESC
        LIMIT $n
        """,
        r => new HistoryEntry(
            r.GetInt64(0),
            DateTime.Parse(r.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.IsDBNull(2) ? "" : r.GetString(2),
            r.IsDBNull(3) ? "" : r.GetString(3),
            r.IsDBNull(4) ? "" : r.GetString(4),
            r.GetInt64(5),
            r.GetInt64(6),
            r.GetInt64(7) != 0,
            r.IsDBNull(8) ? "" : r.GetString(8),
            r.IsDBNull(9) ? "" : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            Math.Max(0, r.GetInt32(12))),
        extra.Append(("$n", (object?)limit)).ToArray());

    /// <summary>One turn of a conversation, for the detail view.</summary>
    public sealed record Turn(string Prompt, string Answer, bool IsRoot, DateTime WhenUtc,
                              long In, long Out, bool FromCache);

    public static List<Turn> LoadConversation(Database db, long rootId) => db.Query(
        """
        SELECT CASE WHEN id = root_id THEN selection ELSE COALESCE(question, selection) END,
               answer, id = root_id, created_utc, input_tokens, output_tokens, from_cache
        FROM queries
        WHERE root_id = $root
        ORDER BY created_utc
        """,
        r => new Turn(
            r.IsDBNull(0) ? "" : r.GetString(0),
            r.IsDBNull(1) ? "" : r.GetString(1),
            r.GetInt64(2) != 0,
            DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.GetInt64(4), r.GetInt64(5), r.GetInt64(6) != 0),
        ("$root", rootId));

    public double Cost => UsagePricing.Cost(InputTokens, OutputTokens);

    /// <summary>
    /// What this one lookup cost. A cache hit is called out rather than shown as $0.0000,
    /// because the two mean different things: one is free, the other is a rounding artefact.
    /// </summary>
    public string Usage => (FromCache
        ? "cached · free"
        : InputTokens + OutputTokens == 0
            ? "no tokens recorded"
            : $"{InputTokens:N0} in / {OutputTokens:N0} out · {UsagePricing.Money(Cost)}") + FollowUpNote;

    /// <summary>One-line label for the list. The selection is the useful part.</summary>
    public string Headline
    {
        get
        {
            var s = Selection.Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (s.Length > 64) s = s[..64] + "…";
            return string.IsNullOrWhiteSpace(s) ? "(empty selection)" : s;
        }
    }
}

/// <summary>
/// Token prices, so display records can put a cost next to a token count.
///
/// Static because the alternative is threading a config object through every history record
/// and data template for two doubles that are fixed for the process lifetime. Prices are
/// display-only — token counts remain the source of truth, and the budget is enforced in
/// tokens, not dollars.
/// </summary>
internal static class UsagePricing
{
    private static double _inPerMTok = 1.0;
    private static double _outPerMTok = 5.0;

    public static void Configure(SaurusConfig cfg)
    {
        _inPerMTok = cfg.Pricing.InputPerMTok;
        _outPerMTok = cfg.Pricing.OutputPerMTok;
    }

    public static double Cost(long input, long output) =>
        input / 1_000_000.0 * _inPerMTok + output / 1_000_000.0 * _outPerMTok;

    /// <summary>
    /// Individual lookups cost fractions of a cent, so a fixed 2dp would render every one of
    /// them as "$0.00". Precision scales with magnitude instead.
    /// </summary>
    public static string Money(double d) =>
        d >= 1 ? $"${d:F2}" : d >= 0.01 ? $"${d:F3}" : $"${d:F4}";

    public static string Money(long input, long output) => Money(Cost(input, output));
}

/// <summary>
/// One document's worth of lookups. Threads are the top level of history now, because a
/// thread is a document and that is how you actually go looking for something: "what did I
/// ask about that paper", not "what did I ask at 14:32".
/// </summary>
public sealed record ThreadSummary(
    long Id,
    string Label,
    string Kind,
    DateTime LastUtc,
    int QueryCount,
    long InTokens,
    long OutTokens)
{
    public long TotalTokens => InTokens + OutTokens;

    public string When
    {
        get
        {
            var local = LastUtc.ToLocalTime();
            var age = DateTime.Now - local;
            if (age < TimeSpan.FromDays(1)) return local.ToString("HH:mm");
            if (age < TimeSpan.FromDays(7)) return local.ToString("ddd HH:mm");
            return local.ToString("d MMM");
        }
    }

    public string Detail
    {
        get
        {
            var count = QueryCount == 1 ? "1 lookup" : $"{QueryCount} lookups";
            return TotalTokens > 0
                ? $"{count}  ·  {TotalTokens:N0} tok  ·  {UsagePricing.Money(Cost)}"
                : count;
        }
    }

    public double Cost => UsagePricing.Cost(InTokens, OutTokens);

    /// <summary>Threads with no surviving queries are not listed — they are not history.</summary>
    public static List<ThreadSummary> Load(Database db, int limit = 200) => db.Query(
        """
        SELECT t.id,
               COALESCE(NULLIF(t.source_label, ''), NULLIF(t.window_title, ''), t.process, 'unknown'),
               COALESCE(t.source_kind, 'app'),
               MAX(q.created_utc),
               COUNT(q.id),
               COALESCE(SUM(q.input_tokens), 0),
               COALESCE(SUM(q.output_tokens), 0)
        FROM threads t
        JOIN queries q ON q.thread_id = t.id
        GROUP BY t.id
        ORDER BY MAX(q.created_utc) DESC
        LIMIT $n
        """,
        r => new ThreadSummary(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            r.GetInt32(4),
            r.GetInt64(5),
            r.GetInt64(6)),
        ("$n", limit));
}

/// <summary>
/// The simple list the brief allows: past queries, newest first, with their answers.
///
/// Unlike the pill and popup this is a normal activating window - it only opens on an
/// explicit click, long after any capture, so there is no focus constraint to respect.
/// </summary>
public sealed class HistoryWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1B));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x23));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3A));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0));

    private readonly Database _db;
    private readonly SaurusConfig _cfg;
    private readonly GuardrailChain _guards;

    private readonly ListBox _list;
    private readonly RichTextBox _detail;
    private readonly TextBlock _detailHeader;
    private readonly TextBlock _detailMeta;
    private readonly TextBlock _summary;

    /// <summary>Set by shutdown so the close handler stops intercepting.</summary>
    public bool ForceClose { get; set; }

    public HistoryWindow(Database db, SaurusConfig cfg, GuardrailChain guards)
    {
        _db = db;
        _cfg = cfg;
        _guards = guards;

        Title = "SAURUS - history";
        Width = 920;
        Height = 580;
        MinWidth = 620;
        MinHeight = 380;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- summary bar ----------------------------------------------------
        _summary = new TextBlock
        {
            Foreground = Dim,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(16, 12, 16, 12)
        };
        var bar = new Border
        {
            Background = Panel,
            BorderBrush = Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _summary
        };
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        // ---- split ----------------------------------------------------------
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = MarkdownRenderer.BodyFg,
            ItemTemplate = BuildItemTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _list.SelectionChanged += (_, _) => ShowDetail(_list.SelectedItem as HistoryEntry);
        Grid.SetColumn(_list, 0);
        split.Children.Add(_list);

        var divider = new Border { Width = 1, Background = Line };
        Grid.SetColumn(divider, 1);
        split.Children.Add(divider);

        var detailPane = new Grid { Margin = new Thickness(18, 14, 18, 16) };
        detailPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _detailHeader = new TextBlock
        {
            Foreground = MarkdownRenderer.BodyFg,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_detailHeader, 0);
        detailPane.Children.Add(_detailHeader);

        _detailMeta = new TextBlock
        {
            Foreground = Dim,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            Margin = new Thickness(0, 5, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(_detailMeta, 1);
        detailPane.Children.Add(_detailMeta);

        _detail = new RichTextBox
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
        Grid.SetRow(_detail, 2);
        detailPane.Children.Add(_detail);

        Grid.SetColumn(detailPane, 2);
        split.Children.Add(detailPane);

        Grid.SetRow(split, 1);
        root.Children.Add(split);

        Content = root;

        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Hide(); };

        // Closing would destroy the HWND; the app outlives this window, so hide instead.
        // ForceClose lets shutdown actually tear it down.
        Closing += (_, e) =>
        {
            if (ForceClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    private static DataTemplate BuildItemTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(MarginProperty, new Thickness(10, 7, 10, 7));

        var headline = new FrameworkElementFactory(typeof(TextBlock));
        headline.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryEntry.Headline)));
        headline.SetValue(TextBlock.ForegroundProperty, MarkdownRenderer.BodyFg);
        headline.SetValue(TextBlock.FontSizeProperty, 12.5);
        headline.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        panel.AppendChild(headline);

        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetValue(TextBlock.ForegroundProperty, Dim);
        meta.SetValue(TextBlock.FontSizeProperty, 10.5);
        meta.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding
        {
            StringFormat = "{0} - {1}",
            Bindings =
            {
                new System.Windows.Data.Binding(nameof(HistoryEntry.When)),
                new System.Windows.Data.Binding(nameof(HistoryEntry.Where))
            }
        });
        panel.AppendChild(meta);

        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>Reloads from SQLite every time it opens, so it never shows stale data.</summary>
    public void ShowHistory()
    {
        Reload();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Reload()
    {
        try
        {
            var rows = HistoryEntry.Load(_db);

            _list.ItemsSource = rows;
            if (rows.Count > 0) _list.SelectedIndex = 0;
            else ShowDetail(null);

            var u = _guards.Usage();
            _summary.Text =
                $"today  {u.DayTotalTokens:N0} tokens  ·  in {u.DayInputTokens:N0} / out {u.DayOutputTokens:N0}  " +
                $"·  ${u.DayCostUsd:F3}  ·  {u.DayCalls} calls  ·  budget {_cfg.Limits.DailyTokenBudget:N0}" +
                (_cfg.OfflineMode ? "     [OFFLINE - no API calls]" : "");
        }
        catch (Exception ex)
        {
            Core.Log.Error("could not load history", ex);
            _summary.Text = "could not load history - see the log";
        }
    }

    private void ShowDetail(HistoryEntry? entry)
    {
        if (entry is null)
        {
            _detailHeader.Text = "Nothing yet";
            _detailMeta.Text = "Highlight some text and click the pill.";
            _detail.Document = MarkdownRenderer.Render("");
            return;
        }

        _detailHeader.Text = entry.Selection;

        var bits = new List<string>
        {
            entry.WhenUtc.ToLocalTime().ToString("ddd d MMM, HH:mm:ss"),
            string.IsNullOrEmpty(entry.Process) ? "unknown app" : entry.Process
        };
        if (!string.IsNullOrEmpty(entry.WindowTitle)) bits.Add(entry.WindowTitle);
        if (!string.IsNullOrEmpty(entry.Url)) bits.Add(entry.Url!);
        bits.Add(entry.FromCache
            ? "served from cache"
            : $"in {entry.InputTokens} / out {entry.OutputTokens} tok");

        _detailMeta.Text = string.Join("  ·  ", bits);

        var body = entry.Answer;
        if (!string.IsNullOrWhiteSpace(entry.Context))
            body += $"\n\n---\n\n**Context captured with it**\n\n{entry.Context}";

        _detail.Document = MarkdownRenderer.Render(body);
    }
}
