# Scripting Reference

The complete `.cmd` scripting language as Genie 5 implements it. New to scripting? Read [Scripting](Scripting) first — this page is the full reference. The language is the **Genie 4 Wizard-derived dialect**, ported faithfully; the original Genie 4 documentation remains an authoritative reference for the language itself, while this page focuses on Genie 5's behavior and timing.

## Execution model

A script is a **flat list of statements** parsed from one or more `.cmd` files. A running script is always in one of three states:

- **Running** — ready to execute the next statement.
- **Blocked** — paused on a timer, prompt, match, evaluation, or the roundtime gate.
- **Finished** — removed from the active list.

Scripts do **not** each get their own thread. They are advanced off three game events plus a timer for pure pauses, all on Genie's **game thread** — the same thread that reads and parses the game stream, separate from the UI — so a busy or stuck script stalls only the game side while typing, menus, **Esc** and `#stopall` stay responsive (`#config gamethread off` restores the older UI-thread behaviour at the next launch). The engine yields between statements, and a per-tick statement budget means even a tight `goto` loop simply resumes on the next tick.

The three driving events:

| Event | Unblocks |
| --- | --- |
| A line of game text | `matchwait`, `waitfor` / `waitforre`, actions |
| A game prompt | `wait`, type-ahead accounting, roundtime re-check |
| A room change | `move`, `nextroom` |

## Parsing

When a script loads, it's transformed in a few passes: `include foo` is expanded recursively (cycles detected; a missing include becomes an echo, not a crash); inline `<% … %>` JavaScript blocks are lifted out (see [JavaScript Scripting](JavaScript-Scripting#inline-javascript-blocks)); bare `%name = value` assignments become `var` statements; inline conditionals (`if X then put Y`) are normalized to block form; labels are indexed for O(1) `goto`/`gosub`; and `if`/`else`/`while` jump tables are pre-computed so conditionals don't scan for their matching brace at runtime.

**Includes.** `include name` looks for `name`, then `name.inc`, then `name.cmd`, relative to the folder of the script you started (`include name.js` loads a [JavaScript library](JavaScript-Scripting) instead). Each file is pulled in once per run, and a cycle stops itself. The resolved file must sit inside your Scripts folder or the repo-scripts folder (`#config reposcriptdir`); anything else — `..\..\secret.txt`, an absolute path elsewhere — is refused with `[script] include refused — outside the scripts folder: …` instead of being read.

## Statement reference

### Flow control

