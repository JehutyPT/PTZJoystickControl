# PTZJoystickControl

A tray app that turns controller input (USB joysticks, the SKAARHOJ panel) into live PTZ camera moves.

## Cameras

**Camera type**:
The protocol a camera is driven with: VISCA (VISCA-over-IP) or XC (Canon's HTTP control protocol). Chosen when the camera is added; the same physical camera can be added once as each type to compare them.
_Avoid_: driver, device kind

**Selected camera**:
The camera a controller is currently driving. Set by Companion for every controller at once.
_Avoid_: active camera, preview camera

**Camera preset**:
A position stored in the camera and recalled by number, with its own recall speed.
_Avoid_: preset (alone, when a speed profile could be meant)

## Speed

**Speed fraction**:
How fast a move should be, as a share (0 to 1) of the camera's full speed range for that axis, independent of camera type.
_Avoid_: speed step, VISCA speed (for the protocol-neutral value)

**Max speed**:
A controller input's own ceiling, as a percentage of its axis's full range.
_Avoid_: speed limit, scaler

**Speed profile**:
A named set of percentages for pan, tilt, zoom and focus, picked from one global ordered list from finest to fastest. Exactly one is active, for every camera.
_Avoid_: speed preset, sensitivity, gear, scaler
