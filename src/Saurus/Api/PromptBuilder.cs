using System.Text;
using Saurus.Capture;
using Saurus.Config;
using Saurus.Storage;

namespace Saurus.Api;

/// <summary>
/// Builds the system prompt and message list.
///
/// Selection and context are passed as separate, clearly delimited fields rather than
/// glued into a sentence: the model needs to know exactly which span the user pointed at,
/// because the selection is usually a single word and the context is the only thing that
/// disambiguates it.
/// </summary>
public static class PromptBuilder
{
    public static string System(SaurusConfig cfg) => $"""
        You explain highlighted text in place.

        The user has selected a fragment of text somewhere on their screen. You receive the
        exact selection, the surrounding text it appeared in, and where it came from. Explain
        what the SELECTION means as it is used in that specific CONTEXT.

        Rules:
        - No preamble. Do not restate the question or name the term before defining it.
          Start with the explanation itself.
        - Write for a technically literate reader. Assume general competence; do not define
          widely known terms or pad with background.
        - Dense. Every sentence carries information. No hedging, no "it's worth noting",
          no closing summary.
        - Roughly {cfg.ResponseSentences} sentences by default. If the user explicitly asks
          for more depth, give it.
        - If the selection is ambiguous, pick the reading the context supports and flag the
          alternative in a clause, not a paragraph.
        - The context is provided so you can disambiguate; do not explain the context itself.
        - Plain markdown only: `inline code`, **bold**, short bullet lists. No headings, no
          horizontal rules.
        """;

    /// <summary>
    /// Full message list: prior exchanges from the thread (oldest first), then this query.
    /// Prior exchanges are capped by config so an old thread cannot grow the payload.
    /// </summary>
    public static List<ChatTurn> Messages(
        CaptureResult capture,
        string selection,
        string context,
        IReadOnlyList<Exchange> history,
        string? followUp)
    {
        var turns = new List<ChatTurn>();

        foreach (var ex in history)
        {
            // A document thread can span days. Without a marker the model sees three
            // exchanges with no indication that two of them were on Tuesday, and reasons
            // about them as if they were part of one continuous conversation.
            var age = Age(ex.WhenUtc);
            var question = age is null ? ex.Question : $"[{age}]\n{ex.Question}";

            turns.Add(new ChatTurn("user", question));
            turns.Add(new ChatTurn("assistant", ex.Answer));
        }

        turns.Add(new ChatTurn("user", followUp is null
            ? InitialQuery(capture, selection, context)
            : followUp));

        return turns;
    }

    public static string InitialQuery(CaptureResult capture, string selection, string context)
    {
        var sb = new StringBuilder();
        sb.Append("<selection>\n").Append(selection).Append("\n</selection>\n");

        if (!string.IsNullOrWhiteSpace(context))
            sb.Append("<context>\n").Append(context).Append("\n</context>\n");

        var fg = capture.Foreground;
        if (!string.IsNullOrEmpty(fg.ProcessName) || !string.IsNullOrEmpty(fg.NormalizedTitle))
            sb.Append("<source app=\"").Append(Escape(fg.ProcessName)).Append("\">")
              .Append(Escape(fg.NormalizedTitle)).Append("</source>\n");

        if (!string.IsNullOrEmpty(fg.Url))
            sb.Append("<url>").Append(Escape(fg.Url)).Append("</url>\n");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Compact identity for the prior-exchange context, folded into the cache key so the
    /// same selection in two different threads does not share a cached answer.
    /// </summary>
    public static string HistoryFingerprint(IReadOnlyList<Exchange> history) =>
        history.Count == 0
            ? "-"
            : Core.Log.Fingerprint(
                string.Join("\n", history.Select(h => h.Question + "" + h.Answer)));

    /// <summary>
    /// Null for anything recent enough that saying so would be noise. Deliberately vague:
    /// the model needs to know an exchange is from a different sitting, not the timestamp.
    /// </summary>
    private static string? Age(DateTime whenUtc)
    {
        var gap = DateTime.UtcNow - whenUtc;
        if (gap < TimeSpan.FromHours(6)) return null;
        if (gap < TimeSpan.FromHours(30)) return "earlier session, yesterday";
        return $"earlier session, {(int)gap.TotalDays} days ago";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
