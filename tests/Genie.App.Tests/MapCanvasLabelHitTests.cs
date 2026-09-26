using Genie.App.Controls;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Label hit-testing for the mapper's edit mode (<see cref="MapCanvas.FindLabelAt"/>).
/// Genie 4's <c>FindLabel</c> takes the LAST label under the pointer, so when
/// two overlap the one drawn on top is the one you grab; a hidden floor's
/// labels are never hit; and the rectangle tested is exactly the one the label
/// pass paints (measured text + 2, one Genie 4 row tall).
/// </summary>
public class MapCanvasLabelHitTests
{
    private static double Measure(string text, double fontSize) => text.Length * 6.0 * (fontSize / 11.0);

    private static MapZone Zone()
    {
        var zone = new MapZone();
        zone.Nodes[1] = new MapNode { Id = 1, PixelX = 0,   PixelY = 0 };
        zone.Nodes[2] = new MapNode { Id = 2, PixelX = 200, PixelY = 100 };
        zone.Labels.Add(new MapLabel { Text = "Bank",     X = 100 / 20.0, Y = 40 / 20.0, Z = 0 });
        zone.Labels.Add(new MapLabel { Text = "Bank Row", X = 100 / 20.0, Y = 40 / 20.0, Z = 0 });   // overlaps, later in file
        zone.Labels.Add(new MapLabel { Text = "Upstairs", X = 100 / 20.0, Y = 40 / 20.0, Z = 1 });
        return zone;
    }

    [Fact]
    public void Point_inside_a_label_rectangle_hits_it()
    {
        var zone = Zone();
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        var (x, y) = proj.LabelOrigin(zone.Labels[0]);
        var hit = MapCanvas.FindLabelAt(zone, 0, proj, Measure, x + 3, y + 3);
        Assert.NotNull(hit);
    }

    [Fact]
    public void The_last_overlapping_label_wins_like_genie4()
    {
        var zone = Zone();
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        var (x, y) = proj.LabelOrigin(zone.Labels[0]);
        Assert.Equal("Bank Row", MapCanvas.FindLabelAt(zone, 0, proj, Measure, x + 3, y + 3)!.Text);
    }

    [Fact]
    public void Beyond_the_shorter_labels_text_only_the_longer_one_is_hit()
    {
        var zone = Zone();
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        var (x, y) = proj.LabelOrigin(zone.Labels[0]);
        var shortWidth = Measure("Bank", proj.LabelFontSize) + 2;
        Assert.Equal("Bank Row", MapCanvas.FindLabelAt(zone, 0, proj, Measure, x + shortWidth + 5, y + 3)!.Text);
    }

    [Fact]
    public void Misses_outside_the_rectangle_and_on_other_floors()
    {
        var zone = Zone();
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        var (x, y) = proj.LabelOrigin(zone.Labels[0]);
        Assert.Null(MapCanvas.FindLabelAt(zone, 0, proj, Measure, x - 1, y + 3));                         // left of it
        Assert.Null(MapCanvas.FindLabelAt(zone, 0, proj, Measure, x + 3, y + proj.LabelRowHeight + 1));   // below it
        // Floor 1 has no rooms in this zone, so its projection is empty; but
        // even against floor 0's projection the z=1 label is never a hit.
        Assert.DoesNotContain(MapCanvas.FindLabelAt(zone, 0, proj, Measure, x + 3, y + 3)!.Text, new[] { "Upstairs" });
    }
}
