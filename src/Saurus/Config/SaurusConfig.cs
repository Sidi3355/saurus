using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saurus.Config;

/// <summary>
/// User-editable configuration, JSON at %APPDATA%\Saurus\config.json.
/// Hand-editing is the supported path; there is no settings UI in v1.
/// Unknown properties are ignored, missing properties take the defaults below.
/// </summary>
public sealed class SaurusConfig
{
    /// <summary>Skip the pill and fire immediately when a selection is detected.</summary>
    public bool InstantMode { get; set; } = false;

    /// <summary>
    /// Run with no API key and no network call at all. Every other layer behaves normally
    /// and a canned answer streams into the popup, so the trigger, capture, focus, markdown,
    /// threading and rate limits can all be tested before a key exists.
    /// Also settable per-run with `--offline`.
    /// </summary>
    public bool OfflineMode { get; set; } = false;

    public string Model { get; set; } = "claude-haiku-4-5";

    /// <summary>Hard ceiling on tokens per response. Also the cost ceiling per call.</summary>
    public int MaxTokens { get; set; } = 300;

    /// <summary>Ceiling used when the user presses "Expand".</summary>
    public int ExpandedMaxTokens { get; set; } = 800;

    /// <summary>Target sentence count for the default answer. Fed to the prompt.</summary>
    public int ResponseSentences { get; set; } = 3;

    /// <summary>Drag distance before a left-button-up counts as a selection. 0 = use SM_CXDRAG/SM_CYDRAG.</summary>
    public int DragThresholdPx { get; set; } = 0;

    /// <summary>
    /// How long the pill stays on screen before giving up, ms. The countdown is suspended
    /// while the cursor is over the pill and restarts when it leaves.
    /// </summary>
    public int PillTimeoutMs { get; set; } = 6000;

    /// <summary>
    /// "panel" slides a dimmed side panel in from the right; "anchored" is the small popup
    /// pinned next to the cursor. Panel looks better, anchored is lower friction for a quick
    /// one-word lookup. Both are fully supported; this is the only switch between them.
    /// </summary>
    public string PopupStyle { get; set; } = "panel";

    public PanelConfig Panel { get; set; } = new();

    public sealed class PanelConfig
    {
        /// <summary>Fraction of monitor width the panel occupies. Clamped to sane pixel bounds.</summary>
        public double WidthFraction { get; set; } = 0.26;

        /// <summary>
        /// Gap between the panel and the screen edges, in DIPs. Applied on top of whatever
        /// taskbar chrome is already there, so the visible space above and below matches.
        /// </summary>
        public double InsetDip { get; set; } = 24;

        /// <summary>
        /// Backdrop dim, 0 (off) to 1 (black). Off by default: any dim reads as a modal
        /// takeover, which is the wrong signal for a tool you use twenty times an afternoon.
        /// At 0 the backdrop window is not created at all, and the panel falls back to a
        /// real hit test for click-outside dismissal.
        /// </summary>
        public double DimOpacity { get; set; } = 0.0;

        /// <summary>Slide + fade duration, ms. Lower this if the animation starts to annoy.</summary>
        public int SlideMs { get; set; } = 190;
    }

    /// <summary>Ctrl+click inside a live selection fires without needing to hit the pill.</summary>
    public bool CtrlClickFires { get; set; } = true;

    /// <summary>
    /// Whether to expand the selection to its enclosing paragraph and send a window of it
    /// as context.
    ///
    /// On, but narrow — see <see cref="LimitsConfig.MaxContextChars"/>. Context exists to
    /// disambiguate: "transformer" in a paper could be attention or electrical, and the
    /// surrounding sentence is the only thing that says which. A sentence either side does
    /// that job; a whole paragraph mostly just costs tokens.
    /// Set false to send nothing but the highlighted text.
    /// </summary>
    public bool CaptureSurroundingContext { get; set; } = true;

    /// <summary>
    /// Process names (no .exe) where SAURUS never captures and never shows a pill.
    /// Matched case-insensitively.
    /// </summary>
    public string[] ProcessBlocklist { get; set; } =
    {
        "1Password", "KeePass", "KeePassXC", "Bitwarden", "Dashlane", "LastPass",
        "LogonUI", "consent", "CredentialUIBroker", "LsaIso", "SecHealthUI"
    };

    public UpdateConfig Update { get; set; } = new();

    public sealed class UpdateConfig
    {
        /// <summary>
        /// GitHub repository holding the releases, e.g. "https://github.com/you/saurus".
        /// Empty disables updating entirely, which is the right default: a build that has not
        /// been told where its releases live should not be reaching out to guess.
        /// </summary>
        public string RepositoryUrl { get; set; } = "";

        /// <summary>Include pre-releases. Useful for testing an update before friends get it.</summary>
        public bool AllowPrerelease { get; set; } = false;

        public bool CheckOnStartup { get; set; } = true;

