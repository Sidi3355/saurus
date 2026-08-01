using Saurus.Core;
using Saurus.Interop;

namespace Saurus.Trigger;

public readonly record struct InputPoint(int X, int Y);

/// <summary>
/// Low-level mouse + keyboard hooks on a dedicated thread with its own message pump.
///
/// Contract for the hook callbacks: do the absolute minimum (read the struct, raise a
/// synchronous in-memory event, return). They must never block, allocate heavily, touch
/// the UI, or do I/O — they run on the input path and Windows will silently unhook us if
/// we exceed LowLevelHooksTimeout. Everything expensive is marshalled off by the
/// subscriber (TriggerService posts to the WPF dispatcher).
///
/// A watchdog re-installs the hooks if Windows drops them.
/// </summary>
public sealed class InputHooks : IDisposable
{
    // Delegates are fields, not locals: if they are collected the callback address dies
    // and the process faults on the next input event.
    private readonly Win32.HookProc _mouseProc;
    private readonly Win32.HookProc _keyboardProc;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;

    private Thread? _thread;
    private uint _threadId;
    private volatile bool _running;
    private Timer? _watchdog;

    /// <summary>Left button pressed. Fired for every press, including the one that starts a drag.</summary>
    public event Action<InputPoint>? LeftDown;

    /// <summary>Left button released, with the distance dragged since the matching press.</summary>
    public event Action<InputPoint, int>? LeftUp;

    /// <summary>Left double-click.</summary>
    public event Action<InputPoint>? DoubleClick;

    /// <summary>Escape pressed anywhere. Needed because our windows do not hold focus.</summary>
    public event Action? EscapePressed;

    private InputPoint _downPoint;
    private bool _downSeen;

    public InputHooks()
    {
        _mouseProc = MouseCallback;
        _keyboardProc = KeyboardCallback;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _thread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = "saurus-hooks",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Windows silently unhooks callbacks that exceed LowLevelHooksTimeout. Re-arm.
        _watchdog = new Timer(_ => Reinstall(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    private void PumpLoop()
    {
        _threadId = Win32.GetCurrentThreadId();
        Install();

        // Low-level hooks are dispatched to the installing thread, which must pump messages.
        while (_running && Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Uninstall();
    }

    private void Install()
    {
        var hMod = Win32.GetModuleHandle(null);
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, hMod, 0);
            if (_mouseHook == IntPtr.Zero) Log.Error("failed to install WH_MOUSE_LL");
        }
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyboardProc, hMod, 0);
            if (_keyboardHook == IntPtr.Zero) Log.Error("failed to install WH_KEYBOARD_LL");
        }
    }

    private void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyboardHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
    }

    /// <summary>
    /// Re-registers dropped hooks. Cheap when nothing is wrong: SetWindowsHookEx is only
    /// called for a handle that is actually zero.
    /// </summary>
    private void Reinstall()
    {
        if (!_running) return;
        if (_mouseHook != IntPtr.Zero && _keyboardHook != IntPtr.Zero) return;
        Log.Warn("hook dropped by the OS; reinstalling");
        Install();
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        try
        {
            var msg = (int)wParam;
            if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_LBUTTONUP or Win32.WM_LBUTTONDBLCLK)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                var pt = new InputPoint(data.pt.X, data.pt.Y);

                switch (msg)
                {
                    case Win32.WM_LBUTTONDOWN:
                        _downPoint = pt;
                        _downSeen = true;
                        LeftDown?.Invoke(pt);
                        break;

                    case Win32.WM_LBUTTONUP:
                        var dx = pt.X - _downPoint.X;
                        var dy = pt.Y - _downPoint.Y;
                        var dist = _downSeen ? (int)Math.Sqrt((double)dx * dx + (double)dy * dy) : 0;
                        _downSeen = false;
                        LeftUp?.Invoke(pt, dist);
                        break;

                    case Win32.WM_LBUTTONDBLCLK:
                        DoubleClick?.Invoke(pt);
                        break;
                }
            }
        }
        catch
        {
            // An exception escaping a hook callback is fatal to the input path. Swallow.
        }

        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Filters to Escape only. Nothing else is read, retained, or logged — this hook exists
    /// solely because our non-activating windows cannot receive a normal key event.
    /// </summary>
    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        try
        {
            var msg = (int)wParam;
            if (msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if (data.vkCode == Win32.VK_ESCAPE) EscapePressed?.Invoke();
            }
        }
        catch { }

        return Win32.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _running = false;
        _watchdog?.Dispose();
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
        Uninstall();
    }
}
