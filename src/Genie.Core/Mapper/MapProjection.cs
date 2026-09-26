namespace Genie.Core.Mapper;

/// <summary>An axis-aligned rectangle in canvas pixels — UI-toolkit-free so the
/// projection (and its tests) live in Core.</summary>
public readonly record struct MapRect(double X, double Y, double Width, double Height)
{
    public double Right  => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width  / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(double px, double py) =>
        px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>True when the two rectangles share any area (touching edges do
    /// not count — a 1px gap between neighbouring rooms is a clean separation).</summary>
    public bool Intersects(MapRect o) =>
        X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
}

/// <summary>Measures on-map label text: returns the rendered width, in canvas
/// pixels, of <paramref name="text"/> at <paramref name="fontSize"/>.</summary>
public delegate double LabelMeasure(string text, double fontSize);

/// <summary>
/// Maps Genie 4 map pixels (the <c>&lt;position x y/&gt;</c> values, and the
/// label positions) onto the map canvas, for one floor. This is the ONE place
/// the mapper turns a stored position into a drawn one; the canvas, hit-test,
/// drag and layout all go through it so they can't disagree.
///
/// <para><b>Why pixels, not grid cells.</b> Genie 4 draws every room as an
/// 8px box centred on its raw pixel position (<c>MapForm.ConvertPoint(pos,
/// 4 * scale)</c>) and its snap-to-grid step is 10px (<c>e.X % 10</c>). The
/// community maps are authored on that 10px grid: 623 of Crossing's 1,060
/// rooms have an x or y ending in an odd multiple of 10, and across the 123
/// shipped zones 29 are predominantly 10px-spaced. The previous canvas
/// rounded each room to a 20px cell (<see cref="MapNode.X"/>) and drew it
/// there, which collapsed 4,043 rooms in 64 files onto neighbours — the four
/// Goldstone Square / Arboretum rooms (30,-468 · 30,-458 · 40,-468 · 50,-458)
/// drew as a single square. Projecting the pixels directly draws them where
/// the author put them.</para>
///
/// <para><b>Labels share the origin.</b> Genie 4 anchors a label's top-left at
/// its raw pixel and a room's centre at its raw pixel, in the same space. The
/// old canvas put labels at the cell CORNER and rooms at the cell CENTRE, so
/// every label sat half a cell up-left of where the author placed it. Both
/// now go through <see cref="ToCanvasX"/>/<see cref="ToCanvasY"/>.</para>
///
/// <para><b>Labels are part of the bounds.</b> A label whose text runs past
/// the outermost room (Crossing's "Northeast Gate", or "Drelstead Prison" at
/// x=-411 beside a leftmost room at -401) used to be clipped because the
/// canvas was sized from rooms alone. <see cref="Create"/> folds each visible
/// label's measured rectangle into the extents.</para>
/// </summary>
public sealed class MapProjection
{
    // ── Genie 4 geometry (MapForm.cs) ────────────────────────────────────
    /// <summary>Map pixels per <see cref="MapNode.X"/> grid unit.</summary>
    public const double G4GridPx = 20.0;
    /// <summary>Room box side in map pixels: Genie 4 fills 8px and outlines
    /// 0..8 inclusive, so the visible square is 9px.</summary>
    public const double G4NodePx = 9.0;
    /// <summary>Genie 4's label rectangle height (<c>r.Height = 15</c>).</summary>
    public const double G4LabelRowPx = 15.0;
    /// <summary>Genie 4 label font: the WinForms default (8.25pt ≈ 11px),
    /// multiplied by the map scale.</summary>
    public const double G4LabelFontPx = 11.0;
    /// <summary>Genie 4's snap-to-grid step for dragged rooms and labels
    /// (<c>e.X % 10</c>) — NOT the 20px automapper placement unit.</summary>
    public const double SnapPx = 10.0;

    // ── Genie 5 canvas sizing ────────────────────────────────────────────
    /// <summary>Canvas pixels per map pixel at zoom 1. 1.1 keeps the 22px
    /// room pitch the canvas has always had for a 20px-grid map.</summary>
    public const double BaseScale   = 1.1;
    public const double BasePadding = 32.0;

    /// <summary>Canvas pixels per map pixel (<see cref="BaseScale"/> × zoom).</summary>
    public double Scale   { get; }
    /// <summary>Blank margin around the drawn extents, in canvas pixels.</summary>
    public double Padding { get; }

    /// <summary>Extents of everything drawn on this floor, in map pixels
    /// (rooms and, when included, the label rectangles).</summary>
    public double MinMapX { get; }
    public double MinMapY { get; }
    public double MaxMapX { get; }
    public double MaxMapY { get; }

    /// <summary>False when the floor has no rooms — the canvas shows a message
    /// instead of a map, and every coordinate here is meaningless.</summary>
    public bool Any { get; }

    public double NodeSide       => G4NodePx      * Scale;
    public double LabelFontSize  => G4LabelFontPx * Scale;
    public double LabelRowHeight => G4LabelRowPx  * Scale;

    public double CanvasWidth  => Any ? (MaxMapX - MinMapX) * Scale + Padding * 2 : 0;
    public double CanvasHeight => Any ? (MaxMapY - MinMapY) * Scale + Padding * 2 : 0;

    private MapProjection(double scale, double padding, bool any,
                          double minX, double minY, double maxX, double maxY)
    {
        Scale = scale; Padding = padding; Any = any;
        MinMapX = minX; MinMapY = minY; MaxMapX = maxX; MaxMapY = maxY;
    }

    /// <summary>
    /// Build the projection for <paramref name="level"/> of <paramref name="zone"/>.
    /// <paramref name="measure"/> supplies label text widths (canvas px) so the
    /// labels can be folded into the extents; pass null (or
    /// <paramref name="includeLabels"/> = false) to size from rooms alone.
    /// </summary>
    public static MapProjection Create(MapZone zone, int level, double zoom,
                                       LabelMeasure? measure = null, bool includeLabels = true)
    {
        var scale   = BaseScale   * zoom;
        var padding = BasePadding * zoom;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var node in zone.Nodes.Values)
        {
            if (node.Z != level) continue;
            any = true;
            if (node.PixelX < minX) minX = node.PixelX;
            if (node.PixelX > maxX) maxX = node.PixelX;
            if (node.PixelY < minY) minY = node.PixelY;
            if (node.PixelY > maxY) maxY = node.PixelY;
        }

        if (!any)
            return new MapProjection(scale, padding, false, 0, 0, 0, 0);

        if (includeLabels && measure is not null)
        {
            var fontSize = G4LabelFontPx * scale;
            foreach (var label in zone.Labels)
            {
                if (label.Z != level || string.IsNullOrEmpty(label.Text)) continue;
                var x0 = label.X * G4GridPx;
                var y0 = label.Y * G4GridPx;
                // Text width comes back in canvas px; the extents are map px.
                var x1 = x0 + (measure(label.Text, fontSize) + 2) / scale;
                var y1 = y0 + G4LabelRowPx;
                if (x0 < minX) minX = x0;
                if (y0 < minY) minY = y0;
                if (x1 > maxX) maxX = x1;
                if (y1 > maxY) maxY = y1;
            }
        }

        return new MapProjection(scale, padding, true, minX, minY, maxX, maxY);
    }

    // ── Coordinate transforms ────────────────────────────────────────────

    public double ToCanvasX(double mapPx) => Padding + (mapPx - MinMapX) * Scale;
    public double ToCanvasY(double mapPx) => Padding + (mapPx - MinMapY) * Scale;
    public double ToMapX(double canvasPx) => (canvasPx - Padding) / Scale + MinMapX;
    public double ToMapY(double canvasPx) => (canvasPx - Padding) / Scale + MinMapY;

    /// <summary>Round a map-pixel coordinate the way a drag release stores it:
    /// to Genie 4's 10px grid when snapping, else to the nearest whole pixel
    /// (the XML holds integers).</summary>
    public static int SnapMapPx(double mapPx, bool snapToGrid) =>
        snapToGrid ? (int)(Math.Round(mapPx / SnapPx, MidpointRounding.AwayFromZero) * SnapPx)
                   : (int)Math.Round(mapPx, MidpointRounding.AwayFromZero);

    // ── Rooms ────────────────────────────────────────────────────────────

    /// <summary>Canvas centre of a room — its raw pixel position projected.
    /// Works for rooms on other floors too (ghost floors share the origin).</summary>
    public (double X, double Y) NodeCenter(MapNode node) =>
        (ToCanvasX(node.PixelX), ToCanvasY(node.PixelY));

    /// <summary>The room's box: <see cref="NodeSide"/> square centred on
    /// <see cref="NodeCenter"/>, as Genie 4 draws it.</summary>
    public MapRect NodeRect(MapNode node)
    {
        var (cx, cy) = NodeCenter(node);
        var half = NodeSide / 2;
        return new MapRect(cx - half, cy - half, NodeSide, NodeSide);
    }

    // ── Labels ───────────────────────────────────────────────────────────

    /// <summary>Canvas top-left of a label's rectangle — its stored position
    /// projected through the SAME transform as the rooms.</summary>
    public (double X, double Y) LabelOrigin(MapLabel label) =>
        (ToCanvasX(label.X * G4GridPx), ToCanvasY(label.Y * G4GridPx));

    /// <summary>The label's rectangle (Genie 4: text width + 1px inset each side,
    /// 15px row). <paramref name="textWidth"/> is the measured canvas width.</summary>
    public MapRect LabelRect(MapLabel label, double textWidth)
    {
        var (x, y) = LabelOrigin(label);
        return new MapRect(x, y, textWidth + 2, LabelRowHeight);
    }

    /// <summary>Where the text itself is painted: Genie 4 draws it 1px inside
    /// the label rectangle.</summary>
    public (double X, double Y) LabelTextOrigin(MapLabel label)
    {
        var (x, y) = LabelOrigin(label);
        return (x + 1, y + 1);
    }
}
