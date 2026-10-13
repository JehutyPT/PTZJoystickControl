# SKAARHOJ PTZ Extreme integration (planned)

Hardware: SKAARHOJ PTZ Extreme running UniSketch firmware (pre-Blue Pill). Goal: use its joystick to drive the cameras through this app, and use its buttons and displays as a Bitfocus Companion surface.

> **Status: design only, panel input side verified.** The [Raw Panel input protocol](#raw-panel-verified-on-hardware) was checked against a real panel on 2026-10-13. LED/display output, the [Satellite API](#satellite-api-companion-side) and the [mapping](#mapping) are still design and unverified; check them against SKAARHOJ's Raw Panel documentation, Companion's Satellite API documentation and real hardware before relying on them.

## Vendor references

- [SKAARHOJ Raw Panel manual](https://github.com/SKAARHOJ/Support/blob/master/Manuals/SKAARHOJ/SKAARHOJ_RawPanel_V2.pdf): protocol commands, including LED and display output.
- [SKAARHOJ installation and operation manual](https://github.com/SKAARHOJ/Support/blob/master/Manuals/SKAARHOJ/SKAARHOJ_Manual.pdf): firmware updater, online and local configuration, enabling WebConfig (pages 17 and 45).

## Overview

```
SKAARHOJ ⇄ Raw Panel (TCP 9923) ⇄ SkaarhojPanel module ⇄ Satellite API (TCP 16622) ⇄ Companion
                                          │
                                          └─ joystick axes → SkaarhojGamepad (IGamepad) → cameras
```

One module inside this app owns the TCP connection to the panel and splits its traffic:

- **Joystick and camera-control axes** become inputs on a `SkaarhojGamepad : IGamepad`. Mapping, deadzone/saturation, camera selection, and safety logic are then shared with the USB joystick for free.
- **Buttons, LEDs, and displays** are bridged to Companion as a Satellite surface.

New code lives in its own project (e.g. `SkaarhojPanel/`), mirroring `SdlGamepads/`.

## Raw Panel (verified on hardware)

Verified 2026-10-13 against one panel:

| | |
|---|---|
| Model (`_model`) | `SK_PTZEXTREMEV2` |
| Firmware (`_version`) | `v2.5.27` (UniSketch; `_bluePillReady=0`) |
| Address | DHCP on the LAN; TCP 9923 |

### Panel setup

In the panel's UniSketch configuration, under **Devices**:

1. Enable **UniSketch Raw Panel**.
2. Set its mode to **server**. It shipped in **client** mode, in which port 9923 is closed and the panel tries to connect out instead.
3. Set the device's IP field to the app host's address. Whether server mode ignores this field or uses it as a filter is unverified.

This was done in SKAARHOJ's firmware updater app, which lists the panel (`SK_PTZEXTREMEV2`, features `Unisketch, rawpanel`) and edits its configuration. The panel's own web config (the updater's **Configure** button) needs WebConfig enabled on the panel; it was off, and is not needed for Raw Panel.

No firmware update was needed. The panel's own address (**Network configuration**, DHCP/Static) is a separate setting. Because the app connects to the panel, give the panel a stable address before relying on it in a show: a DHCP reservation on the router or a static IP.

This panel had a locally administered MAC address (`92:a1:…`) and port 80 closed (WebConfig off), so neither identifies it on the network. Use the updater app's device list, or probe TCP 9923.

### Connection

- The panel is a TCP server on port 9923; the app connects as a client.
- Lines are ASCII, terminated by a bare `\n` (no `\r`).
- On connect the panel sends `RDY`, then one `map=<id>:<id>` line per input HWC, unprompted.
- The panel accepts **several clients at once**. A second client receives the `map=` lines but no `RDY`, and each new connection makes the panel **re-send the `map=` lines to the clients already connected**. A parser must accept `map=` at any time and must not wait for `RDY` before treating the link as up. Whether input events go to every client or only one is unverified, so the app should keep a single connection and close any stale one before reconnecting.
- When a client connects, the panel **switches off all its LEDs and displays** and leaves them to the client. A client that sends no output commands leaves the panel dark. When the client disconnects, the panel restores its own lights.
- The panel does **not report current control state on connect**. A joystick held deflected while the app connects reports nothing until it moves. Initialise every axis to 0 on connect (the safe value).

### Heartbeat and disconnect

- The panel sends **nothing while idle**: no keepalive, no repeats (30 s idle observed). Silence on the socket says nothing about link health.
- Send `ping`; the panel replies `ack`. Round trip on the LAN: 1.4–18 ms, median 12.5 ms (144 samples, 1 ping/s, light input load).
- With the panel's Ethernet cable pulled, `ack`s stopped immediately, but the OS reported no error on the socket for ~10 s (macOS returned `EHOSTDOWN` on send, driven by ARP). A blocking read would wait forever. **Detect loss with a `ping`/`ack` timeout**, not with socket errors or input silence; this is the dead-man timeout in [live-safety.md](live-safety.md). A 100 ms ping interval and a ~300 ms timeout fit the measured latency; not yet tested under heavy input load.
- The panel also reports `_isSleeping=0` (seen in a `list` reply). The manual describes a display sleep timer; whether a sleeping panel still sends joystick input is unverified.
- After the cable was replugged, the panel accepted new connections normally. How soon it re-accepts, and whether it keeps the old half-open socket, were not measured.

### Queries

| Send | Reply |
|---|---|
| `ping` | `ack` |
| `list` | `_model=SK_PTZEXTREMEV2`, `_serial=…`, `_version=v2.5.27`, `_bluePillReady=0` |
| `PanelTopology?` | `_panelTopology_svgbase=<svg …>` (panel drawing), then `_panelTopology_HWC={…}` (JSON: `typeIndex` plus one `HWc` entry per control with `id`, `x`, `y`, `txt`, `type`) |

`list` does **not** return the HWC map on this firmware; the map arrives as `map=` lines on connect. Use `PanelTopology?` for control names and types.

### HWC map

58 HWCs in the topology: 51 inputs (the ids in `map=`, all mapped to themselves) and 7 layout placeholders (64–70, type 250, `Controller`/`Section 1–6`, no input). Ids 25–36 do not exist.

| HWC | Label | Type | Input | Sends |
|---|---|---|---|---|
| 1–12 | `Cam1`–`Cam12` | 133 four-way button, RGB, 64×32 display | `b4` | `HWC#<id>.<edge>=Down` / `Up` |
| 13–24 | `X1`–`X12` | 133 four-way button, RGB, 64×32 display | `b4` | as above |
| 37–45, 54–56 | `A 1`–`A 12` | 134 four-way button, RGB, 64×32 display | `b4` | as above (assumed from type; not pressed) |
| 46–53 | `Knob A`–`Knob H` | 18 encoder with push, RGB, 112×32 display | `pb` | `HWC#<id>=Enc:±1`, `HWC#<id>=Press` |
| 57 | `Iris` | 16 encoder with push (smooth, no detents) | `pb` | `HWC#57=Enc:±1`, `HWC#57=Press` |
| 58 | `Zoom` | 104 elastomer zoom rocker | `iv` | `HWC#58=Speed:<-500…500>` |
| 59 | `Focus` | 16 encoder with push | `pb` | `HWC#59=Enc:±1` (press not tested) |
| 60 | `LR` | 96 joystick left/right | `ih` | `HWC#60=Speed:<-500…500>` |
| 61 | `UD` | 97 joystick up/down | `iv` | `HWC#61=Speed:<-500…500>` |
| 62 | `Rotate` | 98 joystick rotate | `ir` | `HWC#62=Speed:<-500…500>` |
| 63 | `Top button` | 99 joystick top button | `b` | `HWC#63=Down` / `Up` |

Tested: HWC 1, 46, 57–63. The rest are taken from the topology.

### Input messages

**Joystick axes (60–62) and zoom rocker (58):** `HWC#<id>=Speed:<n>`, integer −500…500, 0 at rest.

- Signs: left −, right +; up (away from operator) +, down −; rotate clockwise +, counter-clockwise −. Zoom direction relative to tele/wide is unverified.
- Sent **only on change**, at most every ~50 ms (20 Hz) while moving. **Nothing is sent while held still**, including at full deflection (1–2 s gaps at ±500 observed). The last value stands until the next message.
- Returning to rest always ends with an explicit `Speed:0`.
- At the limit, rotate jittered between 498 and 500 for a couple of seconds; other axes held steady. Saturation must absorb this.
- A fast flick that springs back can overshoot the other way: `-116, -500, -424, 172, -3, 0` within ~300 ms. Deadzone and stop logic must tolerate a brief opposite-sign spike.
- The zoom rocker reports few intermediate values (e.g. `-36, -500, -415, 0`); treat it as close to three-state.

**Buttons:** `HWC#63=Down` / `HWC#63=Up`. No auto-repeat while held (2 s hold sends one `Down`, one `Up`).

**Four-way buttons (types 133/134):** `HWC#<id>.<edge>=Down` / `Up`, with edge 1 = top, 2 = left, 4 = bottom, 8 = right (one clean pass on `Cam1`). There is **no separate centre event**: pressing the centre reports whichever edge closes first (`.1` once, `.2` once). Treat each button as one key, or the four edges as four keys; not five.

**Encoders:** `HWC#<id>=Enc:1` or `Enc:-1` per step, clockwise +. One message per step, never aggregated: ~3–4 steps/s turned slowly, steps as close as ~17 ms when spun fast. Push sends a single `HWC#<id>=Press`, with no `Down`/`Up` pair.

### Output (unverified)

- The app sets LED colors and display text per HWC. Commands not yet tested.

## Satellite API (Companion side)

Unverified design.

- The app connects to Companion's Satellite port and registers a device (id, name, key count, layout, text/color capabilities).
- Companion pushes key state (color, text) for each key; the app pushes key press/release.
- The panel then appears in Companion's Surfaces list and can be assigned pages like a Stream Deck.
- The PTZ Extreme's displays can show text and colors but not Stream Deck-style bitmaps.

## Mapping

Unverified design. A settings file maps HWC ids to either a gamepad input (axis or button) or a Satellite key index. The joystick axes (60–62) and the zoom rocker (58) are reserved for the gamepad path and never go through Companion. Use the HWC ids and message formats in [HWC map](#hwc-map).

## Failure behavior

- Panel connection lost or `ping` unanswered within the dead-man window → stop all cameras driven by `SkaarhojGamepad`, mark it disconnected, reconnect with backoff. On reconnect, axes start at 0.
- Companion connection lost → panel joystick keeps working; panel buttons show an "offline" state; reconnect with backoff.
- On reconnect to Companion, re-register the device and let Companion resend all key states.
