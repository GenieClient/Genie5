# Genie 5 — v5.0.0-alpha.7.1

A **maps & polish** point release on top of the Persistent Core. The headline is
a maps-updater fix; the rest is quality-of-life plus a security fix.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## 🗺️ Update Maps now pulls *every* map

The updater was silently dropping **13 of the 90 official maps** — including
**Riverhaven** (`Map30`), **Crossing West Gate**, **Shard West Gate**, the
**Southern Trade Routes**, **M'Riss**, **Hibarnhvidar**, and **Fang Cove**. Those
files are saved with a UTF-8 BOM that the XML importer rejected, so every Update
Maps run reported success but left them missing — which is why the mapper showed
**"No zone loaded"** in those areas. The BOM is now stripped on import. **If your
mapper couldn't place you somewhere, re-run File → Update Maps** and the missing
zones fill in.

## ✨ New & quality-of-life

- **Zone / Room ID on the status bar (#66)** — an optional bottom line showing
  your current zone and `$roomid`; a View-menu toggle switches the zone field
  between **name** and the numeric `$zoneid`.
- **Per-script pause/resume + debug level (#94)** — each running-script chip now
  has a **⏸ / ▶** pause button and a **dbg:N** button that cycles the script's
  trace level 0 → 1 → 5 → 10.
- **Atmospherics window (#85)** — a dockable Atmo stream tab (Window →
  Atmospherics).
- **`#echo` colour + mono (#84)** — `#echo Yellow …` renders coloured and
  `#echo mono …` renders monospaced, from the command bar and scripts.
- **`#var` / `#class` `list` & `set` subcommands (#97)** — `#var list` lists
  (instead of filtering by the text "list"), `#var set x 1` sets `x` (instead of
  creating a variable named "set"); plus full multi-row copy in the Variables grid.

## 🔒 Security

- **SGE game-entry key no longer logged (#45)** — the one-time `KEY=` token is
  masked in connection logs.

---

# Genie 5 — v5.0.0-alpha.7

The **Persistent Core** release. Genie now keeps one live session "brain" for the
whole time the app is open, and that unlocks a lot at once: you can **run scripts
while disconnected**, write a **logon script that connects and keeps running after
you're in the game**, and **switch characters without restarting** — all without
losing your engines, mapper, or trackers. The auto-walker also got a lot smarter
about pacing itself to the game.

> **Alpha software.** Expect rough edges. Builds are **unsigned** — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

> ⚠️ **Major release — please regression-test.** alpha.7 rebuilds the session
> core (the engine behind your connection, scripts, mapper, rules, and trackers)
> into a single **persistent core**, and significantly changes **auto-walk**
> pacing. That's a large change surface touching things that previously
> "just worked." **Please run your normal workflows — connect, scripts,
> triggers, mapper/travel, multi-character — and report anything that regressed**
> in the regression-testing tracker (#98). Reproduction steps help enormously.
> If something is worse than alpha.6.2, that's a bug we want.

## ✨ New since alpha.6.2

- **Run scripts offline + logon scripts that survive connecting (#88)** — the
  headline. The session core now persists for the whole app run instead of being
  rebuilt on every connect, so:
  - **While disconnected**, you can still run `.cmd`/`.js` scripts, set
    `#var`/`#class`/aliases/triggers, `#edit`, and save your config — handy for
    setting things up before you log in or testing a script offline.
  - A **logon script keeps running across the connect**: a `.cmd` that sets up
    your vars + trigger classes, then `put #connect <profile>`, then does more
    after login, runs straight through — the same script survives the connection.
    (In a `.cmd`, remember a bare `#` line is a comment; run client commands from
    a script with `put #…`, e.g. `put #connect`, just like Genie 4.)
- **Switch characters without restarting** — `#connect <other-profile>` (or the
  connect dialog) cleanly swaps characters in the same session: the previous
  character's rules/variables/classes/skills are cleared and the new character's
  saved config is loaded, while your scripts, mapper, and panels stay alive.
- **Bounded auto-reconnect** — an unexpected drop now retries on a sensible
  ladder (~2 quick tries, then a few longer ones) and **stops after ~1.5–2
  minutes** with a clear *Disconnected* instead of retrying forever. A deliberate
  `quit`/`exit` never auto-reconnects.
- **Smarter auto-walk pacing** — the auto-walker now holds each step until the
  game is ready: it **waits out roundtime** and movement-blocking states
  (stunned/webbed), and **stands you up automatically** when you're sitting,
  kneeling, or prone before it walks. It also no longer reports a false **"No
  path"** before your skills are loaded (gated exits are assumed reachable until
  Genie has read your `info`/`exp`).
- **Built-in trackers + new panels** — **Spell Timer**, **Experience**, and
  **Time Tracker** are now built into Genie (you can delete their old plugin
  DLLs), plus new **Mobs** and **Players** panels that list what's in the room
  (#86).
- **Dock fixes** — a panel you float out (e.g. the Mapper) now reopens **fully
  on-screen** instead of off a monitor edge, and its title bar **double-click
  maximizes / restores** like a normal window.
- Plus a batch of smaller fixes (#80 / #81 / #82) and parser improvements.

## ✅ What works

Connection (Secure SGE / Lich proxy / dev-replay), the StormFront XML parser and
live GameState, the full Genie 4 `.cmd` script engine plus JavaScript `.js`
scripts — now runnable **offline** and **across reconnects** — the rules engines
(`#alias` / `#trigger` / `#highlight` / `#substitute` / `#gag` / `#macro` /
`#class` / `#var`) with `.cfg` persistence and **per-character switching**, the
AutoMapper (click-to-goto, `#goto`, weighted + cross-zone routing, RT/posture-paced
walking), dockable panels with save/load layouts, built-in trackers, the plugin
host, and the in-app updater. See the [README status table](README.md#status) for
the full list.

## 🚧 Not working yet / known gaps

- **Unsigned builds** — SmartScreen warning on Windows (#33).
- **macOS / Linux update channels** — the in-app updater self-updates on Windows
  only; other platforms install fresh builds manually for now (#27).
- **Same-description rooms** — server-uid pacing greatly improves walking through
  identical rooms, but routes through them can still occasionally mis-resolve
  (#76 / #77); `#mapper reset` helps.
- **Skill-gated routing accuracy** — until Genie has read your `info` + `exp`,
  gated exits are *assumed reachable* (so paths aren't blocked); once read, climbs
  and swims you can't take are filtered out.
- **No light theme** yet (single dark palette, #20); no injuries panel (#18); no
  raw-XML inspector window (#14).

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.6.2...v5.0.0-alpha.7
