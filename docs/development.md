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

There is no test project yet, and `dotnet test` is commented out in CI. Adding `Tests/` (xUnit) is on the roadmap. Priority coverage:

- input shaping (deadzone, saturation, inversion) and stop-on-deadzone behavior;
- stop-on-disconnect and stop repetition;
- Companion request parsing (valid, unknown, malformed);
- Raw Panel and Satellite message parsing, once those exist.

## Verification before merging

| Check | Command |
|---|---|
| Build | `dotnet build PTZJoystickControl.sln` succeeds with no new warnings |
| Tests | `dotnet test` passes |
| Hardware | Pre-show checklist in [live-safety.md](live-safety.md) on real cameras for any change to input, commands, or networking |
| Secrets | No credentials or tokens committed |

## Branches

- `main` is the working line.
- New work branches off `main`: `fix/<issue#>-short-description` or `feature/<issue#>-short-description`, one roadmap item per branch where practical.

## CI

`.github/workflows/build-release.yml` builds and packages releases (currently pinned to .NET 6). `.github/workflows/issue-readiness-audit.yml` keeps the `ready-for-agent` label honest; see [agents/issue-tracker.md](agents/issue-tracker.md).
