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

- Work directly in the current session by default.
- Do not create or propose Agent Teams.
- Do not delegate routine repository exploration, implementation, testing, debugging, or self-review.
- Prefer direct `rg`/search/read operations over spawning an agent solely to inspect code.
- A single narrowly scoped subagent may be used when an independent investigation clearly benefits from isolated context.
- Do not spawn multiple subagents unless explicitly requested.
- Sonnet is the default model for implementation work.
- Opus is appropriate for difficult architecture, debugging, integration judgment, or high-value review.
- Haiku may be used for a small, bounded read-only investigation when delegation is genuinely cheaper than doing it directly.
- Be selective with file reads: locate relevant code first and avoid repeatedly reading large files or unchanged material.

## Repo discipline

- Do not commit or push unless explicitly instructed
- Do not add or expose secrets
- Do not change Git remotes
- Do not silently expand milestone scope
