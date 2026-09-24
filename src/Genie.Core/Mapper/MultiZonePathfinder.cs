using Genie.Core.Skills;

namespace Genie.Core.Mapper;

/// <summary>
/// One step in a multi-zone walk plan. Either an intra-zone move (just
/// the verb, e.g. "north", "climb wall") OR a cross-zone connection
/// (verb plus a wait window the walker honours). The walker treats
/// both as "send the verb, wait for room change," but cross-zone
/// steps surface a wait-time hint to the user.
/// </summary>
public sealed record WalkStep
{
    public required string Verb           { get; init; }
    public bool   IsCrossZone             { get; init; }
    public int?   ExpectedWaitMinSeconds  { get; init; }
    public int?   ExpectedWaitMaxSeconds  { get; init; }
    public string TargetZone              { get; init; } = string.Empty;
    public string Description             { get; init; } = string.Empty;

    public override string ToString()
        => IsCrossZone ? $"[→ {TargetZone}] {Verb}" : Verb;
}

/// <summary>
/// Result of a multi-zone pathfind. Carries the ordered plan + a flag
/// indicating whether any cross-zone hop was used (the walker uses
/// this to decide whether to surface boat-wait UI).
/// </summary>
public sealed record MultiZonePath(
    IReadOnlyList<WalkStep> Steps,
    bool HasCrossZoneHop);

/// <summary>
/// Dijkstra over a meta-graph of (zoneFile, roomId) tuples. Loads
/// zones lazily as the search reaches them. Edges come from two
/// sources:
/// <list type="bullet">
///   <item>Intra-zone <see cref="MapExit"/>s inside each loaded
///   <see cref="MapZone"/></item>
///   <item>Cross-zone <see cref="ZoneConnection"/>s from a
///   <see cref="ZoneConnectionsRepository"/></item>
/// </list>
/// Both honour <see cref="ExitRequirement"/> against the character's
/// live <see cref="SkillStore"/> / class / level. Edges with unmet
/// requirements are excluded from pathfinding entirely.
/// </summary>
public sealed class MultiZonePathfinder
{
    private readonly MapZoneRepository    _zoneRepo;
    private readonly IReadOnlyList<ZoneConnection> _connections;
    private readonly string               _mapsDirectory;
    private readonly SkillStore?          _skills;
    private readonly string?              _characterClass;
    private readonly int                  _characterLevel;
    private readonly int                  _athleticsRank;   // cached for swim/climb cost scaling

    public MultiZonePathfinder(
        MapZoneRepository zoneRepo,
        string mapsDirectory,
        IReadOnlyList<ZoneConnection> connections,
        SkillStore? skills,
        string? characterClass,
        int characterLevel)
    {
        _zoneRepo       = zoneRepo;
        _mapsDirectory  = mapsDirectory;
        _connections    = connections;
        _skills         = skills;
        _characterClass = characterClass;
        _characterLevel = characterLevel;
        _athleticsRank  = skills?.Rank("Athletics") ?? 0;
    }

    /// <summary>
    /// Find a walkable path from (<paramref name="startZone"/>,
    /// <paramref name="startRoom"/>) to (<paramref name="destZone"/>,
    /// <paramref name="destRoom"/>). Returns null if unreachable.
    /// <para>
    /// When start and destination are in the same zone AND no cross-
    /// zone hop offers a shorter route, the result has
    /// <see cref="MultiZonePath.HasCrossZoneHop"/> = false and the
    /// pathfinder behaves identically to the single-zone Dijkstra in
    /// <see cref="AutoMapperEngine.FindPath"/>.
    /// </para>
    /// </summary>
    public MultiZonePath? FindPath(
        string startZone, string startRoom,
        string destZone,  string destRoom)
    {
        // Lazy-loaded cache so each zone is read at most once per
        // pathfinding run.
        var loadedZones = new Dictionary<string, MapZone>(StringComparer.OrdinalIgnoreCase);

        // Index connections by from-zone for O(1) outgoing-edge lookup.
        var connsByZone = new Dictionary<string, List<ZoneConnection>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _connections)
        {
            if (!connsByZone.TryGetValue(c.FromZone, out var list))
                connsByZone[c.FromZone] = list = new List<ZoneConnection>();
            list.Add(c);
        }

        var startKey = (startZone, startRoom);
        var destKey  = (destZone,  destRoom);

        var distances    = new Dictionary<(string, string), int> { [startKey] = 0 };
        var cameFromKey  = new Dictionary<(string, string), (string, string)>();
        var cameFromStep = new Dictionary<(string, string), WalkStep>();
        var queue        = new PriorityQueue<(string zone, string room), int>();
        queue.Enqueue(startKey, 0);

