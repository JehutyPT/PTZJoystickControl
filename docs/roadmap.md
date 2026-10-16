# Roadmap

Work is ordered so the app is safe for live use before new features land. Each item is tracked as a GitHub issue.

## Phase 1: Hardening

All phase 1 items are done.

## Phase 2: Companion integration cleanup

No open items. Moving the app's outbound calls to Companion's current HTTP API (#15) was closed as won't-do: Companion owns camera selection, so the app's outbound calls were removed instead (see [companion-integration.md](companion-integration.md#app--companion-nothing)).

## Phase 3: SKAARHOJ PTZ Extreme

- `SkaarhojGamepad` (Raw Panel joystick as an `IGamepad`) with dead-man timeout.

The panel's buttons, LED colours and display text are not on this roadmap: they are a Companion surface plugin in [its own repository](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel), tracked in [its issue #1](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel/issues/1).

## Phase 4: Canon XC

Canon XC protocol cameras alongside VISCA, and global speed profiles stepped from Companion (#41, label `phase 4: xc`). Order: camera-selection cast fix (#42), speed fractions (#43), camera abstraction (#44), speed profiles (#45) and Companion stepping (#46), then the XC camera (#47, #48, #49, #54, #55, #53), disposing a removed camera (#56), limiting the out-of-range camera preset `WARN` (#58), the camera's own zoom and focus speed ranges (#60), and the Focus step (#61, #62).

## Later

Nothing queued.
