using Genie.App.Settings;

namespace Genie.App.Docking;

/// <summary>
/// Translates a tabbed/docked arrangement into windowed-mode (MDI) geometry
/// (public #363). Toggling Windowed Mode used to throw the current layout away
/// and cascade the canonical defaults; this carries the arrangement across
/// instead, as far as MDI can express it.
///
/// <para>Each dock group (a ToolDock or DocumentDock) is given the rectangle
/// its proportions give it inside the available area, and the panels in that
/// group — tabs, in the docked layout — tile that rectangle as a grid, so a
/// group of four stream tabs becomes four child windows side by side where the
/// group used to be. A group too small to split sensibly cascades its windows
/// inside the rectangle instead. Panels that were floating in the docked layout
/// (outside the tree the snapshot describes) cascade from the middle of the
/// area.</para>
///
/// <para>Pure geometry over a <see cref="DockNodeSnapshot"/> — no Dock or
/// Avalonia types — so it's testable without a window. Dock's MDI layout
/// manager clamps every rect to the area it actually gets, so an area estimate
/// that is a little too large still lands every window on screen.</para>
/// </summary>
public static class MdiTiler
{
    /// <summary>Smallest child a group is split into before it cascades
    /// instead. Below this a window shows only its title bar.</summary>
    public const double MinCellWidth  = 160;
    public const double MinCellHeight = 100;

    /// <summary>Per-window offset for cascaded windows — the same step Dock's
    /// own default cascade uses.</summary>
    public const double CascadeStep = 24;

    /// <summary>Area assumed when the host can't measure the real one.</summary>
    public const double FallbackWidth  = 1200;
    public const double FallbackHeight = 760;

    private readonly record struct Rect(double X, double Y, double W, double H);

    /// <summary>
    /// Geometry for every leaf in <paramref name="tree"/> plus each of
    /// <paramref name="extraIds"/> (panels outside the tree, e.g. floats), in an
    /// area of <paramref name="width"/> × <paramref name="height"/>. Ids already
    /// placed by the tree are not placed again. Returns MDI coordinates with the
    /// area's top-left at 0,0.
    /// </summary>
    public static Dictionary<string, MdiWindowBounds> Tile(
        DockNodeSnapshot? tree, double width, double height,
        IEnumerable<string>? extraIds = null)
    {
        if (!double.IsFinite(width)  || width  <= 0) width  = FallbackWidth;
        if (!double.IsFinite(height) || height <= 0) height = FallbackHeight;

        var result = new Dictionary<string, MdiWindowBounds>(StringComparer.OrdinalIgnoreCase);
        if (tree is not null)
            Place(tree, new Rect(0, 0, width, height), result);

        var extras = (extraIds ?? Enumerable.Empty<string>())
            .Where(id => !string.IsNullOrEmpty(id) && !result.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extras.Count > 0)
        {
            // A float had its own OS window, whose screen position means nothing
            // inside the main window — give it a middle-of-the-area window.
            var w = Math.Max(MinCellWidth,  width  * 0.4);
            var h = Math.Max(MinCellHeight, height * 0.4);
            Cascade(extras, new Rect(width * 0.3, height * 0.3, w, h), result, shrink: false);
        }
        return result;
    }

    private static void Place(DockNodeSnapshot node, Rect area, Dictionary<string, MdiWindowBounds> result)
    {
        switch (node.Kind)
        {
            case "proportional":
                Split(node, area, result);
                break;
            case "tooldock":
            case "documentdock":
                var ids = new List<string>();
                CollectLeaves(node, ids);
                // The active tab goes first so it lands top-left — it is the one
                // the user was looking at.
                if (node.ActiveId is { } active &&
                    ids.FindIndex(i => string.Equals(i, active, StringComparison.OrdinalIgnoreCase)) is > 0 and var at)
                {
                    ids.RemoveAt(at);
                    ids.Insert(0, active);
                }
                Grid(ids, area, result);
                break;
            case "leaf":
                if (node.Id is { Length: > 0 } id)
                    Grid(new List<string> { id }, area, result);
                break;
        }
    }

