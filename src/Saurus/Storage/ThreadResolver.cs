using Saurus.Capture;
using Saurus.Config;
using Saurus.Core;

namespace Saurus.Storage;

public sealed record Exchange(string Question, string Answer, DateTime WhenUtc);

/// <summary>
/// Decides which conversation a query belongs to, and which parts of it to replay.
///
/// A thread is a document. You can read one paper over three days, switch between four of
/// them in an afternoon, and every lookup lands in the right conversation — because the key
/// is the document's identity, not how recently you last used it. See
/// <see cref="SourceIdentity"/>, which does the actual work.
///
/// The time window survives in exactly one place: when the source cannot be identified more
/// precisely than "some application". Threading a coarse key with no time limit would put
/// every lookup ever made in a code editor into one enormous thread.
///
/// Still one small replaceable class. Nothing else knows how either decision is made.
/// </summary>
public sealed class ThreadResolver
{
    private readonly Database _db;
    private readonly SaurusConfig _cfg;

    public ThreadResolver(Database db, SaurusConfig cfg) { _db = db; _cfg = cfg; }

    // ------------------------------------------------------------------ assignment

    public long Resolve(ForegroundInfo fg)
    {
        var id = SourceIdentity.Resolve(fg);
        var now = DateTime.UtcNow.ToString("O");

        long? existing;

        if (id.IsCoarse)
        {
            // Unidentifiable source: fall back to the old behaviour rather than pooling
            // everything together forever.
            var cutoff = DateTime.UtcNow.AddMinutes(-_cfg.Threading.WindowMinutes).ToString("O");
            existing = _db.Scalar<long?>(
                "SELECT id FROM threads WHERE source_key = $k AND last_utc >= $cut " +
                "ORDER BY last_utc DESC LIMIT 1",
                ("$k", id.Key), ("$cut", cutoff));
        }
        else
        {
            // No time limit at all. This is the whole point.
            existing = _db.Scalar<long?>(
                "SELECT id FROM threads WHERE source_key = $k ORDER BY last_utc DESC LIMIT 1",
                ("$k", id.Key));
        }

        if (existing is > 0)
        {
            _db.Exec("UPDATE threads SET last_utc = $now, window_title = $title, url = $url " +
                     "WHERE id = $id",
                     ("$now", now),
                     ("$title", fg.WindowTitle),
                     ("$url", string.IsNullOrEmpty(fg.Url) ? null : fg.Url),
                     ("$id", existing.Value));

            Log.Info($"thread {existing.Value} resumed [{id.Kind}] {id.Label}");
            return existing.Value;
        }

        _db.Exec(
            "INSERT INTO threads(source_key, source_label, source_kind, url, process, window_title, " +
            "created_utc, last_utc) VALUES($k, $lbl, $kind, $url, $proc, $title, $now, $now)",
            ("$k", id.Key),
            ("$lbl", id.Label),
            ("$kind", id.Kind),
            ("$url", string.IsNullOrEmpty(fg.Url) ? null : fg.Url),
            ("$proc", fg.ProcessName),
            ("$title", fg.WindowTitle),
            ("$now", now));

        var created = _db.Scalar<long>("SELECT last_insert_rowid()");
        Log.Info($"thread {created} created [{id.Kind}] {id.Label}");
        return created;
    }

    // ------------------------------------------------------------------ context

    /// <summary>
    /// Picks which prior exchanges to replay.
    ///
    /// Once a thread is a document rather than a 20-minute window it can hold hundreds of
    /// lookups, and "the last three" stops being the right three: if I have been reading a
    /// paper across two sessions and I ask about something in the results section, the useful
    /// context is the three lookups about that, not whatever I happened to check last. Worse,
    /// an unrelated most-recent exchange actively degrades the answer.
    ///
    /// So: always keep the single most recent exchange (a genuine follow-up is common and
    /// recency is the right signal for it), and fill the remaining slots by relevance to the
    /// current selection. Returned oldest-first.
    ///
    /// Scoring is term overlap weighted by inverse document frequency computed over the
    /// thread itself. No model, no index — the candidate set is one document's history, so
    /// scoring it in memory costs nothing and avoids a schema for something that may well be
    /// replaced.
    /// </summary>
    /// <summary>
    /// The turns of one conversation, oldest first.
    ///
    /// A follow-up is a chat, not a fresh lookup: what it needs is what was just said, in
    /// order, not the most topically similar thing said about this document last Tuesday.
    /// Relevance selection is for starting a new conversation.
    /// </summary>
    public List<Exchange> SelectConversation(long rootId)
    {
        var budget = Math.Max(1, _cfg.Threading.MaxExchanges);

        var turns = _db.Query(
            "SELECT CASE WHEN id = root_id THEN selection ELSE COALESCE(question, selection) END, " +
            "       answer, created_utc " +
            "FROM queries " +
            "WHERE root_id = $root AND answer IS NOT NULL AND answer <> '' " +
            "ORDER BY created_utc DESC LIMIT $n",
            r => new Exchange(
                r.GetString(0),
                Clip(r.GetString(1), _cfg.Threading.MaxContextAnswerChars),
                DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)),
            ("$root", rootId), ("$n", budget));

