using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="MapProjection"/> — the one map-pixel → canvas transform the mapper
/// draws, hit-tests and drags through.
///
/// The bug these pin: Genie 4 draws every room at its raw pixel position and its
/// snap-to-grid step is 10px, so the community maps are authored on a 10px grid
/// (623 of Crossing's 1,060 rooms have an odd-ten coordinate). The old canvas
/// rounded each room to a 20px cell and drew it there, collapsing 4,043 rooms in
/// 64 shipped zones onto their neighbours; labels were anchored at the cell
/// CORNER while rooms sat at the cell CENTRE (half a cell off), and the canvas
/// was sized from rooms alone so label text past the outermost room clipped.
/// </summary>
public class MapProjectionTests
{
    // A deterministic stand-in for FormattedText: 6 canvas px per character at
    // an 11px font, scaled with the font size.
    private static double Measure(string text, double fontSize) => text.Length * 6.0 * (fontSize / 11.0);

    private static MapNode Room(int id, int px, int py, int z = 0) =>
        new() { Id = id, Title = $"room {id}", PixelX = px, PixelY = py, Z = z };

    /// <summary>Crossing's Goldstone Square / Arboretum cluster — four rooms
    /// 10px apart that the 20px snap drew as ONE square.</summary>
    private static MapZone Goldstone()
    {
        var zone = new MapZone { Name = "Goldstone" };
        foreach (var n in new[] { Room(279, 30, -468), Room(280, 30, -458), Room(289, 40, -468), Room(293, 50, -458) })
            zone.Nodes[n.Id] = n;
        return zone;
    }

    [Fact]
    public void Rooms_ten_pixels_apart_get_four_distinct_non_overlapping_boxes()
    {
        var zone = Goldstone();
        var proj = MapProjection.Create(zone, level: 0, zoom: 1.0, Measure);

        var rects = zone.Nodes.Values.Select(proj.NodeRect).ToList();
        Assert.Equal(4, rects.Distinct().Count());

        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                Assert.False(rects[i].Intersects(rects[j]), $"rooms {i} and {j} overlap");

        // Genie 4 pitch: 10 map px → 11 canvas px at zoom 1 (BaseScale 1.1), and
        // the box is the 9px Genie 4 square scaled — so neighbours sit ~1px apart.
        var (ax, _) = proj.NodeCenter(zone.Nodes[279]);
        var (bx, _) = proj.NodeCenter(zone.Nodes[289]);
        Assert.Equal(11.0, bx - ax, 6);
        Assert.Equal(9.9, proj.NodeSide, 6);
    }

    [Fact]
    public void Old_twenty_pixel_snap_really_did_collapse_the_cluster()
    {
        // Documents WHY the projection ignores MapNode.X: the grid cell is the
        // same for rooms 10px apart, so anything keyed on it stacks them.
        var zone = Goldstone();
        var cells = zone.Nodes.Values.Select(n => (n.X, n.Y)).Distinct().Count();
        Assert.Equal(1, cells);
    }

    [Fact]
    public void Label_shares_the_rooms_origin_so_it_lands_where_the_author_put_it()
    {
        // Genie 4: a room's CENTRE is at its raw pixel; a label's TOP-LEFT is at
        // its raw pixel. Same space, no half-cell offset between the two.
        var zone = Goldstone();
        zone.Labels.Add(new MapLabel { Text = "Goldstone", X = 30 / 20.0, Y = -468 / 20.0, Z = 0 });
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);

        var center = proj.NodeCenter(zone.Nodes[279]);
        var origin = proj.LabelOrigin(zone.Labels[0]);
        Assert.Equal(center.X, origin.X, 6);
        Assert.Equal(center.Y, origin.Y, 6);

