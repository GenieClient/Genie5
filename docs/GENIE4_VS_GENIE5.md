# Genie 4 vs Genie 5 — Comprehensive Side-by-Side Comparison (V3)

**Prepared:** 2026-05-26 · **Re-baselined:** 2026-08-02 (beta.3) · 2026-11-22 (5.0.0)
**Genie 5 version:** v5.0.0 "True Colours" — the first stable release (published 2026-11-22)
**Genie 4 reference:** local clone at `_refs/Genie4/` (143 source files, WinForms + .NET 6, GenieClient/Genie4 upstream)
**Purpose:** Track feature parity with Genie 4 and list the gaps that remain after the first stable release. (V1 audited parity *before the alpha shipped*; V2 re-baselined to beta.3. See the change log at the bottom.)

> **What changed in V3 (2026-11-22):** the project moved from **beta.3** to **5.0.0 stable**. This pass retitles to 5.0.0, re-checks every row that still showed ⚠️ / 🗓 / ❌ against 5.0.0 source, adds the windows, menus and engines that shipped between beta.4 and 5.0.0, and replaces the "road to v1.0" wording with the horizons in [ROADMAP.md](ROADMAP.md).

---

## Executive Summary

Genie 5 at 5.0.0 is at or beyond Genie 4 across almost the entire feature surface. Since the beta.3 baseline the largest parity gaps have closed: **server-driven dialog windows** render as ordinary panels (#156), **Windowed Mode (MDI)** and the Genie 4 workspace layouts ship, the **Presets tab** colours what it names (#406), `#goto` hands the walk to the community **automapper.cmd** as Genie 4 did (#226), Genie 4's embedded `<% %>` JavaScript blocks run (#322), and **self-update is verified on Windows, macOS and Linux** (#27).

What is left is small:
- **Parity gaps:** Genie 4's evaluated `e/…/` triggers, the command-bar `class:` modifier, class names on aliases / macros / names, inline rendering of server-sent `<image>` tags, a system-message Log panel, profile notes, and Genie 4's keep-alive / idle-quit timers (Genie 5 has one dead-link watchdog instead).
- **Roadmap:** plugin hash verification and signing, the plugin marketplace, screen-reader support, the script debugger, AI advisor mode, cloud sync.

**Genie 5 also adds capability Genie 4 never had**: cross-platform (Win/Mac/Linux), AES-256-GCM password encryption, per-character rules layered over a shared global set, Session Recorder, a built-in script editor and `#scriptcheck`, `#shunt`, a Healing window for Empaths, Objects / Mobs / Players windows, offline neural text to speech, an in-app updater with stable and beta channels, EV-signed releases, a map editor, and a Genie 4 settings / layout importer.

### Resolved since V1 (alpha.8 → beta.3)

Everything V1 tagged 🔧 FIX BEFORE ALPHA is done. The table was verified against beta.3 source; the two `MainWindow.axaml` ranges were refreshed at 5.0.0, the other `file:line` cites are as of beta.3:

| V1 blocker / gap | V1 status | beta.3 reality | Evidence |
|---|---|---|---|
| AutoMapper auto-walk (the named alpha blocker) | 🔧 FIX BEFORE ALPHA | ✅ Engine-driven step walker with Esc / off-plan / disconnect cancel | `AutoWalkService.cs` (`AutoWalkSession` over `AutoMapperEngine.FindPath`) |
| Help menu + community links | 🔧 FIX BEFORE ALPHA | ✅ Full Help menu (Latest Release, Changelog, Discord, GitHub, Wiki, Play.net, Elanthipedia, Lich Discord, About) | `MainWindow.axaml:777-877` |
| `Character-Account` display format | 🔧 FIX BEFORE ALPHA | ✅ Shipped | `CharacterIdentity.cs:23`; picker label `ConnectDialog.axaml:25` |
| In-app DR policy summary | 🔧 FIX BEFORE ALPHA | ✅ Help menu carries the policy/link surface | `MainWindow.axaml:777-877` |
| JavaScript `.js` scripts (Jint) | ❌ Missing | ✅ Threaded Jint runtime + `js` / `jscall` / `include <file>.js` | `ScriptEngine.cs:1976`; `ScriptInstance.cs:22` |
| Find in buffer (Ctrl+F) | ❌ Missing | ✅ Find bar on the focused stream | `MainWindow.axaml.cs:761` |
| Layout save / load (workspace presets) | 🗓 BETA OK | ✅ Save/Load Layout, global + per-profile scopes | `SaveLayoutDialog.axaml`; `MainWindowViewModel.cs:143` |
| Familiar / Death / Active Spells / Conversation streams | 🗓 BETA OK | ✅ Registered dock tools | `GenieDockFactory.cs:184`; `ActiveSpellsViewModel.cs` |
| Auto-launch Lich on connect | 🗓 BETA OK | ✅ `#lc` / `#lconnect` / `#lichconnect` auto-launch + attach | `LichLauncher.cs` (`EnsureRunningAsync`) |
| Dedicated Scripts menu (Manager, List/Pause/Resume/Abort All, Trace All, Update Scripts) | 🗓 BETA OK | ✅ Full top-level Scripts menu + Scripts updater | `MainWindow.axaml:658-715`; `ScriptsUpdater.cs` |
| `Reconnect` config key (was present-but-unwired) | Low-risk | ✅ Now **wired** as an attended-session reconnect — reconnects only a session the user was actively driving | `MainWindowViewModel.cs:4798-4933` |

**Every V1 menu-parity item has now shipped.** The last one — the **"Open Log In Editor"** menu item — landed on top of Auto Log (shipped alpha.8): File ▸ Open Log In Editor opens the live session's log, or the most recent `*.log` when idle, in the configured editor (`OpenLogInEditorCommand` → `OpenLogInEditor`, reusing the `LaunchExternalEditor` ladder).

### Resolved since V2 (beta.4 → 5.0.0)

| V2 gap | V2 status | 5.0.0 reality | Shipped in |
|---|---|---|---|
| Server-driven dialog windows (bank, store, profile, feats, …) | not listed (the largest unlisted gap) | ✅ Generic renderer: each dialog is a dockable panel built from the controls the server sent, with a settings page and placement hints (#156) | beta.8 capture, beta.9 renderer, beta.9.6 |
| MDI / windowed workspace | not listed | ✅ Layout ▸ Windowed Mode (MDI), Genie 4 `.layout` import (#319), "Heirloom" and two windowed built-in layouts (#425) | beta.9.5 – 5.0.0 |
| Presets that were saved but never used | ✅ (over-credited) | ✅ Presets colour typed and script commands, script output, vitals bars, the map, the cast bar and window chrome (#406) | 5.0.0 |
| Specialized walk routines (sigil / caravan / ice road …) | 🗓 | ✅ `#goto` and click-to-walk start the community automapper.cmd when it is installed, as Genie 4 did (#226) | beta.4.g |
| Mute Sounds menu item | 🗓 "No audio yet" (stale) | ✅ File ▸ Mute Sounds (`MainWindow.axaml:76`) | before 5.0.0 |
| Icon Bar / Health Bar fixed in place | ⚠️ | ✅ Dock Top / Dock Bottom for the Icon, Health and Script bars, carried on saved layouts (#349, #357) | beta.9.6 |
| Roundtime bar | badge only | ✅ Layout ▸ Roundtime ▸ Countdown Bar (Genie 4) (#404) | 5.0.0 |
| macOS / Linux self-update | 🎯 roadmap | ✅ Verified end-to-end on all three platforms (#27) | beta.9.9 |
| Embedded `<% … %>` JavaScript in `.cmd` scripts | not listed | ✅ (#322) | beta.8.3 |
| `{display:command}` links, `@` caret and `\x` command-bar directives | not listed | ✅ (#362, #348) | beta.9.6 |
| `#img` / `#image` pictures in the game window | ❌ | ✅ (#361) — server-sent `<image>` tags are a separate, still-open row | beta.9.7 |
| Mono Font setting | missing on Linux | ✅ Edit ▸ Display Settings ▸ Mono font + size, with font fallback lists (#395) | 5.0.0 |

---

## Methodology

**Sources consulted:**
1. **Genie 5 source tree** — `src/` (Core + App projects)
2. **Genie 5 documentation** — README, CONTRIBUTING, `docs/`, RELEASE_NOTES, commit history
3. **Internal development notes** — design backlog, policy compliance review, terminology, milestone checkpoints (not in this repo)
4. **Empirical findings** — recorded live-DR session captures, parser diff reports, verb-inventory experiments (not in this repo)
5. **Genie 4 source tree** — full local clone of the [GenieClient](https://github.com/GenieClient) org repos (~143 .cs files in the main client)

**Limitations:**
- The Genie 4 inventory was assembled by reading source; some plugin-API specifics may be incomplete.
- Genie 4 config keys were enumerated from `Lists/Config.cs`; a few edge-case keys may not be covered.
- **V2.2 verification scope:** as of the V2.2 pass (2026-08-02), **every** ⚠️/🗓/❌ row was re-verified against beta.3 source (four parallel verification sweeps — config keys, rule/script engine, UI/menu/mapper, audio/images/compliance).
- **V3 verification scope (2026-11-22):** every row that still showed ⚠️/🗓/❌ after V2.2 was re-checked against 5.0.0 source, and the menu tables were rebuilt from `MainWindow.axaml`. Rows that were already ✅ at beta.3 were **not** re-walked, and their `file:line` citations are as of beta.3 — the files have moved since, so treat an old line number as a pointer to the file, not the line. Features added between beta.4 and 5.0.0 are taken from [RELEASE_NOTES.md](../RELEASE_NOTES.md).

**Status legend:**
- ✅ **Shipped** — Genie 5 has it at parity
- 🆕 **Better / addition** — Genie 5 has it AND improves on Genie 4, or Genie 4 lacked it
- ⚠️ **Partial** — Genie 5 has some of it, or does it differently
- 🗓 **Deferred** — a known gap with no release assigned yet
- 🎯 **Roadmap** — on [ROADMAP.md](ROADMAP.md) (the horizon is named in the row)
- ❌ **Missing** — not present and no active plan
- 🛑 **Won't ship** — Genie 4 has it; Genie 5 won't ship it for DR-policy compliance

---

## 1. Menus

### File menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Connect... | ✅ | ✅ | ✅ | Genie 5 unifies "Connect" + "Connect Using Profile" into one dialog with a profile picker |
| Connect Using Profile... | ✅ separate | ✅ merged | ✅ | Merged into Connect dialog |
| Disconnect | ✅ | ✅ | ✅ | |
| Open Directory... → submenu | ✅ submenu | ✅ submenu | ✅ | Data Folder / Config / Logs / Maps / Scripts / Plugins (Config entry is profile-aware, resolved at click time) |
| Auto Log (toggle) | ✅ | ✅ | ✅ | File ▸ Auto Log checkbox over `autolog`, applied live mid-session |
| Open Log In Editor | ✅ | ✅ | ✅ | File ▸ Open Log In Editor — opens the live (or most recent) Auto Log in the configured editor via the shared `LaunchExternalEditor` ladder |
| Auto Reconnect | ✅ default ON | ✅ attended-session form | 🆕 | Config-driven (`#config reconnect`); reconnects only a session the user was actively driving (requires user input since connect), on a bounded retry ladder |
| Classic Connect Window (toggle) | ✅ | n/a | ✅ | We don't have a legacy dialog to fall back to; the `classicconnect` key is accepted so imported settings load cleanly |
| Ignores/Gags Enabled (master toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Gags | ✅ | Engine gates via an Enabled flag (rules stay loaded/editable while off); live-synced with `#config gags off` |
| Triggers Enabled (master toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Triggers | ✅ | Same Enabled-flag gating; live-synced with `#config triggers off` |
| Highlights / Substitutes / Aliases master toggles | ❌ | ✅ File ▸ Master Toggles | 🆕 | Genie 5's Master Toggles set extends beyond G4's (Highlights / Triggers / Substitutes / Gags / Aliases / Images), all backed by same-named settings.cfg keys |
| Plugins Enabled (master toggle) | ✅ | ✅ per-plugin enable/disable (Plugins menu + `#plugin`) | 🆕 | Toggled individually; no single master switch |
| AutoMapper Enabled (master toggle) | ✅ | ✅ "Enable AutoMapper" (`MapperSettingsDialog`) + `#config automapper` | ✅ | Off stops room tracking and `#goto` / `#mapper` reply "Mapper is currently disabled", as in Genie 4 (#401). Record is separate: `#config automapperrecord`, the ⏺ Record button, `#mapper record` |
| Images Enabled (toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Images | ✅ | Rides `showimages`; clears/re-fetches Portrait art live. Inline rendering of server `<image>` tags is still open (§10) |
| Mute Sounds (toggle) | ✅ | ✅ File ▸ Mute Sounds | ✅ | Flips the `muted` config (`MainWindow.axaml:76`) |
| Show Raw Data (toggle) | ✅ | ✅ Window ▸ Raw XML | ✅ | Dockable read-only live view of the raw server stream, hidden by default; tooltip notes it covers G4's Debug window |
| Update Maps from Official Repo... | n/a | 🆕 Maps ▸ Update from Official Repo | 🆕 | Pulls from github.com/GenieClient/Maps; moved from File to the top-level Maps menu |
| Open Maps Folder | ✅ via Open Directory | ✅ Maps menu + Open Directory | 🆕 | |
| Change Maps Directory... | n/a | 🆕 Maps menu | 🆕 | Genie 5 addition for git-clone workflow |
| Import from Genie 4... | n/a | 🆕 | 🆕 | Migrates rule files, names, presets, settings and saved `.layout` files (as windowed-mode layouts) with Global/per-character routing, and reports each rule it could not bring over |
| Record Session (raw XML, toggle) | n/a | 🆕 | 🆕 | Captures raw XML to `Logs/raw_session_*.xml` |
| Open Recordings Folder | n/a | 🆕 | 🆕 | Pair with Record Session |
| Performance Test Parse (dev) | ✅ | partial via Console | ✅ | Genie 5's TestHarness REPLAY mode covers this |
| Exit | ✅ | ✅ | ✅ | |

### Edit menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Paste Multi Line | ✅ | ✅ | ✅ | Edit ▸ Paste Multi Line splits clipboard text on the separator char |
| Configuration... | ✅ tabbed dialog | ✅ tabbed dialog | ✅ | Every list panel has a live Find box (beta.8.2); the window opens at once and fills tabs lazily (#402). Adds Shunts, Text-to-Speech and Server Dialogs pages |
| Update Images | ✅ | n/a | 🗓 | Portrait art is fetched on demand instead; no bulk "update" command |
| Display Settings... | n/a | 🆕 | 🆕 | Font family/size, mono font (Genie 4's Mono Font) + size, game colours, three echo colours (the `inputuser` / `inputother` / `scriptecho` presets), editor path. The strip-position choices (hands / roundtime / script bar) live in the Layout menu, not here |
| Theme (submenu + editor) | n/a | 🆕 | 🆕 | Built-in and user themes; a theme writes its colours into the presets |
| Profile → Load / Save / Include Password | ✅ | ✅ merged into Connect dialog | ✅ | Genie 5 always encrypts saved passwords (AES-GCM) — no include toggle |

### Window menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Show/hide each window | ✅ | ✅ | 🆕 | Genie 4's set plus many more — the full list is in §5 (`MainWindow.axaml:368-600`) |
| Plugin Windows / Script Windows / Server Dialogs submenus | n/a | ✅ | 🆕 | Windows opened by plugins, by scripts (`#echo >name`) and by the server are listed apart |
| Game Window → per-tag toggle (Game / Echo / Script) | n/a | ✅ | 🆕 | Genie 5 addition |
| Raw XML | ✅ (Debug) | ✅ | ✅ | Read-only live server stream, hidden by default |
| Find (Ctrl+F) | ✅ Ctrl+F | ✅ | ✅ | Find bar on the focused stream — **shipped since V1** |
| Right-click window menu (Clear, Time Stamp, Name List Only, Close Window) | ✅ | ✅ | ✅ | Works in docked and windowed mode (#403) |

The hands-strip, roundtime, bar-position and Reset Layout items that V2 listed here live under **Layout**; Float Mapper Window lives under **Maps**.

### Layout menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Load Layout... / Save Layout As... | ✅ | ✅ | ✅ | **Shipped since V1** — global + per-profile scopes (`SaveLayoutDialog.axaml`) |
| Manage Layouts... | n/a | 🆕 | 🆕 | Rename / delete saved layouts |
| Built-in layouts | ✅ Basic Layout | 🆕 Strongbox, Shadowveil, Heirloom (the Genie 4 workspace), Windowed Genie 4 Retro, Windowed Genie 5 Starter | 🆕 | Under Load Layout (#425) |
| Load Default Layout | ✅ | ✅ Reset to Default Layout / Reset Layout | ✅ | Equivalent functionality |
| Save Default Layout / Save Sized Default | ✅ | ⚠️ via Save Layout As | ⚠️ | Named layouts cover it; the specific G4 "default / sized-default" verbs aren't 1:1 |
| Windowed Mode (MDI) | ✅ (G4's only mode) | ✅ Layout ▸ Windowed Mode (MDI) | ✅ | Free-floating child windows as in Genie 4; docked mode stays the default. Imported Genie 4 `.layout` files arrive as windowed layouts (#319) |
| Icon Bar | ✅ submenu | ✅ Icon Bar toggle + Icon Bar Position (Top / Bottom) | ✅ | Text-chip strip: posture chip (dead > standing > kneeling > sitting > prone) + STUNNED / BLEEDING / POISONED / DISEASED / HIDDEN / INVISIBLE / WEBBED / JOINED, fed by IndicatorEvent, dimmed while disconnected. Poison/disease chips are new over G4's six slots (#349) |
| Script Bar | ✅ Dock Top / Dock Bottom | ✅ Script Bar toggle + Script Bar Position (Top / Bottom) | ✅ | Auto-hides when empty. G4 defaults to Top; Genie 5 keeps Bottom as the default and offers Top (#357) |
| Health Bar | ✅ | ✅ Status Bar toggle + Health Bar Position (Top / Bottom) | ✅ | (#349) |
| Hands Strip toggle + position, Enhanced Hands Strip, Rested EXP Bar | n/a | ✅ | 🆕 | Genie 5 additions |
| Roundtime (Command Bar / Hands Strip; Badge / Countdown Badge / Countdown Bar; Keep Visible) | ✅ bar | ✅ | 🆕 | Countdown Bar draws Genie 4's RT bar (#404) |
| Magic Panels (toggle) | ✅ | ✅ | ✅ | G4 SetMagicPanels parity: mana bar / cast bar / spell labels show-hide with column reflow |
| Combined Spell Timer | ✅ | ✅ | ✅ | Spell box doubles as the cast bar, `(N) Spell (M)` (#420) |
| Type-Ahead Counter, Window Banners, Zone / Room ID, Zone as Number, Guild in Title Bar, Disconnect Popup | n/a | 🆕 | 🆕 | Genie 5 toggles |
| Align Input to Game Window | ✅ | ✅ | ✅ | Command bar's side margins track the Game window's dock extent (full-width fallback when floated) |
| Always On Top | ✅ | ✅ | ✅ | Layout ▸ Always on Top |

### Scripts menu

A dedicated top-level **Scripts** menu (`MainWindow.axaml:658-715`) mirrors Genie 4's.

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| **Dedicated Scripts menu** | ✅ Script menu | ✅ Scripts menu | ✅ | **Shipped since V1** |
| Script Explorer / Manager | ✅ tree browser | ✅ Script Manager panel | ✅ | Dockable Script Manager (browse / run / edit / pause / stop / reload / vars / trace); toggles from the Scripts menu; also opens via `#script explorer` |
| Update Scripts | ✅ LAMP | ✅ Updates dialog ▸ Scripts tab | 🆕 | `ScriptsUpdater.cs` (git-pull semantics; new/changed files pulled, sha-matched skipped, local-only files untouched) + Check-on-Startup / Auto-Apply toggles. You can choose which script files to update (beta.9.10), and repo scripts get their own directory (#221) |
| Show / List Running Scripts | ✅ | ✅ List Running Scripts + Script Bar | ✅ | `#scripts`; Script Bar also always-visible when scripts run |
| Trace All Scripts (debug) | ✅ | ✅ Trace All submenu (Off / 1 / 3 / 5 / 10) | ✅ | `#traceall N`; plus per-chip debug levels on the Script Bar |
| Pause All / Resume All Scripts | ✅ | ✅ Pause All / Resume All | ✅ | `#pauseall` / `#resumeall` (preserves state via `UserPaused`) — **shipped since V1** (the "no pause/resume primitive" note was wrong) |
| Abort All Scripts | ✅ | ✅ Abort All Scripts | ✅ | `#stopall` |
| External Editor (change / OS default) | via `editor` cfg | 🆕 External Editor submenu | 🆕 | Shows the resolved launch-ladder rung; Change… writes the override, Use OS Default clears it. Since beta.9.7 `#edit` opens the built-in editor unless `#config externaleditor on` |
| Open Scripts Folder | ✅ | ✅ | ✅ | Per-character Scripts directory |

### AutoMapper menu (Genie 5: **Maps**)

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Show Window | ✅ | ✅ Maps ▸ Show Mapper | ✅ | Plus Float Mapper Window and Show Map Legend |
| Update Maps | ✅ LAMP | ✅ Maps ▸ Update from Official Repo | 🆕 | Opt-in Festivals and Quests (spoilers) folders. Gives no notice of success or failure yet (#423) |
| Repair Maps (full re-download) | n/a | 🆕 | 🆕 | (#352) |
| Script Settings | ✅ FormMapperSettings | ✅ AutoMapper Settings | ✅ | Genie 5 has both walkers: the built-in engine walker (`AutoWalkService.cs`) and, when automapper.cmd is installed, the community script as in Genie 4 (#226; `#config automapperscript false` forces the built-in one) |
| Cross-Zone Connections... | n/a (script data) | 🆕 | 🆕 | Editor for `ZoneConnections.xml`; zones and rooms are picked from lists |

### Analyst and Alterations menus (Genie 5 only)

| Menu | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Analyst | n/a | 🆕 | 🆕 | Capture recipes and manual captures for parser / protocol reports |
| Alterations | n/a (plugin) | 🆕 | 🆕 | Alteration designer and library, with Genie 4 import / export |

### Plugins menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| **Entire menu** | ✅ | ✅ Plugins menu (Open Folder, Reload, Load ▶, Enable/Disable ▶, Unload ▶) + `#plugin` | ✅ | Marketplace is on the roadmap (Horizon 3) |

### Help menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Check For Updates / AutoUpdate / Force Update | ✅ | ✅ Help → Check for Updates (dialog + ● badge + startup background check) | ✅ | In-app updater (Velopack) with a stable / beta Channel box; the dialog says what changed |
| Update Settings (update policies) | ⚠️ scattered toggles | ✅ Help → Update Settings | 🆕 | Per-kind Check-on-Startup + Auto-Apply menus (Core, Maps, Plugins, Scripts), plus a dismissible status-bar notice strip |
| Load Test Client | ✅ | n/a | 🗓 | The game picker on the Connect dialog covers the Test instance; there is no separate test build of the client to load |
| Latest Release Page / Changelog | ✅ | ✅ | ✅ | In-app Changelog dialog is new |
| Discord / GitHub / Wiki / Play.net / Elanthipedia / Lich Discord links | ✅ | ✅ | ✅ | **Shipped since V1** (`MainWindow.axaml:805-872`) |
| Performance (overlay, counters, regex safety per engine) | n/a | 🆕 | 🆕 | Regex timeout + pre-filter switches for triggers / highlights / substitutes / gags |
| About Genie 5… | n/a | 🆕 | 🆕 | |

### Profile menu (Genie 4 has its own menu)

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Load / Save Profile / Include Password | ✅ separate menu | ✅ folded into Connect dialog | ✅ | No top-level Profile menu; functionality lives in the Connect dialog |

**Menu rollup:**
- Every Genie 4 menu has a Genie 5 counterpart. The only menu items without one are **Update Images**, **Load Test Client** and the "Save Default / Sized Default" layout verbs, and each has a working substitute.
- One behavioural divergence is open: `#config automapper true` also starts Record mode (#401, aimed at 5.0.1).
- Auto Reconnect ships in an attended-session form (reconnects only sessions the user was actively driving; a script's `put quit` now counts as quitting on purpose, #419).

---

## 2. Settings (cfg keys / configuration)

Genie 4 has 60+ config keys in `Lists/Config.cs`. Genie 5 has equivalents for nearly all of them; the importer names each key it does not carry over and why. See [SETTINGS_REFERENCE.md](SETTINGS_REFERENCE.md) for the full Genie 5 list.

### Already-shipped parity

| Key | Genie 4 default | Genie 5 default | Notes |
|---|---|---|---|
| `commandchar` | `#` | `#` | ✅ Match |
| `scriptchar` | `.` | `.` | ✅ Match |
| `separatorchar` | `;` | `;` | ✅ Match |
| `scriptextension` | `cmd` | `cmd` | ✅ Match |
| `prompt` | `> ` | `> ` | ✅ Match |
| `mycommandchar` | `/` | `/` | ✅ Match (input starting with this char is echoed + run through triggers but never sent to the game — G4 `SendText` parity) |
| `spelltimer` | True | True | ✅ Match (cast bar shipped) |
| `autoupdate` | False | False | ✅ Match (both default OFF; startup runs a background **check** only, never a silent apply) |

### Shipped with a different default

| Key | Genie 4 default | Genie 5 default | Why |
|---|---|---|---|
| `showlinks` | False | True | Genie 5 always-on; better UX |
| `keepinputtext` | False | False | Match |
| `weblinksafety` | True | True | Match (confirm-before-open URLs) |

### Config-key parity ledger (backlog "Genie 4 Config-Option parity audit")

Verified against beta.3 source (2026-08-02); the three open rows were re-checked at 5.0.0 (2026-11-22).

| Key | Default | What it does | Status |
|---|---|---|---|
| `abortdupescript` | True | Abort a duplicate same-named running script | ✅ shipped (`GenieConfig.cs:131`; `ScriptEngine.cs:147`) |
| `editor` | `notepad.exe` | External editor path | ✅ shipped as `Display.EditorPath` |
| `maxgosubdepth` | 50 | Script-engine GOSUB recursion limit | ✅ shipped (`GenieConfig.MaxGoSubDepth = 50`) |
| `maxrowbuffer` | 5 | Output buffering line count | ⚠️ superseded by `ScrollbackLines` (default 2000, retention cap — genuinely different semantics; G4's was a WinForms paint knob) |
| `promptbreak` | True | Insert blank line before each `<prompt>` | ✅ shipped (`GameTextViewModel.cs:170`) |
| `promptforce` | True | Force prompt display when server omits | ✅ shipped (`GameTextViewModel.cs:173`) |
| `condensed` | False | Compact display mode | ✅ shipped (`GameTextViewModel.cs:133`) |
| `triggeroninput` | True | Run triggers against user input lines | ✅ shipped |
| `roundtimeoffset` | 0 | Latency-comp adjustment to RT display | ✅ shipped (`GenieConfig.cs:172`; `VitalsViewModel.cs:177`) |
| `weblinksafety` | True | Confirm-before-open on URL clicks | ✅ shipped |
| `monstercountignorelist` | regex | Patterns to exclude from monster count | ✅ shipped (`GenieConfig.cs:85`; Mobs panel + `$monstercount`/`$monsterlist`) |
| `scripttimeout` | 5000 ms | Max runtime per script | ✅ shipped |
| `ignorescriptwarnings` | False | Suppress script-engine warnings | ✅ shipped (`GenieCore.cs:500`) |
| `parsegameonly` | False | Skip parser on user input | ✅ shipped (`GenieCore.cs:1008`) |
| `ignoreclosealert` | False | Suppress confirm-on-close | ✅ shipped (`MainWindow.axaml.cs:481`) |
| `sizeinputtogame` | False | Align input bar to game width | ✅ shipped (Layout ▸ Align Input to Game Window) |
| `connectscript` | empty | Auto-run a named script on connect | ✅ shipped (`GenieCore.cs:1153`; per data-root/profile, not per-character) |
| `connectstring` | `FE:GENIE...` | Client-ID announcement string | ✅ shipped (engine-controlled) |
| `requiresignedplugins` | False | Plugin signature verification | 🎯 Roadmap (not present in `src/`; plugin hash verification is Horizon 1 Track A, aimed at 5.1) |
| `servertimeout` + `servertimeoutcommand` | 180s / fatigue | Keep-alive verb on idle | ⚠️ not carried over — Genie 5 has one idle timer, `activitytimeout` (default 300s, 60–3600, `0` = off): a dead-link watchdog that disconnects after that long with nothing from the server. It never sends a keep-alive verb. The importer says so (`Genie4Importer.cs:801`) |
| `usertimeout` + `usertimeoutcommand` | 300s / quit | User-side idle disconnect verb | ⚠️ not carried over — same note; Genie 5 does not send `quit` for an idle user |
| Per-data-dir overrides (`artdir`, `logdir`, `configdir`, `plugindir`, `mapdir`, `scriptdir`, `sounddir`) | local relative dirs | user-settable keys, validated + resolved via `LocalDirectoryService` (`mapdir`/`plugindir` resolve against the shared root) | ✅ shipped (`GenieConfig.cs:594`) |
| Repository URLs (`scriptrepo`, `maprepo`, `pluginrepo`, `artrepo`) | empty | superseded by `update-feeds.json` | 🆕 maps + plugin + **script** feeds shipped (`ScriptsUpdater.cs`); art not yet |
| Lich integration (`rubypath`, `cmdpath`, `lichpath`, `licharguments`, `lichserver`, `lichport`, `lichstartpause`) | typical | ✅ auto-launch shipped (`LichLauncher.cs`) | ✅ **shipped since V1** — `#lc`/`#lconnect`/`#lichconnect` launch + attach |

**Only three config keys have no direct equivalent:** `servertimeout` and `usertimeout` (replaced by the single `activitytimeout` dead-link watchdog, which neither sends a keep-alive verb nor quits an idle user) and `requiresignedplugins` (plugin verification, aimed at 5.1).

### Reconnect — now implemented (attended-session form)

| Key | Genie 4 default | Genie 5 status | Notes |
|---|---|---|---|
| `reconnect` | True | ✅ Wired as an attended-session reconnect | Reconnects only a session the user was actively driving (requires user input since connect); no longer the V1 present-but-unwired key. |

### Genie 5-only additions

- `frontendid` (`GENIE`/`STORM`) — FE handshake selector (CLI/code-controllable)
- `RoundTimeOnHandsStrip` — RT badge position
- `ShowGameText` / `ShowEchoText` / `ShowScriptText` — per-tag visibility
- `EditorPath` — external editor for `#edit`; `externaleditor` chooses it over the built-in editor
- `activitytimeout` — dead-link watchdog (see the ledger above)
- `automapperscript` — `false` forces the built-in walker over automapper.cmd
- `mapfestivals` / `mapquests` — opt-in map folders (kept in `update-feeds.json`)
- `scriptdebugwindow`, `tracesends`, `lichdebug`, `conndebug` — diagnostics
- `MapBackgroundHex` / `MapTextHex` — removed (#406). The map background and label text are Genie 4's `automapper.panel` preset again; values in an older `display.json` are migrated into it once.

---

## 3. Rule Engines

The largest area of genuine parity. **Genie 5 ships all of Genie 4's rule engines.**

| Engine | Genie 4 | Genie 5 | Class scope | Persistence | Status |
|---|---|---|---|---|---|
| **Aliases** | `Lists/Aliases.cs` + `#alias` | `Aliases/AliasEngine.cs` + `#alias`/`#unalias` | ✅ | `aliases.cfg` (JSON) | ✅ |
| **Triggers** | `Lists/Globals.cs` Triggers | `Triggers/TriggerEngineFinal.cs` + `#trigger`/`#action` | ✅ | `triggers.cfg` (JSON) | ✅ |
| **Highlights** | `Lists/Highlights.cs` | `Highlights/HighlightEngine.cs` + `#highlight` | ✅ | `highlights.cfg` (JSON) | ✅ |
| **Substitutes** | `Lists/Globals.cs` Subs + `#sub` | `Substitutes/SubstituteEngine.cs` + `#substitute`/`#sub`/`#subs` | ✅ | `substitutes.cfg` (JSON) | ✅ |
| **Gags** | `Lists/Globals.cs` Gags + `#gag` | `Gags/GagEngine.cs` + `#gag`/`#ungag` | ✅ | `gags.cfg` (JSON) | ✅ |
| **Macros** | `Lists/Macros.cs` + `#macro` | `Macros/MacroEngine.cs` + `#macro` | ✅ | `macros.cfg` (JSON) | ✅ |
| **Variables** | `Lists/Globals.cs` Variables + `#setvar` | `Variables/VariableEngine.cs` + `#var`/`#tvar` | n/a | `variables.cfg`/`tvars.cfg` | ✅ |
| **Classes** | `Lists/Classes.cs` + `#class` | `Classes/ClassEngine.cs` + `#class` | n/a | `classes.cfg` (JSON) | ✅ |
| **Names** | `Lists/Names.cs` + `#name` | `Highlights/NameHighlightEngine.cs` | ❌ no class scope | `names.cfg` (JSON) | ⚠️ class scope missing |
| **Presets** | `Lists/Globals.cs` Presets + UI | `Presets/PresetEngine.cs` | n/a | `presets.cfg` (JSON) | ✅ every listed preset colours what it names and repaints at once: game text, `inputuser` / `inputother` / `scriptecho`, vitals bars, `automapper.*`, `ui.*` chrome, `castbar`, `roundtime`, `roomname`, `link`, `url` (#406, 5.0.0) |
| **Shunts** | ❌ (requested as Genie 4 #81) | `#shunt` / `#unshunt` + Configuration ▸ Shunts | ✅ | `#shunt add` lines | 🆕 route matching lines to a named window, optionally as a copy (#248) |

All rule files load in two layers — a shared global set with per-character rules over it (#257) — and reload live when edited on disk (#231).

### Rule-engine sub-features

| Sub-feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Highlight sound playback (`SoundFile` on rule) | ✅ | ✅ (`AudioService` → `DefaultHighlights.cs:291`) | ✅ |
| Highlight "match whole line" vs "substring" | ✅ | ✅ | ✅ |
| Highlight foreground + background colors | ✅ | ✅ | ✅ |
| Highlight case-sensitive flag | ✅ | ✅ | ✅ |
| Trigger regex `/pattern/i` syntax | ✅ | ✅ | ✅ |
| Highlight regex capture groups colour only the groups; BeginsWith colours the whole line | ✅ | ✅ (#397, #400) | ✅ |
| Substitute whole-words-only; `$globals` in the replacement | ❌ (Genie 4 #123, #91) | ✅ per-rule (beta.9.6) | 🆕 |
| Trigger `eval` expression triggers | ✅ via `e/` | ❌ no equivalent — a trigger cannot fire on a variable change. (Genie 5's `eval` trigger flag is different: it evaluates `{…}` blocks in the *action*, `TriggerEngineFinal.cs:131`.) Inside a script, `action … when eval` covers the same need | ❌ gap — `e/…/` rules are refused with a reason, on import and on `#trigger` (`CommandEngine.cs:2697`, #353) |
| Trigger fire-on-input (vs server only) | ✅ via `triggeroninput` | ✅ | ✅ |
| Macro keybind: F-keys, Ctrl/Alt/Shift+X | ✅ | ✅ | ✅ |
| Variable types: SaveToFile / Temporary / Reserved | ✅ | ⚠️ via `Scope` enum (Global/Script/Tvar) | ✅ (semantically equivalent) |
| Reserved variables ($health, $mana, $stance, …) | ✅ ~30 vars | ✅ ~40 vars via `ScriptGlobalsSync` | 🆕 |
| Per-rule ClassName for filtering | ✅ | ✅ for Highlights/Triggers/Substitutes/Gags/Shunts/Aliases/Macros; ❌ Names (`NameRule` has no class) | ⚠️ partial |
| Command-bar `class:foo` modifier | ✅ on most | ❌ no `class:` token parsing (`CommandEngine.cs`); class is a positional argument instead | 🗓 |
| .cfg round-trip with class name | ✅ | ⚠️ Highlight/Trigger/Sub/Gag/Shunt serializers write ClassName; **Alias/Macro drop it on save** (`CfgFormat.cs:34,70`) | 🗓 |

**Rule-engine rollup:**
- All core engines ship at Genie 4 parity, and Presets now do everything they did in Genie 4.
- Four confirmed gaps at 5.0.0: evaluated `e/…/` triggers, the command-bar `class:foo` modifier, Alias/Macro `ClassName` `.cfg` persistence (held in memory, dropped on save), and Names class scope.
- On the roadmap (Horizon 2): multi-line regex matching (#22) and ignoring a leading timestamp when matching — both supersets of Genie 4.

---

## 4. Script Engine

### Native `.cmd` script support

Both clients support the Wizard-derived `.cmd` language. **Genie 5 is a faithful port** — same vocabulary, same `$variable` substitution, same `MATCH`/`WAITFOR`/`GOSUB`/`GOTO` flow control.

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Labels (`:label`, `label:`) | ✅ | ✅ | ✅ |
| `GOTO` / `GOSUB` / `RETURN` | ✅ | ✅ | ✅ |
| `MATCH` / `MATCHRE` / `WAITFOR` / `WAITFORRE` | ✅ | ✅ | ✅ |
| `PUT` / `SEND` (send to game) | ✅ | ✅ | ✅ |
| `#put` alias | n/a | ✅ | 🆕 |
| `ECHO` (with optional `>window` `#color`) | ✅ | ✅ | ✅ |
| `PAUSE` / `WAIT` (sleep N seconds) | ✅ | ✅ | ✅ |
| `waitpause` (sleep until current RT expires) | ✅ | ✅ | ✅ |
| `if_*` conditional slots, `IF … THEN … ELSE … ENDIF` | ✅ | ✅ | ✅ |
| `def(name)` expression | ✅ | ✅ | ✅ |
| Variables (`var foo = bar`, `$foo`) | ✅ | ✅ | ✅ |
| `%1 %2 … %0` argument substitution | ✅ | ✅ | ✅ |
| `#var` / `#tvar` (session globals) | ✅ | ✅ | ✅ |
| `EVAL` / `EVALMATH` | ✅ | ✅ native `eval`/`evalmath` (`ScriptEngine.cs:2006`) — not just `def(...)` | ✅ |
| `random` / `counter` | ✅ | ✅ (`ScriptEngine.cs:1892`/`:1928`) | ✅ |
| `INCLUDE <script>` (parse-time inclusion) | ✅ | ✅ | ✅ |
| `EXIT` (stop script) | ✅ | ✅ | ✅ |
| `#stop` / `#stopall` | ✅ | ✅ | ✅ |
| `#scripts` (list running) | ✅ | ✅ | ✅ |
| `#edit` (open in external editor) | ✅ | ✅ | ✅ |
| Tab-complete script names in command bar | ❌ | 🆕 | 🆕 |
| Type-ahead budget management | ✅ | ✅ (`TypeAheadSession`) | ✅ |
| RT-aware command queueing | ✅ | ✅ (`CommandQueue`) | ✅ |
| GOSUB recursion limit | ✅ `maxgosubdepth=50` | ✅ `MaxGoSubDepth=50` | ✅ |
| Script timeout | ✅ `scripttimeout=5000` | ✅ `ScriptTimeout=5000` | ✅ |
| Abort-on-undefined-var | n/a | 🆕 (G4 silently expanded to empty); plus a warning on unexpanded variables in outgoing commands | 🆕 |
| `waiteval` | ✅ waits for a change | ✅ same since 5.0.0 — a condition that is already true no longer falls through (#428); it also follows variables that change while it waits (#332) | ✅ |
| `waitfor` / `matchwait` resume after the whole server read | ✅ | ✅ (#433) | ✅ |
| Embedded `<% … %>` JavaScript blocks in `.cmd` | ✅ | ✅ (#322) | ✅ |
| `%list.length`, `%name(...)` indexing, bare sigil assignment, base-10 `log()` | ✅ | ✅ (beta.9.6 – 9.7 parity batch) | ✅ |
| `$scriptlistactive` / `$scriptlistpaused`, `#script` over a list | ❌ (Genie 4 #47) | ✅ | 🆕 |
| `{display:command}` inline links; `@` caret and `\x` command-bar directives | ✅ | ✅ (#362, #348) | ✅ |
| `#scriptcheck <name>` (validate without running) | ❌ | 🆕 (#239) | 🆕 |
| Built-in script editor (syntax colouring, Ctrl+S / Ctrl+F) | ❌ external only | 🆕 (#243) | 🆕 |
| Game pipeline on its own thread | n/a (not compared) | ✅ a wedged script cannot freeze the client (#251) | ✅ |

### JavaScript `.js` script support — ✅ SHIPPED since V1

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| `.js` array scripts via Jint engine | ✅ | ✅ persistent threaded Jint runtime (`ScriptInstance.cs:22`) | ✅ |
| `js` / `jscall` / `include <file>.js` script commands | ✅ | ✅ (`ScriptEngine.cs:1976`) | ✅ |

Sandboxing: JS runs with the `genie.*` API plus memory/runaway guards; no unrestricted host/CLR access by default.

### Lich .rb script support

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Launch Lich proxy + run `.rb` scripts | ✅ | ✅ via LichProxy mode | ✅ |
| Auto-launch Lich on connect | ✅ | ✅ `LichLauncher.cs` (`#lc`/`#lconnect`/`#lichconnect`) | ✅ **shipped since V1** |

**Script-engine rollup:**
- `.cmd` parity: the community scripts (automapper.cmd, travel.cmd, uber.cmd) run; the fixes between beta.4 and 5.0.0 were timing and edge-case parity found by running them, largely on Linux.
- `.js` (Jint) and Lich auto-launch — both V1 gaps — ship. No known script-engine feature gap remains.
- One behaviour change to know about when moving a Genie 5 beta script forward: `waiteval` now waits for a change, as Genie 4 does (#428).
- Roadmap (Horizon 3): breakpoints and a live variable inspector on top of the built-in editor.

---

## 5. UI Panels / Windows

Genie 4 is WinForms MDI. Genie 5 is Avalonia + Dock.Avalonia, docked by default, with **Windowed Mode (MDI)** for the Genie 4 arrangement. Every panel can float, reopens floating if that is where you left it (#359), and has per-window font, colour, timestamp and log settings.

### Dockable windows

| Window | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| **Game** (main text) | ✅ | ✅ | ✅ | Per-tag visibility filter (Game/Echo/Script) |
| **Vitals** bars | ✅ ComponentBars | ✅ VitalsTool | ✅ | G5 default hidden (Status Bar duplicates it) |
| **Inventory** / **Backpack** | ✅ | ✅ | ✅ | |
| **Mapper** | ✅ MapForm | ✅ MapperTool | ✅ | |
| **Logons / Talk / Whispers / Thoughts / Combat** | ✅ | ✅ | ✅ | Combat active-by-default in the bottom-left tab cluster |
| **Familiar** | ✅ | ✅ | ✅ | **Shipped since V1** (`GenieDockFactory.cs:184`) |
| **Death** | ✅ | ✅ | ✅ | **Shipped since V1** |
| **Active Spells** (`percWindow`) | ✅ | ✅ `ActiveSpellsViewModel` | ✅ | **Shipped since V1** |
| **Conversation** / **Group** | ✅ | ✅ | ✅ | **Shipped since V1**; Group added in beta.9.7 |
| **Assess** / **Atmospherics** / **OOC** / **Item Log** | n/a as dedicated windows | ✅ | 🆕 | Their own streams and windows |
| **Server dialogs** (bank, store, profile, feats, Friends & Enemies, …) | ✅ | ✅ | ✅ | Generic renderer: each dialog is a panel built from the controls the server sent, placed where the game hints or beside a window you name, remembered per character (#156). One piece is left — the aim timer (#436) |
| **Injuries** (self) / another character's injuries | ✅ | ✅ | ✅ | Reads nerve damage and the tail; another character's injuries render as content (#342) |
| **Healing** (Empaths) | ⚠️ Crutch plugin | 🆕 built in | 🆕 | Per-patient body figures, click-to-heal, bulk and partial takes (#263, #375) |
| **Mobs** / **Players** / **Objects** | ⚠️ via scripts | 🆕 | 🆕 | Room contents split into their own windows (#329); Mobs reads structured assess rows |
| **Experience** | ⚠️ EXPTracker plugin | 🆕 built in | 🆕 | Classic EXPTracker sorting, pulse echo, rested EXP (#272) |
| **Analytics** / **Time Tracker** / **Inventory View** | ⚠️ plugins | 🆕 built-in extensions | 🆕 | |
| **Script windows** (`#echo >name`), plugin windows | ✅ | ✅ | ✅ | Listed apart in the Window menu; container contents open in their own window (#336) |
| **Log** | ✅ system messages | ⚠️ dock exists but carries conversation/speech + `#echo >log` | ⚠️ | A dockable Log window ships, but *system diagnostics* still route to the Game window (Game Window ▸ Script Lines), not this panel |
| **Debug** (parser trace) | ✅ | ✅ via Raw XML window | ✅ | Plus `[dbg:N]` script-level traces |
| **Raw** (raw XML inspector) | ✅ | ✅ RawXmlTool | ✅ | Window ▸ Raw XML, hidden by default |
| **Portrait** | ✅ | ✅ | ✅ | Room-art panel takes its G4 name; dock id unchanged so saved layouts restore it |
| **Room** (title/description/exits) | ✅ | ✅ | ⚠️ | Known bug: the Room window ignores its font and colour settings and the `roomdesc` preset (#424, aimed at 5.1) |
| **Hands Strip** | ✅ within icon bar | ✅ separate strip | 🆕 | Dedicated; toggleable position |
| **Script Bar** | ✅ | ✅ | ✅ | Auto-hides when empty; per-chip pause/resume/debug/trace/vars/edit/stop; Top or Bottom position |
| **Script Manager** panel | ✅ Script Explorer | ✅ dockable panel | 🆕 | Browse / run / edit the library + manage running scripts (pause / stop / reload / vars / trace); toggled from the Scripts menu or `#script explorer` |

### Default layout

| Aspect | Genie 4 | Genie 5 |
|---|---|---|
| Layout shape | User-configured MDI; default = Game left, Thoughts / Room / Log down the right | Docked 3-column: Room+Streams left / Game+Mapper center / Backpack right. Genie 4's arrangement is one click away as **Heirloom** (docked) or **Windowed Genie 4 Retro** (MDI) |
| Status bar | Optional | Yes by default (Wrayth-style vitals bars); top or bottom |
| Hands strip | In icon bar | Dedicated strip, default below status bar |

**UI rollup:**
- Server dialog windows — the largest parity gap at beta.3 — shipped in beta.9, and Windowed Mode restores Genie 4's MDI workspace.
- Genie 5 now has more windows than Genie 4, several of them replacing Genie 4 plugins (Healing, Experience, Inventory View).
- Two things are open: the **Log** dock is a conversation/speech stream, so G4's *system-message* Log has no dedicated panel; and the Room window ignores its font / colour settings (#424).

---

## 6. Plugin System

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Plugin host (.NET DLL API) | ✅ `Core/PluginHost.cs` + `Core/LegacyPluginHost.cs` | ✅ `Genie.Plugins.Abstractions` (`IGeniePlugin`/`IPluginHost`/`IGameStateView`) + `PluginManager` | ✅ |
| Plugin DLL loading | ✅ | ✅ collectible `AssemblyLoadContext` (`PluginLoadContext`); `DiscoverAndLoad` from `{AppData}/Genie5/Plugins/`; load / unload / reload | ✅ |
| Plugin signature verification | ✅ `requiresignedplugins` | ❌ plugins load without a signature or hash check | 🎯 Roadmap — hash verification is Horizon 1 Track A, aimed at 5.1; signing follows with the marketplace |
| Plugin manager UI | ✅ `Forms/FormPlugins.cs` | ⚠️ Plugins menu + `#plugin`; no dedicated manager dialog | ⚠️ |
| Plugin `/commands` from scripts, aliases and triggers | ✅ | ✅ (#325, #326) | ✅ |
| Text transform hooks (`OnGameText` / `OnEcho` / `OnInput`) | ✅ | ✅ | ✅ |
| Plugin marketplace | ❌ | 🎯 parked until hash verification lands | 🎯 Roadmap (Horizon 3) |
| Built-in extensions | ❌ | ✅ EXP tracker, Inventory View, Spell Timer, Time Tracker, Circle Calc | 🆕 |

### Genie 4 plugin catalogue

The 21 Genie 4 plugins are audited one by one in [#271](https://github.com/GenieClient/Genie5/issues/271): built in, worth porting, or retired.

| Genie 4 plugin | Genie 5 | Status |
|---|---|---|
| EXPTracker / ExpEcho | Experience window (beta.7) | ✅ built in |
| Crutch | Healing window (#263) | ✅ built in |
| SpellInfo | `/spellinfo` (#267) | ✅ built in |
| Per-window logging | `#windowlog` (#270) | ✅ built in |
| Bank Tracker | waits on the structured bank dialog | 🎯 Roadmap (Horizon 2) |
| BestiaryQuery | original wiki query is dead; needs a new source | 🗓 |
| Combat Tracker | closed not-planned (#265) — nobody asked for it | 🛑 retired |

**Plugin rollup:**
- Plugin host **shipped**: UI-free contract, collectible-ALC loader, Plugins menu + `#plugin`, built-in extensions.
- A plugin runs with your user's rights and is not verified on load — install only plugins you trust until hash verification ships (5.1).
- G4 plugin DLLs still need a recompile against `Genie.Plugins.Abstractions` (WinForms/Windows-only); the interface shape is kept familiar to ease porting. The contract is UI-free, so a plugin whose value is a clickable window needs a built-in-panel decision first.

---

## 7. AutoMapper

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Map data format (zone XML) | ✅ canonical | ✅ identical | ✅ (round-trip via `Genie4MapImporter` / `Genie4MapExporter`) |
| Map rendering (zone canvas) | ✅ | ✅ `MapCanvas` | ✅ |
| Click-to-go on map | ✅ left-click | ✅ right-click context menu | 🆕 |
| **Auto-walk between rooms** | ✅ via `.automapper` script | ✅ automapper.cmd when installed (#226), otherwise the built-in walker (`AutoWalkService.cs`) | ✅ `#goto` and click-to-walk start the script with the planned route; the built-in walker steps through `FindPath` results with Esc / off-plan / disconnect cancel |
| Map fingerprinting (title + exits) | ✅ | ✅ `MapFingerprint.cs` | ✅ |
| Auto-detect zone from current room | ⚠️ via script | ✅ engine | 🆕 |
| Less Obvious Paths display | ✅ | ✅ clickable buttons | ✅ |
| Editable room Notes | ✅ | ✅ inline editor, saves to zone XML | ✅ |
| Stale-zone warning | ❌ | 🆕 "may be stale" badge after 30 days | 🆕 |
| Auto-center on current room | ✅ via script | ✅ engine | ✅ |
| Zone update from official repo | ✅ via LAMP | ✅ via File menu (`MapsUpdater` + `GithubContentsSource`) | 🆕 |
| Multi-zone navigation (cross-zone pathing) | ✅ via script | ✅ `MultiZonePathfinder.cs` + `AutoWalkService.StartCrossZone` + `ZoneConnections.xml` editor | ✅ **shipped since V1** |
| Per-class mapper script | ✅ AutoMapper Script Settings dialog | ✅ automapper.cmd runs as in Genie 4 | ✅ |
| Sigil / search walk / caravan / broom_carpet / iceroadcollect | ✅ via script | ✅ via the same script (#226) — its special-move directives and pacing globals (`$caravan`, `$powerwalk`, …) work. Not implemented in the built-in walker | ✅ |
| `#goto` to the room you are in answers `YOU HAVE ARRIVED!`; `$zoneid` / `$roomid` switch in step at a map boundary | ✅ | ✅ (#427, #426) | ✅ |
| Return portals (Fang Cove) | ✅ via script | ✅ engine routes back to where you entered | ✅ |
| Named-object hidden exits | ✅ | ✅ (beta.8.2) | ✅ |
| Map editing: add room, link rooms, labels, tags, notes | ✅ | ✅ Add Room Here, Link to…, editable labels and tags | ✅ |
| Map legend, spoiler settings, faint other-floor rooms, zone browsing | ❌ | 🆕 (#254) | 🆕 |
| Repair Maps; opt-in Festivals / Quests folders | ❌ | 🆕 (#352) | 🆕 |
| Rooms with day and night descriptions | ✅ | ⚠️ only the first stored description is compared, so such rooms can resolve to the wrong room (#407, aimed at 5.1) | ⚠️ |
| Map visual: zoom, pan | ✅ basic | ✅ mouse-wheel zoom; rooms and labels at their Genie 4 pixels | ✅ |
| Map visual: room color by exit type | ❌ | 🆕 cyan vertical / green special / grey compass | 🆕 |
| Map visual: room labels from Notes | ❌ | 🆕 | 🆕 |
| Float mapper to separate window | ❌ | 🆕 (Dock.Avalonia FloatDockable) | 🆕 |

**AutoMapper rollup:**
- Data + rendering: Genie 5 ahead.
- **Two walkers:** the community automapper.cmd when it is installed — which carries the specialized routines exactly as in Genie 4 — and a built-in engine walker with a cancel-on-input / off-plan / disconnect guard when it is not.
- **Cross-zone pathing ships** (`MultiZonePathfinder` + `ZoneConnections.xml`, with an editor).
- Open: day/night room descriptions (#407), `#config automapper` also starting Record (#401), and no success / failure notice from Update and Repair Maps (#423). The map file format is unchanged; UTF-8 output is an undecided roadmap question.

---

## 8. Logging

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| **Auto Log** (rendered text to disk) | ✅ `autolog` + File menu | ✅ `#config autolog` + File ▸ Auto Log checkbox (live mid-session) | ✅ |
| **Open Log In Editor** menu | ✅ | ✅ | ✅ (opens live/most-recent Auto Log in the configured editor) |
| **Session XML capture** | partial (built-in save) | 🆕 explicit File → Record Session toggle | 🆕 |
| **REC indicator in title bar** | ❌ | 🆕 (red 🔴 REC) | 🆕 |
| **Error log** | ✅ `errors.log` | ✅ `ErrorLog.cs` | ✅ |
| **Debug log** | ✅ via `-d` CLI flag | ⚠️ script-level `[dbg:N]` | 🗓 |
| **Per-character log files** | ✅ | ✅ via Auto Log | ✅ rolls to the new day's file at midnight; falls back to a numbered file when another copy holds the day's log |
| **Per-window logs** | ⚠️ plugin | 🆕 `#windowlog` (#270) | 🆕 |
| **Log directory configurable** | ✅ `logdir` | ✅ via `LocalDirectoryService` | ✅ (no settings-dialog field; set with `#config logdir`) |
| **Crash log** | n/a (not compared) | ✅ `genie_crash.log`; a stray UI-thread error is logged and the session carries on | 🆕 |

**Logging rollup:**
- Auto Log, per-window logs and session XML capture all ship. The one difference is the debug log: Genie 4's `-d` flag wrote a file, Genie 5 traces to the screen (`#debug`, `#trace`, `#config scriptdebugwindow`, `tracesends`).

---

## 9. Updater (in-app)

> The planned standalone **LAMP 2.0** updater was **canceled** and replaced by an integrated in-app updater: Velopack for the Core app, plus a GitHub-feed framework for Maps and Plugins.

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Auto-updater | ✅ separate `Lamp.exe` | ✅ in-app (Velopack `UpdateManager`) | 🆕 |
| Check For Updates menu | ✅ | ✅ Help → Check for Updates (Core / Maps / Plugins / Scripts tabs) + ● badge; the dialog says what changed | ✅ |
| Auto-update on startup | ✅ | ✅ background **check** + badge; Help ▸ Update Settings for per-kind Check-on-Startup + Auto-Apply opt-in | 🆕 |
| Update plugins / maps / scripts independently | ✅ | ✅ maps + plugins + scripts (`ScriptsUpdater.cs`), each with its own Updates-dialog tab; pick which script files to update | 🆕 |
| Update channels (stable/beta/nightly) | ❌ | ✅ stable + beta. From 5.0.0 stable releases and pre-releases are published separately; every install moved to stable once | 🆕 |
| Core app self-update | ✅ `autoupdatelamp` (Windows) | ✅ Velopack, **verified end-to-end on Windows, macOS and Linux** incl. AppImage self-replace and delta feeds (#27) | 🆕 |
| Signed releases | ❌ | ✅ every tagged release is EV-signed via SignPath | 🆕 |

**Updater rollup:**
- Integrated updater **shipped** on all three platforms, superseding LAMP 2.0.
- Remaining is hardening, not features (Horizon 1 Track A): validating update download URLs (HTTPS + host pinning, aimed at 5.0.1) and a signed manifest.

---

## 10. Images & Audio

> **Major V2.2 correction:** V1 said Genie 5 had "no audio system." That is **wrong** — a full cross-platform audio + neural-TTS stack shipped. Audio is no longer the gap; inline image rendering is.

### Images (`<image>` tags from DR)

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Render server `<image>` tags inline in game text | ✅ | ❌ outside dialogs and the injuries panel, `<image>` is consumed and not drawn (`DrXmlParser.cs`) | 🗓 |
| `#img` / `#image` — a picture line from a script or the command bar | ✅ | ✅ (#361, `InlineImageCache.cs`) | ✅ |
| Images inside server dialogs | ✅ | ✅ dialogs show their content rather than sprite names (#341, #345) | ✅ |
| Update Images command | ✅ | ❌ (art auto-fetched on demand instead) | 🗓 |
| Show Images toggle | ✅ | ✅ File ▸ Master Toggles ▸ Images (rides `showimages`; Portrait art live) | ✅ |
| Art directory | ✅ `Art/` | ✅ `Config.ArtDir` is a real cache dir; `RoomArtService` downloads `play.net/bfe/DR-art/{id}.jpg` | ✅ |
| Room/scene art panel | ✅ | ✅ renders in the Portrait/Scene panel (`SceneViewModel`) | ✅ |

### Audio — ✅ SHIPPED (was "no audio system" in V1)

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| WAV / sound playback engine | ✅ | ✅ `AudioService` — Windows `winmm PlaySound`, macOS `afplay`, Linux `paplay`/`aplay` (`AudioService.cs:24`) | ✅ |
| Sound on highlight match | ✅ | ✅ `DefaultHighlights.OnHighlightSound` → `PlaySound` (`MainWindowViewModel.cs:4420`) | ✅ |
| Sound on trigger match | ✅ | ✅ (`TriggerEngineFinal.cs:87`) | 🆕 |
| Mute toggle | ✅ `muted` | ✅ `IsMuted` ↔ `Config.PlaySounds`, gates the real engine (`GenieCore.cs:1554`) | ✅ |
| Sound directory | ✅ | ✅ `sounddir` config key (resolved via `LocalDirectoryService`) | ✅ |
| System speech (TTS) | ✅ via `SpeechSynthesizer` (Win-only) | 🆕 offline **neural** TTS (Piper VITS/ONNX) or the system's own voices (#368), `#speak` / `#tts`, per-stream read-aloud, per-rule Speak | 🆕 |

**Image/Audio rollup:**
- **Audio fully shipped** — playback engine, highlight/trigger sound, File ▸ Mute Sounds, sound dir, and cross-platform TTS (an improvement over G4's Windows-only `SpeechSynthesizer`).
- **Room/scene art and `#img` shipped**; the image gaps are inline drawing of server-sent `<image>` tags in the game-text flow and the explicit "Update Images" command.
- Roadmap (Horizon 3): screen-reader support — automation peers and labelling so NVDA / Narrator / VoiceOver can navigate the client.

---

## 11. Profile Management

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Saved connection profiles | ✅ XML files | ✅ JSON via `ProfileStore.cs` | ✅ |
| Tree-view of accounts/games in connect dialog | ✅ | ✅ flat list (simpler) | ✅ |
| Password encryption on disk | ✅ XOR'd in XML | 🆕 AES-256-GCM machine-bound key (`ProfileCrypto.cs`) | 🆕 |
| Include Password in Profile (toggle) | ✅ optional | always-encrypted-if-saved | 🆕 |
| Per-character config directory | ✅ | ✅ `Profiles/{Char}-{Acct}/` | ✅ |
| Per-profile rule overrides | ✅ | ✅ per-character rules layered over a shared global set (#257) | 🆕 |
| Login over TLS | ✅ | ✅ SGE on TLS 7910 by default, plaintext 7900 fallback; Linux and macOS too (#316) | ✅ |
| Per-profile layout state | ✅ | ✅ Save/Load Layout with per-profile scope | ✅ **shipped since V1** |
| Profile notes | ✅ via `DialogProfileNote` | ❌ | 🗓 |
| Character display format (`Char-Acct`) | n/a | ✅ (`CharacterIdentity.cs:23`) | ✅ **shipped since V1** |
| OS keystore (DPAPI/Keychain/libsecret) | ❌ | ❌ | 🎯 Roadmap — Track A hardens the key derivation to per-user PBKDF2/Argon2 (aimed at 5.1) and restricts profile-file permissions (aimed at 5.0.1); no OS-keystore work is scheduled |

**Profile management rollup:**
- Materially safer than Genie 4 (AES-GCM vs XOR), and an interrupted `profiles.json` write can no longer lock you out.
- `Character-Account` format and per-profile layout — both V1 gaps — ship.
- Open: profile notes, and the password-storage hardening above.

---

## 12. Hands / Vitals / Status Indicators

| Indicator | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Health / Mana / Spirit / Stamina / Concentration bars | ✅ | ✅ | ✅ |
| Bar colors configurable | ✅ via preset | ✅ via DisplaySettings | ✅ |
| Roundtime countdown | ✅ ComponentRoundtime | ✅ inline RT badge (optionally with a shrinking fill), or Genie 4's bar via Layout → Roundtime → Countdown Bar (`roundtime` preset) | ✅ |
| RT badge position (command bar vs hands strip) | ❌ | 🆕 toggleable | 🆕 |
| Spell-cast countdown | ✅ Castbar (`castbar` preset) | ✅ cast bar on the hands strip, coloured by the `castbar` preset; optional Combined Spell Timer works it into the spell box, `(N) Spell (M)` | ✅ |
| Posture: STAND/KNEEL/PRONE/SIT | ✅ | ✅ | ✅ |
| Stealth: HIDE | ✅ | ✅ | ✅ |
| Stealth: INVISIBLE | ✅ | ✅ Icon Bar chip (fed by IndicatorEvent) | ✅ |
| Afflictions: BLEED / POIS / DIS | ✅ | ✅ | ✅ |
| Afflictions: WEB / STUN / JOINED | ✅ | ✅ | ✅ |
| Status: DEAD | ✅ | ✅ | ✅ |
| Stance: OFF/ADV/FWD/NEU/GRD/DEF | ❌ as badge | 🆕 inline badge | 🆕 |
| Left/Right hand contents | ✅ | ✅ | ✅ |
| Prepared spell with elapsed time | ✅ | 🆕 with cast bar | 🆕 |
| `$preparedspell` script variable | ✅ | ✅ | ✅ |
| Hands strip position (top/bottom) | bottom only | 🆕 toggleable | 🆕 |
| Icon / Health / Script bar position | ✅ Dock Top / Dock Bottom | ✅ (#349, #357) | ✅ |
| Rested EXP bar; type-ahead counter | ❌ | 🆕 (rested bar assumes a Premium account — Standard accounts and Platinum read off) | 🆕 |
| Vitals bar colours | ✅ presets | ✅ the `health` / `mana` / `stamina` / `spirit` / `concentration` presets, with Genie 4's stock colours (#412) | ✅ |

**Hands/Vitals rollup:** full parity on gameplay indicators, including Genie 4's roundtime bar and combined spell timer; Genie 5 adds the stance badge, rested EXP bar and position toggles.

---

## 13. Other User-Visible Features

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Find / search in current buffer | ✅ Ctrl+F | ✅ Find bar on focused stream (`MainWindow.axaml.cs:761`) | ✅ **shipped since V1** |
| Paste Multi Line | ✅ | ✅ (Edit menu, splits on separator char) | ✅ |
| Ctrl+Right-Click selected text → command bar | ❌ | 🆕 | 🆕 |
| Tab-complete script names | ❌ | 🆕 | 🆕 |
| Up-arrow command history | ✅ | ✅ | ✅ |
| `<d cmd>` clickable links | ✅ | ✅ with echoOverride for friendly display | 🆕 |
| `<a href>` URL links | partial | 🆕 | 🆕 |
| URL safety prompt (`weblinksafety`) | ✅ | ✅ | ✅ |
| User-timeout auto-disconnect | ✅ `usertimeout` | ❌ by design — Genie 5 does not send `quit` for an idle user | ⚠️ |
| Server-timeout keep-alive | ✅ `servertimeout` | ❌ by design — no keep-alive verb is sent | ⚠️ |
| Silent-disconnect watchdog | ❌ | 🆕 `activitytimeout` declares a link dead after 5 minutes of total silence | 🆕 |
| Confirm-on-close dialog | ❌ | 🆕 | 🆕 |
| Recording REC indicator | ❌ | 🆕 | 🆕 |
| SimuCoins balance and reward claim | ❌ | 🆕 (#328) | 🆕 |
| Alteration designer | ⚠️ plugin | 🆕 built in, with Genie 4 import / export | 🆕 |
| Inactive tabs blink on activity; scrolled-back reading holds still | n/a (not compared) | ✅ (#293, #298) | ✅ |
| macOS and Linux builds | ❌ Windows only | 🆕 Apple Silicon, Intel and Linux AppImage on every tag | 🆕 |
| OBS streamer mode (hide sensitive info) | ❌ | ❌ | 🎯 Roadmap (Horizon 4) |

---

## Remaining Gaps after 5.0.0

Every row above that is not ✅ or 🆕, in one place.

### Genie 4 behaviour Genie 5 does not have
- **Evaluated `e/…/` triggers** — a trigger that fires on a variable change. Refused with a reason on import; no release assigned.
- **Command-bar `class:foo` modifier**; **class names on aliases and macros lost on save**; **Names have no class scope**.
- **Server `<image>` tags drawn inline** in the game text, and the **Update Images** command.
- **System-message Log panel** (the Log dock is a conversation stream).
- **Profile notes**; a **debug-log file** (`-d`); a **plugin manager dialog**.
- **`servertimeout` / `usertimeout`** — deliberately not carried over; `activitytimeout` is a dead-link watchdog, not a keep-alive.

### Known bugs against Genie 4 behaviour (tracked, with a release)
- `#config automapper true` also starts Record mode (#401) — 5.0.1.
- Rooms with day and night descriptions can match the wrong room (#407) — 5.1.
- The Room window ignores its font and colour settings and `roomdesc` (#424) — 5.1.
- Update / Repair Maps give no notice of the result (#423) — 5.1.
- The aim timer from the server dialogs is not drawn (#436).

### Security hardening (Horizon 1 Track A)
- Plugin DLLs load without hash or signature verification — 5.1.
- Update download URLs are not yet validated; profile files are written without restrictive permissions — 5.0.1.
- Stored-password key derivation, script-regex match timeout, dependency scanning — 5.1.

### Roadmap (see [ROADMAP.md](ROADMAP.md))
- **Horizon 2:** Bank Tracker and BestiaryQuery ports (#271), multi-line regex matching (#22), ignore-leading-timestamp matching, the map file encoding decision.
- **Horizon 3:** screen-reader support, Avalonia 12, plugin marketplace + signing, hunting analytics with history, script debugger, archived-session search, desktop notifications, AI advisor (advisory only), cloud sync.
- **Horizon 4:** attended multi-character support, streamer mode, visual trigger designer, Lua, mobile companion.
- **Parallel track:** GemStone IV.

---

## Closing notes

**Genie 5 at 5.0.0 has cleared every gap V1 called a blocker and every large gap V2 listed.** Server dialog windows, the MDI workspace, working presets, the community automapper script, and self-update on all three platforms shipped between beta.4 and 5.0.0.

**What is left is a short list of small Genie 4 behaviours** — evaluated triggers and the class-name loose ends in the rule engines are the ones a migrating Genie 4 user is most likely to notice — plus a security-hardening track that comes first in 5.0.1 and 5.1.

**Trigger phrase to revisit this doc:** "review the Genie 4 vs Genie 5 comparison" or "what's in the comparison audit."

---

## Change log

**V3 — 2026-11-22 (re-baseline to 5.0.0 stable):**
- Retitled from beta.3 to v5.0.0 "True Colours"; replaced the "road to v1.0" / "v1.0+" wording with the ROADMAP.md horizons and rewrote the Executive Summary, Remaining Gaps and Closing notes.
- Re-checked every ⚠️/🗓/❌ row against 5.0.0 source. **Flipped to shipped:** Mute Sounds menu item (V2 still said "no audio yet"), Icon / Health bar position, specialized walk routines (via automapper.cmd, #226), macOS / Linux self-update (#27), `#img`, the Configuration dialog pass. **Reworded:** `servertimeout` / `usertimeout` (replaced by `activitytimeout`, a watchdog rather than a keep-alive), evaluated triggers (Genie 5's `eval` flag is a different feature), OS keystore (not scheduled; key-derivation hardening is).
- **Confirmed still open:** `e/…/` triggers, `class:` modifier, Alias/Macro class persistence, Names class scope, inline server `<image>`, system-message Log panel, profile notes, plugin verification.
- Rebuilt the menu tables from `MainWindow.axaml` (new Maps, Analyst and Alterations menus; Layout menu additions; Help ▸ Performance) and refreshed the stale `MainWindow.axaml` line ranges.
- Added what shipped between beta.4 and 5.0.0: server dialog windows, Windowed Mode and built-in layouts, Shunts, the per-character / global rule layers, the script-engine parity batch, the Healing / Objects / Mobs / Experience windows, the Genie 4 plugin catalogue table, map editing and Repair Maps, per-window logs, release signing and channels.
- Fixed V2.3 self-contradictions: the AutoMapper rollup listed cross-zone pathing as both shipped and remaining; the menu rollup listed the AutoMapper master switch as deferred while the table showed it shipped; the script rollup called `random` / `counter` "verify" items while the table showed them ✅.
- Added known bugs against Genie 4 behaviour (#401, #407, #423, #424, #436) so the doc does not over-credit parity.

**V2.3 — 2026-08-02 (public edition):**
- Trimmed internal compliance / DR-policy detail and low-level implementation notes for public posting. Auto-reconnect is described as the user-facing feature it is (attended-session reconnect); the internal policy analysis is maintained separately.

**V2.2 — 2026-08-02 (full row-by-row re-verification):**
- Swept every remaining ⚠️/🗓/❌ row against beta.3 source via four parallel verification passes. The systematic pattern held: the doc was under-crediting Genie 5 nearly everywhere.
- **Config keys (§2):** flipped 11 of 15 to ✅ (`abortdupescript`, `promptbreak`, `promptforce`, `condensed`, `roundtimeoffset`, `monstercountignorelist`, `ignorescriptwarnings`, `parsegameonly`, `ignoreclosealert`, `connectscript`, per-data-dir overrides). `maxrowbuffer` clarified as superseded by `ScrollbackLines`. Only `servertimeout`/`usertimeout`/`requiresignedplugins` remain unimplemented.
- **Audio (§10):** corrected the flat-wrong "no audio system" — a full cross-platform `AudioService` (winmm/afplay/paplay) + highlight/trigger sound + mute + offline neural **Piper TTS** all ship. Room/scene art + `ArtDir` cache ship too. Only inline `<image>` rendering and the Update Images command remain.
- **Rule/script engine (§3/§4):** Presets render-side colors, highlight sound playback, native `EVAL`/`EVALMATH`, `random`, `counter` all ✅. Confirmed still-missing: Names class scope, `class:foo` parser modifier, Alias/Macro `ClassName` `.cfg` round-trip.
- **Mapper (§7):** cross-zone / multi-zone pathing ships (`MultiZonePathfinder`); AutoMapper Enabled toggle ships. Only specialized walk routines remain.
- **UI (§1/§5):** AutoMapper master toggle ✅; Log dock exists but is a conversation stream (system-message Log still absent) — corrected to ⚠️.

**V2.1 — 2026-08-02 (Scripts-menu re-verify):**
- Corrected the entire Script-menu block against source. Genie 5 ships a **full dedicated Scripts menu** (`MainWindow.axaml:474-540`): Script Manager, List/Pause/Resume/Abort All Scripts, Trace All (0/1/3/5/10), Update Scripts, External Editor, Open Scripts Folder — none of which V1 credited. Fixed the "no pause/resume primitive" and "no Script menu / Script Explorer" claims.
- The Scripts **updater** (`ScriptsUpdater.cs`, `IUpdater`, git-pull semantics) has shipped — corrected §2, §9, and the "scripts not yet" notes.
- Added a dockable **Script Manager** panel row to §5.

**V2 — 2026-08-02 (re-baseline to beta.3):**
- Retitled from a pre-alpha audit to a beta.3 parity/roadmap reference; dropped the "before alpha ships" purpose and the alpha-decision apparatus (Headline Calls, Alpha-Blocker Matrix, alpha-framed Recommendations).
- Unified the two V1 legends (status + alpha-decision) into one status legend.
- Flipped 9 rows verified against beta.3 source: JS scripts, Help menu, Find (Ctrl+F), AutoMapper auto-walk, `Character-Account`, layout save/load, Familiar/Death/Active Spells/Conversation streams, Lich auto-launch, and the `reconnect` key (now wired).
- Fixed V1 self-contradictions (Logging rollup vs table; JS listed as deferred in the Executive Summary while shipped elsewhere).

**V1 — original, prepared 2026-05-26, last synced 2026-07-05 (alpha.8).**
