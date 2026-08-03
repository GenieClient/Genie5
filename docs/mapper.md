# Mapper

The mapper knows where the player is, owns the zone files, plans paths between rooms, and (in the UI) walks the player along them. It splits cleanly across the two projects:

- **[AutoMapperEngine](../src/Genie.Core/Mapper/AutoMapperEngine.cs)** (`Genie.Core`) — pure game-state logic. Given a loaded zone and a game-state feed, it resolves the current node, follows movement, optionally learns new rooms, and answers pathfinding queries. No UI, no threads, no IO beyond what the repository does.
- **[AutoWalkService](../src/Genie.App/Services/AutoWalkService.cs)** + **[MapperViewModel](../src/Genie.App/ViewModels/MapperViewModel.cs)** (`Genie.App`) — UI/host glue: the zone picker, the map render, click-to-goto, and the attended-mode walking state machine.

This page covers the runtime behaviour. For the larger design (skill-weighted paths, cross-zone travel, the user-editable connection database), see [AUTOMAPPER_DESIGN.md](AUTOMAPPER_DESIGN.md). For cross-zone routing specifically, see [multi-zone-travel.md](multi-zone-travel.md).

## Data model

```
MapZone
├── Id (Guid)               generated on creation
├── Name                    display name ("Crossing")
├── Genie4Id                preserved from imported XML ("1", "60", "2d")
└── Nodes : Dictionary<int, MapNode>

MapNode
├── Id, Title, Description, X, Y, Z
├── Notes, Color
├── ServerRoomId            from <nav rm="…"/>, stamped on first visit
└── Exits : List<MapExit>

MapExit
├── Direction               Direction enum (n, ne, …, up, down, out)
├── MoveCommand             what to send ("n", "go gate", "climb tree")
├── DestinationId           int?; null for unmapped exits
├── Requires                skill/class/level gate (see ExitRequirement)
├── RtCost                  roundtime seconds (pathfinder weight)
├── WaitMin / WaitMax       scheduled-departure wait window (boats)
└── Notes                   community notes (rope needed, night only, …)
```

Zones load and save through [MapZoneRepository](../src/Genie.Core/Mapper/MapZoneRepository.cs). Genie 4's XML map files import via [Genie4MapImporter](../src/Genie.Core/Mapper/Genie4MapImporter.cs) and [Genie4MapExporter](../src/Genie.Core/Mapper/Genie4MapExporter.cs) round-trips back. Zone files live in the user's **Maps** directory as `Map##_*.xml`.

## Wiring into the engine

The engine doesn't read `GameState` directly — it reads through [IMapperGameState](../src/Genie.Core/Mapper/IMapperGameState.cs), a four-property adapter (`RoomTitle`, `RoomDescription`, `Exits`, `ServerRoomId` + a `StateChanged` event). The concrete adapter is [MapperGameStateAdapter](../src/Genie.Core/Mapper/MapperGameStateAdapter.cs), constructed in `GenieCore` over the live `GameState` and the parser's `GameEvents`. This keeps the engine testable with a fake state and decoupled from the DR-specific event shapes.

```csharp
AutoMapper     = new AutoMapperEngine(new MapZone { Name = "(unsaved)" });
_mapperAdapter = new MapperGameStateAdapter(state, _parser.GameEvents);
AutoMapper.Attach(_mapperAdapter);
AutoMapper.Skills         = state.LiveSkills;   // for skill-weighted pathfinding
AutoMapper.CharacterClass = …; AutoMapper.CharacterLevel = state.Circle;
```

`GenieCore` also forwards outgoing commands to `AutoMapper.OnCommandSent(text)` so the engine knows which movement verb the player just used — that direction is the strongest signal for resolving the next room.

## Lookup-only vs. learning

`AutoMapperEngine.IsEnabled` defaults to **false** — *lookup-only* mode. In lookup-only mode the engine resolves the current node against the loaded community map but never modifies the zone (no new nodes, no new arcs, no stamping). Turning it on (the **AutoMapper** config / menu toggle) enables learning: stamping `ServerRoomId` on matched nodes, adding arcs for observed movement, and creating new nodes for unrecognised rooms. The save policy follows from this — there's no point writing the zone back to disk when the engine never modifies it.

## Resolving the current node

