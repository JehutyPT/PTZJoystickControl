# Architecture

## Projects

The solution has four projects with a one-way dependency flow:

```
Gui ──► SdlGamepads ──► Application ──► Core
  └──────────────────────► Application
```

`Core` holds interfaces and models only. `Application` implements commands, devices, and services. `SdlGamepads` is one implementation of controller input. `Gui` wires everything together in `Program.cs` using Splat's service locator (`services.RegisterConstant(...)`). `Packaging/` holds the Windows MSI (WiX) and macOS app packaging; it is not part of the dependency graph.

## From stick movement to camera movement

1. **Polling.** `SdlGamepadsService.UpdateLoop` runs on a background task every 15 ms and drains SDL events (axis motion, button up/down, device added/removed).
2. **Raw value.** `SdlGamepad.OnAxisEvent` maps the SDL value to `-1..1` and sets `Input.InputValue`.
3. **Shaping.** The `Input.InputValue` setter applies inversion, deadzone and saturation (linear mapping between them), then calls `ExecuteCommand()`.
4. **Command.** The input's mapped `ICommand` runs. Dynamic commands (`IDynamicCommand`: pan, tilt, zoom, focus) take a speed and direction; static commands (`IStaticCommand`: select camera, preset, power…) take a fixed value.
5. **Device.** The command calls the gamepad's `SelectedCamera` (`ViscaDeviceBase`), which builds a VISCA packet and sends it via `ViscaIpDevice` over UDP (shared socket in `Core/Utils/UdpSocket.cs`) or TCP.

Commands are **event-driven**: a packet is only sent when the shaped input value changes. The one exception is stops: when pan/tilt, zoom or focus goes to stop, `ViscaIpDevice` repeats that packet 3×, at least 50 ms apart (roughly 100–200 ms in all), built from the camera's current state at send time. Motion is not re-sent periodically. See [live-safety.md](live-safety.md) for why this matters.

## Key abstractions

| Type | Purpose |
|---|---|
| `IGamepad` | A controller: its inputs, available commands, camera list, and `SelectedCamera`. Any new controller type (e.g. SKAARHOJ) implements this. |
| `IInput` | One axis or button, with its mapped command and shaping settings. |
| `ICommand` | An action on the selected camera. Created per gamepad by `CommandsService`. |
| `ViscaDeviceBase` | A camera: VISCA command builders, connection settings, and the Companion page/button to press when it is selected. |
| `IGamepadsService` | Discovers controllers, activates/deactivates them, owns the polling loop. |

## Settings

JSON files in `%APPDATA%\PTZJoystickControl\` on Windows (`~/.PTZJoystickControl/` on macOS): `Cameras.json` for cameras and `Gamepads.json` for input mappings. Settings are saved on every persistent property change.

## Threading

- SDL polling and command execution run on the `UpdateLoop` background task.
- The Companion HTTP listener runs its callbacks on thread-pool threads and currently calls into gamepad input from there (see [companion-integration.md](companion-integration.md) and the roadmap).
- Avalonia UI updates must happen on the UI thread; `PropertyChanged` raised from background threads can reach UI bindings.
