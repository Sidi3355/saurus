# SAURUS

Highlight text anywhere on screen. A small pill appears next to the cursor. Click it and
get a dense technical explanation of what you highlighted, in a popup, without leaving the
app you were in. Ask follow-ups in the popup. That's the whole product.

The thing being optimised is friction and latency. The competition is alt-tabbing to a
chatbot and pasting.

---

## Setup

### 1. Install the .NET 10 SDK

**This machine does not currently have any .NET SDK.** `dotnet` is not on PATH.

```bash
winget install Microsoft.DotNet.SDK.10
```

Or download the x64 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>.

Open a new terminal afterwards so PATH is picked up, then confirm:

```bash
dotnet --version
```

The project targets `net10.0-windows`, so the SDK alone is enough — no separate runtime
install. (An SDK 10 can *build* a net8.0 project, but running one would additionally need
the .NET 8 runtime, because roll-forward does not cross major versions by default.)

### 2. (Optional) Try it with no API key first

```bash
dotnet run --project src/Saurus -- --offline
```

No key is loaded and no network call is made — not even a DNS lookup. Everything else runs
for real: the hooks fire, UI Automation reads the selection, the pill appears, the popup
streams a canned answer through the same renderer, follow-ups work, and every guardrail
except the token budget is exercised.

The canned answer echoes back the exact payload that *would* have been sent, so it doubles
as a capture diagnostic — the fastest way to see what UIA actually grabbed out of a given
app. See [Testing offline](#testing-offline) for what to try.

Set `"offlineMode": true` in `config.json` to make it the default.

### 3. Store your API key

The key is **never** read from a file in the repo, an environment variable at runtime, or a
command-line argument. It is entered once and stored encrypted.

```bash
dotnet run --project src/Saurus -- --set-key
```

Paste your Anthropic API key at the prompt (input is hidden). If `ANTHROPIC_API_KEY` is set
in the environment, that value is used instead and no prompt appears — useful if you'd
rather not type it, since a pasted key can end up in terminal scrollback.

The key is encrypted with Windows DPAPI scoped to your user account and written to
`%APPDATA%\Saurus\apikey.dat`.

**What that protects against:** another Windows user on this machine reading it, and anyone
who pulls the drive and mounts it offline. **What it does not protect against:** any process
running as you — which can call `CryptUnprotectData` on the same blob, exactly as SAURUS
does at startup. It defeats disk theft and other users, not malware in your own session.

To remove it: `dotnet run --project src/Saurus -- --clear-key`

### 4. Run

```bash
dotnet run --project src/Saurus
```

No window appears. A tray icon does. Highlight some text and a pill shows up next to the
cursor.

Windows Defender / your EDR may prompt on first run, because SAURUS installs low-level
input hooks. That is expected — see [Hooks](#hooks-and-what-they-see) below.

---

## Using it

| Gesture | What happens |
|---|---|
| Drag-select text, release | Pill appears next to the cursor |
| Click the pill | Panel slides in from the right, over a dimmed screen |
| Hover the pill | It stays put — the auto-hide countdown pauses and restarts when you leave |
| **Ctrl+click** while a selection is live | Fires immediately, no pill |
| Double-click a word | Pill appears (only in real text controls) |
| Type in the box, Enter | Follow-up in the same thread |
| **Expand** | Re-asks with a larger token ceiling |
| Esc, or click outside | Dismissed. No confirmation. |

Set `"instantMode": true` in the config to skip the pill entirely and fire on every
detected selection. Off by default, because it turns every drag into a paid API call.

### The panel

Clicking the pill (or the badge) dims the whole monitor and slides a panel in from the
right. Same panel either way — pill opens it on the answer, badge opens it on history, and
the header button moves between the two.

Dismiss with Esc, the ✕, or by clicking anywhere outside the panel.

**The backdrop dim is off by default.** Any dim reads as a modal takeover, which is the
wrong signal for something you use twenty times an afternoon. Raise `panel.dimOpacity` above
zero to bring it back — the implementation is still there, covering the whole monitor
including the taskbar (dimming only the work area leaves a bright strip along the bottom
that ruins the effect) and only on the monitor the panel is on.

At zero the backdrop window is not created at all. That matters: an invisible full-screen
window would swallow the first click you aimed at the app behind it and give nothing back
visually, so with the dim off the panel falls back to a real hit test against its own
rectangle for click-outside dismissal.

Tune it in `config.json` under `panel`:

```jsonc
"panel": {
  "widthFraction": 0.22,   // of monitor width, clamped to 380-680 DIP
  "insetDip": 16,          // gap from the screen edges
  "dimOpacity": 0.0,       // off; raise above 0 to dim the screen behind the panel
  "slideMs": 190           // lower this if the animation starts to annoy
}
```

Set `"popupStyle": "anchored"` to go back to the small popup pinned at the cursor — lower
friction for a quick one-word lookup, no animation, no dim. Both are fully supported; the
orchestrator talks to an `IAnswerSurface` and cannot tell which it has.

Two implementation notes, because they are load-bearing. Neither the panel nor the dim uses
WPF's `AllowsTransparency`: per-pixel alpha forces WPF to software-render that window, and a
full-screen backdrop redrawn in software every frame of a fade visibly stutters. The dim is
an opaque black window made layered via `WS_EX_LAYERED`, with its alpha driven through
`SetLayeredWindowAttributes`; the panel is opaque with rounded corners and a shadow from
DWM. Both are GPU-composited. The slide and fade animate on 16 ms tickers that stop the
moment they land, so an open panel costs nothing.

### The badge and history

A small badge sits in the corner of the screen: proof the app is alive, plus today's tokens
and cost at a glance. The dot goes amber past 80% of the daily budget and grey when paused.

- **Click it** to open the history window — past queries newest-first, with their answers,
  where they came from, and what each one cost.
- **Drag it** anywhere. The position is written back to `config.json`.
- Set `"showStatusBadge": false` to turn it off; the tray icon carries the same information
  and its menu has a **History…** item.

The tray tooltip shows the same usage figures. Right-click for history, pause/resume, the
config folder, and the log.

### Running it properly

`dotnet run` makes the app a child of your terminal, so closing the terminal kills it. That
console is also the only reason anything appears in the taskbar — every SAURUS window is
`ShowInTaskbar = false` and there is no main window. Publish once and run the exe instead:

```bash
dotnet publish src/Saurus -c Release -o dist
```

`dist\Saurus.exe` is independent of any terminal and has no taskbar presence at all — just
the tray icon and the badge.

**Start with Windows** is a toggle in both the tray menu and the badge's right-click menu.
It writes a per-user entry under `HKCU\...\CurrentVersion\Run`. HKCU rather than HKLM
deliberately: no elevation, no effect on other accounts, and you can see and disable it from
Task Manager's Startup tab without knowing SAURUS put it there. A background tool that can
only be disabled from inside itself is a tool you cannot get rid of when it misbehaves.

The toggle is greyed out under `dotnet run`, because the process is `dotnet.exe` and there
would be nothing useful to register.

To stop it: **Quit** from either the tray menu or the badge's right-click menu.

### About the tray icon

A tray icon only exists while its process does — the notification area shows running
programs, so there is no way to have a SAURUS tray entry when SAURUS is not running. Two
separate things are usually meant by "it's not in the tray":

**You can't see it while it *is* running.** Windows 11 puts every new tray icon in the
hidden overflow by default. Click the `^` chevron on the taskbar and drag the SAURUS icon
out onto the taskbar, or go to Settings → Personalization → Taskbar → *Other system tray
icons* and switch SAURUS on. Nothing in the app can force this; Windows owns that decision.

**You want something to click when it is *not* running.** That's a shortcut:

```bash
dist\Saurus.exe --install-shortcuts
```

Puts SAURUS in the Start Menu and on the desktop (`--uninstall-shortcuts` removes them,
and there's an equivalent item in the tray menu). Combined with **Start with Windows** you
should rarely need either.

## Giving it to other people

Each person runs their own copy with their own Anthropic key and their own data. Nothing is
shared and there is no server — `%APPDATA%\Saurus` is per-user, so history, usage and stats
are naturally separate without any of the machinery that would normally imply.

### One-time setup (you)

1. Create a GitHub repo (private is fine — releases are still downloadable by anyone you
   give a link to, or make it public if you'd rather).
2. `git init`, commit, and add it as `origin`.
3. Create a personal access token with `repo` scope and set `$env:GITHUB_TOKEN`.

### Publishing a version

```bash
.\release.ps1 -Version 1.0.1
```

That builds self-contained (the .NET runtime is bundled, so friends install nothing),
packages an installer, and pushes it to GitHub Releases. Test it locally first with
`-LocalOnly`, which stops after packaging so you can run the installer yourself.

The version number must go up every time or nobody updates.

### What friends do

Send them `Saurus-win-Setup.exe` from the release page. They run it, and on first launch a
window asks for their Anthropic API key with a link to create one. That's the whole setup —
no terminal, no config file.

Two things to warn them about: **SmartScreen will flag the installer** as being from an
unidentified publisher, because it isn't code-signed (a certificate is ~£200/year, and
without one this never goes away). And their EDR may prompt about the input hooks.

### How updating works

Their copy checks GitHub 30 seconds after launch and every 6 hours after that, downloads
anything newer in the background, and applies it **when they next quit and restart**. It
never restarts itself: this is a background utility with global input hooks, and a window
disappearing mid-sentence because an update landed would be far more disruptive than waiting
for a restart that's going to happen anyway.

There's a **Check for updates** item in the tray menu for when you want someone on the new
version now.

Configure it under `update` in `config.json`:

```jsonc
"update": {
  "repositoryUrl": "https://github.com/you/saurus",  // empty disables updating entirely
  "allowPrerelease": false,   // true to test a release before friends get it
  "checkOnStartup": true,
  "checkEveryHours": 6
}
```

Updating is off unless `repositoryUrl` is set, and off when running from a plain build
rather than an installed copy — so your development runs never try to update themselves.

### Idle cost

Measured on this machine, 8 logical cores, process left untouched for 40 seconds:

| | |
|---|---|
| CPU while idle | **0.0 ms / 40 s** — below measurement resolution |
| Working set | ~50 MB |
| Threads | 18 (WPF, the hook pump, the UIA worker, thread-pool) |

Nothing polls. The only periodic work is two timers — the badge refresh (10 s) and the tray
refresh (15 s) — each doing one small SQLite read. There is no capture loop, no clipboard
polling, no network connection held open.

The one genuinely per-event cost is the mouse hook, which Windows invokes for *every* mouse
message system-wide, mouse moves included. The callback is written for that: it compares
`wParam` against three button constants and returns. `Marshal.PtrToStructure` only runs for
actual button events, never for moves, and nothing on that path allocates.

This is comfortably inside consumer-app territory — it will not show up in Task Manager's
"high impact" startup list and it will not measurably affect battery.

---

## Testing offline

`--offline` swaps `AnthropicClient` for `OfflineClient` behind the `IExplainClient`
interface. Nothing else in the app changes behaviour, so a pass here means the hard parts
work. The popup and tray both read `OFFLINE` so you can never mistake a canned answer for a
real one.

Work through these in order — each isolates a different thing that can break:

**Trigger and capture**
1. Drag-select a few words in Notepad → pill appears next to the cursor.
2. Click it → popup opens instantly with the canned answer streaming in.
3. Check the code block in the answer. It should contain a `<selection>` matching what you
   highlighted and a `<context>` containing the surrounding sentence. If `<context>` is
   missing, that app's UIA provider doesn't implement paragraph expansion.
4. Repeat in a browser. The echoed payload should now include a `<url>` line.
5. Repeat in VS Code, Word, a PDF viewer, Slack. Anywhere producing no pill at all has no
   usable accessibility tree — that's the documented UIA-only limitation, not a bug.

**Focus — the thing most likely to be subtly wrong**
6. With the popup open, the caret should still be blinking in the app behind it, and the
   title bar should still look focused. If focus moved, capture is broken.
7. Now click into the follow-up box. *Now* the popup should take focus, and typing should
   land in it. Press Enter → second canned answer, same thread.
8. Press Esc → dismissed, no confirmation. Click somewhere else with a popup open →
   dismissed. Both of these come from the global hooks, not from focus events.

**Gestures**
9. Double-click a word in a text editor → pill.
10. Double-click a file in Explorer, and a row in any list view → **no pill.** This is the
    control-type filter; if a pill appears here, that filter isn't catching it.
11. Ctrl+click with a selection live → fires immediately, no pill.
12. Set `"instantMode": true`, restart → every selection fires with no pill at all.

**Guardrails** (all active offline except the token budget, which stays at zero)
13. Fire twice within 1.5 s → the second is blocked with a rate-limit message in the popup.
14. Fire the same selection twice inside the cache window → status reads
    `cached · no API call` and the answer appears with no streaming delay.
15. Fire ~9 times inside 20 s → the circuit breaker trips, the app auto-pauses, and the tray
    goes to `PAUSED`. Resume from the tray menu.
16. Set `"maxSelectionChars": 40`, restart, select a long paragraph → the echoed
    `<selection>` is clipped with an ellipsis.
17. Add `"notepad"` to `processBlocklist`, restart → no pill in Notepad at all.

**Threading**
18. Two selections on the same page within 20 minutes → the second echo shows
    `prior exchanges replayed from this thread: 1`.
19. Switch to a different app and select something → back to `0`, i.e. a new thread.

**Cost and state**
20. Hover the tray icon. Calls are counted; tokens and cost stay at zero, because they were.
21. `%APPDATA%\Saurus\saurus.log` should contain capture lines with fingerprints and
    character counts, not selection text (unless you set `"logSelections": true`).

Once that all passes, add a key and drop `--offline`. The only thing that changes is which
`IExplainClient` gets constructed.

---

## Where things live

Everything is under `%APPDATA%\Saurus`:

| File | What |
|---|---|
| `config.json` | Settings. Edit by hand; there is no settings UI. Written with full defaults on first run. |
| `apikey.dat` | DPAPI-encrypted API key. |
| `saurus.db` | SQLite: threads, queries, token ledger, response cache. |
| `saurus.log` | Local log. Rolls at 2 MB. Never contains the API key. |

---

## Capture: UI Automation only

SAURUS reads the selection through UI Automation: `TextPattern`, `GetSelection()`. It then
clones the range and expands it to the enclosing paragraph, which matters enormously —
selections are usually one word, and the surrounding sentence is the only thing that
disambiguates it. That context is truncated to a window centred on the selection.

**Where it looks matters more than it sounds.** The focused element is tried first, but it
is frequently the wrong place. In a chat UI, keyboard focus stays in the composer box while
you select text up in the transcript; in many web apps focus sits on the document body or
on nothing useful at all. Looking only at the focused element means selections in exactly
those places silently produce nothing.

So when the focused element yields no selection, SAURUS falls back to the element directly
under the cursor and walks up to six ancestors looking for a text provider — in Chromium
and Electron the provider usually sits several levels above the leaf node you hit. It also
scans every range returned by `GetSelection()` rather than just the first, because some
providers return a degenerate caret-only range ahead of the real one.

**There is deliberately no clipboard fallback.** The obvious fallback is to save the
clipboard, `SendInput` a Ctrl+C, poll, read, and restore. Three reasons it isn't here:

- In `cmd`, Windows Terminal, mintty, and WSL, Ctrl+C is SIGINT. It kills whatever the user
  is running. A background utility silently killing processes is unacceptable.
- If a modifier is physically held at the time — which is likely, since shift-drag is a
  normal way to select — the synthesised chord becomes Ctrl+Shift+C, which is DevTools in
  every browser.
- Restoring arbitrary clipboard formats faithfully is not actually possible; you can round-
  trip text and a few well-known formats and silently destroy everything else.

The cost of not having it is that apps with no accessibility tree produce nothing. SAURUS
stays silent rather than doing something dangerous, which is the correct failure mode for
something that runs on every mouse-up.

**Also captured:** foreground process name, window title, and — in browsers — the URL, read
from the address bar via UIA and matched on control type plus a value that parses as an
absolute URL, rather than on the element's name (which is locale-dependent).

### Timeouts

Every UIA call runs on a dedicated MTA thread with a **250 ms** budget (150 ms for URL
lookup), sized to cover a focused-element miss plus the ancestor walk. UIA is a cross-process COM call; against a hung app a single property read can
block for seconds. A blocked COM call cannot be cancelled, so when the budget is exceeded
the whole worker thread is abandoned and a fresh one takes over. A dropped capture is
strictly better than a stalled UI.

---

## Focus

The pill and popup are both created with `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` and
`ShowActivated = false`. **Capture always completes before any window is created** — if
focus moved first, the selection we are trying to read would be gone.

One deliberate exception: a non-activating window cannot receive keyboard input, so the
follow-up box would be untypeable. When you click into it, SAURUS drops `WS_EX_NOACTIVATE`
and calls `SetForegroundWindow`. By that point capture finished long ago, so taking focus is
harmless. The invariant that matters is *no focus change before capture*, not *never*.

Because the windows don't hold focus, there's no deactivation event to hang dismissal off —
Esc and click-outside both come from the global hooks.

---

## Hooks, and what they see

- **`WH_MOUSE_LL`** — left button down/up/double-click. Used for the drag threshold
  (`SM_CXDRAG`), Ctrl+click, and click-outside dismissal.
- **`WH_KEYBOARD_LL`** — **filtered to Escape only.** Nothing else is read, retained, or
  logged. It exists solely because a non-activating window can't get a key event.

Both callbacks do the minimum and return: read the struct, raise an in-memory event, call
`CallNextHookEx`. All real work is marshalled to the dispatcher. Windows silently unhooks
callbacks that exceed `LowLevelHooksTimeout`, so a watchdog re-installs them every 20s.

**Limitation:** hooks and UIA cannot see elevated windows (UIPI). No pill over an admin
console. SAURUS deliberately runs as invoker rather than elevating — elevating would mean
an elevated process reading every selection you make.

---

## Guardrails

The trigger is automatic, so a bug upstream is an unbounded spend loop. Everything below is
a **hard stop with a visible message**, never a warning-and-continue. There is exactly one
code path to the network and it goes through `GuardrailChain.Evaluate`.

Order (deliberate):

1. **Paused / circuit breaker** — beats everything else.
2. **API key present** — fails with a setup message rather than silently no-opping.
3. **Truncation** — selection and context clamped to a character limit before anything
   measures them. Selecting an entire webpage sends a few hundred characters.
4. **Cache** — hash of model + max_tokens + selection + context + thread context + question.
   A hit costs nothing, so it is checked before any rate limit. Thread context is in the key
   deliberately: the same word in two different conversations must not share an answer.
5. **Minimum interval** between calls.
6. **Per-minute call ceiling.**
7. **Token budget** — hourly and daily, persisted in SQLite.

### The budget is two-phase

Before a call, an estimate (`chars / 3.5` + the full `max_tokens` ceiling) is **reserved**
in the ledger inside a transaction. After the response, it's **settled** with the real
counts from the stream's `message_start` / `message_delta` usage fields.

Two reasons: concurrent calls can't both pass a check that neither should, and a crash
mid-call still counts against the budget rather than vanishing.

### Why there's an hourly budget too

A calendar-day budget alone lets a runaway loop burn the entire day's allowance in about
thirty seconds and only then stop. The rolling 60-minute cap catches it in minutes. The
circuit breaker catches it in seconds — N calls inside a short window auto-pauses the whole
app and tells you.

### Changing the caps

Edit `%APPDATA%\Saurus\config.json` and restart:

```jsonc
"limits": {
  "maxSelectionChars": 600,        // hard truncation of the selection
  "maxContextChars": 1200,         // hard truncation of surrounding context
  "minIntervalMs": 1500,           // floor between two API calls
  "maxCallsPerMinute": 12,
  "dailyTokenBudget": 200000,      // hard stop for the calendar day (local time)
  "hourlyTokenBudget": 40000,      // rolling 60-minute runaway guard
  "requestTimeoutMs": 20000,
  "cacheTtlMinutes": 10,
  "circuitBreakerCalls": 8,        // this many calls...
  "circuitBreakerWindowSeconds": 20 // ...in this window auto-pauses
}
```

`maxTokens` (per-response ceiling) and `expandedMaxTokens` (the Expand button) are
top-level. `pricing` is only used to render a dollar figure — token counts are the source
of truth.

### Retries

One retry, backed off 750 ms, only on a genuinely transient failure (timeout, connection
error, 429, 5xx) **and only if no bytes have streamed yet**. If content already arrived and
the connection then dropped, the partial answer is kept — retrying would re-bill tokens the
API has already generated and charged for.

### Key handling

Sent only to `https://api.anthropic.com/v1/messages`, which is a pinned constant asserted
before every send. Redirects are disabled. The key is never logged: every line written to
the log passes through a redaction pass that scrubs both the registered key and anything
matching `sk-ant-...`, including exception text and API error bodies.

---

## Threading

A query joins the most recent thread sharing the same URL, or the same process plus
normalized window title, within 20 minutes. Otherwise it starts a new one. At most the last
3 exchanges are replayed as context.

Titles are normalized before comparison — unread counts (`(3) Inbox`), dirty markers
(`*file.ts`) and notification dots change constantly without the document changing.

This is one small class (`ThreadResolver`) with one public method. Nothing else in the app
knows how the decision is made, so it can be swapped wholesale.

---

## Layout

```
src/Saurus/
  Program.cs                     entry point, CLI modes, wiring
  Config/SaurusConfig.cs         JSON config + defaults
  Core/Log.cs                    logging with key redaction
  Interop/Win32.cs               the entire P/Invoke surface
  Trigger/InputHooks.cs          low-level hooks + message pump + watchdog
  Trigger/TriggerService.cs      gesture -> "a selection exists here"
  Capture/UiaWorker.cs           dedicated MTA thread with a hard time budget
  Capture/UiaCaptureService.cs   selection, context expansion, URL
  Guardrails/ApiKeyStore.cs      DPAPI at rest
  Guardrails/Guardrails.cs       the single gate: cache, limits, ledger, breaker
  Api/IExplainClient.cs          the seam live/offline swap happens on
  Api/AnthropicClient.cs         streaming SSE over HttpClient
  Api/OfflineClient.cs           canned streamed answer, no network, no key
  Api/PromptBuilder.cs           system prompt + message assembly
  Storage/Database.cs            SQLite schema and access
  Storage/ThreadResolver.cs      the 20-minute rule
  Ui/PillWindow.cs               the pill
  Ui/PopupWindow.cs              the popup, streaming, follow-ups
  Ui/Theme.xaml                  dark control templates (buttons, scrollbars, list items)
  Ui/MarkdownRenderer.cs         markdown -> FlowDocument
  Ui/StatusBadge.cs              corner badge: alive, spend, click for history
  Ui/HistoryWindow.cs            past queries and their answers
  Ui/TrayIcon.cs                 spend readout + kill switch
  Orchestration/ExplainOrchestrator.cs   query lifecycle
```

The four layers named in the brief — capture, API client, guardrails, UI — don't reference
each other. `ExplainOrchestrator` is the only thing that knows all four exist.

---

## Known limitations

- **Sensitive text is not detectable.** The password-field check (`IsPassword`) only covers
  masked inputs. Your bank balance, a medical portal, a private DM are ordinary unmasked
  text and indistinguishable from a Wikipedia paragraph. If you highlight it, it is sent.
  The real controls are the process blocklist and the tray pause.
- **No keyboard-selection trigger.** Shift+arrow selections don't fire anything; the mouse
  hook is the only trigger.
- **No elevated windows** (UIPI), by choice.
- **Chromium enables its accessibility tree lazily** — expect a one-off latency and memory
  bump inside the browser on the first capture.
- **Not "instant".** Window with a spinner appears in well under 100 ms. First token from
  Haiku is ~300-600 ms over the network and no architecture fixes that. Prefetching on pill
  *appearance* would hide it, but would bill you for every selection you never clicked.

## Not in v1

No screenshot or vision capture. No thread browser. No settings GUI. No installer,
auto-update, telemetry, or packaging.
