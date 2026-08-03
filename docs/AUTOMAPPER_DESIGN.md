# AutoMapper AutoWalk — Design Document

**Status:** design proposal, in active build. Phase 1 implementation in flight.
**Prepared:** 2026-05-26
**Scope:** full auto-walk feature including skill-weighted paths + cross-zone travel + user-editable database, per alpha-blocker decision.

---

## Goals

1. **Click a room on the map, walk there.** Step-by-step execution, RT-aware, cancel-on-input.
2. **Cross-zone travel.** Type `Crossing → Throne City` or click a Throne City room while in Crossing — pathfinder spans zone boundaries.
3. **Skill-weighted paths.** If the route requires Climbing 50 and the character has Climbing 30, skip that edge and find an alternate route. Boats with wait times factor into time-to-destination.
4. **User-editable database.** Users discover that a wall requires climbing 50, or that a boat takes 5-10 min — they edit the exit in the Mapper, the data persists to zone XML, the community Maps repo absorbs it over time.
5. **DR policy compliance.** Walks halt on window unfocus > 60s, cancel on any typed command, never auto-resume across disconnects. Attended-mode by construction.

---

## What we already have

Before any new code, these pieces exist:

| Piece | Where | Notes |
|---|---|---|
| `MapExit.Requires` field | `src/Genie.Core/Mapper/MapExit.cs:25` | Free-form string today ("athletics 50"); structured in Phase 2 |
| `MapExit.MoveCommand` | same | Captures non-compass arcs ("climb tall wall", "swim river") |
| `AutoMapperEngine.FindPath(start, dest)` | `src/Genie.Core/Mapper/AutoMapperEngine.cs:512` | BFS shortest path; returns ordered move commands |
| `Genie4MapImporter` / `Genie4MapExporter` | `src/Genie.Core/Mapper/` | Already round-trip the `requires=` XML attribute |
| `CommandQueue` | `src/Genie.Core/Queue/CommandQueue.cs` | RT-aware command serialization |
| `NavEvent` from parser | `src/Genie.Core/Events/` | Fires when player walks into a new room |
| `CurrentNodeChanged` event on `AutoMapperEngine` | already wired | What we hook into to advance the walk |
| Skill data | `GameState.Components` dict with keys like `exp Climbing` | Raw text content; needs parser |
| `MapperViewModel.GotoNode` | `src/Genie.App/ViewModels/MapperViewModel.cs:626` | Currently sends all moves in a burst — replace with stepwise execution |

**What the pre-Automapper checkpoint taught us** (May 2026 rollback):

The earlier `.automapper`-driven approach hit a wall: `automapper.cmd` is 34 KB of state-aware logic depending on ~80% of the Genie 4 ecosystem (specific aliases, variables, classes, triggers). Genie 5 didn't have that ecosystem, so the script couldn't run. We rolled back to "click opens a dialog" and never returned.

The lesson: **drive movement through the engine directly, not through a community script.** Engine has perfect visibility into RT state, room changes, and the `CommandQueue` — it's the right primitive.

---

## Phase 1 — Single-zone auto-walk (foundation)

**Scope:** the minimum useful auto-walker. Single zone, no skill gating, no wait times. Just step-by-step execution with cancel + focus-check + indicator.

### Architecture

```
User right-clicks room → MapCanvas context menu → "Go Here"
  → MapperViewModel.GotoNode(MapNode target)
    → AutoWalkService.Start(currentNode, target)
      → FindPath returns ordered move commands [n, e, climb wall, w, n]
      → AutoWalkSession created with state machine
      → Pump first command via CommandQueue / ProcessInput
      → Subscribe to NavEvent
      
On each NavEvent:
  → if room matches expected next-in-path → pump next command
  → if room is the destination → walk complete, fire WalkFinished
  → if room is unexpected (off-path) → cancel walk, report "lost"

On Esc keypress (handled in MainWindow):
  → AutoWalkService.CancelCurrent("user cancelled via Esc")

On any user-typed command (handled in CommandEngine):
  → If active walk: AutoWalkService.CancelCurrent("user took manual command")

On window deactivation (handled in MainWindow Deactivated event):
  → Start 60-second timer
  → If timer expires while still unfocused: AutoWalkService.PauseCurrent
  → On reactivation: surface "Walk paused. Click Resume or Cancel."

On disconnect:
  → AutoWalkService.CancelCurrent("disconnected")
  → Never auto-resume on reconnect — per compliance review
```

