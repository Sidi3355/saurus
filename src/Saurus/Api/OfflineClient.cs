using System.Text;

namespace Saurus.Api;

/// <summary>
/// Stand-in for <see cref="AnthropicClient"/> that makes no network call.
///
/// It streams a canned answer back token-by-token through exactly the same path a real
/// response takes, so everything except the model itself is under test: the hooks, the UIA
/// capture, focus behaviour, pill placement, markdown rendering, the throttled streaming
/// renderer, follow-ups, threading, the response cache and every rate limit.
///
/// The canned answer echoes the captured payload verbatim, which makes this the fastest way
/// to see what UIA actually grabbed from a given app.
///
/// Reports zero tokens because zero were spent. The ledger still records the call, so the
/// reservation path and the per-minute and interval limits are exercised honestly.
/// </summary>
public sealed class OfflineClient : IExplainClient
{
    /// <summary>Always true: offline mode needs no credential, so the key check passes.</summary>
    public bool HasKey => true;

    /// <summary>Roughly imitates a fast model so the streaming UI behaves realistically.</summary>
    private const int DelayPerChunkMs = 12;

    public async Task<ExplainResult> StreamAsync(
        string model,
        int maxTokens,
        string systemPrompt,
        IReadOnlyList<ChatTurn> turns,
        Action<string> onDelta,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var payload = turns.Count > 0 ? turns[^1].Content : "(nothing captured)";
        var priorExchanges = Math.Max(0, (turns.Count - 1) / 2);

        var body = $"""
            **Offline mode.** No API call was made and nothing was sent anywhere. Everything
            else on this path is real: the hook fired, UI Automation read the selection, and
            this text is streaming through the same renderer a live answer uses.

            This is exactly what would have been sent as the user turn:

            ```
            {payload}
            ```

            - model that *would* have been used: `{model}`
            - `max_tokens` ceiling: `{maxTokens}`
            - prior exchanges replayed from this thread: `{priorExchanges}`
            - system prompt length: `{systemPrompt.Length}` chars

            Type a follow-up below to check that the popup takes focus correctly, or press
            **Expand** to confirm the larger ceiling is applied. Re-select the same text
            within the cache window to confirm the cache short-circuits this entirely.
            """;

        await StreamOutAsync(body, onDelta, ct).ConfigureAwait(false);

        return new ExplainResult(body, 0, 0);
    }

    /// <summary>
    /// Emits in small chunks rather than all at once, so the 80 ms render throttle, the
    /// scroll-to-end behaviour and the window's height growth all get exercised.
    /// </summary>
    private static async Task StreamOutAsync(string text, Action<string> onDelta, CancellationToken ct)
    {
        var chunk = new StringBuilder();

        foreach (var ch in text)
        {
            chunk.Append(ch);

            // Break on whitespace so chunks land on word boundaries, as real deltas mostly do.
            if (chunk.Length < 6 || !char.IsWhiteSpace(ch)) continue;

            ct.ThrowIfCancellationRequested();
            onDelta(chunk.ToString());
            chunk.Clear();
            await Task.Delay(DelayPerChunkMs, ct).ConfigureAwait(false);
        }

        if (chunk.Length > 0) onDelta(chunk.ToString());
    }

    public void Dispose() { }
}
