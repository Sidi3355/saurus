namespace Saurus.Ui;

/// <summary>
/// Whatever the answer gets streamed into.
///
/// Two implementations: <see cref="PopupWindow"/> (small, anchored at the cursor) and
/// <see cref="SidePanel"/> (dimmed backdrop, slides in from the right). The orchestrator
/// talks only to this, so switching between them is a config value rather than a rewrite.
/// </summary>
public interface IAnswerSurface
{
    bool IsVisible { get; }

    /// <summary>
    /// Puts the surface into its loading state. Must not wait on anything.
    /// </summary>
    /// <param name="subject">The selected text this query is about; shown as the heading.</param>
    /// <param name="source">Where it came from, e.g. "chrome · example.com".</param>
    void BeginQuery(string subject, string source, string statusLine);

    /// <summary>
    /// A follow-up in the conversation already on screen. Distinct from
    /// <see cref="BeginQuery"/> because the previous answers must stay visible — this is a
    /// chat, and replacing what you just read with the reply to your question about it makes
    /// the reply unreadable.
    /// </summary>
    void BeginFollowUp(string question, string statusLine);

    /// <summary>Places the surface for a query originating at this cursor position.</summary>
    void ShowFor(int cursorX, int cursorY);

    /// <summary>Appends a streamed chunk. Rendering is throttled internally.</summary>
    void AppendDelta(string chunk);

    /// <summary>Replaces the whole body, for answers that arrive complete (cache hits).</summary>
    void SetText(string markdown);

    void CompleteQuery(string statusLine);

    /// <summary>Terminal state for a guardrail deny, refusal or network failure.</summary>
    void ShowMessage(string statusLine, string body);

    void Dismiss();

    /// <summary>Physical-pixel hit test, for click-outside dismissal.</summary>
    bool ContainsPoint(int x, int y);

    /// <summary>The user typed a follow-up and pressed Enter.</summary>
    event Action<string>? FollowUpSubmitted;

    event Action? ExpandRequested;

    event Action? Dismissed;
}
