using System.Windows.Threading;
using Saurus.Capture;
using Saurus.Config;
using Saurus.Core;
using Saurus.Interop;

namespace Saurus.Trigger;

/// <summary>
/// Turns raw input events into "a selection now exists here".
///
/// The hook callbacks must not block, so everything here runs on the WPF dispatcher: the
/// hook raises an event, we immediately hand off, and the UIA read happens on its own
/// worker with a time budget. Nothing on the input path waits for us.
/// </summary>
public sealed class TriggerService : IDisposable
{
    private readonly InputHooks _hooks = new();
    private readonly UiaCaptureService _capture;
    private readonly SaurusConfig _cfg;
    private readonly Dispatcher _dispatcher;

    private string _lastFingerprint = "";
    private DateTime _lastFingerprintAt = DateTime.MinValue;
    private int _inFlight;

    /// <summary>A selection was detected. Second argument is the cursor position in physical pixels.</summary>
    public event Action<CaptureResult, InputPoint>? SelectionDetected;

    /// <summary>Ctrl was held — skip the pill and fire immediately.</summary>
    public event Action<CaptureResult, InputPoint>? ImmediateRequested;

    /// <summary>Left button pressed somewhere. Used for click-outside dismissal.</summary>
    public event Action<InputPoint>? Clicked;

    public event Action? EscapePressed;

    public TriggerService(SaurusConfig cfg, UiaCaptureService capture, Dispatcher dispatcher)
    {
        _cfg = cfg;
        _capture = capture;
        _dispatcher = dispatcher;

        // InvokeAsync, not Invoke: the hook thread must return to the input path immediately.
        _hooks.LeftDown += pt => _dispatcher.InvokeAsync(() => Clicked?.Invoke(pt));
        _hooks.LeftUp += OnLeftUp;
        _hooks.DoubleClick += OnDoubleClick;
        _hooks.EscapePressed += () => _dispatcher.InvokeAsync(() => EscapePressed?.Invoke());
    }

    public void Start() => _hooks.Start();

    /// <summary>
    /// Left-button-up. A selection is plausible if the pointer moved further than the system
    /// drag threshold. Ctrl held at release is the pill-free fast path.
    /// </summary>
    private void OnLeftUp(InputPoint pt, int dragDistance)
    {
        var ctrl = Win32.IsCtrlDown();

        if (!ctrl && dragDistance < _cfg.EffectiveDragThreshold()) return;
        if (ctrl && !_cfg.CtrlClickFires && dragDistance < _cfg.EffectiveDragThreshold()) return;

        _dispatcher.InvokeAsync(() => _ = TryCaptureAsync(pt, immediate: ctrl, doubleClick: false));
    }

    /// <summary>
    /// Double-click. This is also the universal "open" gesture in Explorer, list views and
    /// IDE trees, so the capture layer applies a control-type guard — but only for this
    /// gesture, never for a drag.
    /// </summary>
    private void OnDoubleClick(InputPoint pt)
    {
        var ctrl = Win32.IsCtrlDown();
        _dispatcher.InvokeAsync(() => _ = TryCaptureAsync(pt, immediate: ctrl, doubleClick: true));
    }

    private async Task TryCaptureAsync(InputPoint pt, bool immediate, bool doubleClick)
    {
        // One capture at a time. A dropped gesture is strictly better than a queue of UIA
        // reads piling up behind a slow app.
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return;

        try
        {
            // The cursor position is passed through: when the focused element has no
            // selection (a chat composer, say, while the selection is up in the transcript)
            // the capture layer falls back to the element under the pointer.
            var outcome = await _capture.CaptureAsync(pt.X, pt.Y, doubleClick).ConfigureAwait(true);

            // Misses are logged inside the capture layer, where the reason is known.
            if (!outcome.Success || outcome.Result is null) return;

            var result = outcome.Result;

            // Re-selecting the same text (or a stray second gesture on it) should not put
            // the pill back up.
            // Short window on purpose. This exists to stop one gesture firing twice - a
            // double-click raises both LBUTTONUP and LBUTTONDBLCLK - not to stop you
            // re-querying something. Two seconds was long enough to feel like the app was
            // ignoring a deliberate second attempt.
            var fp = result.Fingerprint;
            if (fp == _lastFingerprint && DateTime.UtcNow - _lastFingerprintAt < TimeSpan.FromMilliseconds(700))
                return;

            _lastFingerprint = fp;
            _lastFingerprintAt = DateTime.UtcNow;

            if (immediate || _cfg.InstantMode)
                ImmediateRequested?.Invoke(result, pt);
            else
                SelectionDetected?.Invoke(result, pt);
        }
        catch (Exception ex)
        {
            Log.Error("capture failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _inFlight, 0);
        }
    }

    public void Dispose() => _hooks.Dispose();
}
