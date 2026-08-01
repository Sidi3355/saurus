using System.Windows.Threading;
using Saurus.Api;
using Saurus.Capture;
using Saurus.Config;
using Saurus.Core;
using Saurus.Guardrails;
using Saurus.Storage;
using Saurus.Trigger;
using Saurus.Ui;

namespace Saurus.Orchestration;

/// <summary>
/// Wires the four layers together and owns the query lifecycle. Everything here runs on the
/// UI dispatcher; the slow parts (UIA, HTTP) are awaited, not blocked on.
/// </summary>
public sealed class ExplainOrchestrator : IDisposable
{
    private readonly SaurusConfig _cfg;
    private readonly TriggerService _trigger;
    private readonly GuardrailChain _guards;
    private readonly IExplainClient _api;
    private readonly Database _db;
    private readonly ThreadResolver _threads;
    private readonly Dispatcher _dispatcher;

    private readonly PillWindow _pill = new();

    /// <summary>Anchored popup or side panel, chosen by config. The orchestrator cannot tell.</summary>
    private readonly IAnswerSurface _popup;

    private CaptureResult? _current;
    private long _currentThreadId;

    /// <summary>Root of the conversation in progress. Zero until the first answer is stored.</summary>
    private long _currentRootQueryId;
    private InputPoint _currentPoint;
    private CancellationTokenSource? _inFlight;

    public ExplainOrchestrator(
        SaurusConfig cfg, TriggerService trigger, GuardrailChain guards,
        IExplainClient api, Database db, ThreadResolver threads, Dispatcher dispatcher,
        IAnswerSurface surface)
    {
        _cfg = cfg; _trigger = trigger; _guards = guards;
        _api = api; _db = db; _threads = threads; _dispatcher = dispatcher;
        _popup = surface;

        _trigger.SelectionDetected += OnSelectionDetected;
        _trigger.ImmediateRequested += (c, p) => { Stage(c, p); _ = RunAsync(null, _cfg.MaxTokens); };
        _trigger.Clicked += OnClickedAnywhere;
        _trigger.EscapePressed += () => { _pill.Dismiss(); _popup.Dismiss(); };

        _pill.Fired += () => { _ = RunAsync(null, _cfg.MaxTokens); };

        if (_popup is SidePanel sidePanel) sidePanel.ResumeRequested += ResumeConversation;

        _popup.FollowUpSubmitted += q => { _ = RunAsync(q, _cfg.MaxTokens); };
        _popup.ExpandRequested += () => { _ = RunAsync("Expand on that — more depth and detail.", _cfg.ExpandedMaxTokens); };
        _popup.Dismissed += CancelInFlight;
    }

    private void OnSelectionDetected(CaptureResult capture, InputPoint pt)
    {
        Stage(capture, pt);
        _pill.ShowAt(pt.X, pt.Y, _cfg.PillTimeoutMs);
    }

    private void Stage(CaptureResult capture, InputPoint pt)
    {
        _current = capture;
        _currentPoint = pt;
        _currentThreadId = 0;

        // A new selection starts a new conversation. Follow-ups asked after this point
        // belong to it, not to whatever was on screen before.
        _currentRootQueryId = 0;
    }

    /// <summary>
    /// Adopts a conversation opened from history as the current one.
    ///
    /// Restores enough state that a follow-up behaves identically to one asked seconds after
    /// the original: the same thread, the same conversation root, and a capture rebuilt from
    /// what was stored. The foreground window is deliberately not consulted — you are almost
    /// certainly looking at something else by now, and the conversation belongs to the
    /// document it came from, not to whatever happens to be on screen.
    /// </summary>
    private void ResumeConversation(long rootId)
    {
        try
        {
            var rows = _db.Query(
                "SELECT q.selection, q.context, q.thread_id, t.process, t.window_title, t.url " +
                "FROM queries q JOIN threads t ON t.id = q.thread_id WHERE q.id = $id",
                r => (
                    Selection: r.IsDBNull(0) ? "" : r.GetString(0),
                    Context: r.IsDBNull(1) ? "" : r.GetString(1),
                    ThreadId: r.GetInt64(2),
                    Process: r.IsDBNull(3) ? "" : r.GetString(3),
                    Title: r.IsDBNull(4) ? "" : r.GetString(4),
                    Url: r.IsDBNull(5) ? null : r.GetString(5)),
                ("$id", rootId));

            if (rows.Count == 0)
            {
                Log.Warn($"cannot resume conversation {rootId}: no such query");
                return;
            }

            var row = rows[0];
            _current = new CaptureResult(
                row.Selection,
                row.Context,
                new Capture.ForegroundInfo(IntPtr.Zero, row.Process, row.Title, row.Url));

            _currentThreadId = row.ThreadId;
            _currentRootQueryId = rootId;

            Log.Info($"resumed conversation {rootId} in thread {row.ThreadId}");
        }
        catch (Exception ex)
        {
            Log.Error("could not resume conversation", ex);
        }
    }

