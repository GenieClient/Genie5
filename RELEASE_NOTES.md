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
