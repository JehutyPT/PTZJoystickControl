# AGENTS.md

A C# / Avalonia tray app that drives two Canon CR-N500 PTZ cameras over VISCA-over-IP or Canon's XC protocol (HTTP) from a joystick, integrated with Bitfocus Companion. It runs **live shows**: reliability outranks every other concern.

## Always

- **Live-safety:** cameras stop when anything fails, and the process never crashes. Read [docs/live-safety.md](docs/live-safety.md) before changing input, commands, networking, threading or error handling.
- **Repo:** work lands on `main` of `JehutyPT/PTZJoystickControl`; pass `--repo JehutyPT/PTZJoystickControl` to `gh`.

## Docs

| Read | When |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Changing structure; tracing how input becomes a camera command |
| [docs/development.md](docs/development.md) | Building, testing, branching, committing, releasing |
| [docs/roadmap.md](docs/roadmap.md) | Picking up work; phase order |
| [docs/companion-integration.md](docs/companion-integration.md) | Bitfocus Companion |
| [docs/skaarhoj-integration.md](docs/skaarhoj-integration.md) | SKAARHOJ panel |

## Agent skills

| Read | When |
|---|---|
| [docs/agents/issue-tracker.md](docs/agents/issue-tracker.md) | Creating, reading or editing issues, sub-issues and blockers |
| [docs/agents/issue-labels.md](docs/agents/issue-labels.md) | Labelling issues; `ready-for-agent` |
| [docs/agents/implementation-contract.md](docs/agents/implementation-contract.md) | Writing or implementing an issue's `## Implementation Contract` |
| [docs/agents/domain.md](docs/agents/domain.md) | Domain terms (`GLOSSARY.md`) and ADRs (`docs/adr/`) |
