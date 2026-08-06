# Genie 5 — v5.0.0-beta.4.b

Silent-disconnect detection, config edits that finally stick on Genie 4-import
profiles, a mapper zone-browsing suite, and a wide batch of scripting and
parity fixes — the external-testing bundle.

## ✨ New
- **Silent-disconnect watchdog.** DragonRealms can end a session without
  closing the connection — the client used to sit "Connected" (and
  `$connected` stayed `1`) indefinitely. A server-activity watchdog now
  declares the link dead after 5 minutes of total silence (a healthy link is
  never quiet for more than ~30 seconds), triggering the normal
  disconnect handling. Tune or disable with `#config activitytimeout`
  (seconds, 60–3600, `0` = off).
- **Mapper zone browsing.** Picking a map you're not in pauses tracking with
  a "Return to Current Zone" bar instead of confusing the walker; clicking a
  blue cross-zone room opens the connecting map (Genie 4 parity); the zone
  dropdown gains Name / Recently Changed / Map Number sort orders and a
  SPECIAL badge for event maps.
- **`/track` and `/trackreset`** are claimed client-side, so the Genie 4
  EXPTracker muscle-memory commands work (and never leak to the game).

## 🐛 Fixes
- **Configuration edits survive reconnect on Genie 4-import profiles.** For
  profiles carrying `.cfg` files, panel edits (macros, highlights, triggers,
  aliases, substitutes, gags, variables, classes) were silently reverted at
  the next connect by the stale `.cfg` replay. Panel saves now keep the
  `.cfg` in lockstep, and the offline dialog shows cfg-only rules.
- **`#send`, `#wait`, and `#event` queues actually pump** in the app —
  queued sends no longer sit forever.
- **Connect dialog attributes the session to the right profile** — retyping
  credentials for a different character no longer scopes the session (LIVE
  badge, rules, layout, config dir) to the previously selected profile.
- **Automapper follows Genie 4 hidden-exit arcs** (`search go X` directives
  and quick-send chains) during `#goto`.
- **Undefined `$variables` stay literal** in scripts (Genie 4 parity)
  instead of collapsing to empty text.
- **Every script death raises `ScriptFinished` exactly once** — no more
  missed or doubled notifications for `#action` / plugin listeners.
- **`#action remove` on a pattern that isn't registered** is a silent no-op
  (Genie 4 parity) instead of an error.
- **`#config fe`** resolves as an alias of `frontend`, and four stranded
  config keys are reachable from `#config list` categories again.
- **Phantom "Record Session" toggles fixed** — a bare Alt press no longer
  arms the menu bar so the next keystroke can't trigger a menu item.
- **Status bar slots collapse promptly** — a cleared status row lingered
  5 seconds before giving the space back.
