# JavaScript (`.js`) array scripts

Genie 5 runs `.js` scripts alongside the classic `.cmd` Wizard-dialect scripts.
Drop a `.js` file in your Scripts folder and launch it exactly like a `.cmd`
script:

```
.myscript
.myscript arg1 arg2
```

`.cmd` and `.js` scripts coexist — `$scriptlist`, stop/pause/resume, the script
bar, and tab-completion all treat them uniformly.

## How JS scripts differ from `.cmd`

`.cmd` scripts run cooperatively on the engine's tick loop. **`.js` scripts run
on their own thread**, top to bottom, like a normal program. That means you can
write straight-line procedural code and call blocking helpers directly:

```js
genie.put("stand");
genie.waitFor("You are now standing");
genie.put("get my bow");
genie.pause(0.5);
```

No labels and no `goto` — just functions, loops, and `await`-free blocking
calls. (When you do want `.cmd`-style multi-pattern dispatch, `genie.matchWait`
returns the matched pattern so you can `switch` on it — see below.) Blocking
calls park the script thread without
freezing the game; a `genie.stop` (or stopping it from the UI) unwinds it
cleanly.

## Engine

Scripts run on **[Jint](https://github.com/sebastienros/jint)**, a pure-C#
ECMAScript engine — no native binaries, so `.js` scripts work identically on
Windows, macOS, and Linux. Modern JS syntax (let/const, arrow functions,
template literals, destructuring, etc.) is supported.

## The `genie` API

Every script gets a global `genie` object (also aliased `game`):

| Call | Description |
|---|---|
| `genie.put(cmd)` / `genie.send(cmd)` | Send a command to the game. |
| `genie.echo(text)` / `genie.log(text)` | Echo text to the game window. |
| `genie.echoTo(window [, text [, color]])` | Directed echo — route `text` to a named window/stream (and optional `#RRGGBB`/colour name). Same seam as `.cmd`'s `#echo >Window #Color`. |
| `genie.waitFor(text [, secs])` | Block until a game line contains `text` (case-insensitive). Returns the matched line, or `""` on timeout. `secs` ≤ 0 (default) waits forever. |
| `genie.waitForRe(pattern [, secs])` | Block until a line matches the regex. Returns the capture groups as an array (`[0]` = whole match), or `[]` on timeout. |
| `genie.matchWait(patterns [, secs])` | Block until a line contains any of `patterns` (string or array of substrings, case-insensitive). Returns the **pattern that matched**, or `""` on timeout — `switch` on it to branch. |
| `genie.matchWaitRe(patterns [, secs])` | Regex form of `matchWait`: each entry is a (case-insensitive) regex. Returns the matched pattern string, or `""` on timeout. For capture groups, re-run the winner through `waitForRe`. |
| `genie.waitForPrompt([secs])` | Block until the next game prompt. Returns `true`, or `false` on timeout. |
| `genie.pause([secs])` | Sleep (default 1s). |
| `genie.timerStart()` | Start/restart this script's stopwatch. |
| `genie.timerElapsed()` | Seconds since `timerStart` (0 if not started). |
| `genie.timerStop()` | Stop and clear the stopwatch. |
| `genie.get(name)` | Read a session global (`$Name` in `.cmd` scripts, e.g. game-state vars). |
| `genie.set(name, value)` | Write a session global (visible to `.cmd` scripts as `$Name`). |
| `genie.getVar(name)` / `genie.setVar(name, value)` | Read/write a script-local variable. |
| `genie.roundtime()` | Seconds of roundtime remaining (0 when none). |
| `genie.stop()` | Stop this script. |

### Branching on multiple patterns (`matchWait`)

`matchWait` is the `.js` analogue of `.cmd`'s `match`/`matchwait` label
dispatch. Pass several patterns and branch on whichever fires first:

```js
genie.put("open door");
switch (genie.matchWait(["It is locked", "swings open", "already open"], 5)) {
  case "It is locked":  genie.put("pick door"); break;
  case "swings open":
  case "already open":  genie.put("go door");   break;
  default:              genie.echo("No response — timed out."); // "" on timeout
}
```

### Timing a section (`timerStart` / `timerElapsed`)

```js
genie.timerStart();
genie.put("forage lockflower");
genie.waitForRe("You (find|fail)", 10);
genie.echo("That took " + genie.timerElapsed().toFixed(1) + "s");
```

### Arguments

Launch args are available as script-local vars: `genie.getVar("1")`,
`genie.getVar("2")`, …; `genie.getVar("0")` is all args joined;
`genie.getVar("scriptname")` is the script name.

### Reading game state

State the parser tracks (health, stance, etc.) is mirrored into session globals,
so `genie.get("health")` works the same as `$health` in a `.cmd` script. Use
`genie.set(...)` to hand values back to `.cmd` scripts or other `.js` scripts.

## Example: `forage.js`

```js
// Forage for a given herb until we have 20, respecting roundtime.
var herb  = genie.getVar("1") || "lockflower";
var count = 0;

genie.echo("Foraging for " + herb + "…");

while (count < 20) {
  var rt = genie.roundtime();
  if (rt > 0) genie.pause(rt);

  genie.put("forage " + herb);
  var line = genie.waitForRe("You (find|fail to find|manage)", 10);

  if (line.length === 0) {            // timeout — try again
    continue;
  }
  if (/find/.test(line[0])) {
    count++;
    genie.echo("Found one (" + count + "/20).");
  }
}

genie.echo("Done — gathered 20 " + herb + ".");
```

## Behaviour notes

- **Stopping:** `genie.stop()`, the script bar, or `#stopall` stop a `.js`
  script. A long `waitFor` or infinite loop is aborted promptly (Jint
  cancellation), and blocking calls unwind via an internal abort — no error is
  shown for a deliberate stop.
- **Pausing:** pause takes effect at the next `genie.*` call. A script sitting in
  `waitFor` is already idle; it parks at the following call once resumed.
- **Reload:** launching a `.js` script that's already running stops the old
  instance first (same as `.cmd`).
- **Errors:** JS exceptions and syntax errors are echoed as
  `[script] <name> JS error: …` and stop the script; they never crash Genie.
- **Resolution order:** a bare `.name` resolves `name`, `name.cmd`, `name.inc`,
  then `name.js`. Give a script a unique base name to avoid ambiguity.
