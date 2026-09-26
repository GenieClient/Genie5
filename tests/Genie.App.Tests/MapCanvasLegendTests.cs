using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The mapper legend lists ONLY what the current level actually draws
/// (<see cref="MapCanvas.BuildLegend"/>), so a flat zone with one plain room
/// gets a one-row key and a coloured room only earns a row when a room of that
/// colour is on screen. Room-colour meanings come from the community Maps
/// repo README (the key the map authors actually follow); anything off-key is
/// listed by hex rather than given an invented meaning. Line rows follow the
/// same classifier the renderer uses, so the key can never disagree with the map.
/// </summary>
public class MapCanvasLegendTests
{
    private static readonly Color Ring = Color.FromRgb(0xff, 0x40, 0x40);

    private static MapNode Node(int id, int x, int y, int z = 0, string color = "", string notes = "") =>
        new() { Id = id, X = x, Y = y, Z = z, Color = color, Notes = notes };

    private static void Arc(MapNode from, Direction dir, int? dest, string? move = null) =>
        from.Exits.Add(new MapExit
        {
            Direction     = dir,
            MoveCommand   = move ?? dir.ToString().ToLowerInvariant(),
            DestinationId = dest,
        });

    private static MapZone Zone(params MapNode[] nodes)
    {
        var zone = new MapZone();
        foreach (var n in nodes) zone.Nodes[n.Id] = n;
        return zone;
    }

    private static List<string> Labels(MapZone zone, int level = 0, MapNode? current = null, MapNode? selected = null,
                                       bool showSpoilers = true, bool ghosts = true) =>
        MapCanvas.BuildLegend(zone, level, current, selected, showSpoilers, ghosts, Ring).Select(r => r.Label).ToList();

    [Fact]
    public void Single_plain_room_gives_a_one_row_legend()
    {
        var rows = Labels(Zone(Node(1, 0, 0)));
        Assert.Equal(new[] { "Room" }, rows);
    }

    [Fact]
    public void Empty_level_gives_no_rows_at_all()
    {
        // Rooms exist, but none on the requested level and ghosts are off.
        var rows = Labels(Zone(Node(1, 0, 0, z: 3)), level: 0, ghosts: false);
        Assert.Empty(rows);
    }

    [Fact]
    public void Line_rows_appear_only_for_arc_kinds_actually_drawn()
    {
        var a = Node(1, 0, 0); var b = Node(2, 1, 0); var c = Node(3, 2, 0);
        Arc(a, Direction.East, 2); Arc(b, Direction.West, 1);     // cardinal → "Path"
        Arc(b, Direction.None, 3, "climb wall");                  // → "Climb"
        var rows = Labels(Zone(a, b, c));

        Assert.Contains("Path", rows);
        Assert.Contains("Climb", rows);
        Assert.DoesNotContain("Go / up / down", rows);
        Assert.DoesNotContain("Exit (neighbour not mapped)", rows);
    }

    [Fact]
    public void Go_and_stub_rows_follow_the_renderer()
    {
        var a = Node(1, 0, 0); var b = Node(2, 1, 0);
        Arc(a, Direction.None, 2, "go door");   // blue
        Arc(b, Direction.North, null);          // unmapped cardinal → cyan stub
        Arc(b, Direction.Up, null);             // up with no neighbour: no on-map stub
        var rows = Labels(Zone(a, b));

        Assert.Contains("Go / up / down", rows);
        Assert.Contains("Exit (neighbour not mapped)", rows);
        Assert.DoesNotContain("Path", rows);
    }

    [Fact]
    public void Up_only_arc_with_no_neighbour_earns_no_stub_row()
    {
        var a = Node(1, 0, 0);
        Arc(a, Direction.Up, null);
        Assert.Equal(new[] { "Room" }, Labels(Zone(a)));
    }

    [Fact]
    public void Spoiler_arc_hidden_with_spoilers_off_drops_its_row()
    {
        var a = Node(1, 0, 0); var b = Node(2, 1, 0);
        Arc(a, Direction.None, 2, "search go hatch");
        Assert.Contains("Go / up / down", Labels(Zone(a, b), showSpoilers: true));
        Assert.DoesNotContain("Go / up / down", Labels(Zone(a, b), showSpoilers: false));
    }

