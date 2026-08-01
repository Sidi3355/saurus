using Saurus.Config;
using Saurus.Core;
using Velopack;
using Velopack.Sources;

namespace Saurus.Update;

/// <summary>
/// Checks GitHub for a newer release, downloads it quietly, and applies it when the app next
/// exits.
///
/// Deliberately never restarts on its own. This is a background utility with global input
/// hooks; a window vanishing and reappearing mid-sentence because an update landed would be
/// far more disruptive than waiting until the user quits anyway. The update is staged, the
/// tray says so, and it takes effect on the next launch.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly SaurusConfig _cfg;
    private UpdateManager? _manager;
    private UpdateInfo? _staged;
    private Timer? _timer;
    private int _busy;

    /// <summary>Raised when an update has been downloaded and is waiting for a restart.</summary>
    public event Action<string>? UpdateReady;

    public bool HasStagedUpdate => _staged is not null;

    public string CurrentVersion
    {
        get
        {
            try { return _manager?.CurrentVersion?.ToString() ?? LocalVersion(); }
            catch { return LocalVersion(); }
        }
    }

    private static string LocalVersion() =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public UpdateService(SaurusConfig cfg) => _cfg = cfg;

    /// <summary>
    /// Returns false when updating is not available: no repository configured, or running
    /// from a plain build rather than an installed copy. Both are normal during development
    /// and neither is an error.
    /// </summary>
    public bool Start()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Update.RepositoryUrl))
        {
            Log.Info("updates disabled: no update.repositoryUrl configured");
            return false;
        }

        try
        {
            var source = new GithubSource(_cfg.Update.RepositoryUrl.Trim(), null,
                                          _cfg.Update.AllowPrerelease);
            _manager = new UpdateManager(source);

            if (!_manager.IsInstalled)
            {
                Log.Info("updates disabled: not running from an installed copy");
                _manager = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("could not initialise the updater", ex);
            return false;
        }

        if (_cfg.Update.CheckOnStartup)
        {
            // Delayed: startup is the one moment latency is visible, and an update that
            // lands thirty seconds later is no less useful.
            _timer = new Timer(_ => _ = CheckAsync(), null,
                               TimeSpan.FromSeconds(30),
                               _cfg.Update.CheckEveryHours > 0
                                   ? TimeSpan.FromHours(_cfg.Update.CheckEveryHours)
                                   : Timeout.InfiniteTimeSpan);
        }

        Log.Info($"updates enabled from {_cfg.Update.RepositoryUrl} (current {CurrentVersion})");
        return true;
    }

    /// <summary>Checks and downloads. Safe to call from the tray at any time.</summary>
    public async Task<string?> CheckAsync()
    {
        if (_manager is null) return null;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return null;

        try
        {
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                Log.Info($"update check: already on the latest version ({CurrentVersion})");
                return null;
            }

            var version = update.TargetFullRelease.Version.ToString();
            Log.Info($"update available: {version}; downloading");

            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            _staged = update;
            Log.Info($"update {version} downloaded and staged for next restart");
            UpdateReady?.Invoke(version);
            return version;
        }
        catch (Exception ex)
        {
            // A failed update check must never be more than a log line. The app works fine
            // on the version it already has.
            Log.Warn($"update check failed: {ex.Message}");
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Applies a staged update as the process exits. Called from the shutdown path, so
    /// quitting from the tray is all it takes to land on the new version.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _staged is null) return;

        try
        {
            Log.Info($"applying update {_staged.TargetFullRelease.Version} on exit");
            _manager.WaitExitThenApplyUpdates(_staged);
        }
        catch (Exception ex)
        {
            Log.Error("could not apply the staged update", ex);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
