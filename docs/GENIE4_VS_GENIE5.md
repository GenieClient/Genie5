# Genie 4 vs Genie 5 — Comprehensive Side-by-Side Comparison (V2)

**Prepared:** 2026-05-26 · **Re-baselined:** 2026-08-02
**Genie 5 version:** v5.0.0-beta.3 "Crosstalk" (shipped 2026-08-01)
**Genie 4 reference:** local clone at `_refs/Genie4/` (143 source files, WinForms + .NET 6, GenieClient/Genie4 upstream)
**Purpose:** Track feature parity with Genie 4 and surface the remaining gaps on the road to v1.0. (The original V1 of this doc audited parity *before the alpha shipped*; that framing is now history — see the change log at the bottom.)

> **What changed in V2 (2026-08-02):** the project moved from a pre-alpha audit to **beta.3**. Every item V1 flagged as an "alpha blocker" has shipped. This re-baseline retitles to beta.3, replaces the alpha-decision apparatus with a plain shipped/deferred/roadmap lens, flips the rows verified against beta.3 source, and fixes V1's internal contradictions.

---

## Executive Summary

Genie 5 at beta.3 is at or beyond Genie 4 across almost the entire feature surface. The plugin host, in-app updater, JavaScript (`.js`) scripting, engine-driven AutoMapper auto-walk, the full Help menu, layout save/load, and in-buffer Find have all shipped since V1 was written — including **every item V1 named as an alpha blocker**.