        /// <summary>How often to re-check while running. Zero checks only at startup.</summary>
        public int CheckEveryHours { get; set; } = 6;
    }

    public LimitsConfig Limits { get; set; } = new();
    public PricingConfig Pricing { get; set; } = new();
    public ThreadingConfig Threading { get; set; } = new();

    /// <summary>Write full selection/context text to the local log. Off by default; hashes only.</summary>
    public bool LogSelections { get; set; } = false;

    /// <summary>
    /// Small always-on-top badge showing today's usage. Click it to browse past queries.
    /// The tray icon carries the same information; this is for when the tray is out of sight.
    /// </summary>
    public bool ShowStatusBadge { get; set; } = true;

    /// <summary>
    /// Badge position in physical pixels, written back when you drag it.
    /// Null means "bottom-right of the primary monitor's work area".
    /// </summary>
    public int? BadgeX { get; set; }
    public int? BadgeY { get; set; }

    public sealed class LimitsConfig
    {
        /// <summary>
        /// Hard truncation of the selection before it is sent. Characters.
        ///
        /// 250 is one or two sentences, which is the scope the product is designed for: a
        /// term or a phrase, explained in place. Anything longer is a summarisation request
        /// wearing a disguise, and this is not the tool for that.
        /// </summary>
        public int MaxSelectionChars { get; set; } = 250;

        /// <summary>
        /// Hard truncation of the surrounding context before it is sent. Characters.
        ///
        /// 400 is roughly a sentence either side of the selection, which is what
        /// disambiguation actually needs. The original 1200 sent most of a paragraph and
        /// cost about 340 tokens on every single call for very little extra signal.
        /// </summary>
        public int MaxContextChars { get; set; } = 400;

        /// <summary>Minimum wall-clock gap between two API calls.</summary>
        public int MinIntervalMs { get; set; } = 1500;

        /// <summary>Rolling 60-second ceiling on API calls.</summary>
        public int MaxCallsPerMinute { get; set; } = 12;

        /// <summary>Rolling calendar-day (local time) token budget. Breach = hard stop.</summary>
        public long DailyTokenBudget { get; set; } = 200_000;

        /// <summary>
        /// Rolling 60-minute token budget. Catches a runaway loop in minutes instead of
        /// letting it eat the whole day allowance in one burst. Breach = hard stop.
        /// </summary>
        public long HourlyTokenBudget { get; set; } = 40_000;

        /// <summary>Whole-request timeout including streaming.</summary>
        public int RequestTimeoutMs { get; set; } = 20_000;

        /// <summary>Identical selection+context inside this window returns the cached answer.</summary>
        public int CacheTtlMinutes { get; set; } = 10;

        /// <summary>Circuit breaker: this many calls inside the window auto-pauses the app.</summary>
        public int CircuitBreakerCalls { get; set; } = 8;

        public int CircuitBreakerWindowSeconds { get; set; } = 20;
    }

    /// <summary>Used only to render a dollar figure in the tray. Token counts are the source of truth.</summary>
    public sealed class PricingConfig
    {
        public double InputPerMTok { get; set; } = 1.00;
        public double OutputPerMTok { get; set; } = 5.00;
    }

    public sealed class ThreadingConfig
    {
        /// <summary>A query joins the most recent matching thread inside this window.</summary>
        public int WindowMinutes { get; set; } = 20;

        /// <summary>At most this many prior exchanges are replayed as context.</summary>
        public int MaxExchanges { get; set; } = 3;

        /// <summary>
        /// Replayed answers are truncated to this many characters. Prior exchanges exist to
        /// remind the model what has already been covered, not to be re-read in full, and
        /// every character here is paid for on every subsequent query in the thread.
        /// </summary>
        public int MaxContextAnswerChars { get; set; } = 500;
    }

    // ---------------------------------------------------------------- load / save

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Saurus");

    public static string ConfigPath => Path.Combine(Dir, "config.json");

    /// <summary>
    /// Loads config, writing a fully-populated default file on first run so the user has
    /// something to edit. A malformed file falls back to defaults rather than failing to start.
    /// </summary>
    public static SaurusConfig Load()
    {
        Directory.CreateDirectory(Dir);

        if (!File.Exists(ConfigPath))
        {
            var fresh = new SaurusConfig();
            fresh.Save();
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<SaurusConfig>(json, JsonOpts) ?? new SaurusConfig();
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"config.json unreadable ({ex.GetType().Name}: {ex.Message}); using defaults");
            return new SaurusConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public int EffectiveDragThreshold()
    {
        if (DragThresholdPx > 0) return DragThresholdPx;
        var sys = Math.Max(Interop.Win32.GetSystemMetrics(Interop.Win32.SM_CXDRAG),
                           Interop.Win32.GetSystemMetrics(Interop.Win32.SM_CYDRAG));
        return sys > 0 ? sys : 4;
    }

    public bool IsBlocked(string? processName) =>
        !string.IsNullOrEmpty(processName) &&
        ProcessBlocklist.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
}
