# Roadmap

Work is ordered so the app is safe for live use before new features land. Each item is tracked as a GitHub issue.

## Phase 1: Hardening

All phase 1 items are done.

## Phase 2: Companion integration cleanup

No open items. Moving the app's outbound calls to Companion's current HTTP API (#15) was closed as won't-do: Companion owns camera selection, so the app's outbound calls were removed instead (see [companion-integration.md](companion-integration.md#app--companion-nothing)).

## Phase 3: SKAARHOJ PTZ Extreme

- `SkaarhojGamepad` (Raw Panel joystick as an `IGamepad`) with dead-man timeout.
- Panel as a Companion Satellite surface (buttons, LED colors, display text).
- HWC mapping configuration.

## Later

- Exponential response curve option per axis for finer control near center.
