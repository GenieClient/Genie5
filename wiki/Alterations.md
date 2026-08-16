# Alterations

The **Alterations** menu is a built-in workshop for designing item alterations:
draft your tap, look and read text against DragonRealms' length limits, see
exactly how much room you have left as you type, and keep a library of designs
between sessions.

If you used **Alteration Buddy** in Genie 4, this is that tool brought in-house —
same four fields, same limits, same result line, and your existing
`alterations.csv` imports straight in.

> **Coming in the next release.** This isn't in the current build yet. Everything
> below describes what ships next.

Nothing here needs a connection. You can design alterations, save them, and
browse your library without logging in at all.

## Designing an alteration

**Alterations ▸ Open Designer…**

Fill in as many of the four fields as you need. Under each one, a counter tells
you how much of that field's budget you have left, and turns to an "over" count
if you go past it. The **Result** box at the bottom builds itself as you type —
that's the line you hand to the merchant or GM:

```
Short Tap: a razor-edged scimitar \ Tap: a wickedly curved scimitar with a razor edge \ Look: The blade curves back on itself in a single unbroken arc. \ Read: "For she who does not yield"
```

Leave a field blank and it drops out of the result entirely. **Copy Result** puts
the line on your clipboard.

### The limits

| Field | Limit | Notes |
|---|---|---|
| **Short Tap** | 15 characters **per word** | The item's inventory name. The game budgets this as article / adjective / noun — 15 characters each. The counter shows each word's length and flags any that are over. |
| **Tap** | 80 characters | Shown when the item is tapped. |
| **Look** | 500 characters | Shown on LOOK. Multi-line — write it out properly. |
| **Read** | 10 words **and** 50 characters | The inscription shown on READ. Both are counted, and either one can be what stops you. |

Going over doesn't block you from saving. You might be drafting, and what a GM
will actually accept is their call — Genie just tells you which fields are over
and saves anyway.

**Title** and **Notes** are yours, not the game's. Title is how the design shows
up in your menu; Notes is a scratch space for the merchant, the festival, the
cost, or whatever you want to remember.

## Your design library

**Save** stores the design. If you loaded an existing one, Save updates it;
otherwise it's added as a new entry.

Saved designs appear in two places:

- **Alterations ▸ Saved Designs** — pick one to open it straight in the designer.
- The list on the right of the designer — double-click to load, or select and use
  **Load** / **Delete**.

Designs are shared across all your characters. They live in **`alterations.json`**
in your Config folder (**Alterations ▸ Open Library Folder** takes you there; see
[Application Folders](Application-Folders)). If you hand-edit that file or sync it
between machines, use **Alterations ▸ Reload Library** to pick up the changes.

## Bringing designs over from Genie 4

**Alterations ▸ Import from Genie 4…** and pick your old `alterations.csv` — it
sits in the Alteration Buddy plugin folder in your Genie 4 install.

Imports are **added** to your library. They never overwrite designs you've
already built here, so it's safe to import more than once from more than one
place.

Genie 5 reads the old file more forgivingly than Genie 4 wrote it. The old format
had no way to store a line break, so a design with a multi-line Look could end up
saved in a state Genie 4 itself couldn't read back — those come across intact
here.

**Export for Genie 4…** writes your library back out in the old format if you
need it elsewhere. Title and Notes have no home in that format and are left
behind.

## Reference links

The menu carries the two references worth having open while you write:

- **Elanthipedia Alteration Guide** — what merchants and GMs will and won't do.
- **The Witch's Workshop** — long-running community reference on alteration design.

## Credits

Alteration Buddy was written by **Djordje**, who has since passed away. It was
released under the GPL-3.0, the same licence Genie 5's own plugins ship under,
and this feature is a direct port of his work — the fields, the limits and the
result format are his design.

---

See also: [The Interface](The-Interface) · [Importing from Genie 4](Importing-Genie4-Config) · [Application Folders](Application-Folders)
