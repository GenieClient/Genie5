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
| `put text` / `send text` | Send a command to the server. `;`-chained commands drain one per tick. |
| `put #cmd` | A meta-command (`#var`, `#echo`, …) — handled by Genie, not sent to the server. |
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
| `waitpause` | The current roundtime expires | Yes (that's its purpose) |

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
| `math var op N` | In-place `add` / `subtract` / `multiply` / `divide` / `set`. |
| `eval var expr` / `evalmath var expr` | Evaluate an expression; `evalmath` coerces to numeric. |
| `random low high` | Uniform random into `%r`. |
| `timer start` / `stop` / `clear` | Per-script stopwatch; `%timer` reads live elapsed seconds. |
| `save N value` | Genie 4 `%s` storage. |

### Actions (background reactions)

| Statement | Notes |
| --- | --- |
| `action body when pattern` / `whenre pattern` | Register a reaction; on a matching line, run `body` (captures land in a pushed `$`-frame). |
| `action body when eval expr` | Fires on the rising edge of `expr` becoming true. |
| `action (label) on` / `off` / `remove`; `action on` / `off` / `clear` | Enable/disable/drop actions by label or globally. |

### Other

| Statement | Notes |
| --- | --- |
| `echo text` | Print to the echo channel (main window + Scripts panel). |
| `#statusbar [N] text` | Show `text` in the strip to the right of the Script Bar (`#status` is a synonym). `N` (1–10, default 1) picks one of Genie 4's status slots; the non-empty slots are shown together and clear when the last script ends. An empty `text` clears slot `N`. |
| `debug N` | Per-script trace verbosity (1 = goto/gosub/return … 10 = every line). |
| `include <file>.js` | Load a JavaScript function library for this script run — see [JavaScript Scripting](JavaScript-Scripting). |
| `js <expr>` / `jscall <var> <expr>` | Call a JS library function; `jscall` stores the result in `%var`. |
| `plugin …` | Parsed for Genie 4 parity; .NET plugin execution is not supported. |

## Variables and scope

Two namespaces, distinguished by prefix:

| Prefix | Namespace | Lifetime | Set by |
| --- | --- | --- | --- |
| `%name` | per-script locals | the script | `var` / `math` / `eval…`; `%0..%9` seeded with script args |
| `$name` | engine-wide globals | the session | live game state and `#var` / `#tvar` |
| `$0..$9` | the top `$`-frame | a `gosub` call or the latest regex match | `gosub args`, `matchre`, `waitforre`, action firing |

`%` reads locals only. `$` reads the top frame for `$0..$9`, then falls back to globals. Name resolution, `%%name` / `$$name` double-evaluation, and `%name(N)` pipe-array indexing all follow Genie 4 rules.

### Engine-set globals

These live game-state globals are mirrored as events arrive (a non-exhaustive list):

| Global | Source |
| --- | --- |
| `$health`, `$mana`, `$spirit`, `$stamina`/`$fatigue`, `$concentration`, `$encumbrance` | progress bars |
| `$roundtime`, `$casttime` | live seconds remaining |
| `$righthand` / `$righthandnoun` / `$righthandid` (and `left*`) | held items |
| `$preparedspell`, `$stance` | prepared spell, stance |
| `$standing`, `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$hidden`, `$invisible`, `$dead`, `$webbed`, `$joined`, `$bleeding`, `$poisoned`, `$diseased` | status indicators (`1`/`0`) |
| `$north`, `$northeast`, … `$up`, `$down`, `$out` | compass exits (`1`/`0`) |
| `$roomname`, `$roomdesc`, `$roomexits`, `$roomobjs`, `$roomplayers`, `$gameroomid` | room info |
| `$charactername`, `$game`, `$connected` | session |

Because globals are mirrored at event time (not on access), use `timer start` / `%timer` for wall-clock waits rather than diffing `$roundtime` between prompts. Type `#vars` at the command bar for the live list.

## The roundtime gate

DragonRealms' server does **not** send a prompt when roundtime expires — it only prompts in response to commands. So a roundtime-gated script has nothing in the natural event flow to wake it. The engine handles this by scheduling a one-shot timer for the remaining roundtime (read live from the game state) and re-checking when it fires. Roundtime is computed from the absolute timestamp the parser captured, so it's correct regardless of whether the roundtime or the prompt arrived first.

## Type-ahead

Commands you `put` to the game contribute to an in-flight counter that's decremented on each prompt. A shared, auto-calibrating cap limits how many commands can be outstanding, and tightens itself if the server replies *"Sorry, you may only type ahead N commands."* Keeping the cap tight means your script sees a full server response (including any roundtime) before its next game-bound command is considered.

## Diagnostics

- **Per-script tracing** — `debug 5` traces a script's reactions; `debug 10` traces every line. Output goes to the echo channel.
- **Scripts panel** — script output (`[script]`, `[dbg:N]`, in-script `#echo`) is forked to its own panel with separate scrollback.

## Differences from Genie 4

- **Undefined `$var` aborts** the script with a clear reason instead of silently expanding to empty. Use `if def(name)` to guard.
- **Per-character script folders** (see [Application Folders](Application-Folders)).
- **`gosub` for reusable routines** — jumping into a nested/indented label isn't reliable.
- **Comment rule** — `#` is a comment only before whitespace/end-of-line; `#put north` is a meta-command.

## Related

- [Scripting](Scripting) — the friendly tour.
- [Configuration & Rules](Configuration) — triggers, variables, and classes scripts build on.
- [Architecture](Architecture) — where the script engine sits in the pipeline.
