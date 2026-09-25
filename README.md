# VoiceOS

VoiceOS is an experimental voice-first control layer for Windows. The goal is to make natural language a fast, practical way to work with your computer while keeping execution structured, typed, and bounded.

> **Phase 1 complete.** All planned Phase 1 milestones (M0–M6) are implemented and physically validated on the dev machine.

## What works today

VoiceOS currently supports:

* Push-to-talk voice activation
* Local speech recognition with NVIDIA Parakeet via sherpa-onnx
* Semantic command interpretation with TypeSafe Jev
* Live app and window discovery
* Focus-or-launch and new-instance app behavior
* Compound multi-step commands ("open Chrome and snap it right")
* Window focus, close, minimize, maximize, and snap
* Named-window vs. current-window targeting
* Exact step-result chaining across steps ("open Chrome and snap it right" → snap the opened window)
* Chrome profile-aware new-window launch
* Media playback controls
* Absolute volume and relative volume with explicit amounts ("volume up 10%")
* Confidence-based clarification/no-op behavior
* Monitor topology awareness (internal, external, primary, left/right/above/below)
* Moving windows across monitors, including maximized/minimized state and mixed-DPI
* Same-app ambiguity detection — multiple indistinguishable windows of the same app produce a typed failure rather than an arbitrary selection

Natural language is interpreted semantically; commands do not need to match hardcoded phrases.

## How it works

```text
Voice
  → local Parakeet STT
  → live desktop candidates (cached process metadata)
  → Jev semantic planning (single pass for simple; two passes for compound)
  → typed VoiceProgram (one or more typed VoiceSteps)
  → sequential trusted Windows execution
```

The deterministic part of VoiceOS is the execution layer, not the language interface. Different natural phrases can resolve to the same bounded capability, while transcripts and model output never become arbitrary shell commands.

Multi-step programs execute sequentially and stop on failure. Each step's output window (its exact HWND) is available as a reference target for later steps in the same utterance.

## Current status

| Milestone | Scope                                           | Status |
| --------- | ----------------------------------------------- | ------ |
| M0        | Repository and architecture baseline            | ✓      |
| M1        | Activation and audio capture                    | ✓      |
| M2        | Local speech recognition                        | ✓      |
| M3        | Semantic planning and safety model              | ✓      |
| M4        | Native app, window, media, and volume execution | ✓      |
| M5        | Compositional commands and richer targeting     | ✓      |
| M6        | Monitor move, ambiguity detection, numeric volume, hardening | ✓ |
| Phase 2A  | Literal dictation and shared interaction foundation | ✓ |
| Phase 2B  | Bounded managed-browser interaction | ✓ (Alpha) |
| Phase 2C+ | UIA/product shell, agents, richer context | — |

Phase 1 direct Windows capabilities are complete. VoiceOS is now expanding into browser interaction and context-aware computer use.

## Project structure

```text
src/
  VoiceOS/              App entry point and composition
  VoiceOS.Core/
    Activation/         Push-to-talk and keyboard hooks
    Audio/              Capture and preprocessing
    Speech/             Parakeet / sherpa-onnx
    Candidates/         Live app and window state
    Decision/           Jev planning and VoicePlan
    Browser/            Typed execution scope and Chrome Companion browser surface
    Interaction/        Shared bounded observe/decide/execute engine
    Execution/          Native Windows actions
    Apps/               App discovery/catalog
    Monitors/           Display topology types and resolution
    Windows/            WindowMoveService, DisplayTopologyService

tests/
  VoiceOS.Core.Tests/
  VoiceOS.Eval/
```

## Running locally

### Requirements

* Windows
* .NET 9 SDK
* NVIDIA Parakeet-TDT-0.6B-v2 model
* TypeSafe API key
* Google Chrome with the VoiceOS Chrome Companion and Native Messaging host installed

### Speech model

Place the sherpa-onnx Parakeet model at:

```text
src/VoiceOS/models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/
```

The model is intentionally not included in the repository.

### API key

Create a `.env` file in the repository root:

```text
TYPESAFE_API_KEY=your_key_here
```

See `.env.example`.

### Run

```powershell
dotnet restore
dotnet build
dotnet run --project src/VoiceOS
```

