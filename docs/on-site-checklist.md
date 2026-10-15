# On-site checklist

Hardware checks for merged changes that could not be verified off-site. Work through them on the vMix PC with both CR-N500s, the joystick and Companion connected, before the build replaces the frozen production version. Tick each box, and delete an entry once all its boxes are ticked.

After the pending entries, run the [pre-show checklist](live-safety.md#pre-show-checklist-target-state).

## Pending

### #60: XC zoom and focus use the camera's speed ranges

An XC camera now reads its zoom and focus speed ranges from its `info.cgi` on each connect and maps the joystick's deflection onto them, instead of Canon's spec values (zoom 0..15, focus 0..2), which on firmware 1.5.2 (zoom 0..127, focus 0..63) made full-deflection zoom about 12 % of the camera's top speed. A camera that reports no usable range for an axis keeps the spec's for it. Each connect's first probe fetches the whole `info.cgi` once; later probes are unchanged. Pan, tilt, presets and VISCA cameras are unchanged. The cost: until that first read completes (a fraction of a second after the camera connects), zoom and focus still use the spec's ranges. Keep the log open (tray menu → **Show log**).

- [ ] Start the app with both XC cameras: each logs one `INFO` line "uses zoom speeds 0..127 (from the camera), focus speeds 0..63 (from the camera)".
- [ ] Zoom each XC camera tele and wide at full deflection: the zoom matches the VISCA top zoom speed on the same camera, and stops on release.
- [ ] With the camera in manual focus, focus far and near at full deflection: focus moves at a usable speed, and stops on release.

### #58: XC presets above 100 log at most once a minute

An XC camera now logs a recall of a preset above 100, which the CR-N500 does not have, at most once a minute per preset number, instead of on every recall. The recall still does nothing, its `WARN` reads as before, and each camera keeps its own minute for each number. VISCA cameras are unchanged. The cost: a second press of the same out-of-range recall within a minute shows no new line, so the Log window does not show each press. Keep the log open (tray menu → **Show log**).

- [ ] Map a USB joystick button to **Recall - 101**, select an XC camera, and press the button 3 times. The camera does not move, and the log has one `WARN` for preset 101. Restore the button's previous mapping.

### #59: `--settings-dir` runs the app on another settings folder

The app takes one switch, `--settings-dir <folder>`: every settings file and the log are then read from and written to that folder, created if missing, and nothing in `%APPDATA%\PTZJoystickControl` is read or written. Without it, nothing changes, except that the log's start line now names the settings folder in use. A folder that cannot be used, a `--settings-dir` with no folder, or any other argument that starts with `--` stops the app before it starts, with one line on standard error and exit code 2. The cost: `Gui.exe` has no console, so that line shows only when standard error is redirected; otherwise the only signs are exit code 2 and no tray icon. Run these from PowerShell in the app's folder, with the app quit (tray menu → **Quit**). First note the modified times of the files in `%APPDATA%\PTZJoystickControl`, with `Get-ChildItem "$env:APPDATA\PTZJoystickControl"`, and pick a new folder: `$base = Join-Path $env:TEMP "ptz59-$(Get-Random)"`.

- [ ] Run `.\Gui.exe --settings-dir "$base\settings"`. The app starts with an empty camera list and does not connect to the SKAARHOJ panel. `$base\settings` holds `PTZJoystickControl.log`, whose start line names that folder "(from --settings-dir)", and `SkaarhojPanel.json`. A camera selection from Companion meanwhile logs a `WARN` there, since it has no cameras: expected. Quit it: the files in `%APPDATA%\PTZJoystickControl` keep their modified times.
- [ ] Create a file, then start the app on a folder inside it, with standard error redirected: `New-Item -ItemType File -Force "$base\file"`, then `$p = Start-Process .\Gui.exe -ArgumentList "--settings-dir ""$base\file\settings""" -RedirectStandardError "$base\err.txt" -Wait -PassThru` (the doubled quotes keep a path with spaces in one argument). `$p.ExitCode` is 2, no tray icon appears, `Get-Content "$base\err.txt"` shows one "PTZJoystickControl: cannot start: settings folder … cannot be used" line, and `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` gets no new line.
- [ ] The same with `--settings-folder`: `$p = Start-Process .\Gui.exe -ArgumentList "--settings-folder ""$base\other""" -RedirectStandardError "$base\err2.txt" -Wait -PassThru`. `$p.ExitCode` is 2, and `Get-Content "$base\err2.txt"` shows one line naming the unknown argument `--settings-folder`.
- [ ] Start the app as the show starts it, with no switch. Both cameras and the controller mappings are as before, and the start line names `%APPDATA%\PTZJoystickControl`, with no "(from --settings-dir)".
- [ ] Delete what the checks created: `Remove-Item -Recurse "$base"`.

### #57: An invalid active position falls back to the last speed profile

When `SpeedProfiles.json` is usable but its active position is missing or outside its list, the last speed profile is now active instead of the first. In a list kept finest first, that is the fastest, so a bad position does not make the cameras slow on air. One `WARN` names the file, the problem and that speed profile, and the file is not rewritten until a speed profile changes. With no file, or one that cannot be used, "Full" is still active (#52), and removing the active speed profile still makes the first one active (#45). The cost: the last is used whatever its name, so if the list has been reordered with a slower speed profile last, that one is made active.

- [ ] If there is no `SpeedProfiles.json` yet, make another speed profile active in the window, which writes it, then make the previous one active again. Quit (tray menu → **Quit**) and copy `%APPDATA%\PTZJoystickControl\SpeedProfiles.json` aside. Set `"ActivePosition": 0` in it, start the app and open the log (tray menu → **Show log**). The last speed profile in the **Speed profiles** section is active, and one `WARN` names the file and that speed profile. Quit, put the copy back and restart.

### #56: A removed camera is disposed once its stops are out

Removing a camera from the camera list now also disposes it. A removed XC camera no longer probes the camera or logs under its name, and a removed VISCA camera closes its socket and frees its UDP replies, so a camera added again with the same address and port receives them. A camera removed while a controller drives it still stops: it sends its pending stops, then closes its connection. The cost: a removed XC camera that cannot be reached keeps trying its stops for up to 5 s, logging as before (its failure `WARN`, possibly the "not reachable" `INFO`, then the give-up `ERROR`), and a removed camera is no longer stopped when a controller disconnects. The **Select camera** mappings go by position in the camera list, so until the backups are restored, a controller button may select a different camera than before; Companion selects by **Companion #**. Back up `%APPDATA%\PTZJoystickControl\Cameras.json`, `Gamepads.json` and `SkaarhojGamepads.json` before starting, and keep the log open (tray menu → **Show log**).

- [ ] Add an XC camera for one CR-N500 and wait until it shows Connected. Remove it (the row's remove button, tooltip **Remove Camera**). Pull that CR-N500's network cable for about 10 s, then plug it back: no log line names the removed camera.
- [ ] Note a VISCA camera row's values, **Companion #** included. Remove it, then **Add VISCA camera** and give the new row the same values: no "another camera already receives the replies" `WARN`. Select it (from Companion or the USB joystick), then pan and release: the camera moves, stops, and shows Connected.
- [ ] Select a VISCA camera, hold a pan on the USB joystick and click that camera's remove button: the camera stops at once. Do the same with an XC camera added for a CR-N500.
- [ ] Quit (tray menu → **Quit**), restore the three backups, and restart.

### #53: XC stops are given up after 5 s

An XC camera's stop whose sends all fail is no longer retried without limit. Once it has been sent 3 times and 5 s have passed since it started with no send succeeding, the app gives it up: it sends no more of it, logs one `ERROR` naming the camera, the axes (`pan/tilt`, `zoom`, `focus`) and the last failure, and probes the camera again. The cost: the app no longer tries to stop that camera, so it may keep moving, even once the network returns, until that axis is moved and released, stopped again, recalled to a preset, or at its limit. VISCA cameras are unchanged. Keep the log open (tray menu → **Show log**).

- [ ] Pan an XC camera, pull its network cable, release the stick and plug the cable straight back. The camera stops when the network returns, with no `ERROR`. If the `ERROR` appears, the link took more than 5 s to come back: note roughly how long, to tune the bound. A managed switch can take up to about 30 s to forward on a re-plugged port.
- [ ] Pan an XC camera, pull its network cable, release the stick and wait about 10 s. Within about 4 s of the release the status shows Disconnected; about 5 s after the release, one `ERROR` names the camera, `pan/tilt` and the last failure; the app stays responsive throughout. Plug the cable back: the camera shows Connected again. Note whether it was still moving when the network returned (expected: yes, unless it reached its limit). Moving and releasing its stick stops it.
- [ ] If another client can hold the camera's control (see #47's check that drives one camera through its VISCA and XC rows), pan the XC camera, let the other client take control, then release the stick. Expect, in order: one `no camera control right` `WARN`, then one `ERROR` about 5 s after the release, then Connected again about 3 s later, because status requests are still answered.

### #55: XC login applies only on Log in

An XC camera's login now changes only when you press **Log in**, next to its row's **Username** and **Password**: typing in those fields changes only the row, and **Log in** applies both together, saves them in `Cameras.json` and tries them at once. **Log in** is enabled only while the fields differ from the login the camera uses. While the camera's requests fail, the row shows why on a line below its fields, in the words of the log's `WARN`. VISCA cameras are unchanged. Use a CR-N500 with guest (unauthenticated) control off, as in #49's entry below, and keep the log open (tray menu → **Show log**).

- [ ] With the right login applied and the camera Connected, retype its password one character at a time, ending with a wrong one, without pressing **Log in**. The log gets no line, the status stays Connected, and **Log in** is enabled. Pan and release from the USB joystick: the camera moves and stops. Open `Cameras.json` in a text editor: it still holds the right password.
- [ ] Press **Log in**. Within about a second, one `WARN` says the camera refused the username and password, and the row shows that reason. Within about 4 s the status shows Disconnected. **Log in** is disabled, and `Cameras.json` now holds the wrong password.
- [ ] Type the right password and press **Log in**: within about a second the status shows Connected and the reason goes, with no new `WARN`.
- [ ] Type another password and, without pressing **Log in**, quit the app (tray menu → **Quit**) and restart it: the camera shows Connected with no login `WARN`, and **Log in** is disabled.
- [ ] Clear both fields and press **Log in**: the row shows "the camera requires a login; set username and password", with one such `WARN`, and within about 4 s the status shows Disconnected. Quit and open `Cameras.json`: the camera's entry has neither `Username` nor `Password`. Restart, type the login and press **Log in**: Connected.
- [ ] Paste a username that contains a tab, copied from a text editor: the field shows "No control characters" in red, **Log in** is disabled while it does, and the log gets no line. Undo it.
- [ ] Pull an idle XC camera's network cable: within about 4 s the status shows Disconnected and the row shows the reason, in the words of the log's `WARN`. Plug it back: Connected, and the reason goes.
- [ ] With a VISCA row and an XC row in the list, their Status, Companion # and Remove columns line up, a typical username is readable in its field, and a reason line does not cover another row.

### #54: XC preset recall goes out next

An XC camera's preset recall now goes out in the next request, instead of waiting until a stop still being sent is done (#48), and `Stop()` no longer drops it. It takes the place of every stop pending when it goes out: if the recall succeeds, those stops are not sent, and the app relies on the recall to take the camera to the preset and stop it there; if it fails, they are sent again. The recall itself is never retried. VISCA cameras are unchanged. Keep the log open (tray menu → **Show log**).

- [ ] Pan, release the stick and at once recall a distant preset, several times, from the USB joystick. Each recall starts at once, is never cut short, and ends at the preset.
- [ ] Pan at full deflection and hold the stick there, so the camera keeps panning and the app sends nothing more, then recall a distant preset. The camera leaves the pan, goes to the preset and stops there. Release the stick only once it has arrived: it stays. The app relies on this when a recall takes the place of a stop not sent yet. Repeat, releasing the stick while the camera is still on its way, and note whether it stops short of the preset: that release is a stop sent after the recall.
- [ ] Recall a distant preset at **Preset Recall Speed** 1. While it moves, unplug the USB joystick, which stops every camera it drove. Note whether the camera stops at once or completes the recall. Plug the joystick back.
- [ ] Pull an XC camera's network cable, recall a preset, and plug the cable back after about 3 s. The camera does not move when the network returns: a failed recall is not retried.
- [ ] Pan, pull the cable, release the stick and at once recall a preset. Plug the cable back about 2 s later (longer pulls are #53's check). The camera stops within about a second of the network returning, and does not go to the preset: the failed recall restored the stop, and was not sent again itself. The log shows the failure `WARN` and, once the network returns, the recovery `INFO`.
- [ ] On each XC camera, open `http://<camera address>/-wvhttp-01-/info.cgi`. Check `p.<n>.content.ptz=enabled` for every camera preset the show recalls from the app, and note `p.<n>.content.focus`. Report any preset without PTZ before the show: a successful recall of it may leave running a manual move whose stop the recall replaced.

### #40: SKAARHOJ dead-man rides out an app freeze

Needs the panel at 192.168.1.163 set up as in the #35 entry, with no other Raw Panel client connected. The link now counts any line from the panel as hearing from it, sends `ping` only after 500 ms of quiet and drops after 1 s of silence; a freeze of the app itself restarts that count instead of dropping the link. These checks change no panel setting.

- [ ] With the link up and no camera moving (leave the stick alone), freeze the app for about 1.5 s, then let it run. On the vMix PC: Resource Monitor (`resmon`) → **CPU** → right-click `Gui.exe` → **Suspend Process**, wait about 1.5 s, then **Resume Process** (or Sysinternals `pssuspend Gui.exe`, then `pssuspend -r Gui.exe`). On a Mac: `kill -STOP <pid>`, wait about 1.5 s, `kill -CONT <pid>`. The log shows exactly one `WARN` "the app stalled, not the panel at 192.168.1.163:9923: a dead-man check ran … ms late", and **no** "link down".
- [ ] Pull the panel's Ethernet cable: a `WARN` "link down … the panel was silent for …" line appears within about 1.1 s. Plug it back in: "link up" follows without restarting the app.

### #49: XC camera login

An XC camera row now has **Username** and **Password** fields, for a camera that requires a login instead of allowing guest control. With the username empty, the app drives the camera as a guest, as before. With a login set, the app answers the camera's login challenge (HTTP 401) with Digest or Basic authentication, then sends its requests already logged in. The login is saved unencrypted, in plain text, in `Cameras.json`: treat that file, and any backup of it, like a password. VISCA cameras are unchanged. Keep the log open (tray menu → **Show log**).

- [ ] In one CR-N500's web settings, turn off guest (unauthenticated) control, and note a username and password allowed to move the camera. In its XC row, with **Username** and **Password** empty: within a few seconds the status shows Disconnected, and the log has one `WARN` with "camera requires a login; set username and password", and at most one more a minute while it lasts.
- [ ] Type the username and password in the XC row (the password shows as dots) and press **Log in**: within a few seconds the status shows Connected. Pan, tilt, zoom, focus (manual focus) and recall presets from the USB joystick: each works and stops on release, as with guest control.
- [ ] Change the password to a wrong one and press **Log in**: within a few seconds the status shows Disconnected, with one `WARN` saying the camera refused the username and password. Pan and release, then wait two minutes: the log gets at most one such `WARN` a minute, not a flood, and one `ERROR` about 5 s after the release, when the refused stop is given up (#53). Note whether the camera then locks the login or the PC out for a while (the right password also fails). Restore the right password and press **Log in**: Connected again.
- [ ] With the login set, pan the camera and pull its network cable while still moving the stick. Release the stick and plug the cable straight back (longer pulls are #53's check): the camera stops within about a second of the network returning, and the app stays responsive throughout.
- [ ] With the login set, leave the camera idle for longer than its login nonce lifetime (unknown: try 30 minutes), then pan: it moves at once and stops on release, and the log has no login `WARN` for the camera.
- [ ] Quit the app (tray menu → **Quit**) and open `Cameras.json`: the XC entry with a login holds `Username` and `Password` in plain text, and an XC entry without a login has neither key. Restart: the camera shows Connected, with no login `WARN`. Search the log files (`PTZJoystickControl*.log`) for the username and for the password: neither appears.
- [ ] If the camera's settings let you choose the authentication method (Digest or Basic), repeat the second check with each. Afterwards, set the camera's guest control as the show needs it, and the XC row's login to match, pressing **Log in**.

### #48: XC zoom, focus and presets

An XC camera now also zooms, focuses (manual focus) and recalls presets, on the same send path as its pan and tilt (#47). The app's preset N (**Recall - N**) recalls the camera's preset N; presets above 100, which the CR-N500 does not have, do nothing and log a `WARN`, at most once a minute per preset number. A recall moves at the **Preset Recall Speed** last set on that camera (1..24, mapped onto the camera's preset speeds 1..100); until one has been set since the app started, the camera uses the speeds saved with its preset. Focus mode, focus lock, power and preset save still do nothing on an XC camera and only log an `INFO` line; Companion's Canon module covers them. VISCA cameras are unchanged. Keep the log open (tray menu → **Show log**).

- [ ] On each CR-N500, open `http://<camera address>/-wvhttp-01-/info.cgi` in a browser and record `s.firmware`, `c.1.zoom.speed.min`, `c.1.zoom.speed.max`, `c.1.focus.speed.min` and `c.1.focus.speed.max`. The app now uses the ranges the camera reports, falling back to Canon's spec (zoom 0..15, focus 0..2) only when it reports none usable (#60): the log's `INFO` line for each camera's speed ranges should match what you recorded.
- [ ] Zoom each XC camera tele and wide from the USB joystick and from the panel, slowly and at full deflection: the zoom speed follows the deflection, and the zoom stops on release.
- [ ] With an XC camera in manual focus, focus far and near: it moves and stops on release. Put the camera in auto focus (from Companion's Canon module or the camera's web page: the app's **Focus mode** does nothing on XC) and focus far and near again: note whether it moves. After the app has focused and stopped, note `c.1.focus.speed` in `info.cgi`: every focus request from the app sets the camera's manual-focus speed, and a stop sets it to 0.
- [ ] With an XC camera in auto focus, pan it and, while holding the stick, select another camera from Companion: it stops. Pan it again and, while holding the stick, unplug the USB joystick: it stops. Plug the joystick back, select the camera and recall a preset: it moves. Throughout, the log shows no `WARN` for the camera, and it stays Connected. These stops also stop focus: if the camera refused that in auto focus, the app would retry the stop every 50 ms, with a `WARN` in the log and the status turning Disconnected, and give it up after 5 s with an `ERROR` (#53), unless a preset recall on that camera took its place and succeeded first (#54).
- [ ] On each XC camera, recall presets 1, 2 and the highest preset used in the show from the USB joystick, then the same preset numbers from Companion's Canon module: each number goes to the same position both ways. Set **Preset Recall Speed** to 1, 12 and 24 in turn, and recall a distant preset from the joystick after each: the move gets faster in proportion, 24 being the camera's fastest preset speed.
- [ ] Pan an XC camera, release the stick and at once recall a preset: the recall is not cut short, and the camera ends at the preset.
- [ ] On an XC camera, press buttons mapped to **Focus mode**, **Focus lock**, **Power** and a preset **Set**: nothing happens on the camera, and each logs one `INFO` line saying it "is not supported on an XC camera" (at most one per operation per minute). Press **Toggle Set** and then a preset button: nothing is saved, and the next press of that button recalls the preset.
- [ ] Zoom an XC camera and pull its network cable while still zooming. Release the stick and plug the cable straight back (longer pulls are #53's check): the zoom stops within about a second of the network returning, and the app stays responsive throughout.

### VISCA UDP reply listeners follow address and port changes

Each VISCA camera over UDP now listens for replies on the IP:port it registered, so changing a camera's address or port no longer leaves a stale listener behind, and loading `Cameras.json` can no longer fail with "Only one callback is allowed for each remote endpoint" and start with no cameras. Two cameras on the same IP:port no longer throw: the first opened keeps the replies, and the second logs a `WARN` and still sends its commands.

- [ ] Start the new build with the existing `Cameras.json`: both CR-N500 rows load with their settings, and the log (tray menu → **Show log**) has no settings `WARN` or `ERROR`. Pan, tilt, zoom and focus each camera: it moves and stops on release.
- [ ] In a UDP camera row, change the **Port** to another number and back, then the **IP Address** to another address and back: no `WARN` or `ERROR` in the log, and the camera still moves and stops on release.
- [ ] Set camera 2's **IP Address** and **Port** to camera 1's: one `WARN` line says another camera already receives the replies from that IP:port, both rows stay in the list, and the app keeps running. Undo it: camera 2 moves and stops again. Quit and restart the app: both rows load as before.

### #47: XC cameras (pan and tilt over HTTP)

The camera list now has **Add VISCA camera** and **Add XC camera** buttons. An XC camera drives a CR-N500 over Canon's XC protocol (HTTP) instead of VISCA. This entry covers its pan and tilt; #48 above covers its zoom, focus and presets. VISCA cameras are unchanged. Back up `%APPDATA%\PTZJoystickControl\Cameras.json` before starting the new build, and keep the log open (tray menu → **Show log**): XC request failures are logged at `WARN` with the reason.

- [ ] Start the new build with the existing `Cameras.json`: no settings `WARN` or `ERROR` in the log, and the VISCA rows are unchanged. **Add VISCA camera** adds a VISCA row as **Add** did. **Add XC camera** adds a row named `Camera <n>` with only **Name**, **Address** (empty), **Port** (80), **Username** and **Password** (empty; #49 above), **Log in** (disabled; #55 above), **Status** (Disconnected) and **Companion #**: no Protocol, Header or Interval, and never "Unsupported camera type".
- [ ] For each CR-N500, add an XC camera next to its VISCA row, with the camera's IP address, port 80 and a free **Companion #**. Within a few seconds its status shows Connected (green). Pull the camera's network cable: within about 4 s the status shows Disconnected (red), with one `WARN` and one "not reachable" line in the log. Plug it back: Connected again, with one line saying so.
- [ ] In an XC row, type an invalid address (`192.168.1`, `10.0.0.1:80`), a port of 0, then a **Companion #** of 0 or another camera's number, one at a time: each field shows a red border and message, as in a VISCA row. Undo each. Quit and restart the app: the XC rows keep their name, address, port and Companion #, and the VISCA rows are unchanged.
- [ ] Select an XC camera from Companion, then pan and tilt it from the USB joystick and from the panel: it responds and stops on release.
- [ ] At a low **Max speed** (or the finest speed profile), very small stick deflections give smooth, fine moves.
- [ ] Move the stick continuously (circles) for a minute while watching the camera and the log. The app spaces requests at least 30 ms apart per camera, as Canon recommends: the camera follows without visible lag, stutter or queued-up moves after the stick stops, and the log shows no `WARN`. Note whether the gap could be lowered.
- [ ] Note whether the order of the parameters within a request matters to the camera. The app sends each axis's speed before its direction, so a quick reversal from a fast pan left to a slow pan right should go slowly right at once, with no jerk at the old speed: report any jerk.
- [ ] Pan an XC camera and pull its network cable while still moving the stick. Release the stick and plug the cable straight back (longer pulls are #53's check): the camera stops within about a second of the network returning and stays stopped for at least 30 s (watch for a late move), and the app stays responsive throughout.
- [ ] Pan an XC camera and, while holding the stick, select another camera from Companion: the XC camera stops. Pan it again and, while holding the stick, unplug the USB joystick: it stops.
- [ ] Note whether guest (unauthenticated) control works, with the row's **Username** empty: if the camera requires a login, the status stays Disconnected and the log shows "camera requires a login; set username and password" (#49 above covers the login).
- [ ] Drive one CR-N500 through its VISCA row and its XC row at the same time (two controllers): note how the camera behaves, and any `no camera control right` `WARN` in the log.
- [ ] Compare VISCA and XC on the same camera, side by side: speed range, smoothness at small deflections, and how quickly it stops.

### #46: Companion steps and shows the speed profile

Companion can step the active speed profile (#45) and show it on a button. In Companion's generic HTTP connection, set up (see [companion-integration.md](companion-integration.md#companion--app-speed-profiles)) a button whose GET is `http://localhost:12345/?SpeedProfile=faster`, one with `?SpeedProfile=finer`, and a custom variable read from `?SpeedProfile=current` after each step and on a time-interval trigger, shown on a button as the speed profile's name and position.

- [ ] Press **finer**, then **faster**: each press moves the app window's active speed profile one step, and the stick speed changes on the next movement.
- [ ] Press **finer** until the finest speed profile is active, then twice more: nothing changes and it does not wrap to the fastest. Do the same with **faster** at the fastest. The log (tray menu → **Show log**) has no `WARN` or `ERROR` for these presses.
- [ ] In a browser on the vMix PC, open `http://localhost:12345/?SpeedProfile=current`: it shows the active speed profile, e.g. `{"position":2,"count":4,"name":"Medium"}`. The Companion button shows the same name and position, updates right after each step press, and follows a change made in the app window within the trigger's interval.
- [ ] Pan a camera and, while holding the stick, press **faster** and **finer** several times: the camera keeps moving without stopping, stuttering or lagging, and it stops on release. Its speed changes from the stick's next movement, even a slight one while it is held.
- [ ] Select each camera from Companion (`?SelectCamera=N`): the app follows. Then press a button whose GET selects a camera and steps in one request (e.g. `?SelectCamera=2&SpeedProfile=faster`): the app selects camera 2 and the speed profile steps.

### #45: Speed profiles

A **speed profile** sets a top speed for pan, tilt, zoom and focus together. The camera gets the input's own speed (the shaped value × its Max speed or Speed %) × the active speed profile's % for that axis. One ordered list, finest first, applies to every camera; it is edited in the new **Speed profiles** section under the cameras and saved in `%APPDATA%\PTZJoystickControl\SpeedProfiles.json`. With no file, these defaults are used with "Full" active (#52), so the first start moves at the previous build's speeds until a finer speed profile is chosen:

| Speed profile | Pan % | Tilt % | Zoom % | Focus % |
|---|---|---|---|---|
| Fine | 15 | 15 | 30 | 40 |
| Medium | 35 | 35 | 55 | 70 |
| Fast | 65 | 65 | 80 | 100 |
| Full | 100 | 100 | 100 | 100 |

The older entries below compare speeds with the previous build: run them with "Full" (100 % on every axis) active, which changes no speed.

- [ ] First start with no `SpeedProfiles.json`: the **Speed profiles** section shows the four speed profiles above in that order, with "Full" active, and the log (tray menu → **Show log**) has no settings `WARN` or `ERROR` and no `Binding` lines. Pan, tilt, zoom and focus on both CR-N500s run at the previous build's speeds.
- [ ] At "Fine", full pan and tilt deflection on both CR-N500s is clearly slower than at "Full", and small deflections are usable for framing on air.
- [ ] Pan a camera and, while holding the stick still, make another speed profile active in the window: the speed changes on the next stick movement, not before, and the camera still stops on release.
- [ ] Recall the same preset with "Fine" active and then with "Full" active: the preset recall speed is the same on every speed profile.
- [ ] Rename a speed profile, change one of its percentages, add one, move it up and make it active. Quit (tray menu → **Quit**) and restart: the list, the edits and the active speed profile are kept.
- [ ] Tune the default percentages on site until each speed profile suits the show, and record the chosen values (name and four percentages each) so the built-in defaults can be updated to match.

### #44: Camera abstraction

Everything outside the VISCA camera now works with a protocol-neutral camera (`ICamera`) instead of the VISCA camera class, so that another camera type can join the camera list later (#47). Nothing should look or behave differently: the camera rows, `Cameras.json`, the controller mappings and camera control are unchanged. Back up `%APPDATA%\PTZJoystickControl\Cameras.json`, `Gamepads.json` and `SkaarhojGamepads.json` before starting the new build, and note each camera row's fields in the previous build.

- [ ] Start the new build with the existing settings files: no settings `WARN` or `ERROR` in the log (tray menu → **Show log**). Both CR-N500 rows show the same **Name**, **IP Address**, **Port**, **Protocol**, **Header**, **Interval** and **Companion #** as in the previous build, with the connected status, and no row reads "Unsupported camera type". The USB joystick's and the panel's mappings are as before.
- [ ] In a camera row, type an invalid IP address, an invalid port, then a **Companion #** of 0 or another camera's number, one at a time: each field shows the same red border and message as the previous build. Undo each. **Add VISCA camera** (the **Add** button before #47) adds a row with the same fields and the next free Companion #, and that row's remove button removes it again.
- [ ] Rename a camera, then quit and restart the app: the new name is kept and every other field is unchanged. Rename it back.
- [ ] With the USB joystick selected, pan, tilt, zoom and focus both CR-N500s: the speeds are as in the previous build, and each camera stops on release.
- [ ] Select each camera from its Companion button, then by clicking its row in the main window: the selected controller drives it, and the tray icon follows the selection.
- [ ] Pan camera 1 and, while holding the stick, select camera 2 from Companion: camera 1 stops, and the stick now drives camera 2.
- [ ] Pan a camera and, while holding the stick, unplug the USB joystick: the camera stops. Plug it back in, pan again and, while holding the stick, select the SKAARHOJ panel in the controller list (which deactivates the joystick): the camera stops.
- [ ] With the panel selected, repeat the Companion switch check with the panel's stick. Then pan a camera and, while holding the stick, pull the panel's Ethernet cable: the camera stops within about 1 s. The stop when the panel falls asleep is the same stop; the #39 held-still check covers it, run with this build.

### #43: Max speed is a percentage

Pan, tilt, zoom and focus **Max speed** (axes) and **Speed** (buttons) are now percentages of the full range (0–100 %) instead of VISCA steps. Saved steps are converted on load to the smallest percentage that still reaches the same top speed (pan 12 of 24 becomes 48 %, zoom 5 of 7 becomes 72 %); at 100 % the cameras get exactly the speeds the previous build sent. Back up `%APPDATA%\PTZJoystickControl\Gamepads.json` and `SkaarhojGamepads.json` before starting the new build, and note each axis's and button's Max speed/Speed in the previous build.

- [ ] Start the new build with the existing `Gamepads.json` and `SkaarhojGamepads.json`: no settings `WARN` or `ERROR` in the log (tray menu → **Show log**). Every axis mapped to pan, tilt, zoom or focus shows its **Max speed** as a percentage (`100 %` where it was the top step), and the dropdown offers 0 %…100 %.
- [ ] On both CR-N500s, pan, tilt, zoom and focus slowly and fully from the USB joystick, then from the SKAARHOJ panel: the speeds feel the same as the previous build at the converted values, full deflection reaches the same top speed, and each camera stops on release.
- [ ] The output label next to a moving axis reads a direction and a percentage (e.g. `High 37 %`), and `Stop` when released.
- [ ] A button mapped to pan or zoom moves the camera at the same speed as before, and stops on release.
- [ ] Quit and restart the app: every Max speed and Speed shows the same percentage as before the restart (no second conversion).

### #39: SKAARHOJ panel shows asleep and survives waking

Needs the panel set up as in the #37 entry, with the panel selected as the controller and its stick mapped. The app never changes the panel's sleep timer; a short one must be set beforehand, and **only once agreed** (ask before changing any panel setting): with the app quit, have the Companion surface plugin or a test tool send `SleepTimer=60000`, then disconnect the tool. This is the same setup as the #37 sleep check; run both together. If the panel's timer is left short it sleeps every minute during the show, so restore it at the end.

- [ ] Start the app and leave the stick alone until the log shows `INFO … asleep`. The controller list shows **SKAARHOJ panel (192.168.1.163) (Asleep: press a panel button)**, not "(Disconnected)". Deflect the stick: nothing moves.
- [ ] Press any panel button: the log shows `INFO … awake` with **no** "link down" or "link up" around it, the list shows the panel connected again (no "Asleep", no "Disconnected"), and the stick drives the cameras. If a "link down … silent" appears right before or after "awake", note on #39 whether it came before the `awake` line (the panel stalls before reporting the wake) or after.
- [ ] Each sleep and each wake logs exactly one `INFO` line.
- [ ] Held-still check: pan a camera and hold the stick deflected and still, without moving it, until past the timer (over a minute). Either the panel stays awake and the camera keeps moving until release, or the panel sleeps and the camera stops within about half a second. Note which in docs/skaarhoj-integration.md ("Sleep").
- [ ] Afterwards, set the timer back to 4 hours (`SleepTimer=14400000`, again only once agreed) or power-cycle the panel (a reboot also restores 4 hours).

### #37: SKAARHOJ joystick drives cameras

Needs the SKAARHOJ PTZ Extreme at 192.168.1.163 with `SkaarhojPanel.json` set up as in the #35 entry, both CR-N500s, and no other Raw Panel client connected (close SKAARHOJ's updater app and any test tool). The app only reads the panel: its buttons, knobs, lights and displays do nothing, and a dark panel is expected. Camera selection comes from Companion.

- [ ] Start the app. The controller list shows **SKAARHOJ panel (192.168.1.163)** beside the USB joystick, connected once the log shows "link up".
- [ ] Select the panel in the list. The USB joystick is deactivated: its axes move nothing. Map `LR` to *Pan*, `UD` to *Tilt* and `Rotate` to *Zoom* (and `Zoom`, the rocker, if wanted).
- [ ] Select camera 1 from Companion. Push the stick left/right and up/down, and twist it: camera 1 pans, tilts and zooms. Release each one: the camera stops. Repeat on camera 2 after selecting it from Companion.
- [ ] Hold the stick at full deflection for a few seconds, then twist it fully and hold: the camera keeps moving at a steady speed, with no stutter or stop, until released.
- [ ] Pan camera 1 and, while holding the stick, select camera 2 from Companion: camera 1 stops, and the stick now drives camera 2.
- [ ] Turn on **Advanced** and set `LR`'s **Expo** to 50 %: small stick deflections pan noticeably slower, full deflection still reaches max speed, and release stops the camera.
- [ ] Quit and restart the app: the panel is still the selected controller, with the same mappings and Expo.
- [ ] Pan a camera and, while holding the stick, pull the panel's Ethernet cable: the camera stops within about 1 s, the log shows "link down", and the panel shows disconnected in the list.
- [ ] Plug the cable back in without touching the stick: after "link up" (within about 30 s) nothing moves. Then move the stick: the camera follows again.
- [ ] Sleep. Beforehand, with the app quit, use a test tool to send `SleepTimer=60000` to the panel, then disconnect the tool. Start the app and leave the stick alone until the log shows `INFO … asleep`. Deflect the stick: nothing moves. Press any panel button: the log shows `INFO … awake`, and the stick drives the cameras again. The timer runs from the last activity, so it expires a minute after the app connects even if connecting wakes the panel; if the panel never sleeps, note it on #39. Afterwards, set `SleepTimer=` back (4 hours is `14400000`), or power-cycle the panel. How the list shows the asleep panel, and the wake without a reconnect, are checked in the #39 entry.
- [ ] Select the USB joystick in the list: the panel's stick moves nothing, and the USB joystick drives both cameras and stops on release. Select the panel again: the USB joystick moves nothing.
- [ ] With the panel selected, unplug the USB joystick and plug it back in: the panel keeps working, and the USB joystick does not become active. With the USB joystick selected, pull the panel's cable: the USB joystick keeps working.
- [ ] Set `"Address": ""` in `SkaarhojPanel.json` and restart: the controller list shows only the USB joystick, and it works as before. Put the address back.

### #30: Avalonia 12 UI framework

The UI framework moved from Avalonia 0.10 to 12, so every window, the tray icon and the tray menu are drawn by new code. The look should be unchanged (dark Fluent theme, compact). The Windows zip is now published for the `win-x64`/`win-x86` RIDs and still has the same four native DLLs beside `Gui.exe`. Back up `%APPDATA%\PTZJoystickControl\Cameras.json` and `Gamepads.json` before starting the new build.

- [ ] Unzip `PTZJoystickControl_v<version>_win_x64.zip` (and `_win_x86.zip` on a 32-bit PC) into a fresh folder: it holds `Gui.exe`, `av_libglesv2.dll`, `SDL2.dll`, `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `SdlGamepads.dll.config` and a few small `.pdb` files (no `libSkiaSharp.pdb` or `libHarfBuzzSharp.pdb`). Double-click `Gui.exe`: the app starts.
- [ ] The main window draws normally on the vMix PC (no blank, black or white window): dark theme, blurred (acrylic) or plain dark grey background, title on the left, minimise/close buttons work, text is sharp and readable (the font may differ slightly from the previous build). The Companion warning banner appears only if the Companion listener could not start.
- [ ] Camera list: both CR-N500 rows show name, IP, port and the connected icon as before. Type an invalid IP or port in a row: the field shows a red border and validation message as the previous build did, and no `ERROR` appears in the log. Undo it. **Add** adds a row and the row's remove button removes it again.
- [ ] Controller list: the joystick is listed, its inputs show the same mappings, and the axis bars move when the stick moves. Turn on **Advanced**: dead zone, saturation and **Expo** fields and the sliders look as before (the slider overlay over the progress bar lines up).
- [ ] Move every axis and press buttons for a minute while watching the Log window (tray menu → **Show log**): no `ERROR`, `CRITICAL`, `invalid thread` or `Binding` lines appear. Pan, tilt, zoom, focus and presets drive both cameras; each camera stops on release.
- [ ] Tray icon: shows the selected camera's number and changes when Companion or a button selects another camera. Right-click: the menu shows **PTZ Joystick Control**, **Check for updates**, **Show log** and **Quit**, and each item works.
- [ ] **Check for updates** opens the Update Checker window, which shows a result and closes with its OK button.
- [ ] Log window: lines are readable and can be selected and copied; **Clear Log** empties it; closing it hides it and **Show log** brings it back.
- [ ] With the app running, double-click `Gui.exe` again: the borderless "already running" window appears and its OK button closes it; the first instance keeps running.
- [ ] Close the main window: the app keeps running in the tray, and clicking the tray icon shows it again. Quit from the tray: the process ends (no `Gui.exe` left in Task Manager) and the log ends with `PTZJoystickControl stopped`.
- [ ] `Cameras.json` and `Gamepads.json` are unchanged after the run, apart from settings you changed on purpose.

### #20: Expo response curve per axis

Each axis has a new **Expo** setting next to **Dead zone** (0 % = linear, as before; up to 100 %). It only bends pan, tilt, zoom and focus speeds between the dead zone and the saturation zone. Existing settings load with Expo 0 %. Back up `%APPDATA%\PTZJoystickControl\Gamepads.json` before starting the new build.

- [ ] Start the new build with the existing `Gamepads.json`: no settings `WARN` or `ERROR` in the log (tray menu → **Show log**), every axis shows **Expo** 0 %, buttons show no Expo field, and the joystick mappings, dead zones and saturations are unchanged.
- [ ] With Expo 0 %, pan, tilt and zoom each camera slowly and fully, and release: the speeds feel the same as the previous build (judge by the camera, not the output label next to the axis: since #43 it shows a percentage where the previous build showed a VISCA step) and each camera stops on release.
- [ ] Set the pan axis's Expo to 50 %: small deflections give noticeably slower pan than before, a deflection just past the dead zone still moves the camera (output label not `Stop`), and full deflection still reaches the configured max speed (output label shows the max-speed value; since #43 that is a percentage, e.g. `High 100 %`). Release: the camera stops.
- [ ] Repeat at 100 %: fine control near centre, full deflection still reaches max speed, release stops.
- [ ] Quit and restart: the axis still shows the Expo you set. Set it back to the value the operator wants for the show.
- [ ] An axis mapped to a static command (e.g. *Preset*) with Expo set above 0 % still triggers at the same deflection as before.

### #15: The app no longer calls Companion

Companion owns camera selection. The app no longer presses a Companion button when the selected camera changes (that call used Companion's legacy HTTP API, which the production Companion has disabled), the camera row's **Bitfocus Page/Button** fields are gone, and `Host` and `Port` in `Companion.json` are no longer used. Companion → app selection (`?SelectCamera=N`, `?SetCameraPreview=N`) is unchanged. The other entries no longer ask for Companion's feedback to update for this reason. Before starting the new build, back up `%APPDATA%\PTZJoystickControl\Cameras.json` and `Companion.json` (if present).

- [ ] Start the new build with the existing settings files: it starts with no settings `WARN` or `ERROR`, both cameras are listed with the same name, IP address, port, protocol, header, interval and Companion # as before, and the joystick mappings are unchanged.
- [ ] If `Companion.json` has `Host` or `Port` (it probably has `"Host": "192.168.68.110"` from #16): the log (tray menu → **Show log**) has one line `INFO [CompanionSettingsStore] Companion settings in ...\Companion.json: Host is no longer used (the app makes no calls to Companion) and ignored` (or `Host and Port are no longer used ...`), not a `WARN`, followed by `Companion: listening on http://localhost:12345/` (or the `ListenerPort` set in the file). The app never removes the keys; delete them by hand afterwards if you like and restart: the `INFO` line is gone.
- [ ] Each camera row shows Name, IP Address, Port, Protocol, Header, Interval, Status and Companion #, then the Remove button, with no Bitfocus Page/Button fields and no empty gap before the Remove button.
- [ ] Press the operator's Companion camera buttons in turn: the app's selection (main window and tray icon) follows each one, and the joystick drives that camera and stops it on release.
- [ ] Press the controller button mapped to *Select camera* for each camera: the app's selection follows and the joystick drives that camera. Companion is not told, so its buttons may still show the camera Companion last selected; that is expected.
- [ ] After the checks above and before the other entries (some log a Companion `WARN` on purpose), search the log for warnings: `findstr /C:" WARN " "%APPDATA%\PTZJoystickControl\PTZJoystickControl.log" | findstr /I companion` prints no line from this start (in particular no `WARN [BitfocusCompanionClient]`).

### #35: SKAARHOJ panel link

Needs the SKAARHOJ PTZ Extreme at 192.168.1.163, with no other Raw Panel client connected (close SKAARHOJ's updater app and any test tool). The link alone moves nothing: the panel's joystick drives cameras only once the panel is selected as the controller (#37), and its buttons do nothing yet. **While the app is connected, the panel switches off all its LEDs and displays** and leaves them to the app, which sends none yet, so a dark panel is expected; it lights up again when the app quits.

- [ ] Quit the app. In the settings folder (`%APPDATA%\PTZJoystickControl`), open `SkaarhojPanel.json` (start and quit the app once if it is missing) and set `"Address": "192.168.1.163"`, keeping `"Port": 9923`. Start the app.
- [ ] Tray menu → **Show log**: an `INFO [SkaarhojPanelLink]` "link up to 192.168.1.163:9923" line, then `_model=SK_PTZEXTREMEV2` and `_version=…`. No "link down" in the first few seconds (the ~0.5 s post-connect stall must not trip it).
- [ ] The logged `_connections=` reply lists only the vMix PC's address.
- [ ] Pull the panel's Ethernet cable: within about 1 s a `WARN` "link down … the panel was silent for …" line appears (about 1000–1100 ms). The joystick still drives both cameras.
- [ ] Plug the cable back in: within about 30 s an `INFO` "link up" line appears without restarting the app, followed by `_connections=` listing only the vMix PC. A single further down/up right after reconnecting is acceptable (seen in the protocol tests); repeated flapping is not.
- [ ] Quit from the tray: the log ends with "link down … (stopped)" then `PTZJoystickControl stopped`, and the panel's own lights come back.
- [ ] Set `"Address": ""`, start the app: no `SkaarhojPanelLink` lines in the log, and the joystick and Companion work as before.

### SDL2 upgrade to SDL 2.31 (`ppy.SDL2-CS` 1.0.741-alpha)

- [ ] The joystick is detected at startup and appears in the app.
- [ ] Every axis and button still drives its mapped command (pan, tilt, zoom, focus, presets, camera select).
- [ ] Unplug and replug the joystick: it is detected again and still drives the cameras.
- [ ] On Apple Silicon (osx-arm64), if used: the app starts and detects the joystick.

### #16: Companion host and ports in `Companion.json`

The Companion listener port moved from code to an optional `Companion.json` next to `Cameras.json` (`%APPDATA%\PTZJoystickControl\` on the vMix PC). With no file nothing changes. The app reads it only at startup. (#16 also added `Host` and `Port` for the app's calls to Companion; #15 retired them, see its entry.)

- [ ] With no `Companion.json`, start the app: the log (tray menu → **Show log**) has an `INFO` "Companion: listening on http://localhost:12345/" and no Companion settings `WARN`. Select each camera from Companion and with the joystick: the app follows.
- [ ] Set `"ListenerPort": 12346`, restart, and point Companion's HTTP module at `http://localhost:12346/`: selecting a camera from Companion switches the app's camera. Put both back afterwards (remove the key, restart, re-point Companion to 12345) and check selection again.
- [ ] Replace the file's contents with `{ not json` and restart: the app starts, the log shows a `WARN` "Failed to load Companion settings" naming the file and the `INFO` line shows the defaults, and the joystick selects, moves and stops both cameras.
- [ ] Set `{ "ListenerPort": 70000 }` and restart: the app starts with a `WARN` (invalid ListenerPort) and the `INFO` line shows `http://localhost:12345/`. Delete or fix the file and restart before the show.

### #36: Relative input type (controller page regression)

Nothing produces a relative input yet, so it has no checks of its own. This change touched how an axis's second command and the command settings are shown, so check that axes and buttons are unchanged.

- [ ] Open the joystick's inputs. An axis mapped to a static command (e.g. *Preset* or *Select camera*) still shows its second command row, the second row's direction is greyed out, and deflecting the axis each way runs the matching command once.
- [ ] Axes mapped to pan, tilt, zoom and focus still show their max-speed setting and drive the cameras; each camera stops on release.
- [ ] Buttons mapped to static and dynamic commands still show their settings and work as before. An input with no command still shows "Select a command".

### #14: Companion selects cameras by Companion number on every controller

The "Vmix Cam 1/2 Preview" virtual buttons are gone. `?SetCameraPreview=N` (the existing Companion buttons) and the new `?SelectCamera=N` select the camera whose **Companion #** (new field in each camera row) is N, on every active controller, with no mapping. Existing cameras get their list position as their number: the first camera in the list is 1, the second is 2.

- [ ] **Before upgrading**, with the old build still running: open the joystick's inputs and note which camera "Vmix Cam 1 Preview" and "Vmix Cam 2 Preview" are mapped to (*Select camera* → camera name). They must be the first and second camera in the camera list, in that order. Back up `%APPDATA%\PTZJoystickControl\Cameras.json` and `Gamepads.json`. If the mapping is different (swapped, or not *Select camera*), the existing Companion buttons would select a different camera after upgrading. In the new build, set each camera's **Companion #** to the number of the button that should select it (swap via a spare number, e.g. 1 → 9, 2 → 1, 9 → 2) before going on.
- [ ] Start the new build. Each camera row shows **Companion #**: the first camera 1 and the second 2 (unless you changed them in the previous step). The joystick's input list no longer shows "Vmix Cam N Preview".
- [ ] Press the operator's existing Companion camera buttons (`SetCameraPreview=1` and `=2`) in turn: the app's selection follows the camera noted in the first step, and the joystick drives it.
- [ ] In a browser on the vMix PC, open `http://localhost:12345/?SelectCamera=2`, then `?SelectCamera=1`: the selection follows each time.
- [ ] Open `http://localhost:12345/?SelectCamera=9`: the selection does not change, and the Log window shows a `WARN` "No camera has Companion number 9".
- [ ] In the camera row, type the other camera's number into **Companion #**: the field shows "Used by <other camera>" and the number is not taken. Type `0`: it is rejected too. Put the original number back.
- [ ] Pan camera 1 with the joystick and, while holding the stick, press Companion's button for camera 2: camera 1 stops, and the joystick now drives camera 2.
- [ ] Unplug and replug the joystick (no restart), then press Companion's camera buttons: the selection still follows.
- [ ] Quit the app, rename `Gamepads.json` to `Gamepads.json.bak`, start the app and activate the joystick (now with no mappings): Companion's camera buttons still change the selection. Quit, restore `Gamepads.json`, and start again: the joystick's usual mappings are back.
- [ ] Add a camera in the app: it gets Companion # 3. Remove it again. Quit and restart: both cameras keep their numbers.

### #34: App restarts after the previous instance was killed

- [ ] Start `Gui.exe`, then double-click `Gui.exe` again and leave its "PTZ Joystick Control is already running." window open.
- [ ] In Task Manager's **Processes** tab, expand each `Gui.exe` entry and **End task** on the one that does not list the "already running" window (the first instance). Leave the "already running" window open.
- [ ] With the "already running" window still open, start `Gui.exe` again: it starts normally (tray icon appears, no crash dialog), and the joystick drives both cameras.
- [ ] `PTZJoystickControl.log` (tray menu → **Show log**, or `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log`) has a `WARN` line "The previous PTZJoystickControl instance did not exit cleanly" right after the new `started` line.
- [ ] Click OK on the old "already running" window, then double-click `Gui.exe` once more: it only shows the "already running" window again, and Task Manager shows one running instance after you click OK.

### #29: Single-instance check uses a new mutex name

The mutex name changed so the macOS build starts. A build from before this fix and one from after it do not detect each other, so two copies could run on the same joystick and cameras.

- [ ] Before starting the new build, quit any older copy from the tray and check Task Manager shows no other `Gui.exe`.
- [ ] Start the new `Gui.exe`, then double-click `Gui.exe` again: the second launch only shows a window saying "PTZ Joystick Control is already running.", Task Manager shows one `Gui.exe` after you click OK, and the joystick still drives both cameras.
- [ ] `PTZJoystickControl.log` has exactly one `started` line for these two launches.

### #1: Companion unreachable no longer crashes the app

- [ ] Stop Companion. Select each camera from the joystick, then pan, tilt and zoom it and release: the app stays up, and each camera moves and stops.
- [ ] Start Companion again. Selecting a camera from Companion switches the app's camera again.

### #2: Malformed Companion requests no longer crash the app

- [ ] Send a Companion HTTP request with a misspelled parameter: Companion gets a 400 and the app stays up.
- [ ] Trigger a valid `SetCameraPreview` from Companion: the app's camera selection follows it.

### #27: Companion listener retries until it starts

- [ ] Quit the app. Hold port 12345 with another program (for example `python -m http.server 12345` on the vMix PC), then start the app: the red "Companion → app control is off, retrying automatically" notice appears and the joystick selects, moves and stops both cameras as normal.
- [ ] While the port is still held, keep using the joystick for a minute: no stutter or delay in camera moves.
- [ ] Release port 12345 (stop the other program) without restarting the app: within about 30 seconds the notice disappears, and selecting a camera from Companion switches the app's camera.
- [ ] The log (tray menu → **Show log**) shows one `WARN` "failed to start", a few `WARN` "still cannot start" lines (not one per second), and an `INFO` "listening on http://localhost:12345/".
- [ ] Quit from the tray menu while the port is still held: the app exits promptly.

### #23: Companion listener port in use no longer stops the app starting

- [ ] Quit the app. Hold port 12345 with another program (for example a second copy of an old build, or `python -m http.server 12345` on the vMix PC), then start the app: it starts, the main window shows a red "Companion → app control is off" notice naming the port, and the joystick still selects, moves and stops both cameras.
- [ ] Release port 12345 and restart the app: no notice is shown and selecting a camera from Companion works again (recovery without a restart is covered by #27).

### #4: Stop commands are repeated

- [ ] On each camera, pan, tilt, zoom and focus fully and release: the camera stops promptly every time, with no lurch or late stop.
- [ ] Release the stick and immediately push it the other way: the camera follows the new direction and does not stop.
- [ ] Pan and tilt together, release only pan: tilt keeps moving at the same speed until released, then stops.
- [ ] Optional, with Wireshark on the vMix PC filtered to the camera's VISCA port (normally `udp.port == 52381`): releasing an axis shows 3 identical stop packets roughly 50–65 ms apart.

### #13: Log file and Log window

- [ ] Start the installed (Release) build: `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` exists and its last lines include `INFO [Program] PTZJoystickControl <version> started`.
- [ ] Tray menu → **Show log**: the Log window opens and shows the same startup entries. Close it: the app keeps running and the joystick still drives the cameras. Open it again: it reappears.
- [ ] With the Log window open, open `http://localhost:12345/?SelectCamera=9` in a browser on the vMix PC: a `WARN [BitfocusCompanionService]` line with "No camera has Companion number 9" appears in the window and in the file, and the joystick still moves and stops the cameras.
- [ ] Quit the app from the tray: the file ends with `PTZJoystickControl stopped`.

### #26: Log file written on a background thread

- [ ] Start the installed (Release) build, then pan, tilt and zoom each camera from the joystick and release: control feels as responsive as before, and each camera moves and stops.
- [ ] With the Log window open, open `http://localhost:12345/?SelectCamera=9` in a browser on the vMix PC: the `WARN [BitfocusCompanionService]` line with "No camera has Companion number 9" appears in the window and, within a second, at the end of `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log`.
- [ ] Quit the app from the tray: the file ends with `PTZJoystickControl stopped`, and contains no `Dropped ... log entries` line.

### #3: Cameras stop when a controller disconnects

- [ ] Pan a camera with the joystick and, while still holding the stick, unplug the joystick's USB cable: the camera stops within a fraction of a second.
- [ ] Repeat while zooming, and while focusing manually: the camera stops.
- [ ] Select camera 1 and start it panning, select camera 2 and start it panning, then unplug while holding the stick: both cameras stop.
- [ ] Hold the stick deflected and deactivate the joystick in the app: the camera stops, and the app stays up.
- [ ] Replug and reactivate the joystick: it drives the cameras normally again.

### #5: Companion camera selection survives a USB replug

- [ ] Trigger `SetCameraPreview` for each camera from Companion: the app's camera selection follows.
- [ ] Unplug the joystick and plug it back in (no app restart). Trigger `SetCameraPreview` for each camera from Companion again: the selection still follows, and the joystick drives the newly selected camera.
- [ ] Deactivate the joystick in the app, activate it again, and repeat the Companion selection: it still follows.
- [ ] Rename a camera and add or remove a camera in the app after a replug: the joystick's camera-select options update and the app stays up.

### #24: log lines appear for camera connect/disconnect

- [ ] Start the installed (Release) build and open the Log window. Unplug the vMix PC's network cable (or disable its adapter), then pan a camera: an `ERROR` line `Camera <name>: send to <ip:port> failed, closing socket` appears in the window and in the log file, and the app stays up. Repeated `ERROR`/`INFO` lines while you keep panning are expected.
- [ ] Plug the network back in and pan again: an `INFO` line `Camera <name>: no socket, connecting to <ip:port>` appears, and the camera moves and stops.
- [ ] Unplug only one camera's network cable and pan it: note on #24 whether any `WARN`/`ERROR` line appears (over UDP there may be none). Plug it back in: it moves and stops again, and the other camera works throughout.

### #25: The previous camera stops when the selected camera changes

- [ ] Select camera 1 with the joystick and start it panning. While still holding the stick, select camera 2 with the joystick: camera 1 stops within a fraction of a second.
- [ ] Repeat while zooming, and while focusing manually: camera 1 stops.
- [ ] Pan camera 1 with the joystick and, while holding the stick, trigger `SetCameraPreview` for camera 2 from Companion: camera 1 stops, and the app's selection follows camera 2.
- [ ] Pan camera 2, then select camera 1 from the app's camera list while holding the stick: camera 2 stops.
- [ ] Recall a preset on camera 1 that needs a long move, then select camera 2 (from the joystick, and again via Companion `SetCameraPreview`) before camera 1 arrives: camera 1 completes the recall and ends at the preset position.
- [ ] Switch back and forth between the cameras several times without touching the stick: nothing moves, and the app stays responsive.

### #6: Companion input runs on the joystick input loop

- [ ] Alternate several times: select a camera from Companion, then pan and zoom it with the joystick and release. Each time the joystick drives the camera Companion selected, it stops on release, and the app stays up. (Don't switch while an axis is held: stop on camera switch is tracked separately, #25.)
- [ ] Press Companion's camera-select buttons rapidly in alternation: the app ends on the last one pressed, and the main window and tray icon show that camera.
- [ ] With the main window open, select a camera from Companion: the highlighted camera in the window updates without delay or error in the Log window.

### #11: .NET 10 and WiX 5 installer

The app now runs on .NET 10 (bundled in the MSI) and the MSI is built with WiX 5. The installer should behave as before: same install folder, shortcuts and settings, and it replaces the old version in place. The installed files change (the old `api-ms-win-*.dll`, `ucrtbase.dll` and `dbgshim.dll` are gone, a few new .NET 10 files are added). .NET 10 does not run on Windows 7 or 8.1.

Releases after #31 ship a zip instead of an MSI. When testing such a build, skip the first two boxes (the #31 entry covers installing and keeping the settings) and run the rest against the unzipped `Gui.exe`.

- [ ] Note the installed version in Settings → Apps, then run the new MSI over it without uninstalling first: it installs without errors, and Settings → Apps lists PTZ Joystick Control once, with the new version.
- [ ] The Start menu and desktop shortcuts start the app. `%APPDATA%\PTZJoystickControl\Cameras.json` and `Gamepads.json` are kept: both cameras and the joystick settings are as before the upgrade.
- [ ] The main window, tray icon and Log window draw normally (no blank or black window). `PTZJoystickControl.log` shows the new version started and no `ERROR` or `CRITICAL` lines, including from the startup update check.
- [ ] The joystick is detected, and pan, tilt, zoom, focus and presets drive both cameras; each camera stops on release.
- [ ] Companion selects cameras as before.
- [ ] Quit from the tray: the log ends with `PTZJoystickControl stopped`. Start it again: it starts normally.

### #32: Update check sees prerelease builds

Needs internet on the vMix PC. The check now considers every GitHub release, prereleases included (CI publishes only prereleases).

- [ ] Once a release newer than the first one containing this fix exists on the [releases page](https://github.com/JehutyPT/PTZJoystickControl/releases), start that first fixed build (older builds still have the bug and never show the dialog): the update dialog opens and names the newest release's version.
- [ ] Start the newest released build: no update dialog at startup, and tray → **Check for updates** shows "No Update Available".
- [ ] With the PC offline, start the app: it starts normally with no dialog, and `PTZJoystickControl.log` has a `WARN` line saying `Update check failed, reporting no update`, not an `ERROR` or `CRITICAL`.

### #31: Windows zip replaces the MSI

The Windows release is now a zip (`PTZJoystickControl_v<version>_win_x64.zip`, or `_win_x86.zip` for 32-bit Windows) holding a single-file `Gui.exe` with four native DLLs beside it (`av_libglesv2.dll` draws the window, `SDL2.dll` reads the joystick, `libSkiaSharp.dll` and `libHarfBuzzSharp.dll` render). There is no installer: no Apps entry, no Start menu or desktop shortcut. Settings and logs stay in `%APPDATA%\PTZJoystickControl`.

- [ ] Note the installed version in Settings → Apps, then uninstall PTZ Joystick Control there. `%APPDATA%\PTZJoystickControl\Cameras.json`, `Gamepads.json` and `PTZJoystickControl.log` are still there afterwards.
- [ ] Unzip the new zip into a folder you keep (e.g. `C:\PTZJoystickControl`, not Downloads). It contains `Gui.exe`, the four DLLs above, `SdlGamepads.dll.config` and a few `.pdb` files, and nothing else is needed.
- [ ] Double-click `Gui.exe`. If SmartScreen warns about an unrecognised app, choose **More info → Run anyway** (the MSI was unsigned too). The app starts, the tray icon appears, and both cameras and the joystick settings are as before.
- [ ] The main window, tray icon and Log window draw normally (no blank or black window). `PTZJoystickControl.log` shows the new version started and no `ERROR` or `CRITICAL` lines, including from the startup update check.
- [ ] The joystick is detected, and pan, tilt, zoom, focus and presets drive both cameras; each camera stops on release.
- [ ] Companion selects cameras as before.
- [ ] Quit from the tray: the log ends with `PTZJoystickControl stopped`. Start `Gui.exe` again: it starts normally.
- [ ] Uninstalling the MSI removed its Start menu and desktop shortcuts, and any taskbar pin or Startup-folder shortcut pointing at the old `Program Files` folder no longer works. Recreate the ones the owner uses, pointing at the new `Gui.exe` (right-click → Show more options → Send to → Desktop (create shortcut), or Pin to taskbar), and check each one starts the app.

### Companion listener stops quietly on quit

- [ ] Quit from the tray: the last lines of `PTZJoystickControl.log` have no `WARN [BitfocusCompanionService] Companion listener: failed to accept request` line.
- [ ] Start the app again: `?SelectCamera=N` from Companion still selects cameras.
