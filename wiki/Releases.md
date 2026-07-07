# Releases & Changelog

Where to get Genie 5 and what changed in each build. Downloads live on the [Releases page](https://github.com/GenieClient/Genie5/releases); the [latest release](https://github.com/GenieClient/Genie5/releases/latest) is always the one to grab. For how to install each download, see [Installation](Installation); for staying current after that, [Keeping Up to Date](Updates).

> Genie 5 is **alpha**. Versions are tagged `v5.0.0-alpha.N`. Builds are unsigned for now (Windows/macOS show a first-launch warning — see [Installation](Installation#platform-first-launch-notes)); signed Windows builds are expected from an upcoming release.

## Latest: v5.0.0-alpha.8.4 — Parser Gap Reporting

When DragonRealms sends an element Genie's parser doesn't recognize yet, a one-click prompt drafts a pre-redacted GitHub issue for you to review and submit.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.4** as a delta.

- **Report parser gaps ([#152](https://github.com/GenieClient/Genie5/issues/152))** — if the game sends an element the parser doesn't handle yet, a slim notice offers to report it. One click opens a **pre-filled, pre-redacted** GitHub issue in your browser (other players' speech removed, your version attached); nothing is posted until you review and submit. Each unknown element asks once per session.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.4)

## v5.0.0-alpha.8.3 — Experience & Highlights

The Experience window catches up to Genie 3/4, a Display Settings Theme manager arrives, dock windows gain Save As / Find / Word Wrap, and a batch of highlight fixes land.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.3** as a delta.

- **Experience window parity ([#144](https://github.com/GenieClient/Genie5/issues/144))** — a **Track gain** checkbox shows session rank-gain per skill plus a total; **Numbers Only** / **Short Names** density stops now show the mindstate as a number; **your highlights colour the panel**; and the header shows skills learning, mind-locked count, and elapsed session time.
- **Display Settings → Theme tab ([#20](https://github.com/GenieClient/Genie5/issues/20))** — import / export / duplicate / delete themes from one place; the secondary dialogs follow the active theme too.
- **Dock-window Save As…, Find…, Word Wrap ([#120](https://github.com/GenieClient/Genie5/issues/120))** — save a text window to a file, search within it, or toggle word wrap; plus a window-menu Copy fix.
- **Highlight fixes** — the long `Roundtime: N seconds.` form highlights fully ([#145](https://github.com/GenieClient/Genie5/issues/145)); editing a highlight updates it in place instead of duplicating ([#142](https://github.com/GenieClient/Genie5/issues/142)); your highlight rules now win over the built-in default colours ([#143](https://github.com/GenieClient/Genie5/issues/143)).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.3)

## v5.0.0-alpha.8.2 — Themes & Type-Anywhere

Seven built-in UI themes with a live in-app editor, three Genie 3/4 input features straight from community requests, spoken-alert upgrades, and a batch of script-engine fixes.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.2** as a delta.

- **UI Themes ([#20](https://github.com/GenieClient/Genie5/issues/20), first wave)** — Edit → Theme: Dark, Light, Genie 4 Classic, High Contrast, Solarized Dark/Light, Wrayth-style; live repaint, no restart. **Edit Theme…** opens a color-role editor with live preview; custom themes are shareable JSON in `Config/Themes`. Per-window / per-stream color overrides still win.
- **Type anywhere ([#141](https://github.com/GenieClient/Genie5/issues/141))** — typing with a panel or the game text focused routes into the command bar, Genie 3/4 style.
- **10-key hotkeys ([#140](https://github.com/GenieClient/Genie5/issues/140))** — numpad `/ * - +` → `assess / health / fatigue / look` by default, rebindable.
- **`#flash` ([#139](https://github.com/GenieClient/Genie5/issues/139))** — taskbar flash (Windows) / dock bounce (macOS) until refocused.
- **Time Tracker panel + `#statusbar` slots** — the Elanthian clock as a dockable window; ten positional status-bar slots under the vitals bar.
- **Spoken alerts** — per-rule Speak on highlights/triggers; `#tts rate` / `#tts volume`.
- **Fixes** — mm_train script-engine batch (`#clear <name>`, `#script abort`, argument parsing, inline `{#eval}`, quoted `#echo ">window"`, `triggeroninput`), plugin slash-commands reach plugins again, and the phantom "Updates available: Maps" banner is gone.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.2)

## v5.0.0-alpha.8.1 — Genie5.exe Everywhere

A portable-install follow-up: the executable is now **`Genie5.exe`** on every platform, and Genie announces which data folder it is using the moment it starts.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.1** as a delta.

- **One executable name (#137)** — the app exe, the Task Manager process name, and the portable launcher all say `Genie5` now (the app inside `current\` used to be `Genie.exe`). ⚠️ **One-time note:** shortcuts made directly to `current\Genie.exe` stop working — re-point them at the root `Genie5.exe`, which survives every update.
- **`[data]` startup line ([#138](https://github.com/GenieClient/Genie5/issues/138))** — the first game-window line shows the resolved data root and mode, e.g. `[data] root: D:\Genie 5 (portable)`; a second line appears when a profile's Data Directory override repoints scripts/rules/layouts elsewhere.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.1)

## v5.0.0-alpha.8 — Menu Parity

The Genie 4 menu-parity milestone: master toggles for every rule engine, the Icon Bar status strip, an Injuries panel, a Scripts updater with per-component Update Settings, and a stack of muscle-memory menu items — plus Lich-attach and scripting fixes from community reports.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8** as a delta.

- **Master Toggles** — File-menu switches for Highlights / Triggers / Substitutes / Gags / Aliases / Images; rules stay loaded, they just stop applying. Mirrored by `#config triggers off` etc.
- **Icon Bar** — posture + condition chips (stunned, bleeding, hidden, invisible, webbed, joined — plus poisoned and diseased, new over Genie 4) below the vitals bar.
- **Injuries panel** — colour-coded body silhouette of wounds and scars, with opt-in `health`-poll refinement ([#18](https://github.com/GenieClient/Genie5/issues/18)).
- **Scripts updater + Update Settings** — pull community script repos like a git pull; choose per component (client / maps / plugins / scripts) what the startup check covers and what may auto-install. Auto client updates apply on exit, never mid-session.
- **Menu parity round-up** — Open Directory submenu, Auto Log toggle, Paste Multi Line, Always on Top, Align Input to Game Window, Magic Panels, and the room-art panel under its Genie 4 name, **Portrait**.
- **Fixes** — Lich attach rebuilds room + character identity ([#126](https://github.com/GenieClient/Genie5/issues/126), [#127](https://github.com/GenieClient/Genie5/issues/127)); script `count()` occurrence semantics ([#134](https://github.com/GenieClient/Genie5/issues/134)); `||` with an unset variable ([#133](https://github.com/GenieClient/Genie5/issues/133)); unbalanced-quote hint ([#135](https://github.com/GenieClient/Genie5/issues/135)); bad conditions warn instead of silently failing; PageUp/PageDown window scrolling ([#136](https://github.com/GenieClient/Genie5/issues/136)); Open Scripts Folder on locked-down Windows setups.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8)

## v5.0.0-alpha.7.11 — Named Windows & Panel Filters

The Genie 4 named-window command family (`#log`, `#link`, `#clear`, `#window`) so classic menu scripts run as-is, directed-echo routing fixes from a community report, MonsterBold in the Room panel, and type-to-filter boxes on the Configuration panels.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.11** as a delta.

- **Named-window commands** — `#window add|open|close|remove "Name"`, clickable `#link` menu lines, `#clear >window`, and `#log` file logging: the Genie 4 menu-script toolkit (mm_train-style) now works.
- **`#echo >Main` fixed** — directed echoes reach Main/Game and every built-in stream window instead of being silently dropped; only `>Log`/`>ItemLog` worked before. Stray chevrons in a target variable no longer manufacture a junk `>Log` window.
- **MonsterBold in the Room panel** — the room objects line golds creatures like the game window does.
- **Configuration panel filters** — type-to-filter boxes on Aliases, Triggers, Highlight Strings, Substitutes and Gags.
- **Per-stream "Also show in Main"** — a Layout-tab toggle echoes a stream's lines into the main window too.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.11)

## v5.0.0-alpha.7.10 — Experience Density & Active Spells

An Experience-window **Density** slider, Active Spells promoted to a proper window, and a `#config list` grouped by category.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.10** as a delta.

- **Experience density slider** — condense each skill line from **Full** down to **Brief** (Full / No count / Numbers only / Short names / Brief); the slider, `#config experiencedensity`, and `settings.cfg` all drive one setting ([#125](https://github.com/GenieClient/Genie5/issues/125)).
- **Active Spells window** — now a first-class dock tool: it stays closed when you close it and carries the standard window decorations in windowed mode ([#112](https://github.com/GenieClient/Genie5/issues/112)).
- **`#config list` grouped by category** — the settings dump is organised into labelled sections instead of one flat list.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.10)

## v5.0.0-alpha.7.9 — Scripting Parity & MonsterBold

Three Genie 4 script-language fixes from community reports, a `#goto` combat-retreat fix, and **MonsterBold** — DR's creature and NPC names now stand out in colour.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.9** as a delta.

- **MonsterBold** — creature and NPC names DR marks as monster-bold now render in a distinct colour (default gold) in the main window and every stream window; on by default, toggle it live under **Config → Highlights → Presets** or with `#config monsterbold on|off` ([#131](https://github.com/GenieClient/Genie5/issues/131)).
- **`#goto` retreats when engaged** — travel/auto-walk no longer stalls when a creature has you engaged; it retreats and retries, like Genie 3/4 ([#130](https://github.com/GenieClient/Genie5/issues/130)).
- **Nested variables** — stacked references like `$%output` and `%harness%counter` now resolve inside-out, matching Genie 4 ([#128](https://github.com/GenieClient/Genie5/issues/128)).
- **`def()` sees `#var` variables** — `def(name)` now checks the `#var` store and reports existence, Genie 4-style ([#129](https://github.com/GenieClient/Genie5/issues/129)).
- **`\;` escape in the separator** — an escaped semicolon (or one inside quotes/braces) no longer splits a command mid-value ([#132](https://github.com/GenieClient/Genie5/issues/132)).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.9)

## v5.0.0-alpha.7.8 — Travel & Mapper Polish

Smarter auto-walk routing that factors your Athletics skill, a movement pacing fix, and two Mapper window fixes — a single right-click menu and no more duplicate Mapper window on layout changes.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.8** as a delta.

- **Skill-aware travel routing** — auto-walk weighs swim/climb exits against your Athletics rank, so a strong swimmer takes the water instead of waiting on a ferry ([#122](https://github.com/GenieClient/Genie5/issues/122)).
- **Travel pacing fix** — map movement no longer sends `slow`/`rt` pacing prefixes to the game verbatim ([#123](https://github.com/GenieClient/Genie5/issues/123)).
- **One Mapper right-click menu** — right-clicking a room now opens a single menu (Float / Close folded in, room actions greyed when you click empty space) instead of two overlapping ones.
- **No duplicate Mapper window** — floating the Mapper then resetting the layout or toggling windowed mode no longer leaves an orphaned second window.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.8)

## v5.0.0-alpha.7.7 — Community Fixes & Polish

A bug-fix and polish release built mostly from issue reports — clickable news listings, clearer disconnect feedback, an accurate mob count, and a Room window that wraps again in windowed mode.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.7** as a delta.

- **Clickable news listings** — the numbered items in DR's plain-text `news` listing are now click-to-open ([#30](https://github.com/GenieClient/Genie5/issues/30)).
- **Disconnect feedback** — a timestamped `disconnected` line plus an optional "Disconnected" popup when you leave the game; toggle the popup under **Window → Disconnect Popup** ([#114](https://github.com/GenieClient/Genie5/issues/114)).
- **Accurate mob count** — same-type creatures joined by "and" ("a giant viper and a giant viper") are split and counted individually ([#118](https://github.com/GenieClient/Genie5/issues/118)).
- **Room window wraps in MDI** — fixed text running off the edge of the Room window in windowed mode ([#124](https://github.com/GenieClient/Genie5/issues/124)).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.7)

## v5.0.0-alpha.7.6 — Genie 4 Script-Language Parity

A faithful-port pass over the scripting language so it behaves like Genie 4 — verified against ~130 community scripts.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.6** as a delta.

- **Genie 4 parity behaviour changes** — `match(a, b)` is now exact equality (was substring); `contains` / `startswith` / `endswith` are case-sensitive; `indexof` / `lastindexof` are 1-based; `if_N` means "at least N arguments were passed".
- **New operators & functions** — `eq` (≡ `=`), `<>` (≡ `!=`), and the `instr` / `substring` / `defined` aliases; `%argcount` / `$argcount` plus a faithful `shift`.
- **Heads-up** — scripts written against the older Genie 5 behaviour may need a tweak; see the full notes.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.6)

## v5.0.0-alpha.7.5 — Text-to-Speech

Genie can now **read the game aloud** with natural neural voices that run entirely on your machine — offline, free, and private (no game text leaves your computer). A first step toward making DragonRealms playable for blind and low-vision players.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.5** as a delta.

- **`#speak <text>`** (alias `#say`) reads a line aloud — typed, from scripts, or as a trigger action.
- **Voices on demand** — `#tts install` grabs a natural neural voice (one-time ~60 MB); `#tts voices` / `#tts use <name>` to browse and switch. Identical on Windows, macOS, and Linux.
- **Read-aloud by stream** — `#tts read on` auto-reads whispers, talk, thoughts, and deaths; `#tts read <stream>` / `#tts mute <stream>` tune the list. Urgent lines jump the queue; `#tts stop` silences everything.
- Everything is **opt-in** — nothing speaks until you turn it on. See [Text-to-Speech](Text-to-Speech).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.5)

## v5.0.0-alpha.7.4 — Circle Calculator & Raw XML

A built-in guild **Circle Calculator**, a live **Raw XML** stream inspector, more right-click window actions, and Genie 4 `#parse` parity.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.4** as a delta.

- **Circle Calculator** — `/calc [guild] [circle]` shows how many ranks each skill still needs for your next circle (auto-detecting your guild via `info`); `/sort [skillset|group] [rank]` lists your skills highest-rank first. Requirement tables ship built-in ([#117](https://github.com/GenieClient/Genie5/issues/117)).
- **Raw XML window** — a dockable, read-only live view of the raw server XML stream, before any tag stripping; capped buffer, auto-scroll, default hidden, reopen via **Window → Raw XML** ([#14](https://github.com/GenieClient/Genie5/issues/14)).
- **More window right-click actions** — the per-window menu gains **Copy All**, **Float / Re-dock**, and **Pause / Resume scrolling** ([#13](https://github.com/GenieClient/Genie5/issues/13)).
- **`#parse` parity** — `#parse` feeds your triggers and plugins and works typed from the command bar ([#113](https://github.com/GenieClient/Genie5/issues/113)).
- **Fixes** — map labels stay put instead of snapping onto the nearest room; `exp all` now populates your skill ranks, so the pathfinder and the mapper's "fetch your skills" banner work.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.4)

## v5.0.0-alpha.7.3 — Windows & Maps

Genie 4-style per-window controls (a right-click menu with timestamps and a Name List Only filter), the window-chrome settings reorganised under the Layout menu, map landmark labels, and a skill/class/circle-gated routing fix ([#90](https://github.com/GenieClient/Genie5/issues/90), [#95](https://github.com/GenieClient/Genie5/issues/95)).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.3)

## v5.0.0-alpha.7.2 — JavaScript libraries

Call JavaScript functions from your `.cmd` scripts — the Genie 4 "array script" pattern. Keep a library of functions in a `.js`, `include` it, and call it.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7.2** as a delta.

- **Call JS functions from a `.cmd`** — `include foo.js` loads a function library for the running script; **`js <expr>`** runs a function and **`jscall <var> <expr>`** stores its result in `%var`. Functions read/write your script's variables via bare `getVar`/`setVar` (→ `%vars`) and `getGlobal`/`setGlobal` (→ `$globals`) — great for list/array work ([#104](https://github.com/GenieClient/Genie5/issues/104)).
- **Genie 4 libraries port cleanly** — `include` auto-converts the old `array.length()` idiom to `array.length`, so existing Genie 4 `.js` array libraries run unchanged.
- **New [JavaScript Scripting](JavaScript-Scripting) page** — covers standalone `.js` scripts and the new function-library interop, with a sample for every call.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7.2)

## v5.0.0-alpha.7.1 — Maps & polish

A maps-and-polish point release on top of the Persistent Core.

**The big one:** **Update Maps now pulls every map.** The updater was silently dropping **13 of the 90 official maps** — Riverhaven (`Map30`), Crossing West Gate, Shard West Gate, the Southern Trade Routes, M'Riss, Hibarnhvidar, Fang Cove and more — because they're saved with a UTF-8 BOM the XML importer rejected. Every Update Maps run looked successful but left them missing, which is why the mapper showed **"No zone loaded"** in those areas. The BOM is now stripped on import. **If the mapper couldn't place you somewhere, re-run File → Update Maps** and the missing zones fill in.

**New & quality-of-life**

- **Zone / Room ID on the status bar** — an optional bottom line showing your current zone and `$roomid`; a View-menu toggle switches the zone field between the **name** and the numeric `$zoneid` ([#66](https://github.com/GenieClient/Genie5/issues/66)).
- **Per-script pause/resume + debug level** — each running-script chip now has a **⏸ / ▶** pause button and a **dbg:N** button that cycles the script's trace level 0 → 1 → 5 → 10 ([#94](https://github.com/GenieClient/Genie5/issues/94)).
- **Atmospherics window** — a dockable Atmo stream tab (Window → Atmospherics) ([#85](https://github.com/GenieClient/Genie5/issues/85)).
- **`#echo` colour + mono** — `#echo Yellow …` renders coloured and `#echo mono …` monospaced, from the command bar and scripts ([#84](https://github.com/GenieClient/Genie5/issues/84)).
- **`#var` / `#class` `list` & `set` subcommands** — `#var list` lists instead of filtering by "list", `#var set x 1` sets `x` instead of creating a variable named "set"; plus full multi-row copy in the Variables grid ([#97](https://github.com/GenieClient/Genie5/issues/97)).

**Security**

- **SGE game-entry key no longer logged** — the one-time `KEY=` token is masked in connection logs ([#45](https://github.com/GenieClient/Genie5/issues/45)).

## v5.0.0-alpha.7 — Persistent Core

The session core now persists for the whole time Genie is open, instead of being rebuilt on every connect. That one change unlocks a lot: you can **run scripts while disconnected**, write a **logon script that connects and keeps running after login**, and **switch characters without restarting** — all without losing your engines, mapper, or trackers. The auto-walker also got smarter about pacing itself to the game.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.7** as a delta.

> ⚠️ **Big release — please regression-test.** This rebuilds the session core and changes auto-walk significantly. Run your normal workflows — connect, scripts, triggers, mapper/travel, multi-character — and report anything that regressed in the [regression tracker (#98)](https://github.com/GenieClient/Genie5/issues/98).

**Persistent core & scripting**

- **Run scripts offline** — set `#var` / `#class` / aliases / triggers, run `.cmd` / `.js`, `#edit`, and save your config while disconnected — handy for setting up before login or testing a script offline ([#88](https://github.com/GenieClient/Genie5/issues/88)).
- **Logon scripts survive connecting** — a `.cmd` that sets things up, then `put #connect <profile>`, then does more after login runs straight through; the same script keeps running across the connect. (In a `.cmd`, a bare `#` line is a comment — run client commands with `put #…`, as in Genie 4.)
- **Switch characters without restarting** — `#connect <other-profile>` clears the previous character's rules / variables / classes / skills and loads the new one's, while your scripts, mapper, and panels stay alive.

**Connection & travel**

- **Bounded auto-reconnect** — an unexpected drop retries on a short ladder and **stops after ~1.5–2 minutes** with a clear *Disconnected*, instead of retrying forever. A deliberate `quit` / `exit` never auto-reconnects.
- **Smarter auto-walk** — holds each step until the game is ready: it waits out **roundtime** and movement blocks (stunned / webbed), and **stands you up automatically** when you're sitting, kneeling, or prone. It also no longer reports a false **"No path"** before your skills are read.

**Trackers & windows**

- **Built-in Spell Timer, Experience, and Time Tracker** — no external plugin DLLs needed — plus new **Mobs** and **Players** panels listing what's in the room ([#86](https://github.com/GenieClient/Genie5/issues/86)).
- **Dock fixes** — a floated panel (e.g. the Mapper) reopens **fully on-screen** instead of off a monitor edge, and its title bar **double-click maximizes / restores**.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.7)

## v5.0.0-alpha.6.2 — type-ahead & script editing

Genie now shows a live **type-ahead counter** in the command bar, and that limit finally matches your **account tier** (free 1 / premium 2 / +LTB 3) and self-corrects from the server. Script editing also gets friendlier: `#edit` opens your chosen editor — including the Genie 4 `#config editor` setting that used to be ignored — and **creates a new script** when one doesn't exist yet.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.6.2** as a delta.

**Command bar**

- **Type-ahead pip counter** — a counter between the roundtime badge and the input box shows the per-account type-ahead cap as pips (filled = queued ahead of the game, hollow = free slots). Dim when idle, **amber** at the cap, so you can see when the game is lagging your typing ([#67](https://github.com/GenieClient/Genie5/issues/67)).
- **Tier-accurate type-ahead** — the limit is seeded from your account tier (free = 1 line, premium = 2, premium + LTB = 3) and self-calibrates if the server reports a different cap, so the auto-walker stops overrunning the buffer on free/premium accounts ([#66](https://github.com/GenieClient/Genie5/issues/66)).

**Scripting**

- **Editor selection that works** — `#edit` and the ✏ Script Bar icon open your editor in a clear order: Display Settings → Editor Path, then `#config editor`, then your OS default. The Genie 4 `#config editor` setting is now honoured instead of silently ignored. See [Scripting](Scripting#choosing-an-editor).
- **Create-on-edit** — `#edit <name>` on a script that doesn't exist creates it (Genie 4 parity). An explicit extension (`#edit foo.js`) is used directly; otherwise a small dialog asks which supported type to create (`.cmd` / `.inc` / `.js`). See [Scripting](Scripting#creating-a-new-script).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.6.2)

## v5.0.0-alpha.6.1 — secure login & fixes

Genie now signs in over an **encrypted TLS connection** — the same secure path Lich 5 uses — falling back to the legacy plaintext login only if the secure port is blocked. A **padlock** in the title bar (🔒 encrypted / 🔓 obfuscated fallback) tells you which you got. Riding along: a quieter `#var`, human-readable config files, and an updater that no longer freezes at 70%.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.6.1** as a delta.

**Connecting**

- **Secure (TLS) login** — authentication runs over `eaccess.play.net:7910` with certificate pinning, so your password travels inside an encrypted tunnel instead of the old lightly-obfuscated plaintext scheme ([#61](https://github.com/GenieClient/Genie5/issues/61)). If the secure port is blocked, Genie **falls back automatically** to plaintext rather than failing — and shows a 🔓 so you know.
- **Padlock indicator** — 🔒 *"Connected over TLS (encrypted)"* or 🔓 *"login was obfuscated, not encrypted"* in the title bar and game window. See [Connecting](Connecting#secure-tls-login--the-padlock).
- **Clearer connection failures** — a refused or failed login surfaces the actual reason (bad password, already-logged-in, billing, timeout…) in the game window instead of a generic error.
- **`#config conndebug`** — opt-in connect trace: the next login prints each protocol step with timings into the game window, ideal for diagnosing a stalled login. Off by default. See [Troubleshooting](Troubleshooting#connecting).

**Fixes**

- **Updater no longer freezes at 70%** — the Core updater reports real phase-based progress and has a stall watchdog ([#60](https://github.com/GenieClient/Genie5/issues/60)).
- **Quieter `#var` / `#tvar`** — echoes *"Variable set:"* only when you type it, not every time a script, trigger, or alias sets a variable.
- **Readable config files** ([#78](https://github.com/GenieClient/Genie5/issues/78)) — settings write with a relaxed JSON encoder, so regex patterns and UTF-8 text stay legible instead of escaped into `\u00NN` soup. Thanks to VTCifer for the report.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.6.1)

## v5.0.0-alpha.6 — Weighted Travel

The auto-walker now routes by **effort**, not raw hop count — it avoids brutal open-water swims and cliff climbs when a bridge or gate exists, paces reliably through identical-looking rooms, and recovers from a stuck move instead of hanging.

- **Weighted pathfinding** — routes score high-roundtime terrain (swims, climbs) against drier alternatives, so a one-hop flounder-swim no longer beats a slightly longer bridge route.
- **Server-uid pacing** — in stretches of identical rooms (lava fields, marshes) the walker tracks the live server room id and keeps moving instead of stalling.
- **Stuck-move recovery** — a per-step watchdog fails a stuck auto-walk cleanly (and signals `#goto`-driven scripts) instead of hanging.
- **Cross-zone transitions** follow boundary-room map notes, so a route can walk from one zone into the next.
- **`#goto` script compatibility** — the engine emits the Genie 4 automapper signals (`YOU HAVE ARRIVED!` / `MOVEMENT FAILED` / `DESTINATION NOT FOUND`) so power-travel scripts like `travel.cmd` drive the mapper correctly.
- **Edit Exit — Skill / Environment / Guild** — right-click an exit → **Edit Exit** is reorganised into three carded sections with dropdowns (all 53 DR skills, traversal environments, guild routes), so the community can fill in the **Exit Details** the pathfinder routes on.
- **Live Audit** (`#audit on`) and **Ctrl+Click a room to walk there**.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.6)

## v5.0.0-alpha.5 — prompts, scene art, preset colours & scripting parity

A hearty feast of a release: the game **prompt** now shows in the window, notable rooms bloom with **scene artwork**, descriptions and whispers wear their **preset colours**, and the scripting larder is fully stocked with reserved `$variables` — plus a round of Genie 4 parity across the mapper and `#config`.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.5** as a delta.

**Display**

- **Game prompt in the window** — the `>` / `R>` / `H>` prompt renders in the game window using your `prompt` string; `promptbreak` keeps it on its own line and `promptforce` reconstructs the status letters (kneeling, hidden, roundtime, …) from live indicators.
- **Scene panel** — DR room/scene artwork for notable locations, fetched from the play.net art CDN and shown in a dockable panel (Window → Scene; toggle with `#config showimages`).
- **Preset colouring** — room descriptions, whispers, speech and the rest of the preset palette render in their configured colours, with a Configuration → Presets editor.
- **Condensed mode** — collapse blank lines for a tighter scroll.

**Scripting & mapper**

- **Full reserved-variable vocabulary** — `$health`, `$roomid`, `$zoneid`, the status flags, hands, and the clock family are all readable, and **`#var` now lists** the reserved/live-state set beside your own variables ([#45](https://github.com/GenieClient/Genie5/issues/45), [#72](https://github.com/GenieClient/Genie5/issues/72)).
- **`#mapper reset`** — re-resolve a lost location without moving, for the same-description rooms that trip the mapper up ([#75](https://github.com/GenieClient/Genie5/issues/75)).
- **`#config` settings system** — `#config <key> <value>` / `list` backed by `settings.cfg`, with ~20 Genie 4 settings wired and a Configuration → Scripts tab.

**Sound & quality of life**

- **Sound** — optional SFX on triggers/highlights and a `#play` command (cross-platform).
- AutoLog (automatic session log), spell timer (`$spelltime`), monster count (`$monstercount` / `$monsterlist`), preset-coloured presets, and a Help → About dialog.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.5)

## v5.0.0-alpha.4.2 — portable mode for the downloadable builds

The alpha.4.1 fix was only half the story: it got portable-first discovery right, but the **downloadable** Windows/macOS builds are packaged so the app runs from an internal `current/` subfolder (the one with `Update.exe` beside it). Genie looked for your data *inside* that subfolder, found none, and fell back to the per-user OS folder (`%APPDATA%` on Windows) — so a portable unzip with your `Config` / `Scripts` / `Maps` beside `Genie.exe` still leaked into the user profile.

Now Genie resolves its data to the **program folder** — the one holding `Genie.exe` and your data folders — which is both where you put your files and where they survive an update (the `current/` subfolder is replaced wholesale each update).

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.4.2** as a delta.

**Fixes**

- **Portable mode now works on the packaged downloads** — discovery resolves the data root to the program folder (beside `Genie.exe` / `Update.exe`), not the internal `current/` folder the app launches from, so a portable copy is truly self-contained and survives updates. A freshly-extracted portable build is recognized even before its first `Config` folder exists ([#38](https://github.com/GenieClient/Genie5/issues/38)).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.4.2)

## v5.0.0-alpha.4.1 — portable-first storage

A focused point release that fixes where Genie keeps its data. Previously a portable copy could still read and write the per-user OS folder (`%APPDATA%` on Windows) instead of its own; now **data beside the executable always wins**, so a portable unzip is truly self-contained.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.4.1** as a delta.

**Fixes**

- **Portable-first data discovery** — a copy of Genie with its data folder beside the `.exe` now runs fully local; nothing is read from or written to the per-user OS folder. The old build only did this when a `genie5.portable` marker file was present, which the release zip wasn't reliably shipping — so portable installs leaked into `%APPDATA%` ([#38](https://github.com/GenieClient/Genie5/issues/38), [#74](https://github.com/GenieClient/Genie5/issues/74)).
- **First-run location prompt** — a fresh install with no data in either place now asks where to keep it (portable, beside the exe — the default; or your user folder). The choice persists and is never asked again. See [Application Folders](Application-Folders).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.4.1)

## v5.0.0-alpha.4 — JavaScript scripting, typed login & dev tools

The biggest alpha to walk the lands of Elanthia yet: `.js` scripting, connect-by-typing, and two new tools for the tinkering adventurer — plus a satchel of scripting and mapper fixes to ease the road ahead.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.4** as a delta.

**Scripting**

- **JavaScript (`.js`) array scripts** — run `.js` scripts alongside `.cmd`, on a pure-C# [Jint](https://github.com/sebastienros/jint) engine (no native deps; identical on Windows/macOS/Linux). A `genie.*` API covers `put`/`send`, `waitFor`/`waitForRe`/`matchWait`, `pause`, timers, and session/script vars — straight-line procedural code with real blocking calls. Memory + runaway-loop guards included ([#21](https://github.com/GenieClient/Genie5/issues/21)).
- **`#connect` / `#reconnect` / `#lichconnect`** — log in by typing or from a script (Genie 4 parity): saved-profile, explicit, and reconnect-last forms, plus the Lich variant; passwords masked in history ([#46](https://github.com/GenieClient/Genie5/issues/46)).
- **New reserved variables** — `$gamehost` / `$gameport` (resolved game endpoint), `$roomnote`; `$zoneid` now reads `0` off-map ([#45](https://github.com/GenieClient/Genie5/issues/45)).

**Tools**

- **Analyst Capture** — a redacted, recipe-driven session capture (raw XML + parsed streams + a meta sidecar) for parser/analysis work; other players' speech is stripped by default.
- **Performance overlay** — live per-stage pipeline timing (Parse / Scripts / JavaScript / Triggers / Highlights / …) plus a running-`.js` list, behind the Performance menu.

**Fixes**

- **`#goto` no longer floods the game** — it waits for a confirmed room change between moves instead of overrunning the typeahead buffer ([#69](https://github.com/GenieClient/Genie5/issues/69)).
- **Mapper** — auto-hiding Details flyout; the Mapper floats by default; a new top-level **Maps** menu.
- **Docking** — blank tool panels after a close/reopen are fixed.
- **Connect errors** are now immediate and specific (bad password, character already in game) instead of a ~50-second retry loop.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.4)

## v5.0.0-alpha.3.6 — scripting, docking & UI polish

A quality-of-life batch that closes a run of community-reported issues, plus mapper-aware scripting.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **3.6** as a small delta. See [Keeping Up to Date](Updates).

**Scripting**

- **Mapper variables for scripts** — `$roomid`, `$zoneid`, and `$zonename` now track the mapper's current location (Genie 4 parity), so scripts can branch on where you are. Thanks to **@dylb0t** ([#67](https://github.com/GenieClient/Genie5/pull/67), [#45](https://github.com/GenieClient/Genie5/issues/45)).
- **Room/zone tags + `#goto @tag`** — tag rooms or zones from the mapper, then route to the nearest room carrying a tag.
- **Paused/delayed scripts resume reliably** — fixed a stall where `pause` / `delay` could hang a script ([#61](https://github.com/GenieClient/Genie5/issues/61)).
- **`#edit <script>`** opens the script in your editor instead of running it ([#63](https://github.com/GenieClient/Genie5/issues/63)).

**Interface**

- **Character-Account identity** in the title bar and profile picker, e.g. `Renucci-MONIL` ([#4](https://github.com/GenieClient/Genie5/issues/4)).
- **Browser-style selection** — click-drag to select across multiple lines in the Game window and copy ([#34](https://github.com/GenieClient/Genie5/issues/34)).
- **Docking** — draggable centre splitters; a panel dragged to a new spot keeps that spot after floating or closing ([#35](https://github.com/GenieClient/Genie5/issues/35)).
- **Windowed (MDI) document mode** with Genie 4-style window decorations ([#52](https://github.com/GenieClient/Genie5/issues/52)).
- **Config** — per-profile settings with a global fallback ([#60](https://github.com/GenieClient/Genie5/issues/60)); Room-panel fields wrap; script output is classified as Script Lines.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.3.6)

## v5.0.0-alpha.3.4 — connection, mapper & update-cycle test

A connection/mapper/quality-of-life batch, and the build we're using to exercise the **in-app update cycle** end-to-end.

> **📡 You're on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so Genie now defaults its Core updater to the **beta** channel. That's what lets **Help → Check for Updates** see new alpha builds. On **alpha.3.2**? Open the Updates dialog and you should be offered **3.4** as a small delta — install it and the app restarts on the new version. (Switching to **stable** shows "up to date" until 5.0.0 ships — stay on **beta** to ride the test releases.) See [Keeping Up to Date](Updates).

**Connecting**

- **Connection-mode dropdown** in the Connect dialog — **Direct (SGE login)** or **Lich proxy (local)**; the dialog shows the right fields for each, and the choice saves with the profile.
- **Command-line startup** — launch pre-pointed at a connection, no dialog: `Genie.exe --profile=<name>`, or `Genie.exe --host=127.0.0.1 --port=8000` to attach to a local Lich proxy. Handy for headless-Lich setups and external launchers.
- **Per-profile data folder** — optionally keep a character's data (Config, Scripts, Maps, Plugins, Logs) in a folder you choose, e.g. a synced drive or USB stick. Blank = the default location.

**Mapper & quality of life**

- **`#goto` / `#go2`** — the typed/scripted equivalent of clicking a room: an attended, roundtime-gated walk by room id, note label, or title. Esc / any typed command / a disconnect interrupts it.
- **Numpad movement macros** seeded on a profile's first run (NumLock on): `8/2/4/6` = n/s/w/e, `7/9/1/3` = diagonals, `5` = out, `0` = down. Edit or remove any in the Macros panel.
- **Open Scripts Folder** now opens exactly the folder the script engine loads from.

**Policy posture (clarified)** — the policy docs now centre on DR's [Scripting Policy](https://elanthipedia.play.net/Policy:Scripting_policy): the line is being **responsive to the game**, not keeping the window focused, and it's the player's call. The auto-walk **idle pause** is now **optional and off by default**.

**Secure SGE auth (TLS)** — the Direct login handshake can now connect over TLS (port 7910) with a pinned server certificate, instead of the plaintext port. Plaintext remains available as a fallback.

**Fix** — recovered a class of silently-dropped game text when the server merges a response onto a held-item update.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.3.4)

## v5.0.0-alpha.3.2 — dock layout restore

- **Dock layout close→reopen restore** — closing and reopening a panel now restores it to its prior place in the docked tree instead of dropping it to a default spot.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.3.2)

## v5.0.0-alpha.3.1 — cross-platform companion (Linux + macOS)

The headline: **Genie 5 now has native downloads for all three platforms.** This is the cross-platform companion to alpha.3 — the **same codebase**, plus the Linux and macOS binaries that weren't ready when alpha.3 first shipped. It adds *platforms*, not features.

**First-ever native Genie client on Linux and macOS:**

- 🐧 **Linux x64** — `Genie5.AppImage`, a single-file executable for Ubuntu / Fedora / Debian / Arch / etc.
- 🍎 **macOS Apple Silicon (M1+)** — `Genie5-osx-Setup.pkg` installer, or `Genie5-osx-Portable.zip`.
- 🍎 **macOS Intel (pre-2020)** — `Genie5-osx-x64-Setup.pkg` / `Genie5-osx-x64-Portable.zip`.

On each of these platforms the **in-app updater** handles subsequent releases, just like Windows. Pick your download in the [Installation](Installation#download-a-pre-built-build-recommended) tables.

**Already on Windows alpha.3?** This is offered to you as a tiny delta through the in-app updater — nothing changes behaviourally.

**Read before you run:**

- **macOS** — unsigned, so Gatekeeper blocks the first launch. Right-click → **Open**, or `xattr -d com.apple.quarantine <path>`. ([details](Installation#macos--gatekeeper))
- **Linux** — `chmod +x Genie5.AppImage` first; install FUSE (`sudo apt install libfuse2`) if you see a FUSE error; minimal distros may need `fontconfig`. ([details](Installation#-linux))
- **Windows** — unchanged from alpha.3; SmartScreen still warns until builds are signed (code-signing is planned for an upcoming release).

> ⚠️ Linux and macOS are **brand-new, alpha-tier platforms** here. CI builds them cleanly, but no live-app smoke test has happened on either OS yet. First-tester reports — what works, what's broken, what renders with a weird font — are very welcome: [file an issue](https://github.com/GenieClient/Genie5/issues/new) or post in [Discord](https://discord.gg/MtmzE2w).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.3.1)

## v5.0.0-alpha.3 — Integrated updater

The release that made Genie 5 **self-updating** — no more downloading a fresh zip every version.

- **Integrated updater** — one **Help → Check for Updates** dialog with three channels: **Core** (the app, via Velopack binary-diff updates that install and restart from inside the app), **Maps** (zone XML from the community repo), and **Plugins** (DLLs from configured feeds, with a new `#plugin` command to inspect / install / remove). The Help menu shows a badge when an update is available. It's an in-process system — distinct from Genie 4's separate `Lamp.exe`. See [Keeping Up to Date](Updates).
- **Windows installer** — `Genie5-win-Setup.exe` registers the app for auto-updates; from an alpha.3 install onward, new releases arrive in-app.
- **Code-signing pipeline (in progress)** — a tag-triggered workflow that submits the Windows build to the [SignPath Foundation](https://signpath.org/) for approval and signing. This build is still unsigned; code-signing is expected to land in an upcoming release.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.3)

## Earlier milestones

These predate the current download/updater setup but mark where major subsystems landed:

- **alpha.2** — **Plugin system** shipped: load plugins from `Plugins/` with no rebuild, per-plugin isolation, the `#plugin` command, and the Experience-tracker example. See [Plugins](Plugins).
- **alpha.1** — first public alpha: SGE + Lich + replay connections, the StormFront XML parser, the GameState engine, the full Genie 4 `.cmd` [script engine](Scripting) and all the [rule engines](Configuration), the [mapper](Mapper) with click-to-walk, per-character encrypted [profiles](Connecting), and dockable [panels](The-Interface).

## Roadmap

For what's planned versus shipped, see the [project roadmap](https://github.com/GenieClient/Genie5/blob/main/docs/ROADMAP.md). Wiki pages flag still-unshipped features with 🚧.

## Related

- [Installation](Installation) — pick and install the right download.
- [Keeping Up to Date](Updates) — the in-app updater.
- [Troubleshooting & FAQ](Troubleshooting) — first-launch and platform gotchas.
