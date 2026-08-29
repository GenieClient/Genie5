# Scripting Reference

The complete `.cmd` scripting language as Genie 5 implements it. New to scripting? Read [Scripting](Scripting) first — this page is the full reference. The language is the **Genie 4 Wizard-derived dialect**, ported faithfully; the original Genie 4 documentation remains an authoritative reference for the language itself, while this page focuses on Genie 5's behavior and timing.

## Execution model

A script is a **flat list of statements** parsed from one or more `.cmd` files. A running script is always in one of three states:

- **Running** — ready to execute the next statement.
- **Blocked** — paused on a timer, prompt, match, evaluation, or the roundtime gate.
- **Finished** — removed from the active list.

Scripts do **not** run on their own thread. They are advanced off three game events plus a timer for pure pauses, and the engine yields between statements so a long or looping script can't freeze the app. A per-tick statement budget means even a tight `goto` loop won't monopolize the UI — it simply resumes on the next tick.

The three driving events:

| Event | Unblocks |
| --- | --- |
| A line of game text | `matchwait`, `waitfor` / `waitforre`, actions |
| A game prompt | `wait`, type-ahead accounting, roundtime re-check |
| A room change | `move`, `nextroom` |

## Parsing

When a script loads, it's transformed in a few passes: `include foo` is expanded recursively (cycles detected; a missing include becomes an echo, not a crash); inline conditionals (`if X then put Y`) are normalized to block form; labels are indexed for O(1) `goto`/`gosub`; and `if`/`else`/`while` jump tables are pre-computed so conditionals don't scan for their matching brace at runtime.

## Statement reference

### Flow control

| Statement | Notes |
| --- | --- |
| `goto label` | Jump to `label:`. An unknown label stops the script. |
| `gosub label [args]` | Push a return point and a fresh `$0..$9` arg frame, then jump. `gosub clear` wipes the stacks without jumping. |
| `return` | Pop back to the caller; with no caller, the script ends. |
| `exit` | Stop immediately. |
| `if X then …` / `… { } elseif … else { }` | Inline form is normalized to block form; uses pre-built jump tables. |
| `while X { … }` | Tests on entry; the closing brace loops back to re-test. |
| `shift` | Shift `%1..%9` left by one. |

### Sending to the game

