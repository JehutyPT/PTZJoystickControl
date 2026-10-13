# On-site checklist

Hardware checks for merged changes that could not be verified off-site. Work through them on the vMix PC with both CR-N500s, the joystick and Companion connected, before the build replaces the frozen production version. Tick each box, and delete an entry once all its boxes are ticked.

After the pending entries, run the [pre-show checklist](live-safety.md#pre-show-checklist-target-state).

## Pending

### SDL2 upgrade to SDL 2.31 (`ppy.SDL2-CS` 1.0.741-alpha)

- [ ] The joystick is detected at startup and appears in the app.
- [ ] Every axis and button still drives its mapped command (pan, tilt, zoom, focus, presets, camera select).
- [ ] Unplug and replug the joystick: it is detected again and still drives the cameras.
- [ ] On Apple Silicon (osx-arm64), if used: the app starts and detects the joystick.

### #1: Companion unreachable no longer crashes the app

- [ ] Stop Companion. Select each camera from the joystick, then pan, tilt and zoom it and release: the app stays up, and each camera moves and stops.
- [ ] Start Companion again. Selecting a camera from the joystick updates Companion's feedback again.

### #2: Malformed Companion requests no longer crash the app

- [ ] Send a Companion HTTP request with a misspelled parameter: Companion gets a 400 and the app stays up.
- [ ] Trigger a valid `SetCameraPreview` from Companion: the app's camera selection follows it.

### #23: Companion listener port in use no longer stops the app starting

- [ ] Quit the app. Hold port 12345 with another program (for example a second copy of an old build, or `python -m http.server 12345` on the vMix PC), then start the app: it starts, the main window shows a red "Companion → app control is off" notice naming the port, and the joystick still selects, moves and stops both cameras.
- [ ] Selecting a camera from the joystick still updates Companion's feedback.
- [ ] Release port 12345 and restart the app: no notice is shown and selecting a camera from Companion works again.

### #4: Stop commands are repeated

- [ ] On each camera, pan, tilt, zoom and focus fully and release: the camera stops promptly every time, with no lurch or late stop.
- [ ] Release the stick and immediately push it the other way: the camera follows the new direction and does not stop.
- [ ] Pan and tilt together, release only pan: tilt keeps moving at the same speed until released, then stops.
- [ ] Optional, with Wireshark on the vMix PC filtered to the camera's VISCA port (normally `udp.port == 52381`): releasing an axis shows 3 identical stop packets roughly 50–65 ms apart.

### #13: Log file and Log window

- [ ] Start the installed (Release) build: `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` exists and its last lines include `INFO [Program] PTZJoystickControl <version> started`.
- [ ] Tray menu → **Show log**: the Log window opens and shows the same startup entries. Close it: the app keeps running and the joystick still drives the cameras. Open it again: it reappears.
- [ ] With the Log window open, stop Companion, select a camera from the joystick and pan it: a `WARN [BitfocusCompanionClient]` line appears in the window and in the file, and the camera moves and stops normally.
- [ ] Start Companion again. Quit the app from the tray: the file ends with `PTZJoystickControl stopped`.

### #3: Cameras stop when a controller disconnects

- [ ] Pan a camera with the joystick and, while still holding the stick, unplug the joystick's USB cable: the camera stops within a fraction of a second.
- [ ] Repeat while zooming, and while focusing manually: the camera stops.
- [ ] Select camera 1 and start it panning, select camera 2 and start it panning, then unplug while holding the stick: both cameras stop.
- [ ] Hold the stick deflected and deactivate the joystick in the app: the camera stops, and the app stays up.
- [ ] Replug and reactivate the joystick: it drives the cameras normally again.

### #5: Companion camera selection survives a USB replug

- [ ] Trigger `SetCameraPreview` for each camera from Companion: the app's camera selection follows and Companion's feedback updates.
- [ ] Unplug the joystick and plug it back in (no app restart). Trigger `SetCameraPreview` for each camera from Companion again: the selection still follows, and the joystick drives the newly selected camera.
- [ ] Deactivate the joystick in the app, activate it again, and repeat the Companion selection: it still follows.
- [ ] Rename a camera and add or remove a camera in the app after a replug: the joystick's camera-select options update and the app stays up.

### #8: Companion virtual buttons on a controller with no saved settings

- [ ] Quit the app. Make the joystick unknown to the app: rename `%APPDATA%\PTZJoystickControl\Gamepads.json` to `Gamepads.json.bak` (keep it to restore later). Start the app and activate the joystick: its input list shows "Vmix Cam 1 Preview" and "Vmix Cam 2 Preview" straight away, after the physical buttons.
- [ ] Map each of them to *Select Camera* with the matching camera. Trigger `SetCameraPreview` for each camera from Companion: the app's camera selection follows, and the joystick drives the selected camera.
- [ ] Quit and restart the app: both virtual buttons keep their mapping, and `SetCameraPreview` from Companion still selects the cameras.
- [ ] Quit the app and restore the original `Gamepads.json` (delete the new one, rename `Gamepads.json.bak` back). Start the app: the joystick's usual mappings, including its virtual buttons, are back and drive the cameras.
