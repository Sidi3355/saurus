using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Saurus.Config;
using Saurus.Guardrails;

namespace Saurus.Ui;

/// <summary>
/// Tray presence. Two jobs: surface today's spend somewhere trivially accessible, and
/// provide a visible kill switch. A tool that fires automatically needs a stop button the
/// user can reach without editing a config file.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly GuardrailChain _guards;
    private readonly SaurusConfig _cfg;
    private readonly ToolStripMenuItem _usageItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly System.Windows.Forms.Timer _refresh;

    public event Action? QuitRequested;
    public event Action? HistoryRequested;
    public event Action? UpdateRequested;

    public TrayIcon(GuardrailChain guards, SaurusConfig cfg)
    {
        _guards = guards;
        _cfg = cfg;

        _usageItem = new ToolStripMenuItem("today: —") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());

        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutoStart())
        {
            CheckOnClick = false,
            Checked = Core.AutoStart.IsEnabled,
            // Under `dotnet run` the process is dotnet.exe, so there is nothing useful to
            // register. Say why rather than silently doing nothing.
            Enabled = Core.AutoStart.IsSupported,
            ToolTipText = Core.AutoStart.IsSupported
                ? "Adds a per-user entry under HKCU Run. You can also disable it from Task Manager's Startup tab."
                : "Unavailable when launched via `dotnet run` - publish first, then run Saurus.exe."
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_usageItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("History…", null, (_, _) => HistoryRequested?.Invoke()));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripMenuItem("Add Start Menu + desktop shortcut", null,
            (_, _) => AddShortcuts())
        {
            Enabled = Core.AutoStart.IsSupported,
            ToolTipText = "A tray icon only exists while the app runs. This gives you " +
                          "something to click when it is not running."
        });
        menu.Items.Add(new ToolStripMenuItem("Open config folder", null,
            (_, _) => OpenFolder(SaurusConfig.Dir)));
        menu.Items.Add(new ToolStripMenuItem("Open log", null,
            (_, _) => OpenFile(System.IO.Path.Combine(SaurusConfig.Dir, "saurus.log"))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Check for updates", null, (_, _) => UpdateRequested?.Invoke()));
        menu.Items.Add(new ToolStripMenuItem("Quit SAURUS", null, (_, _) => QuitRequested?.Invoke()));

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(active: true),
            Visible = true,
            Text = "SAURUS",
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Refresh(); };

        _guards.StateChanged += () => TryOnUiThread(Refresh);

        _refresh = new System.Windows.Forms.Timer { Interval = 15_000 };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        Refresh();
    }

    private void AddShortcuts()
    {
        var made = Core.Shortcuts.Install();
        Notify(made.Count > 0 ? "Shortcuts created" : "Could not create shortcuts",
               made.Count > 0
                   ? "SAURUS is now in the Start Menu and on your desktop."
                   : "See %APPDATA%\\Saurus\\saurus.log for why.");
    }

    private void ToggleAutoStart()
    {
        Core.AutoStart.Toggle();
        _autoStartItem.Checked = Core.AutoStart.IsEnabled;
    }

    private void TogglePause()
    {
        if (_guards.Paused) _guards.Resume();
        else _guards.Pause("paused from the tray");
        Refresh();
    }

    public void Refresh()
    {
        try
        {
            var u = _guards.Usage();
            var pct = _cfg.Limits.DailyTokenBudget > 0
                ? (double)u.DayTotalTokens / _cfg.Limits.DailyTokenBudget * 100
                : 0;

            _usageItem.Text =
                $"today: {u.DayTotalTokens:N0} tok  ·  ${u.DayCostUsd:F3}  ·  {u.DayCalls} calls";

            // Tray tooltips are capped at 127 chars.
            var state = _guards.Paused ? "PAUSED" : _cfg.OfflineMode ? "OFFLINE" : "active";
            _icon.Text = Trim(
                $"SAURUS ({state})\n{u.DayTotalTokens:N0}/{_cfg.Limits.DailyTokenBudget:N0} tok " +
                $"({pct:F0}%)  ${u.DayCostUsd:F3}\nin {u.DayInputTokens:N0} / out {u.DayOutputTokens:N0}");

            _pauseItem.Text = _guards.Paused ? "Resume" : "Pause";
            _icon.Icon = BuildIcon(active: !_guards.Paused);
        }
        catch (Exception ex)
        {
            Core.Log.Error("tray refresh failed", ex);
        }
    }

    public void Notify(string title, string message) =>
        TryOnUiThread(() =>
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        });

    private static string Trim(string s) => s.Length <= 127 ? s : s[..127];

    private static void TryOnUiThread(Action a)
    {
        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp is null) { a(); return; }
        if (disp.CheckAccess()) a(); else disp.InvokeAsync(a);
    }

    /// <summary>Drawn at runtime so there is no binary asset to ship or keep in sync.</summary>
    private static Icon BuildIcon(bool active)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var fill = active ? Color.FromArgb(0x4A, 0x9E, 0xFF) : Color.FromArgb(0x6A, 0x6F, 0x78);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 14, 14);

            using var font = new Font("Segoe UI", 8f, System.Drawing.FontStyle.Bold,
                                      GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(0x14, 0x16, 0x1A));
            g.DrawString("S", font, text, 4.5f, 3.5f);
        }

        // Icon.FromHandle borrows the HICON; clone so the bitmap can be disposed safely.
        var handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Core.Log.Error("could not open folder", ex); }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Core.Log.Error("could not open file", ex); }
    }

    public void Dispose()
    {
        _refresh.Stop();
        _refresh.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);
    }
}
