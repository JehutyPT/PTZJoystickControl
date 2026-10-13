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

## Tests

`Tests/` is an xUnit project (`PtzJoystickControl.Tests`) that references `Core` and `Application`. It is part of both `PTZJoystickControl.sln` and `PTZJoystickControl.App.slnf`. Run it with:

```
dotnet test PTZJoystickControl.App.slnf
```

Use the solution filter, not bare `dotnet test` or the full `.sln`, which pulls in the Windows-only WiX project.

The test project targets `net6.0` like the rest of the app but sets `RollForward` to `Major`, so its test host runs on a newer installed runtime when the .NET 6 runtime is absent. Any installed SDK (e.g. only .NET 10) can build and run the tests locally without installing .NET 6. CI still runs them under the pinned .NET 6 SDK.

CI runs the tests in a `test` job before anything else: `bump-version` depends on it, so a failing test stops the version bump, the tag, the packages and the GitHub Releases. See [CI](#ci).

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

`.github/workflows/build-release.yml` runs the tests, then builds and packages releases (currently pinned to .NET 6). Packaging and releasing only start once the `test` job passes.

A failed release run on `main` can be retried with "Re-run failed jobs" or "Re-run all jobs". The re-run reuses the bump commit and tag the first attempt pushed (same run number, same version), so it builds and releases that version again rather than bumping twice. If the tag already exists on a different commit, the run stops instead of overwriting another release.

`.github/workflows/issue-readiness-audit.yml` keeps the `ready-for-agent` label honest; see [agents/issue-tracker.md](agents/issue-tracker.md).
