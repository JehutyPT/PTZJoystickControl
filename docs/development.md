# Development

## Prerequisites

- .NET 10 SDK (the projects target `net10.0`, the current LTS).
- Any OS for the Windows zip (the cross-RID publish below works on macOS and Linux too); macOS for the DMG.
- The packaged app runs only where .NET 10 does: no Windows 7/8.1 or older macOS (see .NET 10's [supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)).
- SDL2 comes from the `ppy.SDL2-CS` NuGet package; Linux ARM needs a self-built `libSDL2`.

## Build and run

```
dotnet build PTZJoystickControl.sln
dotnet run --project Gui
```

The solution builds on Windows, macOS and Linux.

Only one instance can run at a time (named mutex in `Gui/Program.cs`).

## Windows package

Windows releases are a zip per architecture (`PTZJoystickControl_v<version>_win_x86.zip` / `_win_x64.zip`), not an installer. Build one locally, on any OS, the way the CI Windows job does:

```
dotnet publish Gui/Gui.csproj -c PublishRelease -r win7-x64 --self-contained -p:PublishSingleFile=true -o publish
```

`publish/` then holds `Gui.exe` (single file: the .NET runtime and every managed assembly), the native DLLs it loads (`av_libglesv2.dll`, `SDL2.dll`, `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`), the pdbs (line numbers in logged stack traces) and `SdlGamepads.dll.config`. CI zips that folder as is. The native DLLs stay beside the exe on purpose: `IncludeNativeLibrariesForSelfExtract` would unpack them to `%TEMP%` on first run, which can fail on the show PC (antivirus, temp clean-up), while beside the exe they load from the app folder as they did from the old MSI's install folder.

Installing is unzipping to a permanent folder and running `Gui.exe`; settings and logs live in `%APPDATA%\PTZJoystickControl`, so replacing the folder with a newer zip keeps them. There are no shortcuts, Apps entry or uninstaller. The zip and exe are unsigned (so was the old MSI), so Windows SmartScreen may ask on first run.

`Directory.Build.props` turns on the legacy RID graph (`UseRidGraph`) so the Windows package can still be published for `win7-x64`/`win7-x86`: Avalonia 0.10 ships its Windows renderer (`av_libglesv2.dll`) only for those RIDs. Remove it when Avalonia is upgraded.

## Logs

Everything the app writes through `System.Diagnostics.Trace` (and `Debug`, in Debug builds only), plus any unhandled exception, goes through one listener (`Application/Logging/AppLogListener.cs`) to a log file and to the in-app Log window (tray menu → **Show log**, which shows the last 1000 entries since startup or since **Clear Log**; clearing never touches the file).

The file sits next to the settings files (`Cameras.json`, `Gamepads.json`):

| OS | Log file |
|---|---|
| Windows | `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` |
| macOS | `~/Library/Application Support/PTZJoystickControl/PTZJoystickControl.log` |
| Linux | `~/.PTZJoystickControl/PTZJoystickControl.log` |

One entry per line, UTF-8:

```
2026-10-13T14:03:12.345+01:00 WARN [BitfocusCompanionClient] Companion request http://localhost:8000/press/bank/1/2 failed: ...
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

- input shaping (deadzone, saturation, inversion) and stop-on-deadzone behavior;
- stop-on-disconnect and stop repetition;
- Companion request parsing (valid, unknown, malformed);
- Raw Panel and Satellite message parsing, once those exist.

## Verification before merging

| Check | Command |
|---|---|
| Build | `dotnet build PTZJoystickControl.sln` succeeds with no new warnings (known baseline: NU1903/NU1904 for SkiaSharp, Tmds.DBus and System.Drawing.Common, which Avalonia 0.10 pins; xUnit1031 in the log writer tests) |
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
