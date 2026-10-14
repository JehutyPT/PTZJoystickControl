# SKAARHOJ PTZ Extreme integration (joystick axes built; buttons and displays in the Companion surface plugin)

Hardware: SKAARHOJ PTZ Extreme running UniSketch firmware (pre-Blue Pill). This app uses its joystick and zoom rocker to drive the cameras. Its buttons, encoders, LEDs and displays are a Bitfocus Companion surface, served by a separate [Companion surface plugin](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel) (tracked in [its issue #1](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel/issues/1)), not by this app.

> **Status: joystick drives cameras.** The [Raw Panel protocol](#raw-panel-verified-on-hardware) (input, LED/display output, sleep, reconnect) was checked against a real panel on 2026-10-13. The app's [panel link](#the-panel-link-in-this-app) (#35) connects, parses the panel's messages, detects loss and reconnects. The [panel controller](#the-panel-as-a-controller) (#37) drives the selected camera from the joystick and zoom rocker, shows the panel asleep and survives the wake stall (#39); neither is yet checked on hardware. Buttons, encoders, LEDs and displays belong to the Companion surface plugin (see [Coexisting with the Companion surface plugin](#coexisting-with-the-companion-surface-plugin)).

## Vendor references

- [SKAARHOJ Raw Panel manual](https://github.com/SKAARHOJ/Support/blob/master/Manuals/SKAARHOJ/SKAARHOJ_RawPanel_V2.pdf): protocol commands, including LED and display output. Commands marked V1 are the ones UniSketch supports.
- [SKAARHOJ Raw Panel device core manual](https://github.com/SKAARHOJ/Support/blob/master/Manuals/DC_SKAARHOJ_RawPanel.pdf): the older UniSketch-specific Raw Panel document; has the full `HWCt` field table and server-mode notes.
- [SKAARHOJ installation and operation manual](https://github.com/SKAARHOJ/Support/blob/master/Manuals/SKAARHOJ/SKAARHOJ_Manual.pdf): firmware updater, online and local configuration, enabling WebConfig (pages 17 and 45).

## Overview

```
           ┌─ Raw Panel (TCP 9923) ⇄ SkaarhojPanel module (this app) → SkaarhojGamepad (IGamepad) → cameras
SKAARHOJ ──┤                           reads HWC 58, 60, 61, 62 only
           └─ Raw Panel (TCP 9923) ⇄ Companion surface plugin ⇄ Companion
                                      buttons, encoders, LEDs, displays, sleep timer
```

The panel takes several Raw Panel clients at once (see [Connection](#connection)), and this app is one of them:

- **Joystick and zoom rocker** (HWC 60, 61, 62 and 58) become inputs on a `SkaarhojGamepad : IGamepad`. Mapping, deadzone/saturation, camera selection, and safety logic are then shared with the USB joystick for free.
- **Everything else** (buttons, encoders, LEDs, displays, the sleep timer) belongs to the [Companion surface plugin](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel), a TypeScript module for Companion in its own repository. This app is a read-only client: it never writes to the panel.

New code lives in its own project, `SkaarhojPanel/`, mirroring `SdlGamepads/`.

## The panel link in this app

`SkaarhojPanel/SkaarhojPanelLink.cs` owns the one TCP connection to the panel (#35). It moves no camera itself; the [panel controller](#the-panel-as-a-controller) (#37) and the asleep UI state (#39) build on it.

**Settings.** `SkaarhojPanel.json`, next to `Cameras.json` and `Gamepads.json` (see [architecture.md](architecture.md#settings)), holds the panel's address and port. There is no GUI for it yet: the app writes an empty template on first start, and you fill in the address by hand and restart the app.

```json
{
  "Address": "192.168.1.163",
  "Port": 9923
}
```

With `Address` empty or missing, the app makes no connection and logs nothing about the panel. A file that cannot be read, or a port outside 1–65535, is logged as an `ERROR` and the panel stays off; the app never overwrites a file it could not read.

**Behaviour.** Each step follows from a measurement in [Connection](#connection) and [Heartbeat and disconnect](#heartbeat-and-disconnect):

- The link is reported up as soon as TCP connects. It does not wait for `RDY`, and accepts `map=` lines at any time.
- Every complete line from the panel counts as hearing from it: an `ack`, an input event, a reply, `RDY`, `map=`, even a line it cannot parse.
- After connecting it sends `list`, `Connections?` and a first `ping`. After that it sends `ping` only when the panel has been quiet: 500 ms after the later of the last line heard and the last `ping`. While lines keep arriving, for example from a moving stick, no `ping` goes out.
- The dead-man timeout is 1 s without hearing from the panel. It counts only after a 1 s grace from connect, and from the end of the grace if nothing was heard later, so the panel's ~0.5 s post-connect stall (and a quick first `ack` before it) never drops a new connection. `_isSleeping=0` re-arms the same 1 s grace, so the [wake stall](#sleep) does not drop the link either; a silence that outlasts the grace still does. A separate timer checks the deadline every 125 ms (a quarter of the ping interval), so a stalled send cannot delay it and a silent panel is dropped within about 1.1 s. The panel closing the connection, or a failed read or send, drops the link at once.
- A dead-man check that runs more than 250 ms later than scheduled means the app froze (a stalled machine, a long garbage collection), not the panel: the panel had no chance to be heard. The 1 s count then restarts from that check, but only if the panel has been heard since the last such restart on this connection (or there has been none). So the app rides out its own freezes, while a panel that is really gone still drops the link at the first check more than 1 s after the last forgiven freeze, however slow the app stays.
- On down, the link closes the socket, then reconnects after 1 s, doubling to at most 30 s. Each connect attempt gives up after 2 s. The delay starts again from 1 s only after a connection has received an `ack`, so a panel that accepts connections but never answers is not retried every second.
- The link holds at most one connection: the next attempt starts only after the previous socket is closed.
- Each received line is split on `\n` and parsed (`RawPanelParser`) into `Speed`, `Down`/`Up` (with the four-way edge, if any), `Encoder`, `Press`, `Map`, `Ready`, `Ack`, `Reply` (`_key=value`) or `Unknown`. A trailing `\r` is ignored. `Speed` is passed on unclamped. Malformed or unknown lines are logged as `WARN` (the 1st, 2nd, 4th, 8th, …) and ignored.

**Log lines** (source `SkaarhojPanelLink`): `INFO` link up, and the `_model`, `_version` and `_connections` replies; `WARN` link down with the reason (how long the panel was silent, panel closed the connection, read or send failed); `WARN` "the app stalled, not the panel at <address>:<port>" with how late the dead-man check ran, once for each series of late checks (a series ends only once a full 1 s passes with no late check); `WARN` connect failures, at the 1st, 2nd, 4th, 8th, … consecutive attempt. Input events are never logged.

**Threading.** The link runs on one background task. Its `LinkUp`, `LinkDown` and `MessageReceived` events are raised there; a subscriber that throws is logged and the link carries on. Anything that drives cameras from these events must queue onto `InputQueue` (live-safety §4). Quitting the app disposes the link, which cancels pings and reconnects and closes the socket.

### The panel as a controller

`SkaarhojPanel/SkaarhojGamepadsService.cs` turns the link into one entry in the controller list (#37). The entry is named `SKAARHOJ panel (<address>)`, and its id is `skaarhoj:<address>`, so keep the panel's address fixed with a DHCP reservation. It is shown connected while the link is up, and `(Asleep: press a panel button)` while the panel is asleep. As with the USB joystick, select it in the list to activate it. Selecting it deactivates the USB joystick, and the reverse, so the two are never active together (`CompositeGamepadsService`). A controller saved as active that comes back (the panel reconnecting or waking, a USB joystick plugged in) is activated only if no other controller is; otherwise the active one keeps control, the returning one is left off and saved as off, and a `WARN` names both.

- **Inputs.** `SkaarhojGamepad` has four axes with the usual deadzone, saturation, inversion and command mapping: `LR` (HWC 60), `UD` (61), `Rotate` (62) and `Zoom` (58, the rocker). `Speed:<n>` becomes `n / 500`, clamped to −1…1. The default saturation (5 %) absorbs the 498–500 jitter at full deflection. Every other HWC is ignored, the top button (63) included. What each axis drives is the app's mapping; the current operator uses the twist (62) for zoom, clockwise in.
- **Mappings** are saved by gamepad id in `SkaarhojGamepads.json`, next to `Gamepads.json`.
- **Camera selection** follows Companion: a Companion camera select reaches the panel like any other active controller.
- **Read-only.** The app never writes to the panel: no LEDs, display text, `SleepTimer=` or other settings. It sends only the link's own `list`, `Connections?` and `ping`. Without the Companion surface plugin connected, the panel stays dark.
- **Threading.** Link events are only queued on the shared `InputQueue`. Axis input, link up/down and sleep/wake run on the input loop, in the order received.
- **Link down** (panel silent for 1 s, panel closed, read or send failed) and **`_isSleeping=1`** both stop every camera the panel's gamepad has driven (`DrivenCameras.StopAll()`) and remove it from the active controllers. Link down shows the entry `(Disconnected)`; sleep shows it `(Asleep: press a panel button)`, still selectable, so an operator knows to press a button before using the stick. A held-still stick sends nothing, so without the stop on sleep a camera could keep moving with no release ever arriving. An `INFO` line logs each sleep and wake, once each.
- **Link up** and **`_isSleeping=0`** activate a fresh `SkaarhojGamepad` (if the panel is the selected controller). Every axis starts at 0, and nothing moves until the next `Speed` message. The panel does not report its sleep state on connect, so a new connection counts as awake: an asleep joystick sends nothing anyway.
- **UI thread.** The entry's connected and asleep changes are made on the input loop and raised on the UI thread (`UiThread.Post`, passed in by `Program.cs`).

## Raw Panel (verified on hardware)

Verified 2026-10-13 against one panel:

| | |
|---|---|
| Model (`_model`) | `SK_PTZEXTREMEV2` |
| Firmware (`_version`) | `v2.5.27` (UniSketch; `_bluePillReady=0`) |
| Address | Fixed by a router DHCP reservation; TCP 9923 |

### Panel setup

In the panel's UniSketch configuration, under **Devices**:

1. Enable **UniSketch Raw Panel**.
2. Set its mode to **server**. It shipped in **client** mode, in which port 9923 is closed and the panel tries to connect out instead.
3. The device's IP field is required by the form but is **not a connection filter** in server mode: with it set to `192.168.1.10`, a client at `192.168.1.11` connected normally.

This was first done in SKAARHOJ's firmware updater app, which lists the panel (`SK_PTZEXTREMEV2`, features `Unisketch, rawpanel`) and edits its configuration. WebConfig, the panel's own web page on port 80 (the updater's **Configure** button), was off at first and has since been enabled permanently. It is not needed for Raw Panel. Saving in the updater's advanced configuration mode failed with "Could not save configuration!" even though the HTTP request returned 200; simple mode saved normally. The cause is unknown.

No firmware update was needed. The panel's own address (**Network configuration**, DHCP/Static) is a separate setting. The panel uses DHCP, with a reservation on the router so its address never changes; the app connects to it by that address.

This panel has a locally administered MAC address (`92:a1:…`), so the MAC doesn't identify it on the network. Use the updater app's device list, the WebConfig page, or probe TCP 9923.

### Connection

- The panel is a TCP server on port 9923; the app connects as a client.
- Lines are ASCII, terminated by a bare `\n` (no `\r`).
- On connect the panel sends `RDY` and one `map=<id>:<id>` line per input HWC, unprompted. `RDY` is not always first: a command sent right after connecting can be answered before it.
- The panel accepts **several clients at once** (the manual says up to 7–8, meant for redundancy). A second client receives the `map=` lines but no `RDY`, and each new connection makes the panel **re-send the `map=` lines to the clients already connected**. A parser must accept `map=` at any time and must not wait for `RDY` before treating the link as up.
- **Every client receives every input event**, at the same moment. Replies to queries and `ping` go only to the client that asked. All clients share the panel's LEDs and displays, so whichever writes last wins. `Connections?` lists the connected client addresses (`_connections=192.168.1.10;`).
- **The app must hold exactly one connection.** The panel doesn't enforce it. A client sending `ping` as fast as it can delayed another client's `ack`s past 300 ms, enough to trip the 300 ms dead-man timeout the app used before #40. Whether a second client (a test tool, a second app instance) can delay them past the current 1 s timeout is untested.
- When a client connects, the panel **switches off all its LEDs and displays** and leaves them to the client. A client that sends no output commands leaves the panel dark. When the client disconnects, the panel restores its own lights.
- The panel does **not report current control state on connect**. A joystick held deflected while the app connects reports nothing until it moves. Initialise every axis to 0 on connect (the safe value).
- **The panel stops answering for about 0.4–0.5 s after every connect.** A `ping` sent within the first few milliseconds may still be answered at once (5–19 ms seen), but `ping`s sent during the stall are answered 420–530 ms after connecting. A 300 ms dead-man timeout armed at connect, or at that first quick `ack`, drops the new connection. Give every new connection about 1 s of grace before the dead-man timeout applies.

### Heartbeat and disconnect

- The panel sends **nothing while idle**: no keepalive, no repeats (30 s idle observed). Silence on the socket says nothing about link health.
- Send `ping`; the panel replies `ack`. Round trip on the LAN: 1.4–18 ms, median 12.5 ms (144 samples, 1 ping/s, light input load).
- With the panel's Ethernet cable pulled, `ack`s stopped immediately, but the OS reported no error on the socket for ~10 s (macOS returned `EHOSTDOWN` on send, driven by ARP). A blocking read would wait forever. **Detect loss with a `ping`/`ack` timeout**, not with socket errors or input silence; this is the dead-man timeout in [live-safety.md](live-safety.md). A 100 ms ping interval and a ~300 ms timeout fit the measured latency; not yet tested under heavy input load. The app now uses a looser window (#40): any line counts as hearing from the panel, a `ping` goes out after 500 ms of quiet, and the link drops after 1 s of silence (see [the panel link in this app](#the-panel-link-in-this-app)).
- In a second cable pull, a 100 ms `ping` with a 300 ms timeout detected the loss 300 ms after the last `ack`. The test client's reconnect attempts (0.5 s connect timeout, retried every 0.2 s) succeeded 26 s after the loss; that includes the time the cable was out, which wasn't timed separately. The first `ack` took 424 ms (see the stall above), and the connection dropped once more before settling. Afterwards `Connections?` listed only the new connection: the panel had not kept the old socket. Joystick input arrived normally on the new connection.

### Sleep

The panel has a global sleep timer, shared by all clients. Set over Raw Panel, it survives the client disconnecting but **not a reboot**: after a power cycle it was back to 4 hours (`14400000`). Setting **System → Panel Sleep Time = 1 hour** on the `Controller` HWC in WebConfig was saved (the config shows the action), but after a reboot `SleepTimer?` still reported 4 hours; why is unknown. Until that is understood, whoever owns the sleep timer (the Companion surface plugin; this app never sends it) should send its own `SleepTimer=` on every connect rather than rely on the panel's stored value.

| Send | Effect |
|---|---|
| `SleepTimer=<ms>` | Idle time before sleep; `0` never sleeps. The panel echoes `_sleepTimer=<ms>`. |
| `SleepTimer?` | Replies `_sleepTimer=<ms>`. |
| `WakeUp!` | Wakes the panel (from the manual; not tested). |
| `SleepMode=`, `SleepScreenSaver=` | Choose the sleep animation, e.g. `SleepScreenSaver=3` only dims and keeps content (from the manual; not tested). |

The panel sends `_isSleeping=1` when it falls asleep and `_isSleeping=0` when it wakes, unprompted. Setting a timer shorter than the current idle time sleeps it at once. `ping` traffic does not count as activity: the panel fell asleep with a client pinging every 100 ms, so staying connected does not keep it awake. Asleep, it shows an animation on one display at a time with a sleep counter.

Verified while asleep:

- **The joystick sends nothing and does not wake the panel.** A 2 s deflection produced no messages; the panel stayed asleep.
- **A button press wakes it, and the `Down` can be lost.** Only `HWC#1.2=Up` arrived, with no matching `Down`. The test client's 300 ms timeout dropped the connection during the wake stall and reconnected, so whether the panel swallows the `Down` or sent it to the closed connection is unverified. Either way, parsers must accept an `Up` with no `Down`.
- **The joystick keeps the panel awake.** With a 15 s timer, 35 s of continuous joystick movement (about 1,200 messages, no buttons) did not put it to sleep. Joystick movement counts as activity; `ping` does not.
- **Waking stalls the panel by more than 300 ms**, enough to trip a 300 ms dead-man timeout. Changing `SleepTimer` while asleep also wakes it.

The manual warns that the OLED displays burn in and that burn-in is not covered by warranty, which is why the panel sleeps by default. Consequences:

- **What this app does** (#37, #39). It only reacts to `_isSleeping`; it never sends `SleepTimer=`, `WakeUp!` or any other setting to set, prevent or end sleep. On `_isSleeping=1` it stops every camera the panel's joystick drove, makes the joystick inactive, shows the panel `(Asleep: press a panel button)` in the controller list and logs `INFO … asleep`. On `_isSleeping=0` it logs `INFO … awake`, makes the joystick available again with every axis at 0, and gives the dead-man timer the same 1 s grace as after a connect, so the wake stall does not drop the link. Off-site tests assume `_isSleeping=0` arrives before the stall; if the panel stalls first, the link still drops once on wake (see the #39 on-site check).
- **The Companion surface plugin owns the sleep timer** ([its issue #1](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel/issues/1)): how long the panel stays awake, sending `SleepTimer=` on every connect, and re-sending all LED and display state on `_isSleeping=0`. Until it does, nothing sets the timer, so after a reboot the panel is back to 4 hours (`14400000`).
- Keeping the panel awake while it is in use (for example with `WakeUp!` when a show starts, or `SleepTimer=0` for its duration) is a design choice for the plugin, weighed against burn-in.
- **A deflected, held-still stick:** not yet known whether it counts as activity. Either the panel stays awake and the camera keeps moving until release, or it sleeps and the camera stops within ~0.5 s; record which here after the #39 on-site check.

### Queries

| Send | Reply |
|---|---|
| `ping` | `ack` |
| `list` | `_model=SK_PTZEXTREMEV2`, `_serial=…`, `_version=v2.5.27`, `_bluePillReady=0` (no `_serverModeLockToIP`/`_serverModeMaxClients`, though the manual lists them) |
| `Connections?` | `_connections=<ip>;<ip>;…`, one entry per connected client |
| `SleepTimer?` | `_sleepTimer=<ms>` (see [Sleep](#sleep)) |
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

- Signs: left −, right +; up (away from operator) +, down −; rotate clockwise +, counter-clockwise −. The zoom rocker's sign relative to its printed tele/wide markings was not checked; what each axis does is set in the app's mapping.
- Sent **only on change**, at most every ~50 ms (20 Hz) while moving. **Nothing is sent while held still**, including at full deflection (1–2 s gaps at ±500 observed). The last value stands until the next message.
- Returning to rest always ends with an explicit `Speed:0`.
- At the limit, rotate jittered between 498 and 500 for a couple of seconds; other axes held steady. Saturation must absorb this.
- A fast flick that springs back can overshoot the other way: `-116, -500, -424, 172, -3, 0` within ~300 ms. Deadzone and stop logic must tolerate a brief opposite-sign spike.
- The zoom rocker reports few intermediate values (e.g. `-36, -500, -415, 0`); treat it as close to three-state.

**Buttons:** `HWC#63=Down` / `HWC#63=Up`. No auto-repeat while held (2 s hold sends one `Down`, one `Up`).

**Four-way buttons (types 133/134):** `HWC#<id>.<edge>=Down` / `Up`, with edge 1 = top, 2 = left, 4 = bottom, 8 = right (one clean pass on `Cam1`). There is **no separate centre event**: pressing the centre reports whichever edge closes first (`.1` once, `.2` once). Treat each button as one key, or the four edges as four keys; not five.

**Encoders:** `HWC#<id>=Enc:1` or `Enc:-1` per step, clockwise +. One message per step, never aggregated: ~3–4 steps/s turned slowly, steps as close as ~17 ms when spun fast. Push sends a single `HWC#<id>=Press`, with no `Down`/`Up` pair.

### Output

The panel accepts output per HWC with no reply. The commands below are from the Raw Panel manual; the examples were checked on the panel.

**LED state:** `HWC#<id>=<n>`, where bits 0–3 are 0 = off, 4 = on, 5 = dimmed (1–3 are legacy fixed colours; avoid). Bits 8–11 add blinking (`0b0001` ≈ 4 Hz up to `0b1000` ≈ 0.5 Hz; full table in the manual). Bit 5 (32) drives an output relay where one exists; the manual suggests sending 36 for on, 5 for dimmed, 0 for off.

**LED colour:** `HWCc#<id>=<n>`, used by the on and dimmed states. 128 + a colour index: 2 white, 3 warm white, 4 red, 5 rose, 6 pink, 7 purple, 8 amber, 9 yellow, 10 dark blue, 11 blue, 12 ice, 13 cyan, 14 spring, 15 green, 16 mint (0 default, 1 off). Or 128 + 64 + `rrggbb` (2 bits per channel) for any other colour.

**Display text:** `HWCt#<id>=<fields>`, pipe-separated: value | format | fine/icon | title | isLabel | label1 | label2 | … (full table in the device core manual). An empty value renders nothing. Keep fields to about 24 characters.

| Sent | Seen on the panel |
|---|---|
| `HWCc#1=132`, `HWC#1=4` | `Cam1` bright red |
| `HWCc#2=143`, `HWC#2=5` | `Cam2` dimmed green |
| `HWCc#3=139`, `HWC#3=260` (4 + 256) | `Cam3` blue, blinking fast |
| `HWCc#46=136`, `HWC#46=4` | `Knob A` amber |
| `HWCt#1=1\|10\|\|CAM 1` | `Cam1` display: "CAM 1" (format 10, one text line, size 1) |
| `HWCt#2=1234\|0\|\|Value\|0` | `Cam2` display: 1234 with title "Value" |
| `HWCt#46=1\|11\|\|\|1\|Line one\|Line two` | `Knob A` display: two text lines |

Nothing else changed. Also in the manual and not tested: `Clear`, `ClearLEDs`, `ClearDisplays`, `PanelBrightness=<leds>,<oleds>` (0–8), `DimmedGain=`, and bitmap graphics (`HWCg#`). UniSketch may send `BSY` while receiving graphics; wait for `RDY` before sending more.

## Coexisting with the Companion surface plugin

The [Companion surface plugin](https://github.com/JehutyPT/companion-surface-skaarhoj-rawpanel) connects to the panel as a second Raw Panel client. Every client receives every input event, and both share the panel's LEDs and displays (see [Connection](#connection)). Toward this app, the plugin must:

- **Ignore HWC 58, 60, 61 and 62.** The joystick and zoom rocker are this app's; Companion never sees them.
- **Rate-limit its writes.** Heavy traffic from one client delayed another client's `ack`s past 300 ms. A delay that leaves this app hearing nothing from the panel for 1 s trips its dead-man timeout and stops the cameras.
- **Own the sleep timer.** This app never sends `SleepTimer=`, `WakeUp!` or any other setting; the plugin decides how long the panel stays awake.

This app ignores the panel's encoders. `InputType.Relative` exists in [InputType.cs](../Core/Devices/InputType.cs) (fed through `IInput.Step`), but nothing produces relative input yet.

## Failure behavior

- Panel connection lost, or nothing heard from the panel (no `ack`, input or reply) for 1 s → stop all cameras driven by `SkaarhojGamepad`, mark it disconnected, reconnect with backoff. On reconnect, axes start at 0, and the dead-man timeout applies only after ~1 s of grace. (Built, #35/#37.)
- Panel falls asleep (`_isSleeping=1`) → stop all cameras driven by `SkaarhojGamepad`, make it unavailable and show it asleep rather than disconnected. On wake, axes start at 0 and the dead-man timeout gets ~1 s of grace again. (Built, #37/#39.)
- Companion or the surface plugin down → the panel joystick keeps working; this app does not depend on either for the panel.
