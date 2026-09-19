# VoiceOS

Single-user Windows 11 voice-first operating layer. Optimize for the current dev machine (i7-12700H, 16GB, RTX 3060 4GB) before generalizing.

## Architecture

- C#/.NET modular monolith, single deployable tray app
- Unelevated interactive-user-session process
- Push-to-talk with hold-or-toggle activation
- NAudio/WASAPI audio capture
- Local Parakeet STT via sherpa-onnx (planned)
- Jev typed decision layer (planned)
- Deterministic/native actions are code-owned, not LLM-routed
- Dictation is a first-class path alongside commands
- Agentic work is delegated out, not baked into core architecture

## Implementation philosophy

- Source-inspect proven open-source implementations before reinventing Windows behavior. Research docs in `docs/` have already mapped patterns from Handy, PowerToys, OpenWhispr, and others.
- Consult `docs/references/` for specific reuse guidance.
- Reuse algorithms/code where licensing and architecture permit.
- Keep milestones bounded. Favor working vertical slices over broad framework construction.
- Do not over-index for other users/hardware before the product works well locally.

## Testing

Test heavily: state machines, parsing, candidate construction, routing/decision logic, risk logic, transformations.

Test lightly / validate physically: global hooks, microphone/device behavior, foreground-window changes, clipboard ownership/paste, real Windows UI integration.

Do not build elaborate fake Windows environments to increase test counts.

## Agent/model delegation

- **Opus**: architecture, decomposition, integration judgment, coordination
- **Sonnet** (`implementer`, `reviewer`): implementation and substantive review
- **Haiku** (`code-scout`): read/search/source inspection, mechanical analysis
- Agent Teams only when work is independently parallelizable
- Avoid multiple agents editing overlapping production files
- Normal subagents preferred for small isolated tasks

## Repo discipline

- Do not commit or push unless explicitly instructed
- Do not add or expose secrets
- Do not change Git remotes
- Do not silently expand milestone scope
