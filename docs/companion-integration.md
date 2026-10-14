# Bitfocus Companion integration

**Target: the latest stable Companion release.** Verify endpoints and protocol details against the current Companion documentation before changing this code; Companion's APIs have changed between major versions.

## Current state

### Settings: `Companion.json`

Optional, hand-edited, next to `Cameras.json` (see [architecture.md](architecture.md#settings)). The app reads it once at startup and never writes it, so **restart the app after editing it**. There is no settings UI.

```json
{ "ListenerPort": 12345 }
```

| Key | Meaning | Default |
|---|---|---|
| `ListenerPort` | The app's listener port on localhost: Companion → app | `12345` |

Keys are case-insensitive and optional; `//` comments and a trailing comma are allowed, and an unknown key (e.g. a typo) is ignored with a `WARN`. With no file, the app uses the default (the value it always used). If the file is not valid JSON or not an object, or `ListenerPort` is not a whole number from 1 to 65535, the default is used; either way the app starts and logs a `WARN` naming the file. At startup it logs an `INFO` line `Companion: listening on http://localhost:<port>/`.

`Host` and `Port` are retired keys: they set where the app sent its calls to Companion, which were removed (see [App → Companion](#app--companion-nothing)). A file that still has them loads normally; whatever their values, they are ignored, and the app logs one `INFO` line that they are no longer used, not a `WARN`. The app never rewrites the file, so they stay until someone deletes them.

### Companion → app: camera selection

- `BitfocusCompanionService` starts an `HttpListener` on `http://localhost:12345/` (`ListenerPort` in `Companion.json`).
- If the listener cannot start (port taken, URL ACL denied), the app still starts: the failure is logged as a warning, `InboundListenerError` holds the reason, and the main window shows a red notice. The service retries in the background (1 s, doubling, capped at 30 s; failed retries are logged only at attempts 1, 2, 4, 8, …). Once the port is free and a retry starts the listener, `InboundListenerError` clears, `InboundListenerErrorChanged` fires, the notice disappears and Companion → app requests work, with no restart. Joystick control is unaffected throughout. Quitting the app (`StopListening`) cancels the retry.
- Each camera has a **Companion number** (`ICamera.CompanionNumber`, the *Companion #* field in the camera row, saved in `Cameras.json`). Cameras saved before numbers existed get their 1-based list position on first load (or the lowest unused number if another camera already has that one); a camera added later gets its list position if free, otherwise the lowest unused number. The camera row rejects a number below 1 or one another camera already has. Duplicates in a hand-edited `Cameras.json` are kept and logged as a `WARN` at startup.
- Companion (via its generic HTTP module) calls `http://localhost:12345/?SelectCamera=N` to select the camera whose Companion number is N. `?SetCameraPreview=N` is a legacy alias, kept so existing Companion buttons keep working.
- `BitfocusCompanionEvent.ParseRequest` validates the request against a snapshot of the camera list, taken on the listener thread.
- When a request is accepted, `onReceivedBitfocusCommand` queues it for the input loop (`UpdateLoop`, within ~15 ms), never running gamepad input on the listener thread. There `CompanionCameraSelection.SelectOnEveryController` sets the camera as `SelectedCamera` on every active controller, addressed by reference rather than SDL instance id, so it keeps working after a USB replug. No input mapping is needed. Each controller is handled in its own try/catch, and a controller whose camera list no longer has the camera is skipped with a `WARN`. Stop on camera switch applies as for any selection change (see [live-safety.md](live-safety.md)).
- Every request is validated in full before anything runs. Parameter names must match a defined command (`SelectCamera`, `SetCameraPreview`) exactly (case-sensitive). Each value must be a plain positive decimal number that exactly one camera has as its Companion number. Anything else gets `400` and runs nothing. That includes an unknown number, a number shared by two cameras (never an arbitrary pick), an empty query, and a request mixing valid and invalid parameters. A failure while accepting a valid request gets `500`. The `200` for a valid request is sent once it is queued, and a failure while it runs later on the input loop is only logged. Every response is closed, every rejection or failure is logged through `Trace` (rejections as `WARN`), and the listener keeps serving.
- Until #14, Companion selection worked through per-gamepad "Vmix Cam N Preview" virtual buttons mapped to *Select Camera*. Those inputs are gone. Their saved mappings in `Gamepads.json` are ignored and dropped the next time the gamepad's settings are saved.

### App → Companion: nothing

The app makes no calls to Companion. **Companion owns camera selection**: its buttons tell the app which camera to drive, and any button feedback in Companion comes from Companion's own state, not from the app.

Selecting a camera from a controller button (*Select camera*) or the app's window changes only the app's selection; Companion is not told. Until #15 the app pressed a per-camera Companion page/button on every selection change, through Companion's legacy HTTP API. The production Companion has that API disabled and nothing relied on the press, so it was removed instead of moved to the current API (#15, closed as won't-do), along with the camera row's *Bitfocus Page/Button* fields. `Cameras.json` files saved before then still hold `OnSelectedBitfocusPageNumber` / `OnSelectedBitfocusButtonNumber`; they load normally, the two keys are ignored, and the next save of the camera list drops them.

## Target design

### Camera selection

Done (#14): an inbound command identifies a camera by its Companion number and selects it on every active controller (`IGamepad`), so a SKAARHOJ controller gets it too.

Inbound transport stays HTTP on localhost (simple to trigger from Companion's HTTP module). The port comes from `Companion.json` (#16).

### Surfaces

The SKAARHOJ panel's buttons, encoders, LEDs and displays reach Companion through a separate [Companion surface plugin](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel), not through this app. This app reads only the panel's joystick and zoom rocker. See [skaarhoj-integration.md](skaarhoj-integration.md#coexisting-with-the-companion-surface-plugin).
