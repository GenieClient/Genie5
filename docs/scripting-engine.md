# Scripting Engine

This page covers [ScriptEngine](../src/Genie.Core/Scripting/ScriptEngine.cs) — the runtime that drives Genie 4 `.cmd` scripts. It's the centre of most "engine vs. script" timing questions, so the execution model below is worth internalising before changing anything in it.

The script *language* (Genie 4 dialect: `put`, `gosub`, `matchwait`, `action … when …`, etc.) is documented by the original Genie 4 wiki, and Genie 5 implements it faithfully. This page focuses on **how Genie 5 executes that language**, how it's wired into the rest of the engine, and where the DragonRealms protocol shapes its timing.

## Where it sits

The engine is constructed and wired in [GenieCore](../src/Genie.Core/GenieCore.cs#L252):

```csharp
Scripts = new ScriptEngine(
    scriptsDir:    Config.ScriptDir,
    typeAhead:     _typeAhead,
    sendCommand:   cmd => _connection.SendCommandAsync(cmd),
    echo:          msg => { EchoLine(msg); ScriptOutputLine(msg); },
    handleHashCmd: cmd => Commands.ProcessInput(cmd));

Scripts.InRoundtime               = () => state.Combat.InRoundTime;
Scripts.RoundTimeRemainingSeconds = () => (int)Math.Ceiling(state.Combat.RoundTimeRemaining);
```

So the engine is decoupled from the network and UI through four callbacks: send a command, echo text, run a `#cmd` (handed to [CommandEngine](../src/Genie.Core/Commanding/CommandEngine.cs)), and query roundtime from the live [GameState](../src/Genie.Core/GameState.cs). Script-readable `$variables` are mirrored separately by [ScriptGlobalsSync](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs) — see [line-pipeline.md](line-pipeline.md#scriptglobalssync--genie-4-reserved-variables).

The three events that drive execution are routed from the parser in `GenieCore._gameEventSub`:

| Event | Engine call | Unblocks |
| --- | --- | --- |
| `TextEvent` | `Scripts.OnGameLine(text)` | `matchwait`, `waitfor`/`waitforre`, actions |
| `PromptEvent` | `Scripts.OnPrompt()` | `wait`, type-ahead accounting, RT-gate recheck |
| `NavEvent` | `Scripts.OnRoomChanged()` | `move`, `nextroom` |

## Model

A script is a **flat list of statements** parsed from one or more `.cmd` files into a [ScriptInstance](../src/Genie.Core/Scripting/ScriptInstance.cs). At any moment a running instance is in one of three macro-states:

- **Running** — ready to execute the next statement.
- **Blocked** — paused on a timer, prompt, match, eval, or roundtime gate.
- **Finished** — dropped from the active list.

Scripts do **not** run on their own thread. They are advanced synchronously off the events above (and a dispatcher timer for pure pauses). The engine yields between statements so a long script can't freeze the app.

## Parser

[ScriptParser.Parse](../src/Genie.Core/Scripting/ScriptParser.cs) turns a file into a `ScriptInstance` in four stages:

1. **Include expansion** — `include foo` is recursively replaced with `foo.cmd` from the scripts directory. Cycles are detected; a missing include becomes an `echo` line rather than a crash.
2. **Inline-conditional normalisation** — `if X then put Y` is rewritten to block form (`if X then { put Y }`) so the jump-table code handles every conditional uniformly. `begin`/`end` are translated to `{`/`}`.
3. **Line list + label table** — every line becomes a `ScriptLine`; `label:` lines populate the label table for O(1) `goto`/`gosub`.
4. **If/else/while jump tables** — pre-computed at parse time so each conditional executes in O(1) at runtime with no forward scanning for the matching brace.

## Tick loop and blocking

The engine advances scripts in a bounded loop driven by the events above plus a dispatcher timer for pure timers (`pause`/`delay`) and the roundtime wakeup. Per instance, the loop checks unblock conditions in order — user pause, `pause`/`wait`/`delay`, `matchwait` deadline, `waitfor` deadline, `waiteval` re-evaluation, the roundtime gate — before executing the next statement. The per-tick statement budget is capped so a runaway `goto` loop can't monopolise the thread; the script resumes on the next tick instead.

### The roundtime gate

DR's server **does not** emit a prompt when roundtime expires — it only prompts in response to commands. So once a script is RT-gated, nothing in the natural event flow will wake it. The gate schedules a one-shot timer for the remaining roundtime (read live from `GameState.Combat.RoundTimeRemaining` via the `RoundTimeRemainingSeconds` callback) and re-checks when it fires. Roundtime is computed from the absolute `<roundTime>` epoch the parser captured, so it's correct regardless of whether `<roundTime>` or `<prompt>` arrived first.

### Type-ahead

`put`/`send` to the game contribute to an in-flight counter decremented on each `OnPrompt`. A shared [TypeAheadSession](../src/Genie.Core/Scripting/TypeAheadSession.cs) caps how many commands may be outstanding; it auto-calibrates downward when the server replies *"Sorry, you may only type ahead N commands."* Holding the script-side cap tight means a script sees a full server response (and any embedded `<roundTime>`) before the next game-bound `put` is considered.

## Statement reference

Listed by category. For exact semantics, read the `case` arms in [ScriptEngine.cs](../src/Genie.Core/Scripting/ScriptEngine.cs).

### Flow control

| Statement | Notes |
| --- | --- |
| `goto label` | Jump to `label:`. Unknown labels stop the script. |
| `gosub label [args]` | Push return PC + a fresh `$0..$9` frame (gosub args), jump. `gosub clear` wipes the stacks without jumping. |
| `return` | Pop the gosub + `$`-frame stacks; with no caller the script ends. |
| `exit` | Stop immediately; fires the finished event. |
| `if X then …` / `… { } elseif … else { }` | Inline form normalised to block form at parse time; block form uses parser-built jump tables. |
| `while X { … }` | Tests on entry; the closing `}` loops back to re-test. |
| `shift` | Shift `%1..%9` script args left by one. |

### Sending to the game

| Statement | Notes |
| --- | --- |
| `put text` / `send text` | Send to the server. Multiple `;`-chained commands drain one per tick. `send` (unlike `put`) additionally parses an optional leading numeric delay, and a leading `-` on the command is a "fire eagerly" marker (dash stripped, send-queue wait bypassed — beta.4). `put` sends its text as-is. |
| `put #cmd` | Routed as a meta-command to [CommandEngine](../src/Genie.Core/Commanding/CommandEngine.cs) (`#var`, `#tvar`, `#echo`, …) — not sent to the server. |
| `put .script args` | Launch `script.cmd` as a sub-script; does not consume type-ahead. |
| `move text` | Send `text`, then block until a new room arrives (`NavEvent` / `OnRoomChanged`), or a movement-failure line unblocks it. |
| `nextroom` | Block for the next room change without sending anything. |

### Timers and blocking

| Statement | Blocks until | RT-aware? |
| --- | --- | --- |
| `pause N` | N seconds elapsed | yes — the gate checks roundtime before the next statement |
| `waitpause N` | N seconds elapsed — plain alias of `pause` (default 1s), no extra roundtime coupling | same as `pause` |
| `wait` | next `<prompt>` | yes |
| `delay N` | N seconds elapsed | no — explicitly bypasses the RT gate (webbed/stunned sleeps) |
| `move` / `nextroom` | new room arrives | n/a |

### Pattern matching

| Statement | Notes |
| --- | --- |
| `match label literal` / `matchre label regex` | Register a pattern; `matchwait [N]` then blocks until a line matches (first match wins), with optional N-second timeout. |
| `waitfor text` / `waitforre regex` | Block until a line contains the substring / matches the regex (single-shot). |
| `waiteval expr` | Block until a [ScriptExpression](../src/Genie.Core/Scripting/ScriptExpression.cs) evaluates true; re-evaluated each tick so state changes (vitals, indicators) unblock it. |

Regex captures from `matchre`/`waitforre`/actions land in the current `$0..$9` frame, not in the `%N` script args.

### Variables and math

| Statement | Notes |
| --- | --- |
| `var name value` (and `setvariable`, `setvar`, …) | Set `%name`. Value is substituted before storage. |
| `unvar name` (and synonyms) | Remove `%name`. |
| `math var op N` | In-place `add`/`subtract`/`multiply`/`divide`/`modulus`/`set`. |
| `eval var expr` / `evalmath var expr` | Evaluate via `ScriptExpression`; `evalmath` coerces to numeric. |
| `random low high` | Uniform random into `%r`. |
| `timer start`/`stop`/`clear`/`reset` | Per-script timer baseline; `%timer` reads live elapsed seconds. Bare `timer` = `timer start`. |

### Actions

| Statement | Notes |
| --- | --- |
| `action body when pattern` / `whenre pattern` | Register a regex action; on a matching line, run `body` (captures in a pushed `$`-frame). |
| `action body when eval expr` | Eval action — fires on the rising edge of `expr` becoming true. |
| `action (label) on`/`off`/`remove`, `action on`/`off`/`clear` | Enable/disable/drop actions by label or globally. |

### Other

| Statement | Notes |
| --- | --- |
| `echo text` | Print to the echo channel (main window + Scripts panel), prefixed by the engine. |
| `debug N` | Per-instance trace verbosity (1 = goto/gosub/return … 10 = every line). |
| `save text` | Store the entire rest of the line into `%s` (Genie 4 parity; there is no slot form). |
| `include foo.js` / `js <expr>` / `jscall <var> <expr>` | JavaScript interop on the embedded Jint engine: `include` loads a `.js` function library for the script run, `js` calls into it, `jscall` stores the result in `%var`. Standalone `.js` scripts are also supported — see [javascript-scripts.md](javascript-scripts.md). |
| `plugin …` | Parsed for Genie 4 parity; execution is not supported. |

## Variables and scope

Two namespaces, by prefix:

| Prefix | Namespace | Lifetime | Set by |
| --- | --- | --- | --- |
| `%name` | per-instance `Vars` | the script | `var`/`math`/`eval…`; `%0..%9` seeded with script args |
| `$name` | engine-wide `Globals` | the session | [ScriptGlobalsSync](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs) (live state) and `#var`/`#tvar` |
| `$0..$9` | top `$`-frame | gosub call OR latest regex match | `gosub args`, `matchre`, `waitforre`, action firing |

`%` reads `Vars` only. `$` reads the top `$`-frame (for `$0..$9`), then falls back to `Globals`. Substitution rules (right-shrinking name search for `[A-Za-z0-9_.-]` identifiers, `%%name`/`$$name` double-eval, `%name(N)` pipe-array indexing) follow Genie 4.

### Engine-set globals

These are mirrored by [ScriptGlobalsSync](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs) at event time (not on access). A non-exhaustive list:

| Global | Source |
| --- | --- |
| `$health`, `$mana`, `$spirit`, `$stamina`/`$fatigue`, `$concentration`, `$encumbrance` | `<progressBar>` |
| `$roundtime`, `$casttime` | `<roundTime>`/`<castTime>` (live seconds remaining) |
| `$righthand`/`$righthandnoun`/`$righthandid` (and `left*`) | `<right>`/`<left>` |
| `$preparedspell`, `$stance` | `<spell>`, `pc stance` |
| `$standing`, `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$hidden`, `$invisible`, `$dead`, `$webbed`, `$joined`, `$bleeding`, `$poisoned`, `$diseased` | `<indicator>` (`"1"`/`"0"`) |
| `$north`, `$northeast`, … `$up`, `$down`, `$out` | `<compass>` (per-exit `"1"`/`"0"`) |
| `$roomname`, `$roomdesc`, `$roomexits`, `$roomobjs`, `$roomplayers`, `$gameroomid` | room `<component>` / `<nav>` |
| `$charactername`, `$gamename`/`$game`, `$connected` | session |

Because these are mirrored at event time, use `timer start` / `%timer` for wall-clock waits rather than diffing `$roundtime` between prompts.

## Diagnostics

- **Per-script** — `debug 5+` in a script traces its reactions (actions at 5, every line at 10), through the echo channel.
- **Scripts panel** — script-originated output (`[script]`, `[dbg:N]`, in-script `#echo`) is forked to the Scripts panel via `ScriptOutputLine` so it has its own scrollback ([GenieCore.cs:252](../src/Genie.Core/GenieCore.cs#L252)).

## Code references

- **[ScriptEngine.cs](../src/Genie.Core/Scripting/ScriptEngine.cs)** — tick loop, dispatch table, action firing, type-ahead gate, RT gate.
- **[ScriptInstance.cs](../src/Genie.Core/Scripting/ScriptInstance.cs)** — per-script state; the fields here are what makes a blocked script blocked.
- **[ScriptParser.cs](../src/Genie.Core/Scripting/ScriptParser.cs)** — include expansion, inline-conditional normalisation, jump tables.
- **[ScriptExpression.cs](../src/Genie.Core/Scripting/ScriptExpression.cs)** — evaluator for `if`/`eval`/`evalmath`/`waiteval`/`action … when eval`.
- **[TypeAheadSession.cs](../src/Genie.Core/Scripting/TypeAheadSession.cs)** — shared, auto-calibrated type-ahead cap.
- **[ScriptGlobalsSync.cs](../src/Genie.Core/Scripting/ScriptGlobalsSync.cs)** — live state → `$` globals.
- **[GenieCore.cs](../src/Genie.Core/GenieCore.cs)** — construction, callbacks, event routing.
