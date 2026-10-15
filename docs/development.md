# Development

## Prerequisites

- .NET 10 SDK (the projects target `net10.0`, the current LTS).
- Any OS for the Windows zip (the cross-RID publish below works on macOS and Linux too); macOS for the DMG.
- The packaged app runs only where .NET 10 does: no Windows 7/8.1 or older macOS (see .NET 10's [supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)).
- SDL2 comes from the `ppy.SDL2-CS` NuGet package; Linux ARM needs a self-built `libSDL2`.

## Build and run

```
dotnet build PTZJoystickControl.sln
dotnet run --project Gui -- --settings-dir <new temporary folder>
```

Without `--settings-dir`, the app runs on the default settings folder, with the real cameras, controllers and SKAARHOJ panel settings (see [Another settings folder](#another-settings-folder)).

Avalonia 12 sends anonymous build-time telemetry unless `AVALONIA_TELEMETRY_OPTOUT=1` is set in the environment. CI sets it for every job; set it in your shell profile to opt out locally.

The solution builds on Windows, macOS and Linux.

Only one instance can run at a time (named mutex, `Application/SingleInstance.cs`); a second launch shows an "already running" window instead. If the previous instance died without releasing the mutex, the next launch takes it over, starts normally and logs a `WARN` that the previous instance did not exit cleanly. The name must not contain `/` (.NET on macOS and Linux throws) or `\`.

### Another settings folder

The app keeps its settings and log in the default folder for the OS (see [Logs](#logs)). `--settings-dir <folder>` runs it on another folder instead: every settings file, the log and its archives, and each save's `.tmp` file are then read from and written to that folder, and the app reads and writes nothing in the default folder. A relative folder is taken from the current directory; a missing one is created, with its parents.

```
dotnet run --project Gui -- --settings-dir <folder>
Gui.exe --settings-dir <folder>
```

A fresh folder starts with no cameras, controller mappings, SKAARHOJ panel or Companion settings, so it reaches no real camera or panel: a smoke test runs on a new temporary folder. Only one copy runs per user session, whatever its folder: a second copy, on any folder, shows "already running".

The switch is matched exactly, and its folder is the next argument. Single-dash arguments, such as `-m` (start minimized), are passed on as before. Startup fails when `--settings-dir` has no folder after it or is given more than once, when the folder cannot be created or written to (the app creates and deletes a uniquely named file in it to check), or when any other argument starts with `--`, `--settings-dir=<folder>` included, so a mistyped switch never starts the app on the default folder. Failing means one line on standard error and exit code 2, before the single-instance check: no window, tray icon or log file, and no settings read. The lines:

- `PTZJoystickControl: cannot start: --settings-dir needs a folder, as in --settings-dir /path/to/folder`
- `PTZJoystickControl: cannot start: --settings-dir is given more than once`
- `PTZJoystickControl: cannot start: unknown argument '<argument>'; to choose the settings folder, give it as the next argument: --settings-dir <folder>`
- `PTZJoystickControl: cannot start: settings folder '<full path>' (--settings-dir) cannot be used: <reason>`, the reason being the system's error message

A macOS or Linux terminal shows the line. `Gui.exe` has no console, so on Windows the line shows only when standard error is redirected, as with PowerShell's `Start-Process .\Gui.exe -ArgumentList ... -RedirectStandardError err.txt -Wait -PassThru` (the returned process's `ExitCode` is 2); otherwise the only signs are exit code 2 and no tray icon.

## Windows package

Windows releases are a zip per architecture (`PTZJoystickControl_v<version>_win_x86.zip` / `_win_x64.zip`), not an installer. Build one locally, on any OS, the way the CI Windows job does:

```
dotnet publish Gui/Gui.csproj -c PublishRelease -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`publish/` then holds `Gui.exe` (single file: the .NET runtime and every managed assembly), the native DLLs it loads (`av_libglesv2.dll`, `SDL2.dll`, `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`), the managed pdbs (line numbers in logged stack traces; `Gui.csproj` leaves out the native SkiaSharp/HarfBuzz pdbs, about 105 MB) and `SdlGamepads.dll.config`. CI zips that folder as is. The native DLLs stay beside the exe on purpose: `IncludeNativeLibrariesForSelfExtract` would unpack them to `%TEMP%` on first run, which can fail on the show PC (antivirus, temp clean-up), while beside the exe they load from the app folder as they did from the old MSI's install folder.

Installing is unzipping to a permanent folder and running `Gui.exe`; settings and logs live in `%APPDATA%\PTZJoystickControl` (unless [`--settings-dir`](#another-settings-folder) names another folder), so replacing the folder with a newer zip keeps them. There are no shortcuts, Apps entry or uninstaller. The zip and exe are unsigned (so was the old MSI), so Windows SmartScreen may ask on first run.

Use `-r win-x86` for the 32-bit zip.

## Logs

Everything the app writes through `System.Diagnostics.Trace` (and `Debug`, in Debug builds only), plus any unhandled exception, goes through one listener (`Application/Logging/AppLogListener.cs`) to a log file and to the in-app Log window (tray menu → **Show log**, which shows the last 1000 entries since startup or since **Clear Log**; clearing never touches the file).

The file sits next to the settings files (`Cameras.json`, `Gamepads.json`, `Companion.json`), in the default folder for the OS unless [`--settings-dir`](#another-settings-folder) names another:

| OS | Log file |
|---|---|
| Windows | `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` |
| macOS | `~/Library/Application Support/PTZJoystickControl/PTZJoystickControl.log` |
| Linux | `~/.PTZJoystickControl/PTZJoystickControl.log` |

Each start logs the folder in use at `INFO`: `PTZJoystickControl <version> started; settings folder <full path>; log file <path>`, with ` (from --settings-dir)` after the folder when the switch named it.

One entry per line, UTF-8:

```
2026-10-13T14:03:12.345+01:00 WARN [BitfocusCompanionService] Companion listener: rejected request '?SelectCamera=9': No camera has Companion number 9 (command 'SelectCamera')
```

- Local timestamp with milliseconds and UTC offset, then the level, then the source in brackets, then the message.
- Levels: `CRITICAL` (unhandled exception), `ERROR`, `WARN`, `INFO` (also plain `Trace.WriteLine`), `VERBOSE`.
- Source is the class that logged; if that cannot be found, the trace source or `Trace.WriteLine` category, else `App`. Unhandled exceptions use `AppDomain` (with `terminating: true/false`) and unobserved task exceptions `TaskScheduler`.
- Line breaks inside a message (e.g. stack traces) are written as the two characters `\n`, so an entry never spans lines.
- A background writer thread writes, flushes and rolls the file, normally within milliseconds of each entry, so logging never waits on the disk. Entries keep their order and the time they were logged. If the writer falls behind by 10,000 entries, new entries are dropped from the file (not from the Log window) and a `WARN [AppLogListener] Dropped N log entries ...` line marks the gap. On exit, and on an unhandled exception, the app waits up to 2 seconds for the queued entries to reach the file. The file can be read while the app runs.

Retention: when the file reaches 5 MB (checked on every entry and when the app starts) it is renamed to `PTZJoystickControl.1.log` (older ones shift to `.2` … `.4`, the oldest is deleted), so the logs never exceed about 25 MB. Newest entries are at the end of `PTZJoystickControl.log`.

Reading after a show, e.g. on Windows:

```
findstr /C:" ERROR " /C:" CRITICAL " "%APPDATA%\PTZJoystickControl\PTZJoystickControl*.log"
```

or `grep -hE ' (ERROR|CRITICAL) ' ~/Library/Application\ Support/PTZJoystickControl/PTZJoystickControl*.log` on macOS.

Log with `Trace.TraceError` / `TraceWarning` / `TraceInformation` so the level is right. `Debug.WriteLine` is compiled out of Release builds and `Console.WriteLine` goes nowhere in the packaged app, so neither is used for anything that must reach the log; `Debug.WriteLine` is kept only for per-packet VISCA reply dumps, which would flood the log. Even with the background writer, don't log per tick or per packet: a flood fills the queue and drops entries.

## Tests

`Tests/` is an xUnit project (`PtzJoystickControl.Tests`) that references `Core` and `Application`. It is part of `PTZJoystickControl.sln`. Run it with:

```
dotnet test PTZJoystickControl.sln
```

CI runs the tests in a `test` job before anything else: `bump-version` depends on it, so a failing test stops the version bump, the tag, the packages and the GitHub Release. See [CI](#ci).

New behavior and fixes land test-first. Priority coverage, each added with its own fix:

- input shaping (deadzone, saturation, inversion, expo) and stop-on-deadzone behavior;
- stop-on-disconnect and stop repetition;
- Companion request parsing (valid, unknown, malformed);
- Raw Panel message parsing.

## Verification before merging

| Check | Command |
|---|---|
| Build | `dotnet build PTZJoystickControl.sln` succeeds with no new warnings (known baseline: xUnit1031 in the log writer tests) |
| Tests | `dotnet test PTZJoystickControl.sln` passes |
| Hardware | Pre-show checklist in [live-safety.md](live-safety.md) on real cameras for any change to input, commands, networking or threading. When no cameras are available, merge and add the change's checks to [on-site-checklist.md](on-site-checklist.md) in the same merge |
| Secrets | No credentials or tokens committed |

## Branches

- `main` is the working line.
- New work branches off `main`: `fix/<issue#>-short-description` or `feature/<issue#>-short-description`, one roadmap item per branch where practical.

## Commits

- The repo owner is the sole author of every commit. Agents commit under the configured git user and add no co-author or AI attribution trailers.
- After `git am` or a cherry-pick of agent-produced patches, reset the author (`git rebase --reset-author` / `git commit --amend --reset-author`).

## CI

`.github/workflows/build-release.yml` runs the tests, then builds and packages releases. The test and package jobs install the latest .NET 10 SDK and pin the run to it with a generated `global.json` (the repo has none), so a newer SDK on the runner image is never used. Packaging and releasing only start once the `test` job passes. The Windows job (x86 and x64) publishes the single-file build and zips it (see [Windows package](#windows-package)); the macOS job builds the DMG. The build jobs only upload their packages as workflow artifacts; on `main`, a single `release` job runs after all of them succeed and publishes the GitHub prerelease with every package at once. If any build fails, no release is created or updated.

A failed release run on `main` can be retried with "Re-run failed jobs" or "Re-run all jobs". The re-run reuses the bump commit and tag the first attempt pushed (same run number, same version), so it builds and releases that version again rather than bumping twice. If the tag already exists on a different commit, the run stops instead of overwriting another release.

`.github/workflows/issue-readiness-audit.yml` keeps the `ready-for-agent` label honest; see [agents/issue-tracker.md](agents/issue-tracker.md).