### Data model

```csharp
public sealed class AutoWalkSession
{
    public Guid Id { get; }                          // for cancellation
    public MapNode Origin { get; }
    public MapNode Destination { get; }
    public List<string> RemainingMoves { get; }      // pops from front as walk progresses
    public int StepsTotal { get; }                   // for "X of Y" display
    public int StepsCompleted => StepsTotal - RemainingMoves.Count;
    public AutoWalkState State { get; set; }         // Active / Paused / Cancelled / Finished
    public string? CancelReason { get; set; }        // shown to user
    public DateTimeOffset StartedAt { get; }
}

public enum AutoWalkState { Active, Paused, Cancelled, Finished }
```

### Service interface

```csharp
public interface IAutoWalkService
{
    AutoWalkSession? CurrentSession { get; }         // null when nothing running
    IObservable<AutoWalkSession?> SessionChanges { get; }

    bool Start(MapNode origin, MapNode destination);
    void CancelCurrent(string reason);
    void PauseCurrent(string reason);
    void ResumeCurrent();
}
```

### UI

- **Indicator strip** in the Mapper panel (above status strip), only visible when a walk is active:
  ```
  ⚡ Walking to Throne City Library — 5 of 12 rooms · Esc to cancel
  ```
- **Pause indicator** when paused (window unfocused > 60s):
  ```
  ⏸ Walk paused (window unfocused) — Resume / Cancel
  ```
- **Cancel reason** briefly shown on completion or cancel:
  ```
  ✓ Arrived at Throne City Library
  ✗ Walk cancelled: window unfocused for too long
  ```

### Compliance gating implementation

- **Focus tracking**: `MainWindow.Activated` / `MainWindow.Deactivated` events drive an `IsForeground` boolean
- **Unfocus timer**: a `Timer` that starts on Deactivated and fires after 60s; the timer's callback calls `AutoWalkService.PauseCurrent`
- **Esc handling**: `MainWindow_KeyDown` for `Key.Escape` calls `AutoWalkService.CancelCurrent("user pressed Esc")`
- **Typed command**: `CommandEngine.ProcessInput` checks `AutoWalkService.CurrentSession` — if active and the input isn't a meta-command (`#stop`, etc.), it cancels the walk before processing
- **Disconnect**: `GenieCore.StateStream.OfType<Disconnected>()` triggers `CancelCurrent("disconnected")`

### Estimated cost: **4-6 hours**

### Deliverable

- `IAutoWalkService` + implementation in `Genie.App/Services/`
- `AutoWalkSession` record in `Genie.Core/Mapper/`
- `MapperViewModel.GotoNode` rewritten to use the service
- Indicator XAML in App.axaml Mapper template
- Esc handler in MainWindow
- CancelCurrent wired to CommandEngine and connection state stream

---

## Phase 2 — Skill model + structured Requires + Dijkstra

**Scope:** parse the player's skill ranks; structure the `Requires` field; replace BFS with weight-aware Dijkstra so edges the character can't take get excluded.

### Skill data model

```csharp
public sealed class SkillStore : ReactiveObject
{
    private readonly ConcurrentDictionary<string, int> _ranks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns current rank for the named skill, 0 if unknown.</summary>
    public int Rank(string skill) => _ranks.TryGetValue(skill, out var r) ? r : 0;

    /// <summary>Set after parsing a `<component id='exp X'>` event.</summary>
    public void SetRank(string skill, int rank) { _ranks[skill] = rank; SkillsChanged?.Invoke(); }

    public event Action? SkillsChanged;
}
```

### Skill parser

DR sends `<component id='exp Climbing'>Climbing: 100 33% (3/34)</component>`. The parser must extract:
- Skill name: `Climbing`
- Current rank: `100`
- (Optional later) Percentage and mind state

Wired into the existing `ComponentEvent` handler in `GameStateEngine`. SkillStore lives on `GameState`.

### Structured Requires

The free-form `Requires` string gets a structured parser that accepts both legacy and new syntax:

