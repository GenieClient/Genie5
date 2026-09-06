# Genie 5 — v5.0.0-beta.9

**"Nothing Lost"** — the dialogs DragonRealms has always sent finally have
somewhere to appear, the room's contents get a window of their own, a bad line
of game text can no longer stop your screen, and a damaged profile can no
longer lock you out.

## ✨ New
- **Server dialogs get real windows** — DragonRealms sends structured dialogs
  for things like paying off bank debt or choosing a spell. Genie has always
  received them and had nowhere to put them, so they were dropped on the floor.
  They now open as ordinary panels you can dock, float, or send to their own
  window.

  None of it is hand-written per dialog. The panel is built from whatever the
  server sends, and the arrangement is worked out from the coordinates in the
  message rather than pinning controls to fixed pixels — so a dialog laid out
  for a small fixed window reflows into whatever space your panel actually has.
  A control Genie doesn't recognise yet appears as a labelled placeholder
  rather than vanishing, so a gap is something you can see and report instead
  of silently missing.

  The first time a new dialog turns up you're asked once where it should live,
  and the answer is remembered for that character's profile. Buttons and edited
  values send the command the server supplied, filled in from the other fields
  as they read at the moment you click — not as they read when the dialog
  arrived.

  This is the first phase. The bespoke ones — the aim timer, injuries for other
  players — come later (#156).
- **Dialog text no longer leaks into your main window** — some dialog contents
  arrive as their own stream, tagged with the dialog they belong to. Genie
  didn't recognise the tag, so that text fell through into the game window as
  stray lines with no obvious source. It now goes to the dialog it belongs to,
  and successive updates build up there until the server clears it (#324).
- **Objects window** — what's on the ground in the room, one item per line,
  the way Genie 3/4 had it. Open it from Window → Objects; it docks beside
  Room alongside Mobs and Players, completing that trio. Creatures are left
  out by default, since the Mobs window already lists them — tick
  **Creatures** in the panel header, or `#config objectscreatures on`, to get
  the Genie 4 behaviour of listing everything after "You also see:" with
  creatures in the `creatures` colour. DragonRealms sends that line as plain
  prose with no per-item markup, so the split into rows reads the commas and
  the trailing "and"; an item whose own name ends in "and" plus an article
  can still land in the wrong place, and a screenshot of the room is enough
  to fix it (#329).
- **Window banners can be hidden** — the accent bar above a dock group,
  showing the active panel's name, repeats what the tab strip right below it
  already says. **View ▸ Window Banners** collapses it and gives the height
  to your text. It applies to all docked groups rather than one panel at a
  time, because the banner belongs to the group, not the window — the stream
  dock alone sits ten panels behind one bar, and a per-panel setting would
  make it appear and disappear as you switched tabs. Floating windows keep
  their own Hide Title Bar (#320).

## 🐛 Fixes
- **A single bad line can no longer stop your game text** — everything that
  consumes the game stream ran chained together, so the first consumer to hit
  an error abandoned the rest of the chain for that line. The part that puts
  text on your screen runs last, which meant one unlucky trigger action or
  parser corner could silently stop your output with nothing to explain it —
  and with the game thread turned off, take the whole session down with it.
  Each consumer is now isolated, so a failure costs you that one consumer's
  work on that one line instead of the line, or the session.
- **A damaged `profiles.json` can no longer lock you out** — if the file was
  interrupted mid-write, the client failed to start at all, with no message
  and no way back except finding and deleting the file by hand, which also
  destroyed every saved account and password. Profiles are now written
  through a temp file and swapped into place, so an interrupted write leaves
  the previous good copy intact; an unreadable file is set aside as
  `.corrupt` — keeping your encrypted passwords for recovery — and the client
  starts with an empty list rather than refusing to open.
- **Panels that re-opened into nothing** — Mobs, Players or any panel opened
  from the Window menu could tick its checkmark and never appear, leaving at
  most a sliver at the edge of the window. Drag every panel out of a column
  over enough sessions and the layout renormalizes that column to zero width,
  and a saved layout keeps the zero; anything re-opened into it was placed
  correctly and rendered invisible. This bit people who had never touched the
  panels in question — some layouts had been hiding Mobs and Players for
  weeks. Re-opening a panel now restores the column it lands in. If you were
  affected, save your layout once your panels are back to clear the old value
  for good (#331).
- **Long-running `.js` scripts stop hitting a phantom memory limit** — a
  script could abort with "memory limit (128 MB) exceeded" while holding
  almost nothing. The budget counted every byte a script had ever allocated
  rather than what it was actually using, so it worked out as a fixed
  allowance of total work — and hunt loops, the scripts meant to run for
  hours, reached it first. It now measures the same way the runaway-loop
  guard does, so a script that keeps its memory small can run indefinitely
  while a genuine memory bomb is still stopped (#330).
- **`element()` clamps out-of-range indices like Genie 4** — asking for an
  index past the end returned an empty string instead of the last element,
  and a negative index returned "" instead of counting back from the end.
  Genie 4 never reports out of range, and scripts are written expecting that;
  the difference turned up as comparisons against "" further down. Both are
  now matched. Parentheses are stripped from a list before it is split, the
  way Genie 4 does it, so `element("(a|b|c)", 0)` gives `a` rather than `(a`
  — that applies to `|` lists only, leaving a list you split on your own
  separator untouched (#323).
- **Update feeds can only write inside their own folder** — filenames coming
  from a maps or plugins feed were used as given, and a name containing a
  path could send its bytes somewhere else on disk. For the plugin feed that
  matters more than a stray file, because a downloaded plugin is then loaded.
  All three updaters now share one containment check: maps and plugins refuse
  any name carrying directory structure, scripts keep the nested paths a
  pulled repository legitimately needs, and both separators are treated as
  separators on every platform so Windows and Linux agree.

## 🔧 Under the hood
- **JavaScript engine updated** — Jint 4.3.0 → 4.16.1, thirteen releases of
  fixes for the engine behind `.js` scripts. The three guards that stand
  between a runaway script and the client — the time budget, the recursion
  limit and the memory cap — hook into engine internals that a version bump
  could quietly change without breaking the build, so each now has a test
  that actually runs it. All three behave identically on both versions (#289).

---

# Genie 5 — v5.0.0-beta.8.3

**"Steady Hands"** — a wedged script can no longer take the whole client down
with it, Genie 4's embedded JavaScript blocks run again, and the Master
Toggles menu finally does what its checkmarks say.

## ✨ New
- **Scripts can no longer freeze the client** — the entire game pipeline
  (reading, parsing, script ticks, triggers, plugins, commands) now runs on
  its own thread instead of sharing the interface's. A busy or stuck script
  stalls only the game side: typing, clicking, menus, and **Esc** /
  `#stopall` stay responsive, and a script wedged for more than five seconds
  is reported in a banner instead of hanging the window in silence. On by
  default — `#config gamethread off` restores the old behavior at the next
  launch if you need it (#251).
- **Embedded `<% … %>` JavaScript blocks in `.cmd` scripts** — the Genie 4
  pattern runs again instead of being sent to the game as text. A block can
  set a script variable that the very next line reads, and engine state
  carries across separate blocks in the same script. Matches Genie 4's rules:
  `<%` opens a block only at the start of a line, the block closes on the
  first line ending in `%>`, and an unterminated block at end of file still
  runs (#322).
- **Analyst captures now record what Genie sent** — captures only ever held
  the incoming stream, so a reply could never be tied to the command that
  caused it. Outbound commands are now interleaved as `[SENT]` lines in wire
  order. Anything sensitive is redacted before it reaches the file — account
  and password on `#connect` / `#lichconnect` / `#reconnect`, and your own
  speech — keeping *when* a command went out while dropping what it said.
  Movement, combat, and polling commands are recorded verbatim.

## 🐛 Fixes
- **Master Toggles do something again** — turning off Triggers (or
  Highlights, Substitutes, Gags, Aliases, Images) from the File menu ticked
  the checkmark and changed nothing. The engines and `#config triggers off`
  were always correct; the menu simply never wrote the change back. Twenty-
  three checkbox menu items were affected, including Auto Log, the Game
  Window view options, and the updater's check-on-startup and auto-apply
  settings.
- **Plugin `/commands` work from scripts, aliases and triggers** — a
  plugin's own command, such as `send /timers start BLESS`, went straight to
  DragonRealms, which answered "Please rephrase that command." A Genie 4
  plugin could not serve the legacy scripts and triggers that call its
  commands without rewriting every one of them. Client extensions now get
  first refusal and plugins get the command next — the same order typed
  input already used — everywhere a command can originate: `.cmd` scripts,
  alias expansions, trigger actions, quick-send segments, the `#send` queue,
  `move <cmd>`, JavaScript, and one plugin driving another. Slash commands
  nobody claims still go to the game as before, and ordinary commands are
  untouched (#325, #326).

---

# Genie 5 — v5.0.0-beta.8.2

**"Spyglass"** — every list panel in Configuration gets a live search box,
scrolled-back reading holds still under even heavier spam, and the
automapper learns another hidden-exit idiom.

## ✨ New
- **Find… boxes on every Configuration list panel** — Variables, Macros,
  Names, and Classes join the other rule panels with a live type-to-filter
  box. Variables searches values too, so typing `off` finds every disabled
  toggle. The whole filter family got hardened while we were in there:
  filters reset cleanly on profile switch and import, hiding the selected
  row clears the editor form instead of leaving it stale, a search keystroke
  no longer discards unsaved edits or a multi-row selection, and Variables'
  **Select All** tells you when a filter is hiding rows from the copy.
- **Automapper: named-object hidden exits** — map arcs like
  `objsearch outcropping climb handholds` now work: Genie searches the named
  object first, waits out the reveal, then takes the move (Genie 4
  `MOVE.OBJSEARCH` parity). Covers the Mistwood outcropping and friends.

## 🐛 Fixes
- **Scroll-hold holds under trimming again** — a regression let the view
  crawl one line at a time while you were scrolled back at the scrollback
  cap. The hold re-engages, and trims are now deferred entirely while
  you're reading — the buffer catches up the moment you return to the
  bottom (#293/#298 follow-up).
- **Macros: re-saving a macro keeps its class** — saving over an existing
  macro that belonged to a rule class no longer silently resets it to
  `default`.
- **Linux: floating windows get their title-bar menu features back** — the
  dock never marked floats as floating on Linux, so float-only items like
  Hide Title Bar didn't appear there.

---

# Genie 5 — v5.0.0-beta.8

**"Field Notes"** — Genie starts keeping a journal of the server's dialog
windows (and asks for your reports), shared config finally layers under each
character's own rules, and inactive tabs light up when something happens in
them. Behind the scenes, self-update is now verified end-to-end on all three
platforms — Windows, macOS, and Linux.

## ✨ New
- **Groundwork for server-driven dialog windows — help us map them!** DR can
  send fully-described UI windows (bank, store, feats, profile-edit, and
  more). Genie now captures every dialog the server sends: the first time an
  unknown one appears you'll see a one-time
  `[dialogs] new server dialog '<id>' captured` line, and its raw form lands
  in `Logs/dialog_journal.xml`. Two new commands: **`#dialogs`** lists the
  dialogs seen this session, and **`#dialogs report <id>`** drafts a
  pre-filled GitHub issue for one — nothing posts automatically; the draft
  opens in your browser (already redacted) for you to review and submit.
  **If you see a captured line during play, please report it** — every
  report directly shapes the dialog-window renderer coming in a future
  release (#156).
- **Shared config works for multi-character players: profile rules layer over
  the global set.** A character's rule files (highlights, triggers,
  substitutes, gags, aliases, macros, names, presets, classes, variables,
  window settings) used to *replace* the shared global set the moment they
  existed — and the first Configuration edit silently copied the whole global
  set into the profile, freezing it there. Now your character's rules apply
  first and every global rule you haven't overridden shows through; edits save
  back to the file each rule actually lives in, so the fork can't happen. Every
  rule editor gains a **Scope** field ("This character" / "All characters" —
  new rules default to this character), a Scope column, and shared-rule
  safeguards: deleting a global rule asks whether to disable it just for this
  character (a reversible local opt-out) or remove it for everyone, and the
  Toggle button on a shared rule quietly writes the local opt-out. Profiles
  forked by the old behaviour keep working identically — the global layer
  simply starts showing through for anything the fork doesn't cover, and
  hand-edits to either layer's files still live-reload (#257 — thanks
  @SaragosDR).
- **Inactive tabs blink when something happens in them.** A docked panel that
  receives new text while it's the hidden tab now blinks its tab title until
  you look — whispers landing in a backgrounded Whispers window no longer
  vanish silently. Every data-bearing panel participates (streams, trackers,
  Room, Mobs, Players, and friends), and a per-window **Flash on Activity**
  toggle (right-click title menu, or Configuration ▸ Layout) turns it off
  wherever you'd rather have quiet.

## 🐛 Fixes
- **SGE over TLS now works from Linux and macOS.** eaccess accepts exactly one
  TLS cipher suite, and .NET's non-Windows defaults don't offer it — so every
  Linux/macOS login silently fell back to plaintext port 7900. Genie now
  offers that suite on those platforms, and the 🔒 padlock shows up off
  Windows too (#316).
- **Linux AppImage runs on minimal distros.** ICU is now bundled into the
  linux-x64 build, so systems without `libicu` (fresh WSL2, slim containers,
  minimal installs) no longer crash at startup; the remaining X11 first-run
  packages are documented on the wiki (#314).
- **Built-in layout presets ship in the Windows installer.** The Strongbox and
  Shadowveil presets were missing from the Velopack package, so Layout ▸
  Load Preset had nothing to offer on a fresh install.
- **`#eval` with quoted arguments works in value position again.** The Genie 4
  composition `#var tmp #eval replacere("$1"," ","_")` — in a trigger action
  or typed directly — stored the literal `#eval replacere($1, ,_)` text
  instead of the evaluated result: command values were rebuilt from parsed
  tokens, which strips quotes, so the expression could no longer evaluate.
  A command's value is now taken from the raw command text (quotes intact),
  matching Genie 4; the braced form `#var x {#eval …}` behaves exactly as
  before. Also applies to `#tvar` and standalone `#eval`/`#evalmath` (#300 —
  thanks @alanpatton for the precise almanac-trigger repro).
- **"Float" in a docked window's title-bar menu works.** The menu item was
  visible on docked chromes but did nothing; it now pops the window out, same
  as dragging it free (#181).
- **Panel toolbars survive narrow windows.** The Inventory View, Script
  Manager, Analytics, and Raw XML header controls used to clip off the right
  edge when their panel got narrow; they now wrap onto extra rows so every
  button stays reachable at any width.

---

# Genie 5 — v5.0.0-beta.7

**"Steadfast"** — scrolled-back reading finally holds dead still under combat
spam, copy copies exactly what you highlighted, and the Experience window
learns the classic EXPTracker tricks. Every fix in this build was found or
verified in live play.

## ✨ New
- **Experience window: classic EXPTracker sorting, pulse echo, and rested EXP.**
  A new **Sort** dropdown on the Experience panel brings back Genie 4
  EXPTracker's orders — A to Z, Left to Right (Armor / Weapons / Magic /
  Survival / Lore, with the category order reorderable via `#config
  experiencesortorder`), and Learning Rate in either direction (`#config
  experiencesort`; the default is the order Genie 5 has always used). An
  **Echo** toggle flushes each experience pulse to the game window —
  `Learned: Athletics(+2), Perception(+1)` / `Pulsed: Evasion(-1)`
  (`#config experienceecho`). The echo is display-only by default; to also
  feed those lines to script triggers/actions like the classic plugin's parse
  leg, additionally set `#config experienceechoparse on` — it's a separate
  opt-in because combat scripts with broad match patterns can end up reacting
  to every pulse. A **Rested** toggle shows DR's rested-EXP
  stored / usable / cycle-refresh times under the panel summary, and the
  `$RestedEXP.Stored` / `.Usable` / `.Refresh` globals populate for scripts
  whether or not the line is shown (`#config experiencerested`). Thanks
  @Azothy for pinning down the classic sort modes from memory (#272).

## 🐛 Fixes
- **Per-window fonts finally apply to Experience, Active Spells, Time
  Tracker, and plugin windows.** Configuration ▸ Layout saved a font size or
  family for these four panels and reported it applied — but the panels never
  read the value back, so nothing changed on screen. They now follow the same
  rules as every other window, including applying immediately on **Apply**
  with no reconnect. Plugin windows also now appear in the Layout list once
  their plugin has opened them (their font settings currently last for the
  session). One small default change: these panels previously hard-rendered
  at 12px no matter what; they now share the standard 13px default like the
  rest of the dock. Thanks @SaragosDR for reporting it — twice, which is fair
  (#233, #292).
- **`$connected` no longer looks stuck at 1.** The live flag always flipped
  correctly on disconnect — but a leftover *saved* `connected` row in the
  variables file (imported from Genie 4, where any `#var connected …` quietly
  converts the reserved variable into a saved one, or typed here) sat in the
  Configuration ▸ Variables panel showing 1 forever, and answered a stale "1"
  to any script started before the session's first connect — exactly how a
  reconnect watcher runs. Reserved connection state can no longer enter the
  saved variable store (existing profiles clean themselves up at the next
  load + save), `#var connected …` now updates the live variable the way
  Genie 4's single list did, and `$connected` exists from launch as 0 —
  staying 0 while a connect attempt is still dialing, so a reconnect loop
  polling right after `#connect` can't read a false "connected" before the
  link is actually up. Thanks @Azothy for the precise echo-vs-panel repro
  (#294).

- **Scrolled-back windows hold still — and copy copies what you highlighted.**
  Scrolling up in the Game window (or any stream window) only held until the
  scrollback buffer reached its cap (2,000 lines by default,
  `#config scrollbacklines`): past that point every incoming line trims one
  off the top of the buffer, and the whole window slid up beneath a
  stationary scrollbar — in combat, a steady crawl that made reading back
  impossible even with the ↓ Bottom button showing. The view now anchors to
  the line you're reading whenever it isn't following the newest text (Pause
  Scrolling holds rock-steady too), and ↓ Bottom resumes the auto-follow as
  before. The same buffer slide was pulling text out from under a selection,
  so copying highlighted text pasted lines from further down; a selection now
  follows its text as the buffer trims — both the visible highlight and what
  Ctrl+C copies. Thanks @SaragosDR (#293) and @alanpatton, whose "it scrolls
  even when the bottom link appears" pinned it (#298).

- **`matchwait` no longer misses responses to a script's earlier `put`s.** The
  classic idiom — arm `match` patterns, `put look shard`, `put look`,
  `matchwait` — could fall through to its error label: Genie 5 pipelines
  script commands one-per-prompt (so scripts can't blast past roundtime), and
  the first put's response landed while the second put was still queued,
  before `matchwait` was listening. Genie 4 never had that window, because its
  `put` sends instantly. Lines that arrive during that internal queueing are
  now kept and checked the moment `matchwait` (or `waitfor`) starts listening
  — exactly what Genie 4 would have matched, and nothing more (text arriving
  during a script's own `pause`/`wait` is still ignored, as in Genie 4). Found
  with the astral-travel script `ap.cmd`, which exited "You are not at a known
  Grazhir shard" while standing at one. An action's `goto` firing mid-wait
  discards that held text along with the wait itself, and debug mode now
  reports which match fired and on what line (#309).
- **No more window twitch on `#statusbar` updates.** When a script cleared and
  rewrote its status slots (uber.cmd does this on every status change), the
  bottom slot row measured zero height for a frame and the whole window
  reflowed ~19px before snapping back — a visible full-screen twitch. The row
  now keeps its one-line height while its cells are momentarily empty, the
  same fix the posture chip strip got in beta.6 (#259), and both floors are
  now guarded by tests.
- **Overflowing tab strips are now scrollable — with visible arrows.** Stack
  more tabbed windows than the panel is wide and the extra tabs were simply
  clipped, with no indication they existed and no way to reach them short of
  an undocumented mouse-wheel scroll. Now ◀ ▶ arrows appear at the strip's
  edges exactly while tabs overflow (click or hold to scroll; the mouse wheel
  still works over the tabs), and activating a tab that sits past the edge —
  including at layout restore — scrolls its header into view automatically.
  Applies to docked panels, floated windows, and tabbed documents alike (#312).

## 🔧 Build
- **Delta updates can no longer be silently skipped.** The release pipeline's
  download of prior packages (the base for binary-diff delta updates) ran
  against the anonymous GitHub API and could hit its rate limit — which is how
  the beta.6 Intel-Mac lane quietly shipped without a delta, forcing a full
  download on update. The download is authenticated now, and any residual
  failure is flagged loudly on the run instead of passing unnoticed (#311).

---

# Genie 5 — v5.0.0-beta.6

Clockwork: the mapper tracks like it means it, every timer runs on the
server's clock instead of yours, and the script engine is hardened against
the crash that could take it down mid-hunt. Plus Alteration Buddy comes
in-house as a top-level menu, and the experimental editor renderer reaches
the Raw XML and Stream windows.

## ✨ New
- **Alterations.** Alteration Buddy is now built in, as a top-level
  **Alterations** menu rather than a plugin — design item alterations against
  the four length budgets, keep a shared library of designs, and import an
  existing `alterations.csv`. Finished designs can be marked done: they sort
  below your drafts, group separately in the Saved Designs menu, and nothing is
  auto-removed, so a completed alteration stays as a record. With thanks to
  Djordje, whose GPL-3.0 Alteration Buddy this is a port of, and to Bardolf for
  the completed-designs request.

- **Experimental editor renderer for the Raw XML and Stream windows.** The
  AvaloniaEdit-backed renderer that already sat behind `#config
  useeditorgamewindow` can now also drive the Raw XML window (`#config
  useeditorrawxmlwindow on`) and all twelve Stream windows (`#config
  useeditorstreamwindow on` — one switch for Logons, Talk, Whispers, Combat,
  and the rest). All three settings default off and take effect on restart;
  with them off, the classic renderer is exactly what it always was. Thanks
  @simtel12 (#296).

## 🐛 Fixes
- **Roundtime survives a wrong PC clock.** Roundtime, cast time, and spell-prep
  time arrive as absolute server timestamps, but Genie 5 compared them against
  your PC's clock — so a machine a minute or two off turned `roundtime 3` into
  a minutes-long stall (wedging `#send` and every RT-gated script), or read 0
  and let scripts fire straight into roundtime. Genie 5 now learns the server
  clock's offset from the game's own prompt stream and corrects every timer:
  the RT badge, `#send`, `$roundtime`, `$spelltime`, and `$casttimeremaining`
  are all true regardless of clock skew. Script-facing `$gametime`/`$casttime`/
  `$spellstarttime` stay raw server values, so existing script math is
  unchanged (#261).
- **Multi-command lines pause again: `put health;-0.05 encumbrance`.** The `-N`
  at the start of a `;`-chain segment is Genie 4's quick-send prefix — pause N
  seconds (plus any roundtime), then send the rest. Genie 5 was sending those
  segments to the game literally, which DR answered with "Please rephrase that
  command." All the places a chain can come from now honor it — typed commands,
  aliases, triggers, and script `put`/`send` lines — including Genie 4's glued
  form (`-3knock concealed door`) and the bare `-verb` form (send when
  roundtime clears). Also corrected: `send`'s `-N` previously fired with no
  wait at all; it now pauses like Genie 4. Thanks Azothy for the exact repro
  lines (#278).
- **Starting a script can no longer crash the script engine.** Starting a
  large script (the community automapper was the repro) at the exact moment
  game text was streaming in could throw "Collection was modified" and take
  the engine down — the engine was driven from two threads with almost nothing
  guarding its state. Every entry into the script engine is now serialized, so
  script starts, stops, pauses, and incoming game lines take their turn
  instead of colliding. Verified with the original replay repro (10/10 clean
  runs that crashed ~2 in 3 before) and five new concurrency tests (#242).
- **An action's `goto` now breaks a script out of `pause` and `matchwait`.**
  `action goto stats when ^report` firing while the script sat in a pause,
  matchwait, waitfor, or waiteval moved the program counter and nothing else —
  the script stayed parked forever. Genie 4 parity, verified against its
  source: an action-dispatched goto abandons the block (including match
  patterns armed before the matchwait) and resumes at the target label. A
  normal `goto` still clears nothing — registering matches and jumping to a
  shared `matchwait` label keeps working — and pausing a script from the
  script bar still wins until you resume it. Thanks to the reporters for the
  Genie 4 / Genie 5 side-by-side logs that pinned the behavior (#297).
- **The `automapper` setting now does something.** It was written by the
  toolbar toggle, by `#mapper record on`, and by your saved profile — and read
  by none of them, so record mode always started from the engine's own default,
  the toolbar toggle died with the session, and a saved preference never
  applied. Both it and `automapperalpha` (ghost-floor opacity) now apply live.

  **One behaviour change worth knowing about:** `automapper` was *declared*
  with a default of `true` while the engine actually defaulted to off. Wiring it
  up as declared would have silently switched map auto-recording on for
  everyone, where a single wrong room match can mutate an imported community
  map. **The default is now `false`**, matching how Genie 5 has actually
  behaved to date — so nothing changes for you unless you ask for it. The flip
  side: if your profile explicitly says `automapper = True` — including one
  imported from a Genie 4 `settings.cfg`, where the automapper *did* record as
  you walked — you will now genuinely start in record mode, which is what that
  line always meant. Set it to `false`, or use the toolbar toggle, if you'd
  rather it stayed off (#274, #275).
- **Map recording no longer strands a session's first room in a nameless zone.**
  With map recording on (`automapper`), the very first room of a session
  skipped the cross-map auto-detect and quietly seeded your character into an
  unnamed, unsaveable zone — even when a local map containing that exact room
  was sitting right there. It could also lose a race against the map index
  still building at connect, leaving "Waiting for zone index" up until you
  moved. Both fixed: the first room now gets the same auto-detect shot that
  lookup-only mode gets, and the index re-checks your room the moment it
  finishes building. One behaviour change: recording in a genuinely unmapped
  area now waits for you to pick "New Zone" (or for a map to match) instead of
  recording into an invisible zone you could never save. Thanks @simtel12
  (#295).
- **Identical-looking rooms each keep their own map node.** Corridors of rooms
  sharing one title, description, and exit set — the eleven "Segoltha River,
  Midstream" rooms are the community maps' worst case, and Leth Deriel's twin
  "Liyos Approach" rooms the subtlest — could freeze the map marker (and
  `$roomid`) mid-crossing, or collapse two adjacent twins onto a single node
  while recording. The mapper now uses DR's own room numbers as the "you
  moved" signal, and a room it knows to be a *different* server room can never
  steal a match on looks alone.
- **The Astral Plane no longer pins the map marker to your departure room.**
  Areas that send no room numbers at all (the astral is one) left the mapper
  holding the last number it ever saw, matching every pillar and conduit back
  to the room you entered from. A room change without a fresh room number now
  marks the old one stale — the `mapperdebug` trace shows `STALE` beside it —
  and the mapper falls back to matching by what it can actually see.
- **Map recording follows you through teleports instead of polluting your map.**
  With recording on, arriving somewhere you didn't walk — an astral exit, a
  portal, a ferry docking — used to stitch the destination into whatever map
  was loaded as a foreign room. Recording now only charts rooms you walked
  into (which is also exactly when it can draw the connecting arc); a teleport
  arrival instead asks the cross-map auto-detect first, so landing in a mapped
  area switches to that map and keeps recording there. Genuinely uncharted
  arrivals still get recorded — as a standalone room, with a status line
  saying so. No more toggling record off to travel.
- **Command history skips immediate repeats.** Submitting the same command
  several times in a row (`attack`, `attack`, `attack`) now records one
  recall-history entry instead of three, so Up-arrow reaches your earlier
  commands without paging through the repeats. Non-consecutive repeats are
  still recorded in full, and every submission still goes to the game
  unchanged. Thanks @simtel12 (#308).
- Corrected the settings reference: `automapperalpha` is ghost-floor opacity,
  not window opacity; the missing `automapperscript` row is documented; and
  `updatemapperscripts` is marked as not yet wired.

---

# Genie 5 — v5.0.0-beta.5.1

Bearings: a fix-focused follow-up to the .NET 10 release, built almost
entirely from player reports. The mapper knows where you are again, four
script behaviours match Genie 4 that quietly didn't, and out-of-character
chatter finally has a window of its own.

## ✨ New
- **OOC window.** DR sends out-of-character chatter on its own `ooc` stream,
  but Genie 5 had nowhere to put it, so it fell through into the main game
  window. There is now a dedicated **OOC** window — hidden by default, enable
  it from the Window menu. It follows DR's own declaration for the stream, so
  it doesn't echo to main and doesn't swallow lines meant for other windows.
  The `conversation` and `group` streams are still to come (#260).

## 🐛 Fixes
- **`$roomid` could read 0 for an entire session.** Browsing the map to another
  zone puts a hold on your displayed location so scripts don't get dragged
  along with the view — but on a session's first zone load that hold latched
  and never released, leaving `$roomid` at 0 for every script depending on it.
  The hold no longer latches on that first load, and it now also releases when
  a room match proves the zone you're browsing is the one you're standing in.
  Thanks @Azothy.
- **`$roundtime` was a frozen snapshot, not a countdown.** It reported the
  roundtime as of the moment it was set and never moved, so scripts gating on
  it waited on a number that would never reach zero. Thanks @Azothy.
- **`action` bodies can branch again.** `goto`, `gosub` and `exit` inside an
  `action` body had been dead engine-wide since mid-July, and surfaced as an
  "Index was out of range" script error rather than anything pointing at the
  cause. Thanks @Azothy.
- **`timer` and `%t` match Genie 4.** Four separate divergences, every one of
  which failed silently in scripts carried over from Genie 4: the elapsed time
  was readable only as `%timer` where Genie 4 names it `%t`; `timer start`
  after a stop restarted from zero instead of resuming; `timer stop` discarded
  the elapsed, so the standard `timer stop` / `echo %t` pair read `0`; and
  `timer setstart <datetime>` was rejected outright. One thing to know if you
  script against it: `%timer` now reports fractional seconds to match Genie 4,
  so numeric comparisons behave as before but an exact string match would see
  `12.346` where it saw `12`. Thanks @Azothy.
- **Literal angle brackets survive.** Text such as `<1-20>` was consumed as if
  it opened a tag and vanished from the window. A `<` now only opens a tag
  when what follows it is a legal tag start (#238 — thanks @paragonmac).
- **Outbound OOC whispers printed three times.** DR sends the same line on the
  `whispers`, `ooc` and `main` streams; the `ooc` copy landed between the other
  two and disarmed the duplicate check (#256).
- **MonsterBold no longer loses to your own highlights.** Creature names keep
  MonsterBold's colour even when one of your highlight rules matches the same
  line, while rule backgrounds still show through — and the `creatures` preset
  is now the single colour source for both the main window and the Mobs panel
  (#235, #236 — thanks @SaragosDR).

---

# Genie 5 — v5.0.0-beta.5

The long-haul release: Genie 5 now runs on **.NET 10**, the current
long-term-support runtime. Nothing changes in how you use it — the download
still bundles everything it needs — but the runtime inside is supported to
November 2028 instead of expiring this November.

## 🔧 Under the hood
- **Retargeted to .NET 10 (#234).** Genie 5 ships self-contained: the .NET
  runtime is baked into the download, so the version we build against is the
  version you run. .NET 8 reaches end of support on **10 November 2026**,
  after which it stops getting security patches — and a self-contained app
  would keep shipping that unpatched runtime. .NET 10 is the current LTS,
  supported through **14 November 2028**. There is nothing to install and
  nothing to configure; existing profiles, scripts, maps, plugins and layouts
  all carry over untouched. Thanks @simtel12 for the retarget.
- **Dependencies moved with it.** The logging packages follow the same
  support line (8.0.0 → 10.0.10), and Avalonia moves to 11.3.18. The
  Avalonia DataGrid stays pinned at 11.3.13 — its 11.3 line stops there, and
  unpinning it breaks the restore.
- **Building from source now needs the .NET 10 SDK.** Only relevant if you
  compile Genie yourself; see `docs/NET10_UPGRADE.md` for the details and
  the reasoning behind skipping .NET 9 and 11.
- **Plugin authors are unaffected.** The plugin contract is unchanged, and
  existing plugin DLLs keep loading.

## 🐛 Fixes
- **Injuries figure spacing tightened.** In the assembled-figure layout the
  thighs now meet the hips instead of floating below them, and the back and
  nervous-system views sit closer to the body rather than stranded off to
  the right.

---

# Genie 5 — v5.0.0-beta.4.g

The hand-off release: `#goto` walks the Genie 4 way again — when the community
automapper.cmd is installed, it drives the route and every special-move
directive works — plus spell timers now match Genie 4 to the second, and your
rule files reload live when you edit them on disk.

## ✨ New
- **`#goto` hands the walk to automapper.cmd (#226).** Genie 4's `#goto`
  never walked — it launched the community automapper.cmd, which is where
  special-move directives (`script ggbypass`, `ice nw`, `swim …`, timed
  waits) and the pacing globals (`$caravan`, `$powerwalk`, …) live. Genie 5
  now does the same: when the script is in your Scripts folder (or the
  repo-scripts folder), `#goto` and click-to-walk start it with the planned
  route as its arguments; without it, the built-in walker handles everything
  as before. `#config automapperscript false` forces the built-in walker,
  and a second `#goto` mid-walk restarts the script with the new route,
  matching Genie 4. Thanks @Azothy for the report and the field diagnosis.
- **Rule files reload live (#231).** External edits to the seven rule
  `.json` files — highlights, triggers, substitutes, gags, aliases,
  variables, classes — now apply to the running engines the moment the file
  is saved, no reconnect needed. The file on disk becomes the truth for its
  rule type; a torn or invalid file is rejected whole rather than clearing
  anything.

## 🐛 Fixes
- **Spell timers match Genie 4 (#224 follow-ups).** `$spellpreptime` no
  longer reads one second short (19 for a 20s prep), `$casttimeremaining` is
  a live countdown instead of a frozen snapshot, `$casttime` is the raw
  epoch scripts compose with, and the `<spelltime>` tag is parsed. Thanks
  @SaragosDR for the surgical repros.
- **Experience-window highlights stay in their lane (#232).** Highlight
  scoping no longer bleeds across the Experience window, and retokenizing a
  line no longer drops styled spans.
- **`#statusbar` slots size to their text.** Populated slots pack left and
  take only the width they need; empty slots take none. A single giant
  un-numbered line still spans the window and trims with an ellipsis instead
  of pushing later slots off screen.

## 🔧 Under the hood
- **Line endings normalized (#237).** A `.gitattributes` now pins LF across
  the repo, ending the CRLF churn in diffs. Thanks @simtel12.
- **Replay harness can smoke-run real scripts.** An env-gated hook starts a
  community `.cmd` through the fully-wired script engine during a replay —
  it's how the automapper hand-off was validated against the real
  2,500-line script before shipping.

---

# Genie 5 — v5.0.0-beta.4.f

Script-engine parity weekend: both of Saturday's script bug reports fixed —
nested `if` blocks and mid-name variable composition — plus type-anywhere
grows up: editing and history keys now reach the command bar from anywhere,
straight from a community PR.

## ✨ New
- **Backspace, Delete, Enter, and the arrow keys reach the command bar from
  anywhere.** The #141 type-anywhere redirect was Backspace-only; now the
  full editing/history set forwards to the command bar when no text control
  has focus — Up/Down recall history and Enter submits with the Game window
  focused, exactly like Genie 4. Along the way this fixes two silent
  AvaloniaEdit quirks that were eating Delete and the arrows in the Game
  window. PR #227 by @simtel12. 🙏

## 🐛 Fixes
- **Nested `if … then {` blocks skip correctly (#228).** The brace matcher
  only counted a `{` sitting alone on its line, so a nested `if … then {`
  block's closing brace was paired with the *outer* if — a false outer
  condition jumped straight into its own body and ran the tail lines (the
  reported "get my mallet" firing while already holding the mallet). Headers
  that open a block inline now count as openers, so depth tracking survives
  arbitrary nesting — covered by eight new engine tests including the exact
  reported script. Thanks @digitalnyc1 — the debug trace in the report made
  this a same-day find.
- **Variables mid-name compose again (#225).** A July guard against
  undefined names being eaten mid-word (`$Outdoorsmanship.Ranks` matching
  the compass `$out`) also outlawed Genie 4's numbered-variable idiom:
  `%spell%countermana` must resolve `%counter` mid-name to form
  `%spell1mana`. The rule now mirrors what Genie 4's case-sensitive lookup
  would do — an exact-case match may break mid-word, a case-insensitive-only
  match may not — so both behaviors hold at once. The reporter's
  three-position loop script runs verbatim in the test suite, asserting the
  exact Genie 4 output. Thanks @SaragosDR for the surgical repro.

## 🔧 Under the hood
- **Genie.App gets its own test project (#223).** First App-layer harness,
  covering the full stream-routing matrix (`EchoToMain` × panel visibility ×
  `IfClosed`) that PR #222 fixed — the double-add regression now has a
  permanent net. PR by @simtel12, and it runs in CI on every push. 🙏

---

# Genie 5 — v5.0.0-beta.4.e

The community batch: everything in this release came straight from player
reports, pull requests, and DM'd field findings — most of it filed and fixed
the same day.

## ✨ New
- **Repo scripts get their own directory (#221).** "Update Scripts" now
  pulls the community script repo into a separate directory
  (`#config reposcriptdir`, also on the Scripts panel) instead of writing
  into your script folder. Your locally edited scripts always win — the
  script engine searches your primary directory first and falls back to the
  repo copy, so an update can never clobber a script you've customized.
  Thanks @Azothy for the report and the design conversation.

## 🐛 Fixes
- **Talk and whisper lines no longer double in Main and Log.** DR sends
  every say/whisper twice on the wire — once on its own stream, once as a
  bare main-window copy. The parser now tags the duplicate instead of
  dropping it, so windows show each line exactly once while scripts,
  triggers, and plugins still see the wire exactly as DR sent it — including
  ParseGameOnly trigger parity in both modes. PR #222 by @simtel12, with a
  new Genie.App test harness following in #223. 🙏
- **`action … when eval` conditions evaluate for real (#224).** Variables
  inside a `when eval` expression were never substituted, so the condition
  was silently false forever and the action never fired. The expression now
  runs through full variable substitution at fire time. Thanks @SaragosDR —
  the report exposed it.

## 🙏 Today's community
Five releases into the external-testing round, today was the community's
day: @simtel12 (PR #222, the #223 test harness, and the XML coverage
reports behind this morning's parser fix), @SaragosDR (#224 and more sharp
reports in the queue), @Azothy (#221 and the automapper field reports),
@digitalnyc1 (#219, shipped this morning), and everyone testing the betas,
DMing traces, and talking through designs in Discord. Filed-to-fixed in a
day only happens because the reports are this good.

---

# Genie 5 — v5.0.0-beta.4.d

Window-management polish, headlined by built-in layout presets: **Strongbox**
(the new default) and **Shadowveil** ship with every install, the docking
engine collapses emptied columns so the Game window can reclaim the full
width, and the `#statusbar` slot row matches Genie 4's StatusStrip geometry.
Plus a command-line safety net and an XML-coverage fix.

## ✨ New
- **Built-in layout presets — Strongbox & Shadowveil.** Two curated layouts
  now ship with every install and appear in the Layout menu (marked
  built-in). **Strongbox** is the default for fresh profiles; **Shadowveil**
  is the darker alternative. Built-ins can't be destroyed — saving over one
  creates your own editable copy.
- **Experience window — right-click "Show Config Bar."** Toggle the settings
  strip on the Experience window from its right-click menu (persisted as
  `#config experienceconfigbar`).
- **Warning on unexpanded variables in outgoing commands.** A command about
  to reach the game with a raw `$var`/`%var` still in it (the classic
  "What were you referring to?" cause) now draws a warning first. The
  command still goes out — silence the warning with
  `#config warnrawvars false`.

## 🐛 Fixes
- **Docking: emptied columns collapse.** Closing or moving the last panel
  out of a side column lets the Game window expand all the way into that
  space (#219 — thanks @digitalnyc1 for the report).
- **`#statusbar` slot row uses Genie 4 StatusStrip geometry.** Slot 1
  springs to fill the row and slots 2–10 autosize (hiding when empty), so
  an un-numbered `#statusbar` line is no longer clipped to a tenth of the
  bar.
- **`<clearDynaStream>` is consumed.** The dynamic-stream variant of
  `<clearStream>` now clears its window (e.g. spellInfo) instead of
  registering as an unhandled element (#220 — thanks @simtel12).

---

# Genie 5 — v5.0.0-beta.4.c

A fast-follow fix bundle for the external-testing round: script-set timer
variables compute correctly again (the combat-script escape-timer bug),
typed commands expand `$variables` with full Genie 4 parity, `#window show`
reaches the real built-in panels, and the mapper's zone browsing gets three
follow-up fixes.

## 🐛 Fixes
- **Script `put #tvar` with a braced eval stores the result.** The combat-
  script idiom `put #tvar Combat.Last {#evalmath ($gametime - $Combat.Time)}`
  stored the literal `{#evalmath (…)}` text instead of the number, so a later
  `if ($Combat.Last > $MaxTrain)` hit a parse error and always read false —
  escape/back-training timers built on it never fired. Script-issued `#tvar`
  now runs the same value pipeline as a typed one (Genie 4 parity) and
  registers the name for `#tvar save`.
- **Typed commands expand `$variables` with full Genie 4 parity.** The
  command bar and forwarded `#` commands resolve `$vars` through the same
  rules as script text — including the shrink-search for dotted names and
  the reserved clock variables (`$date`, `$time`, …) — so rank-gain echoes
  and other `$var`-bearing commands display real values.
- **`#window show` on a built-in panel operates on the real tool.**
  `#window show Assess` used to spawn an empty duplicate panel while the
  game text went to the real one — and reserved names (Log, Familiar,
  Death…) could never be reopened by a script at all. add/open/show now
  reveal the built-in panel, close/hide/remove hide it, and clear empties
  the log buffers. Only Main stays script-untouchable.
- **Echo to a window named by an unresolved `$variable` routes to Main** —
  a typo like `#echo >$Log text` no longer manufactures a phantom panel
  named after the raw `$var` text.
- **Mapper zone-browsing follow-ups.** Browsing is now decided by zone
  identity instead of room-match probing, so clicking a neighbouring map
  that carries a boundary stub of your current room no longer bounces the
  view through three maps; the browse hold engages *before* the zone loads
  (no more stale `$zoneid`/`$roomid` snapshot on the first click); and the
  hold releases the moment your character actually moves.
- **Bare-Alt menu arming countered at the menu itself** — a follow-up to
  the beta.4.b fix, covering Alt-combination keybinds that could still arm
  the menu bar and swallow the next keystroke.

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
