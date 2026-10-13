# Roadmap

Work is ordered so the app is safe for live use before new features land. Each item is tracked as a GitHub issue.

## Phase 1: Hardening

| Severity | Item |
|---|---|
| Critical | Companion listener rethrows exceptions in its callback (e.g. `Enum.Parse` on an unknown query parameter), crashing the app. |
| High | No stop command is sent to cameras when a controller disconnects. |
| High | Stop commands are sent once over UDP and never repeated; a lost packet leaves the camera moving. |
| High | Companion camera selection hardcodes SDL instance id `0`; it stops working after a USB replug. |
| High | Companion events call into gamepad input from the HTTP thread, concurrently with the SDL loop. |
| Medium | Companion HTTP responses are never closed; Companion's requests hang until timeout. |
| Medium | Companion virtual buttons are only added when the gamepad already has saved settings. |
| Medium | Settings files are written non-atomically and can be corrupted by a crash mid-save. |
| Medium | Unused ASP.NET Core 2.2 package references (out of support, known vulnerabilities). |
| Medium | Projects and CI target .NET 6 (out of support); move to the current .NET LTS. Also upgrade WiX off `4.0.0-rc.4` and make `PTZJoystickControl.sln` build on macOS/Linux (skip the WiX project off Windows). |
| Low | Global unhandled-exception logging and a rolling log file. |

## Phase 2: Companion integration cleanup

- Select cameras directly from Companion on all active controllers (replace virtual buttons; remove the hardcoded two-camera list).
- Move outbound calls to Companion's current HTTP API; prefer a custom variable for selection feedback.
- Ports and hosts in settings; remove unused `BitfocusCompanionSettingsStore`.

## Phase 3: SKAARHOJ PTZ Extreme

- `SkaarhojGamepad` (Raw Panel joystick as an `IGamepad`) with dead-man timeout.
- Panel as a Companion Satellite surface (buttons, LED colors, display text).
- HWC mapping configuration.

## Later

- Exponential response curve option per axis for finer control near center.