| Statement | Notes |
| --- | --- |
| `put text` / `send text` | Send a command to the server. `;`-chained commands drain one per tick. A leading `-` on a command segment (`-cast`, `-0.05 cast`) is Genie 4's **quick-send** form — a positive, roundtime-gated pause before the send (applies to `put`, `send`, and `#send` alike). `send` additionally parses an optional leading numeric delay. |
| `put #cmd` | A meta-command (`#var`, `#echo`, …) — handled by Genie, not sent to the server. |
| `#send N cmd` / `#send clear` | Queue `cmd` to fire in N seconds; `clear` drops any pending queued sends. Works typed at the command bar or from a script via `put #send …`. |
| `put .script args` | Launch `script.cmd` as a sub-script (doesn't consume type-ahead). |
| `move text` | Send `text`, then block until a new room arrives (or a movement-failure line unblocks it). |
| `nextroom` | Block for the next room change without sending anything. |

### Timers and blocking

| Statement | Blocks until | Roundtime-aware? |
| --- | --- | --- |
| `pause N` | N seconds elapse | Yes — checks roundtime before the next statement |
| `wait` | The next prompt | Yes |
| `delay N` | N seconds elapse | **No** — deliberately bypasses the RT gate (e.g. webbed/stunned sleeps) |
| `move` / `nextroom` | A new room arrives | n/a |
| `waitpause N` | N seconds elapse — plain alias of `pause` (default 1s), no extra roundtime coupling | Same as `pause` |

### Pattern matching

| Statement | Notes |
| --- | --- |
| `match label literal` / `matchre label regex` | Register a pattern; `matchwait [N]` then blocks until a line matches (first match wins), with optional N-second timeout. |
| `waitfor text` / `waitforre regex` | Block until a line contains the substring / matches the regex (single-shot). |
| `waiteval expr` | Block until an expression evaluates true; re-checked each tick, so changing state (vitals, indicators) unblocks it. |

Regex captures from `matchre` / `waitforre` / actions land in the current `$0..$9` frame.

### Variables and math

| Statement | Notes |
| --- | --- |
| `var name value` | Set `%name` (value is substituted before storage). Synonyms: `setvariable`, `setvar`. |
| `unvar name` | Remove `%name`. |
| `math var op N` | In-place `add` / `subtract` / `multiply` / `divide` / `modulus` / `set`. |
| `eval var expr` / `evalmath var expr` | Evaluate an expression; `evalmath` coerces to numeric. |
| `random low high` | Uniform random into `%r`. |
| `timer start` / `stop` / `clear` / `reset` | Per-script stopwatch; `%timer` reads live elapsed seconds. Bare `timer` = `timer start`. |
| `save text` | Store the entire rest of the line into `%s` (Genie 4 parity; there is no slot form). |

### Actions (background reactions)

| Statement | Notes |
| --- | --- |
| `action body when pattern` / `whenre pattern` | Register a reaction; on a matching line, run `body` (captures land in a pushed `$`-frame). |
| `action body when eval expr` | Fires on the rising edge of `expr` becoming true. |
| `action (label) on` / `off` / `remove`; `action on` / `off` / `clear` | Enable/disable/drop actions by label or globally. |

A `goto` run from an action body redirects the whole script, Genie 4 style — it abandons whatever the script is blocked on (`pause`, `matchwait`), clears the armed match patterns, and resumes at the target label. (A normal in-line `goto` clears nothing — the register-matches-then-goto-to-a-shared-`matchwait` idiom depends on that asymmetry.)

Text you **send to the game** (typed or scripted) also runs through actions and triggers, Genie 4 style (`#config triggeroninput`, default on) — this is how menu scripts capture typed input with a pattern like `when ~(.*)` and a `~value` convention. Pair it with `#config mycommandchar ~` (Genie 4's parse-but-don't-send prefix, default `/`) and the `~value` reply fires the action **without reaching the game**, so the server never answers "Please rephrase that command."

### Other

| Statement | Notes |
| --- | --- |
| `echo text` | Print to the echo channel (main window + Scripts panel). |
| `#statusbar [N] text` | Show `text` in one of ten positional slots just below the vitals Status Bar (`#status` is a synonym). `N` (1–10, default 1) picks the slot, and the slot keeps its position — like Genie 4's status strip, so scripts can use slots as columns. Text persists until overwritten; an empty `text` clears slot `N`, `#statusbar clearall` empties all ten, and the row hides itself when every slot is empty. Every populated slot sizes to its text and the cells pack left in slot order; empty slots take no space. Slot 1 may additionally grow into any width left over (so a single long un-numbered `#statusbar` line still spans the window, as scripts like uber.cmd expect) and trims with an ellipsis instead of pushing later slots off screen. |
| `#flash` | Flash the Genie entry in the taskbar (Windows) or bounce the dock icon (macOS) until you bring the window back to the front — Genie 4 style. Classic use is a trigger action so a whisper or a hunting-script alert grabs your attention while Genie is in the background. Does nothing when the window is already focused. |
| `debug N` | Per-script trace verbosity (1 = goto/gosub/return … 10 = every line). |
| `include <file>.js` | Load a JavaScript function library for this script run — see [JavaScript Scripting](JavaScript-Scripting). |
| `js <expr>` / `jscall <var> <expr>` | Call a JS library function; `jscall` stores the result in `%var`. |
| `plugin …` | Parsed for Genie 4 parity; .NET plugin execution is not supported. |

### Named windows, links, and logging

The Genie 4 **menu-script toolkit** — the commands classic scripts like `mm_train` use to build clickable menu windows. They work typed at the command bar or from a script via `put #…`:

| Command | Notes |
| --- | --- |
| `#window add\|open\|show\|close\|hide\|remove\|clear "Name"` | Create, show, hide, or destroy a named dock window. `add`/`open`/`show` bring it up (creating it if needed); `clear` wipes its text in place. |
| `#link [>window] {text} {command}` | Print a clickable line — clicking it runs `command` through the normal input pipeline (it does **not** run at `#link` time). |
| `#echo [>window] [color] text` | Directed echo. Targets **Main**/**Game**, any built-in stream window (`>Combat`, `>Talk`, `>Thoughts`, …), or a named window; colours are honoured. Non-text panels (`>Mapper`, `>Vitals`, …) fall back to Main. |
| `#clear [window]` | Wipe a window's scrollback in place. The name works with or without the `>` prefix (`#clear "Moonmage Training Menu"`, Genie 4 style); a bare `#clear` wipes the main Game window. |
| `#script abort\|pause\|resume [name\|all]` | Script lifecycle control, Genie 4 style. Acts on the named script, or every script for `all` (or no name). `#script` never *starts* a script — use `.name` for that; bare `#script` lists what's running, like `#scripts`. |
| `#log [>file] text` | Append to a log file under your Logs folder. The `>filename` form writes verbatim; the bare form appends to the per-character daily log (with the Genie 4 `LOG CREATED` banner). Writes are serialized across scripts. |

Windows created this way render full text lines — clickable links and your highlight rules both apply.

## Variables and scope

Two namespaces, distinguished by prefix:

| Prefix | Namespace | Lifetime | Set by |
| --- | --- | --- | --- |
| `%name` | per-script locals | the script | `var` / `math` / `eval…`; `%0..%9` seeded with script args |
| `$name` | engine-wide globals | the session | live game state and `#var` / `#tvar` |
| `$0..$9` | the top `$`-frame | a `gosub` call or the latest regex match | `gosub args`, `matchre`, `waitforre`, action firing |

`%` reads locals only. `$` reads the top frame for `$0..$9`, then falls back to globals. Name resolution, `%%name` / `$$name` double-evaluation, and `%name(N)` pipe-array indexing all follow Genie 4 rules.

A `#var` / `#tvar` **value** that is itself `#eval` or `#evalmath` stores the expression's *result*, Genie 4 style — the classic menu-script idiom `put #var selection {#eval toupper("$selection")}` stores `MAGIC`, not the literal `#eval …` text. Typed standalone, `#eval <expr>` echoes the result.

### Engine-set globals

These live game-state globals are mirrored as events arrive (a non-exhaustive list):

| Global | Source |
| --- | --- |
| `$health`, `$mana`, `$spirit`, `$stamina`/`$fatigue`, `$concentration`, `$encumbrance` | progress bars |
| `$roundtime` | seconds remaining at event time |
| `$casttime` | raw epoch of when the cast is fully prepped (Genie 4 parity — compose `$casttime - $spellstarttime`) |
| `$spellpreptime` | full prep length in seconds (constant per spell) |
| `$spelltime`, `$spellstarttime`, `$casttimeremaining` | computed live on every read: elapsed count-up, prep-start epoch, countdown-to-prepped |
| `$righthand` / `$righthandnoun` / `$righthandid` (and `left*`) | held items |
| `$preparedspell`, `$stance` | prepared spell, stance |
| `$standing`, `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$hidden`, `$invisible`, `$dead`, `$webbed`, `$joined`, `$bleeding`, `$poisoned`, `$diseased` | status indicators (`1`/`0`) |
| `$north`, `$northeast`, … `$up`, `$down`, `$out` | compass exits (`1`/`0`) |
| `$roomname`, `$roomdesc`, `$roomexits`, `$roomobjs`, `$roomplayers`, `$gameroomid` | room info |
| `$charactername`, `$game`, `$connected` | session |

Because globals are mirrored at event time (not on access), use `timer start` / `%timer` for wall-clock waits rather than diffing `$roundtime` between prompts. Type `#var` at the command bar for the live list.

## The roundtime gate

DragonRealms' server does **not** send a prompt when roundtime expires — it only prompts in response to commands. So a roundtime-gated script has nothing in the natural event flow to wake it. The engine handles this by scheduling a one-shot timer for the remaining roundtime (read live from the game state) and re-checking when it fires. Roundtime is computed from the absolute timestamp the parser captured, so it's correct regardless of whether the roundtime or the prompt arrived first.

## Type-ahead

Commands you `put` to the game contribute to an in-flight counter that's decremented on each prompt. A shared, auto-calibrating cap limits how many commands can be outstanding, and tightens itself if the server replies *"Sorry, you may only type ahead N commands."* Keeping the cap tight means your script sees a full server response (including any roundtime) before its next game-bound command is considered.

## Diagnostics

- **Per-script tracing** — `debug 5` traces a script's reactions; `debug 10` traces every line. Output goes to the echo channel, and each running-script chip on the Script Bar shows the script's live trace level (`dbg:N`).
- **Script Manager** — script output (`[script]`, `[dbg:N]`, in-script `#echo`) is forked to the Script Manager's log view (script library + running-script list + output log), toggled from the **Scripts** menu.

## Differences from Genie 4

- **`gosub` for reusable routines** — jumping into a nested/indented label isn't reliable.

Compatibility notes (all Genie 4 parity): a script line starting with `#` is *always* a comment — `#put north` does nothing; meta-commands run from a script only via `put #cmd`. An undefined `$var` is left **literal** in the text (never aborts the script, never expands to empty); guard explicitly with `if def(name)` when it matters. Scripts live in one shared `Scripts/` folder at the data root, used by every character (see [Application Folders](Application-Folders)).

## Related

- [Scripting](Scripting) — the friendly tour.
- [Configuration & Rules](Configuration) — triggers, variables, and classes scripts build on.
- [Architecture](Architecture) — where the script engine sits in the pipeline.
