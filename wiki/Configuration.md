# Configuration & Rules

Genie 5 ships all of Genie 4's **rule engines** — the pattern-driven helpers that color text, expand shortcuts, react to the game, and bind keys. You manage them two ways:

- **The Configuration dialog** — **Edit → Configuration…** opens a tabbed, form-based editor. The tabs are **Layout** (with Windows and Settings sub-tabs), **Highlights**, **Triggers**, **Substitutes**, **Gags**, **Aliases**, **Scripts**, **Text-to-Speech**, **Macros**, **Variables**, and **Classes** — so it covers script settings and [Text-to-Speech](Text-to-Speech) alongside the rule engines. The list-based rule tabs each have a **type-to-filter box**, so a several-hundred-line trigger list stays navigable.
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

A **class** is an on/off switch that gates a group of rules. Tag highlights, triggers, substitutes, gags, aliases, or macros with a class name, then flip them all at once:

```
#class {combat} {on}
#class {combat} {off}
```

This is how you keep, say, a full set of combat triggers ready but inactive until you start hunting.

## Themes

The whole client is themeable from the **Edit → Theme** submenu. Seven themes are built in — **Dark** (the default), **Light**, **Genie 4 Classic**, **High Contrast**, **Solarized Dark**, **Solarized Light**, and **Wrayth-style** — and a **theme editor** lets you tweak any of them into your own. Custom themes are saved as JSON files in `Config/Themes`, so they're easy to back up or share.

## Where rules are stored

Each rule type saves to its own **`.json` file** — `highlights.json`, `triggers.json`, `substitutes.json`, `gags.json`, `aliases.json`, `variables.json`, `classes.json` (plus `macros.json`) — with a Genie 4-format **`.cfg` twin** (one entry per line, the commands that recreate the rule) written alongside it so your rules still round-trip with the Genie 4 ecosystem:

- **Shared baseline** — in `Config/`.
- **Per-character** — a copy under `Profiles/<Character>-<Account>/`, seeded from the shared baseline the first time that character connects, then independent.

See [Application Folders](Application-Folders) for the full layout.

**Hand-editing is supported — even live.** External edits to the seven rule `.json` files apply to the running client **without a reconnect**: Genie watches them, reloads the engines, and prints a `[config] … reloaded` line in the game window. (A file with a syntax error is left alone — nothing is cleared until it parses.) Other config files — display settings, themes, layouts, macro keybindings — still load at startup/connect.

## Importing from Genie 4

You don't have to recreate any of this by hand — **File → Import from Genie 4…** reads your existing Genie 4 `.cfg` files and folds them in, per-category, with merge/replace options. See [Importing from Genie 4](Importing-Genie4-Config).

## Related

- [Scripting](Scripting) — variables and triggers lead naturally into scripts.
- [The Interface](The-Interface) — where highlights and streams show up.
- [Importing from Genie 4](Importing-Genie4-Config) — bring existing rules across.
