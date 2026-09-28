# The Mapper

The mapper knows where you are, draws the zone you're in, and can walk you to any room you pick. It reads the community zone maps (the same XML format Genie 4 uses) and tracks your position as you move.

## The map panel

![The Mapper showing all of Riverhaven — 568 rooms with guild labels, the exit-type legend, and the current room highlighted](images/mapper-panel.png)

The Mapper panel (**floating** in its own window by default; dock it by dragging, or toggle it via **Maps ▸ Show Mapper**) shows the current zone with your room highlighted. Rooms are drawn as nodes connected by their exits, color-coded by exit type (compass directions, vertical moves, and special exits like `go gate` or `climb wall`). Scroll to zoom; drag to pan.

As you walk, Genie matches each new room to a node and re-centers on you. When you enter a room it doesn't recognize in the current zone, it can auto-switch to the zone that contains it.

### Zones, floors, and the legend

- **Zone picker** — the dropdown at the top lists every zone in your Maps folder; auto-detect normally picks for you as you walk. A sort control beside it orders the list by **Name**, **Recently Changed**, or **Map Number** (special event/quest maps are badged *SPECIAL* and sort to the bottom by number).
- **Return to Current Zone** — while you're browsing a zone your character isn't in, tracking pauses and a **⌖ Return to Current Zone** button appears; click it to jump back and resume following.
- **Floors** — **▲ / ▼** step the map up and down a level (the `L0` readout shows which). The floors directly above and below yours draw as a faint white "ghost" map under the current one, the way Genie 4 grays out other levels. The **Map overlay opacity** slider in **Maps ▸ AutoMapper Settings…** (`#config automapperalpha`, 0–255) fades them; 0 hides them for a pure single-floor view.
- **Legend** — **Maps ▸ Show Map Legend** (on by default) draws a colour key in the map's top-left corner. It lists only what the current floor actually draws — room colours named from the community Maps colour key, cross-zone rooms, ghost floors, and each kind of path — so an entry you see always means something on screen.
- **Labels** — the **Labels** toggle shows or hides the zone's landmark labels (the map's own text, like "East Gate").

### Map spoilers

Community maps record secret exits — `search` / `objsearch` arcs and hidden quick-send moves. If you'd rather discover those yourself:

