# Alterations

The **Alterations** menu is Genie 5's built-in alteration designer: a place to
draft the tap / look / read text for an item alteration against DragonRealms'
length budgets, and to keep a library of designs between sessions.

It replaces the Genie 4 **Alteration Buddy** plugin by
[Djordje](https://github.com/mj-colonel-panic/AlterationBuddy) (GPL-3.0), whose
source was contributed to the project by Etherian in August 2026. The four
design fields, their limits, and the composed result line are a deliberate
one-for-one match with that plugin — a Genie 4 player should recognise it
immediately, and their existing `alterations.csv` imports directly.

## Why a menu and not a window

Every other first-class panel in Genie 5 — Vitals, Mobs, Room, Experience,
Inventory View — tracks the live session and earns a permanent slot in the dock
layout. Designing an alteration does not: nothing on this screen changes while
you play, it is used occasionally and deliberately, and a docked copy would sit
idle competing for space with panels that actually update.

So it takes the same shape as **Maps**: its own top-level menu holding the
actions, with the editor itself in a dialog. Nothing here depends on a live
connection, a `GenieConfig`, or a parsed game event, so the whole feature works
offline and before you connect.

## The menu

| Item | What it does |
| --- | --- |
| **Open Designer…** | The editor: four fields, live counters, composed result, and the saved library. |
| **Saved Designs ▸** | Rebuilt from the library each time it opens; picking one opens the designer with it loaded. |
| **Import from Genie 4…** | Merges an Alteration Buddy `alterations.csv`. Imports **append** — they never replace what you already have. |
| **Export for Genie 4…** | Writes the library back out in Alteration Buddy's format. |
| **Reload Library** | Re-reads `alterations.json` after a hand-edit or a file sync. |
| **Open Library Folder** | Opens the Config directory. |
| **Elanthipedia Alteration Guide** / **The Witch's Workshop** | The two community references the original plugin linked. |

## The budgets

| Field | Limit | Notes |
| --- | --- | --- |
| Short Tap | 15 characters **per word** | DR budgets this as article / adjective / noun. Alteration Buddy showed "15/15/15" as a static hint; Genie 5 measures each word and flags the ones over. |
| Tap | 80 characters | |
| Look | 500 characters | Multi-line. |
| Read | 10 words **and** 50 characters | Both are reported; either can be the binding constraint. |

Going over a budget does not block a save — you may be drafting, and what a GM
will accept is their call. The designer says which fields are over and saves
anyway.

Two counter bugs in the original are fixed here, and pinned by tests so a later
"restore parity" pass can't reintroduce them:

- An **empty** read field reported nine words remaining, not ten:
  `"".Split(' ')` yields one empty element, so a blank field had already spent a
  word.
- `"a  b"` counted as three words, because every run of consecutive spaces added
  one.

## Storage

Designs live in **`{Config}/alterations.json`** — the shared Config directory,
not a profile directory. Designs are ideas for items, and players move them
between characters freely; this also matches where Alteration Buddy kept its own
file. The file is read at startup and written on every save, delete, and import.

A corrupt `alterations.json` is **reported, not swallowed**. The library throws
on a parse failure rather than returning an empty list, because presenting an
empty designer and then overwriting the file on the next save would destroy the
user's real designs.

`alterations.json` is **not** one of the seven live-reloaded rule files (see
`RuleFileWatcher`) — use **Reload Library** after a hand-edit.

### Genie 4 interop

Alteration Buddy's format is one design per line, tab-separated, no header and
no escaping of any kind:

```
ShortTap<TAB>Tap<TAB>Look<TAB>Read
```

That format has a latent bug: the Look box was multi-line, and the writer
emitted the field verbatim, so a design with a line break in its Look text was
written across several physical lines and could not be read back correctly by
the plugin itself. Our importer recovers those: a physical line containing no
tab is treated as a continuation of the previous design's Look field rather than
as a new, mangled design.

On export, embedded tabs and newlines are flattened to spaces — the format
cannot represent them, and one raw tab would shift every following field.
`Title` and `Notes` are Genie 5 additions with no home in the old format and are
dropped on export.

## Code map

| Piece | Where |
| --- | --- |
| Design record + Genie 4 line round-trip | [`AlterationDesign.cs`](../src/Genie.Core/Alterations/AlterationDesign.cs) |
| Limits, budgets, counter text | [`AlterationLimits.cs`](../src/Genie.Core/Alterations/AlterationLimits.cs) |
| Result-line composition | [`AlterationFormatter.cs`](../src/Genie.Core/Alterations/AlterationFormatter.cs) |
| Library + JSON store + CSV import/export | [`AlterationLibrary.cs`](../src/Genie.Core/Alterations/AlterationLibrary.cs) |
| Menu state, load/save, import/export | [`AlterationsViewModel.cs`](../src/Genie.App/ViewModels/AlterationsViewModel.cs) |
| The editor dialog | [`AlterationDesignerDialog.axaml`](../src/Genie.App/Views/AlterationDesignerDialog.axaml) |
| Menu + Saved Designs builder | [`MainWindow.axaml`](../src/Genie.App/Views/MainWindow.axaml), `OnAlterationsMenuOpened` |

All measurement and formatting is in `Genie.Core` and UI-free, so a future
`#alteration` script command can reuse it without touching the dialog.

## Attribution

Ported from **Alteration Buddy** by Djordje (GPL-3.0), who has since passed
away. Genie 5 ships under the same licence. The behaviour, field names, limits,
and result format are his design; the storage format, per-word short-tap
measurement, and the counter fixes above are ours.