    [Fact]
    public void Arc_to_another_floor_draws_nothing_and_earns_no_line_row()
    {
        var a = Node(1, 0, 0); var up = Node(2, 0, 0, z: 1);
        Arc(a, Direction.None, 2, "go stairs");
        var rows = Labels(Zone(a, up), ghosts: false);
        Assert.Equal(new[] { "Room" }, rows);
    }

    [Fact]
    public void Known_room_colours_get_their_README_meaning_in_README_order()
    {
        var zone = Zone(
            Node(1, 0, 0, color: "#FF0000"),   // Shop
            Node(2, 1, 0, color: "#00FFFF"),   // PC housing
            Node(3, 2, 0, color: "#FF00FF"),   // Portal — first in README priority
            Node(4, 3, 0));                    // plain
        var rows = Labels(zone);
        Assert.Equal(new[] { "Room", "Portal / transport", "Shop", "PC housing" }, rows);
    }

    [Fact]
    public void Only_colours_present_on_this_level_are_listed()
    {
        var zone = Zone(
            Node(1, 0, 0, color: "#0000FF"),          // Water on level 0
            Node(2, 0, 0, z: 1, color: "#993300"));   // Mining on level 1
        Assert.Contains("Water (swim)", Labels(zone, level: 0));
        Assert.DoesNotContain("Mining",  Labels(zone, level: 0));
        Assert.Contains("Mining",        Labels(zone, level: 1));
        Assert.DoesNotContain("Water (swim)", Labels(zone, level: 1));
    }

    [Fact]
    public void Named_colours_and_alpha_hex_match_the_same_key_entry()
    {
        // The corpus still has a few named fills ("Red", "Blue") and the parser
        // accepts #AARRGGBB; both must land on the README entry, not on "Other".
        var zone = Zone(Node(1, 0, 0, color: "Red"), Node(2, 1, 0, color: "#FFFF0000"));
        var rows = Labels(zone);
        Assert.Equal(new[] { "Shop" }, rows);
    }

    [Fact]
    public void White_fill_is_a_plain_room_not_a_colour_row()
    {
        var rows = Labels(Zone(Node(1, 0, 0, color: "White")));
        Assert.Equal(new[] { "Room" }, rows);
    }

    [Fact]
    public void Off_key_colours_are_listed_by_hex_after_the_known_ones()
    {
        var zone = Zone(
            Node(1, 0, 0, color: "#C0C0C0"),   // silver — legacy, no agreed meaning
            Node(2, 1, 0, color: "#00FF00"));  // Bank / services
        var rows = Labels(zone);
        Assert.Equal(new[] { "Bank / services", "Other (#C0C0C0)" }, rows);
    }

    [Fact]
    public void Colour_swatch_style_is_a_fill_and_carries_the_map_colour()
    {
        var entry = MapCanvas.BuildLegend(Zone(Node(1, 0, 0, color: "#993300")), 0, null, null, true, true, Ring).Single();
        Assert.Equal(MapCanvas.LegendStyle.Room, entry.Style);
        Assert.Equal(Color.FromRgb(0x99, 0x33, 0x00), entry.Color);
    }

    [Fact]
    public void Cross_zone_row_only_when_a_connector_room_is_on_the_level()
    {
        var plain = Zone(Node(1, 0, 0));
        var xz    = Zone(Node(1, 0, 0, notes: "Map31_Riverhaven.xml|Riverhaven"));
        Assert.DoesNotContain("Cross-zone room", Labels(plain));
        var row = MapCanvas.BuildLegend(xz, 0, null, null, true, true, Ring).Single(r => r.Label == "Cross-zone room");
        Assert.Equal(MapCanvas.LegendStyle.Ring, row.Style);
        Assert.Equal(Colors.Blue, row.Color);
    }

