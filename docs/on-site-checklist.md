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

### #4: Stop commands are repeated

- [ ] On each camera, pan, tilt, zoom and focus fully and release: the camera stops promptly every time, with no lurch or late stop.
- [ ] Release the stick and immediately push it the other way: the camera follows the new direction and does not stop.
- [ ] Pan and tilt together, release only pan: tilt keeps moving at the same speed until released, then stops.
- [ ] Optional, with Wireshark on the vMix PC filtered to the camera's VISCA port (normally `udp.port == 52381`): releasing an axis shows 3 identical stop packets roughly 50–65 ms apart.
