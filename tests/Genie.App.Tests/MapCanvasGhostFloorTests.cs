using System.Collections.Generic;
using Genie.App.Controls;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The mapper's Pass 0 "ghost floor" arcs. Genie 4 paints the rooms of another
/// floor AND their arcs in the presets' background colour, so the floor below
/// still reads as a map under the current floor. Genie 5's first cut drew only
/// the boxes: standing on Crossing's one-floor Riverlace Lane inset (z=1) turned
/// the 1,148 floor-0 rooms into a line-less grid that looked like a broken map.
/// <see cref="MapCanvas.ForEachGhostEdge"/> is the geometry the canvas now
/// paints; these pin what it enumerates.
/// </summary>
public class MapCanvasGhostFloorTests
{
    private static MapNode Node(int id, int x, int y, int z) => new() { Id = id, X = x, Y = y, Z = z };

    private static void Arc(MapNode from, Direction dir, int? dest, string? move = null) =>
        from.Exits.Add(new MapExit
        {
            Direction     = dir,
            MoveCommand   = move ?? dir.ToString().ToLowerInvariant(),
            DestinationId = dest,
        });

    /// <summary>Floor 0: a 3-room east-west street (1—2—3) with a dead-end north
    /// stub on 2 and a "go stairs" up to floor 1. Floor 1: the 2-room inset (10—11),
    /// 11 has a spoiler "search go hatch" to 10 and a west stub. Floor 2: one room.</summary>
    private static MapZone Zone()
    {
        var zone = new MapZone();
        var n1 = Node(1, 0, 0, 0); var n2 = Node(2, 1, 0, 0); var n3 = Node(3, 2, 0, 0);
        var n10 = Node(10, 5, 5, 1); var n11 = Node(11, 6, 5, 1);
        var n20 = Node(20, 9, 9, 2);
        foreach (var n in new[] { n1, n2, n3, n10, n11, n20 }) zone.Nodes[n.Id] = n;

        Arc(n1, Direction.East, 2); Arc(n2, Direction.West, 1);           // two-way, draws once
        Arc(n2, Direction.East, 3); Arc(n3, Direction.West, 2);
        Arc(n2, Direction.North, null);                                    // unmapped neighbour → stub
        Arc(n2, Direction.None, 10, "go stairs");                          // to another floor, non-cardinal → nothing
        Arc(n3, Direction.Up, 20);                                         // up → no stub (no on-map direction)

        Arc(n10, Direction.East, 11);                                      // one-way, lower id → draws
        Arc(n11, Direction.None, 10, "search go hatch");                   // spoiler, non-cardinal
        Arc(n11, Direction.West, null);                                    // stub
        return zone;
    }

    private static (List<(int from, int to)> edges, List<(int from, Direction dir)> stubs) Collect(int level, bool showSpoilers = true)
    {
        var edges = new List<(int, int)>();
        var stubs = new List<(int, Direction)>();
        MapCanvas.ForEachGhostEdge(Zone(), level, showSpoilers,
            (a, b) => edges.Add((a.Id, b.Id)),
            (a, d) => stubs.Add((a.Id, d)));
        return (edges, stubs);
    }

    [Fact]
    public void Standing_on_the_inset_floor_ghosts_the_whole_street_below()
    {
        var (edges, stubs) = Collect(level: 1);

        // The floor-0 street draws as a map: each two-way arc once, from the lower id.
        Assert.Equal(new[] { (1, 2), (2, 3) }, edges);
        // Its dead-end keeps its stub; the "go stairs" and "up" arcs leave no mark.
        Assert.Equal(new[] { (2, Direction.North) }, stubs);
    }

    [Fact]
    public void Ghosts_cover_the_floor_above_as_well_as_below()
    {
        var (edges, stubs) = Collect(level: 0);

        // Floor 1 (above) is a ghost floor: its street edge and west stub draw.
        // Floor 2 is two away and is not drawn at all.
        Assert.Equal(new[] { (10, 11) }, edges);
        Assert.Equal(new[] { (11, Direction.West) }, stubs);
    }

    [Fact]
    public void A_floor_two_away_is_not_a_ghost_floor()
    {
        var (edges, stubs) = Collect(level: 2);

        // Only floor 1 is adjacent to floor 2; floor 0's street must not appear.
        Assert.Equal(new[] { (10, 11) }, edges);
        Assert.Equal(new[] { (11, Direction.West) }, stubs);
        Assert.True(MapCanvas.IsGhostFloor(1, 2));
        Assert.False(MapCanvas.IsGhostFloor(0, 2));
        Assert.False(MapCanvas.IsGhostFloor(2, 2));
    }

    [Fact]
    public void Spoiler_arcs_on_a_ghost_floor_follow_the_same_switch_as_the_current_floor()
    {
        // With spoilers shown, the hatch is a non-cardinal arc to a same-floor room
        // from the HIGHER id, so it neither draws an edge nor a stub — but it must
        // be filtered by the switch, not by accident. Make it the lower-id side.
        var zone = Zone();
        zone.Nodes[11].Exits.Clear();
        zone.Nodes[10].Exits.Clear();
        zone.Nodes[10].Exits.Add(new MapExit { Direction = Direction.None, MoveCommand = "search go hatch", DestinationId = 11 });

        var shown  = new List<(int, int)>();
        var hidden = new List<(int, int)>();
        MapCanvas.ForEachGhostEdge(zone, 0, showSpoilers: true,  (a, b) => shown.Add((a.Id, b.Id)),  (_, _) => { });
        MapCanvas.ForEachGhostEdge(zone, 0, showSpoilers: false, (a, b) => hidden.Add((a.Id, b.Id)), (_, _) => { });

        Assert.Equal(new[] { (10, 11) }, shown);
        Assert.Empty(hidden);
    }

    [Fact]
    public void Only_the_eight_map_cardinals_get_a_stub()
    {
        foreach (var dir in new[] { Direction.North, Direction.NorthEast, Direction.East, Direction.SouthEast,
                                    Direction.South, Direction.SouthWest, Direction.West, Direction.NorthWest })
            Assert.True(MapCanvas.IsStubDirection(dir, out _), dir.ToString());
        foreach (var dir in new[] { Direction.Up, Direction.Down, Direction.Out, Direction.In, Direction.None })
            Assert.False(MapCanvas.IsStubDirection(dir, out _), dir.ToString());
    }
}
