# AGENTS.md

Guide for AI agents and contributors. Start here, then open only the docs you need.

## What this is

A C# / Avalonia tray app that drives PTZ cameras over VISCA-over-IP from a USB joystick, forked from [AronHetLam/PTZJoystickControl](https://github.com/AronHetLam/PTZJoystickControl). It adds Bitfocus Companion integration and is used **in live productions** with two Canon CR-N500 cameras.

This repo is developed independently and optimised for that one setup. Upstream compatibility is not a goal: don't preserve upstream structure for its own sake, and never open PRs or issues against upstream.

Planned work: hardening for live use, a cleaner Companion integration, and support for a SKAARHOJ PTZ Extreme (UniSketch, pre-Blue Pill) as both joystick and Companion surface.

## Ground rules

1. **Live-safety first.** A camera must never keep moving because something failed. Read [docs/live-safety.md](docs/live-safety.md) before touching input, commands, networking, or threading.
2. **The process must not crash.** No `async void` without a try/catch, no rethrowing in background callbacks, no unguarded `.First()` on runtime data.
3. **Keep integrations isolated.** Companion and SKAARHOJ code live in their own folders/projects so a failure in one can't take down camera control.
4. **Target the latest stable Bitfocus Companion.** Verify protocol details against current Companion docs; don't rely on legacy endpoints.
5. **No secrets in the repo.** Ports and hosts go in settings files, never credentials.

## Repo map

| Path | Role |
|---|---|
| `Core/` | Interfaces and models: `IGamepad`, `IInput`, `ICommand`, VISCA device base, settings models |
| `Application/` | Implementations: commands (pan, tilt, zoom, select camera…), VISCA-IP device, settings stores, Companion service |
| `SdlGamepads/` | SDL2-backed joystick input and the input polling loop |
| `Gui/` | Avalonia UI, tray icon, dependency wiring in `Program.cs` |
| `Packaging/` | Windows MSI (WiX) and macOS packaging |

## Build and test

```
dotnet build PTZJoystickControl.sln      # Windows
dotnet build PTZJoystickControl.App.slnf # macOS/Linux (skips the WiX installer)
dotnet test   # no test project yet; see docs/development.md
```

## Docs index

| Read this | When you are |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Changing anything structural; learning how input becomes a camera command |
| [docs/live-safety.md](docs/live-safety.md) | Touching input, commands, sockets, threads, or error handling |
| [docs/companion-integration.md](docs/companion-integration.md) | Working on anything Bitfocus Companion |
| [docs/skaarhoj-integration.md](docs/skaarhoj-integration.md) | Working on the SKAARHOJ panel |
| [docs/development.md](docs/development.md) | Setting up, building, testing, branching, releasing |
| [docs/roadmap.md](docs/roadmap.md) | Picking up work; checking known issues and phase order |

## Agent skills

### Issue tracker

GitHub Issues on JehutyPT/PTZJoystickControl via `gh`; external issues and PRs are not triage surfaces. See `docs/agents/issue-tracker.md`.

### Issue labels and readiness

`bug` / `enhancement` categorise work; `prd` marks parent specs; `ready-for-agent` is applied last to a validated contract and enforced by `.github/workflows/issue-readiness-audit.yml`. Severity, phase, `live-safety` and `chore` labels are context only. See `docs/agents/issue-labels.md`.

### Implementation contracts

The issue body's `## Implementation Contract` section is the sole implementation authority. See `docs/agents/implementation-contract.md`.

### Domain docs

Single context: root `GLOSSARY.md` and `docs/adr/`, created lazily. See `docs/agents/domain.md`.