- `#config showmapspoilers off` hides them on the map and from the [Less Obvious Paths](#less-obvious-paths) buttons (default **on**).
- `#config avoidmapspoilers on` keeps `#goto` and click-to-walk from routing through them, so a walk can't lead you through a secret either (default **off**).

Both affect display and routing only — the map files themselves are untouched.

## Finding your room

Genie identifies your current room from what the game sends — the room title, the obvious paths, the description, and (when present) the server's room id. It resolves position with a priority ladder: a known server-room id is definitive; otherwise it follows the exit you just used from your last known room; otherwise it fingerprints the room (title + exits) and disambiguates by neighborhood and description. When it genuinely can't tell, it declines rather than guessing — a wrong lock-in would cascade through every later move.

### Lookup vs. learning

- **Lookup-only (default)** — Genie matches you against the community map but never changes the map files.
- **Learning (AutoMapper toggle on)** — Genie also stamps server-room ids onto matched rooms, records exits it sees you use, and adds new rooms it doesn't recognize, saving the zone back to disk. This makes your future visits resolve instantly and fills gaps in the community map.

## Click-to-walk

Right-click a room on the map and choose **Go Here** (or **Ctrl+click** the room — a plain click never starts a walk, so you can't walk by accident) and Genie plans a route and walks you there, one room at a time. Pathfinding is **skill-aware**: exits your character can't take — a climb beyond your skill, a guild-locked door, a level-gated arc — are excluded from the route, so it won't try to send you somewhere you can't go.

![The Mapper's skills banner offering "Fetch skills now", "Skip", and "Don't ask again"](images/mapper-skills-banner.png)

The first time skill-aware routing needs your numbers, the Mapper shows a one-time banner offering to **fetch your skills** (it sends the game's own skill command and reads the reply); **Skip** routes without skill filtering, and **Don't ask again** silences the banner.

Walking goes through the normal command path (the same one your typing uses), so roundtime is handled automatically and your aliases/triggers still apply. If you get knocked off the planned route, the walk **cancels** rather than firing the wrong command.

![A walk in progress — "Walking to North Road, Plains — 87 of 119 rooms · Esc to cancel" with a red Cancel button](images/mapper-walk-strip.png)

### Attended-mode rules (why it's safe)

The walker is deliberately conservative — it assists an attentive player and is responsive to your intent (a click or `#goto`), stepping under roundtime gating rather than firing a burst of commands:

- **Cancels** on **Esc**, on any command you type, on disconnect, or if you walk off the planned path.
- **Never auto-resumes** across a disconnect — a fresh walk needs a fresh click.
- A visible strip shows progress and a Cancel/Resume control.
- **Optional:** an idle pause can suspend a walk after the window has been unfocused for a configurable interval. It's **off by default** (DR policy is about responsiveness, not window focus); turn it on if you want the extra backstop, then click **Resume** to continue.

See [Policy Compliance](Policy-Compliance) for the full reasoning.

### Community automapper.cmd hand-off

If the community **automapper.cmd** script is in your Scripts folder (or the
repo-scripts folder), `#goto` and click-to-walk **hand the planned route to the
script** instead of walking natively — exactly what Genie 4 did. That matters on
maps whose paths carry special-move directives (`script ggbypass`, `ice nw`,
`swim …`, timed waits): the community script knows how to execute those, and it
honors its own pacing globals (`$caravan`, `$powerwalk`, …). Starting a new
`#goto` while one is running restarts the script with the new route (governed by
`#config abortdupescript`). Without the script installed, the built-in walker
handles everything as described above; `#config automapperscript false` forces
the built-in walker even when the script is present. Cross-zone routes always
use the built-in walker.

Arcs whose move is `script <name>` can only be executed by the community
script, so the built-in walker (and the cross-zone pathfinder) never plans a
route through one — it finds another way, or reports no route, rather than
sending the directive to the game as text.

### Walking to a tagged room

Rooms can carry free-form **tags** (`bank`, `forge`, `healer`, …), and
`#goto @bank` walks you to the **nearest** room in the zone tagged `bank`. Tag
the room you're standing in with `#mapper tag add bank` (`#mapper tag remove
bank` to undo); `#mapper tag list` shows the current room's tags, and
`#mapper tags` lists every tag in the zone with its count. Tags can also be
edited in the Details panel (see [Editing maps](#editing-maps)).

## Less Obvious Paths

DragonRealms rooms have "obvious paths," but maps also record the non-obvious connections (a trellis you can climb, an alley with no signposted exit). Genie surfaces these as clickable buttons so you can take them without memorizing the verb.

![The room strip — compass exits plus go-buttons for temple, town hall, portal, and meeting portal](images/mapper-less-obvious-paths.png)

## Room notes

You can add a note to a room (a landmark, a warning, a shop name) in the Mapper's **Details** panel — **Save Notes** writes it into the zone XML. Notes double as `#goto` names and show when you hover the room; they aren't drawn on the canvas. To put text on the map itself, add a **label** instead (below).

## Editing maps

The toolbar under the zone picker carries Genie 4's AutoMapper edit tools:

- **✎ Edit** — edit mode: click a room to select it, drag to move it. Off (the default), a click does nothing and right-click offers **Go Here**.
- **⏺ Record** — add new rooms to the active zone as you walk into them (Genie 4 Record Mode).
- **New** starts an empty zone; **Save** writes the active zone to its XML file (a dot marks unsaved edits).
- In edit mode: **Remove** deletes the selected room or label, **+ Label** adds a landmark label beside your room (or right-click ▸ **Add Label Here**), **Reset IDs** renumbers rooms 1..N, **Snap** snaps dragged rooms to Genie 4's 10px grid, **Lock** stops rooms being dragged, and **Dup** allows duplicate rooms while recording.

The **Details** panel slides out from the map's right edge when you hover the **DETAILS** strip (or click it to keep it open); the **📌** pin holds it open until you release it. It shows the current room's server id and notes, an **Edit Room** form for the selected room (title, notes, colour, server id, tags — **Apply**, then **Save** the zone), an **Edit Label** form for a selected label, the zone's room count and last update, and the map's background and text colours.

## Where maps live

Zone files are XML — one per zone — in your **Maps** folder (`Map1_Crossing.xml`, `Map60_Southern_Trade_Road.xml`, …), alongside an optional `ZoneConnections.xml` for extra cross-zone transit links (most links are read straight out of the maps' own border-room notes; the file augments and overrides those). Jump there with **Maps ▸ Open Maps Folder**; change the location with **Maps ▸ Change Maps Directory…**. See [Application Folders](Application-Folders).

Because Genie 5 uses the **same Genie 4 map format**, maps move between the two clients cleanly, and the 24+ community map forks all work.

## Getting and updating maps

- **From the community repo** — **Maps ▸ Update from Official Repo…** pulls the latest zone XML and merges it with your local progress (upstream layout changes come down; your stamped room ids survive).
- **Repairing damaged maps** — **Maps ▸ Repair Maps (full re-download)…** re-downloads every zone, including ones that haven't changed upstream, to undo the damage earlier builds did to exit types, hidden flags and room descriptions. Your own notes, colours and roundtimes are kept.
- **From a Genie 4 install** — on Windows, the first time Genie 5 starts with an empty Maps folder it copies your Genie 4 zone files across; otherwise copy the `*.xml` files in yourself.

Full details: [Updating Maps & Scripts](Updating-Maps-and-Scripts).

## Cross-zone travel

Routing **across** zones — boats, ferries, climb-walls between map files — is built in too. Most zone links are derived automatically from the maps' border-room notes, augmented by an editable transit table (**Maps ▸ Cross-Zone Connections…**). `#goto` a room in another zone, or click a room while browsing a different zone's map, and Genie routes across the boundary and walks you there. See [Cross-Zone Travel](Cross-Zone-Travel).

## Related

- [Cross-Zone Travel](Cross-Zone-Travel) — the multi-zone transit graph and editor.
- [Updating Maps & Scripts](Updating-Maps-and-Scripts) — keep maps current.
- [Policy Compliance](Policy-Compliance) — why the walker behaves the way it does.
