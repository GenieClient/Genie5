# Installation

Genie 5 is in **alpha**, but you no longer have to build it yourself — **pre-built downloads ship for Windows, macOS, and Linux** on the [Releases](https://github.com/GenieClient/Genie5/releases/latest) page. Grab the one for your platform, or [build from source](#build-from-source) if you're contributing. See [Releases & Changelog](Releases) for what's in the latest build.

> **Coming from Genie 4?** Install fresh first, then jump to [Importing from Genie 4](Importing-Genie4-Config) to bring your aliases, triggers, highlights, etc. across.

> ⚠️ **Signing status — Windows is signed; macOS & Linux aren't yet.** **Windows** release binaries are **EV code-signed** under **Shadow Realms LLC**, the organization that maintains and publishes Genie 5 — a GlobalSign Extended Validation certificate, signed through [SignPath.io](https://signpath.io/) with maintainer approval on every release. **macOS and Linux** builds are unsigned for now and trip a first-launch warning (Gatekeeper) — see [Platform first-launch notes](#platform-first-launch-notes). Windows builds published *before* EV signing went live are also unsigned, and because SmartScreen reputation accrues per file over time, freshly-signed builds may still show a reduced warning until downloads add up.

## Download a pre-built build (recommended)

From the [latest release](https://github.com/GenieClient/Genie5/releases/latest), pick the download for your platform:

### 🪟 Windows

| Download | When to pick it |
| --- | --- |
| **`Genie5-win-Setup.exe`** *(recommended)* | Normal install. Registers the app for **in-app auto-updates**, so future releases arrive via **Help → Check for Updates**. |
| `Genie5-win-Portable.zip` | No-install / portable. Extract anywhere and run `Genie5.exe` (point shortcuts at this one — the copy inside `current\` is replaced on every update). In-app auto-update works here too. |

### 🐧 Linux

| Download | When to pick it |
| --- | --- |
| **`Genie5.AppImage`** | A single-file executable that runs on Ubuntu / Fedora / Debian / Arch / etc. Mark it executable and run it (below). Subsequent releases can update in-app. |

```bash
chmod +x Genie5.AppImage
./Genie5.AppImage
```

If you hit a **"FUSE not installed"** error, install FUSE (Debian/Ubuntu: `sudo apt install libfuse2`; Fedora generally works out of the box). On very minimal distros you may also need `fontconfig` for correct text rendering. For a desktop-menu entry, see [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher).

### 🍎 macOS

Pick by your Mac's chip — **Apple Silicon** (M1/M2/M3 or newer) or **Intel** (pre-2020):

| Your Mac | Download | When to pick it |
| --- | --- | --- |
| Apple Silicon | **`Genie5-osx-Setup.pkg`** *(recommended)* | Standard `.pkg` installer. |
| Apple Silicon | `Genie5-osx-Portable.zip` | Drag the app into **Applications** yourself. |
| Intel | **`Genie5-osx-x64-Setup.pkg`** *(recommended)* | Standard `.pkg` installer (x86_64). |
| Intel | `Genie5-osx-x64-Portable.zip` | Drag-to-Applications portable bundle (x86_64). |

> **Not sure which Mac you have?**  → menu → **About This Mac**. "Apple M1/M2/M3…" = Apple Silicon; "Intel Core…" = Intel.

### What *not* to download

The other release assets — the `*.nupkg` packages, `RELEASES*`, and `releases.*.json` / `assets.*.json` files — are the **Velopack update-feed manifests** the in-app updater reads. You don't download those directly.

## Platform first-launch notes

Windows release binaries are now EV-signed by **Shadow Realms LLC** (see above); macOS and Linux builds aren't signed yet, so your OS may warn the first time you run one:

### macOS — Gatekeeper

An unsigned build trips Gatekeeper ("developer cannot be verified" or "damaged"). Two ways past it:

- **Right-click the app → Open → Open** (instead of double-clicking). macOS remembers the choice and stops asking.
- Or clear the download quarantine in Terminal (substitute the real path):
  ```bash
  xattr -d com.apple.quarantine /Applications/Genie5.app
  ```

### Windows — SmartScreen

Windows release binaries are EV-signed by **Shadow Realms LLC**, so recent builds show that publisher name rather than "unknown publisher." SmartScreen reputation still accrues per file, so a brand-new signed build may briefly show the blue "Windows protected your PC" panel — click **More info → Run anyway**, and it's remembered for that file. Older builds from before signing went live are unsigned and always show the panel.

### Linux

The AppImage just needs execute permission (`chmod +x`); see the [Linux download notes](#-linux) above for FUSE/fontconfig.

## First launch

On first run Genie 5 creates its per-user data folder (`~/Library/Application Support/Genie5` on macOS, `%APPDATA%\Genie5` on Windows, `~/.local/share/Genie5` on Linux) with `Config/`, `Scripts/`, `Maps/`, and `Logs/` subfolders. See [Application Folders](Application-Folders) for the full layout.

Then head to [Quick Start](Quick-Start) to connect and play.

## Staying up to date

If you installed via **`Genie5-win-Setup.exe`** (Windows) or the **`.pkg`** / **AppImage** (macOS / Linux), future releases arrive through the in-app updater — **Help → Check for Updates**, which shows a badge when something's available. Portable `.zip` builds don't auto-update; re-download to upgrade. Full details: [Keeping Up to Date](Updates).

## Build from source

For contributors, or to run the bleeding edge. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Git](https://git-scm.com/):

```bash
git clone https://github.com/GenieClient/Genie5.git
cd Genie5
dotnet run --project src/Genie.App
```

Avalonia is a tier-1 cross-platform UI toolkit, so that same command launches the GUI on Windows, macOS, and Linux. For a faster-starting build, compile Release first:

```bash
dotnet build -c Release
dotnet run -c Release --project src/Genie.App
```

Running from source via `dotnet run` also sidesteps the Gatekeeper/SmartScreen warnings entirely. To produce your own self-contained single-file executable for a platform:

```bash
# Windows x64
dotnet publish src/Genie.App -c Release -r win-x64   -o publish/win-x64
# macOS Apple Silicon
dotnet publish src/Genie.App -c Release -r osx-arm64 -o publish/osx-arm64
# macOS Intel
dotnet publish src/Genie.App -c Release -r osx-x64   -o publish/osx-x64
# Linux x64
dotnet publish src/Genie.App -c Release -r linux-x64 -o publish/linux-x64
```

See [docs/build-and-release.md](https://github.com/GenieClient/Genie5/blob/main/docs/build-and-release.md) for full publish/packaging detail, and [Building from Source](Building-from-Source) for the project layout and dev test harness.

## After installation

- [Quick Start](Quick-Start) — connect, play, save a profile, run a script.
- [Application Folders](Application-Folders) — where your data lives on disk.
- [Importing Genie 4 Config](Importing-Genie4-Config) — migrate from Genie 4.
- [Updating Maps and Scripts](Updating-Maps-and-Scripts) — get the latest community maps.
- [Releases & Changelog](Releases) — what shipped in each build.
