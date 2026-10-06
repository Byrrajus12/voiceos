# VoiceOS

VoiceOS is an experimental voice-first control layer for Windows. The goal is to make natural language a fast, practical way to work with your computer while keeping execution structured, typed, and bounded.

> **Current status: experimental Alpha.** Direct Windows capabilities, literal dictation, and bounded semantic browser execution are implemented. The current rescue/hardening work is being frozen; live-site reliability remains uneven.

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
* Literal dictation with clipboard-preserving text insertion
* Semantic multi-step browser tasks in the user's normal Chrome through Native Messaging and VOS Companion
* Structured browser interaction plans, grounded target selection, and clickable runtime choices for genuine ambiguity
* Step-local effects/postconditions, bounded recovery, and early browser preparation
* Alpha tray UI, status pill, and screen-edge activity glow

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

Browser tasks take a separate path after routing and scope selection: a structured interaction plan is compiled, then executed one semantic step at a time against fresh companion observations. Speech recognition runs locally; semantic routing, browser compilation, and browser decisions use remote model services.

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
| Phase 2B  | Bounded Chrome Companion interaction | ✓ (Alpha) |
| Alpha UI | Tray, activity glow, status pill, and runtime choices | ✓ (Alpha) |
| Future    | Generic native UIA/OCR control, agents, richer context | — |

Phase 1 direct Windows capabilities are complete. The browser rescue/integration implementation is the current Alpha baseline; it does not provide general computer control.

## Project structure

```text
src/
  VoiceOS/              App entry point and composition
    UI/                 Alpha status overlays and choice panel
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
  VoiceOS.Eval.Tests/

tools/
  VoiceOS.ChromeCompanion/   Chrome extension and protocol tests
  VoiceOS.ChromeNativeHost/  Native Messaging to local pipe bridge
```

## Running locally

### Requirements

* Windows
* .NET 9 SDK
* NVIDIA Parakeet-TDT-0.6B-v2 model
* TypeSafe API key
* OpenRouter API key for browser plan compilation, extraction, blocker assessment, and repair
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
OPENROUTER_API_KEY=your_key_here
```

See `.env.example`. Browser tasks require both keys; local STT does not send microphone audio to these services.

### Run

```powershell
dotnet restore
dotnet build
dotnet run --project src/VoiceOS
```

### Test

```powershell
dotnet test
node --test tools/VoiceOS.ChromeCompanion/companion.test.js
dotnet build tools/VoiceOS.ChromeNativeHost/VoiceOS.ChromeNativeHost.csproj
```

The solution tests cover Core and Eval behavior, including direct Windows planning, dictation, routing, scope, structured browser plans, dataflow, target safety, choices/resume, effects, recovery, and startup overlap. The companion has a separate Node test suite, and the Native Messaging host is built separately. Global hooks, microphone behavior, and live websites require physical validation; passing automated tests does not certify every site.

## Browser interaction Alpha

After semantic routing, a typed scope resolver chooses where the command belongs using foreground-window information and lightweight Chrome tab metadata. Explicit scope wins; a relevant active tab can be used, inactive user tabs need explicit selection, and unrelated active pages are preserved. VoiceOS-owned task tabs may be resumed. Direct window, app, media, and volume capabilities remain on their deterministic path.

The browser path uses the user's normal Chrome profile through VoiceOS → Chrome Native Messaging host → VOS Companion. Existing account state stays in Chrome; no separate automation profile or remote-debugging session is required. See the [companion setup and protocol guide](tools/VoiceOS.ChromeCompanion/README.md).

The companion supplies tab identity, origin, title, and ownership for scope resolution, then bounded DOM-semantic observations for the selected surface. A structured interaction plan describes Search, Locate, Open, Act, and History steps, including produced values and downstream references. Each step runs with its own target binding, effects, and postconditions. Reveal requests bring an observed section into view; numeric and pagination targets must be grounded in the control's own label or structural position.

The bounded observe → decide → act → reobserve loop chooses only offered actions and current target references. Models do not supply selectors, JavaScript, click coordinates, shell commands, or arbitrary keys. Destinations are validated and grounded separately. Multiple handles for one result are grouped; genuinely distinct plausible options can suspend execution with a clickable choice panel displaying the page's own words. Selection resumes the same plan and rebinds the chosen item against fresh evidence. Missing or unresolved targets stop safely instead of becoming speculative clicks.

Observed effects establish step completion rather than model confidence alone. Recovery can assess a prerequisite or repair a step within fixed budgets; authentication, CAPTCHA, verification, and payment requirements can require the user. Playback already established by an Open step can satisfy a following Play step without clicking again.

To improve responsiveness, plan compilation can overlap tab selection or known-destination startup. Eligible self-contained web tasks can prepare the default search surface early; a mismatched origin is discarded when the plan arrives. Prepared startup observations are reused within a freshness bound, duplicate compile prefetches are reused, and timing logs record compiler/provider costs. This reduces avoidable waiting but does not guarantee low latency.

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
* Recent references use a bounded typed referent store and live validation; arbitrary conversational history or indistinguishable old windows cannot be resolved reliably.

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
* The product choice panel and resume path are implemented, but choices expire and dynamic page changes can invalidate or make a selected option ambiguous. Indistinguishable options may stop without a useful question.
* Chrome Companion is the only browser channel. Native UI interaction is represented in scope types but is not enabled yet.
* Scope resolution uses lightweight tab metadata, so ambiguous tab names and unavailable Chrome connections stop for clarification. Model interpretation, normalization, and live site behavior remain imperfect, including broad media requests such as "Play Hello on YouTube".
* Observations cover the top document and ordinary DOM controls/sections, not cross-origin frames, browser chrome, or a complete accessibility/occlusion model. Synthetic clicks may not satisfy trusted-user-gesture requirements.
* Voice Back/Forward traverses page history. Chrome's physical toolbar Back may skip entries created by synthetic clicks; this known limitation is intentionally left alone. See the [history note](tools/VoiceOS.ChromeCompanion/README.md#back--forward-and-chromes-skippable-history-entries).

**Future phases**
* Text transformation beyond literal dictation — not implemented.
* Wake word / semantic VAD — not implemented.
* General UI Automation — not implemented.
* General conversational memory — not implemented; bounded typed follow-up references are available.
* Agent delegation for multi-step tasks — not implemented.
* Developer-oriented setup; no installer yet.

## Direction

VoiceOS is intended to grow beyond desktop commands into compositional actions, richer desktop context, dictation and text manipulation, dynamic UI interaction, browser and media workflows, integrations, and a deeper agentic path for tasks that cannot be handled by deterministic capabilities alone.