```
"climbing 50"                    → MinRank("climbing", 50)
"climbing>=50"                   → MinRank("climbing", 50)        [explicit]
"climbing 50, athletics 30"      → MinRank("climbing", 50) AND MinRank("athletics", 30)
"class=Thief"                    → ClassEq("Thief")
"level>=25"                      → MinLevel(25)
"verb=climb tall wall"           → RequiredVerb("climb tall wall") [rarely needed; usually in MoveCommand]
```

```csharp
public sealed record ExitRequirement
{
    public Dictionary<string, int> MinRanks { get; init; } = new();
    public string? RequiredClass { get; init; }
    public int? MinLevel { get; init; }
    public string? RequiredVerb { get; init; }   // matches MoveCommand
    public string? RawText { get; init; }        // fallback, original string

    public bool IsMet(SkillStore skills, string? characterClass, int characterLevel)
    {
        foreach (var (name, min) in MinRanks)
            if (skills.Rank(name) < min) return false;
        if (RequiredClass is not null && !string.Equals(characterClass, RequiredClass, StringComparison.OrdinalIgnoreCase))
            return false;
        if (MinLevel.HasValue && characterLevel < MinLevel.Value) return false;
        return true;
    }
}
```

### Dijkstra pathfinder

Replaces `AutoMapperEngine.FindPath` BFS with weight-aware Dijkstra. Edge weight:

```
weight(exit) =
    INFINITY            if exit.Requires.Parse().IsMet(skills, ...) == false
    1 + waitTime/4      otherwise
    (where 1 = baseline cost per room; waitTime in seconds, scaled to dwarf the room cost for slow links like boats)
```

If shortest path is INFINITY (no path), the existing "no path" message fires. The pathfinder reports the failed requirement so the user can fix it:

```
✗ No path to Throne City Library — climb tall wall (climbing>=50) blocks the only known route. Your climbing rank is 30.
```

### Where skills get persisted

Skills are character-specific live state. They're parsed from each session's first stats-display. **We do NOT persist them** — they're re-parsed on each connect. The first time the user opens stats / skills, the data is fresh.

For the auto-walker: on session start, if skill data hasn't arrived yet (we just connected and player hasn't typed `skills` or `info`), the pathfinder treats all skill-gated edges as "unknown" — defaults to "include the edge, log a warning." User can re-route after their skills load.

### Estimated cost: **4-6 hours**

### Deliverable

- `SkillStore.cs` in Genie.Core/GameState/
- Skill parser hook in GameStateEngine
- `ExitRequirement` record + parser in Genie.Core/Mapper/
- `AutoMapperEngine.FindPath` rewritten to Dijkstra with weighted edges
- "Why no path" diagnostic surfaced when pathfinding fails

---

## Phase 3 — Edit-exit UI

**Scope:** right-click an exit in the Mapper → "Edit Exit" dialog. User can set requirements, wait times, RT cost. Persists to zone XML.

### Trigger points

- **Right-click in MapCanvas** (context menu): existing menu adds an "Edit Exit..." item. Submenu shows each exit; pick which to edit.
- **Less Obvious Paths button right-click**: existing buttons handle compass directions; right-clicking a Less-Obvious arc opens its edit dialog directly.

### Dialog UI

```
┌─────────────────────────────────────────────────────────┐
│ Edit Exit                                               │
├─────────────────────────────────────────────────────────┤
│ From:  Garden Rooftop, Medical Pavilion (#37666728)     │
│ Verb:  climb tall wall                                  │
│ To:    Throne City Library Roof (#37666729)             │
├─────────────────────────────────────────────────────────┤
│ REQUIREMENTS                                            │
│ Skills:    [Climbing  ▼] [≥] [ 50 ]    [+] [-]          │
│            [Athletics ▼] [≥] [ 30 ]    [+] [-]          │
│ Class:     [(any)         ▼]                            │
│ Min level: [ 0 ]                                        │
│                                                         │
│ TIMING                                                  │
│ RT cost:    [ 5 ] seconds                               │
│ Wait min:   [ 0 ] seconds (e.g. boat schedule)          │
│ Wait max:   [ 0 ] seconds                               │
│                                                         │
│ Notes:     [Free-form notes for the community Maps repo]│
│                                                         │
│ [ Cancel ]                                  [ Save ]    │
└─────────────────────────────────────────────────────────┘
```

### Persistence

Updated `MapExit` fields:

