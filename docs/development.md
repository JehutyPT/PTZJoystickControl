# Development

## Prerequisites

- .NET SDK matching the projects' `TargetFramework` (currently `net6.0`; moving to the current .NET LTS is on the [roadmap](roadmap.md)).
- Windows for the MSI package (WiX); macOS for the DMG.
- SDL2 comes from the `ppy.SDL2-CS` NuGet package; Linux ARM needs a self-built `libSDL2`.

## Build and run

```
dotnet build PTZJoystickControl.sln
dotnet run --project Gui
```

On macOS/Linux the solution build fails in `Packaging/Windows` (WiX is Windows-only); use `dotnet build PTZJoystickControl.App.slnf`, which builds only the app projects. Making the full solution build cross-platform is part of the .NET LTS upgrade (see roadmap).

Only one instance can run at a time (named mutex in `Gui/Program.cs`).

## Logs

Everything the app writes through `System.Diagnostics.Trace` (and `Debug`, in Debug builds only), plus any unhandled exception, goes through one listener (`Application/Logging/AppLogListener.cs`) to a log file and to the in-app Log window (tray menu → **Show log**, which shows the last 1000 entries since startup or since **Clear Log**; clearing never touches the file).

The file sits next to the settings files (`Cameras.json`, `Gamepads.json`):

| OS | Log file |
|---|---|
| Windows | `%APPDATA%\PTZJoystickControl\PTZJoystickControl.log` |
| macOS | `~/.config/PTZJoystickControl/PTZJoystickControl.log` |
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

or `grep -hE ' (ERROR|CRITICAL) ' ~/.config/PTZJoystickControl/PTZJoystickControl*.log` on macOS.

Log with `Trace.TraceError` / `TraceWarning` / `TraceInformation` so the level is right. `Debug.WriteLine` is compiled out of Release builds and `Console.WriteLine` goes nowhere in the packaged app, so neither is used for anything that must reach the log; `Debug.WriteLine` is kept only for per-packet VISCA reply dumps, which would flood the log. Even with the background writer, don't log per tick or per packet: a flood fills the queue and drops entries.

## Tests

`Tests/` is an xUnit project (`PtzJoystickControl.Tests`) that references `Core` and `Application`. It is part of both `PTZJoystickControl.sln` and `PTZJoystickControl.App.slnf`. Run it with:

```
dotnet test PTZJoystickControl.App.slnf
```

Use the solution filter, not bare `dotnet test` or the full `.sln`, which pulls in the Windows-only WiX project.

The test project targets `net6.0` like the rest of the app but sets `RollForward` to `Major`, so its test host runs on a newer installed runtime when the .NET 6 runtime is absent. Any installed SDK (e.g. only .NET 10) can build and run the tests locally without installing .NET 6. CI still runs them under the pinned .NET 6 SDK.

CI runs the tests in a `test` job before anything else: `bump-version` depends on it, so a failing test stops the version bump, the tag, the packages and the GitHub Release. See [CI](#ci).

New behavior and fixes land test-first. Priority coverage, each added with its own fix:

- input shaping (deadzone, saturation, inversion) and stop-on-deadzone behavior;
- stop-on-disconnect and stop repetition;
- Companion request parsing (valid, unknown, malformed);
- Raw Panel and Satellite message parsing, once those exist.

## Verification before merging

| Check | Command |
|---|---|
| Build | `dotnet build PTZJoystickControl.sln` succeeds with no new warnings |
| Tests | `dotnet test PTZJoystickControl.App.slnf` passes |
| Hardware | Pre-show checklist in [live-safety.md](live-safety.md) on real cameras for any change to input, commands, networking or threading. When no cameras are available, merge and add the change's checks to [on-site-checklist.md](on-site-checklist.md) in the same merge |
| Secrets | No credentials or tokens committed |

## Branches

- `main` is the working line.
- New work branches off `main`: `fix/<issue#>-short-description` or `feature/<issue#>-short-description`, one roadmap item per branch where practical.

## Commits

- The repo owner is the sole author of every commit. Agents commit under the configured git user and add no co-author or AI attribution trailers.
- After `git am` or a cherry-pick of agent-produced patches, reset the author (`git rebase --reset-author` / `git commit --amend --reset-author`).

## CI

`.github/workflows/build-release.yml` runs the tests, then builds and packages releases (currently pinned to .NET 6). Packaging and releasing only start once the `test` job passes. The build jobs only upload their packages as workflow artifacts; on `main`, a single `release` job runs after all of them succeed and publishes the GitHub prerelease with every package at once. If any build fails, no release is created or updated.

A failed release run on `main` can be retried with "Re-run failed jobs" or "Re-run all jobs". The re-run reuses the bump commit and tag the first attempt pushed (same run number, same version), so it builds and releases that version again rather than bumping twice. If the tag already exists on a different commit, the run stops instead of overwriting another release.

`.github/workflows/issue-readiness-audit.yml` keeps the `ready-for-agent` label honest; see [agents/issue-tracker.md](agents/issue-tracker.md).