On every relevant state change, [OnStateChanged](../src/Genie.Core/Mapper/AutoMapperEngine.cs#L163) re-derives the current node. It re-processes whenever **title, exits, or description** changes — all three, because DR can deliver the parts of a room transition (title, compass, description, nav) in any order, and a later-arriving description may carry the disambiguating signal an earlier title-only fire lacked.

`OnRoomChanged` ([AutoMapperEngine.cs:194](../src/Genie.Core/Mapper/AutoMapperEngine.cs#L194)) tries a priority ladder:

1. **(a) Server room id** — if `<nav rm="…"/>` gave us an id and a node has that `ServerRoomId`, that's definitive.
2. **(b) Graph walk** — if we know the previous node and the movement verb used, follow that arc and confirm the destination title matches. Handles both compass directions and non-compass `MoveCommand`s (`go alley`, `climb trellis`).
3. **(c) Fingerprint index** — [MapFingerprint](../src/Genie.Core/Mapper/MapFingerprint.cs) hashes title + cardinal exits. A single match wins; on collisions (dense cities with duplicate titles) it disambiguates by adjacency to `prevNode`, then by description, and otherwise **declines** rather than guess — a wrong lock-in cascades through every subsequent move.
4. **(d) Reverse-arc search**, **(e) description tiebreaker**, then
5. **(f)** create a new node (if learning is enabled) or fire `RoomNotFoundInZone` (the hook the UI uses to auto-switch zones).

Once matched, `ServerRoomId` is stamped (when learning is on) so future visits resolve via (a) in O(1). An index (`RebuildIndex`) keeps fingerprint→nodes and serverRoomId→node lookups warm.

## Pathfinding

[FindPath(start, destination)](../src/Genie.Core/Mapper/AutoMapperEngine.cs#L547) is **Dijkstra** over the exit graph of the active zone. Edges are **weighted** (`EdgeCost` / `IntraZoneEdgeCost`): cost = `1` baseline `+ (rt + 1)/2` when the exit has an authored `RtCost` — otherwise a verb-inferred effort penalty (`EffortPenalty`: swim/climb moves cost more, scaled down by the character's Athletics rank) — `+ waitAvg/30` for scheduled waits (boats). A brutal high-RT swim costs as much as ~8 dry rooms, so a nearby bridge or gate wins the route. Each exit is gated by [ExitRequirement.Parse(exit.Requires).IsMet(Skills, CharacterClass, CharacterLevel)](../src/Genie.Core/Mapper/ExitRequirement.cs) — an exit the character can't take (climb wall below the skill threshold, guild-locked door, level gate) is excluded entirely. When `Skills` is null (no character data), every exit passes and the result matches a plain BFS. It returns the ordered list of `MoveCommand` strings to send.

Cross-zone routing is **not** done here — the engine only ever sees one zone. That's [MultiZonePathfinder](../src/Genie.Core/Mapper/MultiZonePathfinder.cs), documented in [multi-zone-travel.md](multi-zone-travel.md).

## Walking (the UI)

Walking starts two ways. From the Mapper panel: **right-click a room → Go Here**, or **Ctrl+Left-Click** (a plain left-click never walks in navigate mode — see `MapCanvas`). Or from the **`#goto` command** (alias `#go2`, [CommandEngine.cs:617](../src/Genie.Core/Commanding/CommandEngine.cs#L617)): `#goto <room id | label | title | @tag>` — numeric map id, note label, room title, or `@tag` for nearest-tagged-room routing (`MapperViewModel.GotoByName`). Targets in *another* zone work too: `#goto` falls through to the whole-Maps `ZoneRoomIndex` and plans with `MultiZonePathfinder` (`TryStartCrossZoneGoto` / `AutoWalkService.StartCrossZone`), and clicking a room while viewing a non-current zone does the same. Either way the view-model builds the plan (`FindPath` in-zone) and hands it to [AutoWalkService](../src/Genie.App/Services/AutoWalkService.cs).

```
GotoNode(target)
  → FindPath(CurrentNode, target) → [move1, move2, …]
  → AutoWalkService.Start(origin, destination)
        DispatchNextStep(): ProcessInput(move) through the normal pipeline
  → AutoMapperEngine.CurrentNodeChanged fires when the player enters a room
  → OnRoomChanged(): if at destination → Finished
                     else → advance step, DispatchNextStep()
```

Moves go through `Commands.ProcessInput` so the same alias/trigger/command-queue path runs as if typed. The walker also paces itself: before each step it checks roundtime **and** movability (stunned / webbed / prone / kneeling / sitting) and retries on a short timer instead of firing blind; it auto-sends `stand` when the character is down and `retreat` (capped per step) when combat engagement bounces a move (`CanMoveNow` / `DispatchNextStep` in `AutoWalkService`). Step matching trusts `FindPath`'s ordering: if the player gets bounced off-path, the next move won't match and the walk **cancels** rather than firing arbitrary commands.

### Attended-mode posture

The walker is deliberately conservative, to stay within DR's allowed-software policy (see the project README and `AutoWalkService`'s class comment):

- **Cancels** on: Esc, any typed non-meta command, disconnect, or walking off-plan.
- **Optional idle pause** — **off by default** (`GenieConfig.AutoWalkPauseOnUnfocus`): when opted in, a walk pauses after the configured unfocus window (the 60 s constant is only a fallback floor) and the user must click **Resume**. The always-on gates above are the compliance mechanism; DR policy is about responsiveness, not window focus.
- **Never auto-resumes** across a disconnect — a fresh walk needs a fresh click.
- A visible indicator strip shows progress and a Cancel/Resume control.

### Cross-zone wait UI

For a cross-zone hop with a known wait window (boats, ferries), `AutoWalkService` surfaces a countdown ("~4:23 left") in the indicator strip. The countdown is only a UI hint — actual arrival is driven by the destination zone fingerprinting in, so a late boat just shows "any moment now…" until the room change fires.

## Zone files, import, and updates

- **Import from Genie 4** — **File → Import from Genie 4…** brings `.cfg` rules across; map XML is imported via the mapper. See the [Importing Genie4 Config](../wiki/Importing-Genie4-Config.md) wiki page.
- **Update from Official Repo** — **Maps ▸ Update from Official Repo…** pulls fresh zone XML from the community [GenieClient/Maps](https://github.com/GenieClient) repo via [MapsUpdater](../src/Genie.Core/Update/Updaters/MapsUpdater.cs) (built on the shared updates framework's [GithubContentsSource](../src/Genie.Core/Update/Sources/GithubContentsSource.cs)), merging upstream layout changes while preserving your stamped `ServerRoomId`s. See [Updating Maps and Scripts](../wiki/Updating-Maps-and-Scripts.md).
- **Maps directory** — **Maps ▸ Open Maps Folder** / **Maps ▸ Change Maps Directory…**. Configurable via `#config mapdir`.

## Code references

- **[AutoMapperEngine.cs](../src/Genie.Core/Mapper/AutoMapperEngine.cs)** — node resolution ladder, `FindPath`, learning, `RoomNotFoundInZone`.
- **[MapperGameStateAdapter.cs](../src/Genie.Core/Mapper/MapperGameStateAdapter.cs)** / **[IMapperGameState.cs](../src/Genie.Core/Mapper/IMapperGameState.cs)** — the state bridge.
- **[MapZone.cs](../src/Genie.Core/Mapper/MapZone.cs)**, **[MapNode.cs](../src/Genie.Core/Mapper/MapNode.cs)**, **[MapExit.cs](../src/Genie.Core/Mapper/MapExit.cs)** — data model.
- **[MapZoneRepository.cs](../src/Genie.Core/Mapper/MapZoneRepository.cs)**, **[Genie4MapImporter.cs](../src/Genie.Core/Mapper/Genie4MapImporter.cs)** — persistence + import. Updates from the official repo now go through the shared updates framework: **[MapsUpdater.cs](../src/Genie.Core/Update/Updaters/MapsUpdater.cs)** + **[GithubContentsSource.cs](../src/Genie.Core/Update/Sources/GithubContentsSource.cs)**.
- **[MapFingerprint.cs](../src/Genie.Core/Mapper/MapFingerprint.cs)**, **[ExitRequirement.cs](../src/Genie.Core/Mapper/ExitRequirement.cs)** — disambiguation and skill gating.
- **[AutoWalkService.cs](../src/Genie.App/Services/AutoWalkService.cs)**, **[AutoWalkSession.cs](../src/Genie.App/Services/AutoWalkSession.cs)**, **[MapperViewModel.cs](../src/Genie.App/ViewModels/MapperViewModel.cs)**, **[MapCanvas.cs](../src/Genie.App/Controls/MapCanvas.cs)** — UI walker + render.