- **macOS menu bar reads "Genie 5"** instead of "Avalonia Application"
  (#215 — thanks @simtel12).

## 💡 Notes
- The watchdog is on by default at 300 seconds. If you play through a
  connection that legitimately goes quiet for longer (an unusual proxy
  setup), raise it with `#config activitytimeout 600` or disable with `0`.

# Genie 5 — v5.0.0-beta.4

A new editor-backed Game window you can opt into, Open Log In Editor, and a
scripting fix for community `-verb` send idioms.

## ✨ New
- **AvaloniaEdit-backed Game window (opt-in).** A new renderer for the main
  Game window via `#config useeditorgamewindow on` (default off): flat memory
  use regardless of scrollback depth and a "Pause Scrolling" that genuinely
  holds. The classic renderer stays the default until parity is proven.
- **File ▸ Open Log In Editor.** Opens the current session log in your
  configured external editor — the last Genie 4 File-menu parity item.

## 🐛 Fixes
- **`send`/`#send` treat a leading `-verb` as "fire eagerly."** Community
  scripts that prefix a command with `-` (e.g. `send -cast;-0.05 gesture`)
  now reach the game instead of bouncing "Please rephrase." `put` is unchanged.

## 💡 Notes
- The editor-backed Game window is experimental and off by default; the stream
  windows will follow on the same renderer in a later build.

# Genie 5 — v5.0.0-beta.3

Window routing that finally works, a Hide Title Bar fix, and a batch of stream,
Lich, and experience fixes — several from @simtel12.

## ✨ New

- **Hide a floated window's title bar** to reclaim space — from either the window
  menu or the title-bar right-click menu; restore by double-clicking the window's
  top edge. (#181)
- **CircleCalc output toggles** — `#var CircleCalc.Echo 0` hides the result and
  `#var CircleCalc.Parse 0` stops it feeding scripts (both default on). (#207)
- **Per-creature combat status** — `<crtrStatus>` (hostile / disengaged / flying)
  is now tracked in game state, cleared on room change. (#202)

## 🐛 Fixes

- **Closed stream panels honour their "If Closed" routing** — a closed panel's
  text can redirect to another window (Talk/Whispers consolidate into Log by
  default), follows the chain if that window is also closed, and is never
  silently dropped; the Layout-tab dropdown now round-trips. (#211)
- **Hide Title Bar no longer blanks the window** — it collapses only the title
  strip, leaving the panel content in place. (#181)
- **No more double-printed stream lines in Main** when a stream panel is closed.
  (#210)
- **Blank lines are preserved** — `INFO`, `LOOK`, `HELP`, and experience spacing
  no longer collapse into a wall of text. (#209)
- **Crafting no longer leaks markup** — the `<forging>` UI element is recognized
  and discarded instead of showing as garbled text. (#208)
- **Live experience under `BRIEFEXP ON`** — the Experience window updates from the
  shorthand pulses. (#204)
- **Lich debug logging** — resolves the temp directory from the arguments Lich
  actually received, and keeps the mirror up until the owned Lich exits. (#205,
  #206)
- **Extension output feeds scripts** — a built-in extension's game-window output
  now drives script actions and triggers, matching Genie 4.

## 💡 Notes

- **DR front-end features:** DR gates `EXPBRIEF` and richer clickable-command
  markup to the Wrayth front end. To enable them, set `#config frontend wrayth`
  and reconnect.

Thanks to @simtel12 (Gregorios Leach) for #204, #205, #206, #209, #210, #212, and
for reporting #211.

---

# Genie 5 — v5.0.0-beta.2

Lich-proxy diagnostics and combat-color polish — the second beta.

## 🔌 New: `#config lichdebug`

When Genie auto-launches (owns) Lich, `#config lichdebug true` live-tails that
session's `temp/debug-*.log` into the game window as `[lich-debug]` lines for
the whole owned session — a direct window into what Lich is doing during a
proxy connect. It's independent of `#config conndebug` (the Genie-side
connection trace); enable both for a full Lich-proxy diagnosis. Auto-launch
status messages now include the Lich PID.

As part of this, Genie now resolves Lich's temp directory the way `lich.rbw`
actually parses its arguments (`--temp=PATH`, else `--home=PATH/temp`) and
honours quotes in `#config lichargs`, so a path with spaces survives. If
`lichargs` carries a directory flag Lich silently ignores (the space form, or
the `--help`-only `--temp-dir=`/`--script-dir=`/`--data-dir=` aliases), the
auto-launch now stops and names the working spelling instead of writing
somewhere you didn't ask for. Thanks @simtel12!

## 🐛 Fixes

- **Combat text bold is aligned** — bold/preset/link coloring landed a few
  characters off when a line contained an HTML entity before it (combat lines
  open with a literal `<`). Span offsets are now rebased into the decoded
  text, so the damage phrase paints correctly in both the Combat and Main
  windows. (#199)
- **Cleaner XML stream** — the server's `<link>` menu element (Game/Help menu
  URLs) is recognized and discarded instead of surfacing as stray output.
  (#198)

Thanks to @simtel12 for all three.

---

# Genie 5 — v5.0.0-beta.1

**Genie 5 graduates to beta** — plus a round of display-polish fixes.

## 🎓 Alpha → Beta

Genie 5 has been in alpha long enough to prove the shape is right: SGE and
Lich login, the Genie 4 `.cmd` script engine, the mapper, plugins, themes,
floating and docked windows, and — since alpha.10 — EV-signed Windows
builds. This release graduates the version line from `alpha` to **beta**.

Nothing about how you install or update changes. Beta builds still ship as
GitHub **pre-releases**, and the Core updater's **beta channel** delivers
them — so **Help → Check for Updates** will offer `beta.1` as a delta from
alpha.10. The stable `5.0.0` channel arrives once the beta soak and the
remaining roadmap items (cross-platform auto-update, server-driven dialog
windows) are done.

## 🐛 Fixes in this release

- **Monospace game text** — `<output class="mono">` blocks (stat tables,
  ASCII maps, some menus) now render in the monospace font while normal
  prose keeps your configured game font; highlights still paint. (#178)
- **Combat text keeps its color** — lines echoed from a side stream
  (combat, thoughts, whispers…) to the main window no longer lose their
  bold, link, and preset colors, so combat hits paint yellow again. (#187)
- **Floating windows restore correctly** — a minimized floating window
  comes back to its previous floated bounds instead of jumping to full
  screen, and a new **right-click title-bar menu** (Restore / Maximize /
  Minimize / Close) gives minimized floats a direct way back. (#196)
- **Script Manager keeps its name** — the panel no longer flips between
  "Script Manager" and "Scripts" depending on connection state. (#197)
- **Cleaner XML handling** — the server's `<switchQuickBar>` UI element is
  now recognized and discarded instead of leaking as stray output. (#188)

Thanks to everyone who filed these on GitHub.

---

# Genie 5 — v5.0.0-alpha.10

**The first code-signed release** — plus a batch of community fixes.

## 🔏 Signed Windows builds

Starting with this release, the Windows binary is **code-signed with an
Extended Validation (EV) certificate** issued by GlobalSign to
**Shadow Realms LLC**, the project's support partner. Every release is
built from this repository's source by GitHub Actions, submitted to
[SignPath.io](https://signpath.io/) automatically, and manually approved
by the maintainer before the HSM-held key applies the signature (with an
RFC 3161 timestamp, so signatures stay valid past certificate expiry).

What you'll notice: the signed `Genie5.exe` verifies as
**Shadow Realms LLC** under Properties → Digital Signatures, and the
"unknown publisher" era is over. SmartScreen reputation accrues per
file, so a reduced warning may still appear on brand-new builds while
download counts build up. macOS and Linux builds remain unsigned for
now. Details in the README's Code signing policy section.

The version jumps from the alpha.8.x series to **alpha.10** to mark the
milestone.

## 🆕 New

- **Lich: dynamic `lichargs` + owned auto-launch lifecycle** — the
  auto-launcher rebuilds Lich arguments per connection and properly owns
  the process it starts. Thanks @simtel12! (#182)

## 🔧 Fixed

- **Script Manager works before the first connect/command** — opening it
  on a fresh launch no longer requires a live session. Thanks @simtel12!
  (#193)
- **No more double `Disconnected` on a normal disconnect.** Thanks
  @simtel12! (#195)
- **Lich: `GENIE5-IDENT` probe is nil-safe** when `XMLData.name` is
  unset. Thanks @simtel12! (#185)
- **Lich: FE-port probe binding + owned PID preserved on reconnect.**
  Thanks @simtel12! (#186)
- **Folder/editor launches handle paths with spaces** via a shared
  FileBrowser helper. Thanks @simtel12! (#192)
- **Configuration → Layout → Windows keeps Apply/Reset reachable** on
  small screens. (#184)

## 📝 Docs

- New wiki exploration: **using an iPhone with Genie 5**. (#189)
- README **Code signing policy** rewritten for the EV certificate.

# Genie 5 — v5.0.0-alpha.8.17

A community bug-fix round — display polish and a Genie 4 command.

## 🆕 New

- **`#comment <window> <text>`** — Genie 4's window-title annotation is back.
  A trigger like `#comment Room $zoneid. $roomid` titles the Room panel
  **Room (69. 120)**; `#comment Room` with no text clears it. The window
  name matches a panel (`Room`, `Mapper`, …) case-insensitively. (#179)

## 🔧 Fixed

- **Intentional blank lines are preserved** — `INFO`, `LOOK <character>`,
  `HELP ADVICE` and similar output use blank lines for spacing; Genie 5 had
  been collapsing them. They now render with the same spacing as Genie 4,
  without adding stray blanks elsewhere. (#176)
- **Obvious-paths links sit on the baseline** — the room-exit direction
  links no longer render raised/superscript; they align with the
  surrounding text. (#177)
- **Script debug trace shows real line numbers** — at debug level 1+, the
  `goto` / `gosub` / `return` trace now reports the actual source line of
  the jump target instead of an internal index.

# Genie 5 — v5.0.0-alpha.8.16

A quick fix for a script-variable regression from 8.14/8.15.

## 🔧 Fixed

- **Nested `%$name_suffix` variables resolve again** — a variable name with
  an underscore suffix, like mm_train's `%$selection_DESC`, stopped
  resolving in alpha.8.14/8.15 and could surface as a `bad condition …
  unexpected '%'` error in menu scripts. The variable-name shrink search
  now breaks correctly at an underscore (as Genie 4 does), so
  `$selection_DESC` resolves as `$selection` + `_DESC` again. The
  alpha.8.14 fix this regressed (undefined skill/spell variables no longer
  erroring) stays fixed. **If you use mm_train or another menu script,
  grab this build.**

# Genie 5 — v5.0.0-alpha.8.15

Floating windows grow up, layouts remember them, and plugins get real
transform power.

## 🆕 New

- **Startup layout per character** — the Connect dialog gains a **Layout**
  dropdown: pick any saved layout (global or that profile's own) and it
  applies automatically every time that character connects. The choice
  saves with the profile — including when you hit Connect without Save.
- **`#tvar save` / `#tvar load`** — the advertised subcommands now work:
  save writes the tvars you've set this session to `tvars.cfg` (never the
  live game state), load replays them — including across restarts.
- **Plugin transform hooks are honored end-to-end** (plugin authors):
  - `OnGameText` now runs **first** in the per-line pipeline (Genie 4's
    order — plugins before triggers) and its return value is respected:
    rewrite a line and that's what scripts, triggers, and every window
    see; return null and the line is gagged everywhere. `#parse`-injected
    lines flow through the same way. Game state, the mapper, and the
    built-in trackers still read the raw server events — a plugin controls
    what's *seen*, not what *happened*.
  - New **`OnEcho(text, window)`** hook — `#echo` output, script `echo`
    lines, and system messages dispatch through plugins before display,
    same rewrite/gag contract. Genie 4 never ran echoes through plugins;
    existing plugin DLLs keep loading unchanged (default pass-through).

## 🔧 Fixed

- **One taskbar button** (#170) — floated panels no longer each claim a
  taskbar entry. They behave as proper tool windows now: a **minimize
  button** on the float's title bar, floats minimize/restore together
  with the main window, and a minimized float comes back via its Window
  menu entry.
- **Saved layouts keep floating windows** — save a layout with floated
  panels and loading it brings each one back at its saved position and
  size. Layouts saved before this release simply restore as they always
  did.
- **`$spellpreptime` is the spell's full prep length** (Genie 4 parity) —
  it had mirrored `$spelltime`'s elapsed count-up; it's now the constant
  casttime − prep-start duration, `0` when nothing is being prepared.

## 📝 Plugin-author note

Plugin dispatch moved from *last* to *first* in the per-line pipeline.
Observe-only plugins are unaffected; a plugin that relied on seeing lines
after scripts/triggers acted will notice the order change.

# Genie 5 — v5.0.0-alpha.8.14

Three community-reported fixes, all in Genie 4 parity territory: **script
variables and your hands now behave exactly like Genie 3/4.**

## 🔧 Fixed

- **`$righthand` / `$lefthand` carry the full display name** (#172) — Genie
  3/4 set these from the item's display name ("whiskey jug") and
  `$righthandnoun`/`$lefthandnoun` from the bare noun ("jug"); Genie 5 had
  both as the noun. The hands display in the status bar shows the full name
  now too. Live-verified in-game.
- **Undefined skill / spell variables no longer error** (#171) — lines like
  `if ($Outdoorsmanship.Ranks >= 1750)` or
  `if ($SpellTimer.Sanctuary.active = 1)` threw
  `bad condition … missing ')'` when the Experience or Active Spells window
  hadn't populated the variable yet. The engine's variable-name matching
  could stop mid-word and splice a shorter variable (`$out`, `$spelltime`)
  into the middle of the name; it now only matches at word boundaries. An
  unpopulated variable simply evaluates false — same as Genie 3/4 — and
  resolves normally once the skill or spell data arrives.
- **`roomname` preset colors the room title** (#174) — a preset rule for
  `roomname` now paints the room-title line, matching Genie 4.

# Genie 5 — v5.0.0-alpha.8.13

A quick follow-up to 8.12, all about highlights: **they now paint in every
window — and you can choose exactly which ones.**

## 🔧 Fixed

- **Highlights work in the Room, Mobs, and Players panels** — user highlight
  rules (and Names colors) previously painted only in the game window and
  the stream tabs; in the Room panel only the objects line was styled, and
  the Mobs / Players panels rendered plain. All three now run the same
  highlight pipeline as the game window, and the Room panel repaints in
  place when you edit rules mid-session. (From a field report: imported
  Genie 4 highlights worked everywhere except on room text.)

## 🆕 New

- **Per-window highlight scoping** — every highlight rule has an optional
  **Windows** list (Highlights tab, new field + column): leave it blank and
  the rule paints everywhere (the default — existing and imported rules are
  unchanged), or list window ids (`main`, `room`, `mobs`, `players`,
  `backpack`, or a stream tab like `thoughts`) to restrict it. Also
  available as the last argument of `#highlight add`, and shown by
  `#highlight list` as `@room,main`. The scope is stored on the Genie 5
  side — `highlights.cfg` stays byte-compatible with Genie 4.

# Genie 5 — v5.0.0-alpha.8.12

The headline: **Inventory View is now a first-class window** — your
characters' belongings as one searchable, sortable catalog.

## 🆕 New

- **Inventory View window** (Window menu, or `/iv open`) — run `/iv scan`
  while connected and Genie catalogs everything you own: items on your
  person, your **vault** (if you're holding a vault book), your **deed
  register**, your **home**, and **Trader storage**, per character, saved
  between sessions in the same `InventoryView.xml` format the Genie 4
  plugin used (existing files carry over — old catalogs damaged by DR's
  newer merged `INV LIST` output are repaired automatically on load).
  - **Search all characters** — live filter; matches highlight, their
    container chain stays visible.
  - **Wt / Size columns** — weight (stones) and dimensions per item,
    resolved from Elanthipedia's item database in the background the
    first time and cached locally after that. Undocumented items stay
    blank.
  - **Sortable columns** — click **Item**, **Wt**, or **Size** to sort
    within each container; click again to flip.
  - **Wiki Lookup** — jumps straight to the item's Elanthipedia page
    when one exists, else the wiki's search.
  - **Find in Shops** (also `/iv shops <text>`) — searches current
    player-shop listings, with data from the community-run
    [DR Service Plaza](https://drservice.info/Plaza/): price, shop,
    room, owner, and town. Fetched at most once per 6 hours.
  - **Export** to CSV, and per-character **Remove** (two-step confirm).
  - Replaces the external `Plugin_InventoryViewV5` DLL — a leftover copy
    in your Plugins folder is skipped automatically. `/iv help` lists
    every command; a scan still ends with the scriptable
    `InventoryView scan complete` line.
- **`#browser <url>`** — opens your OS-default browser (Genie 4 parity;
  previously "Unknown command: browser").
- **`#queue clear`** — flushes every queued-but-unsent command: the
  RT-gated command queue and running scripts' pending `put`/`send`
  segments (Genie 4 parity; travel.cmd's `RETURN_CLEAR` relies on it).

## 🔧 Fixed

- **`$game` / `$charactername` now come from the server** — the login
  stream's identity tag corrects them a few lines into any connection,
  which matters most for Lich sessions where the connect dialog can't
  know the instance (scripts branch on `$game` for Platinum/Fallen). The
  Lich tab also gains an **Instance** picker to seed the value.
- **Script diagnostics** — the `[script] name started` line now shows
  the resolved file path (running the *other* copy of a script is the
  classic "my fix didn't take"), a malformed `waiteval` warns once
  instead of hanging silently, and conditions accept Genie 4's
  trailing-paren leniency (`if (exists("x")) ) then` no longer reads as
  false).

# Genie 5 — v5.0.0-alpha.8.11

A small, focused release: **scripts can now run client /commands.**

## 🔧 Fixed

- **Scripts can drive client /commands** — `put /sort weapon`,
  `send /sort weapon`, and a bare `/sort weapon` script line now run the
  Circle Calculator (and every other tracker command — `/calc`, `/tt`,
  `/spelltimer`, `/exp`) exactly like typed input, instead of leaking the
  literal text to the game server. Claimed commands never touch the wire
  and don't consume the type-ahead budget; anything no tracker claims
  still goes to the game verbatim. The semicolon form
  (`put look;/sort weapon`) and delayed sends (`send 0.5 /sort`) work
  too. (#169)

## 📝 Script-author note

`#put /sort weapon` (leading `#`) is a **comment** in the script
language — Genie 4 parity — and is ignored by design. Use
`put /sort weapon` or a bare `/sort weapon` line.

# Genie 5 — v5.0.0-alpha.8.10

A bug-sweep release with one theme: **rules you import or save now load back
exactly as written.** Every fix came out of a single live smoke-test evening.

## 🔧 Fixed

- **Genie 4 imports survive a restart** — per-character imports were saved
  into a `Config/Profiles/{guid}/` folder the engine never read, so a "This
  character only" import silently vanished on relaunch. Core and the app now
  share one path contract (`Profiles/{Character}-{Account}/`), legacy GUID
  folders migrate automatically on first use, and the Configuration dialog
  saves to the same place the engine loads from. (#163)
- **Rule files are always real cfg files** — the Import dialog had been
  writing JSON into `.cfg`-named files; at connect the loader replayed those
  lines through the command pipeline, whose fallthrough sent them to the game
  server as typed commands. Saves and the import now share one cfg-format
  writer; loaders dispatch only #commands (file content can never reach the
  game) and self-heal legacy JSON saves by converting them in place on the
  next load. (#168)
- **Loads no longer corrupt rules or flood the login** — replaying saved
  rules doesn't variable-expand them any more (a trigger saved with pattern
  `$monstercount` came back as the literal `0`), and the per-rule
  "Trigger added: …" confirmations are silent during loads — one
  "Triggers Loaded" summary per file instead of a thousand-line wall. A class
  literally named `list` also restores correctly again. (#168)
- **Typed `#action {command} when {pattern}`** stored the rule transposed —
  action `when`, your pattern in the class column — creating a trigger that
  fired the literal command "when". Genie 4's action-first form now parses
  correctly from the command bar, as it always did inside scripts. (#162)
- **File → Import from Genie 4 works on a fresh launch** — it no longer
  claims the app is "still initialising" until you type a command. (#164)
- **Phantom walk strip** — the Mapper's walk indicator (Resume/Cancel +
  progress bar) no longer renders on a fresh launch with no walk active. (#165)
- **Analytics axis precision** — a "Rank over time" range spanning less than
  one rank shows decimal y-axis labels (131.66 / 131.68 / …) instead of the
  same integer on every tick; the hover badge matches the axis. (#166)
- `/sort` help text mentions the accepted `all` token (CircleCalc).

# Genie 5 — v5.0.0-alpha.8.9

A script-management milestone: the Scripts window grows into a full **Script
Manager**, the Injuries panel gets a proper body-part display, and a cluster of
script-variable parity gaps closes.

## ✨ New

- **Script Manager** — the Scripts window is now a full management panel
  (Scripts → Script Manager):
  - **Library tree** of your scripts folder (subfolders included) with a filter
    box, live folder watching, and preserved expansion across refreshes.
  - **Running scripts** with pause state, elapsed time, and the current line;
    a detail strip for the selected script; per-row actions (pause/resume,
    abort, reload, vars, trace, debug level, edit).
  - **Run with arguments**, inline new-script creation, two-step delete, and
    right-click menus on both the library tree and running rows.
  - **Script Bar chips** gain the same right-click menu, with live paused-state
    sync.
  - **`#script` command parity with Genie 4** — every panel action routes
    through it: `abort`/`pause`/`resume`/`pauseorresume` accept `all` and
    `except <name>`, `#script reload` hot-swaps a running script's file at its
    next `goto` (state preserved), plus `trace`, `vars`, `debug 0-10`, and
    `explorer` (opens the panel). `#script` alone lists running scripts in the
    Genie 4 status format.
  - **External Editor picker** — Scripts → External Editor shows which editor
    Edit will use and lets you change it or reset to the OS default (same
    setting as Display Settings).
- **Injuries body display** — the Injuries panel renders each body part as a
  sprite with severity colour variants (healthy ghost, wound 1–3, scar, nerve
  damage), in a compact 4×4 grid or an assembled figure — toggle in the panel
  or via `#config injurieslayout figure|grid`. Readings stay listed in words,
  so colour is never the only signal.
- **`$spellpreptime`** reserved script variable — the current spell's prep
  duration, alongside `$spellstarttime`/`$spelltime`.

## 🔧 Fixed

- **Window-menu Copy** — right-click → Copy now copies the full highlighted
  selection. It had silently copied nothing (then only the right-clicked line):
  the menu resolved its target to the individual line under the pointer rather
  than the window that owns the menu. Ctrl+C was never affected.
- **`$` variable scoping matches Genie 4** — `$name` resolves globals only
  (`#tvar`/`#var`/events) and no longer falls back to script-local `%`
  variables, fixing globals set through `#link → #parse` menu-script chains
  being shadowed by same-named locals. `$argcount` — which had only worked via
  that fallback — is restored as a true `$`-frame token: script args at top
  level, gosub args inside a gosub, capture count after a capturing match.
- **Mapper legend placement** — on dense maps (e.g. Throne City) the colour
  legend tests each viewport corner against the actual room boxes instead of
  the zone's bounding box, so it lands in a genuinely clear corner instead of
  covering rooms.
- **Cross-zone wait bar** — the "boarding boat · time left" text updates every
  second again (it could stick at just the separator).
- **Details side-tab** — the collapsed DETAILS caption renders vertically
  instead of clipping.

# Genie 5 — v5.0.0-alpha.8.8

Stability and mapper groundwork fixes.

## 🔧 Fixed

- **Connect / reconnect race** — an auto-reconnect racing a manual connect could
  interleave, with the second attempt clobbering the connection the first just
  made. Connects are now serialized, so overlapping connect/reconnect attempts
  always settle on a single clean session.
- **Cross-zone map connections** — the placeholder `ZoneConnections.xml` that
  Genie seeds on first launch no longer shadows the cross-zone links derived from
  your maps' border-room notes. Hand-authored connections now augment the derived
  graph instead of replacing it wholesale. (Groundwork for multi-zone travel;
  cross-zone routing itself still depends on per-room server ids your maps may not
  yet carry.)

# Genie 5 — v5.0.0-alpha.8.7

Two Genie 4 command-parity fixes: `#send` again queues with an optional delay
instead of firing immediately like `#put`, and `#beep` / `#bell` sound the
system alert instead of reporting an unknown command.

## 🔧 Fixed

- **`#send` delay & queue** — `#send` had behaved like `#put` (send right now).
  It again matches Genie 4: it queues through the roundtime gate with an optional
  leading delay, and `#send clear` empties the queue. `#send 5 stow my gem` waits
  5 seconds (plus roundtime), then sends `stow my gem`; `#put` still sends
  immediately. Scripts that rely on `#send N …` — such as retry-after-web combat
  loops — now behave as intended instead of transmitting the literal delay.
- **`#beep` / `#bell`** — both previously printed `Unknown command: beep`. They
  now sound the system alert, respecting the **Play Sounds** setting: a native
  beep on Windows, the system alert on macOS, and the terminal bell on Linux.
  Handy as a trigger action or a hunting-script alert.

# Genie 5 — v5.0.0-alpha.8.6

A Genie 4 parity and polish pass: a connect-time **flags check** that warns when
DragonRealms' `flags` settings would confuse the parser, the **Show in Main
Window** stream toggle on the right-click menu, `#lc` Lich shortcuts, and a batch
of parser and travel fixes.

## ✨ New

- **`flags` check at connect ([#29](https://github.com/GenieClient/Genie5/issues/29))**
  — Genie silently reads DragonRealms' `flags` settings once when you connect and
  warns if any of them would change what the parser sees (e.g. `RoomBrief`,
  `MonsterBold`, `ShowRoomID`, `StatusPrompt`). If a flag is in an untested state,
  a single advisory line tells you which one and how to restore it — so
  "my room window looks wrong" has an answer instead of silent misparsing. The
  probe never displays; a `flags` you type yourself still shows normally. Turn it
  off with `#config flagscheck off`.
- **"Show in Main Window" on the stream right-click menu** — every stream window
  (Logons, Talk, Combat, …) now has a **Show in Main Window** toggle in its
  right-click menu, matching the Configuration → Layout checkbox. It's **on by
  default**, so streams mirror into the main game window until you opt one out —
  turn it off for a stream you only want in its own panel.
- **`#lc` / `#lconnect` / `#ls` Lich shortcuts** — Genie 4's short aliases for
  `#lichconnect` (`#lc`, `#lconnect`) and a `#ls` / `#lichsettings` command that
  prints the Lich configuration. New **opt-in Lich auto-launch** (off by default):
  set `#config lichpath {…}` and `#config lichautolaunch on` and a Lich-proxy
  connect will start Lich for you if it isn't already running, then connect. With
  it off (the default) these verbs attach to an already-running Lich exactly as
  before.

## 🐛 Fixed

- **Paired `<b>` bold text ([#160](https://github.com/GenieClient/Genie5/issues/160))**
  — help text that uses HTML-style `<b>…</b>` for emphasis (e.g. `PROFILE HELP`)
  now renders bold instead of leaking the raw tags.
- **`#goto` while already walking ([#96](https://github.com/GenieClient/Genie5/issues/96))**
  — a new `#goto` now interrupts the walk in progress and starts the new one,
  instead of being silently rejected. Scripts that `matchwait` on the second
  `#goto` no longer hang forever.
- **Stream menu / Layout-tab desync** — a stream's "Show in Main Window" (and
  Time Stamp / Name List Only / Word Wrap) right-click checkmark now matches its
  saved setting after connect, instead of showing the built-in default.

# Genie 5 — v5.0.0-alpha.8.5

## ✨ New

- **Analytics window** — Window → Analytics opens a skill-history dashboard:

- **Analytics window** — Window → Analytics opens a skill-history dashboard:
  live **XP/hour** and per-skill gain bars for the current session, long-horizon
  **skill-gain curves** (7/30/90 days/All), and a **session list with
  compare-up-to-3** gain-curve overlay. History records locally as you play
  (own character's skill table only, never uploaded — see PRIVACY.md); a
  one-time notice at first connect explains the recording and offers to turn
  it off. Configure via `#config analytics` / `analyticsinterval` /
  `analyticsretentiondays`, or the panel's inline Record/retention controls.
  Raw snapshots older than the retention window (default 90 days) fold into
  permanent per-day rollups, so charts keep their history at ~1 KB/day.
- **Screen-reader support (first pass)** — the command input, hands strips,
  vitals bars, script bar, find bars, and the Connect / Configuration / import
  / updates dialogs now carry accessibility names readable by NVDA and
  Narrator; hand and prepared-spell changes announce politely. Windows has the
  fullest support; macOS is partial and Linux screen readers aren't supported
  by the UI framework yet — the built-in TTS (`#tts`) is the path there. See
  `docs/accessibility.md`.
- **Text-to-Speech settings tab** — Configuration → Text-to-Speech: master
  read-aloud switch, a per-stream grid of **read + priority** controls, voice
  picker with a **Test** button, and rate/volume sliders. All backed by the
  same settings.cfg keys as `#tts`, so commands and UI stay in sync.
- **`#tts priority`** — per-stream read-aloud urgency overrides
  (`#tts priority <stream> <low|normal|high|default>`, new
  `ttsstreampriority` config key). Defaults unchanged: whispers/deaths barge
  in, logons/atmospherics/familiar yield, everything else is normal.
- **Name-list highlight colours + `#names` command
  ([#154](https://github.com/GenieClient/Genie5/issues/154),
  [#148](https://github.com/GenieClient/Genie5/issues/148))** — a name rule's
  colour now actually paints on game and stream text (before, it only drove the
  Name-List-Only filter). New typed `#names` / `#name` family — add / remove /
  list / clear / save / load — so scripts and the command bar can manage the
  names list without the dialog.
- **`#preset` command ([#149](https://github.com/GenieClient/Genie5/issues/149))**
  — set a token's colour from the command line or a script: `#preset {id} {fg}
  [{bg}]` (known tokens only), plus list / save / load / reset. Preset overrides
  now persist across restarts.
- **Trigger `eval` + `matchall`
  ([#150](https://github.com/GenieClient/Genie5/issues/150),
  [#23](https://github.com/GenieClient/Genie5/issues/23))** — opt-in per rule.
  `eval` evaluates `{…}` expression blocks in the action (math / functions /
  variables) before it fires; `matchall` fires the action once per match on the
  line (each with its own `$1..$n`) instead of once for the first match. Both
  are keywords on `#trigger add` and checkboxes in the Triggers panel.
- **Experience window — Genie 4 layout
  ([#144](https://github.com/GenieClient/Genie5/issues/144))** — `#config
  experienceg4layout` drops the "Learning Skills" summary to a footer beneath
  the skill list (the classic EXPTracker look) instead of a header on top.
- **Help ▸ Changelog ([#155](https://github.com/GenieClient/Genie5/issues/155))**
  — read this build's release notes in-app, without opening the Releases page.
- **Genie 4 parity odds & ends
  ([#151](https://github.com/GenieClient/Genie5/issues/151))** — `#ignore` (an
  alias for `#gag`), an `ignorescriptwarnings` toggle to silence non-fatal
  script parse advisories, and the reserved script variables `$year` / `$month`
  / `$spellstarttime`.

## 🐛 Fixed

- **Name highlight colours never rendered
  ([#154](https://github.com/GenieClient/Genie5/issues/154))** — see above; name
  rules also now save and reload across launches (they weren't persisting at all).
- **Thoughts stream colour** — the Thoughts stream now shows its palette colour
  (Cyan by default) instead of the default foreground.

# Genie 5 — v5.0.0-alpha.8.4

Genie can now help improve its own parser: when the game sends an element it
doesn't recognize yet, a one-click prompt drafts a pre-redacted GitHub issue
for you to review and submit.

> **Alpha software.** Windows SmartScreen may warn on first launch
> (More info → Run anyway) while code signing is being rolled out — tracked
> in #33.

## ✨ New

- **Report parser gaps (#152)** — if DragonRealms ever sends an element Genie's
  parser doesn't handle yet, a slim notice appears above the vitals bar offering
  to report it. One click opens a **pre-filled GitHub issue** in your browser,
  with the sample **already redacted** (other players' speech removed) and your
  Genie version attached — nothing is posted until you review it and press
  Submit. A one-click way to help the parser keep pace as the game evolves; each
  unknown element only asks once per session.

# Genie 5 — v5.0.0-alpha.8.3

The Experience window catches up to Genie 3/4 — session rank-gain, numeric
mindstates, and your own highlights now colour it — alongside a Display
Settings Theme manager, three dock-window tools, and a batch of highlight
fixes.

> **Alpha software.** Windows SmartScreen may warn on first launch
> (More info → Run anyway) while code signing is being rolled out — tracked
> in #33.

## ✨ New

- **Experience window Genie 3/4 parity (#144)** — a **Track gain** checkbox
  shows the ranks each skill has gained this session plus a running session
  total; the **Numbers Only** and **Short Names** density stops now carry the
  mindstate as a number (the field you actually watch); your **highlight
  rules colour the panel**; and the header shows how many skills are
  learning, how many are mind-locked, and the elapsed session time.
- **Display Settings → Theme tab (#20)** — manage themes from one place:
  import and export theme JSON, duplicate a preset to tweak it, and delete
  the ones you don't use. The secondary dialogs (About, Connect, Updates, …)
  now follow the active theme too.
- **Dock-window Save As…, Find…, and Word Wrap (#120)** — right-click a text
  window to save its contents to a file, search within it (the same find bar
  the game window uses), or toggle word wrap — plus a fix so the window
  menu's Copy acts on the right window.

## 🐛 Fixes

- **Roundtime highlight (#145)** — the long form `Roundtime: 3 seconds.` now
  highlights the whole word, not just `sec`.
- **Highlight editing (#142)** — editing a saved highlight updates it in
  place instead of leaving the old entry behind and adding a duplicate.
- **Highlight priority (#143)** — your highlight rules now win over the
  built-in default colours (room titles, numbers), so a rule aimed at the
  room name or the EXP numbers takes effect. Genie 3/4 semantics.

## 🙏 Thanks

Saragos, for the #142 / #143 / #144 / #145 reports.

# Genie 5 — v5.0.0-alpha.8.2

Themes arrive: seven built-in looks (including Light, High Contrast, and
Solarized) with a live in-app theme editor — plus three community-requested
input features from Genie 3/4, spoken-alert upgrades, and a batch of
script-engine fixes from the mm_train review.

> **Alpha software.** Windows SmartScreen may warn on first launch
> (More info → Run anyway) while code signing is being rolled out — tracked
> in #33.

## ✨ New

- **UI Themes (#20, first wave)** — Edit → Theme picks from seven built-in
  presets: **Dark** (the classic), **Light**, **Genie 4 Classic**,
  **High Contrast**, **Solarized Dark**, **Solarized Light**, and
  **Wrayth-style**. The whole app repaints live — no restart.
- **Theme editor** — Edit → Theme → **Edit Theme…** opens a color-role
  editor (surfaces, text, accents, vitals bars, game text) with live
  preview while you drag; save your palette as a custom theme. Custom
  themes are shareable JSON files in `Config/Themes`. Your per-window and
  per-stream color overrides always win over the theme.
- **Type anywhere, it lands in the command bar (#141)** — typing while a
  panel, button, or the game text has focus routes straight into the input
  box, Genie 3/4 style. No more clicking back into the command bar.
- **The rest of the 10-key hotkeys (#140)** — numpad `/` `*` `-` `+` now
  fire `assess` / `health` / `fatigue` / `look` by default (rebindable in
  the Macros panel; existing profiles pick them up automatically unless
  you've bound those keys yourself).
- **`#flash` (#139)** — flashes the taskbar entry (Windows) or bounces the
  dock icon (macOS) until you refocus the window; classic trigger fodder
  for "something needs your eyes." No-op on Linux for now.
- **Time Tracker window** — the Elanthian clock/calendar is now a proper
  dockable panel with rebuilt date math.
- **`#statusbar` slots** — Genie 4 parity: ten positional slots rendered
  under the vitals status bar (plus `#statusbar clearall`), no longer
  squatting in the Script Bar.
- **Spoken alerts** — per-rule **Speak** on highlights and triggers, and
  `#tts rate` / `#tts volume` controls.

## 🔧 Fixed

- **mm_train-style menu scripts** — a batch of script-engine fixes:
  `#clear <name>` clears named windows, `#script abort` parity, doubled
  separators no longer produce phantom empty arguments, bare multi-word
  operands compare correctly, inline `{#eval …}` works in `#var`/`#tvar`,
  quoted `#echo ">window text"` routes correctly, and `triggeroninput`
  sees typed commands.
- **Plugin slash-commands** — plugin input dispatch is wired back into the
  command pipeline (`/iv` and friends reach plugins again).
- **Maps updater** — no more phantom "Updates available: Maps" every
  launch; applied zone versions are now tracked, so the banner only
  appears for genuinely new map data. (Existing installs will see one
  last update pass that records versions, then it goes quiet.)

# Genie 5 — v5.0.0-alpha.8.1

A portable-install follow-up driven by community reports: the executable is
now `Genie5.exe` everywhere, and Genie announces which data folder it is
using the moment it starts.

> **Alpha software.** Windows SmartScreen may warn on first launch
> (More info → Run anyway) while code signing is being rolled out — tracked
> in #33.

## ✨ New

- **`[data]` startup line** — the first line in the game window shows the
  data root Genie resolved and the mode it chose, e.g.
  `[data] root: D:\Genie 5 (portable)`. If a connect profile's Data
  Directory override repoints scripts/rules/layouts somewhere else, a second
  `[data] profile override: …` line says so. "Which folder is Genie actually
  reading?" is now answered at a glance (#138).

## 🔧 Changed

- **The executable is `Genie5.exe` on every platform (#137)** — the app exe,
  the process name in Task Manager, and the portable launcher now all say
  `Genie5`. Previously the portable zip's launcher was `Genie5.exe` but the
  app it started was `Genie.exe`, which made pinned icons and shortcuts tell
  a confusing story after auto-updates — and collided with Genie 4's own
  `Genie.exe` for players running both.

> **⚠️ One-time shortcut note (portable installs).** If you made a shortcut
> directly to `current\Genie.exe`, it stops working after this update — the
> file is now `current\Genie5.exe`. Re-point shortcuts at the root
> `Genie5.exe`, which survives every update. Start-menu shortcuts from the
> Setup install update themselves.

# Genie 5 — v5.0.0-alpha.8

The Genie 4 menu-parity milestone: the full menu audit closes with master
toggles, an Icon Bar, Update Settings, and a stack of muscle-memory items —
plus an Injuries panel, a Scripts updater, a Lich-attach fix, and a round of
scripting-language fixes from community reports.

> **Alpha software.** Windows SmartScreen may warn on first launch
> (More info → Run anyway) while code signing is being rolled out — tracked
> in #33.

## ✨ New

- **Master Toggles (File menu)** — turn whole rule engines on or off without
  touching the rules: Highlights, Triggers, Substitutes, Gags, Aliases, and
  Images. Rules stay loaded and editable while off; each toggle is also a
  `#config` key (`#config triggers off`) and the menu stays in sync either
  way. Images clears or re-fetches the room art live.
- **Icon Bar** — Genie 4's status strip returns as colour-coded chips below
  the vitals bar: your posture (dead / standing / kneeling / sitting / prone)
  plus STUNNED, BLEEDING, HIDDEN, INVISIBLE, WEBBED, JOINED — and two Genie 4
  never had: POISONED and DISEASED. Dims while disconnected. Layout ▸ Icon
  Bar to hide.
- **Injuries panel (#18)** — a dockable body silhouette showing per-region
  wounds and scars from the game's injury data, colour-coded by severity with
  a text list alongside. An opt-in auto-refresh (`#config injuriespoll N`)
  can poll `health` to refine the nervous-system reading while the panel is
  open.
- **Scripts updater** — the Updates dialog grows a Scripts tab: subscribe to
  GitHub script repositories and pull new/changed `.cmd`/`.js` files like a
  git pull, subfolders included; your local-only files are never touched.
  The community DR-Genie-Scripts repo ships as a ready-to-enable row.
- **Update Settings (Help menu)** — choose what the silent startup check
  covers (Core / Maps / Plugins / Scripts) and what it may install by itself.
  Auto-applied client updates install when you *close* Genie — never a
  mid-session restart. A quiet notice above the status bar reports "Updates
  available" / "Auto-updated" and opens the dialog on click.
- **Open Directory (File menu)** — jump to any Genie data folder: Data root,
  Config (profile-aware), Logs, Maps, Scripts, Plugins.
- **Menu parity round-up** — Auto Log checkbox (applies live mid-session),
  Edit ▸ Paste Multi Line, Layout ▸ Always on Top, Layout ▸ Align Input to
  Game Window (the command bar tracks the Game window's width), Layout ▸
  Magic Panels (hide the mana bar / cast bar / spell labels on a non-caster),
  and the room-art panel takes its Genie 4 name: **Portrait**.
- **PageUp / PageDown scrolling (#136)** — page the selected game window from
  the keyboard; Ctrl+PageUp/PageDown jump to top/bottom. Focus stays in the
  command bar, Genie 3/4 style.

## 🐛 Fixed

- **Lich attach shows your room and character (#126, #127)** — attaching to a
  running Lich session after Lich did the login left the Room panel and title
  bar empty; Genie now rebuilds both from the first `look` after attach.
- **Script `count()` counts occurrences (#134)** — Genie 4 semantics restored:
  `count("a|b|c","|")` is the separator count, so classic `0..count`
  inclusive loops over pipe lists work again.
- **`if` with an unset variable no longer eats the whole condition (#133)** —
  a missing operand (`(%unset = 1)` arriving as `( = 1)`) reads as the empty
  string, so the defined side of an `||` still decides the outcome.
- **Unbalanced-quote hint (#135)** — when a stray quote makes an `if` line
  unparseable, the "missing 'then'" warning now suggests the actual problem:
  `(unbalanced " quotes?)`.
- **Bad conditions warn instead of failing silently** — a condition that
  can't parse echoes a once-per-line `[script] … bad condition` notice
  (covers hung `waiteval` too) instead of silently evaluating false.
- **Open Scripts Folder** — no longer fails with "Location is not available"
  on some Windows setups; it now opens the folder the same way every other
  folder menu item does.

# Genie 5 — v5.0.0-alpha.7.11

Menu-script windows arrive — the Genie 4 named-window command family (`#log`,
`#link`, `#clear`, `#window`) — plus directed-echo routing fixes from a
community report, MonsterBold in the Room panel, and filter boxes on the
Configuration panels.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New

- **Named-window commands** — the Genie 4 menu-script toolkit now works:
  `#window add|open|show|close|hide|remove|clear "Name"` manages script-created
  dock windows, `#link [>window] {text} {command}` renders a clickable line that
  runs its command when clicked, `#clear [>window]` wipes a window in place, and
  `#log [>file] text` appends to a log file under your Logs directory (the bare
  form targets the per-character daily log, banner and all). Classic Genie 4
  menu scripts like `mm_train` run as-is.
- **MonsterBold in the Room panel** — the room objects line now golds creature
  and NPC names the same way the game and stream windows do, honouring the
  MonsterBold toggle and the `creatures` preset colour.
- **Configuration panel filters** — Aliases, Triggers, Highlight Strings,
  Substitutes and Gags each get a type-to-filter box, so a several-hundred-line
  trigger list is navigable again.
- **Per-stream "Also show in Main"** — each stream window (Combat, Talk,
  Whispers, …) has a Layout-tab toggle to additionally echo its lines into the
  main game window, Genie 4-style.
- **Script Bar debug readout** — each running-script chip shows the script's
  live `#debug` trace level (`dbg:N`).

## 🐛 Fixed

- **`#echo >Main` no longer vanishes** — echoing to `Main`/`Game` or to a
  built-in stream window (`>Combat`, `>Talk`, `>Thoughts`, …) now reaches that
  window; previously only `>Log`/`>ItemLog` worked and everything else was
  silently dropped. Colours are honoured, and non-text panels (`>Mapper`,
  `>Vitals`, …) fall back to Main instead of eating the text.
- **Junk `>Log` window** — an `#echo` target variable whose value already
  carried a chevron (`var w >Log` + `#echo >$w …`) manufactured a window
  literally named `>Log`; extra chevrons are now trimmed so it lands in Log.
- **Raw XML window font** — the Raw XML panel now honours the Layout-tab font
  instead of a hardcoded one.

# Genie 5 — v5.0.0-alpha.7.10

An Experience-window density control, Active Spells promoted to a proper window,
and a tidier `#config list`.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New

- **Experience density slider (#125)** — the Experience panel now has a **Density**
  slider that condenses each skill line to taste: **Full → No count → Numbers
  only → Short names → Brief**. The slider, the `#config experiencedensity`
  command, and `settings.cfg` all drive the one setting, and dragging re-renders
  the panel live without spamming the Game window.
- **Active Spells window (#112)** — Active Spells is now a first-class dock tool:
  it no longer springs back open after you close it, and it carries the standard
  window decorations in windowed / MDI mode.

## 🔧 Improved

- **`#config list` grouped by category** — the settings dump is now organised into
  labelled sections instead of one flat wall of keys, so related options sit
  together.

# Genie 5 — v5.0.0-alpha.7.9

A scripting-parity and readability release — three Genie 4 script-language fixes
from community reports, a `#goto` combat-retreat fix, and **MonsterBold**: DR's
creature and NPC names now stand out in colour.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New

- **MonsterBold (#131)** — the creature and NPC names (and combat messages) that
  DR marks as "monster bold" now render in a distinct colour — default **gold**,
  the traditional Wrayth / Genie 3-4 look — in the main window and every stream
  window, so mobiles pop out of a busy scroll. **On by default.** Toggle it live
  from **Config → Highlights → Presets** (the new *MonsterBold* checkbox) or with
  `#config monsterbold on|off`, and recolour it via the `creatures` preset. Note
  it bolds every mobile DR tags — friendly NPCs (guards, shopkeepers) as well as
  hostile creatures — exactly as Wrayth does.

## 🐛 Fixes

- **`#goto` retreats when engaged (#130)** — auto-walk and travel scripts driving
  movement with `#goto` would stall when a creature had you engaged at melee or
  pole range. The walker now retreats and retries the step, matching Genie 3/4.
- **Nested variables expand inside-out (#128)** — stacked references like
  `$%output` and `%harness%counter` now resolve the inner variable first
  (right-to-left), matching Genie 4, instead of leaving `$var1` / dropping the
  prefix.
- **`def()` sees `#var` variables (#129)** — `def(name)` / `defined(name)` now
  checks the persistent `#var` store and reports **existence** (Genie 4
  semantics), so a variable set with `#var` — even to an empty value — reads as
  defined.
- **`\;` escape in the command separator (#132)** — a backslash-escaped semicolon
  (and a `;` inside `"quotes"` or `{braces}`) no longer splits a command
  mid-value, so `#var t a\;b` stores the whole `a\;b` instead of truncating and
  sending the tail to the game.

# Genie 5 — v5.0.0-alpha.7.8

A travel-and-mapper polish release — smarter auto-walk routing, a movement
pacing fix, and two Mapper window annoyances put to rest.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New

- **Skill-aware travel routing (#122)** — auto-walk now weighs effort-heavy exits
  like swimming and climbing against your **Athletics** rank. A strong swimmer
  takes the water instead of waiting on a ferry; a weak one still routes around
  it. The single-zone and multi-zone pathfinders now score an edge identically,
  so routes stay consistent however far you travel.

## 🐛 Fixes

- **Travel pacing prefixes leaking to the game (#123)** — map movement could send
  a `slow`/`rt` pacing prefix to DR verbatim, which the game rejected with "Please
  rephrase that command." The prefix is now stripped before the move is sent.
- **One Mapper right-click menu** — right-clicking a room opened two overlapping
  menus (the room actions plus the window's Float / Close), and a second click
  could stack another copy. It's now a single menu: room actions grey out when you
  click empty space, Float / Close are folded in, and any previous menu closes
  first.
- **Duplicate Mapper window on layout change** — floating a tool (the default
  layout floats the Mapper) and then **Reset to Default Layout** — or toggling
  windowed mode — left the old floating window orphaned beside its rebuilt copy.
  The outgoing floating windows are now torn down on rebuild.

Under the hood this release also lays the groundwork for **cross-zone travel** (a
whole-Maps room index and automatic zone-link derivation). It isn't active yet —
single-zone travel is unchanged — but it's the foundation the next release builds
on.

# Genie 5 — v5.0.0-alpha.7.7

A community bug-fix and polish release — clickable news listings, clearer
disconnect feedback, an accurate mob count, and a Room window that wraps again in
windowed mode. Most of these came straight from issue reports; thanks to everyone
who filed them.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New

- **Clickable news listings (#30)** — DR's `news` listing arrives as plain text
  with no links, so the numbered items weren't clickable. Genie 5 now synthesizes
  a click target over each numbered line (e.g. `news 1 2`), so you can open an
  article straight from the list.
- **Disconnect feedback (#114)** — leaving the game is now unmissable: a
  timestamped `disconnected` line in the Game window (Genie 4 parity) plus an
  optional "Disconnected" popup. It's suppressed while auto-reconnecting, and the
  popup can be turned off under **Window → Disconnect Popup** (the line always
  shows regardless).

## 🐛 Fixes

- **Mob count for same-type creatures (#118)** — two creatures joined by "and"
  with no comma ("a giant viper and a giant viper") were collapsed into a single
  entry, throwing the count off. Each creature is now split and counted
  individually, so the Mobs panel and `$monstercount` are correct.
- **Room window wrapping in MDI (#124)** — in windowed/MDI mode the Room window
  stopped wrapping and ran off the edge. It wraps again. (Docked/tabbed mode was
  never affected.)

# Genie 5 — v5.0.0-alpha.7.6

The **Genie 4 script-language parity** release — a pass over the script
interpreter to match Genie 4 behaviour where Genie 5 silently diverged. Verified
against the community script corpus (Tirost DR-Genie-Scripts + EtherianDR, ~130
scripts) and locked behind a new unit-test project.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

Some of these are **behaviour changes** — a script written against the older
Genie 5 behaviour may need a tweak.

## ⚠️ Behaviour changes (Genie 4 parity)

- **`match(a, b)` is now exact, case-sensitive equality** — it previously behaved
  like a case-insensitive `contains` (substring).
- **String predicates are now case-sensitive** — `contains`, `instr`/`instring`,
  `startswith`, `endswith`. (Real scripts are unaffected: their needles already
  match the game text's case.)
- **`indexof` / `lastindexof` are now 1-based** — a hit returns its position
  starting at **1**, and **not-found returns 0** (was 0-based, with -1 for
  not-found). This makes the common `if !indexof(haystack, needle)` idiom
  ("needle absent") behave as it does in Genie 4.
- **`if_N` now means "at least N arguments were passed"** (`argcount >= N`), not
  "%N is set". `%argcount` / `$argcount` are now available, and `shift` keeps the
  count and rebuilds `%0`.

## ✨ New operators & functions (Genie 4 parity)

- Operators **`eq`** (≡ `=`) and **`<>`** (≡ `!=`).
- Function aliases **`instr`/`instring`** (boolean `contains`), **`substring`**
  (≡ `substr`), **`defined`** (≡ `def`).

## ⏸️ Deferred

- **The `do` command is not implemented, and is deferred indefinitely.** Genie 4's
  `do` re-sends a command until a response matches — but **no script in the
  community corpus uses it** (~130 scripts, including GenieHunter/hunt.cmd). A
  stray `do` line is now safely **ignored with a warning** instead of being sent
  to the game. It will only be built if a valid use case appears — **if you need
  `do`, please [open an issue](https://github.com/GenieClient/Genie5/issues)** so
  it can be prioritised.

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.7.5...v5.0.0-alpha.7.6

---

# Genie 5 — v5.0.0-alpha.7.5

The **Text-to-Speech** release — Genie can now *read the game aloud* with
offline neural voices: no cloud, no API key. Plus a `send` timing parity fix
and a `#goto` shorthand-matching fix.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New since alpha.7.4

- **Offline neural text-to-speech** — a new **`#speak <text>`** command plus a
  built-in **voice installer** that downloads high-quality neural voices which
  run entirely on your machine (no cloud, no API key). A streaming player +
  queue speaks lines in order without blocking the game.
- **Per-stream read-aloud** — pick which streams Genie reads aloud (e.g. speech,
  whispers, thoughts) so you hear the parts you care about and mute the rest.
- **Per-segment leading delay for `send` (#parity)** — `send` now honours a
  leading delay per segment, matching Genie 4's timing behaviour.

## 🐛 Fixes

- **`#goto` matches note-label and title shorthands (#115)** — `#goto <text>`
  now resolves against map note-labels and room-title shorthands by prefix, so
  a partial name jumps you to the right room.

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.7.4...v5.0.0-alpha.7.5

---

# Genie 5 — v5.0.0-alpha.7.4

The **Circle Calculator & Raw XML** release — a built-in guild circle calculator,
a live raw-XML stream inspector, more right-click window actions, and Genie 4
`#parse` parity.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New since alpha.7.3

- **Circle Calculator (#117)** — the Genie 4 circle calculator, built in.
  `/calc [guild] [circle]` works out how many ranks each skill still needs for
  your next circle (or a circle you name), auto-detecting your guild from `info`;
  `/sort [skillset|group] [rank]` lists your skills highest-rank first. Guild
  requirement tables ship built-in, and `$CircleCalc.Guild` sets a default guild.
- **Raw XML window (#14)** — a dockable, read-only live view of the raw server XML
  stream exactly as it arrives, before any tag stripping. Capped rolling buffer,
  auto-scroll, default hidden; reopen via **Window → Raw XML**. Handy for parser
  work and "where did that line come from?" debugging.
- **More window right-click actions (#13)** — the per-window menu gains **Copy
  All**, **Float / Re-dock**, and **Pause / Resume scrolling**, alongside the
  existing Clear / Time Stamp / Name List Only / Close.
- **`#parse` parity (#113)** — `#parse <text>` now feeds the line through your
  triggers and plugins (not just the script engine) and works typed from the
  command bar, matching Genie 4.
- **`#statusbar` / `#status` (#111)** — these now route to a dedicated Script Bar
  strip.

## 🐛 Fixes

- **Map labels no longer stack on rooms** — landmark labels are free-floating, so
  they keep their exact placement instead of snapping onto the nearest room cell
  on import/export.
- **Skill ranks populate from `exp all`** — running `exp all` now fills the skill
  store from the printed table (the per-skill push is empty for skills you aren't
  actively learning), so the pathfinder gets your ranks and the mapper's "fetch
  your skills" banner clears.

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.7.3...v5.0.0-alpha.7.4

---

# Genie 5 — v5.0.0-alpha.7.3

The **Windows & Maps** release — Genie 4-style per-window controls (a right-click
window menu with timestamps and a Name List Only filter), the window-chrome
settings reorganized under the Layout menu, map landmark labels, and a mapper
routing fix.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New since alpha.7.2

- **Per-window right-click menu (#90)** — right-click any window for the Genie 4
  window menu: **Clear**, **Time Stamp**, **Name List Only**, and **Close**. Each
  window shows only the items it actually supports.
- **Per-window timestamps (#90)** — turn on **Time Stamp** (from that right-click
  menu, or Configuration → Layout → Windows) and every new line in that window is
  prefixed with `[HH:mm:ss]`. Works on the main Game window and every stream/log
  window; existing scrollback isn't re-stamped, and bare prompts are left alone.
- **Name List Only** — filter a window down to just the lines that mention a name
  in your Names list (a clean arrivals / whispers feed). Remembered per window.
- **Window controls moved to the Layout menu** — Hands Strip, Roundtime / Hands
  position, Status Bar, Zone / Room ID, Windowed Mode, and Guild in Title Bar now
  live under **Layout** (alongside save/load arrangement); the **Window** menu is
  now just the panel show/hide list. The per-window settings in Configuration also
  moved under a new **Layout → Windows** sub-tab, matching Genie 4.
- **Map labels** — landmark text the map author placed (gate names, shop names,
  guild houses) is now imported, drawn on the map, and preserved on export.

## 🐛 Fixes

- **Skill / class / circle-gated routing now enforces (#95)** — the pathfinder
  reads your guild and circle from the live game (once you've run `info`), so a
  route is correctly steered around climbs, swims, or guild passages your
  character can't take. Previously that data was read once at startup and never
  refreshed, so the gates never took effect.

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.7.2...v5.0.0-alpha.7.3

---

# Genie 5 — v5.0.0-alpha.7.2

The **JavaScript libraries** release. Keep a library of JavaScript functions in a
`.js` file, `include` it from a `.cmd`, and call those functions with `js` /
`jscall` — the Genie 4 "array script" pattern — with the functions reading and
writing your `.cmd`'s variables.

> **Alpha software.** Builds are **unsigned** for most platforms — Windows
> SmartScreen will warn on first launch (More info → Run anyway). Signing is
> tracked in #33.

## ✨ New since alpha.7.1

- **Call JavaScript functions from a `.cmd` (#104)** — `include foo.js` loads a
  function library for the running script; **`js <expr>`** runs a function and
  **`jscall <var> <expr>`** stores its result in `%var`. The library reads and
  writes your script's variables through bare `getVar`/`setVar` (→ your `%vars`)
  and `getGlobal`/`setGlobal` (→ `$globals`) — ideal for list/array work that's
  awkward in plain `.cmd`.
- **Genie 4 `.js` libraries port cleanly** — `include` **auto-converts** the old
  `array.length()` (method-call) idiom to standard `array.length`, so existing
  Genie 4 array libraries load and run unchanged. (Genie 4 ran an older JavaScript
  engine; Genie 5's is current and spec-compliant.)
- **New docs** — a **JavaScript Scripting** wiki page covering both standalone
  `.js` scripts and the new function-library interop, with a sample for every call.

## ⬇️ Downloads

Grab the installer or portable build for your platform from the assets below:

| Platform | Installer | Portable |
|---|---|---|
| Windows | `01-Windows-Genie5-Setup.exe` | `01-Windows-Genie5-Portable.zip` |
| macOS (Apple Silicon) | `02-macOS-Apple-Silicon-Genie5.dmg` | `02-…-Portable.zip` |
| macOS (Intel) | `03-macOS-Intel-Genie5.dmg` | `03-…-Portable.zip` |
| Linux (x64) | `04-Linux-Genie5.AppImage` | — |

**Full changelog:** https://github.com/GenieClient/Genie5/compare/v5.0.0-alpha.7.1...v5.0.0-alpha.7.2

---

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