| Statement | Notes |
| --- | --- |
| `label:` / `:label` | Define a label — a line holding one word with a colon at either end (no spaces). `goto` / `gosub` / `match` name it without the colon. |
| `goto label` | Jump to `label:`. An unknown label stops the script. |
| `gosub label [args]` | Push a return point and a fresh `$0..$9` arg frame, then jump. `gosub clear` wipes the stacks without jumping. |
| `return` | Pop back to the caller; with no caller, the script ends. |
| `exit` | Stop immediately. |
| `if X then …` / `… { } elseif … else { }` | Inline form is normalized to block form; uses pre-built jump tables. `X` is an [expression](#expressions). |
| `if_1` … `if_9` | Genie 4's argument test: `if_2 put wave %2` (or `if_2 then …`) runs when the script got **at least** 2 arguments (`%argcount >= 2`), with the same inline and block forms as `if`. |
| `begin` / `end` | Aliases for `{` / `}` when the word is alone on its line. |
| `while X { … }` | Tests on entry; the closing brace loops back to re-test. |
| `shift` | Shift `%1..%9` left by one, drop the last, rebuild `%0`, and count `%argcount` down by one. |

### Sending to the game

| Statement | Notes |
| --- | --- |
| `put text` / `send text` | Send a command to the server. `;`-chained commands drain one per tick. A leading `-` on a command segment (`-cast`, `-0.05 cast`) is Genie 4's **quick-send** form — a positive, roundtime-gated pause before the send (applies to `put`, `send`, and `#send` alike). `send` additionally parses an optional leading numeric delay. |
| `put #cmd` | A meta-command (`#var`, `#echo`, …) — handled by Genie, not sent to the server. |
| `#send N cmd` / `#send clear` | Queue `cmd` to fire in N seconds; `clear` drops any pending queued sends. Works typed at the command bar or from a script via `put #send …`. |
| `put .script args` | Launch `script.cmd` as a sub-script (doesn't consume type-ahead). |
| `put /cmd` / `send /cmd` / a bare `/cmd` line | A client **/command** (`/calc`, `/sort`, a plugin's own `/timers …`). Built-in extensions get first refusal, then plugins — the same order as typed input, and the same for `move /cmd`. A `/…` line nobody claims stays local (Genie 4's `mycommandchar` rule): it's echoed with a one-time warning that no extension or plugin claimed it, and it never reaches the game. |
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

Matching is **case-sensitive**, Genie 4 style — `match` / `waitfor` look for the exact substring, and `matchre` / `waitforre` / action patterns are case-sensitive regexes unless the pattern says otherwise (e.g. `(?i)`). Regex captures from `matchre` / `waitforre` / actions land in the current `$0..$9` frame.

### Variables and math

| Statement | Notes |
| --- | --- |
| `var name value` | Set `%name` (value is substituted before storage). Synonyms: `setvariable`, `setvar`, `variable`, `vars`. There's no `=` in this form — `var foo = bar` stores `= bar`. |
| `%name = value` / `%name value` | Genie 4's bare assignment: a line that starts with `%name` followed by a value is the same as `var name value` (the ` = ` is optional; `$name = value` sets a global the same way, like `put #var name value`). A line that is just `%cmd` on its own is still sent as a command; to send a variable *plus* more words, use `put %cmd north` (a bare `%cmd north` line is an assignment). |
| `unvar name` | Remove `%name`. Synonyms: `unvariable`, `unsetvar`, `unsetvariable`, `deletevariable`. |
| `math var op N` | In-place `add` / `subtract` / `multiply` / `divide` / `modulus` / `set`. An unset or non-numeric `%var` counts as `0`; dividing by `0` gives `0`. Whole results store without a decimal point (`4`, not `4.0`). |
| `counter op N` | Genie 4 shorthand for `math c op N` — `counter add 1` then `echo %c`. |
| `eval var expr` / `evalmath var expr` | Evaluate an [expression](#expressions) into `%var`; `evalmath` coerces the result to a number. Synonyms: `evaluate`, `evaluatemath`. An expression that fails to evaluate leaves `%var` empty. |
| `random low high` | Uniform random into `%r`. |
| `timer start` / `stop` / `clear` / `reset` / `setstart <datetime>` | Per-script stopwatch, Genie 4 semantics. `%t` (the Genie 4 name) and `%timer` read the elapsed seconds, fractional (`12.346`). `stop` keeps the elapsed value, so `timer stop` then `echo %t` shows the final time; `start` after a `stop` resumes from it. Only `clear` / `reset` zero it. `setstart` seeds the start from a date/time. Bare `timer` = `timer start`. A local you set yourself with `var t …` takes precedence over `%t`. |
| `save text` | Store the entire rest of the line into `%s` (Genie 4 parity; there is no slot form). |

### Actions (background reactions)

| Statement | Notes |
| --- | --- |
| `action body when pattern` / `whenre pattern` | Register a reaction; on a matching line, run `body` (captures land in a pushed `$`-frame). Both forms are regexes (Genie 4 parity); variables in the pattern are filled in when the action is registered. An optional `add` or `instant` keyword after `action` is accepted for Genie 4 compatibility. |
| `action body when eval expr` | Fires on the rising edge of `expr` becoming true. |
| `action (label) on` / `off` / `remove`; `action on` / `off` / `clear` | Enable/disable/drop actions by label or globally. |
| `action remove pattern` | Drop the action(s) registered with exactly that pattern. Removing one that isn't there is silently ignored, so remove-then-re-register is safe. |

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
| `js <expr>` / `jscall <var> <expr>` | Call a JS library function; `jscall` stores the result in `%var`. `javascript` is a synonym for `js`. |
| `<% … %>` | An inline JavaScript block, Genie 4 style — see [JavaScript Scripting](JavaScript-Scripting#inline-javascript-blocks). |
| `plugin …` | The Genie 4 `plugin` *statement* is not supported: it warns and clears its variable. Plugins are still reachable from scripts through their **/commands** — `put /cmd` (see [Sending to the game](#sending-to-the-game)). |
| `pluginscript …` / `do …` | Not supported: each prints a `not supported` warning and the line is skipped (never sent to the game). |

### Named windows, links, and logging

The Genie 4 **menu-script toolkit** — the commands classic scripts like `mm_train` use to build clickable menu windows. They work typed at the command bar or from a script via `put #…`:

| Command | Notes |
| --- | --- |
| `#window add\|open\|show\|close\|hide\|remove\|clear "Name"` | Create, show, or hide a named dock window. `add`/`open`/`show` bring it up (creating it if needed); `close`/`hide`/`remove` all hide it (the window keeps its text for next time); `clear` wipes its text in place. |
| `#link [>window] {text} {command}` | Print a clickable line — clicking it runs `command` through the normal input pipeline (it does **not** run at `#link` time). |
| `#echo [>window] [color] text` | Directed echo. Targets **Main**/**Game**, any built-in stream window (`>Combat`, `>Talk`, `>Thoughts`, …), or a named window; colours are honoured. Non-text panels (`>Mapper`, `>Vitals`, …) fall back to Main. |
| `#img [>window] <file> [w:N] [h:N]` | Show a picture (`#image` is a synonym). `file` is relative to your **Art** folder (`#config artdir`) unless it's a full path; Genie 4's `icons\sword.png` backslash style works on every platform. png, jpg, gif (first frame) and bmp only, up to 16 MB. `w:`/`h:` (or `width:`/`height:`) set the size in pixels — give both to stretch, one to scale keeping the shape — and nothing draws larger than 1024 px on a side. With no `>window` the picture goes to the **Portrait** panel when it's open (replacing its art, Genie 4 style) and otherwise inline in the Game window; `>Name` puts it in a named window. A missing or unreadable file prints the reason instead. See [Image lines](#image-lines) below. |
| `#clear [window]` | Wipe a window's scrollback in place. The name works with or without the `>` prefix (`#clear "Moonmage Training Menu"`, Genie 4 style); a bare `#clear` wipes the main Game window. |
| `#script abort\|pause\|resume\|pauseorresume [name\|all] [except name]` | Script lifecycle control, Genie 4 style. Acts on the named script, or every script for `all` (or no name); a trailing `except name` leaves that one alone. The target (and the `except` side) may be a `\|`-separated list, so `#script pause $scriptlistactive` and `#script resume $scriptlistpaused` work — and a list of `none` touches nothing. `#script` never *starts* a script — use `.name` for that; bare `#script` lists what's running, like `#scripts`. |
| `#script reload [name]` / `trace [name]` / `vars [name] [filter]` / `debug <0-10> [name]` / `explorer` | `reload` hot-reloads the script's file at its next `goto`; `trace` shows its recent jump history; `vars` lists its `%variables` (`filter` matches name or value); `debug` sets its trace level; `explorer` opens the Script Manager. |
| `#pauseall` / `#resumeall` / `#traceall <0-10>` | Pause every running script, resume them all, or set every script's trace level (`0` turns tracing off). `#stop [name]` / `#kill [name]` stop one script (no name = the most recently started); `#stopall` / `#killall` stop them all. |
| `#log [>file] text` | Append to a log file under your Logs folder. The `>filename` form writes verbatim; the bare form appends to the per-character daily log (with the Genie 4 `LOG CREATED` banner). Writes are serialized across scripts. |
| `#windowlog add\|remove\|on\|off\|timestamp\|defaults …` | Per-stream log files with filename templates (`{charactername}`, `{yyyy}`, …) — the Genie 4 Window Logger. See [Configuration → Window logs](Configuration#window-logs--a-file-per-stream). |

Windows created this way render full text lines — clickable links and your highlight rules both apply.

**Inline click links.** Anywhere in output text — an `echo`, an `#echo`, a named window, even game text — Genie 4's `{display:command}` markup shows `display` as a link that runs `command` when clicked, as many times per line as you like. In a script:

```
echo Go {north:north} or {south:go south}
```

prints "Go north or south" with two links. Typed at the command bar, quote the text so the braces survive argument parsing: `#echo "Go {north:north} or {south:go south}"`. The display runs up to the *last* colon (`{HP: 50:look}` shows "HP: 50"), so a command can't contain a colon, and there's no escape — a literal `{a:b}` in echoed text always becomes a link.

#### Image lines

An `#img` picture occupies one line and behaves like text everywhere except on screen. Behind the picture the line holds the text `[image: sword.png]`, and that's what you get from:

- **Scrollback.** It counts as one line against the scrollback limit.
- **Timestamps.** With the window's Time Stamp toggle on, the time appears before the picture.
- **Session log / Auto Log.** The log records `[image: sword.png]`.
- **Copy, Copy All and Find.** Selecting across the picture copies `[image: sword.png]`, and Find matches it.

Stream windows (`>Talk`, `>Combat`, …) and the other built-in panels can't show pictures, so an image aimed at one of them goes to the Game window. The optional AvaloniaEdit game window (`#config useeditorgamewindow`) shows the text form for now. Pictures can't be clicked yet.

### Other command-bar commands

The rest of Genie's `#` commands. Like everything above, each works typed at the command bar, from a trigger or alias, or from a script via `put #…`:

| Command | Notes |
| --- | --- |
| `#put text` | Send `text` to the game **immediately** — no delay, no roundtime hold (compare `#send`). |
| `#wait N text` | Queue `text` to run N seconds after the queue reaches it, on the same queue as `#send` but **without** waiting out roundtime. `text` may itself be a `#` command. |
| `#event N text` | Run `text` N seconds from now on a separate timer, independent of the `#send` queue and of roundtime. |
| `#queue clear` | Empty every pending send: the `#send` / `#wait` queue (including mapper walk steps) and each running script's queued `put` / `send` segments. Commands already sent can't be recalled. |
| `#parse text` | Feed `text` through Genie as if the game had sent it — scripts (`match`, `waitfor`, actions), triggers and plugins all see it; it's never shown or sent. The classic way to wake another script: `put #parse HUNT DONE`. |
| `#eval expr` / `#evalmath expr` | Print the value of an [expression](#expressions). As a `#var` / `#tvar` value (`{#eval …}`) it stores the result instead. |
| `#tvar name value` / `#untvar name` | Set / remove a session global `$name` (not saved with `#var save`). `#tvar save` / `#tvar load` write and read them in `tvars.cfg`. See [Configuration → Variables](Configuration#variables--stored-values). |
| `#unvar name` | Remove a saved `#var` (`#unsetvariable` is a synonym). A session global of the same name is removed too, so `$name` really is gone (the one exception is the live `$connected`). |
| `#comment window text` | Annotate a panel's title bar, Genie 4 style — `#comment Room $zoneid. $roomid` titles the Room panel "Room (69. 120)". No text clears it. |
| `#beep` / `#bell` | Sound the system alert (silent while `#config muted on`). |
| `#browser url` | Open `url` in your default browser; `https://` is added when no scheme is given. |
| `#layout [list]` / `load name` / `save [global\|profile] name` / `default name` / `delete name` / `reset` | Manage saved window layouts from the command bar; `save` goes to the connected profile unless you say `global`. See [The Interface → Layouts](The-Interface#layouts). |
| `#dialogs [list]` / `report id` / `forget id` | List the server dialog windows seen this session, draft a report for an unsupported one, or forget the placement answer you gave one so Genie asks again. |
| `#audit on\|off\|xmlhunting` | Start or stop the **Live Audit** log (raw game XML, parsed events, room/zone changes) for troubleshooting; it prints the log's path. `xmlhunting` also flags XML Genie doesn't yet use. |
| `#lichsettings` / `#ls` | Print the Lich launch settings — see [Lich 5 Integration](Lich-5-Integration). |

Commands covered on their own pages:

| Commands | See |
| --- | --- |
| `#alias` / `#unalias`, `#trigger` / `#action` / `#untrigger` / `#unaction`, `#highlight` / `#unhighlight`, `#substitute` / `#sub` / `#unsub`, `#gag` / `#ignore` / `#ungag`, `#shunt` / `#unshunt`, `#macro` / `#unmacro`, `#var`, `#class` | [Configuration & Rules](Configuration#the-rule-engines) |
| `#name [add] name [fg] [bg]` / `#name remove name` / `#unname name` | Player-name highlighting (the **Highlights → Names** tab in [Configuration](Configuration)). |
| `#preset id fg [bg]` | Recolour a built-in text preset (the **Presets** tab in [Configuration](Configuration)); bare `#preset` lists them. |
| `#config` (`#set`, `#setting`, `#settings`), `#windowlog` | [Configuration](Configuration) |
| `#goto` / `#go2`, `#mapper` | [Mapper](Mapper) |
| `#connect`, `#reconnect`, `#lichconnect` / `#lc` | [Connecting](Connecting) |
| `#speak` / `#say`, `#tts` | [Text-to-Speech](Text-to-Speech) |
| `#play` (`#playsound`, `#playwave`) | [Application Folders](Application-Folders) |
| `#plugin` / `#plugins` | [Plugins](Plugins) |

## Variables and scope

Two namespaces, distinguished by prefix:

| Prefix | Namespace | Lifetime | Set by |
| --- | --- | --- | --- |
| `%name` | per-script locals | the script | `var` / `math` / `eval…`; `%0..%9` seeded with script args |
| `$name` | engine-wide globals | the session | live game state and `#var` / `#tvar` |
| `$0..$9` | the top `$`-frame | a `gosub` call or the latest regex match | `gosub args`, `matchre`, `waitforre`, action firing |

`%` reads locals only (plus the computed `%t` / `%timer`). A `$name` is looked up in this order, and the first hit wins:

1. `$0` … `$9` — the top `$`-frame (script or `gosub` arguments, or the latest regex captures).
2. **Computed values** — `$argcount`, `$spelltime`, `$spellstarttime`, `$casttimeremaining`, `$roundtime` / `$roundtimeremaining`, recalculated on every read.
3. **Session globals** — live game state and `#tvar` values.
4. **Your saved `#var` values.**
5. **`$scriptlist` / `$scriptlistactive` / `$scriptlistpaused`, then the clock family** (`$date`, `$time`, …) — last, so a variable of your own with the same name shadows them.

A `#var` whose name already exists as a session global writes both, so the new value is what `$name` reads. Name resolution, `%%name` / `$$name` double-evaluation, and `%name(N)` pipe-array indexing all follow Genie 4 rules.

Every script also starts with `%0` (all arguments as one string), `%1` … `%9` (an argument that wasn't passed reads as empty, not literal), `%argcount` (how many arguments were passed; `shift` counts it down) and `%scriptname` (the name it was started under). `$argcount` is the count for the *current* `$`-frame instead: the script's arguments at top level, the `gosub` arguments inside a `gosub`, or the number of capture groups after a capturing `matchre` / `waitforre`.

`%name.length` (or `$name.length`) is Genie 4's built-in count of a `|`-separated list: with `var list a|b|c`, `%list.length` is `3`. Like Genie 4 it counts separators plus one, so an empty list reads `1`. An undefined `%name.length` stays literal.

A `#var` / `#tvar` **value** that is itself `#eval` or `#evalmath` stores the expression's *result*, Genie 4 style — the classic menu-script idiom `put #var selection {#eval toupper("$selection")}` stores `MAGIC`, not the literal `#eval …` text. Typed standalone, `#eval <expr>` echoes the result.

### Engine-set globals

These are the globals Genie itself publishes. Type `#var` at the command bar to see them all with their current values.

**Character and combat**

| Global | Source |
| --- | --- |
| `$health`, `$mana`, `$spirit`, `$stamina`/`$fatigue`, `$concentration`, `$encumbrance` | progress bars, as a percentage (`0`–`100`) |
| `$healthBarText`, `$manaBarText`, `$spiritBarText`, `$staminaBarText`, `$concentrationBarText`, `$encumbranceBarText` | the text the game shows on that bar |
| `$roundtime` / `$roundtimeremaining` | live countdown of roundtime seconds left (`0` when none), recomputed on every read |
| `$casttime` | raw epoch of when the cast is fully prepped (Genie 4 parity — compose `$casttime - $spellstarttime`) |
| `$spellpreptime` | full prep length in seconds (constant per spell) |
| `$spelltime`, `$spellstarttime`, `$casttimeremaining` | computed live on every read: elapsed count-up, prep-start epoch, countdown-to-prepped |
| `$righthand` / `$righthandnoun` / `$righthandid` (and `left*`) | held items |
| `$preparedspell`, `$stance` | prepared spell, stance |
| `$standing`, `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$hidden`, `$invisible`, `$dead`, `$webbed`, `$joined`, `$bleeding`, `$poisoned`, `$diseased` | status indicators (`1`/`0`) |
| `$prompt` | the game prompt's indicator text: `>` normally, with letters for your state such as `R>` (roundtime), `H>` (hidden), `S>` (stunned), `D>` (dead) |
| `$lastcommand` | the last command sent to the game |
| `$charname`, `$guild`, `$race`, `$gender`, `$age`, `$circle` | read from the output of the game's `info` command, so they're set once you've typed `info` this session |
| `$Skill.Ranks`, `$Skill.LearningRate`, `$Skill.LearningRateName` | per skill from the experience window or `exp` output, with spaces in the skill name turned into underscores (`$Inner_Magic.Ranks`). `.Ranks` is the whole-number rank; `.LearningRate` is the mindstate as a number `0`–`34`; `.LearningRateName` is its word (`clear`, `dabbling`, … ). Until the skill has been seen, the name stays literal. |
| `$TDPs`, `$RestedEXP.Stored`, `$RestedEXP.Usable`, `$RestedEXP.Refresh` | time-development points, and the rested-experience figures with a trailing "hours" removed |

**Room and map**

| Global | Source |
| --- | --- |
| `$north`, `$northeast`, … `$up`, `$down`, `$out` | compass exits (`1`/`0`) |
| `$roomname`, `$roomdesc`, `$roomexits`, `$roomobjs`, `$roomplayers`, `$gameroomid` | room info from the game (`$gameroomid` is the game's own room number) |
| `$monstercount`, `$monsterlist` | creatures in the room — a count, and their names joined with `, ` — after your monster-ignore list is applied |
| `$roomid`, `$zoneid`, `$zonename`, `$roomnote` | where the [Mapper](Mapper) places you: map room number, zone id, zone name, and the room's map note. `$roomid` / `$zoneid` read `0` (and the other two empty) when the mapper can't place you; they hold their last value while you're browsing another map. |

**Session**

| Global | Source |
| --- | --- |
| `$charactername`, `$game`, `$gamename`, `$connected` | your character, the game instance code (`DR`, `DRX`, `DRF`, `DRT`, taken from what the server reports), and `1`/`0` for a live connection |
| `$account`, `$gamehost`, `$gameport` | your account name, and the game server's host and port (`""` / `0` until connected) |
| `$gametime` | the game server's clock, in Unix seconds, from the latest prompt (`0` before one arrives) — compose it with other server epochs such as `$casttime` |
| `$client`, `$version` | `Genie Client 5` and Genie's version string |
| `$scriptlist`, `$scriptlistactive`, `$scriptlistpaused` | running scripts (`.cmd` and `.js`) joined with `\|` — all of them, only the unpaused ones, or only the paused ones — or `none` when the set is empty. Computed on every read; they compose with `#script` (e.g. `put #script resume $scriptlistpaused`). |

**Clock** — your computer's local time, computed on every read (Genie 4 formats, always with English AM/PM):

| Global | Format | Example |
| --- | --- | --- |
| `$date` | month/day/year, no leading zeros | `9/28/2026` |
| `$time` | 12-hour with AM/PM | `03:07:45 PM` |
| `$time24` | 24-hour, **still** followed by AM/PM (a Genie 4 quirk kept for compatibility) | `15:07:45 PM` |
| `$datetime` / `$datetime24` | `$date` and `$time` / `$time24` together | `9/28/2026 03:07:45 PM` |
| `$militarytime` | `HHmm`, no separator | `1507` |
| `$dayofmonth` | two digits (`05` on the 5th) | `28` |
| `$dayofyear` | `1`–`366` | `271` |
| `$year` | four digits | `2026` |
| `$month` | two digits (Genie 4's `$month` actually returned minutes; Genie 5 returns the month) | `09` |
| `$unixtime` | Unix seconds (the same instant everywhere, unlike the local-time rows above) | `1790608065` |

`$roundtime`, `$casttimeremaining`, `$spelltime`, `$spellstarttime`, the `$scriptlist` family and the clock family are computed each time they're read; the rest are mirrored when game events arrive. For wall-clock waits, use `timer start` / `%t`.

## Expressions

`if`, `elseif`, `while`, `waiteval`, `action … when eval`, `eval` / `evalmath` and `#eval` all use the same expression evaluator. Variables are substituted into the line **first**, so the evaluator only ever sees values — put a variable that may hold spaces or symbols in quotes (`"%weapon"`).

```
var weapon broadsword
eval up toupper("%weapon")
if contains("%weapon", "sword") then echo %up is a sword
```

prints `BROADSWORD is a sword`.

### Values

- **Numbers** — `12`, `3.5`. **Strings** — `"in quotes"`, with `\n`, `\t`, `\r`, `\\` and `\"` escapes; any other backslash is kept as-is, so regex escapes like `\d` pass through. `true` / `false`.
- **Bare words** are text, and may run to several words: `$guild = Moon Mage` compares against `Moon Mage`. A bare phrase ends at an operator or at the words `and`, `or`, `not`, `eq`.
- **An empty operand is the empty string**, not an error: with `%kcast` unset, `(%kcast = 1)` becomes `( = 1)`, which is simply false.
- **True or false.** A value is false when it's empty, `0`, `false` (any case), or an undefined variable left literal (`if (%unsetvar)` is false); everything else is true.
- **Results.** `eval` stores whole numbers without a decimal point (`4`), other numbers as-is (`2.5`), and comparisons as `true` / `false`. `evalmath` turns the result into a number (`true` → `1`).

### Operators

From lowest to highest precedence:

| Precedence | Operators | Notes |
| --- | --- | --- |
| 1 (lowest) | `\|\|`, `or` | Either side true. |
| 2 | `&&`, `and` | Both sides true. |
| 3 | `!`, `not` | Negation. It binds more loosely than a comparison, so `!$hidden = 1` means `!($hidden = 1)`. |
| 4 | `=`, `==`, `eq`, `!=`, `<>`, `<`, `<=`, `>`, `>=` | One comparison at a time — `1 < 2 < 3` is a parse error, so write `(1 < 2) and (2 < 3)`. Numeric when **both** sides are numbers, otherwise a case-sensitive text comparison. |
| 5 | `+`, `-` | `+` adds two numbers but joins text when either side isn't a number (`"HP: " + 5` → `HP: 5`). `-` is always numeric. |
| 6 | `*`, `/`, `%` | Numeric; a non-number counts as `0`. Dividing (or `%`) by `0` gives `0`. Put spaces around `%` (`7 % 2`): written `7%2`, the `%2` is read as script argument 2 before the expression is evaluated. |
| 7 (highest) | unary `-`, `( … )`, function calls | |

The word operators (`and`, `or`, `not`, `eq`) are case-insensitive and only count as whole words. In an `if`, unbalanced parentheses are fixed up Genie 4 style with a one-time warning; a condition that still can't be parsed warns once and counts as false.

### Functions

Function names are case-insensitive. Text arguments compare **case-sensitively**, positions are 1-based, and a missing argument counts as `""` (text) or `0` (number). An unknown function name is an error: in an `if` the condition warns once and is false; in `eval` the variable is left empty.

| Function | Returns | Example → result |
| --- | --- | --- |
| `contains(text, part)` — also `instr`, `instring` | true if `part` appears in `text` | `contains("a broadsword", "sword")` → `true` |
| `startswith(text, part)` / `endswith(text, part)` | true if `text` begins / ends with `part` | `endswith("longsword", "sword")` → `true` |
| `match(a, b)` | true if the two are **exactly** equal (not a substring test) | `match("sword", "Sword")` → `false` |
| `matchre(text, regex)` | true if the regex matches; the match and groups land in `$0`…`$9`. An invalid regex is an error; a regex that runs too long counts as no match. | `matchre("You have 42 silver", "(\d+) silver")` → `true`, `$1` = `42` |
| `def(name)` — also `defined` | true if `name` exists as a local, a session global or a saved `#var`, even when its value is empty. Give the name **without** a sigil. | `def(weapon)` |
| `len(text)` — also `length` | number of characters | `len("sword")` → `5` |
| `count(text, part)` | how many times `part` occurs (`0` if either is empty) — so a `\|` list has `count + 1` elements | `count("a\|b\|c", "\|")` → `2` |
| `indexof(text, part)` / `lastindexof(text, part)` | 1-based position of the first / last occurrence, `0` when absent (so `if !indexof(…)` means "not found") | `indexof("broadsword", "sword")` → `6` |
| `substr(text, start[, length])` — also `substring` | part of `text` from a **0-based** `start`, to the end or for `length` characters; out-of-range values are clamped, and a `start` past the end gives `""` | `substr("broadsword", 5)` → `sword` |
| `element(list, index[, separator])` | the element at a 0-based `index` of a list (separator `\|` by default). Never out of range: too high gives the last element, a negative index counts back from the end. For `\|` lists, parentheses in the list are removed first. | `element("a\|b\|c", 1)` → `b` |
| `replace(text, old, new)` | every `old` replaced by `new` (case-sensitive; an empty `old` is an error) | `replace("a-b-c", "-", "+")` → `a+b+c` |
| `replacere(text, regex, new)` | regex replacement; a bad or runaway regex returns `text` unchanged. `$1`-style group references in `new` get substituted as script variables before the function runs, so they don't reach the regex. | `replacere("a1b22", "\d+", "#")` → `a#b#` |
| `tolower(text)` / `toupper(text)` / `trim(text)` | lower-case / upper-case / whitespace trimmed from both ends | `toupper("sword")` → `SWORD` |
| `abs(n)`, `pos(n)` / `neg(n)` | absolute value; `pos` = `abs`, `neg` = minus the absolute value | `neg(5)` → `-5` |
| `min(a, b)` / `max(a, b)` | the smaller / larger of two numbers | `max(3, 7)` → `7` |
| `floor(n)` / `ceil(n)` — also `ceiling` | round down / up to a whole number | `ceil(2.1)` → `3` |
| `round(n[, digits])` | round to `digits` decimal places (default 0). Halves go to the even neighbour: `round(2.5)` → `2`, `round(3.5)` → `4`. | `round(2.567, 1)` → `2.6` |
| `sqrt(n)`, `log(n)` — also `ln` — and `log10(n)` | square root, natural logarithm, base-10 logarithm. Genie 4's `log` was base 10, so write `log10` or `ln` to be unambiguous. Out-of-range input (a negative square root, the log of `0`) gives a non-number rather than an error. | `log10(1000)` → `3` |

## The roundtime gate

DragonRealms' server does **not** send a prompt when roundtime expires — it only prompts in response to commands. So a roundtime-gated script has nothing in the natural event flow to wake it. The engine handles this by scheduling a one-shot timer for the remaining roundtime (read live from the game state) and re-checking when it fires. Roundtime is computed from the absolute timestamp the parser captured, so it's correct regardless of whether the roundtime or the prompt arrived first.

## Type-ahead

Commands you `put` to the game contribute to an in-flight counter that's decremented on each prompt. A shared, auto-calibrating cap limits how many commands can be outstanding, and tightens itself if the server replies *"Sorry, you may only type ahead N commands."* Keeping the cap tight means your script sees a full server response (including any roundtime) before its next game-bound command is considered.

## Diagnostics

- **Per-script tracing** — `debug 5` traces a script's reactions; `debug 10` traces every line. Output goes to the echo channel, and each running-script chip on the Script Bar shows the script's live trace level (`dbg:N`).
- **`#scriptcheck <name>`** — parses a script exactly as a start would and reports every problem with file and line, without running it: missing `goto`/`gosub`/`match` labels, `if` without `then`, unbalanced parentheses or braces, an `action` without `when`, missing includes, duplicate labels. Variable jump targets (`goto %next`) are skipped. `#checkscript` is a synonym.
- **`#script trace` / `#script vars`** — dump a running script's recent jumps, or its `%variables`, without stopping it; `#traceall <0-10>` sets every running script's trace level at once.
- **Script Manager** — script output (`[script]`, `[dbg:N]`, in-script `#echo`) is forked to the Script Manager's log view (script library + running-script list + output log), toggled from the **Scripts** menu.

## Differences from Genie 4

- **`gosub` for reusable routines** — jumping into a nested/indented label isn't reliable.
- **`log(n)` is the natural logarithm** here; in Genie 4 it was base 10. Genie 4's trigonometry functions (`sin`, `cos`, `tan`, `arcsin`, `arccos`, `arctan`) aren't available yet.

Compatibility notes (all Genie 4 parity): a script line starting with `#` is *always* a comment — `#put north` does nothing; meta-commands run from a script only via `put #cmd`. There are no end-of-line comments: `pause 1 # wait` hands `1 # wait` to `pause` (which then falls back to its 1-second default), so keep comments on their own lines. An undefined `$var` is left **literal** in the text (never aborts the script, never expands to empty); guard explicitly with `if def(name)` when it matters. Scripts live in one shared `Scripts/` folder at the data root, used by every character (see [Application Folders](Application-Folders)); with `#config reposcriptdir` set, that folder is searched first and the repo-scripts folder second, so a local copy always wins.

## Related

- [Scripting](Scripting) — the friendly tour.
- [Configuration & Rules](Configuration) — triggers, variables, and classes scripts build on.
- [Architecture](Architecture) — where the script engine sits in the pipeline.
