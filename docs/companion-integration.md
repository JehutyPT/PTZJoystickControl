# Bitfocus Companion integration

**Target: the latest stable Companion release.** Verify endpoints and protocol details against the current Companion documentation before changing this code; Companion's APIs have changed between major versions.

## Current state

### Companion → app: camera selection

- `BitfocusCompanionService` starts an `HttpListener` on `http://localhost:12345/` (port hardcoded in `Gui/Program.cs`).
- If the listener cannot start (port taken, URL ACL denied), the app still starts: the failure is logged as a warning, `InboundListenerError` holds the reason, and the main window shows a red notice. The service retries in the background (1 s, doubling, capped at 30 s; failed retries are logged only at attempts 1, 2, 4, 8, …). Once the port is free and a retry starts the listener, `InboundListenerError` clears, `InboundListenerErrorChanged` fires, the notice disappears and Companion → app requests work, with no restart. Joystick control and outbound feedback are unaffected throughout. Quitting the app (`StopListening`) cancels the retry.
- Each camera has a **Companion number** (`ViscaDeviceBase.CompanionNumber`, the *Companion #* field in the camera row, saved in `Cameras.json`). Cameras saved before numbers existed get their 1-based list position on first load (or the lowest unused number if another camera already has that one); a camera added later gets its list position if free, otherwise the lowest unused number. The camera row rejects a number below 1 or one another camera already has. Duplicates in a hand-edited `Cameras.json` are kept and logged as a `WARN` at startup.
- Companion (via its generic HTTP module) calls `http://localhost:12345/?SelectCamera=N` to select the camera whose Companion number is N. `?SetCameraPreview=N` is a legacy alias, kept so existing Companion buttons keep working.
- `BitfocusCompanionEvent.ParseRequest` validates the request against a snapshot of the camera list, taken on the listener thread.
- When a request is accepted, `onReceivedBitfocusCommand` queues it for the input loop (`UpdateLoop`, within ~15 ms), never running gamepad input on the listener thread. There `CompanionCameraSelection.SelectOnEveryController` sets the camera as `SelectedCamera` on every active controller, addressed by reference rather than SDL instance id, so it keeps working after a USB replug. No input mapping is needed. Each controller is handled in its own try/catch, and a controller whose camera list no longer has the camera is skipped with a `WARN`. Stop on camera switch applies as for any selection change (see [live-safety.md](live-safety.md)).
- Every request is validated in full before anything runs. Parameter names must match a defined command (`SelectCamera`, `SetCameraPreview`) exactly (case-sensitive). Each value must be a plain positive decimal number that exactly one camera has as its Companion number. Anything else gets `400` and runs nothing. That includes an unknown number, a number shared by two cameras (never an arbitrary pick), an empty query, and a request mixing valid and invalid parameters. A failure while accepting a valid request gets `500`. The `200` for a valid request is sent once it is queued, and a failure while it runs later on the input loop is only logged. Every response is closed, every rejection or failure is logged through `Trace` (rejections as `WARN`), and the listener keeps serving.
- Until #14, Companion selection worked through per-gamepad "Vmix Cam N Preview" virtual buttons mapped to *Select Camera*. Those inputs are gone. Their saved mappings in `Gamepads.json` are ignored and dropped the next time the gamepad's settings are saved.

### App → Companion: selection feedback

- Each camera has an optional Companion page/button number (`OnSelectedBitfocusPageNumber` / `OnSelectedBitfocusButtonNumber`, editable in the camera row in the UI, saved in `Cameras.json`).
- When `SelectedCamera` changes, `SelectCameraCommand` calls `PressButton`, which sends `GET http://localhost:8000/press/bank/{page}/{button}`. This is Companion's legacy page/bank endpoint.
- Outbound calls go through `BitfocusCompanionClient`: they run in the background, time out after 2 s, never throw, and log failures (path and reason) through `Trace`. A failed press is dropped, not retried.
- `SetCustomVariable` exists but is unused.

### Known problems

See [roadmap.md](roadmap.md) for the tracked list. In short: the outbound call uses a legacy endpoint and page/button numbering instead of Companion's page/row/column model.

## Target design

### Camera selection

Done (#14): an inbound command identifies a camera by its Companion number and selects it on every active controller (`IGamepad`), so a SKAARHOJ controller gets it too.

Inbound transport stays HTTP on localhost (simple to trigger from Companion's HTTP module). Remaining: the port comes from settings, not hardcoded.

### Feedback

Outbound calls move to Companion's current HTTP API (location-based press using page/row/column, custom variables). Preferred approach: set a custom variable such as `ptz_selected_camera` instead of pressing a button, and let Companion button feedbacks/styles react to it. That removes the need to configure a page/button per camera.

All outbound calls: short timeout, fully caught, never on the input path.

### Surfaces

The SKAARHOJ panel connects to Companion as a surface through the **Satellite API**. See [skaarhoj-integration.md](skaarhoj-integration.md).
