using System.Windows;
using System.Windows.Threading;
using Saurus.Api;
using Saurus.Capture;
using Saurus.Config;
using Saurus.Core;
using Saurus.Guardrails;
using Saurus.Interop;
using Saurus.Orchestration;
using Saurus.Storage;
using Saurus.Trigger;
using Saurus.Ui;
using Saurus.Update;
using Velopack;

namespace Saurus;

public static class Program
{
    private static Mutex? _instanceMutex;

    private const string InstanceMutexName = @"Local\Saurus.SingleInstance";

    /// <summary>
    /// Takes the single-instance lock for a command-line operation.
    ///
    /// SQLite tolerates multiple readers, but this database is opened read-write by every
    /// path that touches it, and two processes running schema migration and WAL checkpoints
    /// against the same file will corrupt it. A diagnostic that destroys the data it was
    /// asked to inspect is worse than no diagnostic, so every CLI command that opens the
    /// database refuses to run while the app holds it.
    /// </summary>
    private static bool TryTakeInstanceLock(out Mutex? held)
    {
        held = new Mutex(true, InstanceMutexName, out var acquired);
        if (acquired) return true;

        held.Dispose();
        held = null;
        return false;
    }

    private static int RefuseWhileRunning(string what)
    {
        WriteLine($"SAURUS is running, so {what} would corrupt the database.");
        WriteLine("Quit it from the tray icon (or the badge's right-click menu) and try again.");
        return 1;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must be the very first thing. Velopack re-runs the executable with hook arguments
        // during install, update and uninstall; those runs have to be handled and exited
        // before any window, hook or database exists.
        VelopackApp.Build().Run();

        var dir = SaurusConfig.Dir;
        Log.Init(dir);

        // ---- CLI modes: these run and exit, they never start the tray app ----
        if (args.Any(a => a is "--help" or "-h" or "/?")) return PrintHelp();
        if (args.Contains("--set-key")) return SetKey(dir);
        if (args.Contains("--clear-key")) { ApiKeyStore.Delete(dir); WriteLine("API key deleted."); return 0; }
        if (args.Contains("--stats")) return PrintStats(dir);
        if (args.Contains("--clear-history")) return ClearHistory(dir);
        if (args.Contains("--install-shortcuts")) return InstallShortcuts();
        if (args.Contains("--uninstall-shortcuts")) { EnsureConsole(); Shortcuts.Uninstall(); WriteLine("Shortcuts removed."); return 0; }

        // A second instance would install a second pair of global hooks and double every
        // capture. Refuse rather than misbehave.
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirst);
        if (!isFirst)
        {
            // Logged as well as shown: a silent refusal here looks identical to a crash when
            // you are staring at a log wondering why the new build did not take effect.
            Log.Warn("another instance is already running; this one is exiting");
            MessageBox.Show("SAURUS is already running - quit it from the tray icon first.", "SAURUS",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        var cfg = SaurusConfig.Load();
        if (args.Contains("--offline")) cfg.OfflineMode = true;

        Log.Info($"config loaded: model={cfg.Model} instantMode={cfg.InstantMode} " +
                 $"offlineMode={cfg.OfflineMode} dailyBudget={cfg.Limits.DailyTokenBudget}");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Control templates live in XAML because they have to: WPF's stock templates ignore
        // a Background you set in code, so a dark theme means replacing them outright.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Saurus;component/Ui/Theme.xaml", UriKind.Absolute)
        });

        Database? db = null;
        UiaCaptureService? capture = null;
        TriggerService? trigger = null;
        ExplainOrchestrator? orchestrator = null;
        TrayIcon? tray = null;
        StatusBadge? badge = null;
        HistoryWindow? history = null;
        SidePanel? panel = null;
        DimOverlay? overlay = null;
        PopupWindow? popup = null;
        IExplainClient? api = null;
        UpdateService? updates = null;
        string? key = null;

