# Scripting

Genie 5 runs Genie 4 `.cmd` scripts. The engine is a **faithful port** of Genie 4's Wizard-derived script language — if you've written scripts for Genie 4, Wizard, or StormFront, the syntax is identical and your scripts should just run. New to scripting? This page is a friendly tour; the complete vocabulary is on [Scripting Reference](Scripting-Reference).

> If a script worked in Genie 4 and doesn't work here, that's a bug we want to hear about — [file an issue](https://github.com/GenieClient/Genie5/issues/new). Script compatibility is treated as non-negotiable.

## Where scripts live

Drop `.cmd` files into your **Scripts** folder — one shared `Scripts/` folder at the data root, used by every character. See [Application Folders](Application-Folders) for the exact path on your OS. No restart needed — Genie picks up new files immediately.

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
# A comment is a # followed by whitespace or end-of-line.
# Lines starting with #command are meta-commands (#echo, #put, #stop, ...).

# Variables:
#   $name  reads a global / live game-state value
#   %1, %2 are the arguments passed to the script (%0 is all of them)
#   var foo bar     sets a local variable (name, then value — no equals sign); read it back as %foo

# Send a command to the game:
put look

# Wait for game text matching a label (literal) or regex:
match Done You finish searching.
matchwait

# Or block until a substring appears:
waitfor You can move again

# Pause for seconds:
pause 2.5
waitpause 0.5        # alias of pause — a plain N-second timer

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
| `$roundtime` | Seconds of roundtime remaining. |

Type `#var` at the command bar to see the full live list. The complete table is on [Scripting Reference](Scripting-Reference#engine-set-globals).

## Running and stopping scripts

```
.myscript arg1 arg2   # run Scripts/myscript.cmd with %1=arg1 %2=arg2
#scripts              # list running scripts
#stop myscript        # stop one script
#stopall              # stop everything
#edit myscript        # open it in your editor (creates it if new — see below)
```

### Creating a new script

`#edit` doubles as "new script": if the name doesn't exist yet, Genie creates
an empty file and opens it (matching Genie 4). How the type is chosen:

- **`#edit foo.js`** — an explicit supported extension (`.cmd`, `.inc`, `.js`)
  is honoured directly: `foo.js` is created.
- **`#edit foo`** — no extension given, so a small dialog asks which supported
  type to create (`.cmd` is the default). Cancelling creates nothing.

Names are bare file names under the `Scripts` folder; path separators and `..`
are rejected.

### Choosing an editor

`#edit` (and the ✏ icon on the Script Bar) opens the script in an external
editor. Genie resolves which editor to use in this order, falling back to the
next rung if one isn't set or fails to launch:

1. **Display Settings → Editor Path** — *Edit → Configuration → Display
   Settings*. A full path, e.g. `C:\Program Files\Notepad++\notepad++.exe`.
2. **`#config editor <path>`** — the Genie 4-parity command, stored in
   `settings.cfg`. Accepts a full path or a bare executable on your `PATH`
   (e.g. `code`, `notepad++.exe`).
3. **OS default** — Notepad on Windows, the default text editor via `open -t`
   on macOS, or `xdg-open` on Linux.

A **Script Bar** above the command bar shows what's running, with stop/edit controls; it hides itself when nothing is running.

## What's different from Genie 4

A few intentional divergences:

- **Comments** — `#` is a comment only when followed by whitespace or end-of-line. `#put north` is a meta-command, not a comment.
- **`gosub` for reusable routines** — jumping into a nested/indented block isn't reliable; use `gosub` for sub-routines.

And a compatibility note: an undefined `$var` expands to empty (Genie 4-compatible) — it never aborts the script. When it matters, guard explicitly with `if def(name)`.

## Example scripts to study

The community [DR-Genie-Scripts](https://github.com/Tirost/DR-Genie-Scripts) repo has ~55 real scripts, from one-liners to 500+-line hunt loops. Start with the simpler ones (foraging, alchemy, simple buffers) before combat scripts.

## Related

- [Scripting Reference](Scripting-Reference) — the complete language: every statement, variable scoping, the roundtime gate, type-ahead.
- [JavaScript Scripting](JavaScript-Scripting) — `.js` scripts and Genie 4-style function libraries (`include` + `js` / `jscall`).
- [Configuration & Rules](Configuration) — triggers and variables that scripts build on.
- [Lich 5 Integration](Lich-5-Integration) — running Ruby scripts alongside `.cmd`.
