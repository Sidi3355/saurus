using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Saurus.Config;
using Saurus.Storage;

namespace Saurus.Ui;

/// <summary>
/// The analytics view: what you have spent, on what, and when.
///
/// Charts are drawn from Borders in a Grid rather than pulled in from a plotting library.
/// The whole requirement is a stacked bar per day and a progress track — a charting
/// dependency would be several megabytes and a theming fight for about sixty lines of
/// layout, in an app whose selling point is that it idles at zero.
/// </summary>
internal sealed class StatsPane
{
    private const double ChartHeight = 118;

    private readonly SaurusConfig _cfg;
    private readonly Database _db;

    private static object Res(string key) => Application.Current.FindResource(key);
    private static Brush B(string key) => (Brush)Res(key);

    public StatsPane(SaurusConfig cfg, Database db) { _cfg = cfg; _db = db; }

    public double Cost(long input, long output) =>
        input / 1_000_000.0 * _cfg.Pricing.InputPerMTok +
        output / 1_000_000.0 * _cfg.Pricing.OutputPerMTok;

    /// <summary>Rebuilt from scratch on each open — it is a few dozen elements.</summary>
    public UIElement Build(out string subtitle)
    {
        var report = UsageReport.Load(_db);
        subtitle = $"{report.AllCalls} calls all time";

        var stack = new StackPanel();

        stack.Children.Add(TodayBlock(report));
        stack.Children.Add(BudgetBar(report));
        stack.Children.Add(SectionLabel("Last 14 days"));
        stack.Children.Add(Chart(report.Days));
        stack.Children.Add(Legend());
        stack.Children.Add(SectionLabel("Totals"));
        stack.Children.Add(TotalsGrid(report));

        if (report.TopThreads.Count > 0)
        {
            stack.Children.Add(SectionLabel("Where it went"));
            stack.Children.Add(TopThreads(report.TopThreads));
        }

        return new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
    }

    // ------------------------------------------------------------------ blocks

    private UIElement TodayBlock(UsageReport r)
    {
        var cost = Cost(r.Today.In, r.Today.Out);

        var big = new TextBlock
        {
            Text = $"${cost:F3}",
            Foreground = B("TextBody"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 30,
            FontWeight = FontWeights.SemiBold
        };

        var sub = new TextBlock
        {
            Text = $"{r.Today.Total:N0} tokens today  ·  {r.Today.In:N0} in / {r.Today.Out:N0} out  " +
                   $"·  {r.Today.Calls} calls",
            Foreground = B("TextDim"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };

        return new StackPanel { Margin = new Thickness(0, 4, 0, 14), Children = { big, sub } };
    }

    /// <summary>
    /// Today against the daily token budget. This is the number that actually stops the app,
    /// so it gets a bar rather than a line of text.
    /// </summary>
    private UIElement BudgetBar(UsageReport r)
    {
        var budget = Math.Max(1, _cfg.Limits.DailyTokenBudget);
        var frac = Math.Clamp((double)r.Today.Total / budget, 0, 1);

        var fill = new Border
        {
            Background = frac >= 0.8 ? new SolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x3D)) : B("Accent"),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var track = new Border
        {
            Height = 6,
            Background = B("SurfaceInput"),
            CornerRadius = new CornerRadius(3),
            Child = fill
        };

        // Width is a fraction of whatever the track ends up being, so it survives resizing.
        track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * frac);

        var caption = new TextBlock
        {
            Text = $"{r.Today.Total:N0} / {budget:N0} daily budget  ({frac * 100:F0}%)",
            Foreground = B("TextFaint"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0)
        };

        return new StackPanel { Margin = new Thickness(0, 0, 0, 4), Children = { track, caption } };
    }

