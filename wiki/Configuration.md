# Configuration & Rules

Genie 5 ships all of Genie 4's **rule engines** — the pattern-driven helpers that color text, expand shortcuts, react to the game, and bind keys. You manage them two ways:

- **The Configuration dialog** — **Edit → Configuration…** opens a tabbed, form-based editor. The tabs are **Layout** (with Windows and Settings sub-tabs), **Highlights**, **Triggers**, **Substitutes**, **Gags**, **Shunts**, **Aliases**, **Scripts**, **Text-to-Speech**, **Macros**, **Variables**, and **Classes** — so it covers script settings and [Text-to-Speech](Text-to-Speech) alongside the rule engines. The list-based rule tabs each have a **type-to-filter box**, so a several-hundred-line trigger list stays navigable.
- **The command bar** — `#`-prefixed commands add and remove rules on the fly, exactly as in Genie 4.

Either way, rules are saved to disk automatically — a `.json` file per rule type, with a Genie 4-style `.cfg` twin kept in sync (see [Where rules are stored](#where-rules-are-stored)). Command syntax follows the **Genie 4 dialect**; when in doubt about a specific option, the Configuration dialog is the reliable surface.

![The Configuration dialog's Triggers tab with several pattern → action rules listed](images/config-dialog-triggers.png)

## The rule engines

| Rule | What it does | Commands |
| --- | --- | --- |
| **Aliases** | Expand a typed shortcut into a longer command. | `#alias`, `#unalias` |
| **Triggers / Actions** | Run command(s) automatically when game text matches a pattern. | `#trigger`, `#action` |
| **Highlights** | Color lines (or substrings) that match a pattern. | `#highlight` |
| **Substitutes** | Rewrite matching text before it's displayed. | `#substitute`, `#sub`, `#subs` |
| **Gags** | Hide matching lines from the output entirely. | `#gag`, `#ungag` |
| **Shunts** | Send matching lines to a named window instead of (or as well as) the main window. New in Genie 5. | `#shunt`, `#unshunt` |
| **Macros** | Bind a keystroke (F-keys, Ctrl/Alt/Shift+key) to command(s). | `#macro` |
| **Variables** | Store reusable values, readable in scripts as `$name`. | `#var`, `#tvar` |
| **Classes** | Named groups that turn sets of rules on/off together. | `#class` |

### Master toggles — whole engines on/off

The **File** menu has a master switch for each engine — **Highlights**, **Triggers**, **Substitutes**, **Gags**, **Aliases**, and **Images** — so you can silence a whole rule type without touching the rules. Everything stays loaded and editable while an engine is off. Each toggle is also a `#config` key, and the menu stays in sync whichever way you flip it:

```
#config triggers off
#config highlights on
```

(The Images toggle clears or re-fetches the room art live.) For finer-grained switching, use [classes](#classes--grouping-rules).

### Aliases — typing shortcuts

Expand a short token into a full command:

```
#alias {gb} {get my backpack}
```

Now typing `gb` sends `get my backpack`. Remove it with `#unalias {gb}`.

### Triggers / Actions — automatic responses

Run commands when a line of game text matches. Triggers are *responses* to the game, the same model Genie 4 used:

```
#action {stand} when {You stumble to the ground}
```

When "You stumble to the ground" appears, Genie sends `stand`. Patterns can be literal text or regular expressions, and an action can be tagged to a **class** so you can switch groups of them on and off.

> Triggers respond to text; they don't play the game for you. See [Policy Compliance](Policy-Compliance) for where the line is.

### Highlights — coloring text

Make important lines jump out:

```
#highlight {red} {You are bleeding}
#highlight {yellow} {whispers}
```

Highlights support foreground and background colors, whole-line vs. substring matching, and case sensitivity — all editable in the Highlights tab. They paint in **every window** — the game window, the stream tabs, and the Room / Mobs / Players panels.

A rule can also be **scoped to specific windows**: the Highlights tab's **Windows** field takes a comma-separated list of window ids (`main`, `room`, `mobs`, `players`, `backpack`, `experience`, a tracker panel by name like `Active Spells`, or a stream tab like `thoughts`); leave it blank for everywhere, which is the default for every existing and imported rule. Ids are case-insensitive. The same list works as the last argument of `#highlight add`.

![An inventory list with six different substring highlights colored, plus a clickable command link](images/config-highlights-in-action.png)

*Substring highlights in action — six rules coloring an inventory list.*

Two built-in colorings live alongside your own rules:

- **Presets** — the game's own text categories (room descriptions, whispers, speech, …) render in palette colors you can change under **Config → Highlights → Presets**.
- **MonsterBold** — creature and NPC names DragonRealms marks as monster-bold render in a distinct color (default gold) in the main window, the stream windows, and the Room panel. On by default; toggle it under **Config → Highlights → Presets** or with `#config monsterbold on|off`.

### Substitutes — rewriting text

Replace matched text with your own before it's shown (useful for shortening noisy messages). Managed with `#sub` / `#substitute`; list them with `#subs`.

### Gags — hiding lines

Suppress lines you never want to see:

```
#gag {The wind blows gently}
```

Remove with `#ungag`.

### Shunts — sending lines to another window

A shunt routes game lines that match a pattern into a named window, so chatter you still want to read stops scrolling the main window. It's new in Genie 5 (asked for back in Genie 4, never built there).

```
#shunt {^A kitten} {Atmospherics}
#shunt {kitten} {Pets} copy
```

- **Move or copy.** By default the line **moves**: it shows in the target window and not in the main one. Add `copy` at the end to show it in **both**.
- **The window** is any name `#echo >Window` accepts: a stream window (`Talk`, `Thoughts`, `Log`, `Atmospherics`, …) or a new name, which becomes its own window the first time a line is sent to it. `main`/`game` isn't accepted (lines are already there). A non-text panel (`Mapper`, `Vitals`, …) can't hold lines, so they stay in the main window.
- **A moved line never disappears.** If the target window is closed, the line stays in the main window. For a stream window, it goes wherever that window's own **If closed** setting (Layout tab) sends it; if that setting throws text away, a shunted line still comes back to the main window.
- **Ordering:** substitutes run first, then gags, then shunts. The pattern matches the text as you'd have read it (after substitutes), and a gagged line is gone, so it's never shunted. The first shunt that matches wins. Shunts apply to main-window game text only, not to lines already on their own stream (talk, combat, …) or to echoes and script output.
- `#shunt add {pattern} {window} [{class}] [copy]` is the long form; an optional class works as it does for every other rule.
- `#shunt` or `#shunt list` lists them (`#shunt kitten` filters by pattern or window). `#unshunt {pattern}` or `#shunt remove {pattern}` removes one. `#shunt clear`, `#shunt save`, and `#shunt load` work like the other rule types.
- The **Shunts** tab in the Configuration dialog edits the same rules, with an **Also keep in main window** box for copy.

There's no File-menu master switch for shunts yet. To pause a set of them, give them a class and turn the class off.

### Macros — key bindings

Bind a keystroke to one or more commands:

```
#macro {F2} {prepare 101}
```

Macros support F-keys and `Ctrl` / `Alt` / `Shift` modifiers. The Macros tab captures a keypress for you so you don't have to spell the key name.

### Variables — stored values

Variables hold values you can reuse, including inside scripts (where they read as `$name`):

```
#var weapon longsword
```

- `#var` values **persist** to disk (`variables.json`, with a `variables.cfg` twin).
- `#tvar` sets a **temporary** variable for the session only.

Genie also exposes ~40 live **game-state** variables (`$health`, `$stance`, `$righthand`, …) automatically — see [Scripting](Scripting#game-state-variables).

### Classes — grouping rules

A **class** is an on/off switch that gates a group of rules. Tag highlights, triggers, substitutes, gags, shunts, aliases, or macros with a class name, then flip them all at once:

```
#class {combat} {on}
#class {combat} {off}
```

This is how you keep, say, a full set of combat triggers ready but inactive until you start hunting.

## Window logs — a file per stream

**Auto Log** (File → Auto Log) records the whole game window for a session. **Window logs** are the Genie 4 Window Logger: pick a stream — thoughts, talk, whispers, logons, deaths, or any other — and every line of it is appended to a file of its own, for a permanent, greppable archive that isn't mixed in with combat.

```
#windowlog defaults
#windowlog add thoughts GenieWindows\Thoughts\{charactername}\Thoughts-{charactername}-{yyyy}.txt
#windowlog timestamp thoughts HH:mm
#windowlog off logons
#windowlog
```

| Command | What it does |
| --- | --- |
| `#windowlog` or `#windowlog list` | Show every rule, the file it writes to right now, and the streams seen this session. |
| `#windowlog streams` | List the stream ids that have carried text this session — the names you can log. |
| `#windowlog add <stream> [file]` | Log a stream. With no file, it uses `{stream}\{stream}-{charactername}-{yyyy}.txt`. Adding a stream that already has a rule changes its file and turns it on. |
| `#windowlog remove <stream\|all>` | Drop a rule. |
| `#windowlog on\|off <stream\|all>` | Pause or resume a rule without losing it. |
| `#windowlog timestamp <stream\|all> <format\|none>` | Set the timestamp put in front of each line, in brackets. The default is `yyyy-MM-dd HH:mm` (Genie 4's default), which gives `[2026-09-27 14:03]`; `none` turns it off. |
| `#windowlog defaults` | Add the Genie 4 Window Logger's default set: thoughts, talk, whispers, logons and deaths, each with its own yearly file under `Logs\GenieWindows\`. Streams you already have a rule for are left alone. |

**The file name** is a template. These tokens are filled in for each line:

| Token | Becomes |
| --- | --- |
| `{charactername}` or `{character}` | The character's name. |
| `{gamename}` or `{game}` | The game instance code, e.g. `DR` (the same value as `$game`). |
| `{stream}` | The stream id, e.g. `thoughts`. |
| `{yyyy}` `{yy}` `{MM}` `{dd}` | Year, two-digit year, month and day of the line. These are case-sensitive: `{MM}` is the month. |

The date comes from each line, so a `{yyyy}` file moves on to next year's file at midnight on New Year's Eve without a reconnect. Anything in braces that isn't a token is written as-is, and `#windowlog add` points it out.

**Where files go.** As with `#log`, a name is relative to your Logs folder and may include folders, which are created as needed. A leading `\` (the way Genie 4's config wrote it) is also relative to Logs. A window log can't be written outside the Logs folder: absolute paths, drive letters and `..` are refused.

**Two streams can share a file.** Genie 4's defaults send talk and whispers to the same `Conversations-…` file, so a conversation reads in order. Lines from both land in the order they arrived.

**What gets logged** is what the window shows. A Game-window (`main`) log applies your substitutes and leaves out gagged lines. Stream windows show their text unsubstituted, and their logs do too. Lines are buffered and written about once a second, so logging never slows the game down. If a file can't be opened (another copy of Genie has it, or the folder is read-only), you get one line saying so and Genie tries again later. The session carries on either way.

Rules are saved in `windowlog.json`. While you're connected, that's the character's own copy in `Profiles/<Character>-<Account>/`. Rules you set up before logging in go to the shared copy in `Config/`, which applies to every character that has no rules of its own. Changes apply to the next line.

## Themes

The whole client is themeable from the **Edit → Theme** submenu. Seven themes are built in — **Dark** (the default), **Light**, **Genie 4 Classic**, **High Contrast**, **Solarized Dark**, **Solarized Light**, and **Wrayth-style** — and a **theme editor** lets you tweak any of them into your own. Custom themes are saved as JSON files in `Config/Themes`, so they're easy to back up or share.

## Where rules are stored

Each rule type saves to its own **`.json` file** — `highlights.json`, `triggers.json`, `substitutes.json`, `gags.json`, `shunts.json`, `aliases.json`, `variables.json`, `classes.json` (plus `macros.json`) — with a Genie 4-format **`.cfg` twin** (one entry per line, the commands that recreate the rule) written alongside it so your rules still round-trip with the Genie 4 ecosystem. Rules live in **two layers**:

- **All characters** — the shared set in `Config/`.
- **This character** — that character's own rules in `Profiles/<Character>-<Account>/`, which **layer over** the shared set: the character's rules apply first, and every shared rule the character hasn't overridden (same rule key) shows through underneath.

Every rule editor has a **Scope** field ("This character" / "All characters" — new rules default to this character) and a Scope column, and each edit saves back to the file the rule actually lives in. Shared rules get per-character safeguards: deleting a shared rule while connected asks whether to **disable it just for this character** (a reversible local opt-out) or remove it for everyone, and the Toggle button on a shared rule quietly writes the same local opt-out. See [Application Folders](Application-Folders) for the full layout.

**Hand-editing is supported — even live.** External edits to the eight rule `.json` files (all of the above except `macros.json`) — in either layer — apply to the running client **without a reconnect**: Genie watches them, reloads the engines, and prints a `[config] … reloaded` line in the game window. (A file with a syntax error is left alone — nothing is cleared until it parses.) Other config files — display settings, themes, layouts, macro keybindings — still load at startup/connect.

## Importing from Genie 4

You don't have to recreate any of this by hand — **File → Import from Genie 4…** reads your existing Genie 4 `.cfg` files and folds them in, per-category, with merge/replace options. See [Importing from Genie 4](Importing-Genie4-Config).

## Related

- [Scripting](Scripting) — variables and triggers lead naturally into scripts.
- [The Interface](The-Interface) — where highlights and streams show up.
- [Importing from Genie 4](Importing-Genie4-Config) — bring existing rules across.
