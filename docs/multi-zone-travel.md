# Multi-Zone Travel

**Status:** shipped — cross-zone routing and walking work end-to-end.

Unlike the single-zone walk (which [AutoMapperEngine.FindPath](../src/Genie.Core/Mapper/AutoMapperEngine.cs#L547) and [AutoWalkService](../src/Genie.App/Services/AutoWalkService.cs) drive — see [mapper.md](mapper.md)), travelling across zone boundaries needs a graph that spans multiple zone files plus the transit links (boats, ferries, climb-walls, portals) that connect them. Genie 5 has the pathfinder, the data model, the on-disk format, a UI editor for that graph, **and the walker integration**: `AutoWalkService.StartCrossZone` executes multi-zone plans (wait countdown + destination-zone fingerprint arrival), fed by `MapperViewModel.TryStartCrossZoneWalk`. This page documents how the pieces fit.

## The pieces that exist

### MultiZonePathfinder

[MultiZonePathfinder](../src/Genie.Core/Mapper/MultiZonePathfinder.cs) is **Dijkstra over a meta-graph of `(zoneFile, room)` tuples**. It loads zones lazily (each read at most once per search) and draws edges from two sources:

- **Intra-zone** — each loaded zone's `MapNode.Exits`.
- **Cross-zone** — [ZoneConnection](../src/Genie.Core/Mapper/ZoneConnection.cs)s: links **derived from the maps' own border-room notes** ([ZoneConnectionDeriver](../src/Genie.Core/Mapper/ZoneConnectionDeriver.cs)) merged with any hand-authored `ZoneConnections.xml` entries ([ZoneConnectionsRepository](../src/Genie.Core/Mapper/ZoneConnectionsRepository.cs) + `ZoneConnectionMerge`, wired in `MapperViewModel.Connections()`).

Both edge kinds honour [ExitRequirement](../src/Genie.Core/Mapper/ExitRequirement.cs) against the character's live `SkillStore` / class / level — an edge the character can't take is excluded from the search entirely. Weights:

- intra-zone: `AutoMapperEngine.IntraZoneEdgeCost` — the **same cost the single-zone pathfinder uses**: `1 + (RtCost + 1)/2` when the exit has an authored RT (else a verb-inferred, Athletics-scaled effort penalty) `+ averageWait/30`
- cross-zone: `1 + RtCost/4 + averageWait/4 + EffortPenalty(verb, athleticsRank)` — the effort term makes a "swim the Faldesu" link near-free for a skilled character while a ferry's wait keeps it costly

Wait time dominates, so a boat with a long schedule is only preferred when there's no overland route. The result is a [MultiZonePath](../src/Genie.Core/Mapper/MultiZonePathfinder.cs#L30): an ordered list of [WalkStep](../src/Genie.Core/Mapper/MultiZonePathfinder.cs#L12)s (each carrying its verb, and for cross-zone hops the expected wait window + target zone), plus a `HasCrossZoneHop` flag.

Rooms are referenced by either integer node id or DR server-room id (`#NNNN`); `TryMatchRoom` resolves both, preferring `ServerRoomId` since it survives map regeneration.

### Derived connections — the maps already encode the links

Most cross-zone edges are **derived, not authored**. Community maps don't ship a `ZoneConnections.xml` — they encode zone links in the rooms themselves: a **border room** carries a note whose first `|`-segment is the neighbour zone's `.xml` file (`note="Map8_Crossing_East_Gate.xml|E Gate|East"`) plus a destination-less directional arc that is the move out of the zone. [ZoneConnectionDeriver](../src/Genie.Core/Mapper/ZoneConnectionDeriver.cs) pairs these with the reciprocal border room on the far side (only links noted from *both* sides produce an edge, so a one-sided note never fabricates a route). **Cross-zone routing therefore works with no `ZoneConnections.xml` at all**; authored entries augment the derived graph, and override it on an exact endpoint match (`ZoneConnectionMerge`) — so the seeded placeholder baseline can't shadow real derived links.

### ZoneConnection data + ZoneConnections.xml

A [ZoneConnection](../src/Genie.Core/Mapper/ZoneConnection.cs) is one directed link:

| Field | Meaning |
| --- | --- |
| `FromZone` / `ToZone` | zone-file basenames **without** `.xml` (`Map01_Crossing`) |
| `FromRoom` / `ToRoom` | node id or `#serverRoomId` |
| `Verb` | what the walker sends (`board boat`, `climb wall`) |
| `TransitType` | free-form tag (`boat`, `climb`, `ride`, `portal`) |
| `Requires` | skill/class/level gate, parsed by `ExitRequirement` |
| `RtCost` | roundtime seconds |
| `WaitMin` / `WaitMax` | scheduled-departure wait window |
| `Notes` | community notes |

Hand-authored connections live in a single **`ZoneConnections.xml`** at the root of the Maps directory (next to the `Map##_*.xml` zone files), so transit links that aren't encoded in the maps (boats, portals) can be curated without editing individual zone files. The schema:

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

[ZoneConnectionsRepository](../src/Genie.Core/Mapper/ZoneConnectionsRepository.cs) reads and writes this file. On first launch it **seeds** an embedded baseline ([ZoneConnections.baseline.xml](../src/Genie.Core/Mapper/Resources/ZoneConnections.baseline.xml)) — a documented set of example routes with TODO room ids — so users have a starting template. A `.genie5-zone-connections-seeded` marker ensures it never re-seeds: if you delete the file after seeing it, the app respects that. Unresolvable connections (stale zone/room refs) are silently skipped by the pathfinder, so a half-filled file degrades gracefully to single-zone routing rather than breaking.

### The editor UI

**Maps ▸ Cross-Zone Connections…** opens the [ZoneConnectionsViewModel](../src/Genie.App/ViewModels/ZoneConnectionsViewModel.cs) grid ([ZoneConnectionsDialog.axaml](../src/Genie.App/Views/ZoneConnectionsDialog.axaml)) — add / remove / edit / save connections, round-tripping through the repository. This is the curation surface for the meta-graph the pathfinder consults.

### The walker (shipped)

[AutoWalkService](../src/Genie.App/Services/AutoWalkService.cs) executes cross-zone `WalkStep`s end-to-end: **`StartCrossZone`** takes a `MultiZonePathfinder` plan, `DispatchNextStep` surfaces the expected wait window and runs a countdown ("~4:23 left") in the Mapper indicator strip, and arrival is confirmed by the destination zone fingerprinting in (a room change also clears any pending cross-zone wait). Same attended-mode gates as the in-zone walk.

## Entry points (how a cross-zone walk starts)

1. **`#goto` / `#go2` a room in another zone.** When the argument doesn't resolve in the loaded zone, `MapperViewModel.GotoByName` falls through to `TryStartCrossZoneGoto`: the target is resolved through the whole-Maps `ZoneRoomIndex` (server-room-id first, else title) and planned with `MultiZonePathfinder`.
2. **Click a room while viewing a non-current zone.** If the player isn't placed in the displayed zone (you switched the map to browse another zone), `GotoNode` treats the click as a cross-zone goto from the player's real room (resolved via the index) to the clicked room.
3. Both paths converge on **`MapperViewModel.TryStartCrossZoneWalk`** → `AutoWalkService.StartCrossZone`. The origin is always the player's *actual* current room (from its server-room-id), so it works even when the displayed zone isn't the one they're standing in.

## Design alignment

The broader plan — skill-weighted paths, the user-editable connection database, transit modelling, and the phased rollout — lives in [AUTOMAPPER_DESIGN.md](AUTOMAPPER_DESIGN.md). This page tracks the multi-zone slice of that work specifically.

Note that Genie 5 deliberately does **not** aim to port the whole of the community `travel.cmd` into the engine. Escape recipes for un-mappable starting rooms, premium-account shortcuts, and ferry-state recovery are well-suited to scripts and stay there; the engine version targets the common land + scheduled-transit routes, with the script remaining the fallback for the long tail.

## Code references

- **[MultiZonePathfinder.cs](../src/Genie.Core/Mapper/MultiZonePathfinder.cs)** — lazy-loading Dijkstra, `WalkStep` / `MultiZonePath`.
- **[ZoneConnection.cs](../src/Genie.Core/Mapper/ZoneConnection.cs)** — the cross-zone edge model.
- **[ZoneConnectionDeriver.cs](../src/Genie.Core/Mapper/ZoneConnectionDeriver.cs)** — derives edges from border-room notes; merged with authored entries via `ZoneConnectionMerge`.
- **[ZoneConnectionsRepository.cs](../src/Genie.Core/Mapper/ZoneConnectionsRepository.cs)** — `ZoneConnections.xml` I/O + first-launch seeding.
- **[ZoneConnections.baseline.xml](../src/Genie.Core/Mapper/Resources/ZoneConnections.baseline.xml)** — the embedded starter template.
- **[ZoneConnectionsViewModel.cs](../src/Genie.App/ViewModels/ZoneConnectionsViewModel.cs)** — the editor.
- **[AutoWalkService.cs](../src/Genie.App/Services/AutoWalkService.cs)** — the walker (`StartCrossZone`, wait countdown, fingerprint arrival).
