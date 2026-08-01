namespace Saurus.Capture;

/// <summary>What the foreground app is, independent of any text selection.</summary>
public sealed record ForegroundInfo(
    IntPtr Hwnd,
    string ProcessName,
    string WindowTitle,
    string? Url)
{
    public static readonly ForegroundInfo Empty = new(IntPtr.Zero, "", "", null);

    public bool IsBrowser => BrowserProcesses.Contains(ProcessName);

    private static readonly HashSet<string> BrowserProcesses =
        new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "librewolf", "zen" };

    // Leading noise seen in real window titles: black circle, bullet, asterisk,
    // middle dot, zero-width space. Escaped rather than literal so the file stays ASCII.
    private static readonly char[] TitleNoise =
        { '●', '•', '*', '·', ' ', '​' };

    /// <summary>
    /// Window titles carry noise that changes without the underlying document changing:
    /// unread counts, dirty markers, notification dots. Threading keys on the normalized
    /// form so two selections on the same page land in the same thread.
    /// </summary>
    public string NormalizedTitle
    {
        get
        {
            var t = WindowTitle;
            if (string.IsNullOrEmpty(t)) return "";

            t = t.TrimStart(TitleNoise);

            // Strip a leading "(3) " style unread count.
            if (t.StartsWith('('))
            {
                var close = t.IndexOf(')');
                if (close > 1 && close < 6 && t[1..close].All(char.IsDigit))
                    t = t[(close + 1)..];
            }

            return t.Trim();
        }
    }
}

/// <summary>
/// A completed capture. Produced entirely before any SAURUS window is created:
/// showing UI first would move focus and break the selection we are trying to read.
/// </summary>
public sealed record CaptureResult(
    string Selection,
    string Context,
    ForegroundInfo Foreground)
{
    public bool HasSelection => !string.IsNullOrWhiteSpace(Selection);

    /// <summary>
    /// Stable identity for "is this the same selection I just handled?".
    /// Fields are joined with a unit separator so that moving a character across a field
    /// boundary cannot produce a colliding fingerprint.
    /// </summary>
    public string Fingerprint => Core.Log.Fingerprint(
        string.Join('', Foreground.ProcessName, Foreground.NormalizedTitle, Selection));
}

/// <summary>Why a capture produced nothing. Drives whether we log, warn, or stay silent.</summary>
public enum CaptureFailure
{
    None,
    NoSelection,
    PasswordField,
    Blocklisted,
    Timeout,
    NoTextPattern,
    UnsupportedControl,
    Error
}

public sealed record CaptureOutcome(CaptureResult? Result, CaptureFailure Failure)
{
    public static CaptureOutcome Fail(CaptureFailure f) => new(null, f);
    public static CaptureOutcome Ok(CaptureResult r) => new(r, CaptureFailure.None);
    public bool Success => Result is not null && Result.HasSelection;
}
