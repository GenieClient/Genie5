# Scripting

Genie 5 runs Genie 4 `.cmd` scripts. The engine is a **faithful port** of Genie 4's Wizard-derived script language — if you've written scripts for Genie 4, Wizard, or StormFront, the syntax is identical and your scripts should just run. New to scripting? This page is a friendly tour; the complete vocabulary is on [Scripting Reference](Scripting-Reference).

> If a script worked in Genie 4 and doesn't work here, that's a bug we want to hear about — [file an issue](https://github.com/GenieClient/Genie5/issues/new). Script compatibility is treated as non-negotiable.

## Where scripts live

Drop `.cmd` files into your **Scripts** folder — one shared `Scripts/` folder at the data root, used by every character. See [Application Folders](Application-Folders) for the exact path on your OS. No restart needed — Genie picks up new files immediately.

If you set `#config reposcriptdir` (where **Update Scripts** pulls community script repos), Genie looks in your own Scripts folder first and the repo folder second — so a script you've edited locally always wins over the updater's copy.

## Hello world

Create `Scripts/hello.cmd`:

```
echo Hello, %1!
```

Run it from the command bar (scripts are prefixed with `.`):

```
.hello world
```

Output: `Hello, world!` — `%1` was filled in by the argument you passed.

## The vocabulary at a glance

```
# A comment is any script line whose first non-whitespace character is #.
# That includes lines like "#echo foo" — meta-commands never run as bare
# script lines; a script runs one by sending it, e.g.:  put #echo done
# There are no end-of-line comments: "pause 1 # wait" hands "1 # wait"
# to pause, so keep every comment on its own line.

# Variables:
#   $name  reads a global / live game-state value
#   %1, %2 are the arguments passed to the script (%0 is all of them)
#   var foo bar     sets a local variable (name, then value — no equals sign); read it back as %foo
#   %foo = bar      is the same assignment, Genie 4's bare form (the "=" is optional)

# Send a command to the game:
put look

# Wait for game text matching a label (literal) or regex:
match Done You finish searching.
matchwait

# Or block until a substring appears:
waitfor You can move again

# Pause for seconds (waitpause is an alias of pause — a plain N-second timer):
pause 2.5
waitpause 0.5

# Conditionals on live game state:
if $health < 50 then put chant heal
if def(weapon) then echo I have a weapon set

# Loops via labels + goto:
LOOP:
  put assess
  pause 1
  goto LOOP
```

## Roundtime safety

Scripts are **roundtime-aware**. A `put` issued while you're in roundtime queues, waits, and respects the type-ahead budget — the engine's roundtime gate already holds the next statement until roundtime expires, so you don't have to write pause-before-put everywhere. (`waitpause` is simply an alias of `pause` — a plain N-second timer, not a roundtime wait.)

Because DragonRealms doesn't announce when roundtime ends, the engine schedules its own wake-up from the live roundtime clock — so RT-gated scripts resume correctly. The mechanics are in [Scripting Reference](Scripting-Reference#the-roundtime-gate).

## Game-state variables

Every live game-state field is exposed as a `$variable`, so scripts can read your character's condition directly. Common ones:

| Variable | Holds |
| --- | --- |
| `$health`, `$mana`, `$spirit`, `$concentration`, `$stamina` | Current vital percentages (0–100). |
| `$roomname`, `$roomdesc`, `$roomexits` | Current room info. |
| `$righthand`, `$lefthand` | What you're holding. |
| `$preparedspell` | Prepared spell (or empty). |
| `$stance` | `offensive` / `advance` / `forward` / `neutral` / `guarded` / `defensive` (full lowercase words). |
| `$kneeling`, `$prone`, `$sitting`, `$stunned`, `$hidden`, `$webbed`, … | Status booleans. |
| `$roundtime` | Seconds of roundtime remaining — recomputed every time it's read, so it counts down. |

Type `#var` at the command bar to see the full live list. The complete table is on [Scripting Reference](Scripting-Reference#engine-set-globals).

## Running and stopping scripts

| Type at the command bar | Does |
| --- | --- |
| `.myscript arg1 arg2` | Run `Scripts/myscript.cmd` with `%1`=arg1, `%2`=arg2. |
| `#scripts` | List running scripts. |
| `#stop myscript` | Stop one script (`#kill` is a synonym; a bare `#stop` stops the most recently started one). |
| `#stopall` | Stop everything (`#killall` is a synonym). |
| `#pauseall` / `#resumeall` | Pause every running script / resume them all. |
| `#scriptcheck myscript` | Check a script for problems without running it — see below. |
| `#edit myscript` | Open it in the script editor (creates it if new — see below). |

### Checking a script before you run it

`#scriptcheck <name>` (synonym `#checkscript`) parses a script exactly as starting it would and lists every problem with its file and line number, without running anything: a `goto`/`gosub` or `match` to a label that doesn't exist, `if` without `then`, unbalanced parentheses or `{ }` blocks, an `action` with no `when`, a missing include, and duplicate labels. Jumps whose target is a variable (`goto %next`) are only known at run time and are skipped. It's a quick way to vet an old Genie 4 script before taking it into the game.

### Creating a new script

`#edit` doubles as "new script": if the name doesn't exist yet, Genie creates
an empty file and opens it (matching Genie 4). How the type is chosen:

- **`#edit foo.js`** — an explicit supported extension (`.cmd`, `.inc`, `.js`)
  is honoured directly: `foo.js` is created.
- **`#edit foo`** — no extension given, so a small dialog asks which supported
  type to create (`.cmd` is the default). Cancelling creates nothing.

Names are bare file names under the `Scripts` folder; path separators and `..`
are rejected.

### The script editor

`#edit`, **Edit** in the Script Manager, and the ✏ icon on the Script Bar open
the script in Genie's built-in editor, a window of its own beside the game:

- **Line numbers and colours.** Syntax colouring for `.cmd` / `.inc` scripts
  (comments, labels, commands, `$` / `%` variables, strings, `#commands` inside
  a `put`) and for `.js` scripts. The colours follow your theme, light or dark.
  As in the script engine, a line is a comment only when it *starts* with `#`.
- **Ctrl+S saves** (Cmd+S on macOS). The file keeps its encoding (UTF-8 with or
  without a byte-order mark, UTF-16, or an older single-byte encoding) and its
  line endings (CRLF or LF). Saving writes a temporary file first and then swaps
  it in, so a crash mid-save can't leave half a script behind.
- **Ctrl+F** opens a search bar for finding text in the script.
- An `*` in the title means there are unsaved changes. Closing the window then
  asks whether to save; closing Genie asks about every unsaved script.
- If another program changes the file while it's open, a bar across the top
  offers **Reload** (take the new version) or **Keep mine** (your next save
  overwrites it).
- Editing a script that's already open brings its window to the front instead
  of opening a second copy.

`#edit name` opens the same file `.name` would run: the Scripts folder first,
then the repo-scripts folder, trying `.cmd`, `.inc`, then `.js`. Only files inside
those folders open this way. Saving doesn't restart a running script: stop and
start it again to pick up the change.

### Using an external editor instead

To keep editing in your own program, type `#config externaleditor on` (and
`#config externaleditor off` to come back). The Script Manager's right-click
**Edit in external editor** opens one file there whatever the setting says.
Genie picks the external program in this order, falling back to the next rung
if one isn't set or fails to launch:

1. **Display Settings → Editor Path** (*Edit → Display Settings*), or
   **Scripts → External Editor → Change…**. A full path, e.g.
   `C:\Program Files\Notepad++\notepad++.exe`.
2. **`#config editor <path>`**, the Genie 4 command, stored in `settings.cfg`.
   Accepts a full path or a bare program name on your `PATH` (e.g. `code`,
   `notepad++.exe`).
3. **OS default**: Notepad on Windows, the default text editor via `open -t`
   on macOS, or `xdg-open` on Linux.

A **Script Bar** above the command bar shows what's running, with stop/edit controls; it hides itself when nothing is running.

## What's different from Genie 4

A few intentional divergences:

- **`gosub` for reusable routines** — jumping into a nested/indented block isn't reliable; use `gosub` for sub-routines.

And two compatibility notes (both Genie 4 parity, worth spelling out because they're common misconceptions):

- **Comments** — a script line starting with `#` is *always* a comment, including `#put north` (which does nothing). Meta-commands run from a script only by sending them: `put #echo done`.
- **Undefined `$var` stays literal** — an unresolved `$name` is left in the text verbatim; it never aborts the script and never silently expands to empty. When it matters, guard explicitly with `if def(name)`.

## Example scripts to study

The community [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) repo has ~55 real scripts, from one-liners to 500+-line hunt loops. Start with the simpler ones (foraging, alchemy, simple buffers) before combat scripts.

## Related

- [Scripting Reference](Scripting-Reference) — the complete language: every statement, variable scoping, the roundtime gate, type-ahead.
- [JavaScript Scripting](JavaScript-Scripting) — `.js` scripts and Genie 4-style function libraries (`include` + `js` / `jscall`).
- [Configuration & Rules](Configuration) — triggers and variables that scripts build on.
- [Lich 5 Integration](Lich-5-Integration) — running Ruby scripts alongside `.cmd`.