### Test

```powershell
dotnet test
```

The suite covers Phase 1, dictation, browser routing, scope precedence, bounded interaction, and Chrome Companion protocol behavior. Global hooks, microphone behavior, and live websites require physical validation.

## Browser interaction Alpha

After semantic routing, a typed scope resolver chooses where the command belongs using foreground-window information and lightweight Chrome tab metadata. Explicit scope wins; a relevant active tab can be used, inactive user tabs need explicit selection, and unrelated active pages are preserved. VoiceOS-owned task tabs may be resumed. Direct window, app, media, and volume capabilities remain on their deterministic path.

The browser path uses the normal Chrome profile through VoiceOS → Native Messaging host → Chrome Companion. The companion supplies tab identity, origin, title, and ownership for scope resolution; it supplies DOM-semantic observations only to the selected browser interaction surface. The bounded observe → decide → act → reobserve loop chooses actions from fresh page evidence. The model cannot supply selectors, JavaScript, coordinates, URLs, shell commands, or arbitrary keys.

For physical validation:

```powershell
$env:TYPESAFE_API_KEY = "your_key_here"
dotnet run --project src/VoiceOS/VoiceOS.csproj
```

Use the configured command activation key (`Right Ctrl` by default), then speak each scenario independently:

```text
search Wikipedia for TypeSafe Jev and open the article
search YouTube for Never Gonna Give You Up and open a result
play Andrew Huberman on YouTube
search Google for TypeSafe Jev and open its GitHub repository
find one-way flights from Detroit to San Francisco next Friday
go to GitHub, search for torvalds, and open the user repositories
search GitHub for the ripgrep repository
find places with chicken sandwiches on Uber Eats
find places with chicken sandwiches
find alex on GitHub
```

Repeat the flight scenario with different origins, destinations, and dates. For the food scenarios, verify location/address controls are treated as prerequisites rather than generic search fields. For the final ambiguity scenario, use a query that visibly produces multiple similarly plausible results and verify VoiceOS publishes a bounded Choice instead of guessing.

## Known limitations

**Window targeting**
* Same-app multi-window disambiguation is not implemented — when multiple indistinguishable windows of the same app are open, the command produces a typed failure rather than guessing. Monitor-context disambiguation ("Chrome on the left monitor") and a user-facing clarification flow are Phase 2.
* Recent-window references ("the existing one", "the last window I had open") are not supported.

**Monitor targeting**
* Friendly monitor names ("MSI monitor", "the big screen") are not supported.
* Explicit ordinal targeting ("monitor 2", "the third display") is intentionally unsupported — relative semantics (left/right/above/below, primary/other, internal/external) cover the practical cases.
* MoveWindow does not implicitly focus the moved window.

**Volume**
* Compound commands containing multiple numeric volume values (e.g. "set it to 50 and turn the other one up 10") can mis-associate the amount because the extractor sees the full utterance.

**Compound execution**
* An occasional compound follow-up execution inconsistency (e.g. a snap step not applying after a window move) has been observed but is not reliably reproducible.

**Browser Alpha**
* Live-site success depends on semantic accessibility exposed by the page. Canvas-only, closed-shadow, CAPTCHA, authentication, and anti-automation surfaces may safely stop for clarification or fail.
* Choice data and resume behavior are implemented below the application boundary; the dedicated product Choice UI is intentionally deferred.
* Chrome Companion is the only browser channel. Native UI interaction is represented in scope types but is not enabled yet.
* Scope resolution uses lightweight tab metadata, so ambiguous tab names and unavailable Chrome connections stop for clarification. Live scope behavior on the user's desktop still needs physical validation.

**Future phases**
* Text transformation beyond literal dictation — not implemented.
* Wake word / semantic VAD — not implemented.
* General UI Automation — not implemented.
* Conversational memory and cross-utterance references — not implemented.
* Agent delegation for multi-step tasks — not implemented.
* Developer-oriented setup; no installer yet.

## Direction

VoiceOS is intended to grow beyond desktop commands into compositional actions, richer desktop context, dictation and text manipulation, dynamic UI interaction, browser and media workflows, integrations, and a deeper agentic path for tasks that cannot be handled by deterministic capabilities alone.