    [Fact]
    public void Current_room_row_only_when_the_current_room_is_on_this_level_and_uses_the_user_ring_colour()
    {
        var here = Node(1, 0, 0, z: 2);
        var zone = Zone(here, Node(2, 0, 0, z: 0));
        var custom = Color.FromRgb(0x12, 0x34, 0x56);

        Assert.DoesNotContain("Current room *",
            MapCanvas.BuildLegend(zone, 0, here, null, true, false, custom).Select(r => r.Label));
        var row = MapCanvas.BuildLegend(zone, 2, here, null, true, false, custom).Single(r => r.Label == "Current room *");
        Assert.Equal(custom, row.Color);
        Assert.Equal(MapCanvas.LegendStyle.Ring, row.Style);
    }

    [Fact]
    public void Selected_room_row_is_a_dashed_ring_and_only_while_something_is_selected()
    {
        var n = Node(1, 0, 0);
        var zone = Zone(n);
        Assert.DoesNotContain("Selected room", Labels(zone));
        var row = MapCanvas.BuildLegend(zone, 0, null, n, true, true, Ring).Single(r => r.Label == "Selected room");
        Assert.True(row.Dashed);
        Assert.Equal(MapCanvas.LegendStyle.Ring, row.Style);
    }

    [Fact]
    public void Ghost_floor_row_only_when_an_adjacent_floor_has_rooms_and_ghosts_are_on()
    {
        var flat     = Zone(Node(1, 0, 0));
        var twoFloor = Zone(Node(1, 0, 0), Node(2, 0, 0, z: 1));
        var farFloor = Zone(Node(1, 0, 0), Node(2, 0, 0, z: 2));   // ±2 is never ghosted

        Assert.DoesNotContain("Floor above / below", Labels(flat));
        Assert.Contains("Floor above / below",       Labels(twoFloor));
        Assert.DoesNotContain("Floor above / below", Labels(twoFloor, ghosts: false));
        Assert.DoesNotContain("Floor above / below", Labels(farFloor));
        Assert.Equal(MapCanvas.LegendStyle.Ghost,
            MapCanvas.BuildLegend(twoFloor, 0, null, null, true, true, Ring).Single(r => r.Label == "Floor above / below").Style);
    }

    [Fact]
    public void Row_order_is_rooms_then_rings_then_ghost_then_lines()
    {
        var a = Node(1, 0, 0, color: "#FF0000", notes: "Map2_X.xml|X");
        var b = Node(2, 1, 0);
        Arc(a, Direction.East, 2);
        Arc(b, Direction.South, null);
        var zone = Zone(a, b, Node(3, 5, 5, z: 1));
        var rows = Labels(zone, current: b);
        Assert.Equal(new[]
        {
            "Room", "Shop", "Cross-zone room", "Current room *", "Floor above / below",
            "Path", "Exit (neighbour not mapped)",
        }, rows);
    }

    [Fact]
    public void Edge_classifier_matches_the_Genie4_palette()
    {
        Assert.Equal(MapCanvas.EdgeKind.Cardinal, MapCanvas.EdgeKindFor(new MapExit { Direction = Direction.NorthWest, MoveCommand = "northwest" }));
        Assert.Equal(MapCanvas.EdgeKind.Go,       MapCanvas.EdgeKindFor(new MapExit { Direction = Direction.Down,      MoveCommand = "down" }));
        Assert.Equal(MapCanvas.EdgeKind.Go,       MapCanvas.EdgeKindFor(new MapExit { Direction = Direction.None,      MoveCommand = "go gate" }));
        Assert.Equal(MapCanvas.EdgeKind.Climb,    MapCanvas.EdgeKindFor(new MapExit { Direction = Direction.None,      MoveCommand = "  Climb steps" }));
        Assert.Equal(MapCanvas.EdgeKind.Go,       MapCanvas.EdgeKindFor(new MapExit { Direction = Direction.None,      MoveCommand = "swim river" }));
    }

    [Fact]
    public void README_key_has_the_sixteen_community_colours()
    {
        Assert.Equal(16, MapCanvas.RoomColourKey.Length);
        Assert.Equal("Portal / transport", MapCanvas.RoomColourKey[0].label);   // priority 1
        Assert.Equal("Favor altar",        MapCanvas.RoomColourKey[15].label);
        Assert.Equal(16, MapCanvas.RoomColourKey.Select(k => k.color).Distinct().Count());
    }
}
