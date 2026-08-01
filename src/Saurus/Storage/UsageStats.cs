namespace Saurus.Storage;

public sealed record DayUsage(DateOnly Date, long In, long Out, int Calls)
{
    public long Total => In + Out;
    public string ShortLabel => Date.ToString("d/M");
}

public sealed record TopThread(string Label, long In, long Out, int Calls)
{
    public long Tokens => In + Out;
}

/// <summary>
/// Everything the analytics view shows, read in one pass.
///
/// The ledger is the source of truth for spend — it is what the budget is enforced against —
/// while the query table carries the per-document attribution. Cache hits live in the query
/// table only, because by definition they never reached the ledger.
/// </summary>
public sealed record UsageReport(
    List<DayUsage> Days,
    DayUsage Today,
    long WeekTokens,
    long MonthTokens,
    long AllIn,
    long AllOut,
    int AllCalls,
    int CacheHits,
    List<TopThread> TopThreads)
{
    public long AllTokens => AllIn + AllOut;

    /// <summary>
    /// Tokens a cache hit avoided, valued at the average cost of a real call. Approximate by
    /// construction: nobody knows what the call that never happened would have cost, and the
    /// alternative is not reporting the saving at all.
    /// </summary>
    public long EstimatedCacheSavings =>
        AllCalls == 0 ? 0 : (long)((double)AllTokens / AllCalls * CacheHits);

    public static UsageReport Load(Database db, int days = 14)
    {
        var from = DateTime.Now.Date.AddDays(-(days - 1));

        // day_local is a local calendar day string, which is what the budget resets on.
        var rows = db.Query(
            """
            SELECT day_local,
                   COALESCE(SUM(input_tokens), 0),
                   COALESCE(SUM(output_tokens), 0),
                   COUNT(*)
            FROM usage_ledger
            WHERE settled = 1 AND day_local >= $from
            GROUP BY day_local
            """,
            r => (Day: r.GetString(0), In: r.GetInt64(1), Out: r.GetInt64(2), Calls: r.GetInt32(3)),
            ("$from", from.ToString("yyyy-MM-dd")));

        var byDay = rows.ToDictionary(x => x.Day, x => x);

        // Days with no activity still need a column, or the chart silently compresses time.
        var series = new List<DayUsage>();
        for (var i = 0; i < days; i++)
        {
            var date = DateOnly.FromDateTime(from.AddDays(i));
            var key = date.ToString("yyyy-MM-dd");
            series.Add(byDay.TryGetValue(key, out var v)
                ? new DayUsage(date, v.In, v.Out, v.Calls)
                : new DayUsage(date, 0, 0, 0));
        }

        var today = series[^1];
        var week = series.TakeLast(7).Sum(d => d.Total);
        var month = series.Sum(d => d.Total);

        var allIn = db.Scalar<long>("SELECT COALESCE(SUM(input_tokens),0) FROM usage_ledger WHERE settled=1");
        var allOut = db.Scalar<long>("SELECT COALESCE(SUM(output_tokens),0) FROM usage_ledger WHERE settled=1");
        var allCalls = db.Scalar<int>("SELECT COUNT(*) FROM usage_ledger WHERE settled=1");
        var cacheHits = db.Scalar<int>("SELECT COUNT(*) FROM queries WHERE from_cache = 1");

        var top = db.Query(
            """
            SELECT COALESCE(NULLIF(t.source_label,''), NULLIF(t.window_title,''), t.process, 'unknown'),
                   COALESCE(SUM(q.input_tokens), 0),
                   COALESCE(SUM(q.output_tokens), 0),
                   COUNT(q.id),
                   COALESCE(SUM(q.input_tokens + q.output_tokens), 0) AS tot
            FROM queries q
            JOIN threads t ON t.id = q.thread_id
            GROUP BY t.id
            HAVING tot > 0
            ORDER BY tot DESC
            LIMIT 6
            """,
            r => new TopThread(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3)));

        return new UsageReport(series, today, week, month, allIn, allOut, allCalls, cacheHits, top);
    }
}
