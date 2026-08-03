# Troubleshooting & FAQ

Common problems and quick fixes. If none of these help, drop into [Discord](https://discord.gg/MtmzE2w) or [file an issue](https://github.com/GenieClient/Genie5/issues) — a recording (below) makes bug reports far easier to act on.

## Installing & launching

**Which download do I need?**
Pre-built builds ship for all three platforms — see the [Installation](Installation#download-a-pre-built-build-recommended) tables. Short version: Windows → `01-Windows-Genie5-Setup.exe`; Linux → `04-Linux-Genie5.AppImage`; macOS → `02-macOS-Apple-Silicon-Genie5-Setup.pkg` (Apple Silicon, M1+) or `03-macOS-Intel-Genie5-Setup.pkg` (Intel, pre-2020). Check  → **About This Mac** if you're unsure which Mac you have.

**macOS won't open the app ("developer cannot be verified" / "damaged").**
Beta builds aren't notarized. Right-click the app → **Open** → **Open** (macOS remembers it). If it reports "damaged," clear the download quarantine: `xattr -d com.apple.quarantine /Applications/Genie5.app` (or `xattr -cr <path>`). Running from source with `dotnet run` avoids this entirely.

**Windows SmartScreen blocks the exe.**
Builds from before v5.0.0-alpha.10 are unsigned and always trigger the blue panel: **More info → Run anyway**. Since alpha.10, `Genie5.exe` is EV-signed by Shadow Realms LLC (the project's support partner); a brand-new signed build may still show the panel briefly while SmartScreen reputation accrues per file. See [Installation](Installation).

**Linux: AppImage won't run / "FUSE not installed."**
Make it executable first (`chmod +x 04-Linux-Genie5.AppImage`), then `./04-Linux-Genie5.AppImage`. For the FUSE error, install it — Debian/Ubuntu: `sudo apt install libfuse2` (Fedora usually works out of the box). If text renders oddly on a minimal distro, install `fontconfig`. For a desktop-menu entry, use [AppImageLauncher](https://github.com/TheAssassin/AppImageLauncher).

**`dotnet` isn't recognized / build fails (building from source).**
You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (the SDK, not just the runtime). Verify with `dotnet --version` (should be 8.x). See [Installation](Installation#build-from-source).

## Connecting

**Fetch returns no characters.**
Usually a wrong account name or password, or the account has no DragonRealms characters. Re-enter carefully (the password field is case-sensitive).

**"Already logged in."**
That character is still in game in another session. Quit it there, then reconnect.

**"Billing problem."**
The account's subscription needs attention on [play.net](https://www.play.net/dr).

**Connect hangs, then fails.**
A firewall/proxy is blocking `play.net`, or the game is down for maintenance. Genie retries the **initial** connect a few times, then stops (auto-reconnect only fires for an established session you were actively using — see [Policy Compliance](Policy-Compliance)). To see *where* it stalls, turn on the connect trace: `#config conndebug true`, then reconnect — each protocol step prints into the game window with timings. If Genie auto-launched Lich, also try `#config lichdebug true` to mirror that session's Lich `temp/debug-*.log` as `[lich-debug]` lines (independent of `conndebug`; off again with `#config lichdebug false`).

**The title bar shows 🔓 instead of 🔒.**
🔒 means your login was encrypted (TLS); 🔓 means Genie couldn't reach the secure login port and fell back to the legacy plaintext path — it still connects, but the password is only obfuscated, not encrypted. If it happens every time, something on your network (firewall/filter) is blocking the secure port. Confirm with `#config conndebug true` and reconnect — the trace shows the TLS attempt and why it fell back. See [Connecting](Connecting#secure-tls-login--the-padlock).

**Connecting through Lich.**
Start Lich first so it's listening (default `127.0.0.1:8000`), then choose **Lich Proxy** in the Connect dialog. See [Lich 5 Integration](Lich-5-Integration).

More on connection messages: [Connecting & Profiles](Connecting#connection-troubleshooting).

## Files & profiles

**Where are my scripts/maps/settings?**
In your per-user Genie 5 folder — `%APPDATA%\Genie5` (Windows), `~/Library/Application Support/Genie5` (macOS), `~/.local/share/Genie5` (Linux). Full map: [Application Folders](Application-Folders).

**I saved a profile but it didn't keep my password.**
Saving the password is optional. If you saved without it, you'll be prompted at connect. When saved, it's encrypted with AES-256-GCM — never plain text.

**My settings/scripts from Genie 4 aren't here.**
Import rules via **File → Import from Genie 4…** ([guide](Importing-Genie4-Config)); copy `.cmd` scripts into your [Scripts folder](Application-Folders); import maps via the mapper ([guide](Updating-Maps-and-Scripts)).

## Scripts

**A script misbehaves around an undefined variable.**
Undefined `$var`s expand to empty (Genie 4-compatible), so a script never stops just because a variable is missing. If a script acts oddly around variables, check the spelling against the live list with `#var`, and guard with `if def(name)` where it matters. See [Scripting](Scripting#whats-different-from-genie-4).

**A Genie 4 script doesn't behave the same.**
Script-compat regressions are treated as bugs — please [file an issue](https://github.com/GenieClient/Genie5/issues/new) with the script (or the failing lines). A few intentional differences are listed in [Scripting Reference](Scripting-Reference#differences-from-genie-4).

**`#scripts`, `#stop`, `#stopall`** list and stop running scripts; the Script Bar shows them with controls.

## Mapper

**The map doesn't match where I am.**
Turn on the AutoMapper (learning) toggle and walk through the area so rooms get stamped, or pull a fresh copy of the zone via **Maps ▸ Update from Official Repo…**. Dense cities with duplicate room titles can briefly fail to lock on — Genie declines rather than guessing wrong. See [The Mapper](Mapper).

**Click-to-walk stopped partway.**
Expected if you typed a command, pressed Esc, the window lost focus for ~60s, or you got knocked off the route — the walker cancels rather than firing the wrong command. Click again to restart. See [Policy Compliance](Policy-Compliance).

**A route won't cross zones.**
Cross-zone routing is built in — `#goto` a room in another zone (or click one while browsing another zone's map) and Genie walks you across the boundary. If a particular route won't cross, the zone link is probably missing: the maps' border-room notes may not mark that crossing, or the transit table may need an entry — check **Maps ▸ Cross-Zone Connections…**. See [Cross-Zone Travel](Cross-Zone-Travel).

## Updating

**Every file fails during a maps update.**
No network, a proxy/firewall blocking the repo host, or a badly-skewed system clock (which breaks TLS). Local maps are never corrupted by a failed update — failed files are skipped; re-run later. See [Updating Maps & Scripts](Updating-Maps-and-Scripts#troubleshooting).

**The app updater didn't offer an update.**
In-app **Core** updates work on all three platforms, but only if you installed an updater-aware package: `01-Windows-Genie5-Setup.exe` (Windows), a `.pkg` (macOS), or the `04-Linux-Genie5.AppImage` (Linux). The plain **Portable `.zip`** builds don't register for updates — re-download from [Releases](https://github.com/GenieClient/Genie5/releases/latest) to upgrade those. See [Keeping Up to Date](Updates#core-the-app).

## Resetting to a clean state

To check whether a problem reproduces on a fresh install (and to recover safely):

1. **Quit Genie 5.**
2. **Rename** (don't delete) your `Genie5/` data folder to `Genie5-old/`.
3. Launch — a fresh folder is created.
4. Copy specific files back from `Genie5-old/` while the app is closed.

Details: [Application Folders](Application-Folders#resetting-to-defaults).

## Reporting a bug well

1. **File → Record Session** (toggle on — title bar shows 🔴 REC).
2. Reproduce the problem.
3. Disconnect (recording auto-stops); find the file in your `Logs/` folder.
4. [Open an issue](https://github.com/GenieClient/Genie5/issues/new) describing what you did, what you expected, and what happened — attach the relevant snippet. Include your OS and `dotnet --version`.

Recordings stay on your machine until you choose to share a snippet.

**What's the "Genie received an unrecognized game element" prompt?**
That's Genie offering to help itself. When DragonRealms sends a tag the parser doesn't handle yet, a slim strip appears above the vitals bar. Click it and Genie opens a **pre-filled GitHub issue** in your browser — the sample is **already redacted** (other players' speech removed) and your Genie version is attached. **Nothing is sent until you review it and press Submit.** It's the fastest way to help the parser keep up as the game changes; each unknown element only asks once per session, and **✕** dismisses it. See [The Interface → Notices](The-Interface#notices).

## Related

- [Connecting & Profiles](Connecting) · [Application Folders](Application-Folders) · [The Mapper](Mapper) · [Scripting](Scripting) · [Policy Compliance](Policy-Compliance)