    /// <summary>
    /// Stacked bars, input below and output above. Split rather than totalled because output
    /// is five times the price — a day that looks small in tokens can dominate the bill.
    /// </summary>
    private UIElement Chart(List<DayUsage> days)
    {
        var max = Math.Max(1, days.Max(d => d.Total));

        var bars = new Grid { Height = ChartHeight };
        var labels = new Grid { Margin = new Thickness(0, 5, 0, 0) };

        for (var i = 0; i < days.Count; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            labels.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var scale = (ChartHeight - 6) / max;

            var outBar = new Border
            {
                Height = Math.Max(d.Out > 0 ? 2 : 0, d.Out * scale),
                Background = B("Accent"),
                CornerRadius = new CornerRadius(2, 2, 0, 0)
            };

            var inBar = new Border
            {
                Height = Math.Max(d.In > 0 ? 2 : 0, d.In * scale),
                Background = B("AccentSoft"),
                CornerRadius = d.Out > 0 ? new CornerRadius(0) : new CornerRadius(2, 2, 0, 0)
            };

            // An empty day still gets a hairline, so the gap is visibly a zero rather than
            // a rendering failure.
            var baseline = new Border
            {
                Height = d.Total == 0 ? 2 : 0,
                Background = B("Hairline"),
                CornerRadius = new CornerRadius(1)
            };

            var column = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 2, 0),
                Children = { outBar, inBar, baseline },
                ToolTip = d.Total == 0
                    ? $"{d.Date:ddd d MMM} — nothing"
                    : $"{d.Date:ddd d MMM}\n{d.Total:N0} tokens  ({d.In:N0} in / {d.Out:N0} out)\n" +
                      $"{d.Calls} calls  ·  ${Cost(d.In, d.Out):F3}"
            };

            Grid.SetColumn(column, i);
            bars.Children.Add(column);

            // Every third day plus the last, otherwise the axis is unreadable at this width.
            if (i % 3 == 0 || i == days.Count - 1)
            {
                var lbl = new TextBlock
                {
                    Text = d.ShortLabel,
                    Foreground = B("TextFaint"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 9.5,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(lbl, i);
                labels.Children.Add(lbl);
            }
        }

        return new StackPanel { Children = { bars, labels } };
    }

    private UIElement Legend()
    {
        StackPanel Key(string key, string text) => new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 0),
            Children =
            {
                new Border
                {
                    Width = 9, Height = 9,
                    CornerRadius = new CornerRadius(2),
                    Background = B(key),
                    VerticalAlignment = VerticalAlignment.Center
                },
                new TextBlock
                {
                    Text = text,
                    Foreground = B("TextFaint"),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 10.5,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 9, 0, 0),
            Children = { Key("AccentSoft", "input"), Key("Accent", "output  (5x price)") }
        };
    }

    private UIElement TotalsGrid(UsageReport r)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var cells = new (string Label, string Value)[]
        {
            ("last 7 days",   $"{r.WeekTokens:N0} tok"),
            ("last 14 days",  $"{r.MonthTokens:N0} tok"),
            ("all time",      $"{r.AllTokens:N0} tok"),
            ("all time cost", $"${Cost(r.AllIn, r.AllOut):F2}"),
            ("calls",         $"{r.AllCalls:N0}"),
            ("avg per call",  r.AllCalls == 0 ? "—" : $"${Cost(r.AllIn, r.AllOut) / r.AllCalls:F4}"),
            ("cache hits",    $"{r.CacheHits:N0}"),
            ("saved by cache", $"~{r.EstimatedCacheSavings:N0} tok")
        };

        for (var i = 0; i < cells.Length; i += 2) grid.RowDefinitions.Add(new RowDefinition());

        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
            cell.Children.Add(new TextBlock
            {
                Text = cells[i].Value,
                Foreground = B("TextBody"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            });
            cell.Children.Add(new TextBlock
            {
                Text = cells[i].Label,
                Foreground = B("TextFaint"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetColumn(cell, i % 2);
            Grid.SetRow(cell, i / 2);
            grid.Children.Add(cell);
        }

        return grid;
    }

    /// <summary>Which documents cost the most. Threads are documents, so this is readable.</summary>
    private UIElement TopThreads(List<TopThread> threads)
    {
        var max = Math.Max(1, threads.Max(t => t.Tokens));
        var stack = new StackPanel();

        foreach (var t in threads)
        {
            var label = new TextBlock
            {
                Text = t.Label,
                Foreground = B("TextBody"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var value = new TextBlock
            {
                Text = UsagePricing.Money(t.In, t.Out),
                Foreground = B("TextFaint"),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            head.Children.Add(label);
            head.Children.Add(value);

            var fill = new Border
            {
                Background = B("Accent"),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            var track = new Border
            {
                Height = 4,
                Background = B("SurfaceInput"),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 5, 0, 0),
                Child = fill,
                ToolTip = $"{t.Tokens:N0} tokens over {t.Calls} lookups"
            };
            var frac = (double)t.Tokens / max;
            track.SizeChanged += (_, e) => fill.Width = Math.Max(0, e.NewSize.Width * frac);

            stack.Children.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 12),
                Children = { head, track }
            });
        }

        return stack;
    }

    private static UIElement SectionLabel(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        Foreground = B("TextFaint"),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 9)
    };
}