```csharp
public sealed class MapExit
{
    public Direction Direction     { get; set; }
    public string    MoveCommand   { get; set; } = string.Empty;
    public int?      DestinationId { get; set; }

    // Existing free-form (kept for backwards compat with Genie 4 XML)
    public string    Requires      { get; set; } = string.Empty;

    // NEW — structured fields, written to XML as separate attributes
    public Dictionary<string, int> RequiredSkills { get; init; } = new();
    public string?   RequiredClass { get; set; }
    public int?      MinLevel      { get; set; }
    public int?      RtCost        { get; set; }   // seconds
    public int?      WaitMin       { get; set; }
    public int?      WaitMax       { get; set; }
    public string?   Notes         { get; set; }
}
```

XML serialisation adds new attributes:
```xml
<exit dir="climb tall wall"
      requires="climbing>=50, athletics>=30"
      rt="5"
      notes="rope needed for ascent" />
```

Old Genie 4 clients ignore unknown attributes — backwards-compat is preserved.

### Estimated cost: **4-6 hours**

### Deliverable

- `EditExitDialog.axaml` + `EditExitViewModel.cs`
- `MapCanvas` context menu adds "Edit Exit ▶" submenu with each visible exit
- Less Obvious Paths buttons get right-click handler
- `MapExit` model extended
- `Genie4MapImporter` / `Genie4MapExporter` updated for new attributes
- "Save Exit" persists the zone XML

---

## Phase 4 — Cross-zone routing

**Scope:** routes can span multiple zones. Going from Crossing to Throne City: walk to docks, board boat, wait 5 min, disembark, walk to Throne City Gate, walk through city.

### Data model

A separate file in the Maps dir: `ZoneConnections.xml`. Lives at the same level as the zone files; the community Maps repo absorbs it the same way.

```xml
<?xml version="1.0" encoding="utf-8"?>
<connections>

  <!-- Boat from Crossing docks to Throne City pier -->
  <connection id="boat-crossing-throne"
              from-zone="Map01_Crossing"
              from-room="#37666999"
              to-zone="Map35_Throne_City"
              to-room="#37666500"
              verb="board boat"
              transit-type="boat"
              wait-min="300"
              wait-max="600"
              rt="0"
              notes="Departs every 5-10 game minutes" />

  <!-- Climb the wall between two zones -->
  <connection id="climb-wall-east-gate"
              from-zone="Map01_Crossing"
              from-room="#37666800"
              to-zone="Map02_Crossing_Wall"
              to-room="#37666810"
              verb="climb wall"
              transit-type="climb"
              requires="climbing>=50"
              rt="5"
              notes="" />

</connections>
```

### Multi-zone Dijkstra

Pathfinder graph nodes become `(ZoneFile, RoomId)` pairs. Edges are:
- All intra-zone exits (existing data)
- All cross-zone connections from `ZoneConnections.xml`

Same weight model as Phase 2, but cross-zone edges may have large wait times that dominate.

### Loading multiple zones on demand

When pathfinding spans zones, the engine loads each zone file as needed via the existing `MapZoneRepository`. We don't pre-load every zone; lazy-load when the route requires it.

### UI: Cross-zone connection editor

A new dialog reachable from the Mapper panel's Details column:

```
┌─────────────────────────────────────────────────────────┐
│ Cross-Zone Connections                                  │
├─────────────────────────────────────────────────────────┤
│ Connections involving current zone (Map01_Crossing):    │
│                                                         │
│ → board boat — to Throne City (#37666500)               │
│   Wait 5-10 min, requires Athletics 20                  │
│   [ Edit ] [ Delete ]                                   │
│                                                         │
│ → climb wall — to Crossing Wall (#37666810)             │
│   Requires Climbing 50, RT 5s                           │
│   [ Edit ] [ Delete ]                                   │
│                                                         │
│ [ + Add new connection ]                                │
└─────────────────────────────────────────────────────────┘
```