        turns.Reverse();
        return turns;
    }

    public List<Exchange> SelectContext(long threadId, string selection, string context)
    {
        var budget = Math.Max(0, _cfg.Threading.MaxExchanges);
        if (budget == 0) return new List<Exchange>();

        // Replay the *selection*, not the full user turn that was originally sent.
        //
        // The stored question is the whole XML payload: selection, surrounding paragraph,
        // process, window title, URL. Replaying three of those meant every query in a thread
        // carried three copies of context the model does not need — the current turn already
        // states where you are, and a previous lookup's surrounding paragraph is rarely
        // relevant. Sending the term alone costs roughly a tenth as much and reads the same.
        var candidates = _db.Query(
            "SELECT selection, answer, created_utc FROM queries " +
            "WHERE thread_id = $tid AND answer IS NOT NULL AND answer <> '' " +
            "AND selection IS NOT NULL AND selection <> '' " +
            "ORDER BY created_utc DESC LIMIT 80",
            r => new Exchange(
                r.GetString(0),
                Clip(r.GetString(1), _cfg.Threading.MaxContextAnswerChars),
                DateTime.Parse(r.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)),
            ("$tid", threadId));

        if (candidates.Count <= budget)
        {
            candidates.Reverse();
            return candidates;
        }

        var picked = new List<Exchange> { candidates[0] };   // most recent, always
        var rest = candidates.Skip(1).ToList();

        var queryTerms = Tokenize(selection + " " + context);
        if (queryTerms.Count > 0)
        {
            var idf = BuildIdf(rest);

            var ranked = rest
                .Select(e => (Exchange: e, Score: Score(e, queryTerms, idf)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(budget - 1)
                .Select(x => x.Exchange);

            picked.AddRange(ranked);
        }

        // Nothing scored: fall back to plain recency rather than sending less context.
        if (picked.Count < budget)
            picked.AddRange(rest.Take(budget - picked.Count).Where(e => !picked.Contains(e)));

        return picked.OrderBy(e => e.WhenUtc).ToList();
    }

    /// <summary>Truncates on a word boundary so a replayed answer does not end mid-token.</summary>
    private static string Clip(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', Math.Min(max, s.Length - 1));
        return (cut > max / 2 ? s[..cut] : s[..max]) + "…";
    }

    private static double Score(Exchange e, HashSet<string> queryTerms, Dictionary<string, double> idf)
    {
        var terms = Tokenize(e.Question + " " + e.Answer);
        double score = 0;
        foreach (var t in terms)
            if (queryTerms.Contains(t) && idf.TryGetValue(t, out var w))
                score += w;

        // Long answers share more terms by accident; normalise so a brief precise match wins.
        return terms.Count == 0 ? 0 : score / Math.Sqrt(terms.Count);
    }

    /// <summary>
    /// Inverse document frequency over this thread's own exchanges. A term that appears in
    /// every lookup about a paper (its subject, say) carries no signal about *which* lookup
    /// is relevant; a term that appears in two carries a lot.
    /// </summary>
    private static Dictionary<string, double> BuildIdf(List<Exchange> docs)
    {
        var df = new Dictionary<string, int>();
        foreach (var d in docs)
            foreach (var t in Tokenize(d.Question + " " + d.Answer))
                df[t] = df.GetValueOrDefault(t) + 1;

        var n = Math.Max(1, docs.Count);
        return df.ToDictionary(kv => kv.Key, kv => Math.Log(1.0 + (double)n / kv.Value));
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "that", "this", "with", "from", "which", "into", "when",
        "you", "your", "are", "was", "were", "has", "have", "had", "its", "it's", "but",
        "not", "can", "will", "would", "there", "their", "than", "then", "they", "each",
        "such", "these", "those", "used", "using", "use", "also", "may", "one", "two",
        "selection", "context", "source", "url"
    };

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s)) return set;

        var buf = new System.Text.StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) buf.Append(char.ToLowerInvariant(ch));
            else { Emit(set, buf); buf.Clear(); }
        }
        Emit(set, buf);
        return set;
    }

    private static void Emit(HashSet<string> set, System.Text.StringBuilder buf)
    {
        if (buf.Length < 3) return;
        var w = buf.ToString();
        if (!StopWords.Contains(w)) set.Add(w);
    }
}