        while (queue.TryDequeue(out var current, out var currentDist))
        {
            if (currentDist > distances[current]) continue;
            if (current == destKey) break;

            // Lazy-load the current zone so we can read its node + exits.
            if (!TryLoadZone(current.zone, loadedZones, out var zone)) continue;
            if (!TryMatchRoom(zone, current.room, out var node)) continue;

            // ── Intra-zone edges ────────────────────────────────────
            foreach (var exit in node.Exits)
            {
                if (!exit.DestinationId.HasValue) continue;
                if (!zone.Nodes.TryGetValue(exit.DestinationId.Value, out var nextNode)) continue;
                // Cross-zone walks always run on the built-in walker, which can't
                // execute a `script <name>` arc (#253) — never route through one.
                if (MoveVerb.IsScriptMove(exit.MoveCommand)) continue;

                var req = ExitRequirement.Parse(exit.Requires);
                if (!req.IsMet(_skills, _characterClass, _characterLevel)) continue;

                // Same skill-scaled cost the single-zone pathfinder uses, so an
                // edge weighs the same whether the route stays in-zone or crosses.
                var weight = AutoMapperEngine.IntraZoneEdgeCost(exit, _athleticsRank);
                var nextKey = (current.zone, nextNode.Id.ToString());

                if (!distances.TryGetValue(nextKey, out var existingDist) ||
                    currentDist + weight < existingDist)
                {
                    distances[nextKey]    = currentDist + weight;
                    cameFromKey[nextKey]  = current;
                    cameFromStep[nextKey] = new WalkStep
                    {
                        Verb = string.IsNullOrEmpty(exit.MoveCommand)
                            ? exit.Direction.ToString().ToLowerInvariant()
                            : exit.MoveCommand,
                    };
                    queue.Enqueue(nextKey, currentDist + weight);
                }
            }

            // ── Cross-zone edges out of this room ──────────────────
            if (connsByZone.TryGetValue(current.zone, out var outgoing))
            {
                foreach (var conn in outgoing)
                {
                    if (!string.Equals(conn.FromRoom, current.room, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (MoveVerb.IsScriptMove(conn.Verb)) continue;   // #253, as above

                    var req = ExitRequirement.Parse(conn.Requires);
                    if (!req.IsMet(_skills, _characterClass, _characterLevel)) continue;

                    // Cross-zone weight = 1 baseline + RT/4 + averageWait/4, plus
                    // skill-scaled effort when the transit verb is a swim/climb
                    // (a "swim the Faldesu" link is near-free for a skilled
                    // character, but a ferry's wait keeps it costly — #122).
                    // Wait time dwarfs the room cost so boats with long schedules
                    // are correctly preferred only when no overland path exists.
                    var avgWait = ((conn.WaitMin ?? 0) + (conn.WaitMax ?? conn.WaitMin ?? 0)) / 2;
                    var weight  = 1 + (conn.RtCost ?? 0) / 4 + avgWait / 4
                                    + AutoMapperEngine.EffortPenalty(conn.Verb, _athleticsRank);

                    var nextKey = (conn.ToZone, conn.ToRoom);

                    if (!distances.TryGetValue(nextKey, out var existingDist) ||
                        currentDist + weight < existingDist)
                    {
                        distances[nextKey]    = currentDist + weight;
                        cameFromKey[nextKey]  = current;
                        cameFromStep[nextKey] = new WalkStep
                        {
                            Verb                   = conn.Verb,
                            IsCrossZone            = true,
                            ExpectedWaitMinSeconds = conn.WaitMin,
                            ExpectedWaitMaxSeconds = conn.WaitMax,
                            TargetZone             = conn.ToZone,
                            Description            = string.IsNullOrEmpty(conn.TransitType)
                                ? conn.Verb
                                : $"{conn.TransitType}: {conn.Verb}",
                        };
                        queue.Enqueue(nextKey, currentDist + weight);
                    }
                }
            }
        }

        if (!distances.ContainsKey(destKey)) return null;

        // Reconstruct the plan by walking the cameFrom chain backwards.
        var steps         = new List<WalkStep>();
        var hasCrossZone  = false;
        var cursor        = destKey;
        while (cursor != startKey)
        {
            if (!cameFromStep.TryGetValue(cursor, out var step)) return null;
            steps.Add(step);
            if (step.IsCrossZone) hasCrossZone = true;
            if (!cameFromKey.TryGetValue(cursor, out var prev)) return null;
            cursor = prev;
        }
        steps.Reverse();
        return new MultiZonePath(steps, hasCrossZone);
    }

    /// <summary>Lazy-load a zone by file basename. Returns false on missing/bad XML.</summary>
    private bool TryLoadZone(string zoneFileBaseName, Dictionary<string, MapZone> cache, out MapZone zone)
    {
        if (cache.TryGetValue(zoneFileBaseName, out zone!)) return true;
        var path = Path.Combine(_mapsDirectory, zoneFileBaseName + ".xml");
        var loaded = _zoneRepo.Load(path);
        if (loaded is null) { zone = null!; return false; }
        cache[zoneFileBaseName] = loaded;
        zone = loaded;
        return true;
    }

    /// <summary>
    /// Resolve a room reference (either node-id or DR server-room-id) to
    /// a <see cref="MapNode"/> in the given zone. ServerRoomId match is
    /// preferred (more stable across map regenerations).
    /// </summary>
    private bool TryMatchRoom(MapZone zone, string roomRef, out MapNode node)
    {
        // ServerRoomId path (string starting with '#' or matching ServerRoomId text)
        var serverId = roomRef.StartsWith("#") ? roomRef.Substring(1) : roomRef;
        foreach (var n in zone.Nodes.Values)
        {
            if (string.Equals(n.ServerRoomId, serverId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(n.ServerRoomId, roomRef, StringComparison.OrdinalIgnoreCase))
            {
                node = n;
                return true;
            }
        }

        // Node-id path
        if (int.TryParse(roomRef, out var nodeId) && zone.Nodes.TryGetValue(nodeId, out node!))
            return true;

        node = null!;
        return false;
    }
}
