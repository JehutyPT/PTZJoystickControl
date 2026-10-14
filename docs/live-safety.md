# Live-safety requirements

This app moves physical cameras during live shows. These rules apply to all input, command, networking, and threading code. Items marked ⚠️ are **not yet met** by the current code; see [roadmap.md](roadmap.md).

## 1. Cameras stop when anything fails

- **Stop on controller loss.** When a controller disconnects (USB unplug, SKAARHOJ connection drop, heartbeat timeout) or is deactivated, call `Stop()` on every camera it was driving: each camera it has had selected since activation (`DrivenCameras`), in case a stop on camera switch was lost.
- **Repeat stops.** VISCA over UDP has no delivery guarantee and commands are only sent on change. Whenever pan/tilt, zoom or focus goes to stop, the camera device sends the stop packet 3×, at least 50 ms apart (roughly 100–200 ms in all, depending on timer resolution: ~15.6 ms on Windows). A single lost stop packet must not leave a camera drifting. `ICamera.Stop()` force-stops every axis, regardless of the state the app believes the camera is in; the VISCA camera sends it with the same repetition. The XC camera (HTTP) repeats its pan/tilt, zoom and focus stops the same way: a stop goes out in the next request, ahead of any pending move, is sent 3×, at least 50 ms apart, each built from the camera's wanted state at send time, and is retried until one of those requests succeeds (HTTP 200 with `livescope-status: 0`), unless a new move supersedes it (for pan/tilt, a move on both pan and tilt; for zoom or focus, a move on that axis). Its `Stop()` stops all four axes in every one of those requests. A preset recall is the one request that goes ahead of stops: it is the next request after the call, delayed only by the request in flight and the 30 ms gap, and it takes the place of every stop pending when it goes out, so none of them can cut it short. If it succeeds, those stops end: the app relies on the recall to take the camera to the preset and stop it there, which is checked on site (a recall takes over a manually moving camera, and every preset the show recalls stores pan, tilt and zoom). If it fails, each is restored as a new stop, sent 3× and retried as above, unless a newer stop, a move that ends it or a newer recall took its place. The recall itself is sent once and never retried, and `Stop()` keeps a recall not yet sent. Of several recalls called before one goes out, only the newest is sent. A stop wanted after the recall was sent goes out after it as usual, even if it cuts the recall short. A stop never waits for the request in flight (a move, a probe or a recall, with its login round trip when the camera challenges it) for longer than one request timeout (500 ms), but a stop that a failed recall restores goes out only once that recall has completed: up to about one request timeout later than without it, and later again for each newer recall called before then. A recall answered after the timeout counts as failed even if the camera ran it, so the stops it restores may cut it short partway.
- **Dead-man timeout for network controllers.** Any controller whose input arrives over the network (SKAARHOJ) must stop its cameras if no data or heartbeat arrives within a short window (target ~1 s). The SKAARHOJ link counts every line from the panel (an `ack`, an input event, a reply) as hearing from it, sends `ping` only after 500 ms without a line or a `ping`, and drops after 1 s without hearing from the panel (counted after a 1 s grace from connect and from wake); `SkaarhojGamepadsService` then stops every camera the panel drove on the next input-loop tick. A dead-man check that runs more than 250 ms late means the app froze, not the panel: the 1 s count restarts from that check, but only if the panel has been heard since the last such restart, so a panel that is really gone still drops the link however slow the app stays. Each series of late checks logs one `WARN` saying the app stalled, not the panel; a series ends only once a full 1 s passes with no late check, so late and on-time checks alternating still log once. The same stop runs when the panel reports `_isSleeping=1`, because a stick held still sends nothing and its release would never arrive. After reconnect or wake, every panel axis starts at 0.
- **Stop on camera switch.** When `SelectedCamera` changes while an axis is deflected, stop the previous camera before driving the new one. `DrivenCameras` does this on every selection change (joystick, Companion or GUI) when the app believes it is driving the previous camera (`ICamera.IsMoving`: the last pan/tilt, zoom or focus command sent was not a stop). An idle camera is not stopped, so a preset recall still in progress on it completes.

## 2. The process never crashes

- ⚠️ No `async void` methods without a full try/catch. An unhandled exception in `async void` terminates the process.
- ⚠️ Never rethrow inside socket or `HttpListener` callbacks; log and continue.
- ⚠️ No `.First()` / `Enum.Parse` / casts on data that comes from outside (network, devices, settings). Use `TryParse` / `FirstOrDefault` and handle the miss.
- Add a global last-resort handler (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`) that logs before exit, so incidents can be diagnosed.

## 3. Failures stay isolated

- Companion being down, restarting, or slow must never affect joystick → camera control.
- Log file I/O (writes, flushes, rotation) never runs on the input path. Logging only queues the entry; a background writer does the disk work, and a full queue drops entries instead of waiting. A slow, full or locked disk must never delay joystick → camera control.
- A speed profile step from Companion never runs on the input path either. Each speed profile change saves `SpeedProfiles.json` before it returns, so the Companion listener applies the step itself, on its own thread, instead of queuing it on `InputQueue`. The movement commands read the active speed profile from an immutable snapshot without locking, so they never wait for a step or its save. A slow save delays only the Companion requests behind it (for example a camera selection pressed right after a step), never joystick → camera control.
- A SKAARHOJ disconnect must not affect the USB joystick, and vice versa.

## 4. Threading

- Input from any source (SDL loop, Companion listener, SKAARHOJ socket) must be marshalled onto one input thread or guarded so command execution is never concurrent for the same camera. Input arriving on another thread is queued on `InputQueue` and run by the SDL `UpdateLoop`; the SKAARHOJ panel does the same.
- A speed profile step from Companion is a settings change, not input: like a change made in the window, it goes through the thread-safe speed profiles service on the thread that makes it (the Companion listener), not through `InputQueue` (see §3).
- Raise UI-bound `PropertyChanged` on the UI thread.

## 5. Recoverability

- Every network connection (cameras over TCP, Companion, SKAARHOJ) auto-reconnects with backoff.
- Settings files are written atomically (write to temp file, then replace) so a crash mid-save cannot corrupt the config.
- Log connection state changes and errors with timestamps to a rolling file (see [development.md](development.md#logs)). Camera socket failures, reconnects and settings load/save errors are logged in Release builds; only per-packet dumps stay at `Debug`. VISCA over UDP has no connection, so a camera that silently drops off the network may produce no line until a send fails.

## Pre-show checklist (target state)

1. App shows all controllers connected and all cameras reachable.
2. Move each axis fully and release: camera moves and stops.
3. Select each camera from Companion; confirm the app follows.
4. Unplug the joystick mid-pan: camera stops.
