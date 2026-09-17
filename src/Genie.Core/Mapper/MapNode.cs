namespace Genie.Core.Mapper;

public sealed class MapNode
{
    public int    Id           { get; set; }
    public string Title        { get; set; } = string.Empty;

    /// <summary>
    /// The room's first description. Backed by <see cref="Descriptions"/>;
    /// assigning replaces the first entry (or creates it).
    /// </summary>
    public string Description
    {
        get => Descriptions.Count > 0 ? Descriptions[0] : string.Empty;
        set { if (Descriptions.Count == 0) Descriptions.Add(value); else Descriptions[0] = value; }
    }

    /// <summary>
    /// Authoritative on-disk X position, in Genie 4 pixel units. This — not
    /// <see cref="X"/> — is what the exporter writes, so a room the user never
    /// moved round-trips to the exact value it had upstream.
    /// </summary>
    /// <remarks>
    /// Genie 4 pixel coordinates are NOT reliably multiples of the 20px node
    /// grid: across the 132-file community corpus 12,211 of 27,391 positions
    /// (44.6%) are off-grid, and 10,900 are negative. The old importer did
    /// <c>X = px / 20</c> with integer division, which moved every one of those
    /// rooms — and truncated negatives toward zero, so the shift wasn't even
    /// symmetric. Keeping the pixels verbatim and deriving the grid cell fixes
    /// that without disturbing any consumer: the canvas, pathfinder and
    /// automapper all still see the same integer <see cref="X"/>/<see cref="Y"/>
    /// grid they always have.
    /// </remarks>
    public int    PixelX       { get; set; }

    /// <inheritdoc cref="PixelX"/>
    public int    PixelY       { get; set; }

    /// <summary>
    /// Grid-cell X (pixels ÷ 20), the unit every renderer and pathfinder works
    /// in. Assigning snaps <see cref="PixelX"/> onto the grid — correct, since
    /// the only writer is a user dragging the room to a new cell.
    /// </summary>
    public int    X            { get => Round20(PixelX); set => PixelX = value * 20; }

    /// <inheritdoc cref="X"/>
    public int    Y            { get => Round20(PixelY); set => PixelY = value * 20; }

    public int    Z            { get; set; }

    // Round-half-to-even, and symmetric about zero — unlike the integer
    // division this replaced, which truncated -30 to -1 but +30 to +1.
    private static int Round20(int px) => (int)Math.Round(px / 20.0, MidpointRounding.ToEven);
    public string Notes        { get; set; } = string.Empty;
    public string Color        { get; set; } = string.Empty;
    public string ServerRoomId { get; set; } = string.Empty;
    public List<MapExit> Exits { get; set; } = new();

    /// <summary>
    /// Every <c>&lt;description&gt;</c> element on the room, in file order —
    /// including empty ones, which occur in the community corpus and must
    /// survive the round-trip.
    /// </summary>
    /// <remarks>
    /// Genie 4 rooms may carry several descriptions (seasonal / day-night /
    /// post-event variants). The importer used
    /// <c>SelectSingleNode("description")</c>, which silently kept only the
    /// first — 3,963 nodes in the community corpus have more than one, so the
    /// rest were dropped on import and then erased from disk on the next
    /// export.
    /// <para>
    /// Note that <see cref="AutoMapperEngine"/> still matches arrivals against
    /// <see cref="Description"/> alone. Preserving the variants here is what
    /// makes matching against all of them possible later; it does not by
    /// itself change matching.
    /// </para>
    /// </remarks>
    public List<string> Descriptions { get; set; } = new();

    /// <summary>
    /// True when this room links to another zone file — its <see cref="Notes"/>
    /// reference a ".xml" map (e.g. <c>"Map31_Riverhaven_East_Gate.xml|E Gate|egate"</c>).
    /// Mirrors Genie 4's <c>Node.IsLabelFile</c> (<c>Note.Contains(".xml")</c>); the
    /// map canvas paints these "cross-zone connector" rooms with a blue border —
    /// the blue boxes that show how one map connects to the next.
    /// </summary>
    public bool IsCrossZone => Notes.Contains(".xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Free-form room tags (Lich-style: "bank", "moongate", "gravecottage").
    /// Drive <c>#goto @tag</c> nearest-routing and
    /// <see cref="AutoMapperEngine.FindNearestByTag"/>. Round-tripped as a
    /// '|'-separated <c>tags="..."</c> node attribute — an additive Genie 5
    /// extension that old Genie 4 clients ignore (same as server_id/requires).
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Case-insensitive tag membership test.</summary>
    public bool HasTag(string tag) =>
        Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase));

    public MapExit? GetExit(Direction dir) => Exits.FirstOrDefault(e => e.Direction == dir);

    public MapExit GetOrAddExit(Direction dir, string moveCommand)
    {
        var ex = GetExit(dir);
        if (ex is null) { ex = new MapExit { Direction = dir, MoveCommand = moveCommand }; Exits.Add(ex); }
        return ex;
    }
}
