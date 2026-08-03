# Keeping Up to Date

Genie 5 has a built-in updater so you can keep four things current without hunting for downloads: the **app itself**, your **zone maps**, your **plugins**, and your **scripts**. It's an *in-process* update system — distinct from Genie 4's separate `Lamp.exe` updater.

## The Updates dialog

Open it from the **Help** menu (a badge appears there when something is available). It has four tabs:

| Tab | Updates | Source |
| --- | --- | --- |
| **Core** | The Genie 5 application. | GitHub Releases. |
| **Maps** | Community zone maps. | The community Maps repository. |
| **Plugins** | Installed plugins. | Each plugin's configured release feed. |
| **Scripts** | `.cmd` / `.js` scripts from repositories you subscribe to. | GitHub script repositories. |

The updater uses your system's default network settings, so OS-level proxy configuration is honored automatically.

![The Updates dialog's Core tab offering a newer version, with Update and Restart enabled](images/updates-dialog-core.png)

## Core (the app)

Application updates are delivered via [Velopack](https://velopack.io/): the updater fetches the new build from GitHub Releases and applies it in place, so the next launch is the new version. In-app Core updates work on all three platforms — Windows, macOS (Apple Silicon + Intel), and Linux (AppImage).

Auto-update requires that you installed via an updater-aware package:

| Platform | Auto-updates if installed via | Update by hand if you used |
| --- | --- | --- |
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS | `02-macOS-Apple-Silicon-Genie5-Setup.pkg` / `03-macOS-Intel-Genie5-Setup.pkg` (or the matching `.dmg` drag-install) | the `*-Portable.zip` bundles |
| Linux | `04-Linux-Genie5.AppImage` | — |

The plain **Portable `.zip`** builds don't register for updates — re-download from [Releases](https://github.com/GenieClient/Genie5/releases/latest) to upgrade those. Once you're on an updater-aware install, new releases arrive as small **delta** downloads (only the bytes that changed). See [Installation](Installation) for which download to pick.

> **Signing:** **Windows** release binaries are EV code-signed (since v5.0.0-alpha.10) under **Shadow Realms LLC**, the project's support partner — see the [README's code-signing section](https://github.com/GenieClient/Genie5/blob/main/README.md#code-signing-policy). **macOS and Linux** builds are unsigned for now, so those platforms show a first-launch warning ([details](Installation#platform-first-launch-notes)).

> **Release channel (while we're in beta):** the Core updater has a **stable** / **beta** channel selector in the Updates dialog. Every current build ships as a GitHub **pre-release**, so during the alpha/beta period Genie defaults to the **beta** channel — that's what lets the in-app updater see new beta builds. If you ever switch to **stable** you'll see "up to date" until the first non-prerelease (5.0.0) ships. Leave it on **beta** to ride the test releases.

## Maps

![The Maps, Plugins, and Scripts tabs — subscription rows for the official maps repo, an installed plugin, and a community script repository](images/updates-dialog-tabs.png)

The Maps tab (equivalently **File → Update Maps from Official Repo…**) pulls the latest zone XML from the community repository and **merges** it with your local data — upstream layout fixes come down while your stamped room ids survive. This has its own page: [Updating Maps & Scripts](Updating-Maps-and-Scripts).

## Plugins

The Plugins tab checks each installed plugin against its configured release feed and offers updates. See [Plugins](Plugins) for installing and managing them.

## Scripts

The Scripts tab lets you subscribe to GitHub script repositories and pull new and changed `.cmd` / `.js` files into your Scripts folder — like a `git pull`, subfolders included. Files that exist only locally are never touched, so your own scripts are safe. The community [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) repository ships as a ready-to-enable row; add more repositories (or a fork) as rows of your own. See [Updating Maps & Scripts](Updating-Maps-and-Scripts) for details.

## Update Settings — what runs by itself

**Help → Update Settings…** controls the silent check that runs at startup: choose which kinds it covers (**Core / Maps / Plugins / Scripts**) and, per kind, whether Genie may install what it finds by itself or just tell you.

- Auto-applied **client** updates install when you **close** Genie — never a mid-session restart.
- A quiet notice above the status bar reports **"Updates available"** / **"Auto-updated"**; click it to open the Updates dialog.

## Under the hood

The updater is built around small, swappable pieces: a file-list source and a release source (with implementations for GitHub Contents and GitHub Releases), plus per-domain updaters for the app, maps, plugins, and scripts. That platform-neutral design is what lets the same dialog drive four very different update flows, and it's what let the macOS and Linux app-update channels slot in without reworking the UI.

## What it never does

- **No mid-session restarts, no touching your game.** Updates never interrupt a running session: client updates that you've allowed to auto-apply install as Genie **closes**, and updates never reconnect or resume play for you (auto-reconnect is a separate, attendance-gated connection feature — see [Connecting](Connecting)). This keeps Genie on the right side of [policy](Policy-Compliance) and avoids surprises mid-session.

## Related

- [Installation](Installation) — first install and platform notes.
- [Releases & Changelog](Releases) — what shipped in each build.
- [Updating Maps & Scripts](Updating-Maps-and-Scripts) — the Maps flow in detail.
- [Plugins](Plugins) — managing plugins.
