# .NET 10 upgrade

Genie 5 is moving from **.NET 8** to **.NET 10** during the beta line. This
document is the what, the why, and what it means for users and contributors.
The retarget landed via community PR #234 and ships in a beta release well
before November 2026.

## Why

.NET releases ship every November and are either LTS (three years of support)
or STS (two years). The support math makes this upgrade a due date, not a
preference:

| Runtime | Type | Support ends |
|---|---|---|
| .NET 8 (current) | LTS | **November 10, 2026** |
| .NET 9 | STS | November 10, 2026 — same day |
| **.NET 10 (target)** | **LTS** | **November 14, 2028** |
| .NET 11 | STS | ~November 2028 |

- **Genie 5 ships self-contained** — every release bundles its own copy of the
  .NET runtime, so users never install .NET themselves. That convenience cuts
  both ways: after November 10, 2026, a Genie 5 built on .NET 8 would be
  bundling a runtime that no longer receives security patches. For a client
  that handles account credentials, that is not acceptable.
- **.NET 10 is the only sensible landing spot.** .NET 9 dies the same day as
  .NET 8. .NET 11 is a short-term release. .NET 10 is the current LTS, has been
  in production since November 2025 with monthly patches since, and carries the
  project through November 2028. The next decision point after this is
  .NET 12 (LTS, late 2027).
- **Free wins.** Two runtime generations of JIT, GC, and base-library
  performance improvements, picked up by every platform we ship (Windows,
  macOS, Linux) with no code changes.

## What changes for users

Nothing you have to do. Updates install exactly as before — the new runtime
arrives inside the update like any other release. Startup and general
responsiveness may improve slightly; nothing about settings, scripts, maps,
profiles, plugins, or layouts changes.

## What changes for contributors

- Building the repo now requires the **.NET 10 SDK** (`10.0.x`). Check with
  `dotnet --list-sdks`; install from
  [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
- CI (build and release workflows) runs on `10.0.x`.
- Plugin authors: the plugin contract (`Genie.Plugins.Abstractions`) now
  targets `net10.0`. Plugins compiled against the net8 contract continue to
  load — .NET assemblies are forward-compatible onto a newer host runtime —
  but new plugin builds should target `net10.0` to match.

## Scope

Deliberately minimal — a retarget, not a rewrite:

- All four projects: `net8.0` → `net10.0`.
- `Microsoft.Extensions.Logging*` packages: 8.0.0 → 10.0.10.
- Avalonia patch bump within the same major line: 11.3.11 → 11.3.18
  (DataGrid stays at 11.3.13, the last patch published for its line).
- CI workflows: `dotnet-version: 8.0.x` → `10.0.x`.
- Full test suite green on .NET 10 before merge, plus a canary release through
  the real update channel before it rides a normal beta.

## Explicitly out of scope: Avalonia 12

Avalonia 12 (April 2026) targets .NET 10 and brings real wins — native Linux
screen-reader support, Wayland groundwork, rendering performance — but it is a
**separate, larger migration**: it renames the ReactiveUI integration package
and pulls a major ReactiveUI version bump with it. Coupling that to the runtime
retarget would turn a low-risk change into a risky one. This upgrade keeps the
UI stack on the still-maintained Avalonia 11.3 line; the Avalonia 12 move gets
its own roadmap entry and its own timeline.
