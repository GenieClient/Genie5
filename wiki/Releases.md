# Releases & Changelog

Where to get Genie 5 and what changed in each build. Downloads live on the [Releases page](https://github.com/GenieClient/Genie5/releases); the [latest release](https://github.com/GenieClient/Genie5/releases/latest) is always the one to grab. For how to install each download, see [Installation](Installation); for staying current after that, [Keeping Up to Date](Updates).

> Genie 5 is now in **beta**. Versions are tagged `v5.0.0-beta.N` (earlier builds were `v5.0.0-alpha.N`). **Windows** release binaries are **EV code-signed** under **Shadow Realms LLC**, the project's support partner; **macOS and Linux** builds are unsigned for now and show a first-launch warning — see [Installation](Installation#platform-first-launch-notes).

## Latest: v5.0.0-beta.9 — Nothing Lost

The dialogs DragonRealms has always sent finally have somewhere to appear, the room's contents get a window of their own, and a bad line of game text can no longer stop your screen.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.9** as a delta from beta.8.3.

- **Server dialogs get real windows** — DragonRealms sends structured dialogs for things like paying off bank debt or choosing a spell. Genie has always received them and had nowhere to put them, so they were dropped. They now open as ordinary panels you can dock, float, or send to their own window, built from whatever controls the server sent rather than from per-dialog code. The arrangement is worked out from the coordinates in the message instead of fixed pixels, so a dialog laid out for a small fixed window reflows into whatever space your panel has; a control Genie doesn't recognise yet appears as a labelled placeholder rather than vanishing. Where each dialog lives is remembered per character profile, asked once the first time it appears. This is the first phase — the bespoke ones, the aim timer and injuries for other players, come later (#156).
- **Dialog text no longer leaks into your main window** — some dialog contents arrive as their own stream, tagged with the dialog they belong to. Genie didn't recognise the tag, so that text fell through into the game window as stray lines with no obvious source. It now goes to the dialog it belongs to (#324).
- **Objects window** — what's on the ground in the room, one item per line, the way Genie 3/4 had it. Open it from Window → Objects; it docks beside Room alongside Mobs and Players. Creatures are left out by default since the Mobs window already lists them — tick **Creatures** in the panel header, or `#config objectscreatures on`, for the Genie 4 behaviour (#329).
- **Window banners can be hidden** — the accent bar above a dock group repeats what the tab strip below it already says. **View ▸ Window Banners** collapses it and gives the height to your text (#320).
- **A single bad line can no longer stop your game text** — everything consuming the game stream ran chained together, so the first consumer to hit an error abandoned the rest for that line. Each is now isolated, so a failure costs that one consumer's work on that one line instead of the line, or the session.
- **A damaged `profiles.json` can no longer lock you out** — an interrupted write made the client fail to start with no message. Profiles are now written through a temp file and swapped into place; an unreadable file is set aside as `.corrupt`, keeping your encrypted passwords for recovery, and the client starts with an empty list.
- **Panels that re-opened into nothing** — Mobs, Players or any panel from the Window menu could tick its checkmark and never appear. A saved layout kept a zero-width column, and anything re-opened into it was placed correctly and rendered invisible; some layouts had been hiding Mobs and Players for weeks (#331).
- **Long-running `.js` scripts stop hitting a phantom memory limit** — the budget counted every byte a script had ever allocated rather than what it was using, so hunt loops reached it first. It now measures live usage (#330).
- **`waiteval` follows a variable that changes while it waits** — a condition on a value that moved after the wait armed never unblocked, so a recovery block could park forever. The expression is kept as written and re-read on each check (#332).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.9)

## v5.0.0-beta.8.3 — Steady Hands

A wedged script can no longer take the whole client down with it, Genie 4's embedded JavaScript blocks run again, and a plugin's `/commands` work no matter where the command came from.

- **Plugin `/commands` work from scripts, aliases and triggers** — a plugin's own command, such as `send /timers start BLESS`, was reaching DragonRealms instead of the plugin, which answered "Please rephrase that command." Client extensions now get first refusal and plugins get the command next — the same order typed input already used — everywhere a command can originate: `.cmd` scripts, alias expansions, trigger actions, quick-send segments, the `#send` queue, `move <cmd>`, JavaScript, and one plugin driving another. Slash commands nobody claims still go to the game, and ordinary commands are untouched (#325, #326).
- **Embedded `<% … %>` JavaScript blocks in `.cmd` scripts** — the Genie 4 pattern runs again instead of being sent to the game as text. A block can set a script variable that the very next line reads, and engine state carries across blocks in the same script (#322).
- **Scripts can no longer freeze the client** — the whole game pipeline (reading, parsing, script ticks, triggers, plugins, commands) now runs on its own thread. A busy or stuck script stalls only the game side: typing, clicking, menus, and **Esc** / `#stopall` stay responsive, and a script wedged for more than five seconds is reported in a banner instead of hanging the window. On by default; `#config gamethread off` restores the previous behaviour at the next launch (#251).
- **Analyst captures record what Genie sent** — outbound commands are interleaved as `[SENT]` lines in wire order, so a reply can be matched to the command that caused it. Account and password on `#connect` / `#lichconnect` / `#reconnect`, and your own speech, are redacted before they reach the file.
- **Master Toggles do something again** — turning off Triggers, Highlights, Substitutes, Gags, Aliases or Images from the File menu ticked the checkmark and changed nothing; twenty-three checkbox menu items were affected, including Auto Log and the updater's check-on-startup and auto-apply settings.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.8.3)

## v5.0.0-beta.8.2 — Spyglass

Every list panel in Configuration gets a live search box, scrolled-back reading holds still under even heavier spam, and the automapper learns another hidden-exit idiom.

- **Find… boxes on every Configuration list panel** — Variables, Macros, Names, and Classes join the other rule panels with a live type-to-filter box; Variables searches values too, so typing `off` finds every disabled toggle. The whole filter family got hardened: filters reset cleanly on profile switch and import, hiding the selected row clears the editor form, a search keystroke no longer discards unsaved edits or a multi-row selection, and Variables' Select All says when a filter is hiding rows from the copy.
- **Automapper: named-object hidden exits** — map arcs like `objsearch outcropping climb handholds` now search the named object first, wait out the reveal, then take the move (Genie 4 `MOVE.OBJSEARCH` parity).
- **Scroll-hold holds under trimming again** — a regression let the view crawl while scrolled back at the scrollback cap; the hold re-engages, and trims are deferred entirely while you're reading (#293/#298 follow-up).
- **Macros: re-saving a macro keeps its class**, and **Linux floating windows get their title-bar menu features back**.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.8.2)

## v5.0.0-beta.8 — Field Notes

Genie starts keeping a journal of the server's dialog windows — and asks for your reports. Shared config finally layers under each character's own rules, inactive tabs light up when something happens in them, and self-update is now verified end-to-end on Windows, macOS, and Linux.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.8** as a delta from beta.7.

- **Server-dialog capture groundwork — help us map them!** The first time an unknown server dialog appears you'll see a one-time `[dialogs] new server dialog '<id>' captured` line, and its raw form lands in `Logs/dialog_journal.xml`. **`#dialogs`** lists what you've seen this session; **`#dialogs report <id>`** opens a pre-filled GitHub issue draft in your browser (already redacted — nothing posts automatically). Every report directly shapes the dialog-window renderer coming in a future release (#156).
- **Profile rules layer over the shared global set** — a character's rule files used to *replace* the global set the moment they existed. Now the character's rules apply first and every global rule you haven't overridden shows through; every rule editor gains a **Scope** field ("This character" / "All characters"), and deleting or toggling a shared rule offers a reversible per-character opt-out instead of changing it for everyone (#257 — thanks @SaragosDR).
- **Inactive tabs blink on activity** — a backgrounded panel that receives new text blinks its tab title until you look; a per-window **Flash on Activity** toggle (right-click title menu, or Configuration ▸ Layout) turns it off where you'd rather have quiet.
- **SGE over TLS works from Linux and macOS** — eaccess accepts exactly one TLS cipher suite and .NET's non-Windows defaults don't offer it, so those logins silently fell back to plaintext; the 🔒 padlock now shows up off Windows too (#316).
- **Linux AppImage runs on minimal distros** — ICU is bundled into the linux-x64 build, so systems without `libicu` no longer crash at startup (#314; see [Installation](Installation#platform-first-launch-notes) for the X11 first-run packages).
- **Built-in layout presets ship in the Windows installer** — Strongbox and Shadowveil were missing from the installed package, so Layout ▸ Load Preset had nothing to offer on a fresh install.
- **`#eval` with quoted arguments works in value position again** — `#var tmp #eval replacere("$1"," ","_")` stores the evaluated result instead of the literal expression text, matching Genie 4; also applies to `#tvar` and standalone `#eval`/`#evalmath` (#300 — thanks @alanpatton).
- **"Float" works from a docked window's title-bar menu** (#181), and the **Inventory View / Script Manager / Analytics / Raw XML toolbars wrap** at narrow widths instead of clipping off the edge.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.8)

## v5.0.0-beta.7 — Steadfast

Scrolled-back reading finally holds dead still under combat spam — Pause Scrolling included — and copy copies exactly what you highlighted. The Experience window learns the classic EXPTracker sort, echo, and rested-EXP options, and a batch of live-play fixes lands with it.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.7** as a delta from beta.6.

- **Scrollback holds still — and copy is honest** — past the scrollback cap (`#config scrollbacklines`), every incoming line used to slide the whole window up under a stationary scrollbar, and drag a selection's text out from under it. The view now stays glued to the line you're reading (Pause Scrolling too), and Ctrl+C copies exactly what you highlighted (#293, #298 — thanks @SaragosDR and @alanpatton).
- **Experience window: classic EXPTracker options** — a Sort dropdown (A to Z / Left to Right with reorderable category order / Learning Rate both ways), a pulse Echo (`Learned: Athletics(+2)` lines each prompt — display-only by default; `#config experienceechoparse` feeds them to triggers like the classic parse leg), and a Rested toggle with `$RestedEXP.*` globals (#272 — thanks @Azothy).
- **`matchwait` no longer misses responses to a script's earlier `put`s** — lines arriving while a command is queued behind the one-per-prompt send gate are replayed the moment `matchwait`/`waitfor` starts listening, matching Genie 4 timing (#309).
- **Per-window fonts apply to the tracker panels** — Experience, Active Spells, Time Tracker, and plugin windows now honour Configuration ▸ Layout font settings, live on Apply (#233, #292 — thanks @SaragosDR).
- **`$connected` no longer looks stuck at 1** — leftover saved `connected` rows purge themselves, `#var connected` writes the live flag, and `$connected` is 0 from launch until a connect actually completes (#294 — thanks @Azothy).
- **Tab-strip overflow arrows** — ◀ ▶ appear exactly while tabs overflow, and activating an off-screen tab scrolls its header into view (#312).
- **No window twitch on `#statusbar` updates** — the slot row keeps its height through clear-then-rewrite cycles.
- **Delta updates can't be silently skipped** — the release pipeline authenticates its delta-base download; the rate-limit failure that shipped beta.6's Intel-Mac lane full-only can't recur quietly (#311).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.7)

## v5.0.0-beta.6 — Clockwork

The mapper tracks like it means it, every timer runs on the server's clock instead of yours, and the script engine is hardened against the crash that could take it down mid-hunt. Plus Alteration Buddy comes in-house, and the experimental editor renderer reaches the Raw XML and Stream windows.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.6** as a delta from beta.5.1.

- **Alterations menu** — Alteration Buddy built in: design against the four length budgets, keep a library, import your `alterations.csv`, and mark finished designs done (thanks Djordje for the original GPL tool, and Bardolf for the completed-designs request).
- **Roundtime survives a wrong PC clock** — RT, cast, and spell-prep timers now correct for your clock's skew against the server, so `roundtime 3` is 3 seconds even when Windows drifts; `#send` and RT-gated scripts pace true (#261).
- **Script engine hardened** — starting a big script while game text streamed in could crash the engine ("Collection was modified"); every entry is now serialized (#242).
- **An action's `goto` breaks out of `pause`/`matchwait`** — Genie 4 parity for the stalled-script repro (#297).
- **Quick-send pauses again** — `;-0.05 command` chain segments pause like Genie 4 instead of going out literally (#278 — thanks @Azothy).
- **Mapper tracking batch** — identical-looking rooms each keep their node (Segoltha's eleven midstream rooms), the Astral Plane no longer pins the marker to your departure room, and record mode follows teleports instead of stitching foreign rooms into your map.
- **`automapper` setting works** — and its default is now genuinely `false`; a profile that says `True` really starts in record mode now (#274, #275).
- **Record mode finds your map on a session's first room** — no more phantom "(unsaved)" zone when a community map already covers where you're standing (#295 — thanks @simtel12).
- **Experimental editor renderer for Raw XML + Stream windows** — `#config useeditorrawxmlwindow` / `useeditorstreamwindow`, default off (#296 — thanks @simtel12).
- **Command history skips immediate repeats** — Up-arrow reaches earlier commands without paging through `attack`×3 (#308 — thanks @simtel12).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.6)

## v5.0.0-beta.5.1 — Bearings

A fix-focused follow-up to the .NET 10 release, built almost entirely from player reports. The mapper knows where you are again, four script behaviours match Genie 4 that quietly didn't, and out-of-character chatter finally has a window of its own.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.5.1** as a delta from beta.5.

- **OOC window** — DR's `ooc` stream had nowhere to land, so out-of-character chatter fell into the main game window. There's now a dedicated OOC window, hidden by default; enable it from the Window menu. The `conversation` and `group` streams are still to come (#260).
- **`$roomid` could read 0 for a whole session** — the map-browsing hold latched on a session's first zone load and never released. It no longer latches there, and now also releases when a room match proves you're standing in the zone you're browsing (thanks @Azothy).
- **`$roundtime` counts down** — it was a frozen snapshot, so scripts gating on it waited on a number that never reached zero (thanks @Azothy).
- **`action` bodies can branch again** — `goto`/`gosub`/`exit` inside an `action` had been dead engine-wide since mid-July, surfacing as an "Index was out of range" error (thanks @Azothy).
- **`timer` and `%t` match Genie 4** — `%t` now resolves, `timer start` resumes after a stop, `timer stop` keeps the elapsed, and `timer setstart` works. `%timer` now reports fractional seconds like Genie 4, so an exact string match on it would see `12.346` where it saw `12` (thanks @Azothy).
- **Literal angle brackets survive** — text like `<1-20>` was consumed as a tag and vanished (#238 — thanks @paragonmac).
- **Outbound OOC whispers printed three times** — the `ooc` copy of DR's triple-send disarmed the duplicate check (#256).
- **MonsterBold no longer loses to your own highlights** — creature names keep their colour when a rule matches the same line, and the `creatures` preset is the one colour source for both the main window and the Mobs panel (#235, #236 — thanks @SaragosDR).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.5.1)

## v5.0.0-beta.5 — Long Haul

Genie 5 now runs on **.NET 10**, the current long-term-support runtime. Nothing changes in how you use it — the download still bundles everything it needs — but the runtime inside is supported through November 2028 instead of expiring this November.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.5** as a delta from beta.4.g.

- **Retargeted to .NET 10** — Genie ships self-contained, so the runtime we build against is the one you run. .NET 8 loses security support on 10 November 2026; .NET 10 is supported through 14 November 2028. Nothing to install, nothing to configure — profiles, scripts, maps, plugins and layouts all carry over (#234 — thanks @simtel12).
- **Dependencies moved with it** — logging packages to 10.0.10, Avalonia to 11.3.18 (DataGrid pinned at 11.3.13, where its 11.3 line ends).
- **Building from source now needs the .NET 10 SDK** — only relevant if you compile Genie yourself.
- **Injuries figure spacing tightened** — in the assembled-figure layout the thighs meet the hips, and the back and nerves views sit closer to the body.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.5)

## v5.0.0-beta.4.g — Hand-Off

`#goto` walks the Genie 4 way again: with the community automapper.cmd installed, the script drives the route — special-move directives (`script ggbypass`, `ice nw`) and pacing globals all work. Plus spell timers match Genie 4 to the second, and rule files reload live on external edit.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.g** as a delta from beta.4.f.

- **`#goto` hands the walk to automapper.cmd** — Genie 4 parity: the community script drives when present (built-in walker otherwise, `#config automapperscript` to force it), so map special-move directives and `$caravan`/`$powerwalk` pacing work again (#226 — thanks @Azothy).
- **Rule files reload live** — external edits to the seven rule `.json` files apply without a reconnect; invalid files are rejected whole (#231).
- **Spell timers match Genie 4** — `$spellpreptime` exact, `$casttimeremaining` counts down live, `$casttime` is the raw epoch, `<spelltime>` parsed (#224 — thanks @SaragosDR).
- **Experience-window highlight scoping fixed** — no cross-window bleed, no span loss on retokenize (#232).
- **`#statusbar` slots size to content** — populated slots pack left; a giant un-numbered line still spans and ellipsizes.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.g)

## v5.0.0-beta.4.f — Nested & Composed

Script-engine parity weekend: nested `if … then {` blocks skip correctly, variables compose mid-name again (`%spell%countermana` → `%spell1mana`), and type-anywhere now forwards Backspace/Delete/Enter/arrows to the command bar — history recall and submit work with the Game window focused, like Genie 4.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.f** as a delta from beta.4.e.

- **Nested `if … then {` blocks skip correctly** — a false outer condition no longer executes the lines after a nested block; brace depth now survives arbitrary nesting (#228 — thanks @digitalnyc1).
- **Mid-name variable composition restored** — `%spell%countermana` resolves `%counter` mid-name like Genie 4; the mid-word guard now keys on exact-case matches so the #171 protection stays intact (#225 — thanks @SaragosDR).
- **Editing & history keys reach the command bar from anywhere** — Backspace/Delete/Enter/arrows forward to the bar when no text control has focus; fixes two AvaloniaEdit quirks that ate Delete and the arrows (PR #227 — thanks @simtel12).
- **First Genie.App test project** — the stream-routing matrix behind PR #222 now has permanent CI coverage (PR #223 — thanks @simtel12).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.f)

## v5.0.0-beta.4.e — Community Day

The community batch: a separate repo-scripts directory so script updates can't clobber local edits, talk/whisper lines no longer double in Main and Log, and `action … when eval` conditions evaluate for real. Everything in this build came from player reports, PRs, and DM'd findings — most filed and fixed the same day.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.e** as a delta from beta.4.d.

- **Repo scripts get their own directory** — "Update Scripts" pulls into `#config reposcriptdir` instead of your script folder; locally edited scripts always win (#221 — thanks @Azothy).
- **Talk/whisper lines no longer double in Main and Log** — the parser tags DR's duplicate wire copy instead of dropping it; windows dedupe, scripts/triggers still see the full wire, ParseGameOnly parity in both modes (PR #222 — thanks @simtel12).
- **`action … when eval` evaluates for real** — variables in the expression now substitute at fire time; the condition was silently false forever (#224 — thanks @SaragosDR).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.e)

## v5.0.0-beta.4.d — Strongbox

Window-management polish: built-in layout presets (**Strongbox**, the new default, and **Shadowveil**) ship with every install, emptied dock columns collapse so the Game window can reclaim the full width, and the `#statusbar` slot row matches Genie 4's StatusStrip geometry.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.d** as a delta from beta.4.c.

- **Built-in layout presets — Strongbox & Shadowveil** — two curated layouts in the Layout menu on every install (marked built-in); Strongbox is the default for fresh profiles. Built-ins can't be destroyed — saving over one creates your own editable copy.
- **Experience window "Show Config Bar"** — toggle the settings strip from the window's right-click menu (`#config experienceconfigbar`).
- **Warning on unexpanded variables in outgoing commands** — a command about to reach the game with a raw `$var`/`%var` still in it draws a warning first (`#config warnrawvars false` to silence).
- **Docking: emptied columns collapse** — the Game window expands into a side column once its last panel closes or moves away (#219 — thanks @digitalnyc1).
- **`#statusbar` slot row uses Genie 4 StatusStrip geometry** — slot 1 springs, slots 2–10 autosize and hide when empty; un-numbered `#statusbar` lines are no longer clipped.
- **`<clearDynaStream>` is consumed** — dynamic stream windows (e.g. spellInfo) clear properly (#220 — thanks @simtel12).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.d)

## v5.0.0-beta.4.c — Clean Escape

A fast-follow fix bundle for the external-testing round: script-set timer variables compute correctly again, typed commands expand `$variables` with full Genie 4 parity, `#window show` reaches the real built-in panels, and three mapper zone-browsing follow-ups.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.c** as a delta from beta.4.b.

- **Script `put #tvar` with a braced eval stores the result** — the combat-script idiom `put #tvar X {#evalmath (…)}` stored literal text instead of the number, so escape/back-training timers built on the comparison never fired. Script-issued `#tvar` now runs the same value pipeline as a typed one (Genie 4 parity).
- **Typed commands expand `$variables` with full Genie 4 parity** — shrink-search for dotted names plus the reserved clock variables, matching script-text rules.
- **`#window show` on a built-in panel operates on the real tool** — no more empty duplicate "Assess" panels; scripts can reopen closed built-ins (Log, Familiar, Death…). Only Main stays script-untouchable.
- **Echo to a window named by an unresolved `$variable` routes to Main** instead of manufacturing a phantom panel.
- **Mapper zone-browsing follow-ups** — browsing decided by zone identity (no more three-map bounce off boundary stubs), the browse hold engages before the zone loads, and it releases the moment your character moves.
- **Bare-Alt menu arming countered at the menu itself** — Alt-combination keybinds can no longer arm the menu bar and swallow the next keystroke.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.c)

## v5.0.0-beta.4.b — Night Watch

Silent-disconnect detection, config edits that stick on Genie 4-import profiles, a mapper zone-browsing suite, and a wide batch of scripting and parity fixes — the external-testing bundle.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4.b** as a delta from beta.4.

- **Silent-disconnect watchdog** — the client no longer sits "Connected" forever after DragonRealms drops a session without closing the socket; tune or disable with `#config activitytimeout`.
- **Mapper zone browsing** — picking a map you're not in pauses tracking with a "Return to Current Zone" bar; blue cross-zone rooms open the connecting map (Genie 4 parity); zone dropdown sort orders + SPECIAL badge.
- **Configuration edits survive reconnect on Genie 4-import profiles** — panel saves keep the `.cfg` files in lockstep so the next connect's replay can't revert them.
- **`/track` and `/trackreset`** are claimed client-side for EXPTracker muscle memory.
- **Fixes** — `#send`/`#wait`/`#event` queues pump in the app; connect dialog attributes the session to the right profile; automapper follows Genie 4 hidden-exit arcs during `#goto`; undefined `$variables` stay literal in scripts; `ScriptFinished` fires exactly once per script death; `#action remove` on an unknown pattern is a silent no-op; `#config fe` aliases `frontend`; bare-Alt no longer arms the menu bar; status-bar slots collapse promptly; macOS menu bar reads "Genie 5" (#215 — thanks @simtel12).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4.b)

## v5.0.0-beta.4 — Fresh Ink

A new editor-backed Game window you can opt into, Open Log In Editor, and a scripting fix for community `-verb` send idioms.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.4** as a delta from beta.3.

- **AvaloniaEdit-backed Game window (opt-in)** (#200) — a new renderer for the main Game window via `#config useeditorgamewindow on` (default off): flat memory use regardless of scrollback depth and a "Pause Scrolling" that genuinely holds. The classic renderer stays the default until parity is proven; the stream windows follow on the same renderer in a later build.
- **File ▸ Open Log In Editor** (#89) — opens the current session log in your configured external editor — the last Genie 4 File-menu parity item.
- **`send` / `#send` treat a leading `-verb` as "fire eagerly"** — community scripts that prefix a command with `-` (e.g. `send -cast;-0.05 gesture`) now reach the game instead of bouncing "Please rephrase." `put` is unchanged.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.4)

## v5.0.0-beta.3 — Crosstalk

Window routing that finally works, a Hide Title Bar fix, and a batch of stream, Lich, and experience fixes — several from @simtel12.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.3** as a delta from beta.2.

- **Closed stream panels honour their "If Closed" routing** (#211) — a closed panel's text can redirect to another window (Talk/Whispers consolidate into Log by default), follows the chain if that window is also closed, and is never silently dropped; the Layout-tab dropdown now round-trips.
- **Hide a floated window's title bar** (#181) — reclaim space from the window menu or the title-bar right-click menu; restore by double-clicking the window's top edge. Hide Title Bar also no longer blanks the window — it collapses only the title strip.
- **Per-creature combat status** (#202) — `<crtrStatus>` (hostile / disengaged / flying) is now tracked in game state, cleared on room change.
- **CircleCalc output toggles** (#207) — `#var CircleCalc.Echo 0` hides the result and `#var CircleCalc.Parse 0` stops it feeding scripts (both default on).
- **Fixes** — no more double-printed stream lines in Main when a stream panel is closed (#210); blank lines preserved in `INFO` / `LOOK` / `HELP` and experience output (#209); the crafting `<forging>` element is discarded instead of leaking markup (#208); the Experience window updates live under `BRIEFEXP ON` (#204); Lich debug logging resolves the temp directory from the arguments Lich actually received and keeps the mirror up until the owned Lich exits (#205, #206).
- **DR front-end note** — DR gates `EXPBRIEF` and richer clickable-command markup to the Wrayth front end: set `#config frontend wrayth` and reconnect.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.3)

## v5.0.0-beta.2 — Proxy & Polish

Lich-proxy diagnostics and combat-color polish — the second beta.

> **📡 Beta channel.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel delivers them; **Help → Check for Updates** offers **beta.2** as a delta from beta.1.

- **🔌 New `#config lichdebug`** — when Genie auto-launches (owns) Lich, live-tails that session's `temp/debug-*.log` into the game window as `[lich-debug]` lines. Independent of `conndebug` (the Genie-side connection trace) — enable both for a full Lich-proxy diagnosis. Auto-launch status now shows the Lich PID. Thanks @simtel12!
- **Lich temp-directory resolution** — Genie now resolves Lich's temp dir the way `lich.rbw` actually parses its args (`--temp=PATH`, else `--home=PATH/temp`), honours quotes in `#config lichargs` so paths with spaces survive, and refuses to auto-launch with a directory flag Lich silently ignores (naming the spelling that works). Thanks @simtel12!
- **Combat text bold is aligned** (#199) — bold/preset/link coloring no longer lands a few characters off on lines that contain an HTML entity (combat lines open with a literal `<`); offsets are rebased into the decoded text, correct in both the Combat and Main windows.
- **Cleaner XML stream** (#198) — the server's `<link>` menu element is recognized and discarded instead of leaking as stray output.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.2)

## v5.0.0-beta.1 — Coming of Age

**Genie 5 graduates to beta** — plus a round of display-polish fixes.

> **📡 Still on the beta channel — now literally.** Beta builds ship as GitHub **pre-releases**, so the Core updater's **beta** channel keeps delivering them; **Help → Check for Updates** offers **beta.1** as a delta from alpha.10. A stable `5.0.0` channel follows once the beta soak and the remaining roadmap items (cross-platform auto-update, server-driven dialog windows) land.

- **🎓 Alpha → Beta** — the version line graduates from `alpha` to **beta**. Nothing about install or updates changes; it's a maturity milestone on top of a proven core (SGE/Lich login, the `.cmd` script engine, mapper, plugins, themes, docking, and EV-signed Windows builds).
- **Monospace game text** — `<output class="mono">` blocks (stat tables, ASCII maps, some menus) render in the monospace font while normal prose keeps your game font; highlights still paint. (#178)
- **Combat text keeps its color** — side-stream lines (combat, thoughts, whispers) echoed to the main window no longer lose their bold/link/preset colors; combat hits paint yellow again. (#187)
- **Floating windows restore correctly** — a minimized float returns to its floated bounds instead of full screen, plus a **right-click title-bar menu** (Restore/Maximize/Minimize/Close). (#196)
- **Script Manager keeps its name** (#197) and the server's `<switchQuickBar>` element is discarded cleanly instead of leaking as stray output (#188).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-beta.1)

## v5.0.0-alpha.10 — Signed, Sealed, Delivered

**The first code-signed release** — plus a batch of community fixes.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.10** as a delta.

- **🔏 Signed Windows builds** — the Windows binary is code-signed with an **EV certificate** issued by GlobalSign to **Shadow Realms LLC**. `Genie5.exe` now verifies as Shadow Realms LLC under Properties → Digital Signatures; the "unknown publisher" era is over (SmartScreen reputation still accrues per file on brand-new builds). macOS/Linux remain unsigned for now. The version jumps to **alpha.10** to mark the milestone.
- **Lich improvements** — dynamic `lichargs` + owned auto-launch lifecycle (#182), nil-safe `GENIE5-IDENT` probe (#185), FE-port probe binding + PID preserved on reconnect (#186). Thanks @simtel12!
- **Script Manager opens cleanly before the first connect/command** (#193) and a normal disconnect no longer fires `Disconnected` twice (#195). Thanks @simtel12!
- **Folder/editor launches handle paths with spaces** (#192); **Layout → Windows keeps Apply/Reset reachable** (#184); new wiki exploration of **iPhone options** (#189).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.10)

## v5.0.0-alpha.8.17 — Community Fixes

A community bug-fix round — display polish and a Genie 4 command.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.17** as a delta.

- **`#comment <window> <text>`** — Genie 4's window-title annotation: `#comment Room $zoneid. $roomid` titles the Room panel **Room (69. 120)**; blank text clears it.
- **Intentional blank lines are preserved** — `INFO`, `LOOK <character>`, `HELP ADVICE` output keeps its blank-line spacing instead of collapsing.
- **Obvious-paths links** sit on the text baseline instead of rendering raised/superscript.
- **Script debug trace** (`goto`/`gosub`/`return` at debug level 1+) reports the real source line of the jump target.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.17)

## v5.0.0-alpha.8.16 — Menu-Script Fix

A quick fix for a script-variable regression from 8.14/8.15.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.16** as a delta.

- **Nested `%$name_suffix` variables resolve again** — a name with an underscore suffix (like mm_train's `%$selection_DESC`) stopped resolving in 8.14/8.15 and could throw `bad condition … unexpected '%'` in menu scripts. The variable shrink-search breaks at an underscore again, matching Genie 4. If you use mm_train or any menu script, grab this build.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.16)

## v5.0.0-alpha.8.15 — Floating Windows & Plugin Transforms

Floating windows grow up, layouts remember them, and plugins get real transform power.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.15** as a delta.

- **Floating windows behave like real tool windows** — one taskbar button per Genie instance (no more one-per-float), a minimize button on the float's title bar, floats minimize/restore together with the main window, and **saved layouts now capture and restore floats** at their positions.
- **Startup layout per character** — a Layout dropdown in the Connect dialog auto-applies your chosen saved layout every time that character connects.
- **`#tvar save` / `#tvar load`** persist your session tvars to `tvars.cfg` and back — including across restarts.
- **Plugin transform hooks honored end-to-end** — `OnGameText` runs first in the line pipeline and can rewrite or gag what scripts, triggers, and windows see; the new `OnEcho` hook does the same for echoed lines. See [Plugins](Plugins).
- **`$spellpreptime`** is the spell's full prep length (Genie 4 parity), not the elapsed count-up (that's `$spelltime`).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.15)

## v5.0.0-alpha.8.14 — Script & Hand Parity

Three community-reported fixes: script variables and your hands now behave exactly like Genie 3/4.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.14** as a delta.

- **`$righthand` / `$lefthand` carry the full display name** ("whiskey jug"), with `$righthandnoun`/`$lefthandnoun` staying the bare noun ("jug") — Genie 3/4 parity; both used to be the noun. The hands display in the status bar shows the full name too.
- **Undefined skill / spell variables no longer error** — `if ($Outdoorsmanship.Ranks >= 1750)` before your Experience window has seen the skill used to throw `bad condition … missing ')'`; it now just evaluates false (like Genie 3/4) and resolves normally once the data arrives.
- **`roomname` preset colors the room title**, matching Genie 4.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.14)

## v5.0.0-alpha.8.13 — Highlights Everywhere

A quick follow-up to 8.12: highlights now paint in every window — and you can choose exactly which ones.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.13** as a delta.

- **Highlights work in the Room, Mobs, and Players panels** — user rules and Names colors now paint there like they do in the game window and stream tabs (only the Room panel's objects line was styled before; Mobs/Players were plain).
- **Per-window highlight scoping** — a new **Windows** field on each highlight rule: blank = every window (the default, so existing and imported rules are unchanged), or a comma-separated list like `room, main` to restrict where it paints. Also the last argument of `#highlight add`. See [Highlights](Configuration#highlights--coloring-text).

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.13)

## v5.0.0-alpha.8.12 — Inventory View

Your characters' belongings as one searchable, sortable catalog — plus two Genie 4 parity commands.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.12** as a delta.

- **Inventory View window** — `/iv scan` catalogs everything you own (person, vault, deed register, home, Trader storage) per character into a searchable tree with **weight/size columns** (from Elanthipedia, cached locally), **sortable headers**, **Wiki Lookup**, **Find in Shops** (player-shop prices via the community-run DR Service Plaza), and CSV export. Replaces the external InventoryView plugin; Genie 4 `InventoryView.xml` files carry over. See [The Inventory View window](The-Interface#the-inventory-view-window).
- **`#browser <url>`** opens your default browser and **`#queue clear`** flushes queued-but-unsent commands (travel.cmd's `RETURN_CLEAR`) — both Genie 4 parity.
- **`$game`/`$charactername` corrected from the server's identity tag** — matters for Lich sessions; the Lich tab gains an Instance picker.
- **Script diagnostics** — start lines show the resolved script path, malformed `waiteval` warns instead of hanging silently, and Genie 4's trailing-paren leniency is honored.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.12)

## v5.0.0-alpha.8.11 — Scriptable /commands

A small, focused release: scripts can now run client /commands.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.11** as a delta.

- **Scripts can drive client /commands** — `put /sort weapon`, `send /sort weapon`, and a bare `/sort weapon` script line now run the Circle Calculator (and every other tracker command — `/calc`, `/tt`, `/spelltimer`, `/exp`) exactly like typed input, instead of leaking the literal text to the game server. The semicolon form and delayed sends work too. (#169)
- **Script-author note:** `#put /sort weapon` (leading `#`) is a *comment* in the script language — Genie 4 parity — and is ignored by design. Use `put /sort weapon` or a bare `/sort weapon` line.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.11)

## v5.0.0-alpha.8.10 — Import Integrity

A bug-sweep with one theme: rules you import or save now load back exactly as written — Genie 4 imports finally survive a restart.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.10** as a delta.

- **Genie 4 imports survive a restart** — per-character imports were saved into a folder the engine never read; Core and the app now share one per-character path (`Profiles/{Character}-{Account}/`), with automatic migration of the old folders. (#163)
- **Rule files are always real cfg files** — the import had been writing JSON into `.cfg` files, which the connect-time loader replayed into the game; one shared cfg writer now, loaders dispatch only #commands, and legacy JSON saves convert themselves in place. (#168)
- **Loads are faithful and quiet** — saved rules no longer get variable-expanded at load (patterns like `$monstercount` stay intact), and connecting prints one "Triggers Loaded" summary per file instead of announcing every rule. (#168)
- **Typed `#action {command} when {pattern}`** parses action-first like Genie 4 instead of storing the rule transposed. (#162)
- **File → Import from Genie 4** works on a fresh launch, before any command is typed. (#164)
- **Mapper walk indicator** no longer shows a phantom Resume/Cancel strip on a fresh launch. (#165)
- **Analytics** sub-rank "Rank over time" ranges get decimal y-axis labels instead of a repeated integer. (#166)

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.10)

## v5.0.0-alpha.8.9 — Script Manager & Injuries

The Scripts window grows into a full **Script Manager** (library tree, running-script controls, Genie 4 `#script` command parity incl. hot reload), the Injuries panel gets a sprite-based body display, and a cluster of script-variable parity gaps closes.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.9** as a delta.

- **Script Manager** — library tree with filter + live folder watch, running rows with pause/elapsed/current line, run-with-args, right-click actions everywhere (incl. Script Bar chips), an External Editor picker, and full Genie 4 `#script` parity (`abort/pause/resume … all | except <name>`, hot `reload` at the next `goto`, `trace`, `vars`, `debug`, `explorer`).
- **Injuries body display** — per-part sprites with severity colours, in a 4×4 grid or an assembled figure (`#config injurieslayout`).
- **Script variables** — new **`$spellpreptime`**; **`$` scoping matches Genie 4** (`$` = globals only, fixing `#link → #parse` global visibility); **`$argcount`** restored as a true `$`-frame token.
- **Window-menu Copy** copies the full highlighted selection (Ctrl+C was never affected).
- **Mapper legend** finds a genuinely clear viewport corner on dense maps; the cross-zone **wait-bar text** ticks again; the collapsed **DETAILS** tab renders vertically.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.9)

## v5.0.0-alpha.8.8 — Mapper & Connect Fixes

Stability and mapper groundwork: connect/reconnect races are serialized, and the placeholder cross-zone connections file no longer shadows the links derived from your maps.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.8** as a delta.

- **Connect / reconnect race** — overlapping connect and auto-reconnect attempts now serialize to a single clean session instead of interleaving.
- **Cross-zone map connections** — the placeholder `ZoneConnections.xml` seeded on first launch no longer suppresses the cross-zone links derived from your maps' border-room notes; hand-authored entries augment the derived graph. (Groundwork for multi-zone travel.)

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.8)

## v5.0.0-alpha.8.7 — Command Parity

Two Genie 4 command-parity fixes: **`#send`** again queues with an optional delay (instead of sending immediately like `#put`), and **`#beep` / `#bell`** sound the system alert instead of printing an unknown-command error.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.7** as a delta.

- **`#send` delay & queue** — `#send` again matches Genie 4: it queues through the roundtime gate with an optional leading delay (`#send 5 stow my gem` waits 5 seconds plus roundtime, then sends), and `#send clear` empties the queue. `#put` still sends immediately. Scripts that rely on `#send N …` — such as retry-after-web loops — now work as intended.
- **`#beep` / `#bell`** — now sound the system alert (respecting the Play Sounds setting) instead of printing `Unknown command: beep`: native beep on Windows, system alert on macOS, terminal bell on Linux.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.7)

## v5.0.0-alpha.8.6 — Genie 4 Parity & Polish

A connect-time **`flags` check** that warns when DragonRealms' settings would confuse the parser, the **Show in Main Window** toggle on every stream's right-click menu, Genie 4's **`#lc`** Lich shortcuts, and parser + travel fixes.

> **📡 Still on the beta channel — that's intentional.** Every alpha ships as a GitHub **pre-release**, so the Core updater defaults to **beta**; that's what lets **Help → Check for Updates** see new alpha builds. Already on an earlier alpha? Open the Updates dialog and you'll be offered **alpha.8.6** as a delta.

- **`flags` check at connect ([#29](https://github.com/GenieClient/Genie5/issues/29))** — Genie reads DragonRealms' `flags` once at connect and warns if any would change what the parser sees (`RoomBrief`, `MonsterBold`, `ShowRoomID`, `StatusPrompt`, …), with the fix to apply. Silent otherwise; `#config flagscheck off` to disable.
- **"Show in Main Window" stream toggle** — the right-click menu on every stream window now has a Show in Main Window toggle (matching Configuration → Layout), on by default so streams still mirror into Main until you opt one out.
- **`#lc` / `#lconnect` / `#ls` + opt-in Lich auto-launch** — Genie 4's short Lich-connect aliases, a `#ls` settings dump, and an off-by-default option (`#config lichautolaunch on`) to have Genie start Lich for you before connecting.
- **Fixed** — paired `<b>` bold in help text ([#160](https://github.com/GenieClient/Genie5/issues/160)); `#goto` while already walking now interrupts instead of hanging scripts ([#96](https://github.com/GenieClient/Genie5/issues/96)); stream right-click checkmarks match their saved settings after connect.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.6)

## v5.0.0-alpha.8.5 — Analytics, Accessibility & Parity

A skill-history **Analytics** dashboard, a first pass at **screen-reader** support with a Text-to-Speech settings tab, and a batch of Genie 4 parity: name-highlight colours, `#names` / `#preset` commands, eval + match-all triggers, and more.

- **Analytics window** — Window → Analytics: live XP/hour and per-skill gain bars for the session, 7/30/90-day skill-gain curves, and a session list with compare-up-to-3 overlay. History records locally (your own skills only, never uploaded — see [PRIVACY](https://github.com/GenieClient/Genie5/blob/main/PRIVACY.md)); a one-time first-connect notice explains it and lets you turn it off.
- **Screen-reader support (first pass)** + **Text-to-Speech settings tab** — accessibility names across the main window and dialogs (NVDA / Narrator; Windows fullest, macOS partial), and a Configuration → Text-to-Speech tab with a per-stream read + priority grid, voice test, and rate/volume sliders.
- **Name-highlight colours + `#names` ([#154](https://github.com/GenieClient/Genie5/issues/154), [#148](https://github.com/GenieClient/Genie5/issues/148))**, **`#preset` ([#149](https://github.com/GenieClient/Genie5/issues/149))**, **eval + match-all triggers ([#150](https://github.com/GenieClient/Genie5/issues/150), [#23](https://github.com/GenieClient/Genie5/issues/23))**, **Experience G4 layout ([#144](https://github.com/GenieClient/Genie5/issues/144))**, **Help ▸ Changelog ([#155](https://github.com/GenieClient/Genie5/issues/155))**, and **parity odds & ends ([#151](https://github.com/GenieClient/Genie5/issues/151))**.
- **Fixed** — the Thoughts stream now renders in its palette colour, and name-highlight rules persist across restart.

[Full release notes →](https://github.com/GenieClient/Genie5/releases/tag/v5.0.0-alpha.8.5)

## v5.0.0-alpha.8.4 — Parser Gap Reporting

When DragonRealms sends an element Genie's parser doesn't recognize yet, a one-click prompt drafts a pre-redacted GitHub issue for you to review and submit.

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