    /// <summary>Divide <paramref name="area"/> among a ProportionalDock's
    /// children along its orientation. Explicit proportions keep their share
    /// (normalised when they over- or under-subscribe); NaN children split
    /// whatever is left evenly, which is how Dock itself treats them. Splitters
    /// and empty children take no space.</summary>
    private static void Split(DockNodeSnapshot node, Rect area, Dictionary<string, MdiWindowBounds> result)
    {
        var kids = node.Children.Where(c => c.Kind != "splitter" && HasLeaves(c)).ToList();
        if (kids.Count == 0) return;

        var vertical = string.Equals(node.Orientation, "Vertical", StringComparison.OrdinalIgnoreCase);
        var explicitSum = kids.Where(k => IsShare(k.Proportion)).Sum(k => k.Proportion);
        var autoCount   = kids.Count(k => !IsShare(k.Proportion));

        double[] shares = new double[kids.Count];
        if (autoCount == 0)
        {
            for (int i = 0; i < kids.Count; i++) shares[i] = kids[i].Proportion / explicitSum;
        }
        else
        {
            // Auto children get what the explicit ones leave, but never less
            // than an equal slice's worth of room if the explicit ones claim it all.
            var scale     = explicitSum > 1 ? 1 / explicitSum : 1;
            var remainder = Math.Max(0, 1 - explicitSum * scale);
            if (remainder <= 0)
            {
                remainder = 1.0 / kids.Count * autoCount;
                scale     = (1 - remainder) / explicitSum;
            }
            for (int i = 0; i < kids.Count; i++)
                shares[i] = IsShare(kids[i].Proportion)
                    ? kids[i].Proportion * scale
                    : remainder / autoCount;
        }

        var offset = 0.0;
        for (int i = 0; i < kids.Count; i++)
        {
            var span = (vertical ? area.H : area.W) * shares[i];
            var sub = vertical
                ? new Rect(area.X, area.Y + offset, area.W, span)
                : new Rect(area.X + offset, area.Y, span, area.H);
            offset += span;
            Place(kids[i], sub, result);
        }
    }

    /// <summary>Tile <paramref name="ids"/> across <paramref name="area"/> in a
    /// grid whose shape follows the area's aspect ratio, or cascade them inside
    /// it when the cells would come out too small to use.</summary>
    private static void Grid(List<string> ids, Rect area, Dictionary<string, MdiWindowBounds> result)
    {
        ids = ids.Where(id => !result.ContainsKey(id)).ToList();
        if (ids.Count == 0) return;

        var n    = ids.Count;
        var cols = (int)Math.Round(Math.Sqrt(n * Math.Max(area.W, 1) / Math.Max(area.H, 1)));
        cols     = Math.Clamp(cols, 1, n);
        var rows = (int)Math.Ceiling(n / (double)cols);
        var cw   = area.W / cols;
        var ch   = area.H / rows;

        if (n > 1 && (cw < MinCellWidth || ch < MinCellHeight))
        {
            Cascade(ids, area, result, shrink: true);
            return;
        }

        for (int i = 0; i < n; i++)
        {
            var r = i / cols;
            var c = i % cols;
            // The last row may be short — stretch its cells across the width so
            // the group's rectangle is still covered.
            var inRow = r == rows - 1 ? n - r * cols : cols;
            var w     = area.W / inRow;
            result[ids[i]] = Bounds(new Rect(area.X + c * w, area.Y + r * ch, w, ch));
        }
    }

    /// <summary>Stack <paramref name="ids"/> diagonally from the area's origin.
    /// With <paramref name="shrink"/> every window is sized so the whole cascade
    /// stays inside the area (a group's rectangle); without it each window keeps
    /// the area's size and the cascade walks outward (Dock clamps it back).</summary>
    private static void Cascade(List<string> ids, Rect area, Dictionary<string, MdiWindowBounds> result, bool shrink)
    {
        var travel = CascadeStep * (ids.Count - 1);
        var w = shrink ? Math.Max(MinCellWidth,  area.W - travel) : area.W;
        var h = shrink ? Math.Max(MinCellHeight, area.H - travel) : area.H;
        for (int i = 0; i < ids.Count; i++)
            result[ids[i]] = Bounds(new Rect(area.X + i * CascadeStep, area.Y + i * CascadeStep, w, h));
    }

    private static MdiWindowBounds Bounds(Rect r) =>
        new(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.W), Math.Round(r.H), "Normal");

    private static bool IsShare(double p) => double.IsFinite(p) && p > 0;

    private static bool HasLeaves(DockNodeSnapshot n) =>
        n.Kind == "leaf" ? !string.IsNullOrEmpty(n.Id) : n.Children.Any(HasLeaves);

    private static void CollectLeaves(DockNodeSnapshot n, List<string> ids)
    {
        if (n.Kind == "leaf") { if (n.Id is { Length: > 0 } id) ids.Add(id); return; }
        foreach (var c in n.Children) CollectLeaves(c, ids);
    }
}