The "Add new connection" flow walks the user through picking a from-room (current room as default), a target zone (dropdown of available zone files), a target room (entered as `#NNNN` or browsed from the target zone's room list), and the transit details.

### Walk execution across zone boundaries

When the auto-walker hits a cross-zone connection, the indicator updates:
```
⚡ Walking to Throne City Library — 12 of 27 rooms · boarding boat (wait ~5 min)
```

The walker sends the transit verb (`board boat`), then waits for the destination zone's room title to match the expected target. Wait-times are handled via a per-session sub-timer; if the wait expires without arrival, the walker pauses and asks the user "expected to be in Throne City pier by now — manual recovery needed."

### Estimated cost: **6-10 hours**

### Deliverable

- `ZoneConnection.cs` model in Genie.Core/Mapper/
- `ZoneConnectionsRepository.cs` for I/O
- Multi-zone Dijkstra in `AutoMapperEngine.FindPath` (signature changes to accept origin/destination as `(zone, room)` tuples)
- `CrossZoneConnectionsDialog.axaml`
- `AutoWalkService` handles cross-zone transit verb + wait timer
- Mapper Details panel gets a "Cross-zone connections" expander

---

## Total cost summary

| Phase | Scope | Cost | Ship state |
|---|---|---|---|
| **1** | Single-zone auto-walk + cancel + focus-check + indicator | 4-6 hours | **MVP — must ship in alpha** |
| **2** | Skill model + structured Requires + Dijkstra | 4-6 hours | **Should ship in alpha** |
| **3** | Edit-exit UI | 4-6 hours | **Could defer to "alpha 2" if pressed** |
| **4** | Cross-zone routing (Crossing → Throne City) | 6-10 hours | **Per user: must ship; can be alpha 2** |

**Total for full feature: 2-3 days of focused work.**

---

## Open questions for user decision

1. **Phasing**: ship Phase 1 in alpha now, defer Phases 2-4 to alpha 2 (e.g. v5.0.0-alpha.2 next week)? Or hold the alpha until all four phases ship?
   - **Recommendation**: ship Phase 1 + Phase 2 in v5.0.0-alpha.1 (skills are the most-asked-about feature). Ship Phases 3 + 4 as v5.0.0-alpha.2 a few days later. Testers get value immediately and feed back on Phase 1 while we build 3 + 4.

2. **Skill rank source**: skills come from `<component id='exp X'>` events. If the user hasn't opened their skills panel yet, the pathfinder has no data. Options:
   - **(a)** Auto-fire `skills` on connect to prime the data. Tiny verb spam.
   - **(b)** Defer: pathfinder treats unknown skills as "include edge, warn." User can re-route once skills load.
   - **Recommendation**: (a) — silent `skills` on connect. The user wants to know, the data is small, it's a one-time cost.

3. **Compliance — pause vs cancel on unfocus**: today's design pauses at 60s and waits for user to choose Resume/Cancel. Alternative is hard-cancel at 60s. Per the compliance review, "halt the queue after ~60s of window unfocus" suggests pause (the user has to act to continue). Stick with pause?
   - **Recommendation**: yes, pause + resume-or-cancel choice.

4. **Boat wait UX**: do we surface a countdown timer during boat wait? "Boarding boat… arriving in ~3:45." Useful but more UI work.
   - **Recommendation**: yes for v1 — copy the spell-cast bar pattern.

5. **Cross-zone connection schema location**: `ZoneConnections.xml` at the Maps-dir root, OR inside each zone's XML (`<cross-zone-exits>` block)?
   - **Recommendation**: separate file. Cleaner for community curation; doesn't pollute per-zone schema; one place to look for the meta-graph.

6. **Pre-populated cross-zone data**: ship with a starter set (boats, well-known climb spots) or empty?
   - User said "We don't need to have all the Map weighted features in the database" — so **empty** for alpha. Document the format in `docs/MAPS_FORMAT.md` and let the community grow it.

---

## Compliance posture (final check)

All four phases respect the `policy_compliance_review.md` hard-nevers:
- ❌ Auto-walk while window unfocused — pause at 60s
- ❌ Auto-reconnect-and-resume — never resume across disconnects
- ❌ Overnight chained travel — single trip per Start; no daisy-chained queues
- ❌ Headless / daemon mode — only fires when App window exists
- ❌ Agentive AI — pathfinder is deterministic; AI not involved
- ❌ Multi-character orchestration — single-session client

The visible indicator + Esc cancel + typed-command-cancel + focus-check make this the responsive auto-walker the policy permits. **Same surface as Lich's `go2`** (which Simu has tolerated for 20+ years).