        // The text itself is painted 1px inside the label rectangle (Genie 4
        // DrawString at r.X + 1, r.Y + 1).
        var text = proj.LabelTextOrigin(zone.Labels[0]);
        Assert.Equal(origin.X + 1, text.X, 6);
        Assert.Equal(origin.Y + 1, text.Y, 6);
    }

    [Fact]
    public void Labels_extend_the_canvas_so_their_text_never_clips()
    {
        // Crossing: leftmost room at x=-401, "Drelstead Prison" label at -411;
        // and a long label to the right of the rightmost room.
        var zone = new MapZone();
        zone.Nodes[1] = Room(1, -401, 0);
        zone.Nodes[2] = Room(2, 490, 0);
        var left  = new MapLabel { Text = "Drelstead Prison", X = -411 / 20.0, Y = 0, Z = 0 };
        var right = new MapLabel { Text = "Northeast Gate",   X = 480 / 20.0,  Y = 0, Z = 0 };
        zone.Labels.Add(left);
        zone.Labels.Add(right);

        var proj = MapProjection.Create(zone, 0, 1.0, Measure);

        // The label, not the room, is the left extent: its origin sits exactly
        // one padding in from the canvas edge.
        Assert.Equal(proj.Padding, proj.LabelOrigin(left).X, 6);

        // Every label rectangle lies inside the canvas (with the padding intact).
        foreach (var l in zone.Labels)
        {
            var r = proj.LabelRect(l, Measure(l.Text, proj.LabelFontSize));
            Assert.True(r.X >= proj.Padding - 1e-6, $"{l.Text} starts left of the padding");
            Assert.True(r.Right <= proj.CanvasWidth - proj.Padding + 1e-6, $"{l.Text} runs past the canvas");
            Assert.True(r.Bottom <= proj.CanvasHeight - proj.Padding + 1e-6, $"{l.Text} runs below the canvas");
        }

        // With labels excluded the canvas shrinks back to the rooms.
        var roomsOnly = MapProjection.Create(zone, 0, 1.0, Measure, includeLabels: false);
        Assert.True(roomsOnly.CanvasWidth < proj.CanvasWidth);
        Assert.Equal(-401, roomsOnly.MinMapX);
    }

    [Fact]
    public void Labels_on_another_floor_do_not_affect_this_floors_bounds()
    {
        var zone = Goldstone();
        zone.Labels.Add(new MapLabel { Text = "Upstairs only", X = -50, Y = -50, Z = 1 });
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        Assert.Equal(30, proj.MinMapX);
        Assert.Equal(-468, proj.MinMapY);
    }

    [Fact]
    public void Ghost_floor_rooms_project_through_the_same_origin()
    {
        // A room directly above another (same x/y, z+1) draws exactly over it.
        var zone = Goldstone();
        zone.Nodes[900] = Room(900, 30, -468, z: 1);
        var proj = MapProjection.Create(zone, 0, 1.0, Measure);
        Assert.Equal(proj.NodeCenter(zone.Nodes[279]), proj.NodeCenter(zone.Nodes[900]));
    }

    [Fact]
    public void Zoom_scales_pitch_box_font_and_padding_together()
    {
        var zone = Goldstone();
        var z1 = MapProjection.Create(zone, 0, 1.0, Measure);
        var z2 = MapProjection.Create(zone, 0, 2.0, Measure);
        Assert.Equal(z1.Scale * 2,          z2.Scale, 6);
        Assert.Equal(z1.NodeSide * 2,       z2.NodeSide, 6);
        Assert.Equal(z1.LabelFontSize * 2,  z2.LabelFontSize, 6);
        Assert.Equal(z1.Padding * 2,        z2.Padding, 6);
    }

    [Fact]
    public void Canvas_to_map_round_trips()
    {
        var zone = Goldstone();
        var proj = MapProjection.Create(zone, 0, 1.3, Measure);
        var (cx, cy) = proj.NodeCenter(zone.Nodes[293]);
        Assert.Equal(50,   proj.ToMapX(cx), 6);
        Assert.Equal(-458, proj.ToMapY(cy), 6);
    }

    [Theory]
    [InlineData(33.0,  true,  30)]   // Genie 4 snap: nearest 10px
    [InlineData(35.0,  true,  40)]   // half-way rounds away from zero
    [InlineData(-25.0, true,  -30)]
    [InlineData(-24.9, true,  -20)]
    [InlineData(33.4,  false, 33)]   // snap off: nearest whole pixel (XML holds ints)
    [InlineData(33.6,  false, 34)]
    public void Drag_release_snaps_to_genie4s_ten_pixel_grid(double mapPx, bool snap, int expected) =>
        Assert.Equal(expected, MapProjection.SnapMapPx(mapPx, snap));

    [Fact]
    public void Empty_floor_reports_no_geometry()
    {
        var proj = MapProjection.Create(Goldstone(), level: 5, zoom: 1.0, Measure);
        Assert.False(proj.Any);
        Assert.Equal(0, proj.CanvasWidth);
    }

    /// <summary>
    /// The whole installed map corpus: on every floor of every zone, rooms at
    /// distinct positions never overlap and every label rectangle lies inside
    /// the canvas. Runs only where the maps are installed (the developer's
    /// %APPDATA%\Genie5\Maps); CI has no corpus and passes vacuously.
    /// (Six corpus files place ten rooms on the exact same pixel as another
    /// room — author errors Genie 4 stacks too — hence "distinct positions".)
    /// </summary>
    [Fact]
    public void Installed_corpus_draws_no_stacked_rooms_and_no_clipped_labels()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Genie5", "Maps");
        if (!Directory.Exists(dir)) return;

        int zones = 0, rooms = 0, labels = 0;
        foreach (var file in Directory.GetFiles(dir, "*.xml"))
        {
            MapZone zone;
            try { zone = Genie4MapImporter.Import(file); } catch { continue; }
            zones++;

            foreach (var level in zone.Nodes.Values.Select(n => n.Z).Distinct())
            {
                var proj  = MapProjection.Create(zone, level, 1.0, Measure);
                var floor = zone.Nodes.Values.Where(n => n.Z == level)
                                             .GroupBy(n => (n.PixelX, n.PixelY))
                                             .Select(g => g.First())
                                             .ToList();
                rooms += floor.Count;

                // Bucket by 20px so the overlap check is O(n) per floor, not O(n²)
                // on Ratha's 880 rooms.
                var buckets = floor.GroupBy(n => (n.PixelX / 20, n.PixelY / 20))
                                   .ToDictionary(g => g.Key, g => g.ToList());
                foreach (var a in floor)
                {
                    var ra = proj.NodeRect(a);
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (!buckets.TryGetValue((a.PixelX / 20 + dx, a.PixelY / 20 + dy), out var near)) continue;
                        foreach (var b in near)
                        {
                            if (b.Id <= a.Id) continue;
                            // Rooms an author placed closer than one snap step
                            // (Hibarnhvidar 98 at 20,90 vs 503 at 12,98) overlap
                            // in Genie 4 as well — a 9px box can't fit in 8px.
                            // The projection's job is the ≥10px grid.
                            if (Math.Max(Math.Abs(a.PixelX - b.PixelX), Math.Abs(a.PixelY - b.PixelY)) < MapProjection.SnapPx)
                                continue;
                            Assert.False(ra.Intersects(proj.NodeRect(b)),
                                $"{Path.GetFileName(file)} z={level}: rooms {a.Id} ({a.PixelX},{a.PixelY}) and {b.Id} ({b.PixelX},{b.PixelY}) overlap");
                        }
                    }
                }

                foreach (var l in zone.Labels.Where(l => l.Z == level && l.Text.Length > 0))
                {
                    labels++;
                    var r = proj.LabelRect(l, Measure(l.Text, proj.LabelFontSize));
                    Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= proj.CanvasWidth && r.Bottom <= proj.CanvasHeight,
                        $"{Path.GetFileName(file)} z={level}: label '{l.Text}' clips");
                }
            }
        }

        // Guard against a silently empty corpus dir making this pass for nothing.
        if (zones > 0) Assert.True(rooms > 0 && labels > 0);
    }
}
