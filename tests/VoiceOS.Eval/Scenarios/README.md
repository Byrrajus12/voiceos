# VoiceOS live eval scenarios

> **Live runs cost real model/API calls and drive the real desktop and Chrome.** Never run
> `--all` or a whole family casually. Use `--list` first, then pick a small selection.

## Alpha suite manifest

76 suite scenarios (68 generalization + 8 sentinels) plus the 2 seam-proving smoke scenarios.

| File | Family | Scenarios | Multi-turn |
|---|---|---|---|
| `direct-windows.json` | `direct-windows` | 13 | 1 |
| `media.json` | `media` | 9 | 0 |
| `browser-destination.json` | `browser-destination` | 12 | 0 |
| `browser-current-page.json` | `browser-current-page` | 9 | 0 |
| `tabs-context.json` | `tabs-context` | 10 | 8 |
| `navigation-ambiguity.json` | `navigation-ambiguity` | 8 | 1 |
| `browser-multistep.json` | `browser-multistep` | 7 | 0 |
| `sentinels.json` | `sentinel` | 8 | 1 |
| `smoke.json` | `direct-windows`, `browser-destination` | 2 | 0 |

- Every suite scenario is tagged exactly one of `generalization` or `sentinel` (smoke uses `smoke`).
  Sentinels also have their own family, so `--family` runs never include them.
- Expected outcomes across the 93 turns: 85 Complete, 7 Clarify, 1 Failed (back with no history).
- All scenarios are `unattended: true, mutatesExternalState: false`. Nothing sends, posts, buys,
  signs in, submits a form, or deletes anything. Volume scenarios change the local system volume;
  two sentinels start YouTube playback.

### Environment requirements

- **Chrome + Companion connected** (`chrome` tag, 46 scenarios): every scenario that observes
  browser tabs has a `companionConnected` precondition.
- **Page setup** (`setup-page` tag, 17): setup runs `chrome.exe <url>` and checks that page is the
  active tab (`activeTabOrigin`) before the turn. These tabs are user tabs and are **not** closed
  afterwards; close them by hand if they pile up.
- **Multiple monitors** (`multi-monitor`): `direct.move-current-to-other-monitor`.
- **Specific installed apps** (`requires-app`, 10): Audacity, Obsidian, Notion, Discord,
  Visual Studio Code (+ Cursor), Python 3.12, Google Sheets web app, YouTube Music web app. A
  running VS Code window is needed for `direct.focus-open-vscode`. Missing apps yield
  `EnvironmentMismatch`, not a product failure.
- **Disposable windows**: some direct scenarios launch Character Map, Paint, or File Explorer
  in setup. They are not closed automatically.
- **Media**: media scenarios are most meaningful with a media session present (e.g. a paused
  song). There is no media precondition yet.

Broadly runnable on this machine with only Chrome + Companion: all of `browser-destination`
(except the app-specific ones), `browser-current-page`, `tabs-context`, `browser-multistep`,
and the non-app `navigation-ambiguity` scenarios.

### Running subsets

```
# list only (never builds the product)
dotnet run --project tests/VoiceOS.Eval -- live --tag generalization --list

# one scenario
dotnet run --project tests/VoiceOS.Eval -- live --scenario tabs.the-other-one

# one family (generalization only; sentinels live in their own family)
dotnet run --project tests/VoiceOS.Eval -- live --family browser-current-page

# sentinels separately
dotnet run --project tests/VoiceOS.Eval -- live --tag sentinel

# generalization scenarios only (excludes sentinels and smoke)
dotnet run --project tests/VoiceOS.Eval -- live --tag generalization

# a prefix
dotnet run --project tests/VoiceOS.Eval -- live --scenario "dest.*"
```

### Known harness limitations (affect how results should be read)

- Window state is probed independently (visible frame rect, minimized/maximized, monitor) and
  verified via `final.windows`. Snap is judged as roughly the left/right half of the window's
  monitor work area (tolerance max(16px, 4%)), so quarter snaps or custom split ratios are not
  recognized. A state assertion is judged on the windows the turn changed (or opened) when
  there are any; otherwise on all matching windows, so an already-satisfying window can pass.
  The foreground is not used for window operations (Snap Assist may take focus).
- Media and volume expectations check the operation the executor attempted (`mediaOperation`,
  `setVolume`, `adjustVolume`), but actual playback state and system volume are not probed: a
  correct command that the media app ignores is not detectable.
- Scroll position is not observable; scenarios only assert navigations that change the URL.
- `closeNewTabs` closes only VoiceOS task tabs created during the scenario. Setup-opened
  user tabs and launched app windows remain.
- One product instance serves the whole run, so the recent-task frame can carry across
  scenarios. Cleanup closing task tabs invalidates it in practice, but reference-only
  scenarios such as `nav.underspecified-open-it` are best run early or alone.
- Expected `Failed` (only `nav.back-without-history`) classifies a false success as
  `UnexpectedOutcome` rather than `FalseSuccess`.
- `appInstalled` uses the VoiceOS catalog (Start Menu shortcuts + seeds), so packaged apps
  without a shortcut (Calculator, Notepad, Paint) are checked with `windowOpen` instead.

## Schema

