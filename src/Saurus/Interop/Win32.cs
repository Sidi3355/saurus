using System.Runtime.InteropServices;
using System.Text;

namespace Saurus.Interop;

/// <summary>P/Invoke surface. Kept in one file so the native boundary is auditable.</summary>
internal static class Win32
{
    // ---- window styles -------------------------------------------------------
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TRANSPARENT = 0x00000020;

    // ---- hooks ---------------------------------------------------------------
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;

    public const int VK_ESCAPE = 0x1B;
    public const int VK_CONTROL = 0x11;

    // ---- system metrics ------------------------------------------------------
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_CXVIRTUALSCREEN = 78;

    /// <summary>
    /// An x coordinate beyond every monitor, used to park a window that is kept realised but
    /// out of sight. Cheaper and far less glitchy than repeatedly showing and hiding it.
    /// </summary>
    public static int OffscreenParkX()
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        return (width > 0 ? left + width : 4000) + 200;
    }

    // ---- ShowWindow ----------------------------------------------------------
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- window / cursor -----------------------------------------------------
    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    // ---- positioning ---------------------------------------------------------
    // Windows are positioned with SetWindowPos in *physical pixels* rather than via
    // WPF's Left/Top, which are device-independent units anchored to the primary
    // monitor's scale factor and therefore wrong on a mixed-DPI setup.

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                           int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ---- layered windows -----------------------------------------------------
    // The dim backdrop is a plain black window whose alpha is set here rather than a WPF
    // AllowsTransparency window. Per-pixel WPF transparency forces software rendering for
    // that window; at full-screen size the fade visibly stutters. A layered window's alpha
    // is composited by DWM on the GPU and costs nothing.

    public const int WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    public static void MakeLayered(IntPtr hwnd)
    {
        var ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
    }

    /// <summary>Sets whole-window opacity, 0..1, on a layered window.</summary>
    public static void SetLayeredOpacity(IntPtr hwnd, double opacity)
    {
        var a = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
        SetLayeredWindowAttributes(hwnd, 0, a, LWA_ALPHA);
    }

    /// <summary>Win10 1607+. Returns the DPI of the monitor the window is on.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    public static double ScaleFor(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; }
    }

    public static bool RectContains(RECT r, int x, int y) =>
        x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // ---- DWM ------------------------------------------------------------------
    // Rounded corners and a system shadow on an *opaque* window. This is why the side panel
    // does not use WPF's AllowsTransparency: per-pixel alpha would force software rendering
    // of a full-height window every frame of the slide, whereas DWM rounds and shadows an
    // opaque window on the GPU for free.

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11 only; silently does nothing on Windows 10.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* pre-Win11, or dwmapi unavailable */ }
    }

    [DllImport("kernel32.dll")]
    public static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    public const int ATTACH_PARENT_PROCESS = -1;

    // ---- helpers -------------------------------------------------------------

    public static string GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var sb = new StringBuilder(512);
        var n = GetWindowTextW(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : string.Empty;
    }

    public static bool IsCtrlDown() => (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

    /// <summary>Work area (excludes taskbar) of the monitor containing the point, in physical pixels.</summary>
    public static RECT WorkAreaForPoint(POINT pt)
    {
        var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref mi)) return mi.rcWork;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>
    /// Full bounds of the monitor containing the point, taskbar included. The dim backdrop
    /// uses this rather than the work area: leaving a bright taskbar strip along the bottom
    /// breaks the effect entirely.
    /// </summary>
    public static RECT MonitorRectForPoint(POINT pt)
    {
        var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfoW(mon, ref mi)) return mi.rcMonitor;
        return new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    /// <summary>Adds WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW so the window never steals focus.</summary>
    public static void MakeNonActivating(IntPtr hwnd)
    {
        var ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Drops WS_EX_NOACTIVATE so the window can accept keyboard input.
    /// Only ever called after capture has completed — see PopupWindow.
    /// </summary>
    public static void MakeActivating(IntPtr hwnd)
    {
        var ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
    }
}
