# AGENTS.md

Guide for AI agents and contributors. Start here, then open only the docs you need.

## What this is

A fork of [AronHetLam/PTZJoystickControl](https://github.com/AronHetLam/PTZJoystickControl): a C# / Avalonia tray app that drives PTZ cameras over VISCA-over-IP from a USB joystick. This fork adds Bitfocus Companion integration and is used **in live productions** with two Canon CR-N500 cameras.

Planned work: hardening for live use, a cleaner Companion integration, and support for a SKAARHOJ PTZ Extreme (UniSketch, pre-Blue Pill) as both joystick and Companion surface.

## Ground rules

1. **Live-safety first.** A camera must never keep moving because something failed. Read [docs/live-safety.md](docs/live-safety.md) before touching input, commands, networking, or threading.
2. **The process must not crash.** No `async void` without a try/catch, no rethrowing in background callbacks, no unguarded `.First()` on runtime data.
3. **Keep integrations isolated.** Companion and SKAARHOJ code live in their own folders/projects so the fork stays comparable with upstream.
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
