# Bitfocus Companion integration

**Target: the latest stable Companion release.** Verify endpoints and protocol details against the current Companion documentation before changing this code; Companion's APIs have changed between major versions.

## Current state

### Companion → app: camera selection

- `BitfocusCompanionService` starts an `HttpListener` on `http://localhost:12345/` (port hardcoded in `Gui/Program.cs`).
- Companion (via its generic HTTP module) calls e.g. `http://localhost:12345/?SetCameraPreview=1`.
- `BitfocusCompanionEvent.FindMatchingEvent` maps the query parameter to an event. `SupportedEvents` is hardcoded to two cameras ("Vmix Cam 1/2 Preview").
- `SdlGamepadsService.AddBitfocusCompanionButtons` adds one **virtual button input** per event to the SDL gamepad, numbered after the physical buttons. The user maps each virtual button to the *Select Camera* command in the UI.
- When an event arrives, `onReceivedBitfocusCommand` simulates a press and release of that virtual button on the gamepad with SDL instance id `0`.

### App → Companion: selection feedback

- Each camera has an optional Companion page/button number (`OnSelectedBitfocusPageNumber` / `OnSelectedBitfocusButtonNumber`, editable in the camera row in the UI, saved in `Cameras.json`).
- When `SelectedCamera` changes, `SelectCameraCommand` calls `PressButton`, which sends `GET http://localhost:8000/press/bank/{page}/{button}`. This is Companion's legacy page/bank endpoint.
- Outbound calls go through `BitfocusCompanionClient`: they run in the background, time out after 2 s, never throw, and log failures (path and reason) through `Trace`. A failed press is dropped, not retried.
- `SetCustomVariable` exists but is unused.

### Known problems

See [roadmap.md](roadmap.md) for the tracked list. In short: the inbound listener can crash the app, HTTP responses are never closed, camera selection breaks after a USB replug, virtual buttons only appear once the gamepad has saved settings, and the outbound call uses a legacy endpoint and page/button numbering instead of Companion's page/row/column model.

## Target design

### Camera selection

Replace the virtual-button mechanism with a direct path: an inbound command identifies a camera (by stable id or name, not list position), and the service sets `SelectedCamera` on **every active controller**, including the SKAARHOJ. No fake input events, no dependency on SDL instance ids.

Inbound transport stays HTTP on localhost (simple to trigger from Companion's HTTP module), with:
- port from settings, not hardcoded;
- every request answered and closed, unknown commands returning 400 without throwing;
- handling marshalled onto the input thread.

### Feedback

Outbound calls move to Companion's current HTTP API (location-based press using page/row/column, custom variables). Preferred approach: set a custom variable such as `ptz_selected_camera` instead of pressing a button, and let Companion button feedbacks/styles react to it. That removes the need to configure a page/button per camera.

All outbound calls: short timeout, fully caught, never on the input path.

### Surfaces

The SKAARHOJ panel connects to Companion as a surface through the **Satellite API**. See [skaarhoj-integration.md](skaarhoj-integration.md).
