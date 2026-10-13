# Architecture

## Projects

The solution has five projects with a one-way dependency flow (plus `Tests`):

```
Gui ──► SdlGamepads ───► Application ──► Core
  ├───► SkaarhojPanel ──► Application
  └───────────────────────► Application
```

`Core` holds interfaces and models only. `Application` implements commands, devices, and services. `SdlGamepads` is one implementation of controller input. `SkaarhojPanel` holds the TCP link to a SKAARHOJ panel (connect, `ping`/`ack` dead-man, reconnect, message parsing); it does not drive cameras yet (see [skaarhoj-integration.md](skaarhoj-integration.md#the-panel-link-in-this-app)). `Gui` wires everything together in `Program.cs` using Splat's service locator (`services.RegisterConstant(...)`). `Packaging/` holds the macOS app packaging (the Windows zip needs no files of its own, see [development.md](development.md#windows-package)); it is not part of the dependency graph.

## From stick movement to camera movement

1. **Polling.** `SdlGamepadsService.UpdateLoop` runs on a background task every 15 ms and drains SDL events (axis motion, button up/down, device added/removed).
2. **Raw value.** `SdlGamepad.OnAxisEvent` maps the SDL value to `-1..1` and sets `Input.InputValue`.
3. **Shaping.** The `Input.InputValue` setter applies inversion, deadzone and saturation (linear mapping between them), then calls `ExecuteCommand()`.
4. **Command.** The input's mapped `ICommand` runs. Dynamic commands (`IDynamicCommand`: pan, tilt, zoom, focus) take a speed and direction; static commands (`IStaticCommand`: select camera, preset, power…) take a fixed value.
5. **Device.** The command calls the gamepad's `SelectedCamera` (`ViscaDeviceBase`), which builds a VISCA packet and sends it via `ViscaIpDevice` over UDP (shared socket in `Core/Utils/UdpSocket.cs`) or TCP.

**Relative inputs** (`InputType.Relative`, for encoders such as the SKAARHOJ panel's knobs) carry signed step counts, not a position: clockwise is positive. The controller calls `IInput.Step(steps)` instead of setting `InputValue`. A relative input takes static commands only, like an axis with a static command: each step in the configured direction (High = clockwise, Low = anticlockwise) runs the command once, and the second command runs once per step the other way. Nothing stays pressed or moving. Dynamic commands are not offered; one mapped from saved settings is refused with a `WARN` and the input is left unmapped. More than 100 steps in one event are capped (with a `WARN`) so a faulty device cannot tie up the input loop.

Commands are **event-driven**: a packet is only sent when the shaped input value changes. The one exception is stops: when pan/tilt, zoom or focus goes to stop, `ViscaIpDevice` repeats that packet 3×, at least 50 ms apart (roughly 100–200 ms in all), built from the camera's current state at send time. Motion is not re-sent periodically. When a controller's selected camera changes while the app is driving the previous one (`ViscaDeviceBase.IsMoving`), `DrivenCameras` force-stops it. When a controller is unplugged or deactivated, `SdlGamepadsService` calls `DrivenCameras.StopAll()`, which force-stops every camera that controller has had selected. See [live-safety.md](live-safety.md) for why this matters.

## Key abstractions

| Type | Purpose |
|---|---|
| `IGamepad` | A controller: its inputs, available commands, camera list, and `SelectedCamera`. Any new controller type (e.g. SKAARHOJ) implements this. |
| `IInput` | One axis, button or relative input (encoder), with its mapped command and shaping settings. |
| `ICommand` | An action on the selected camera. Created per gamepad by `CommandsService`. |
| `ViscaDeviceBase` | A camera: VISCA command builders, connection settings, and its Companion number (what a Companion request selects it by). |
| `IGamepadsService` | Discovers controllers, activates/deactivates them, owns the polling loop. |

## Settings

JSON files in `%APPDATA%\PTZJoystickControl\` on Windows (`~/Library/Application Support/PTZJoystickControl/` on macOS, `~/.PTZJoystickControl/` on Linux): `Cameras.json` for cameras, `Gamepads.json` for input mappings, the optional, hand-edited `Companion.json` for the port the app listens on for Companion (read at startup only; see [companion-integration.md](companion-integration.md#settings-companionjson)), and `SkaarhojPanel.json` for the SKAARHOJ panel's address and port (edited by hand; an empty address means no panel). Settings are saved on every persistent property change, atomically: each save writes a sibling `.tmp` file, flushes it to disk, then moves it over the target.

## Threading

- SDL polling and command execution run on the `UpdateLoop` background task. It is the input loop: no other thread runs gamepad input.
- The Companion HTTP listener runs its callbacks on thread-pool threads. It validates the request against a snapshot of the camera list and only queues each camera selection on `InputQueue`; `UpdateLoop` runs the queued input at the start of every tick, before polling SDL. A queued input that throws is logged and skipped (see [companion-integration.md](companion-integration.md)).
- The SKAARHOJ panel link (`SkaarhojPanelLink`) runs on its own background task and raises its events there. It does not run any gamepad input yet; when it does (#37), that input must be queued on `InputQueue` like Companion's.
- Avalonia UI updates must happen on the UI thread. `SelectedCamera` changes are raised synchronously on the input loop, so `DrivenCameras` and `SelectCameraCommand` see them at once; `CamerasViewModel` re-raises them on the UI thread for bindings.
- Selecting a camera or activating/deactivating a controller from the main window still runs on the UI thread and is not serialized with the input loop. The app is not operated from its window during live use.
