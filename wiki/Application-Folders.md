# Application Folders

Genie 5 keeps all of your personal data — scripts, maps, logs, settings, per-character profiles — in a single per-user folder. Knowing where it lives is useful for backing up before an upgrade, editing scripts in your own editor, pointing the import dialog at a Genie 4 install, and syncing across machines.

## Where Genie 5 lives

The location is resolved by [AppPaths.Discover](https://github.com/GenieClient/Genie5/blob/main/src/Genie.Core/Runtime/AppPaths.cs):

### macOS

```
~/Library/Application Support/Genie5/
```

Paste that into Finder's **Go → Go to Folder…** (`⇧⌘G`). `Library` is hidden by default; the Go-to dialog reveals it.

### Windows

```
%APPDATA%\Genie5\
```

…which resolves to `C:\Users\<you>\AppData\Roaming\Genie5\`. Paste `%APPDATA%\Genie5` into Explorer's address bar.

### Linux

```
$XDG_DATA_HOME/Genie5/      # or, if XDG_DATA_HOME is unset:
~/.local/share/Genie5/
```

### Portable mode (portable-first)

Genie 5 resolves its data root **portable-first**: a `Config` folder (or a `genie5.portable` / `.portable` marker) **in the Genie program folder** always wins, and everything lives alongside the app instead of in the per-user location. Useful for a USB-stick, a synced cloud folder, or a self-contained install. (This is the first thing `AppPaths.Discover` checks — local beats the per-user folder even when both exist.)

**Where is "the Genie program folder"?** For the **Portable** download it's the folder you extracted — the one holding `Genie5.exe`, `Update.exe`, and your `Config` / `Scripts` / `Maps` folders. Internally the app actually runs from a `current\` subfolder that gets replaced on every update, so Genie deliberately keeps your data **one level up** in that program folder — where you put it, and where it survives updates. Drop your `Scripts`, `Maps`, etc. beside `Genie5.exe`, not inside `current\`.

### First launch

On a brand-new install — when Genie finds **no** data beside the app **and none** in the per-user folder — it asks where to set up: **Portable (this folder)** or your **User folder**. The default action is Portable. Whatever you pick is created on the spot, so the prompt only appears once; later launches just find the existing data and skip straight to it.

## What's inside

```
Genie5/
├── Config/      ← settings.cfg + rule .cfg files (shared / non-character)
├── Profiles/    ← per-character config: Profiles/<Char>-<Account>/*.cfg
├── Scripts/     ← your .cmd files
├── Maps/        ← zone files (Map##_*.xml) + ZoneConnections.xml
├── Logs/        ← AutoLog output, one file per character per session
├── Sounds/      ← sound files for #play / triggers
├── Voices/      ← TTS voice models
├── Layouts/     ← saved window layouts
├── Plugins/     ← plugin DLLs — see the Plugins page
└── Art/         ← image assets
```

| Folder | What's in it | When to touch |
| --- | --- | --- |
| `Config/` | `settings.cfg` (app settings) and the shared rule files: a `.json` file per rule type (`aliases.json`, `triggers.json`, `highlights.json`, `substitutes.json`, `gags.json`, `macros.json`, `variables.json`, `classes.json`) plus a Genie 4-style `.cfg` twin of each (a plain-text list of the commands that recreate the rules), kept in sync. | Mostly managed via **Edit → Configuration…**. Hand-editable — external edits to the seven rule `.json` files apply **live, without a reconnect** (you'll see a `[config] … reloaded` line); other files load on next launch/connect. |
| `Profiles/` | One subfolder per character (`<Char>-<Account>/`) holding that character's own copy of the rule files. The first time a character connects, the folder is seeded from your shared `Config/` files, then diverges independently. | Created automatically. Edit the per-character files here (rule `.json` edits hot-reload too), or via the GUI while that character is connected. |
| `Scripts/` | Your `.cmd` script files, plus any helper scripts you pull from the community repo. | Drop any script here to run it as `.scriptname` (or `put .scriptname`). |
| `Maps/` | Zone files in Genie 4's XML format (`Map1_Crossing.xml`, …) and `ZoneConnections.xml` (the cross-zone transit graph). | Populated via **File → Import from Genie 4…** or **Maps ▸ Update from Official Repo…**. Jump there via **Maps ▸ Open Maps Folder**. |
| `Logs/` | When AutoLog is on, each session writes a `<character>_<timestamp>` log of plain in/out text. Toggle it live with the **File → Auto Log** checkbox — it applies mid-session. | Read-only from the app's view. Safe to delete or archive. |

> **Jumping to any of these:** **File → Open Directory** opens the folder of your choice — Data root, Config (profile-aware), Logs, Maps, Scripts, or Plugins — in your system file manager.

> **Note on formats.** Genie 5's primary rule store is JSON, but every rule type is also written as a Genie 4-style `.cfg` file (one command per line), and zone maps stay in Genie 4's XML format — so round-tripping with the Genie 4 ecosystem and the community Maps repo stays clean.

## Per-character profiles

Settings split into two tiers:

- **Shared** (`Config/`) — the baseline, used by sessions without a character (LIST mode, dev replay).
- **Per-character** (`Profiles/<Char>-<Account>/`) — each character gets its own aliases/triggers/etc., seeded once from the shared baseline. So your combat triggers on one character don't follow you onto a shopping alt.

The active profile directory is chosen at connect time from the character + account names.

## Backups, syncing, multiple machines

Everything in `Genie5/` is plain text (`.cfg` / `.cmd` / `.xml` / `.log`). Copy the whole folder to a backup drive or sync it via Dropbox / iCloud / OneDrive; Genie 5 picks it up on the destination machine next launch. To share GUI rules but keep scripts machine-local, sync only `Config/` and `Profiles/`.

## Resetting to defaults

1. **Quit Genie 5.**
2. **Rename** (don't delete) the `Genie5/` folder to `Genie5-old/`.
3. Launch — a fresh empty `Genie5/` is created.
4. Recover specific files later by copying them back from `Genie5-old/` while the app is closed.

This is also the clean way to check "does my bug reproduce on a fresh install?" before reporting it.

## Why this location?

`~/Library/Application Support` (macOS), `%APPDATA%` (Windows), and `$XDG_DATA_HOME` (Linux) are the standard per-user "non-document" data locations. They survive app reinstalls, get backed up by Time Machine / OneDrive, don't clutter your home directory, and aren't shared between OS users. The folder is created on first launch — nothing to set up in advance.
