# SKAARHOJ PTZ Extreme integration (planned)

Hardware: SKAARHOJ PTZ Extreme running UniSketch firmware (pre-Blue Pill). Goal: use its joystick to drive the cameras through this app, and use its buttons and displays as a Bitfocus Companion surface.

> **Status: design only.** Protocol details below come from memory and are unverified. Check them against SKAARHOJ's Raw Panel documentation and Companion's Satellite API documentation, and against real hardware, before relying on them.

## Overview

```
SKAARHOJ ⇄ Raw Panel (TCP 9923) ⇄ SkaarhojPanel module ⇄ Satellite API (TCP 16622) ⇄ Companion
                                          │
                                          └─ joystick axes → SkaarhojGamepad (IGamepad) → cameras
```

One module inside this app owns the single TCP connection to the panel and splits its traffic:

- **Joystick and camera-control axes** become inputs on a `SkaarhojGamepad : IGamepad`. Mapping, deadzone/saturation, camera selection, and safety logic are then shared with the USB joystick for free.
- **Buttons, LEDs, and displays** are bridged to Companion as a Satellite surface.

New code lives in its own project (e.g. `SkaarhojPanel/`), mirroring `SdlGamepads/`.

## Raw Panel (panel side)

- The panel must be configured for Raw Panel mode in its UniSketch configuration; a firmware update may be required.
- The panel acts as a server; the app connects as a client. Use a single connection.
- Each control is a hardware component (HWC) with a numeric id. Sending `list` returns the panel's HWC map, so the layout is discovered at runtime rather than hardcoded.
- The panel reports button down/up, encoder steps, and absolute/speed values for analog controls such as joystick axes.
- The app sets LED colors and display text per HWC.
- The connection needs a heartbeat so a dead link is detected quickly (see dead-man timeout in [live-safety.md](live-safety.md)).

## Satellite API (Companion side)

- The app connects to Companion's Satellite port and registers a device (id, name, key count, layout, text/color capabilities).
- Companion pushes key state (color, text) for each key; the app pushes key press/release.
- The panel then appears in Companion's Surfaces list and can be assigned pages like a Stream Deck.
- The PTZ Extreme's displays can show text and colors but not Stream Deck-style bitmaps.

## Mapping

A settings file maps HWC ids to either a gamepad input (axis or button) or a Satellite key index. Joystick axes are reserved for the gamepad path and never go through Companion.

## Failure behavior

- Panel connection lost or heartbeat missed → stop all cameras driven by `SkaarhojGamepad`, mark it disconnected, reconnect with backoff.
- Companion connection lost → panel joystick keeps working; panel buttons show an "offline" state; reconnect with backoff.
- On reconnect to Companion, re-register the device and let Companion resend all key states.
