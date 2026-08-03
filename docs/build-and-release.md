# Build & Release

Genie 5 builds for Windows, macOS, and Linux from one source tree on .NET 8. The published artifact is **self-contained** — the .NET runtime and native libraries are bundled, so end users don't install anything.

There are no platform build scripts checked in (no `build-mac.sh` / `build-win.sh`). Local builds are driven by `dotnet publish` and the publish properties already set in [Genie.App.csproj](../src/Genie.App/Genie.App.csproj); **shipping releases are fully scripted in CI** (see [CI & releases](#ci--releases) below). This page documents the local commands, what the properties do, and how the release pipeline packages each platform.

## Projects

| Project | Output | Notes |
| --- | --- | --- |
| [Genie.Core](../src/Genie.Core/Genie.Core.csproj) | library (`AssemblyName=Genie.Core`) | Pure engine. Marked `SelfContained` so the App can reference it from a self-contained publish (NETSDK1150). Builds as an exe only so the headless [TestHarness](../src/Genie.Core/TestHarness.cs) can run via `dotnet run`. |
| [Genie.App](../src/Genie.App/Genie.App.csproj) | `WinExe` (`AssemblyName=Genie5`) | The Avalonia GUI. References Genie.Core. |
| [Genie.Plugins.Abstractions](../src/Genie.Plugins.Abstractions/Genie.Plugins.Abstractions.csproj) | library | The public plugin contract (`IGeniePlugin` / `IPluginHost`) that plugin authors reference. |

Solution file: [Genie.slnx](../Genie.slnx). Target framework: `net8.0`. UI stack: Avalonia + Dock.Avalonia + AvaloniaEdit + ReactiveUI (with ReactiveUI.Fody) — see the csproj for the pinned versions.

## Local development

```bash
# from repo root
dotnet build -c Release
dotnet run --project src/Genie.App
```

A plain `dotnet build` produces an unbundled `bin/Debug/net8.0/Genie5` you can run directly. The self-contained single-file artifact is only needed for distribution. Run the test suite with `dotnet test tests/Genie.Core.Tests`.

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
<Version>5.0.0-beta.4</Version>        <!-- current tier; bump per release -->
<AssemblyVersion>5.0.0.0</AssemblyVersion>
<FileVersion>5.0.0.0</FileVersion>
<InformationalVersion>5.0.0-beta.4</InformationalVersion>
```

To stamp a different version at publish time, override on the CLI:

```bash
dotnet publish src/Genie.App -c Release -r win-x64 \
  -p:Version=<version> -p:FileVersion=<file-version> -o publish/win-x64
```

Keep `AssemblyVersion` pinned (e.g. `5.0.0.0`) across point releases so the friendly/display version can move without breaking strong-name binding for any future plugin reference. The friendly version (`Version` / `InformationalVersion`) is what the About box and window title surface; `FileVersion` shows in the Windows file-properties dialog.

## Platform packaging notes

The raw publish output is runnable as-is. **Shipping packages are built by the tag-triggered `release.yml` workflow** — Velopack (`vpk pack`) produces the Windows `Setup.exe` + Portable zip, the macOS `.app`/`.pkg` plus a drag-install `.dmg` (two-step `hdiutil` UDRO → UDZO), and the Linux AppImage, and attaches everything to the GitHub Release with numbered filenames (`01-Windows-…`, `02-macOS-Apple-Silicon-…`, `03-macOS-Intel-…`, `04-Linux-…`). The notes below are for understanding the output and for local experiments — they are **not** the shipping path.

### macOS — `.app` bundle and Gatekeeper

CI builds the `.app`/`.pkg`/`.dmg` via `vpk pack --bundleId com.genieclient.genie5`. For a hand-rolled local bundle:

1. Lay out `Genie5.app/Contents/MacOS/Genie5` (the publish output), `Contents/Resources/` (an `.icns`), and a generated `Contents/Info.plist`.
2. `xattr -cr Genie5.app` to strip quarantine attributes.
3. `codesign --force --deep --sign - Genie5.app` for ad-hoc signing — without it, Apple Silicon kills the unsigned binary as "damaged."

Ad-hoc signing is not notarisation. Users still need the right-click → **Open** dance on first launch (documented in the [Installation](../wiki/Installation.md) wiki page). Real Gatekeeper-clean distribution requires an Apple Developer ID certificate plus `xcrun notarytool` + `stapler`.

### Windows — SmartScreen

Windows releases are EV code-signed (GlobalSign certificate issued to Shadow Realms LLC, the project's support partner) via the tag-triggered `release.yml` workflow in two passes: the sign job submits the published `Genie5.exe` to SignPath.io's REST API, the Windows Velopack job then packages that **signed** exe (so `Setup.exe`, the Portable zip, and the updater nupkg/delta payloads all carry it), and a second pass signs the `Setup.exe` installer itself. The maintainer approves each of the two signing requests per release. Residual: Velopack's generated launcher/`Update.exe` stubs remain unsigned (they'd need per-file signing hooks during `vpk pack`). A **locally built** exe is unsigned and shows the SmartScreen "Windows protected your PC" prompt (**More info → Run anyway**) — expected for dev builds. (An MSI installer via [WiX](https://wixtoolset.org/) remains a possible later addition if a richer installer is wanted.)

### Linux

`linux-x64` publish runs directly. The release workflow packages it as `04-Linux-Genie5.AppImage`; `.deb` / Flatpak are not set up.

## CI & releases

Two workflows are checked in under [.github/workflows](../.github/workflows):

- **`build.yml`** — continuous build with an event-tiered OS matrix: PRs build on Linux only; pushes to `main` add Windows; version tags add macOS. A publish-smoke job verifies the self-contained single-file output on pushes.
- **`release.yml`** — the tag-triggered release pipeline: extracts the tag's section from `RELEASE_NOTES.md` (and **fails if the `# Genie 5 — <tag>` heading is missing**), publishes win-x64, signs `Genie5.exe` via SignPath (maintainer email approval), Velopack-packages all four targets, signs `Setup.exe` (second approval), and attaches the numbered artifacts plus updater feeds to the GitHub Release.

## Code references

- **[Genie.App.csproj](../src/Genie.App/Genie.App.csproj)** — assembly name (`Genie5`), framework, publish + version properties, package refs.
- **[Genie.Core.csproj](../src/Genie.Core/Genie.Core.csproj)** — engine library, `SelfContained`, embedded `ZoneConnections.baseline.xml` resource.
- **[Genie.slnx](../Genie.slnx)** — solution layout.