```
{
  "id": "family.short-name",              // required, stable, unique
  "name": "human-readable name",
  "family": "direct-windows|browser-destination|...",
  "tags": ["smoke", "..."],
  "safety": { "unattended": true, "mutatesExternalState": false, "note": "optional" },
  "preconditions": [ ... ],                // fail => scenario classified EnvironmentMismatch, turns not run
  "setup": [ ... ],                        // fail => scenario classified SetupFailure
  "cleanup": [ ... ],                      // best-effort, run after turns regardless of outcome
  "turns": [ { "transcript": "...", "expect": { ... }, "settleMs": 800 } ],
  // OR the shorthand for a single turn:
  "transcript": "...",
  "expect": { ... },
  "timeoutSeconds": 60,                    // per-turn RunTranscriptAsync timeout
  "settleMs": 800                          // wait after activation before probing final state
}
```

Unknown fields are rejected at load time (a misspelled expectation would otherwise be silently
dropped). A turn's `settleMs` defaults to the scenario's `settleMs`.

## Precondition kinds

- `companionConnected { waitSeconds }` — waits for the Chrome extension to reconnect (default 40s)
- `appInstalled { app }` — case-insensitive match on the VoiceOS app catalog (DisplayName, Id, or ProcessName). The catalog only knows Start Menu shortcuts plus seeded apps, so packaged apps like Notepad may be missing; use `windowOpen` when the scenario only needs a running window.
- `foregroundProcess { process }`
- `windowOpen { process }`
- `browserTabs { min }`
- `activeTabOrigin { contains }` — matched against the active tab with the highest LastUsedSequence
- `minMonitors { count }`

## Setup/cleanup step kinds

- `startProcess { file, args? }`
- `focusWindow { process }` — focuses the first open window of that process
- `wait { ms }`
- `closeNewTabs {}` — cleanup-only; closes VoiceOS-owned task tabs (Provenance=VoiceOs with a SessionId) created during this scenario

## Expectation fields (all optional)

- `outcome`: `Complete|Clarify|Unsupported|Failed` (default `Complete`)
- `route`: any of the `CommandRoute` values, compared to the final route after any reroute
- `scope`: any of `Direct|ActiveTab|ExistingNamedTab|RecentOwnedTaskTab|NewTaskTab|Native|TextTransform|Clarify`
- `directSteps: { include, exclude }` — VoiceStep type names without the "Step" suffix (e.g. `FocusWindow`)
- `browserOperations: { include, exclude }` — `InteractionActionKind` names
- `noMediaCommand`: fails if a `MediaControlStep` executed
- `mediaOperation`: `Play|Pause|Toggle|Next|Previous` — every attempted `MediaControlStep` must be
  this operation, and at least one must exist (mismatch => `WrongAction`)
- `setVolume`: 0-100 — every attempted `SetVolumeStep` must set exactly this level
- `adjustVolume: { direction: Up|Down, amount? }` — every attempted `AdjustVolumeStep` must go this
  direction (and, if given, by exactly this amount)
- `newTabs: { min, max }` — tabs the scenario opened during the turn
- `maxActions`, `maxBrowserDecisions` — efficiency ceilings
- `final: { foregroundProcess, foregroundTitleContains, activeTabOriginContains, activeTabUrlContains, activeTabTitleContains, windows, lastActivatedTargetContains }`
- `final.lastActivatedTargetContains` — the name of the last browser control VoiceOS activated during the
  turn. For results that live in in-page state (an SPA menu opening at an unchanged URL), where the URL
  cannot identify the end state; landing on a site via a search result leaves that result as the last activation.
- `final.windows: [ { process?, titleContains?, initialForeground?, exists?, isNew?, minimized?, maximized?, snapped?: Left|Right, monitorChanged? } ]`
  — select by process/title or by the window that was foreground when the turn began; `exists: false`
  means no matching window remains; `isNew` requires a window that did not exist before the turn;
  `monitorChanged` compares the same window's monitor before and after. A failing window check is
  `FalseSuccess` if the selected windows did not change at all, otherwise `WrongTarget`.

## CLI

```
dotnet run --project tests/VoiceOS.Eval -- live --tag smoke --list
dotnet run --project tests/VoiceOS.Eval -- live --scenario smoke.direct.switch-to-notepad
dotnet run --project tests/VoiceOS.Eval -- live --family browser-destination --repeat 3
dotnet run --project tests/VoiceOS.Eval -- live summarize artifacts/eval/<runId>/results.jsonl
```

`--list` never builds the live product. Running scenarios for real requires the VoiceOS tray
app to be closed (both hold the same single-instance mutex and Chrome Companion pipe). Unsafe
scenarios (`unattended: false` or `mutatesExternalState: true`) are skipped unless
`--include-unsafe` is passed.

## Classification meanings

`Pass`/`CorrectClarify` count as success. `Partial` succeeded functionally but missed an
efficiency ceiling. `WrongRoute`/`WrongScope`/`WrongAction`/`WrongTarget` mean the pipeline did
something, just not the right thing. `FalseSuccess` means the product reported success but no
observable state actually changed. `MissedClarify`/`UnnecessaryClarify` are clarification
calibration errors. `ExecutionFailure`, `Unsupported`, `Timeout`, `SetupFailure`,
`EnvironmentMismatch`, `UnexpectedOutcome`, and `HarnessError` are self-describing failure
modes; `Skipped` scenarios are excluded from success-rate math entirely.
