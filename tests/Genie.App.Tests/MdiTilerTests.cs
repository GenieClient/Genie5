using System.Linq;
using Genie.App.Docking;
using Genie.App.Settings;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #363 — the geometry half of carrying a tabbed layout into windowed
/// (MDI) mode. <see cref="MdiTiler"/> turns the dock tree's proportions into
/// child-window rectangles; these pin the translation a user would recognise:
/// a group's panels land where the group was, and nothing lands off the area.
/// </summary>
public class MdiTilerTests
{
    private static DockNodeSnapshot Node(string kind, string? id, double prop = double.NaN,
                                         string? orientation = null, string? activeId = null,
                                         params DockNodeSnapshot[] children) =>
        new()
        {
            Kind = kind, Id = id, Proportion = prop, Orientation = orientation,
            ActiveId = activeId, Children = children.ToList(),
        };

    private static DockNodeSnapshot Leaf(string id) => Node("leaf", id);
    private static DockNodeSnapshot Splitter() => Node("splitter", null);

    /// <summary>The shipped default shape: Room over Streams | Game | Backpack.</summary>
    private static DockNodeSnapshot DefaultShape() =>
        Node("proportional", "root-layout", orientation: "Horizontal", children:
        [
            Node("proportional", "left-col", 0.22, "Vertical", children:
            [
                Node("tooldock", "room-dock", 0.35, children: [Leaf("room")]),
                Splitter(),
                Node("tooldock", "streams", 0.65, activeId: "combat", children:
                    [Leaf("logons"), Leaf("talk"), Leaf("thoughts"), Leaf("combat")]),
            ]),
            Splitter(),
            Node("proportional", "center-col", 0.56, "Vertical", children:
            [
                Node("documentdock", "docs", children: [Leaf("game-text")]),
            ]),
            Splitter(),
            Node("tooldock", "backpack-dock", 0.22, children: [Leaf("backpack")]),
        ]);

    private static bool Inside(MdiWindowBounds b, double w, double h) =>
        b.X >= 0 && b.Y >= 0 && b.X + b.Width <= w + 0.5 && b.Y + b.Height <= h + 0.5;

    [Fact]
    public void Every_leaf_gets_a_window_inside_the_area()
    {
        var tiles = MdiTiler.Tile(DefaultShape(), 1200, 800);

        Assert.Equal(
            new[] { "backpack", "combat", "game-text", "logons", "room", "talk", "thoughts" },
            tiles.Keys.OrderBy(k => k));
        Assert.All(tiles.Values, b => Assert.True(Inside(b, 1200, 800), $"{b} leaves the area"));
        Assert.All(tiles.Values, b => Assert.Equal("Normal", b.State));
    }

    [Fact]
    public void Panels_land_where_their_column_was()
    {
        var tiles = MdiTiler.Tile(DefaultShape(), 1000, 800);

        // Left column is 22% wide: Room and the streams live there.
        Assert.True(tiles["room"].X + tiles["room"].Width <= 220 + 0.5);
        Assert.True(tiles["room"].Y < tiles["combat"].Y, "Room sat above the stream tabs");
        // Game fills the 56% centre column.
        Assert.Equal(220, tiles["game-text"].X);
        Assert.Equal(560, tiles["game-text"].Width);
        Assert.Equal(800, tiles["game-text"].Height);
        // Backpack is the right-hand 22%.
        Assert.Equal(780, tiles["backpack"].X);
    }

    [Fact]
    public void A_tab_group_tiles_its_rectangle_without_overlap()
    {
        // Four stream tabs in one group become four windows side by side, not a
        // stack of identical rectangles.
        var group = Node("tooldock", "streams", activeId: "combat", children:
            [Leaf("logons"), Leaf("talk"), Leaf("thoughts"), Leaf("combat")]);
        var tiles = MdiTiler.Tile(group, 800, 600);

        var rects = tiles.Values.ToList();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i]; var b = rects[j];
                var overlap = a.X < b.X + b.Width && b.X < a.X + a.Width
                           && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;
                Assert.False(overlap, $"{a} overlaps {b}");
            }
        // Together they cover the group's rectangle.
        Assert.Equal(800 * 600, rects.Sum(r => r.Width * r.Height), precision: 0);
        // The active tab — the one the user was reading — goes top-left.
        Assert.Equal(0, tiles["combat"].X);
        Assert.Equal(0, tiles["combat"].Y);
    }

    [Fact]
    public void A_group_too_small_to_split_cascades_inside_its_rectangle()
    {
        var group = Node("tooldock", "streams", children:
            [Leaf("a"), Leaf("b"), Leaf("c"), Leaf("d"), Leaf("e"), Leaf("f")]);
        var tiles = MdiTiler.Tile(group, 300, 200);

        Assert.Equal(6, tiles.Count);
        Assert.All(tiles.Values, b => Assert.True(b.Width >= MdiTiler.MinCellWidth));
        // Each step walks diagonally, so every title bar stays reachable.
        var ordered = tiles.Values.OrderBy(b => b.X).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].X + MdiTiler.CascadeStep, ordered[i].X);
            Assert.Equal(ordered[i - 1].Y + MdiTiler.CascadeStep, ordered[i].Y);
        }
    }

    [Fact]
    public void Floating_panels_get_a_window_in_the_middle_of_the_area()
    {
        var tiles = MdiTiler.Tile(DefaultShape(), 1200, 800, extraIds: ["mapper", "room"]);

        // The Mapper floated outside the tree; it cascades from the centre.
        Assert.True(tiles.ContainsKey("mapper"));
        Assert.Equal(360, tiles["mapper"].X);
        Assert.Equal(240, tiles["mapper"].Y);
        // An extra already placed by the tree is not placed twice.
        Assert.True(tiles["room"].X < 264);
    }

    [Fact]
    public void Empty_columns_and_zero_proportions_do_not_swallow_space()
    {
        // #331's squeezed shape: an empty left column at Proportion 0.
        var tree = Node("proportional", "root-layout", orientation: "Horizontal", children:
        [
            Node("proportional", "left-col", 0, "Vertical"),
            Splitter(),
            Node("proportional", "center-col", 1, "Vertical", children:
            [
                Node("documentdock", "docs", children: [Leaf("game-text")]),
            ]),
        ]);
        var tiles = MdiTiler.Tile(tree, 900, 600);

        Assert.Equal(new MdiWindowBounds(0, 0, 900, 600, "Normal"), tiles["game-text"]);
    }

    [Fact]
    public void An_unmeasured_area_falls_back_to_a_usable_size()
    {
        var tiles = MdiTiler.Tile(Leaf("game-text"), double.NaN, 0);
        var b = tiles["game-text"];
        Assert.Equal(MdiTiler.FallbackWidth,  b.Width);
        Assert.Equal(MdiTiler.FallbackHeight, b.Height);
    }
}
