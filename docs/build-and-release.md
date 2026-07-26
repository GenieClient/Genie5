# Build & Release

Genie 5 builds for Windows, macOS, and Linux from one source tree on .NET 8. The published artifact is **self-contained** — the .NET runtime and native libraries are bundled, so end users don't install anything.

There are no platform build scripts checked in (no `build-mac.sh` / `build-win.sh`). Packaging is driven entirely by `dotnet publish` and the publish properties already set in [Genie.App.csproj](../src/Genie.App/Genie.App.csproj). This page documents the commands and what those properties do.

## Projects

| Project | Output | Notes |
| --- | --- | --- |
| [Genie.Core](../src/Genie.Core/Genie.Core.csproj) | library (`AssemblyName=Genie.Core`) | Pure engine. Marked `SelfContained` so the App can reference it from a self-contained publish (NETSDK1150). Builds as an exe only so the headless [TestHarness](../src/Genie.Core/TestHarness.cs) can run via `dotnet run`. |
| [Genie.App](../src/Genie.App/Genie.App.csproj) | `WinExe` (`AssemblyName=Genie`) | The Avalonia GUI. References Genie.Core. |

Solution file: [Genie.slnx](../Genie.slnx). Target framework: `net8.0`. UI stack: Avalonia 11.3.6 + Dock.Avalonia + ReactiveUI (with ReactiveUI.Fody).

## Local development

```bash
# from repo root
dotnet build -c Release
dotnet run --project src/Genie.App
```

A plain `dotnet build` produces an unbundled `bin/Debug/net8.0/Genie` you can run directly. The self-contained single-file artifact is only needed for distribution.

To run the headless engine harness (no UI):

```bash
dotnet run --project src/Genie.Core
```

## Publishing a distributable

The csproj defaults make `dotnet publish` emit a single self-contained executable with the runtime + native libs (SkiaSharp / HarfBuzz / Avalonia) folded in. Pick the runtime identifier (RID) for the target:

```bash
# Windows x64
dotnet publish src/Genie.App -c Release -r win-x64   -o publish/win-x64

# Windows arm64
dotnet publish src/Genie.App -c Release -r win-arm64 -o publish/win-arm64

# macOS Apple Silicon
dotnet publish src/Genie.App -c Release -r osx-arm64 -o publish/osx-arm64

# macOS Intel
dotnet publish src/Genie.App -c Release -r osx-x64   -o publish/osx-x64

# Linux x64
dotnet publish src/Genie.App -c Release -r linux-x64 -o publish/linux-x64
```

Each produces a single `Genie5` / `Genie5.exe` that a tester can copy and double-click — no .NET install, no loose DLLs.

### What the publish properties do

From [Genie.App.csproj](../src/Genie.App/Genie.App.csproj):

| Property | Effect |
| --- | --- |
| `PublishSingleFile=true` | Bundle everything into one executable. |
| `SelfContained=true` | Embed the .NET runtime so the target machine needs nothing pre-installed. |
| `IncludeNativeLibrariesForSelfExtract=true` | Fold the native shim libraries (Skia/HarfBuzz/Avalonia) into the single file too. |
| `EnableCompressionInSingleFile=true` | Compress the bundle to keep the download smaller. |
| `DebugType=embedded` | Keep PDB symbols inside the exe so field crash reports have readable stack traces. |

Because these live in the csproj, the `dotnet publish -r <rid>` command above is all that's needed — no extra `-p:` flags.

## Version stamping

Version metadata is set in [Genie.App.csproj](../src/Genie.App/Genie.App.csproj):

```xml
<Version>5.0.0-alpha.4</Version>
<AssemblyVersion>5.0.0.0</AssemblyVersion>
<FileVersion>5.0.0.0</FileVersion>
<InformationalVersion>5.0.0-alpha.4</InformationalVersion>
```

To stamp a different version at publish time, override on the CLI:

```bash
dotnet publish src/Genie.App -c Release -r win-x64 \
  -p:Version=5.0.0-alpha.2 -p:FileVersion=5.0.0.2 -o publish/win-x64
```

Keep `AssemblyVersion` pinned (e.g. `5.0.0.0`) across point releases so the friendly/display version can move without breaking strong-name binding for any future plugin reference. The friendly version (`Version` / `InformationalVersion`) is what the About box and window title surface; `FileVersion` shows in the Windows file-properties dialog.

## Platform packaging notes

The raw publish output is runnable as-is. To make it feel native you'll want to wrap it — these steps are **not** scripted in-repo yet:

### macOS — `.app` bundle and Gatekeeper

A bare `osx-*` publish is a Unix executable, not an app bundle. To ship a `.app`:

1. Lay out `Genie5.app/Contents/MacOS/Genie` (the publish output), `Contents/Resources/` (an `.icns`), and a generated `Contents/Info.plist`.
2. `xattr -cr Genie5.app` to strip quarantine attributes.
3. `codesign --force --deep --sign - Genie5.app` for ad-hoc signing — without it, Apple Silicon kills the unsigned binary as "damaged."

Ad-hoc signing is not notarisation. Users still need the right-click → **Open** dance on first launch (documented in the [Installation](../wiki/Installation.md) wiki page). Real Gatekeeper-clean distribution requires an Apple Developer ID certificate plus `xcrun notarytool` + `stapler`.

### Windows — SmartScreen

Release `Genie5.exe` binaries are EV code-signed (GlobalSign certificate issued to Shadow Realms LLC, the project's support partner) via the tag-triggered `release.yml` workflow: the sign job uploads the unsigned binary to SignPath.io's REST API and the maintainer approves each signing request before the HSM-held key signs it. A **locally built** exe is unsigned and shows the SmartScreen "Windows protected your PC" prompt (**More info → Run anyway**) — expected for dev builds. The Velopack `Setup.exe` installer does not yet go through signing; reworking the pipeline so installers inherit the signature is a planned follow-up. (An MSI installer via [WiX](https://wixtoolset.org/) remains a possible later addition if a richer installer is wanted.)

### Linux

`linux-x64` publish runs directly. No packaging (AppImage/.deb/Flatpak) is set up yet.

## CI

No CI pipeline is checked in. A reasonable starter: a GitHub Actions matrix on `windows-latest` / `macos-latest` / `ubuntu-latest` running `dotnet build -c Release`, then `dotnet publish` per RID, uploading the artifacts and cutting a release on tag push.

## Code references

- **[Genie.App.csproj](../src/Genie.App/Genie.App.csproj)** — assembly name (`Genie`), framework, publish + version properties, package refs.
- **[Genie.Core.csproj](../src/Genie.Core/Genie.Core.csproj)** — engine library, `SelfContained`, embedded `ZoneConnections.baseline.xml` resource.
- **[Genie.slnx](../Genie.slnx)** — solution layout.
