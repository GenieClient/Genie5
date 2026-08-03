# Cross-Zone Travel

Single-zone walking is handled by the [Mapper](Mapper) — right-click a room, choose **Go Here**, and Genie walks you there. Travelling **across** zone boundaries needs more: a graph that spans multiple zone files plus the transit links (boats, ferries, climb-walls, portals) that connect them. Genie 5 has the pathfinder, the data format, an editor for that graph — and the walker that executes the route.

> **Status:** shipped. Cross-zone routing and walking work end-to-end — `#goto` a room in another zone, or click a room while browsing another zone's map, and Genie plans across the boundary and walks you there (see [How a cross-zone walk starts](#how-a-cross-zone-walk-starts)).

## The transit graph

Most cross-zone links are **derived automatically from the maps themselves**: community zone files mark their border rooms with a note naming the neighbouring zone plus the move that leaves the zone, and Genie pairs those notes up (only links marked from *both* sides count, so a one-sided note never invents a route). Cross-zone routing works out of the box with nothing to configure.

On top of that, extra links (boats, portals — things the maps don't encode) live in a single **`ZoneConnections.xml`** at the root of your Maps folder, next to the `Map##_*.xml` zone files. Entries there **augment** the derived links, and **override** them when they name the same endpoints. Keeping them in one file lets the community Maps repo curate transit links without touching individual zone files.

Each connection is one directed link:

| Field | Meaning |
| --- | --- |
| `from-zone` / `to-zone` | Zone-file basenames without `.xml` (e.g. `Map01_Crossing`). |
| `from-room` / `to-room` | A node id, or `#serverRoomId`. |
| `verb` | What the walker sends (`board boat`, `climb wall`). |
| `transit-type` | A free-form tag (`boat`, `climb`, `ride`, `portal`). |
| `requires` | A skill / class / level gate. |
| `rt` | Roundtime seconds. |
| `wait-min` / `wait-max` | Scheduled-departure wait window (for boats/ferries). |
| `notes` | Community notes. |

```xml
<connections>
  <connection id="boat-cross-throne"
              from-zone="Map01_Crossing"  from-room="#37666999"
              to-zone="Map35_Throne_City" to-room="#37666500"
              verb="board boat" transit-type="boat"
              wait-min="300" wait-max="600"
              requires="" rt="0" notes="" />
</connections>
```

On first launch Genie **seeds** a documented starter template (example routes with placeholder room ids) so you have something to edit, and writes a marker so it never re-seeds — if you delete the file deliberately, Genie respects that. Connections that can't be resolved (stale zone/room refs) are simply skipped, so a half-filled file degrades gracefully — the derived border-room links keep working regardless.

## The pathfinder

The multi-zone pathfinder runs **Dijkstra over a meta-graph of (zone, room) pairs**, loading each zone lazily (read at most once per search). It draws edges from two sources:

- **Intra-zone** — each loaded zone's own room exits.
- **Cross-zone** — the derived border-room links merged with your `ZoneConnections.xml` entries.

Both kinds are gated against your character's live skills, class, and level — an edge you can't take is excluded from the search entirely. Edge weights:

- intra-zone: the same cost single-zone walking uses — `1` per room, plus a roundtime term (authored RT seconds, or an effort penalty inferred from the verb — swims and climbs cost more, scaled down by your Athletics rank), plus a term for scheduled waits
- cross-zone: `1 + RT/4 + averageWait/4`, plus the same Athletics-scaled effort penalty on the transit verb

Wait time dominates, so a boat with a long schedule is only chosen when there's no overland route. The result is an ordered list of steps, each carrying its verb and — for cross-zone hops — the expected wait window and target zone.

Rooms can be referenced by integer node id or by DragonRealms server-room id (`#NNNN`); the pathfinder resolves both, preferring the server-room id since it survives map regeneration.

## The editor

**Maps ▸ Cross-Zone Connections…** opens a grid editor: add, remove, edit, and save connections. This is the curation surface for the transit graph the pathfinder consults. You can also let the community Maps repo ship richer versions over time — see [Updating Maps & Scripts](Updating-Maps-and-Scripts).

## The walker

The walker executes cross-zone steps like any other: it sends the transit verb, and when a step carries a wait window (a boat schedule) it shows a countdown ("~4:23 left") in the Mapper indicator strip while it waits for the destination zone's room to fingerprint in. Arrival is confirmed by the destination zone matching, and the same attended-mode rules apply as for a single-zone walk — Esc, any typed command, or a disconnect cancels it.

## How a cross-zone walk starts

1. **`#goto` a room in another zone** — if the room id, label, or title doesn't match anything in your current zone, Genie looks it up across *all* your maps and routes there through the transit graph.
2. **Click while browsing another zone** — switch the map to a different zone, right-click a room → **Go Here** (or Ctrl+click), and Genie plans from where you actually are to the room you clicked, across the boundary.

## A note on scope

Genie 5 deliberately does **not** aim to absorb the entire community `travel.cmd` into the engine. Escape recipes for un-mappable starting rooms, premium-account shortcuts, and ferry-state recovery are well-suited to scripts and stay there. The engine targets the common land + scheduled-transit routes, with scripts remaining the fallback for the long tail.

## Related

- [The Mapper](Mapper) — single-zone tracking and walking.
- [Updating Maps & Scripts](Updating-Maps-and-Scripts) — where `ZoneConnections.xml` and zone files come from.
- The developer design notes: [multi-zone-travel.md](https://github.com/GenieClient/Genie5/blob/main/docs/multi-zone-travel.md), [AUTOMAPPER_DESIGN.md](https://github.com/GenieClient/Genie5/blob/main/docs/AUTOMAPPER_DESIGN.md).
