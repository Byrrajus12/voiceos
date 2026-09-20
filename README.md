# VoiceOS

VoiceOS is an experimental voice-first control layer for Windows. The goal is to make natural language a fast, practical way to work with your computer while keeping execution structured, typed, and bounded.

> **Early prototype — actively under development. Currently implemented through M4.**

## What works today

VoiceOS currently supports:

* Push-to-talk voice activation
* Local speech recognition with NVIDIA Parakeet via sherpa-onnx
* Semantic command interpretation with TypeSafe Jev
* Live app and window discovery
* Focus-or-launch app behavior
* Window focus, close, minimize, maximize, and snap
* Named-window vs. current-window targeting
* Media playback controls
* Absolute and relative system volume control
* Confidence-based clarification/no-op behavior

Natural language is interpreted semantically; commands do not need to match hardcoded phrases.

## How it works

```text
Voice
  → local Parakeet STT
  → live desktop candidates
  → Jev semantic planning
  → typed VoicePlan
  → trusted Windows execution
```

The deterministic part of VoiceOS is the execution layer, not the language interface. Different natural phrases can resolve to the same bounded capability, while transcripts and model output never become arbitrary shell commands.

## Current status

| Milestone | Scope                                           | Status |
| --------- | ----------------------------------------------- | ------ |
| M0        | Repository and architecture baseline            | ✓      |
| M1        | Activation and audio capture                    | ✓      |
| M2        | Local speech recognition                        | ✓      |
| M3        | Semantic planning and safety model              | ✓      |
| M4        | Native app, window, media, and volume execution | ✓      |
| M5        | Compositional commands and richer targeting     | Next   |

The current system still executes one action per utterance. Multi-step commands, better same-app window disambiguation, and richer desktop context are next.

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
    Execution/          Native Windows actions
    Apps/               App discovery/catalog

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

## Limitations

* One action per utterance
* Limited disambiguation between multiple windows of the same app
* App-specific new-window behavior is not guaranteed
* No dictation/text manipulation yet
* No general UI Automation layer yet
* No open-ended agent/tool layer yet
* Developer-oriented setup; no installer yet

## Direction

VoiceOS is intended to grow beyond desktop commands into compositional actions, richer desktop context, dictation and text manipulation, dynamic UI interaction, browser and media workflows, integrations, and a deeper agentic path for tasks that cannot be handled by deterministic capabilities alone.
