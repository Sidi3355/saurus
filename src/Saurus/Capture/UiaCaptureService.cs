using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;   // TextPatternRange lives here, not alongside TextPattern
using Saurus.Config;
using Saurus.Core;
using Saurus.Interop;

namespace Saurus.Capture;

/// <summary>
/// Reads the current text selection via UI Automation.
///
/// There is deliberately no clipboard fallback. Synthesising Ctrl+C means sending input
/// into whatever happens to be focused: in a console host that is SIGINT and kills the
/// user's running process, and with a modifier physically held it becomes a different
/// chord entirely. UIA-only means we silently do nothing in apps with no accessibility
/// tree, which is the correct failure mode for a background utility.
/// </summary>
public sealed class UiaCaptureService : IDisposable
{
    private readonly SaurusConfig _cfg;
    private readonly UiaWorker _worker = new();

    /// <summary>
    /// Time budget for the whole UIA read. Anything slower than this is worse than the
    /// alt-tab-and-paste workflow we are competing with, so we give up rather than wait.
    /// Sized to cover a focused-element miss plus a short ancestor walk from the cursor.
    /// </summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    /// <summary>Longer budget for URL lookup, which walks a larger subtree.</summary>
    private static readonly TimeSpan UrlBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How far up we look for a text provider.
    ///
    /// In Chromium the selection is exposed on the document element, and everything between
    /// it and the node you clicked is a chain of Groups. Ten hops was measured as not enough
    /// for a list item inside Claude's transcript; the walk is cheap (one pattern probe and
    /// one GetParent per level, both of which short-circuit) so the limit is generous.
    /// </summary>
    private const int MaxAncestorHops = 30;

    // Control types where a double-click means "open", not "select a word".
    private static readonly HashSet<int> NonTextControlTypes = new()
    {
        ControlType.List.Id, ControlType.ListItem.Id,
        ControlType.Tree.Id, ControlType.TreeItem.Id,
        ControlType.DataGrid.Id, ControlType.DataItem.Id,
        ControlType.Table.Id, ControlType.Button.Id,
        ControlType.MenuItem.Id, ControlType.TabItem.Id,
        ControlType.Image.Id, ControlType.CheckBox.Id
    };

    private readonly Dictionary<IntPtr, (string Url, DateTime At)> _urlCache = new();

    public UiaCaptureService(SaurusConfig cfg) => _cfg = cfg;

    /// <summary>
    /// Full capture: foreground identity plus selection plus surrounding context.
    /// Runs entirely before any SAURUS window exists.
    /// </summary>
    /// <param name="cursorX">Cursor position in physical pixels, used to find the element
    /// under the pointer when the focused element is not the one holding the selection.</param>
    /// <param name="doubleClick">
    /// True when the gesture was a double-click. Only then does the list/tree/grid guard
    /// apply — those control types mean "open", but only for that gesture. A drag that
    /// happens to start inside a bulleted list is a perfectly ordinary text selection.
    /// </param>
    public async Task<CaptureOutcome> CaptureAsync(int cursorX, int cursorY, bool doubleClick)
    {
        var fg = GetForeground();

        if (_cfg.IsBlocked(fg.ProcessName))
        {
            Log.Info($"suppressed: process '{fg.ProcessName}' is blocklisted");
            return CaptureOutcome.Fail(CaptureFailure.Blocklisted);
        }

        var read = await _worker
            .RunAsync(() => ReadSelection(cursorX, cursorY, doubleClick), Budget)
            .ConfigureAwait(false);

        if (read is null)
        {
            Log.Info($"capture miss proc={fg.ProcessName} reason=Timeout (>{Budget.TotalMilliseconds:F0}ms)");
            return CaptureOutcome.Fail(CaptureFailure.Timeout);
        }

        if (read.Failure != CaptureFailure.None)
        {
            // Misses are logged in full. Working out why a selection did not register in one
            // particular app is otherwise guesswork, and the volume is low: this only runs
            // on a drag-end or a double-click, not on every mouse move.
            Log.Info($"capture miss proc={fg.ProcessName} reason={read.Failure} {read.Detail}");
            return CaptureOutcome.Fail(read.Failure);
        }

        var url = fg.IsBrowser ? await GetUrlAsync(fg.Hwnd).ConfigureAwait(false) : null;
        fg = fg with { Url = url };

        var result = new CaptureResult(read.Selection, read.Context, fg);

        Log.Info($"capture ok proc={fg.ProcessName} via={read.Source} url={(url is null ? "-" : "yes")} " +
                 $"sel={Describe(result.Selection)} ctx={Describe(result.Context)}");

        return CaptureOutcome.Ok(result);
    }

    private string Describe(string s) =>
        _cfg.LogSelections ? $"\"{s}\"" : $"{Log.Fingerprint(s)}/{s.Length}c";