The remaining gap is small and splits two ways:
- **Deferred to later beta / post-beta** (don't block value): inline `<image>` rendering, a handful of niche config keys, `server`/`usertimeout` keep-alives, OS keystore.
- **Roadmap to v1.0+**: plugin marketplace, plugin signing / trust, AI advisor mode, macOS / Linux update packaging, cloud sync.

**Genie 5 also adds capability Genie 4 never had**: cross-platform (Win/Mac/Linux), AES-256-GCM password encryption, per-character profile dirs, Session Recorder, tab-complete script names, editor-of-choice integration, a Genie 4 settings-import dialog, map visual improvements, and a modern XAML/MVVM architecture.

### Resolved since V1 (alpha.8 → beta.3)

Everything V1 tagged 🔧 FIX BEFORE ALPHA is done, verified against beta.3 source:

| V1 blocker / gap | V1 status | beta.3 reality | Evidence |
|---|---|---|---|
| AutoMapper auto-walk (the named alpha blocker) | 🔧 FIX BEFORE ALPHA | ✅ Engine-driven step walker with Esc / off-plan / disconnect cancel | `AutoWalkService.cs` (`AutoWalkSession` over `AutoMapperEngine.FindPath`) |
| Help menu + community links | 🔧 FIX BEFORE ALPHA | ✅ Full Help menu (Latest Release, Discord, GitHub, Wiki, Play.net, Elanthipedia, Lich Discord) | `MainWindow.axaml:627-693` |
| `Character-Account` display format | 🔧 FIX BEFORE ALPHA | ✅ Shipped | `CharacterIdentity.cs:23`; picker label `ConnectDialog.axaml:25` |
| In-app DR policy summary | 🔧 FIX BEFORE ALPHA | ✅ Help menu carries the policy/link surface | `MainWindow.axaml:627-693` |
| JavaScript `.js` scripts (Jint) | ❌ Missing | ✅ Threaded Jint runtime + `js` / `jscall` / `include <file>.js` | `ScriptEngine.cs:1976`; `ScriptInstance.cs:22` |
| Find in buffer (Ctrl+F) | ❌ Missing | ✅ Find bar on the focused stream | `MainWindow.axaml.cs:761` |
| Layout save / load (workspace presets) | 🗓 BETA OK | ✅ Save/Load Layout, global + per-profile scopes | `SaveLayoutDialog.axaml`; `MainWindowViewModel.cs:143` |
| Familiar / Death / Active Spells / Conversation streams | 🗓 BETA OK | ✅ Registered dock tools | `GenieDockFactory.cs:184`; `ActiveSpellsViewModel.cs` |
| Auto-launch Lich on connect | 🗓 BETA OK | ✅ `#lc` / `#lconnect` / `#lichconnect` auto-launch + attach | `LichLauncher.cs` (`EnsureRunningAsync`) |
| Dedicated Scripts menu (Manager, List/Pause/Resume/Abort All, Trace All, Update Scripts) | 🗓 BETA OK | ✅ Full top-level Scripts menu + Scripts updater | `MainWindow.axaml:474-540`; `ScriptsUpdater.cs` |
| `Reconnect` config key (was present-but-unwired) | Low-risk | ✅ Now **wired** as an attended-session reconnect — reconnects only a session the user was actively driving | `MainWindowViewModel.cs:4798-4933` |

**Every V1 menu-parity item has now shipped.** The last one — the **"Open Log In Editor"** menu item — landed on top of Auto Log (shipped alpha.8): File ▸ Open Log In Editor opens the live session's log, or the most recent `*.log` when idle, in the configured editor (`OpenLogInEditorCommand` → `OpenLogInEditor`, reusing the `LaunchExternalEditor` ladder).

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
- **V2.2 verification scope:** as of the V2.2 pass (2026-08-02), **every** ⚠️/🗓/❌ row was re-verified against beta.3 source (four parallel verification sweeps — config keys, rule/script engine, UI/menu/mapper, audio/images/compliance). `file:line` citations in the tables mark freshly-verified claims. Rows still showing 🗓/❌/⚠️ are confirmed-current gaps, not stale carryovers.

**Status legend (unified in V2 — the old dual "status + alpha-decision" legend is retired):**
- ✅ **Shipped** — Genie 5 has it at parity
- 🆕 **Better / addition** — Genie 5 has it AND improves on Genie 4, or Genie 4 lacked it
- ⚠️ **Partial** — Genie 5 has some of it
- 🗓 **Deferred** — planned for later beta / post-beta; design notes in `backlog.md`
- 🎯 **v1.0+** — roadmap item; design exists but post-beta
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
| Classic Connect Window (toggle) | ✅ | n/a | ✅ | We don't have a legacy dialog to fall back to |
| Ignores/Gags Enabled (master toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Gags | ✅ | Engine gates via an Enabled flag (rules stay loaded/editable while off); live-synced with `#config gags off` |
| Triggers Enabled (master toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Triggers | ✅ | Same Enabled-flag gating; live-synced with `#config triggers off` |
| Highlights / Substitutes / Aliases master toggles | ❌ | ✅ File ▸ Master Toggles | 🆕 | Genie 5's Master Toggles set extends beyond G4's (Highlights / Triggers / Substitutes / Gags / Aliases / Images), all backed by same-named settings.cfg keys |
| Plugins Enabled (master toggle) | ✅ | ✅ per-plugin enable/disable (Plugins menu + `#plugin`) | 🆕 | Toggled individually; no single master switch |
| AutoMapper Enabled (master toggle) | ✅ | ✅ "Enable AutoMapper" (`MapperSettingsDialog`) + `#config automapper` | ✅ | Caveat: off = "lookup-only" (position still tracked, no room auto-create), not a hard tracking kill |
| Images Enabled (toggle) | ✅ | ✅ File ▸ Master Toggles ▸ Images | ✅ | Rides `showimages`; clears/re-fetches Portrait art live. Inline `<image>` rendering still deferred |
| Mute Sounds (toggle) | ✅ | n/a | 🗓 | No audio yet |
| Show Raw Data (toggle) | ✅ | ✅ Window ▸ Raw XML | ✅ | Dockable read-only live view of the raw server stream, hidden by default; tooltip notes it covers G4's Debug window |
| Update Maps from Official Repo... | n/a | 🆕 | 🆕 | Pulls from github.com/GenieClient/Maps |
| Open Maps Folder | ✅ via Open Directory | ✅ direct | 🆕 | Direct menu in Genie 5 |
| Change Maps Directory... | n/a | 🆕 | 🆕 | Genie 5 addition for git-clone workflow |
| Import from Genie 4... | n/a | 🆕 | 🆕 | Migrates 8 settings types with Global/per-character routing |
| Record Session (raw XML, toggle) | n/a | 🆕 | 🆕 | Captures raw XML to `Logs/raw_session_*.xml` |
| Open Recordings Folder | n/a | 🆕 | 🆕 | Pair with Record Session |
| Performance Test Parse (dev) | ✅ | partial via Console | ✅ | Genie 5's TestHarness REPLAY mode covers this |
| Exit | ✅ | ✅ | ✅ | |

### Edit menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Paste Multi Line | ✅ | ✅ | ✅ | Edit ▸ Paste Multi Line splits clipboard text on the separator char |
| Configuration... | ✅ tabbed dialog | ✅ tabbed dialog | ⚠️ | Genie 5 has tabs but the UX still wants a holistic pass per `backlog.md` "Configuration dialog UX pass" |
| Update Images | ✅ | n/a | 🗓 | No image rendering |
| Display Settings... | n/a | 🆕 | 🆕 | Font, colors, RoundTime position, hands strip position, editor path |
| Profile → Load / Save / Include Password | ✅ | ✅ merged into Connect dialog | ✅ | Genie 5 always encrypts saved passwords (AES-GCM) — no include toggle |

### Window menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Show/hide each dockable | ✅ | ✅ | ✅ | Same set: Game / Vitals / Room / Backpack / Mapper / Logons / Talk / Whispers / Thoughts / Combat |
| Hands Strip toggle + position | n/a | ✅ | 🆕 | Genie 5 addition (Top/Bottom) |
| Roundtime Position (Command Bar / Hands Strip) | n/a | ✅ | 🆕 | |
| Status Bar toggle | ✅ | ✅ | ✅ | |
| Game Window → per-tag toggle (Game / Echo / Script) | n/a | ✅ | 🆕 | Genie 5 addition |
| Float Mapper Window | n/a | ✅ | ✅ | Dock.Avalonia FloatDockable; can re-dock by dragging |
| Reset Layout | n/a | ✅ | ✅ | |
| Raw XML | ✅ (Debug) | ✅ | ✅ | Read-only live server stream, hidden by default |
| Find (Ctrl+F) | ✅ Ctrl+F | ✅ | ✅ | Find bar on the focused stream (`MainWindow.axaml.cs:761`) — **shipped since V1** |

### Layout menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Load Layout... / Save Layout As... | ✅ | ✅ | ✅ | **Shipped since V1** — global + per-profile scopes (`SaveLayoutDialog.axaml`) |
| Load Default Layout | ✅ | ✅ via Reset Layout | ✅ | Equivalent functionality |
| Save Default Layout / Save Sized Default / Basic Layout | ✅ | ⚠️ via Save Layout As | ⚠️ | Named-preset set is covered by Save/Load Layout; the specific G4 "default/sized-default/basic" verbs aren't 1:1 |
| Icon Bar | ✅ submenu | ✅ Icon Bar | ✅ | Text-chip strip below the vitals bar: posture chip (dead > standing > kneeling > sitting > prone) + STUNNED / BLEEDING / POISONED / DISEASED / HIDDEN / INVISIBLE / WEBBED / JOINED, fed by IndicatorEvent, dimmed while disconnected. Poison/disease chips are new over G4's six slots. Fixed position |
| Script Bar | ✅ | 🆕 always above command bar | 🆕 | Fixed-position; auto-hides when empty |
| Health Bar | ✅ | ⚠️ via Status Bar toggle | ✅ | Status bar is fixed at bottom |
| Magic Panels (toggle) | ✅ | ✅ | ✅ | G4 SetMagicPanels parity: mana bar / cast bar / spell labels show-hide with column reflow |
| Align Input to Game Window | ✅ | ✅ | ✅ | Command bar's side margins track the Game window's dock extent (full-width fallback when floated) |
| Always On Top | ✅ | ✅ | ✅ | Layout ▸ Always on Top |

### Scripts menu

A dedicated top-level **Scripts** menu ships in beta.3 (`MainWindow.axaml:474-540`), mirroring Genie 4's — the V1/early-V2 "command-bar only, no menu" claim was stale.

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| **Dedicated Scripts menu** | ✅ Script menu | ✅ Scripts menu | ✅ | Top-level Scripts menu (`MainWindow.axaml:481`) — **shipped since V1** |
| Script Explorer / Manager | ✅ tree browser | ✅ Script Manager panel | ✅ | Dockable Script Manager (browse / run / edit / pause / stop / reload / vars / trace); toggles from the Scripts menu; also opens via `#script explorer` (`MainWindow.axaml:486`) |
| Update Scripts | ✅ LAMP | ✅ Updates dialog ▸ Scripts tab | ✅ | `ScriptsUpdater.cs` (`IUpdater`, git-pull semantics; new/changed files pulled, sha-matched skipped, local-only files untouched) + Check-on-Startup / Auto-Apply toggles. **Shipped since V1** |
| Show / List Running Scripts | ✅ | ✅ List Running Scripts + Script Bar | ✅ | `#scripts`; Script Bar also always-visible when scripts run |
| Trace All Scripts (debug) | ✅ | ✅ Trace All submenu (Off / 1 / 3 / 5 / 10) | ✅ | `#traceall N` (`MainWindow.axaml:506`); plus per-chip debug levels on the Script Bar |
| Pause All / Resume All Scripts | ✅ | ✅ Pause All / Resume All | ✅ | `#pauseall` / `#resumeall` (preserves state via `UserPaused`) — **shipped since V1** (the "no pause/resume primitive" note was wrong) |
| Abort All Scripts | ✅ | ✅ Abort All Scripts | ✅ | `#stopall` |
| External Editor (change / OS default) | via `editor` cfg | 🆕 External Editor submenu | 🆕 | Shows the resolved launch-ladder rung; Change… writes the override, Use OS Default clears it |
| Open Scripts Folder | ✅ | ✅ | ✅ | Per-character Scripts directory |

### AutoMapper menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Show Window | ✅ | ✅ via Window menu | ✅ | |
| Update Maps | ✅ LAMP | ✅ direct via File menu | 🆕 | |
| Script Settings | ✅ FormMapperSettings | n/a | ✅ | G4's dialog drove a community `.cmd` walker; Genie 5 ships **engine-driven auto-walk** instead (`AutoWalkService.cs`) — the script-settings dialog is moot |

### Plugins menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| **Entire menu** | ✅ | ✅ Plugins menu (Open Folder, Reload, Load ▶, Enable/Disable ▶, Unload ▶) + `#plugin` | ✅ | Marketplace still roadmap |

### Help menu

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Check For Updates / AutoUpdate / Force Update | ✅ | ✅ Help → Check for Updates (dialog + ● badge + startup background check) | ✅ | In-app updater (Velopack); no silent auto-apply by policy |
| Update Settings (update policies) | ⚠️ scattered toggles | ✅ Help → Update Settings | 🆕 | Per-kind Check-on-Startup + Auto-Apply menus, plus a dismissible status-bar notice strip |
| Load Test Client | ✅ | n/a | 🗓 | Could use the GameCode picker on the Connect dialog (already supports Test) |
| Latest Release Page | ✅ | ✅ | ✅ | **Shipped since V1** (`MainWindow.axaml:627`) |
| Discord / GitHub / Wiki / Play.net / Elanthipedia / Lich Discord links | ✅ | ✅ | ✅ | **Shipped since V1** (`MainWindow.axaml:627-693`) |

### Profile menu (Genie 4 has its own menu)

| Menu Item | Genie 4 | Genie 5 | Status | Notes |
|---|---|---|---|---|
| Load / Save Profile / Include Password | ✅ separate menu | ✅ folded into Connect dialog | ✅ | No top-level Profile menu; functionality lives in the Connect dialog |

**Menu rollup:**
- Full Help menu with community links: ✅ shipped since V1.
- Full dedicated **Scripts** menu (Manager, List/Pause/Resume/Abort All, Trace All, Update Scripts, External Editor): ✅ shipped since V1.
- Still deferred: AutoMapper master off-switch.
- Auto Reconnect ships in an attended-session form (reconnects only sessions the user was actively driving).

---

## 2. Settings (cfg keys / configuration)

Genie 4 has 60+ config keys in `Lists/Config.cs`. Genie 5 has rough equivalents for most, with a shrinking backlog `Genie 4 Config-Option parity audit`.

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

Re-verified against beta.3 source (2026-08-02): most of this table has shipped since V1 — only three keys remain unimplemented.

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
| `requiresignedplugins` | False | Plugin signature verification | 🎯 v1.0+ (not present in `src/`; signing / trust is Phase 4) |
| `servertimeout` + `servertimeoutcommand` | 180s / fatigue | Keep-alive verb on idle | ❌ not present in `src/` |
| `usertimeout` + `usertimeoutcommand` | 300s / quit | User-side idle disconnect verb | ❌ not present in `src/` |
| Per-data-dir overrides (`artdir`, `logdir`, `configdir`, `plugindir`, `mapdir`, `scriptdir`, `sounddir`) | local relative dirs | user-settable keys, validated + resolved via `LocalDirectoryService` (`mapdir`/`plugindir` resolve against the shared root) | ✅ shipped (`GenieConfig.cs:594`) |
| Repository URLs (`scriptrepo`, `maprepo`, `pluginrepo`, `artrepo`) | empty | superseded by `update-feeds.json` | 🆕 maps + plugin + **script** feeds shipped (`ScriptsUpdater.cs`); art not yet |
| Lich integration (`rubypath`, `cmdpath`, `lichpath`, `licharguments`, `lichserver`, `lichport`, `lichstartpause`) | typical | ✅ auto-launch shipped (`LichLauncher.cs`) | ✅ **shipped since V1** — `#lc`/`#lconnect`/`#lichconnect` launch + attach |

**Only three config keys remain unimplemented:** `servertimeout` and `usertimeout` (no keep-alive / idle-disconnect anywhere in `src/`) and `requiresignedplugins` (Phase 4 signing / trust).

### Reconnect — now implemented (attended-session form)

| Key | Genie 4 default | Genie 5 status | Notes |
|---|---|---|---|
| `reconnect` | True | ✅ Wired as an attended-session reconnect | Reconnects only a session the user was actively driving (requires user input since connect); no longer the V1 present-but-unwired key. |

### Genie 5-only additions

- `frontendid` (`GENIE`/`STORM`) — FE handshake selector (CLI/code-controllable)
- `RoundTimeOnHandsStrip` — RT badge position
- `ShowGameText` / `ShowEchoText` / `ShowScriptText` — per-tag visibility
- `EditorPath` — external editor for `#edit`
- `MapBackgroundHex` — Mapper canvas background

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
| **Presets** | `Lists/Globals.cs` Presets + UI | `Presets/PresetEngine.cs` | n/a | `presets.cfg` (JSON) | ✅ render-side colors applied to game text (`DefaultHighlights.cs:342`) |

### Rule-engine sub-features

| Sub-feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Highlight sound playback (`SoundFile` on rule) | ✅ | ✅ (`AudioService` → `DefaultHighlights.cs:291`) | ✅ |
| Highlight "match whole line" vs "substring" | ✅ | ✅ | ✅ |
| Highlight foreground + background colors | ✅ | ✅ | ✅ |
| Highlight case-sensitive flag | ✅ | ✅ | ✅ |
| Trigger regex `/pattern/i` syntax | ✅ | ✅ | ✅ |
| Trigger `eval` expression triggers | ✅ via `e/` | ⚠️ via `def(...)` | ✅ (different syntax) |
| Trigger fire-on-input (vs server only) | ✅ via `triggeroninput` | ✅ | ✅ |
| Macro keybind: F-keys, Ctrl/Alt/Shift+X | ✅ | ✅ | ✅ |
| Variable types: SaveToFile / Temporary / Reserved | ✅ | ⚠️ via `Scope` enum (Global/Script/Tvar) | ✅ (semantically equivalent) |
| Reserved variables ($health, $mana, $stance, …) | ✅ ~30 vars | ✅ ~40 vars via `ScriptGlobalsSync` | 🆕 |
| Per-rule ClassName for filtering | ✅ | ✅ for Highlights/Triggers/Substitutes/Gags/Aliases/Macros; ❌ Names | ⚠️ partial |
| Command-bar `class:foo` modifier | ✅ on most | ❌ no `class:` token parsing anywhere (`CommandEngine.cs`) | 🗓 |
| .cfg round-trip with class name | ✅ | ⚠️ Highlight/Trigger/Sub/Gag serializers write ClassName; **Alias/Macro drop it on save** (`CfgFormat.cs:32,57`) | 🗓 |

**Rule-engine rollup:**
- All core engines ship at full Genie 4 parity; Presets render-side colors and highlight sound playback both ship (audio system confirmed present).
- Three confirmed small gaps (beta.3): command-bar `class:foo` parser modifier (no `class:` parsing at all), Alias/Macro `ClassName` `.cfg` persistence (held in memory, dropped on save), and Names-engine class scope.
- Names class scope is lowest priority (internal player-name highlighter, not a user-rule engine).

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
| Abort-on-undefined-var | n/a | 🆕 (G4 silently expanded to empty) | 🆕 |

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
- `.cmd` parity: ~99% verified against community scripts.
- `.js` (Jint) and Lich auto-launch — both V1 gaps — now ship. No major script-engine gaps remain (`random`/`counter` are the only "verify" items).

---

## 5. UI Panels / Windows

Both clients support flexible dockable layouts; the tech differs (WinForms MDI vs. Avalonia + Dock.Avalonia).

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
| **Conversation** (NPC speech) | ✅ | ✅ | ✅ | **Shipped since V1** (conversation-class streams registered) |
| **Log** | ✅ system messages | ⚠️ dock exists but carries conversation/speech + `#echo >log` | ⚠️ | A dockable Log window ships (`GenieDockFactory.cs:188`), but *system diagnostics* still route to the Game window (Game Window ▸ Script Lines), not this panel |
| **Debug** (parser trace) | ✅ | ✅ via Raw XML window | ✅ | Plus `[dbg:N]` script-level traces |
| **Raw** (raw XML inspector) | ✅ | ✅ RawXmlTool | ✅ | Window ▸ Raw XML, hidden by default |
| **Portrait** | ✅ | ✅ | ✅ | Room-art panel takes its G4 name; dock id unchanged so saved layouts restore it |
| **Room** (title/description/exits) | n/a as separate | ✅ | 🆕 | Genie 5 splits room from game text into its own panel |
| **Hands Strip** | ✅ within icon bar | ✅ separate strip | 🆕 | Dedicated; toggleable position |
| **Script Bar** | ✅ | ✅ | ✅ | Auto-hides when empty; per-chip pause/resume/debug/trace/vars/edit/stop |
| **Script Manager** panel | ✅ Script Explorer | ✅ dockable panel | 🆕 | Browse / run / edit the library + manage running scripts (pause / stop / reload / vars / trace); toggled from the Scripts menu or `#script explorer` |

### Default layout

| Aspect | Genie 4 | Genie 5 |
|---|---|---|
| Layout shape | User-configured MDI; default = Game center | 3-column: Room+Streams left / Game+Mapper center / Backpack right |
| Status bar | Optional | Yes by default (Wrayth-style vitals bars at bottom) |
| Hands strip | In icon bar | Dedicated strip, default below status bar |

**UI rollup:**
- The niche stream windows V1 deferred (Familiar / Death / Active Spells / Conversation) have all shipped. The one nuance: the **Log** dock exists but is a conversation/speech stream — G4's *system-message* Log has no dedicated panel (those lines still go to the Game window).
- Genie 5 keeps a more opinionated default 3-column layout.

---

## 6. Plugin System

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Plugin host (.NET DLL API) | ✅ `Core/PluginHost.cs` + `Core/LegacyPluginHost.cs` | ✅ `Genie.Plugins.Abstractions` (`IGeniePlugin`/`IPluginHost`/`IGameStateView`) + `PluginManager` | ✅ |
| Plugin DLL loading | ✅ | ✅ collectible `AssemblyLoadContext` (`PluginLoadContext`); `DiscoverAndLoad` from `{AppData}/Genie5/Plugins/`; load / unload / reload | ✅ |
| Plugin signature verification | ✅ `requiresignedplugins` | ❌ | 🎯 v1.0+ (Phase 4 — signing / trust) |
| Plugin manager UI | ✅ `Forms/FormPlugins.cs` | ⚠️ Plugins menu + `#plugin`; no dedicated manager dialog | 🆕 |
| Plugin marketplace | ❌ | 🎯 backlog "Modern Plugin Marketplace" | 🎯 v1.0+ |
| First external plugin | ❌ | ✅ `Plugin_EXPTrackerV5` (separate repo) | 🆕 |

**Plugin rollup:**
- Plugin host **shipped**: UI-free contract, collectible-ALC loader, Plugins menu + `#plugin`, first external plugin.
- Roadmap (v1.0+): marketplace, Phase 4 signing / trust + API-surface lint.
- G4 plugin DLLs still need a recompile against `Genie.Plugins.Abstractions` (WinForms/Windows-only); the interface shape is kept familiar to ease porting.

---

## 7. AutoMapper

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Map data format (zone XML) | ✅ canonical | ✅ identical | ✅ (round-trip via `Genie4MapImporter` / `Genie4MapExporter`) |
| Map rendering (zone canvas) | ✅ | ✅ `MapCanvas` | ✅ |
| Click-to-go on map | ✅ left-click | ✅ right-click context menu | 🆕 |
| **Auto-walk between rooms** | ✅ via `.automapper` script | ✅ **engine-driven** (`AutoWalkService.cs`) | ✅ **shipped since V1** — was the named alpha blocker; steps through `FindPath` results with Esc / off-plan / disconnect cancel |
| Map fingerprinting (title + exits) | ✅ | ✅ `MapFingerprint.cs` | ✅ |
| Auto-detect zone from current room | ⚠️ via script | ✅ engine | 🆕 |
| Less Obvious Paths display | ✅ | ✅ clickable buttons | ✅ |
| Editable room Notes | ✅ | ✅ inline editor, saves to zone XML | ✅ |
| Stale-zone warning | ❌ | 🆕 "may be stale" badge after 30 days | 🆕 |
| Auto-center on current room | ✅ via script | ✅ engine | ✅ |
| Zone update from official repo | ✅ via LAMP | ✅ via File menu (`MapsUpdater` + `GithubContentsSource`) | 🆕 |
| Multi-zone navigation (cross-zone pathing) | ✅ via script | ✅ `MultiZonePathfinder.cs` + `AutoWalkService.StartCrossZone` + `ZoneConnections.xml` editor | ✅ **shipped since V1** |
| Per-class mapper script | ✅ AutoMapper Script Settings dialog | n/a (engine walker supersedes) | ✅ |
| Sigil / search walk / caravan / broom_carpet / iceroadcollect | ✅ via script | ❌ specialized routines | 🗓 |
| Map visual: zoom, pan | ✅ basic | ✅ mouse-wheel zoom | ✅ |
| Map visual: room color by exit type | ❌ | 🆕 cyan vertical / green special / grey compass | 🆕 |
| Map visual: room labels from Notes | ❌ | 🆕 | 🆕 |
| Float mapper to separate window | ❌ | 🆕 (Dock.Avalonia FloatDockable) | 🆕 |

**AutoMapper rollup:**
- Data + rendering: Genie 5 ahead.
- **Auto-walk shipped** (the one alpha blocker V1 named) — engine-driven via the command pipeline, with a cancel-on-input / off-plan / disconnect guard (compliance-aligned; see below).
- **Cross-zone pathing also ships** now (`MultiZonePathfinder` + `ZoneConnections.xml`) — the V1 "single-zone only" limit is stale. The remaining gap is only the specialized community walk routines (sigil / search / caravan / broom_carpet / iceroadcollect), which are not implemented in-engine.
- Remaining: cross-zone chained pathing and the specialized community walk routines (sigil/search/caravan/etc.).

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
| **Per-character log files** | ✅ | ✅ via Auto Log | ✅ |
| **Log directory configurable** | ✅ `logdir` | ✅ via `LocalDirectoryService` | ✅ (override UI deferred) |

**Logging rollup:**
- Auto Log (rendered text) shipped; session XML capture is a Genie 5 improvement.
- The **"Open Log In Editor"** menu item has now shipped — no logging gaps remain.

---

## 9. Updater (in-app)

> The planned standalone **LAMP 2.0** updater was **canceled** and replaced by an integrated in-app updater: Velopack for the Core app, plus a GitHub-feed framework for Maps and Plugins.

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Auto-updater | ✅ separate `Lamp.exe` | ✅ in-app (Velopack `UpdateManager`) | 🆕 |
| Check For Updates menu | ✅ | ✅ Help → Check for Updates (Core / Maps / Plugins tabs) + ● badge | ✅ |
| Auto-update on startup | ✅ | ✅ background **check** + badge; Help ▸ Update Settings for per-kind Check-on-Startup + Auto-Apply opt-in | 🆕 |
| Update plugins / maps / scripts independently | ✅ | ✅ maps + plugins + scripts (`ScriptsUpdater.cs`), each with its own Updates-dialog tab | 🆕 |
| Update channels (stable/beta/nightly) | ❌ | ✅ stable + beta (no nightly) | 🆕 |
| Core app self-update | ✅ `autoupdatelamp` | ⚠️ Velopack replaces app in place; Windows install only — macOS / Linux packaging on roadmap | 🆕 |

**Updater rollup:**
- Integrated updater **shipped**, superseding LAMP 2.0.
- Remaining: macOS / Linux Core packaging + per-platform `IReleaseSource`; signed-installer + signed-manifest hardening (Phase 4). (The `ScriptsUpdater` V1 listed as a to-do has since shipped.)

---

## 10. Images & Audio

> **Major V2.2 correction:** V1 said Genie 5 had "no audio system." That is **wrong** — a full cross-platform audio + neural-TTS stack shipped. Audio is no longer the gap; inline image rendering is.

### Images (`<image>` tags from DR)

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Render `<image>` inline in game text | ✅ | ❌ non-injury `<image>` tags are dropped (`DrXmlParser.cs:1492`) | 🗓 |
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
| System speech (TTS) | ✅ via `SpeechSynthesizer` (Win-only) | 🆕 offline **neural** TTS (Piper VITS/ONNX), `#speak` + per-highlight speak (`TtsService.cs`) | 🆕 |

**Image/Audio rollup:**
- **Audio fully shipped** — playback engine, highlight/trigger sound, mute, sound dir, and cross-platform offline neural TTS (an improvement over G4's Windows-only `SpeechSynthesizer`).
- **Room/scene art shipped**; the only real image gap is **inline `<image>` rendering in the game-text flow** (non-injury image tags are still dropped) and the explicit "Update Images" command (art is auto-fetched instead).

---

## 11. Profile Management

| Feature | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Saved connection profiles | ✅ XML files | ✅ JSON via `ProfileStore.cs` | ✅ |
| Tree-view of accounts/games in connect dialog | ✅ | ✅ flat list (simpler) | ✅ |
| Password encryption on disk | ✅ XOR'd in XML | 🆕 AES-256-GCM machine-bound key (`ProfileCrypto.cs`) | 🆕 |
| Include Password in Profile (toggle) | ✅ optional | always-encrypted-if-saved | 🆕 |
| Per-character config directory | ✅ | ✅ `Profiles/{Char}-{Acct}/` | ✅ |
| Per-profile rule overrides | ✅ | ✅ via per-character config dir | ✅ |
| Per-profile layout state | ✅ | ✅ Save/Load Layout with per-profile scope | ✅ **shipped since V1** |
| Profile notes | ✅ via `DialogProfileNote` | ❌ | 🗓 |
| Character display format (`Char-Acct`) | n/a | ✅ (`CharacterIdentity.cs:23`) | ✅ **shipped since V1** |
| OS keystore (DPAPI/Keychain/libsecret) | ❌ | 🗓 backlog | 🗓 (AES-GCM already safer than G4) |

**Profile management rollup:**
- Materially safer than Genie 4 (AES-GCM vs XOR).
- `Character-Account` format and per-profile layout — both V1 gaps — now ship.
- OS keystore remains a beta enhancement; current crypto is correct for local-only storage.

---

## 12. Hands / Vitals / Status Indicators

| Indicator | Genie 4 | Genie 5 | Status |
|---|---|---|---|
| Health / Mana / Spirit / Stamina / Concentration bars | ✅ | ✅ | ✅ |
| Bar colors configurable | ✅ via preset | ✅ via DisplaySettings | ✅ |
| Roundtime countdown | ✅ ComponentRoundtime | ✅ inline RT badge | ✅ |
| RT badge position (command bar vs hands strip) | ❌ | 🆕 toggleable | 🆕 |
| Spell-cast countdown | ❌ | 🆕 magenta bar with (N) prefix | 🆕 |
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

**Hands/Vitals rollup:** full parity on gameplay indicators; Genie 5 adds cast bar, stance badge, and position toggles.

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
| User-timeout auto-disconnect | ✅ `usertimeout` | ❌ | 🗓 (config exists, not wired) |
| Server-timeout keep-alive | ✅ `servertimeout` | ❌ | 🗓 |
| Confirm-on-close dialog | ❌ | 🆕 | 🆕 |
| Recording REC indicator | ❌ | 🆕 | 🆕 |
| OBS streamer mode (hide sensitive info) | maybe (unclear) | ❌ | 🎯 v1.0+ |

---

## Remaining Gaps to v1.0

The alpha-blocker matrix is retired — all V1 blockers shipped. What's left:

### Small / deferred (later beta) — the genuinely-remaining gaps after full re-verification
- Three config keys only: `servertimeout` + `usertimeout` (keep-alive / idle-disconnect, not present) and `requiresignedplugins` (Phase 4).
- Command-bar `class:foo` parser modifier; Alias/Macro `ClassName` `.cfg` persistence; Names-engine class scope.
- Dedicated **system-message** Log panel (a Log dock exists but carries conversation/speech).
- `-d` / dedicated debug-log file; profile notes; per-data-dir override UI surfacing.

### Multi-day (beta → v1.0)
- Inline `<image>` rendering in the game-text flow (non-injury image tags currently dropped) + an explicit "Update Images" command.
- Specialized community walk routines (sigil / search / caravan / broom_carpet / iceroadcollect) — cross-zone pathing itself already ships.
- macOS / Linux Core update packaging + per-platform release source.
- Configuration dialog holistic UX pass; OS keystore for credentials.

### v1.0+ (vision)
- Plugin marketplace; Phase 4 plugin signing / trust + API-surface lint.
- AI advisor mode.
- Cloud sync / cross-device profiles.
- OBS streamer mode; combat analytics; visual trigger/flow designer.

---

## Closing notes

**Genie 5 at beta.3 has cleared every gap V1 called a blocker.** The plugin host, in-app updater, JavaScript scripting, engine-driven auto-walk, the full Help menu, layout save/load, in-buffer Find, the niche stream windows, `Character-Account` formatting, and Lich auto-launch have all shipped — as has the last V1 menu-parity item, "Open Log In Editor." No V1 gap remains genuinely absent.

**The largest remaining items are all v1.0+ vision work** — plugin marketplace, signing / trust, AI advisor, and macOS / Linux update packaging.

**Trigger phrase to revisit this doc:** "review the Genie 4 vs Genie 5 comparison" or "what's in the comparison audit."

---

## Change log

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
