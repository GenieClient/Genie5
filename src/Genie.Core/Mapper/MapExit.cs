namespace Genie.Core.Mapper;

public sealed class MapExit
{
    public Direction Direction     { get; set; }
    public string    MoveCommand   { get; set; } = string.Empty;
    public int?      DestinationId { get; set; }

    /// <summary>
    /// The raw <c>exit="…"</c> token exactly as it appears on disk. Written
    /// back verbatim by the exporter; <see cref="Direction"/> is only the
    /// parsed compass hint derived from it.
    /// </summary>
    /// <remarks>
    /// <see cref="Mapper.Direction"/> has no <c>Go</c> or <c>Climb</c> member,
    /// so every non-compass arc parses to <see cref="Mapper.Direction.None"/>
    /// — and the exporter, which wrote <c>Direction.ToString()</c>, turned all
    /// of them into <c>exit="none"</c>. That is 13,056 of 59,382 arcs (22.0%)
    /// in the community corpus, and it breaks Genie 4's own ability to follow
    /// or author those portal arcs. It also flattened multi-word tokens the
    /// enum can't express at all (<c>exit="go branches"</c>,
    /// <c>exit="go moss"</c>). Keeping the token separate from the parsed
    /// direction makes the round-trip exact without changing what the
    /// pathfinder or automapper consider a walkable compass step.
    /// </remarks>
    public string    ExitToken     { get; set; } = string.Empty;

    /// <summary>
    /// Genie 4's legacy <c>name="…"</c> arc attribute. On the 58 corpus arcs
    /// that carry it instead of <c>move</c>, Genie 4 falls back to it for the
    /// movement command; the importer now does the same, and the exporter
    /// writes it back so those arcs don't lose their command on save.
    /// </summary>
    public string    LegacyName    { get; set; } = string.Empty;

    /// <summary>
    /// Genie 4's <c>hidden="…"</c> arc flag (1,933 uses in the corpus) — an arc
    /// the map canvas does not draw. Preserved verbatim for round-trip; Genie 5
    /// does not yet act on it.
    /// </summary>
    public string    Hidden        { get; set; } = string.Empty;

    /// <summary>
    /// The raw <c>destination="…"</c> string when it names a node id that is
    /// not present in this zone file. <see cref="DestinationId"/> stays null so
    /// the pathfinder ignores the dangling arc, but the original value is
    /// written back on export instead of being erased — Genie 4 keeps it, and
    /// some are cross-zone stubs an author still needs.
    /// </summary>
    public string    RawDestination { get; set; } = string.Empty;

    /// <summary>
    /// Free-form skill / class / level requirement hint for non-compass arcs
    /// ("climb tall wall", "swim raging river", "go secret door", etc.).
    /// Parsed by <see cref="ExitRequirement"/> into structured form
    /// (min ranks, required class, min level). Expected shapes:
    /// <list type="bullet">
    ///   <item><c>"athletics 50"</c> — legacy free-form</item>
    ///   <item><c>"climbing&gt;=50, athletics&gt;=30"</c> — explicit min</item>
    ///   <item><c>"class=Thief"</c> — guild restriction</item>
    ///   <item><c>"level&gt;=25"</c> — character level gate</item>
    /// </list>
    /// Old Genie 4 clients ignore the round-tripped <c>requires=</c>
    /// attribute. Genie 5 surfaces it as a tooltip on Less Obvious Paths
    /// buttons and uses it for skill-weighted Dijkstra in
    /// <see cref="AutoMapperEngine.FindPath"/>.
    /// </summary>
    public string Requires { get; set; } = string.Empty;

    /// <summary>
    /// Roundtime cost in seconds for taking this exit. Used by the
    /// weighted pathfinder to prefer faster routes when multiple paths
    /// exist. Null = unknown (treated as 0 by the pathfinder).
    /// </summary>
    public int? RtCost { get; set; }

    /// <summary>
    /// Lower bound of expected wait time in seconds. Used for boats and
    /// other scheduled departures: "boards every 5-10 minutes" → 300.
    /// Null = no wait (immediate transit).
    /// </summary>
    public int? WaitMin { get; set; }

    /// <summary>
    /// Upper bound of expected wait time in seconds. Pathfinder averages
    /// <see cref="WaitMin"/> + <see cref="WaitMax"/> when computing edge
    /// weight. Null = same as WaitMin (deterministic wait).
    /// </summary>
    public int? WaitMax { get; set; }

    /// <summary>
    /// Physical aid / transit kind this exit uses — "Bridge", "Boat", "Rope",
    /// "Ladder", "Ford", "Climb", etc. (see
    /// <see cref="ExitEnvironments"/>). Descriptive metadata that groups the
    /// timing fields (<see cref="RtCost"/> / <see cref="WaitMin"/>) under a
    /// recognisable label in the Edit Exit dialog and lets the community
    /// classify how a link is traversed. Empty = an ordinary walked step.
    /// Round-tripped as the <c>env</c> attribute; old Genie 4 clients ignore it.
    /// </summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Free-form notes from the community Maps repo: prerequisites the
    /// pathfinder can't model ("rope needed", "only at night", etc.).
    /// Surfaces as a tooltip on the Less Obvious Paths button.
    /// </summary>
    public string Notes { get; set; } = string.Empty;
}