    // ------------------------------------------------------------------ foreground

    public ForegroundInfo GetForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ForegroundInfo.Empty;

        var title = Win32.GetWindowTitle(hwnd);
        var proc = "";
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0) proc = Process.GetProcessById((int)pid).ProcessName;
        }
        catch { /* process exited, or access denied on an elevated window */ }

        return new ForegroundInfo(hwnd, proc, title, null);
    }

    // ------------------------------------------------------------------ selection

    private sealed record SelectionRead(
        string Selection, string Context, CaptureFailure Failure, string Source, string Detail = "")
    {
        public static SelectionRead Fail(CaptureFailure f, string detail = "") =>
            new("", "", f, "-", detail);
    }

    /// <summary>
    /// Runs on the UIA worker thread.
    ///
    /// The focused element is tried first, but it is frequently the wrong place to look.
    /// In a chat UI the keyboard focus stays in the composer box while you select text up in
    /// the transcript; in many web apps focus sits on the document body or on nothing
    /// useful at all. So if the focused element yields no selection we fall back to the
    /// element directly under the cursor and walk up its ancestors, since text providers in
    /// Chromium and Electron typically live several levels above the leaf node you hit.
    /// </summary>
    private SelectionRead ReadSelection(int cursorX, int cursorY, bool doubleClick)
    {
        AutomationElement? focused = null;
        try { focused = AutomationElement.FocusedElement; } catch { }

        // Password fields: never capture, never show a pill. This is checked on the *focused*
        // element specifically, because that is the one receiving keystrokes. Note it only
        // covers masked inputs; sensitive plain text elsewhere is indistinguishable from any
        // other text and is not detectable here.
        if (focused is not null)
        {
            try
            {
                if (focused.Current.IsPassword) return SelectionRead.Fail(CaptureFailure.PasswordField);
            }
            catch { }
        }

        // The focused element and its ancestors first. In a chat UI keyboard focus stays in
        // the composer while the selection is up in the transcript, so this usually misses -
        // but when it hits, it hits in one call.
        if (WalkUp(focused, "focus", out var fromFocus, out _)) return fromFocus!;

        // Then the pointer.
        AutomationElement? hit = null;
        try { hit = AutomationElement.FromPoint(new System.Windows.Point(cursorX, cursorY)); } catch { }

        if (hit is null)
            return SelectionRead.Fail(CaptureFailure.NoSelection, "FromPoint returned nothing");

        // A double-click on a file in Explorer or a row in a grid lands on a list/tree item;
        // that gesture means "open", so reject before we flash a pill.
        //
        // This is deliberately scoped to double-click only. Applying it to drags was a bug:
        // a drag-selection that happens to start inside a bulleted list, a table or over an
        // inline image reports one of those control types too, and bailing here meant the
        // pill silently never appeared for exactly those passages. Rich text — chat
        // transcripts, docs, rendered markdown — is full of them.
        if (doubleClick && IsNonTextControl(hit))
            return SelectionRead.Fail(CaptureFailure.UnsupportedControl, $"hit={ControlTypeName(hit)}");

        if (WalkUp(hit, "point", out var fromPoint, out var trail)) return fromPoint!;

        return SelectionRead.Fail(CaptureFailure.NoSelection,
            $"hit={ControlTypeName(hit)} focus={ControlTypeName(focused)} trail=[{trail}]");
    }

    /// <summary>
    /// Walks from <paramref name="start"/> up the control view, trying each level for a
    /// non-empty text selection.
    ///
    /// <paramref name="trail"/> records the control types visited, which is the only
    /// practical way to see why a given app failed: the answer is always "the provider was
    /// somewhere other than where we looked", and the trail says where we actually went.
    /// </summary>
    private bool WalkUp(AutomationElement? start, string label,
                        out SelectionRead? result, out string trail)
    {
        result = null;
        trail = "";
        if (start is null) return false;

        var walker = TreeWalker.ControlViewWalker;
        var node = start;
        var path = new List<string>();

        for (var hop = 0; hop < MaxAncestorHops && node is not null; hop++)
        {
            path.Add(ControlTypeName(node));

            if (TryRead(node, hop == 0 ? label : $"{label}+{hop}", out result))
            {
                trail = string.Join(">", path);
                return true;
            }

            // The top-level window is the end of the road; anything above it belongs to the
            // desktop and cannot hold the page's selection.
            if (IsTopLevel(node)) break;

            try { node = walker.GetParent(node); } catch { break; }
        }

        trail = string.Join(">", path);
        return false;
    }

    private static bool IsTopLevel(AutomationElement e)
    {
        try
        {
            var ct = e.Current.ControlType;
            return ct is not null && ct.Id == ControlType.Window.Id;
        }
        catch { return false; }
    }

    private static string ControlTypeName(AutomationElement? e)
    {
        if (e is null) return "none";
        try { return e.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>
    /// Attempts a selection read from one element. Returns false for every "not this one"
    /// case so the caller can keep walking; only a genuine non-empty selection succeeds.
    /// </summary>
    private bool TryRead(AutomationElement? element, string source, out SelectionRead? result)
    {
        result = null;
        if (element is null) return false;

        object? patternObj = null;
        try { element.TryGetCurrentPattern(TextPattern.Pattern, out patternObj); }
        catch { return false; }

        if (patternObj is not TextPattern text) return false;

        try
        {
            if (text.SupportedTextSelection == SupportedTextSelection.None) return false;
        }
        catch { return false; }

        TextPatternRange[]? ranges;
        try { ranges = text.GetSelection(); }
        catch { return false; }

        if (ranges is null || ranges.Length == 0) return false;

        // Some providers hand back a degenerate (caret-only) range for "no selection", and
        // multi-range selections put the real text in a later range, so scan them all.
        foreach (var range in ranges)
        {
            var selection = SafeGetText(range, _cfg.Limits.MaxSelectionChars * 2);
            if (string.IsNullOrWhiteSpace(selection)) continue;

            // Only the highlighted text unless surrounding context is explicitly enabled.
            // Nothing the user did not select ever leaves the machine.
            var context = _cfg.CaptureSurroundingContext ? ExpandForContext(range, selection) : "";

            result = new SelectionRead(selection.Trim(), context, CaptureFailure.None, source);
            return true;
        }

        return false;
    }

    private static bool IsNonTextControl(AutomationElement element)
    {
        try
        {
            var ct = element.Current.ControlType;
            return ct is not null && NonTextControlTypes.Contains(ct.Id);
        }
        catch { return false; }
    }

    /// <summary>
    /// Grows the selection to its enclosing paragraph so a single-word selection arrives
    /// with the sentence it appeared in. Paragraph is used rather than Sentence because
    /// most providers do not implement TextUnit.Sentence honestly - we trim back to a
    /// sentence window in managed code instead.
    /// </summary>
    private string ExpandForContext(TextPatternRange selectionRange, string selection)
    {
        try
        {
            var clone = selectionRange.Clone();
            clone.ExpandToEnclosingUnit(TextUnit.Paragraph);
            var para = SafeGetText(clone, _cfg.Limits.MaxContextChars * 3);

            if (string.IsNullOrWhiteSpace(para)) return "";
            para = para.Replace("\r", " ").Replace("\n", " ").Trim();
            if (para.Length <= _cfg.Limits.MaxContextChars) return para;

            // Too long: keep a window centred on the selection.
            var idx = para.IndexOf(selection.Trim(), StringComparison.Ordinal);
            if (idx < 0) return Truncate(para, _cfg.Limits.MaxContextChars);

            var half = _cfg.Limits.MaxContextChars / 2;
            var start = Math.Max(0, idx - half);
            var len = Math.Min(_cfg.Limits.MaxContextChars, para.Length - start);
            var window = para.Substring(start, len);
            if (start > 0) window = "…" + window;
            if (start + len < para.Length) window += "…";
            return window;
        }
        catch
        {
            return "";
        }
    }

    private static string SafeGetText(TextPatternRange range, int max)
    {
        try { return range.GetText(max) ?? ""; }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ------------------------------------------------------------------ browser URL

    /// <summary>
    /// Best-effort address-bar read. Matched on control type + a value that parses as an
    /// absolute http(s) URL rather than on the element name, which is locale-dependent.
    /// Cached briefly per window because this is the slowest part of a capture.
    /// </summary>
    private async Task<string?> GetUrlAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        lock (_urlCache)
        {
            if (_urlCache.TryGetValue(hwnd, out var hit) &&
                DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(2))
                return hit.Url;
        }

        var url = await _worker.RunAsync(() => ReadUrl(hwnd), UrlBudget).ConfigureAwait(false);
        if (url is null) return null;

        lock (_urlCache)
        {
            _urlCache[hwnd] = (url, DateTime.UtcNow);
            if (_urlCache.Count > 32) _urlCache.Clear();
        }
        return url;
    }

    private static string? ReadUrl(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true));

            var edits = root.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement e in edits)
            {
                if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) continue;
                var value = ((ValuePattern)vp).Current.Value;
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    return uri.ToString();

                // Browsers commonly hide the scheme in the omnibox.
                if (!value.Contains(' ') && value.Contains('.') &&
                    Uri.TryCreate("https://" + value, UriKind.Absolute, out var guessed))
                    return guessed.ToString();
            }
        }
        catch { }
        return null;
    }

    public void Dispose() => _worker.Dispose();
}
