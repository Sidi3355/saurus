using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Saurus.Ui;

/// <summary>
/// Minimal markdown to FlowDocument renderer.
///
/// Hand-rolled rather than pulling in a full markdown library because the output surface is
/// tiny and known: a few sentences of prose with inline code, bold, and the occasional short
/// list. Supports fenced code blocks, `-`/`*` bullets, `1.` ordered items, `#` headings,
/// `---` rules, **bold**, *italic* and `inline code`. Everything else renders as literal
/// text, which is the right failure mode for a popup that must never throw mid-stream.
///
/// Typography is set for reading a paragraph at arm's length on a dark panel, not for
/// cramming: 14.5px at a 24px line height with real space between blocks. Dense answers are
/// the product, so the text has to be comfortable or the whole thing is pointless.
/// </summary>
public static class MarkdownRenderer
{
    private const double BodySize = 14.5;
    private const double BodyLine = 24;
    private const double CodeSize = 13;
    private const double ParaGap = 12;

    private const string BodyFont = "Segoe UI";
    private const string MonoFont = "Cascadia Mono, Consolas, monospace";

    public static readonly Brush BodyFg = Frozen(Color.FromRgb(0xE8, 0xEC, 0xF2));
    public static readonly Brush CodeFg = Frozen(Color.FromRgb(0xA9, 0xD4, 0xFF));
    public static readonly Brush CodeBg = Frozen(Color.FromRgb(0x1E, 0x24, 0x2E));
    public static readonly Brush BlockBg = Frozen(Color.FromRgb(0x1A, 0x1F, 0x27));
    public static readonly Brush RuleFg = Frozen(Color.FromRgb(0x2C, 0x32, 0x3C));
    public static readonly Brush BoldFg = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily(BodyFont),
            FontSize = BodySize,
            Foreground = BodyFg,
            LineHeight = BodyLine,
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Left
        };

        foreach (var block in ParseBlocks(markdown ?? ""))
            doc.Blocks.Add(block);

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });

        return doc;
    }

    private static IEnumerable<Block> ParseBlocks(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // ---- fenced code -------------------------------------------------
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.AppendLine(lines[i]);
                    i++;
                }
                i++; // closing fence, or EOF mid-stream which is fine
                yield return CodeBlock(code.ToString().TrimEnd('\n'));
                continue;
            }

            // ---- horizontal rule ---------------------------------------------
            if (trimmed is "---" or "***" or "___")
            {
                i++;
                yield return Rule();
                continue;
            }

            // ---- heading ------------------------------------------------------
            // The prompt tells the model not to emit headings, but rendering a literal
            // "## Thing" would look broken on the day it ignores that.
            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                if (level <= 4 && level < trimmed.Length && trimmed[level] == ' ')
                {
                    var head = new Paragraph
                    {
                        Margin = new Thickness(0, i == 0 ? 0 : ParaGap + 4, 0, 6),
                        FontSize = level <= 1 ? BodySize + 3 : BodySize + 1,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = BoldFg
                    };
                    AddInlines(head.Inlines, trimmed[(level + 1)..].Trim());
                    i++;
                    yield return head;
                    continue;
                }
            }

            // ---- lists ---------------------------------------------------------
            if (IsBullet(line) || IsOrdered(line))
            {
                var list = new List
                {
                    Margin = new Thickness(0, 2, 0, ParaGap),
                    Padding = new Thickness(20, 0, 0, 0),
                    LineHeight = BodyLine,
                    MarkerStyle = IsOrdered(line) ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
                };

                while (i < lines.Length && (IsBullet(lines[i]) || IsOrdered(lines[i])))
                {
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 5) };
                    AddInlines(p.Inlines, StripMarker(lines[i]));
                    list.ListItems.Add(new ListItem(p));
                    i++;
                }

                yield return list;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // ---- paragraph -------------------------------------------------------
            var buf = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                   && !IsBullet(lines[i]) && !IsOrdered(lines[i])
                   && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)
                   && !lines[i].TrimStart().StartsWith('#')
                   && lines[i].Trim() is not ("---" or "***" or "___"))
            {
                if (buf.Length > 0) buf.Append(' ');
                buf.Append(lines[i].Trim());
                i++;
            }

            var para = new Paragraph { Margin = new Thickness(0, 0, 0, ParaGap) };
            AddInlines(para.Inlines, buf.ToString());
            yield return para;
        }
    }

    private static bool IsBullet(string l)
    {
        var t = l.TrimStart();
        return t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal);
    }

    private static bool IsOrdered(string l)
    {
        var t = l.TrimStart();
        var dot = t.IndexOf(". ", StringComparison.Ordinal);
        return dot is > 0 and < 4 && t[..dot].All(char.IsDigit);
    }

    private static string StripMarker(string l)
    {
        var t = l.TrimStart();
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal))
            return t[2..];
        var dot = t.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 ? t[(dot + 2)..] : t;
    }

    private static Block CodeBlock(string code) => new Paragraph(new Run(code))
    {
        FontFamily = new FontFamily(MonoFont),
        FontSize = CodeSize,
        LineHeight = CodeSize + 7,
        Background = BlockBg,
        Foreground = CodeFg,
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 2, 0, ParaGap),
        BorderBrush = RuleFg,
        BorderThickness = new Thickness(1)
    };

    private static Block Rule() => new Paragraph
    {
        Margin = new Thickness(0, 4, 0, ParaGap + 4),
        BorderBrush = RuleFg,
        BorderThickness = new Thickness(0, 1, 0, 0),
        FontSize = 0.5,
        LineHeight = 0.5
    };

    /// <summary>
    /// Single left-to-right pass over the inline markers. Deliberately not a real parser:
    /// an unmatched marker is emitted literally rather than swallowing the rest of the text,
    /// which matters because this runs against a half-received stream.
    /// </summary>
    private static void AddInlines(InlineCollection target, string text)
    {
        var buf = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            target.Add(new Run(buf.ToString()));
            buf.Clear();
        }

        while (i < text.Length)
        {
            // `inline code`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    // Hair spaces stand in for padding: a Run cannot have any, and code set
                    // flush against surrounding prose is hard to pick out.
                    target.Add(new Run(" " + text[(i + 1)..end] + " ")
                    {
                        FontFamily = new FontFamily(MonoFont),
                        FontSize = CodeSize,
                        Background = CodeBg,
                        Foreground = CodeFg
                    });
                    i = end + 1;
                    continue;
                }
            }

            // **bold**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    Flush();
                    target.Add(new Bold(new Run(text[(i + 2)..end])) { Foreground = BoldFg });
                    i = end + 2;
                    continue;
                }
            }

            // *italic* / _italic_
            if (text[i] is '*' or '_')
            {
                var marker = text[i];
                var end = text.IndexOf(marker, i + 1);
                if (end > i + 1)
                {
                    Flush();
                    target.Add(new Italic(new Run(text[(i + 1)..end])));
                    i = end + 1;
                    continue;
                }
            }

            buf.Append(text[i]);
            i++;
        }

        Flush();
    }
}
