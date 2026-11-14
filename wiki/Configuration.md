# Configuration & Rules

Genie 5 ships all of Genie 4's **rule engines** — the pattern-driven helpers that color text, expand shortcuts, react to the game, and bind keys. You manage them two ways:

- **The Configuration dialog** — **Edit → Configuration…** opens a tabbed, form-based editor. The tabs are **Layout** (with Windows, Settings and Server Dialogs sub-tabs), **Presets**, **Highlights** (with Strings and Names sub-tabs), **Triggers**, **Substitutes**, **Gags**, **Shunts**, **Aliases**, **Scripts**, **Text-to-Speech**, **Macros**, **Variables**, and **Classes** — so it covers script settings and [Text-to-Speech](Text-to-Speech) alongside the rule engines. The list-based rule tabs each have a **type-to-filter box**, so a several-hundred-line trigger list stays navigable.
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

The **File ▸ Master Toggles** submenu has a master switch for each engine — **Highlights**, **Triggers**, **Substitutes**, **Gags**, **Aliases**, and **Images** — so you can silence a whole rule type without touching the rules. Everything stays loaded and editable while an engine is off. Each toggle is also a `#config` key, and the menu stays in sync whichever way you flip it:

```
#config triggers off
#config highlights on
```

(The Images toggle's key is `showimages`; it clears or re-fetches the room art live.) For finer-grained switching, use [classes](#classes--grouping-rules).

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
#highlight {You are bleeding} {red}
#highlight {whispers} {yellow}
```

The pattern comes first, then the colour. (Genie 4's order, with the match type in front, works too: `#highlight {string} {red} {You are bleeding}`.)

Highlights support foreground and background colors, whole-line vs. substring matching, and case sensitivity — all editable in the Highlights tab. They paint in **every window** — the game window, the stream tabs, and the Room / Mobs / Players panels.

A rule can also be **scoped to specific windows**: the Highlights tab's **Windows** field takes a comma-separated list of window ids (`main`, `room`, `mobs`, `players`, `backpack`, `experience`, a tracker panel by name like `Active Spells`, or a stream tab like `thoughts`); leave it blank for everywhere, which is the default for every existing and imported rule. Ids are case-insensitive. The same list works as the last argument of `#highlight add`.

![An inventory list with six different substring highlights colored, plus a clickable command link](images/config-highlights-in-action.png)

*Substring highlights in action — six rules coloring an inventory list.*

Two built-in colorings live alongside your own rules:

- **Presets** — the game's own text categories (room descriptions, whispers, speech, …) render in palette colors you can change on the **Edit → Configuration… → Presets** tab. Presets also color panels: the `creatures` preset is the creature color in both the main window and the Mobs panel. The `familiar` preset is the base color of the Familiar window's text, and the `inputuser`, `inputother` and `scriptecho` presets color typed commands, script commands, and script output / Genie messages. The `health`, `mana`, `stamina`, `spirit` and `concentration` presets color the vitals bars: the color is the bar's fill and the background its track. (Presets used to sit under Highlights, as in Genie 4; they have their own tab now, just before Highlights.)
- **MonsterBold** — creature and NPC names DragonRealms marks as monster-bold render in a distinct color (default gold) in the main window, the stream windows, and the Room panel. On by default; toggle it on the **Edit → Configuration… → Presets** tab or with `#config monsterbold on|off`.

### Substitutes — rewriting text

Replace matched text with your own before it's shown (useful for shortening noisy messages). Managed with `#sub` / `#substitute`; list them with `#subs`.

- **Whole words only** — a per-rule box on the Substitutes tab (or a trailing `wholeword` on `#substitute add {pattern} {replacement} [{class}] wholeword`) matches only at word boundaries, so a substitute for "take" doesn't rewrite the inside of "mistake".
- **`$globals` in the replacement** — `$charactername`, `$roomid` and the other globals in the replacement text are filled in when the line matches, so a rule can swap in your own name or tag a line with the room it came from. A replacement with no `$` stays literal.

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

## Server dialogs — windows the game sends

DragonRealms describes some windows itself — the bank, stores, spell choice, feats, your profile, TDP, and more. Genie builds each one from the controls the server sends and shows it as an ordinary panel you can dock, float, or leave in its own window. The first time a dialog Genie hasn't seen arrives, Genie asks where it should go, and remembers the answer **per character profile**.

**Configuration → Layout → Server Dialogs** lists every answer you've given, with its **Where** and **Auto-open** settings. Select a dialog to change it:

| Where | What it does |
| --- | --- |
| **Its own window** | A panel of its own, docked in the right-hand column (the default). |
| **Where DR suggests** | Follows the placement hint the game sends: `right` or `left` docks it on that side, `center` (and `detach`) floats it centred over the main window, and `force-center` re-centres the float every time it opens (confirmation-style dialogs). No hint docks it on the right. |
| **Beside another window** | Opens it as a tab in the same group as a window you pick, docked or floating. |
| **Never show it** | Never render it, and stop asking. |

- **Your placement wins.** Once you've moved a dialog — floated it, or docked it somewhere else — it comes back where you left it rather than where the hint says (`force-center` excepted).
- **Open it automatically when DragonRealms sends it** — untick it and the window still fills in, but stays closed until you open it from **Window → Server Dialogs**, which lists every dialog seen this session.
- **Forget** drops the answer, so Genie asks again the next time the dialog appears.
- **Some dialogs are never asked about.** DragonRealms' quick-bar launcher entries are left hidden. Another character's injuries window (`injuries-` and a number, titled "*Name*'s Injuries", sent when an empath touches a patient) fills in without opening, because the [Healing window](The-Interface#the-healing-window) shows its health bar, its buttons and its transferable parts. Open it from **Window → Server Dialogs**, or give it an answer here to change that.
- **Show server dialog windows** at the top is the master switch, `#config serverdialogs on|off` (default on). Off, no dialog window opens and nothing asks; the contents keep updating in the background, and your answers are kept.

From the command bar, `#dialogs` (or `#dialogs list`) lists the dialogs seen this session, `#dialogs forget <id>` is the same as **Forget**, and `#dialogs report <id>` opens a pre-filled, redacted GitHub issue draft for a dialog that doesn't render right — nothing is posted until you submit it.

## Themes

A theme sets the colours of the whole client: the window chrome (backgrounds, borders, labels, accents) and the starting colours of the game text, your echoes and the vitals bars. Seven themes are built in: **Dark** (the default), **Light**, **Genie 4 Classic**, **High Contrast**, **Solarized Dark**, **Solarized Light** and **Wrayth-style**. You can also make your own, and share them as files.

A theme changes colours only. For Genie 4 title bars, use windowed mode (**Layout → Windowed Mode (MDI)**) or the built-in **Heirloom** layout; see [Docking Windows](Docking-Windows#windowed-mode-mdi).

### Picking a theme

There are two places to pick one:

- **Edit → Theme** lists every theme (yours are marked *(Custom)*). Clicking one applies it straight away. The same menu has **Edit Theme…**, **Save Current As…** and **Reset to Dark**.
- **Edit → Display Settings… → Theme** has the same list. Picking a theme there applies it behind the dialog so you can see it. **OK** keeps it; **Cancel** (or closing the dialog) puts back the previous theme *and* the game and echo colours you had when you opened it.

The active theme is shared by every character, and Genie remembers it between launches.

### What a theme contains

| Part | What it colours | When the theme sets it |
| --- | --- | --- |
| **Base variant** (Dark or Light) | The standard controls: menus, buttons, text boxes, scrollbars | Every launch |
| **Surfaces, text, accents** | Window and panel backgrounds, toolbars, borders, labels, hints, section headers, links, badges | Every launch |
| **Game text** | The main window's text colour | Only when you pick the theme |
| **Echo colours** (three) | Commands you type, commands scripts send, and script output / Genie's messages | Only when you pick the theme |
| **Vitals bars** (a fill and a track for each of five) | The health, mana, stamina, spirit and concentration bars, in the Vitals panel and the status strip | Only when you pick the theme |

The difference in the last column is the important one:

- **Chrome colours always come from the theme.** To change them, edit the theme (or make a copy and edit that).
- **Game text, echo and vitals colours are copied from the theme when you pick it**, and after that they're your own settings. Change them in Display Settings, the theme editor or the Presets tab, and your change stays — across launches and reconnects — until you pick a theme again.

### The game text and echo colours

These four colours each have **one** value, which you can change from three places. They are the same setting, not copies:

| Display Settings / theme editor | Where else | Colours | Stock default |
| --- | --- | --- | --- |
| **Game text** | — | Game text in the main window that nothing else colours | `#CCCCCC` |
| **Typed commands** | the `inputuser` preset | Commands you type at the command bar | Yellow |
| **Script commands** | the `inputother` preset | Commands a script sends (`put`, `move`, …) | GreenYellow |
| **Echoes & messages** | the `scriptecho` preset | Script `echo` lines, `#echo`, and Genie's own messages such as `[theme] reset to Dark` | Cyan |

The three echo colours are Genie 4's presets of the same names, so they also appear on **Edit → Configuration… → Presets**. A preset you import from Genie 4 keeps its colour until you pick a theme.

**What wins on an echo line.** A colour given in the command, such as `#echo red Danger!`, beats the preset. A preset set to `Default` has no colour of its own, so the line uses the game text colour. **Italicize local echoes** (Display Settings) is separate and only changes the style.

**Which file the echo colours are saved to.** They are saved like any other preset:

- To the **shared** presets (`Config/presets.json`), normally.
- To **this character's** presets, if you're connected and that character has its own override of the preset (Presets tab, Scope = This character). A theme you pick while connected updates that override too.

**What sets these colours back to a theme's colours:**

- Picking any theme, from the menu or Display Settings — including the one already active.
- **Edit → Theme → Reset to Dark**.
- **Reset** in Display Settings, which puts back the *active* theme's colours (or the stock defaults when no theme sets them).
- **Import…** in Display Settings, which picks the imported theme.

Launching Genie, connecting, and changing a theme's chrome don't touch them.

**To keep your own colours with a theme,** use **Edit → Theme → Save Current As…** (below). The new theme stores the colours you have now, so picking it later puts them back.

### The vitals bars

The five vitals bars are coloured by Genie 4's presets, in both the **Vitals** panel and the status strip, as in Genie 4:

| Bar | Preset | Stock fill |
| --- | --- | --- |
| Health | `health` | Maroon |
| Mana | `mana` | Navy |
| Fatigue (stamina) | `stamina` | Green |
| Spirit | `spirit` | Purple |
| Concentration | `concentration` | Navy |

The spell preparation (cast) bar on the hands strip uses Genie 4's `castbar` preset the same way (stock fill Magenta, no track), and so does the spell box's fill with **Layout → Combined Spell Timer** on.

- **The preset's colour is the bar's fill; its background is the track** (the empty part) and the border. With no background, the bar shows its normal track. The stock presets have none, as in Genie 4.
- **Edit them** on **Configuration → Presets**, or as the **Vitals bars** fill and track rows in the theme editor. Either way it's the same preset, and the bars repaint straight away.
- **A theme carries a fill and a track for each bar** and writes both into the presets when you pick it, like the echo colours: they're saved the same way, and the same actions set them back to the theme's colours. Dark and Genie 4 Classic use Genie 4's stock colours with no track; the other built-in themes have tracks shaded to suit their background.
- A preset you import from Genie 4 keeps its colours until you pick a theme.

### Making your own theme

**Edit → Theme → Edit Theme…** opens the theme editor on the active theme:

- **Theme name** — the name to save under. A built-in theme can't be overwritten, so editing one suggests a name like *My Dark* and saves a copy.
- **Base variant** — Dark or Light styling for the standard controls.
- **Colour rows** in five groups: **Surfaces**, **Text**, **Accents**, **Vitals bars** and **Game text**. Each row has a colour picker, with transparency (alpha) supported.
- The vitals and game text groups start with the colours you're using now (Display Settings and Presets), not the theme file's, so you edit what you see. In the vitals group, a fully transparent colour means none: `Default` for a fill, the normal track for a track.
- **Every change previews live** in the window behind the editor.
- **Save** writes the theme to `Config/Themes` and makes it active, game, echo and vitals colours included. **Cancel** puts everything back the way it was when you opened the editor.

Other ways to make or manage themes:

| Where | Action | What it does |
| --- | --- | --- |
| Edit → Theme | **Save Current As…** | Saves the active theme under a new name, with your current game text, echo and vitals colours. Picking an existing custom name overwrites it. |
| Display Settings → Theme | **Duplicate…** | Copies the selected theme to a new custom theme without switching to it. The usual way to start from a built-in. |
| Display Settings → Theme | **Import…** | Adds a theme `.json` file to `Config/Themes` and picks it. It never overwrites a theme: a name that's taken gets `(2)`, `(3)`, … |
| Display Settings → Theme | **Export…** | Saves the selected theme (built-in or custom) as a `.json` file to share or edit. |
| Display Settings → Theme | **Delete** | Deletes the selected custom theme's file. Built-in themes can't be deleted. |

### Theme files

Each custom theme is a JSON file in `Config/Themes` (see [Application Folders](Application-Folders)). You can write or edit one by hand; Genie re-reads the folder each time you open the **Edit → Theme** menu.

```json
{
  "Name": "Midnight",
  "BaseVariant": "Dark",
  "Colors": {
    "Theme.WindowBg":   "#0B0F1A",
    "Theme.Accent":     "#7FB2FF",
    "Theme.Selection":  "#447FB2FF",
    "Theme.GameText":   "#D0D0D0",
    "Theme.InputUser":  "#FFD75F",
    "Theme.InputOther": "#9FE870",
    "Theme.ScriptEcho": "#5FD7D7",
    "Theme.HealthBar":  "#C03030",
    "Theme.HealthTrack": "#300C0C"
  }
}
```

- Colours are `#RRGGBB`, or `#AARRGGBB` with transparency.
- **List only the colours you want to change.** A missing chrome colour comes from the Dark theme. A missing game text, echo or vitals colour is left as it is when the theme is picked. An empty vitals track (`""`) means none.
- A file whose `Name` matches a built-in theme is skipped. If `Name` is empty, the file name is used.
- Comments and trailing commas are allowed.
- **Older theme files** have vitals fills but no tracks, so picking one leaves your tracks as they are. They may also have a single `Theme.GameEcho` colour (from before the three echo colours). If a theme has that and none of the three echo keys, picking it sets all three echo colours to it. **Save Current As…** and the theme editor write the three keys instead.

All the keys, with their names in the theme editor:

| Key | Theme editor | Key | Theme editor |
| --- | --- | --- | --- |
| `Theme.WindowBg` | Window background | `Theme.Accent` | Accent (links, exits, compass) |
| `Theme.PanelBg` | Panel background | `Theme.AccentBg` | Accent background |
| `Theme.PanelBgDeep` | Log / output wells | `Theme.AccentBorder` | Accent border |
| `Theme.ToolbarBg` | Toolbars | `Theme.Success` | Success (players, ok badges) |
| `Theme.StripBg` | Strips (hands / status bars) | `Theme.SuccessBg` | Success background |
| `Theme.Border` | Borders & separators | `Theme.Warning` | Warning (stale) |
| `Theme.Selection` | Text selection | `Theme.WarningBg` | Warning background |
| `Theme.TextPrimary` | Text — primary | `Theme.Danger` | Danger (errors, destructive) |
| `Theme.TextSecondary` | Text — secondary | `Theme.DangerBg` | Danger background |
| `Theme.TextMuted` | Text — muted / hints | `Theme.GameText` | Game text |
| `Theme.SectionHeader` | Section headers | `Theme.InputUser` | Typed commands (`inputuser`) |
| | | `Theme.InputOther` | Script commands (`inputother`) |
| | | `Theme.ScriptEcho` | Echoes & messages (`scriptecho`) |

The vitals keys, a fill and a track per bar, set the preset's colour and background:

| Fill key | Track key | Theme editor | Preset |
| --- | --- | --- | --- |
| `Theme.HealthBar` | `Theme.HealthTrack` | Health fill / track | `health` |
| `Theme.ManaBar` | `Theme.ManaTrack` | Mana fill / track | `mana` |
| `Theme.StaminaBar` | `Theme.StaminaTrack` | Stamina fill / track | `stamina` |
| `Theme.SpiritBar` | `Theme.SpiritTrack` | Spirit fill / track | `spirit` |
| `Theme.ConcBar` | `Theme.ConcTrack` | Concentration fill / track | `concentration` |

### Built-in game text and echo colours

| Theme | Game text | Typed commands | Script commands | Echoes & messages |
| --- | --- | --- | --- | --- |
| Dark | `#CCCCCC` | `#FFFF00` | `#ADFF2F` | `#00FFFF` |
| Light | `#202020` | `#806000` | `#3C7800` | `#00708A` |
| Genie 4 Classic | `#C0C0C0` | `#FFFF00` | `#ADFF2F` | `#00FFFF` |
| High Contrast | `#FFFFFF` | `#FFFF00` | `#ADFF2F` | `#00FFFF` |
| Solarized Dark | `#839496` | `#B58900` | `#859900` | `#2AA198` |
| Solarized Light | `#657B83` | `#B58900` | `#859900` | `#2AA198` |
| Wrayth-style | `#F0F0F0` | `#FFFF00` | `#ADFF2F` | `#00FFFF` |

The dark themes use Genie 4's own defaults (Yellow, GreenYellow, Cyan). Light and Solarized use darker or matching shades so the text stays readable on their backgrounds.

### Coming from an earlier Genie 5

**Vitals bars.** The bars used to take fixed colours (the status strip) or the theme's bar colours (the Vitals panel), whatever your presets said. Both now follow the `health` … `concentration` presets: your imported Genie 4 colours, values already saved in your presets, or otherwise Genie 4's stock colours. Pick a theme to get its bar colours.

**Echo colours.** Earlier versions had a single **Echoes** colour in Display Settings, used for every echo line. It's been replaced by the three presets above, so typed commands, script commands and messages can differ, as in Genie 4. Your old Echoes colour isn't carried over. Echo lines use your `inputuser` / `inputother` / `scriptecho` presets — your Genie 4 values if you imported them, otherwise the stock defaults. Custom themes you saved before still work (see *Older theme files* above).

## Where rules are stored

Each rule type saves to its own **`.json` file** — `highlights.json`, `triggers.json`, `substitutes.json`, `gags.json`, `shunts.json`, `aliases.json`, `variables.json`, `classes.json` (plus `macros.json`) — with a Genie 4-format **`.cfg` twin** (one entry per line, the commands that recreate the rule) written alongside it so your rules still round-trip with the Genie 4 ecosystem. Rules live in **two layers**:

- **All characters** — the shared set in `Config/`.
- **This character** — that character's own rules in `Profiles/<Character>-<Account>/`, which **layer over** the shared set: the character's rules apply first, and every shared rule the character hasn't overridden (same rule key) shows through underneath.

Every rule editor has a **Scope** field ("This character" / "All characters" — new rules default to this character) and a Scope column, and each edit saves back to the file the rule actually lives in. That includes **Variables**, **Classes** and the **Layout → Windows** settings (`windows.json`), where the window list shows each window's layer in brackets. A **scope filter** next to each tab's Find box shows every rule, only this character's, or only the shared ones.

Shared rules get per-character safeguards: deleting a shared rule while connected asks whether to **disable it just for this character** (a reversible local opt-out) or remove it for everyone, and the Toggle button on a shared rule quietly writes the same local opt-out. Going the other way, **deleting a this-character rule that was overriding a shared one brings the shared one back straight away**, with no reconnect needed. The status line says so. On the Presets and Layout tabs, **Reset** on a this-character override does the same: it goes back to the shared setting when there is one, and to the built-in default otherwise.

A variable or class that a script or command changes while you play (for example `#var`, `#class combat off`) becomes this character's value when it's next saved, so one character's session never rewrites the shared set. See [Application Folders](Application-Folders) for the full layout.

**Hand-editing is supported — even live.** External edits to the eight rule `.json` files (all of the above except `macros.json`) — in either layer — apply to the running client **without a reconnect**: Genie watches them, reloads the engines, and prints a `[config] … reloaded` line in the game window. (A file with a syntax error is left alone — nothing is cleared until it parses.) Other config files — display settings, themes, layouts, macro keybindings — still load at startup/connect.

## Importing from Genie 4

You don't have to recreate any of this by hand — **File → Import from Genie 4…** reads your existing Genie 4 `.cfg` files and folds them in, per-category, with merge/replace options. See [Importing from Genie 4](Importing-Genie4-Config).

## Related

- [Scripting](Scripting) — variables and triggers lead naturally into scripts.
- [The Interface](The-Interface) — where highlights and streams show up.
- [Importing from Genie 4](Importing-Genie4-Config) — bring existing rules across.
