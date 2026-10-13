# Live-safety requirements

This app moves physical cameras during live shows. These rules apply to all input, command, networking, and threading code. Items marked ⚠️ are **not yet met** by the current code; see [roadmap.md](roadmap.md).

## 1. Cameras stop when anything fails

- **Stop on controller loss.** When a controller disconnects (USB unplug, SKAARHOJ connection drop, heartbeat timeout) or is deactivated, call `Stop()` on every camera it was driving: each camera it has had selected since activation (`DrivenCameras`), since switching cameras does not yet stop the previous one.
- **Repeat stops.** VISCA over UDP has no delivery guarantee and commands are only sent on change. Whenever pan/tilt, zoom or focus goes to stop, the camera device sends the stop packet 3×, at least 50 ms apart (roughly 100–200 ms in all, depending on timer resolution: ~15.6 ms on Windows). A single lost stop packet must not leave a camera drifting. `ViscaDeviceBase.Stop()` force-stops every axis with the same repetition, regardless of the state the app believes the camera is in.
- **Dead-man timeout for network controllers.** Any controller whose input arrives over the network (SKAARHOJ) must stop its cameras if no data or heartbeat arrives within a short window (target ~200–300 ms; tune on real hardware).
- ⚠️ **Stop on camera switch.** When `SelectedCamera` changes while an axis is deflected, stop the previous camera before driving the new one.

## 2. The process never crashes

- ⚠️ No `async void` methods without a full try/catch. An unhandled exception in `async void` terminates the process.
- ⚠️ Never rethrow inside socket or `HttpListener` callbacks; log and continue.
- ⚠️ No `.First()` / `Enum.Parse` / casts on data that comes from outside (network, devices, settings). Use `TryParse` / `FirstOrDefault` and handle the miss.
- Add a global last-resort handler (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) that logs before exit, so incidents can be diagnosed.

## 3. Failures stay isolated

- Companion being down, restarting, or slow must never affect joystick → camera control.
- A SKAARHOJ disconnect must not affect the USB joystick, and vice versa.
- Outbound calls to Companion are fire-and-forget with a short timeout, never awaited on the input path.

## 4. Threading

- ⚠️ Input from any source (SDL loop, Companion listener, SKAARHOJ socket) must be marshalled onto one input thread or guarded so command execution is never concurrent for the same camera.
- Raise UI-bound `PropertyChanged` on the UI thread.

## 5. Recoverability

- Every network connection (cameras over TCP, Companion, SKAARHOJ) auto-reconnects with backoff.
- Settings files are written atomically (write to temp file, then replace) so a crash mid-save cannot corrupt the config.
- ⚠️ Log connection state changes and errors with timestamps to a rolling file. The rolling file exists (see [development.md](development.md#logs)), but camera connection messages and several error paths still use `Debug.WriteLine`, which is compiled out of Release builds.

## Pre-show checklist (target state)

1. App shows all controllers connected and all cameras reachable.
2. Move each axis fully and release: camera moves and stops.
3. Select each camera from Companion; confirm the app follows and Companion feedback updates.
4. Unplug the joystick mid-pan: camera stops.