        try
        {
            db = new Database(dir);
            var guards = new GuardrailChain(cfg, db);
            var threads = new ThreadResolver(db, cfg);

            if (cfg.OfflineMode)
            {
                // No key is loaded and none is needed. Nothing is sent anywhere.
                api = new OfflineClient();
                Log.Info("OFFLINE MODE: no API key loaded, no network calls will be made");
            }
            else
            {
                var live = new AnthropicClient();
                api = live;

                key = ApiKeyStore.Load(dir);

                if (key is null)
                {
                    // First run, or a fresh machine. Ask rather than leaving a tray icon that
                    // silently does nothing — this is the only path a new user has.
                    Log.Info("no API key found; showing first-run setup");
                    var setup = new SetupWindow(dir);
                    setup.ShowDialog();
                    if (setup.KeySaved) key = ApiKeyStore.Load(dir);
                }

                if (key is null)
                {
                    Log.Warn("no API key configured; queries will fail with a setup message");
                }
                else
                {
                    if (!ApiKeyStore.LooksPlausible(key))
                        Log.Warn("stored API key does not look like an Anthropic key");
                    Log.RegisterSecret(key);      // so it is scrubbed if it ever leaks into a log line
                    live.SetApiKey(key);
                    _ = live.PrewarmAsync();      // TLS handshake off the critical path
                }
            }

            // Display-only prices, so history rows can put a cost beside a token count.
            UsagePricing.Configure(cfg);

            capture = new UiaCaptureService(cfg);
            trigger = new TriggerService(cfg, capture, app.Dispatcher);

            // The answer surface is the one piece the orchestrator does not construct: panel
            // and popup are interchangeable behind IAnswerSurface, chosen here and nowhere
            // else. `openHistory` is whichever of the two knows how to show past queries.
            var usePanel = !string.Equals(cfg.PopupStyle, "anchored", StringComparison.OrdinalIgnoreCase);
            IAnswerSurface surface;
            Action openHistory;

            if (usePanel)
            {
                // With the dim off there is no point creating a full-screen click-swallowing
                // window: it would eat the first click you aimed at the app behind it and
                // give nothing back visually. The panel falls back to a real hit test.
                var wantDim = cfg.Panel.DimOpacity > 0.001;
                overlay = wantDim ? new DimOverlay() : null;
                panel = new SidePanel(cfg, db, guards, overlay);
                // Pay for the window's first layout and render now, off-screen, instead of
                // on the first frame of the first slide.
                panel.Prewarm();
                surface = panel;
                openHistory = () =>
                {
                    Win32.GetCursorPos(out var pt);
                    panel.ShowHistoryAt(pt.X, pt.Y);
                };
            }
            else
            {
                popup = new PopupWindow();
                history = new HistoryWindow(db, cfg, guards);
                surface = popup;
                openHistory = () => history.ShowHistory();
            }

            Log.Info($"answer surface: {(usePanel ? "side panel" : "anchored popup")}");

            orchestrator = new ExplainOrchestrator(
                cfg, trigger, guards, api, db, threads, app.Dispatcher, surface);

            tray = new TrayIcon(guards, cfg);
            tray.QuitRequested += () => app.Dispatcher.InvokeAsync(() => app.Shutdown());
            tray.HistoryRequested += () => app.Dispatcher.InvokeAsync(openHistory);

            if (cfg.ShowStatusBadge)
            {
                badge = new StatusBadge(cfg, guards);
                badge.Clicked += openHistory;
                badge.QuitRequested += () => app.Dispatcher.InvokeAsync(() => app.Shutdown());
                badge.ShowBadge();

                if (panel is not null)
                {
                    // The badge is always where the panel comes from and goes back to,
                    // whether it was opened by clicking the badge or by clicking a pill. It
                    // steps aside while the panel stands in for it.
                    panel.MorphOriginProvider = () => badge.VisibleBounds;
                    panel.Opening += () => badge.Conceal();
                    panel.Dismissed += () => badge.Reveal();
                }
            }

            if (cfg.OfflineMode)
                tray.Notify("SAURUS is running offline",
                            "Canned answers, no API calls. Highlight some text to test.");
            else if (key is null)
                tray.Notify("SAURUS needs an API key",
                            "Run: dotnet run -- --set-key   (or --offline to test without one)");

            updates = new UpdateService(cfg);
            updates.UpdateReady += version => app.Dispatcher.InvokeAsync(() =>
                tray?.Notify("SAURUS update ready",
                             $"Version {version} will be installed next time you start it."));
            updates.Start();
            tray.UpdateRequested += () => _ = CheckForUpdates(updates, tray);

            app.DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Error($"unhandled: {(e.ExceptionObject as Exception)?.Message ?? "unknown"}");

            trigger.Start();
            Log.Info("running");

            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("fatal during startup", ex);
            MessageBox.Show($"SAURUS failed to start: {ex.Message}\n\nSee %APPDATA%\\Saurus\\saurus.log",
                            "SAURUS", MessageBoxButton.OK, MessageBoxImage.Error);
            return 2;
        }
        finally
        {
            orchestrator?.Dispose();
            trigger?.Dispose();
            capture?.Dispose();
            badge?.Close();
            panel?.CloseAll();
            panel?.Close();
            overlay?.Close();
            popup?.Close();
            if (history is not null) { history.ForceClose = true; history.Close(); }
            tray?.Dispose();
            api?.Dispose();
            db?.Dispose();
            _instanceMutex?.Dispose();

            // Last thing before the process ends: a staged update is applied on the way out,
            // so quitting from the tray is all it takes to land on the new version.
            updates?.ApplyOnExit();
            updates?.Dispose();

            Log.Info("--- saurus stop ---");
        }
    }

    private static async Task CheckForUpdates(UpdateService updates, TrayIcon tray)
    {
        tray.Notify("Checking for updates", $"Currently on {updates.CurrentVersion}.");
        var version = await updates.CheckAsync();

        if (version is null && !updates.HasStagedUpdate)
            tray.Notify("No update available", $"You are on the latest version ({updates.CurrentVersion}).");
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI-thread exception must not take down a background utility the user has
        // forgotten is running.
        Log.Error("unhandled on dispatcher", e.Exception);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ CLI

    private static int SetKey(string dir)
    {
        EnsureConsole();

        // Preferred path: pass it through the environment so the key never lands in shell
        // history. Falls back to an interactive prompt.
        var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string? key;

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            WriteLine("Using ANTHROPIC_API_KEY from the environment.");
            key = fromEnv.Trim();
        }
        else
        {
            WriteLine("Paste your Anthropic API key (input is hidden), then press Enter:");
            key = ReadHidden();
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            WriteLine("No key entered. Nothing was written.");
            return 1;
        }

        if (!ApiKeyStore.LooksPlausible(key))
            WriteLine("Warning: that does not look like an Anthropic key (expected an sk-ant- prefix). Storing anyway.");

        ApiKeyStore.Save(dir, key);
        WriteLine($"Stored, DPAPI-encrypted for this Windows user, at {ApiKeyStore.PathFor(dir)}");
        WriteLine("It is never written in plaintext and never appears in the log.");
        return 0;
    }

    private static string? ReadHidden()
    {
        try
        {
            var buf = new System.Text.StringBuilder();
            while (true)
            {
                var k = Console.ReadKey(intercept: true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace)
                {
                    if (buf.Length > 0) buf.Length--;
                    continue;
                }
                if (!char.IsControl(k.KeyChar)) buf.Append(k.KeyChar);
            }
            return buf.ToString();
        }
        catch (InvalidOperationException)
        {
            // No usable console (redirected stdin) — fall back to a plain read.
            return Console.ReadLine();
        }
    }

    /// <summary>
    /// Row counts straight from SQLite, without the UI in the way. When something looks
    /// missing this says whether it was never stored or is simply not being displayed —
    /// two very different bugs.
    /// </summary>
    private static int PrintStats(string dir)
    {
        EnsureConsole();

        if (!TryTakeInstanceLock(out var guard)) return RefuseWhileRunning("reading it");
        using var _ = guard;

        try
        {
            using var db = new Database(dir);

            WriteLine($"database    : {System.IO.Path.Combine(dir, "saurus.db")}");
            WriteLine($"threads     : {db.Scalar<long>("SELECT COUNT(*) FROM threads")}");
            WriteLine($"queries     : {db.Scalar<long>("SELECT COUNT(*) FROM queries")}");
            WriteLine($"  roots     : {db.Scalar<long>("SELECT COUNT(*) FROM queries WHERE root_id = id")}");
            WriteLine($"  followups : {db.Scalar<long>("SELECT COUNT(*) FROM queries WHERE root_id <> id")}");
            WriteLine($"  orphaned  : {db.Scalar<long>("SELECT COUNT(*) FROM queries WHERE root_id IS NULL")}");
            WriteLine($"cache rows  : {db.Scalar<long>("SELECT COUNT(*) FROM cache")}");
            WriteLine("");
            WriteLine($"ledger rows : {db.Scalar<long>("SELECT COUNT(*) FROM usage_ledger")}");
            WriteLine($"  settled   : {db.Scalar<long>("SELECT COUNT(*) FROM usage_ledger WHERE settled = 1")}");
            WriteLine($"  input tok : {db.Scalar<long>("SELECT COALESCE(SUM(input_tokens),0) FROM usage_ledger")}");
            WriteLine($"  output tok: {db.Scalar<long>("SELECT COALESCE(SUM(output_tokens),0) FROM usage_ledger")}");
            WriteLine("");
            WriteLine("per day:");
            foreach (var line in db.Query(
                "SELECT day_local, COUNT(*), COALESCE(SUM(input_tokens),0), COALESCE(SUM(output_tokens),0) " +
                "FROM usage_ledger GROUP BY day_local ORDER BY day_local",
                r => $"  {r.GetString(0)}  {r.GetInt32(1),4} calls  {r.GetInt64(2),8} in  {r.GetInt64(3),7} out"))
                WriteLine(line);

            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"Could not read the database: {ex.Message}");
            return 1;
        }
    }

    private static int ClearHistory(string dir)
    {
        EnsureConsole();

        if (!TryTakeInstanceLock(out var guard)) return RefuseWhileRunning("clearing history");
        using var _ = guard;

        try
        {
            using var db = new Database(dir);
            db.ClearHistory();
            WriteLine("History cleared: queries, threads and cached answers.");
            WriteLine("Token usage ledger kept - that is what the daily budget is enforced against.");
            return 0;
        }
        catch (Exception ex)
        {
            WriteLine($"Could not clear history: {ex.Message}");
            WriteLine("If SAURUS is running, quit it from the tray first.");
            return 1;
        }
    }

    private static int InstallShortcuts()
    {
        EnsureConsole();

        if (!AutoStart.IsSupported)
        {
            WriteLine("Run this against the published exe, not `dotnet run` - the process is dotnet.exe.");
            WriteLine("  dotnet publish src/Saurus -c Release -o dist");
            WriteLine("  dist\\Saurus.exe --install-shortcuts");
            return 1;
        }

        var made = Shortcuts.Install();
        if (made.Count == 0)
        {
            WriteLine("Could not create shortcuts. See %APPDATA%\\Saurus\\saurus.log.");
            return 1;
        }

        foreach (var path in made) WriteLine($"created: {path}");
        return 0;
    }

    private static int PrintHelp()
    {
        EnsureConsole();
        WriteLine("""
            SAURUS — highlight anything, get a dense technical explanation.

              dotnet run                    start the app (tray icon, no window)
              dotnet run -- --offline       start with NO key and NO network calls; the popup
                                            streams a canned answer so the trigger, capture,
                                            focus, markdown and rate limits can all be tested
              dotnet run -- --set-key       store your Anthropic API key (DPAPI, current user)
              dotnet run -- --clear-key     delete the stored key
              dotnet run -- --help          this text

              Saurus.exe --install-shortcuts    Start Menu + desktop shortcut, so there is
                                                something to click when it is NOT running
              Saurus.exe --uninstall-shortcuts  remove them again

            A tray icon exists only while the process does. For a persistent entry point use
            the shortcuts above, and turn on "Start with Windows" in the tray menu.

            Config, key, database and log all live in %APPDATA%\Saurus.
            Edit config.json by hand; there is no settings UI.
            """);
        return 0;
    }

    /// <summary>
    /// This is a WinExe, so it has no console of its own. Attach to the terminal that
    /// launched it, or allocate one, so the CLI modes can talk to the user.
    /// </summary>
    private static void EnsureConsole()
    {
        if (!Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS)) Win32.AllocConsole();
    }

    private static void WriteLine(string s)
    {
        try { Console.WriteLine(s); } catch { }
    }
}