    /// <summary>Click-outside dismissal. Our windows do not hold focus, so there is no
    /// deactivation event to hang this off — the mouse hook is the only signal.</summary>
    private void OnClickedAnywhere(InputPoint pt)
    {
        if (_pill.IsVisible && !_pill.ContainsPoint(pt.X, pt.Y) && !_popup.ContainsPoint(pt.X, pt.Y))
            _pill.Dismiss();

        if (_popup.IsVisible && !_popup.ContainsPoint(pt.X, pt.Y) && !_pill.ContainsPoint(pt.X, pt.Y))
            _popup.Dismiss();
    }

    private void CancelInFlight()
    {
        try { _inFlight?.Cancel(); } catch { }
        _inFlight = null;
    }

    /// <summary>
    /// One query. The popup is shown and put into its loading state before anything
    /// expensive happens — the window must never wait on the network to appear.
    /// </summary>
    private async Task RunAsync(string? followUp, int maxTokens)
    {
        var capture = _current;
        if (capture is null) return;

        _pill.Dismiss();
        CancelInFlight();
        var cts = new CancellationTokenSource();
        _inFlight = cts;

        // Truncated before the header is drawn, not after, so the panel shows exactly the
        // text that was sent. Showing the full selection while sending a clipped one would
        // be quietly wrong in the one case where it matters.
        var (selection, context) = _guards.Truncate(capture.Selection, capture.Context);

        var source = SourceLabel(capture.Foreground);
        if (capture.Selection.Length > selection.Length)
        {
            var note = $"trimmed to {_cfg.Limits.MaxSelectionChars} of {capture.Selection.Length} chars";
            source = string.IsNullOrEmpty(source) ? note : $"{source}  ·  {note}";
        }

        var status = _cfg.OfflineMode
            ? "OFFLINE · no API call"
            : followUp is null ? "explaining…" : "thinking…";

        if (followUp is null) _popup.BeginQuery(selection, source, status);
        else _popup.BeginFollowUp(followUp, status);

        // The surface decides what "show" means: the anchored popup only re-anchors when it
        // was hidden, the panel only replays its slide when it was closed. Either way a
        // follow-up must not make the window move out from under the user.
        _popup.ShowFor(_currentPoint.X, _currentPoint.Y);

        var started = DateTime.UtcNow;

        // Hoisted so the catch blocks can settle a reservation the call never spent.
        long reservationId = 0;

        try
        {
            if (_currentThreadId == 0) _currentThreadId = _threads.Resolve(capture.Foreground);

            // A follow-up replays its own conversation in order; a new lookup gets the most
            // relevant prior exchanges from the document. Different questions, different
            // answers.
            var history = followUp is not null && _currentRootQueryId != 0
                ? _threads.SelectConversation(_currentRootQueryId)
                : _threads.SelectContext(_currentThreadId, selection, context);

            var turns = PromptBuilder.Messages(capture, selection, context, history, followUp);
            var system = PromptBuilder.System(_cfg);

            var cacheKey = GuardrailChain.CacheKey(
                _cfg.Model, maxTokens, selection, context,
                PromptBuilder.HistoryFingerprint(history), followUp ?? "");

            // The exact user turn we are sending, stored verbatim so a later exchange in this
            // thread replays what the model actually saw rather than a reconstruction.
            var userTurn = turns[^1].Content;

            var promptChars = system.Length + turns.Sum(t => t.Content.Length);

            var decision = _guards.Evaluate(_api.HasKey, cacheKey, promptChars, maxTokens);

            switch (decision.Verdict)
            {
                case GuardrailVerdict.Deny:
                    Log.Warn($"denied: {decision.DenyReason}");
                    _popup.ShowMessage("blocked", decision.UserMessage ?? "Blocked.");
                    return;

                case GuardrailVerdict.ServeFromCache:
                    _popup.SetText(decision.CachedAnswer ?? "");
                    _popup.CompleteQuery("cached · no API call");
                    Persist(_currentThreadId, selection, context, userTurn,
                            decision.CachedAnswer ?? "", 0, 0, fromCache: true);
                    return;
            }

            reservationId = decision.ReservationId;

            // ---- live call ----------------------------------------------------
            var result = await _api.StreamAsync(
                _cfg.Model,
                maxTokens,
                system,
                turns,
                chunk => _dispatcher.InvokeAsync(() => _popup.AppendDelta(chunk)),
                TimeSpan.FromMilliseconds(_cfg.Limits.RequestTimeoutMs),
                cts.Token).ConfigureAwait(true);

            _guards.Settle(reservationId, result.InputTokens, result.OutputTokens);
            reservationId = 0;

            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                _guards.StoreInCache(cacheKey, result.Text, result.InputTokens, result.OutputTokens);
                Persist(_currentThreadId, selection, context, userTurn,
                        result.Text, result.InputTokens, result.OutputTokens, fromCache: false);
            }

            var ms = (DateTime.UtcNow - started).TotalMilliseconds;
            var cost = result.InputTokens / 1_000_000.0 * _cfg.Pricing.InputPerMTok
                     + result.OutputTokens / 1_000_000.0 * _cfg.Pricing.OutputPerMTok;

            _popup.CompleteQuery(_cfg.OfflineMode
                ? $"OFFLINE · nothing sent · {ms:F0} ms"
                : $"{_cfg.Model} · in {result.InputTokens} / out {result.OutputTokens} tok · " +
                  $"${cost:F4} · {ms:F0} ms");
        }
        catch (OperationCanceledException)
        {
            // Popup dismissed mid-stream. The reservation stays charged: the API generated
            // and billed those tokens whether or not we displayed them.
            Log.Info("query cancelled by dismissal");
        }
        catch (AnthropicException ex)
        {
            // The call failed outright, so no tokens were generated: settle at zero rather
            // than leaving the pre-call estimate charged. A stream that dies *after* content
            // has arrived returns a partial result instead of throwing, so it does not land
            // here and stays correctly billed.
            SettleUnspent(ref reservationId);
            Log.Error($"api error: {ex.Message}");
            _popup.ShowMessage("error", ex.Message);
        }
        catch (Exception ex)
        {
            SettleUnspent(ref reservationId);
            Log.Error("query failed", ex);
            _popup.ShowMessage("error", $"Unexpected failure: {ex.GetType().Name}. See the log.");
        }
        finally
        {
            if (ReferenceEquals(_inFlight, cts)) _inFlight = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Releases a reservation for a call that never spent anything. Without this, a run of
    /// failed calls (a bad key, say) would burn the day's budget on estimates alone and
    /// lock the user out for reasons that have nothing to do with spend.
    /// </summary>
    private void SettleUnspent(ref long reservationId)
    {
        if (reservationId == 0) return;
        _guards.Settle(reservationId, 0, 0);
        reservationId = 0;
    }

    /// <summary>"chrome · example.com", or just the process when there is no URL.</summary>
    private static string SourceLabel(Capture.ForegroundInfo fg)
    {
        var bits = new List<string>();
        if (!string.IsNullOrEmpty(fg.ProcessName)) bits.Add(fg.ProcessName);

        if (!string.IsNullOrEmpty(fg.Url))
        {
            try { bits.Add(new Uri(fg.Url!).Host); } catch { }
        }
        else if (!string.IsNullOrEmpty(fg.NormalizedTitle))
        {
            var t = fg.NormalizedTitle;
            bits.Add(t.Length > 48 ? t[..48] + "…" : t);
        }

        return string.Join("  ·  ", bits);
    }

    /// <summary>
    /// Writes one turn. A first lookup becomes its own root; a follow-up or an Expand hangs
    /// off the root it belongs to, so history shows one conversation rather than the same
    /// term repeated once per question asked about it.
    /// </summary>
    private void Persist(long threadId, string selection, string context, string? question,
                         string answer, long inTok, long outTok, bool fromCache)
    {
        try
        {
            _db.Exec(
                "INSERT INTO queries(thread_id, created_utc, selection, context, question, answer, " +
                "model, input_tokens, output_tokens, from_cache, root_id) " +
                "VALUES($tid, $t, $sel, $ctx, $q, $a, $m, $i, $o, $c, $root)",
                ("$tid", threadId),
                ("$t", DateTime.UtcNow.ToString("O")),
                ("$sel", selection),
                ("$ctx", context),
                ("$q", question),
                ("$a", answer),
                ("$m", _cfg.Model),
                ("$i", inTok), ("$o", outTok),
                ("$c", fromCache ? 1 : 0),
                ("$root", _currentRootQueryId == 0 ? null : _currentRootQueryId));

            var id = _db.Scalar<long>("SELECT last_insert_rowid()");

            if (_currentRootQueryId == 0)
            {
                // A root points at itself, so every later query can treat root_id uniformly.
                _currentRootQueryId = id;
                _db.Exec("UPDATE queries SET root_id = $id WHERE id = $id", ("$id", id));
            }
        }
        catch (Exception ex)
        {
            Log.Error("could not persist query", ex);
        }
    }

    public void Dispose()
    {
        CancelInFlight();
        _pill.Close();
        // The surface is owned by Program, which built it and closes it.
    }
}
