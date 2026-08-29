# Installation

Genie 5 is in **beta**, but you no longer have to build it yourself — **pre-built downloads ship for Windows, macOS, and Linux** on the [Releases](https://github.com/GenieClient/Genie5/releases/latest) page. Grab the one for your platform, or [build from source](#build-from-source) if you're contributing. See [Releases & Changelog](Releases) for what's in the latest build.

> **Coming from Genie 4?** Install fresh first, then jump to [Importing from Genie 4](Importing-Genie4-Config) to bring your aliases, triggers, highlights, etc. across.

> ⚠️ **Signing status — Windows is signed; macOS & Linux aren't yet.** **Windows** release binaries are **EV code-signed** under **Shadow Realms LLC**, the project's support partner — a GlobalSign Extended Validation certificate, signed through [SignPath.io](https://signpath.io/) with maintainer approval on every release. **macOS and Linux** builds are unsigned for now and trip a first-launch warning (Gatekeeper) — see [Platform first-launch notes](#platform-first-launch-notes). Windows builds published *before* EV signing went live are also unsigned, and because SmartScreen reputation accrues per file over time, freshly-signed builds may still show a reduced warning until downloads add up.

## Download a pre-built build (recommended)

From the [latest release](https://github.com/GenieClient/Genie5/releases/latest), pick the download for your platform:

### 🪟 Windows

| Download | When to pick it |
| --- | --- |
| **`01-Windows-Genie5-Setup.exe`** *(recommended)* | Normal install. Registers the app for **in-app auto-updates**, so future releases arrive via **Help → Check for Updates**. |
| `01-Windows-Genie5-Portable.zip` | No-install / portable. Extract anywhere and run `Genie5.exe` (point shortcuts at this one — the copy inside `current\` is replaced on every update). In-app auto-update works here too. |

### 🐧 Linux

| Download | When to pick it |
| --- | --- |
| **`04-Linux-Genie5.AppImage`** | A single-file executable that runs on Ubuntu / Fedora / Debian / Arch / etc. Mark it executable and run it (below). Subsequent releases can update in-app. |

```bash
chmod +x 04-Linux-Genie5.AppImage
./04-Linux-Genie5.AppImage
```

If you hit a **"FUSE not installed"** error, install FUSE — Debian/Ubuntu: `sudo apt install libfuse2` (on Ubuntu 24.04 and newer the package is `libfuse2t64`); Fedora generally works out of the box. For a desktop-menu entry, see [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher).

**Minimal installs** (server spins, containers, WSL, netinstalls) may also be missing the desktop libraries every GUI app needs. If the AppImage exits with a `libICE.so.6` / `XOpenDisplay` error or text renders oddly, install the X11 client libs and fontconfig — Debian/Ubuntu:

```bash
sudo apt install libx11-6 libice6 libsm6 libxext6 libxrandr2 libxcursor1 libxi6 libxrender1 libfontconfig1
```

Standard desktop distros (Ubuntu Desktop, Fedora Workstation, Mint, …) already have all of these.

Releases **after v5.0.0-beta.7** bundle their own ICU, so no globalization packages are needed. On beta.7 and earlier, a minimal distro may also report *"Couldn't find a valid ICU package"* — fix with `sudo apt install libicu74` (or your release's `libicu` package), or just update to the latest release.

### 🍎 macOS

Pick by your Mac's chip — **Apple Silicon** (M1/M2/M3 or newer) or **Intel** (pre-2020):

| Your Mac | Download | When to pick it |
| --- | --- | --- |
| Apple Silicon | **`02-macOS-Apple-Silicon-Genie5-Setup.pkg`** *(recommended)* | Standard `.pkg` installer. |
| Apple Silicon | `02-macOS-Apple-Silicon-Genie5.dmg` | Disk image — open it and drag the app into **Applications**. |
| Apple Silicon | `02-macOS-Apple-Silicon-Genie5-Portable.zip` | Drag the app into **Applications** yourself. |
| Intel | **`03-macOS-Intel-Genie5-Setup.pkg`** *(recommended)* | Standard `.pkg` installer (x86_64). |
| Intel | `03-macOS-Intel-Genie5.dmg` | Disk image — open it and drag the app into **Applications** (x86_64). |
| Intel | `03-macOS-Intel-Genie5-Portable.zip` | Drag-to-Applications portable bundle (x86_64). |

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

The AppImage just needs execute permission (`chmod +x`); see the [Linux download notes](#-linux) above for FUSE and the minimal-install library notes.

## First launch

On first launch Genie 5 asks where to keep your data: **Portable** (next to the app) or your **user folder** (`%APPDATA%\Genie5` on Windows, `~/Library/Application Support/Genie5` on macOS, `~/.local/share/Genie5` on Linux). See [Application Folders](Application-Folders) for details.

Then head to [Quick Start](Quick-Start) to connect and play.

## Staying up to date

Every official download — **Setup.exe** and the **Portable `.zip`** alike on Windows, the **`.pkg`** on macOS, the **AppImage** on Linux — is updater-aware: future releases arrive through the in-app updater via **Help → Check for Updates**, which shows a badge when something's available. Full details: [Keeping Up to Date](Updates).

## Build from source

For contributors, or to run the bleeding edge. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [Git](https://git-scm.com/):

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
