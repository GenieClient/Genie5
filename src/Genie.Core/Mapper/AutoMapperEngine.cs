using Genie.Core.Mapper;
using Genie.Core.Skills;

namespace Genie.Core.Mapper;

public sealed class AutoMapperEngine
{
    private MapZone _zone;
    private IMapperGameState? _state;

    /// <summary>
    /// Optional skill-rank source for the weighted pathfinder. Set by
    /// <see cref="GenieCore"/> at startup. When null, all skill-gated
    /// exits pass (FindPath behaves identically to the legacy BFS).
    /// </summary>
    public SkillStore? Skills { get; set; }

    /// <summary>
    /// Optional character class for class-restricted exits (e.g. "Thief").
    /// Set by <see cref="GenieCore"/> from <c>GameState.Guild</c>. Null
    /// means "no class info yet" — passes all class checks.
    /// </summary>
    public string? CharacterClass { get; set; }

    /// <summary>
    /// Optional character level/circle for level-gated exits. 0 = unknown
    /// (passes all level checks).
    /// </summary>
    public int CharacterLevel { get; set; }

    // Fingerprint → [NodeId, ...] for fast room matching. The list catches
    // the genuinely common case in dense city zones where multiple rooms
    // share the same title AND the same compass exit set (e.g. several
    // "Old Throne City, Mir'Aevar Jegu" nodes that all expose northeast +
    // southwest). When the list has >1 entries, the engine disambiguates
    // using prevNode + usedDir (graph adjacency) or description match.
    private readonly Dictionary<string, List<int>> _fingerprintIndex = new();

    // ServerRoomId → NodeId for exact room matching via <nav rm="..."/>
    private readonly Dictionary<string, int> _serverRoomIndex = new(StringComparer.OrdinalIgnoreCase);

    // State between room transitions
    private string  _lastTitle = string.Empty;
    private string  _lastExitKey = string.Empty; // sorted join used for change detection
    private string  _lastDescription = string.Empty; // tracks desc so late-arriving descriptions retry the match

    // The movement command that was sent before the last room change fired.
    // _pendingDirection is set when the player typed a compass primitive
    // ("ne", "northwest", "up", "out"…); _pendingMoveCommand carries the raw
    // string for non-compass arcs like "go small alleyway" or "climb trellis"
    // so the graph-walk tier can still find the right destination arc by
    // matching MapExit.MoveCommand.
    private Direction _pendingDirection   = Direction.None;
    private string    _pendingMoveCommand = string.Empty;

    public MapNode?  CurrentNode  { get; private set; }
    public MapZone   ActiveZone   => _zone;
    public bool      IsEnabled    { get; set; } = false;

    /// <summary>
    /// Genie 4 "Allow Duplicate" parity. When true AND <see cref="IsEnabled"/>
    /// (record mode), the fuzzy fingerprint/title match tiers (c)/(e)/(f) are
    /// skipped so a freshly-entered room that merely shares a title+exit
    /// fingerprint with an existing node gets its OWN node instead of being
    /// folded into the existing one. Definitive tiers — server room id (a) and
    /// graph-arc walk (b)/(d) — still match, so genuinely revisiting a known
    /// room via a linked exit does not spawn a duplicate. Off by default
    /// (de-duplication is the right behaviour for almost every zone).
    /// </summary>
    public bool      AllowDuplicateRooms { get; set; } = false;

    public event Action? MapChanged;
    public event Action? CurrentNodeChanged;
    /// <summary>
    /// Raised when the engine can't find the current room in the active zone
    /// (lookup-only mode). The controller should try loading a different zone
    /// that contains the room. The payload carries everything the controller
    /// needs to look up the right zone without having to ask the player to
    /// pick:
    /// <list type="bullet">
    ///   <item><c>serverRoomId</c> — from <c>&lt;nav rm="..."/&gt;</c>;
    ///         the strongest signal but only present once the community has
    ///         populated <c>server_id</c> attributes in the zone XMLs.</item>
    ///   <item><c>title</c> — the room title, used together with exits to
    ///         compute a <see cref="MapFingerprint"/> fallback for old maps
    ///         that don't carry server ids yet.</item>
    ///   <item><c>exits</c> — the visible compass exits at the time the
    ///         match failed. Combined with title via <see cref="MapFingerprint"/>
    ///         this is unique enough to pick the right zone for the vast
    ///         majority of rooms.</item>
    /// </list>
    /// </summary>
    public event Action<string, string, IReadOnlyCollection<string>>? RoomNotFoundInZone;

    public AutoMapperEngine(MapZone zone)
    {
        _zone = zone;
        RebuildIndex();
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public void Attach(IMapperGameState state)
    {
        _state = state;
        state.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Called by the command intercept layer before each command reaches the
    /// server. Tracks the most recently sent movement so the engine's
    /// tier (b) graph walk can resolve where the next room transition lands.
    /// Runs regardless of <see cref="IsEnabled"/> — direction context is just
    /// as important for lookup-only matching as it is for auto-creation
    /// (without it, ambiguous fingerprints can't be disambiguated by
    /// "I just moved NE from this node").
    /// </summary>
    public void OnCommandSent(string rawCommand)
    {
        // Strip leading/trailing whitespace; handle semicolon-separated commands
        // by looking at the first token only.
        var first = rawCommand.Split(';')[0].Trim();
        if (first.Length == 0) return;

        var dir = DirectionHelper.Parse(first);
        if (dir != Direction.None)
        {
            // Compass / cardinal primitive — graph walk matches by Direction.
            _pendingDirection   = dir;
            _pendingMoveCommand = string.Empty;
        }
        else
        {
            // Non-compass movement — "go small alleyway", "climb trellis",
            // "swim river", etc. Stash the raw command so graph walk can
            // match arc.MoveCommand exactly.
            _pendingDirection   = Direction.None;
            _pendingMoveCommand = first;
        }
    }

    public void LoadZone(MapZone zone)
    {
        _zone = zone;
        RebuildIndex();
        CurrentNode = null;
        CurrentNodeChanged?.Invoke();
        MapChanged?.Invoke();
        Recalculate();
    }

    /// <summary>
    /// Force a re-evaluation of the current room from the latest IMapperGameState,
    /// even if title/exits haven't changed since the last check. Used after
    /// loading a zone or when the user issues #goto and CurrentNode is null.
    /// </summary>
    public void Recalculate()
    {
        if (_state is null) return;
        _lastTitle       = string.Empty;
        _lastExitKey     = string.Empty;
        _lastDescription = string.Empty;
        OnStateChanged();
    }

    public MapZone NewZone(string name)
    {
        var zone = new MapZone { Name = name };
        LoadZone(zone);
        return zone;
    }

    // ── Manual editor operations (Genie 4 AutoMapper toolbar parity) ──────────
    // These mutate the active zone directly from the UI editor rather than from
    // the live game stream. Each keeps the fingerprint / server-id indexes
    // consistent (via RebuildIndex) and fires MapChanged so the canvas repaints.

    /// <summary>
    /// Delete a node and scrub every arc that pointed at it (Genie 4
    /// "Remove Selected Nodes/Labels"). If the deleted node was the current
    /// room, CurrentNode is cleared. No-op when the id isn't present.
    /// </summary>
    public bool RemoveNode(int id)
    {
        if (!_zone.Nodes.Remove(id)) return false;

        // Drop any dangling arcs that referenced the removed node so the
        // exported XML stays self-consistent (Genie 4 leaves dead arcs; we
        // prefer a clean graph for the weighted pathfinder).
        foreach (var n in _zone.Nodes.Values)
            n.Exits.RemoveAll(e => e.DestinationId == id);

        if (CurrentNode?.Id == id)
        {
            CurrentNode = null;
            CurrentNodeChanged?.Invoke();
        }

        RebuildIndex();
        MapChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Renumber every node to a dense 1..N sequence in ascending current-id
    /// order, remapping all arc destinations to match (Genie 4 "Reset Map IDs").
    /// Preserves the current-room selection across the renumber. Useful after a
    /// lot of add/remove churn leaves the id space sparse.
    /// </summary>
    public void ResetMapIds()
    {
        var ordered = _zone.Nodes.Values.OrderBy(n => n.Id).ToList();
        var remap   = new Dictionary<int, int>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            remap[ordered[i].Id] = i + 1;

        var rebuilt = new Dictionary<int, MapNode>(ordered.Count);
        foreach (var node in ordered)
        {
            node.Id = remap[node.Id];
            foreach (var exit in node.Exits)
                if (exit.DestinationId is { } dest && remap.TryGetValue(dest, out var newDest))
                    exit.DestinationId = newDest;
            rebuilt[node.Id] = node;
        }

        _zone.Nodes = rebuilt;
        RebuildIndex();
        MapChanged?.Invoke();
        CurrentNodeChanged?.Invoke();   // CurrentNode object is unchanged; its Id moved
    }

    /// <summary>
    /// Signal that node fields affecting the lookup indexes (Title, Exits,
    /// ServerRoomId) were edited in the UI, so the fingerprint / server-id
    /// indexes must be rebuilt and the canvas repainted. Position/Notes/Color
    /// edits don't need this — callers can just bump the render tick.
    /// </summary>
    public void NotifyStructureChanged()
    {
        RebuildIndex();
        MapChanged?.Invoke();
    }

    // ── Internal ────────────────────────────────────────────────────────────

    private void OnStateChanged()
    {
        if (_state is null) return;

        var title       = _state.RoomTitle;
        var exits       = _state.Exits;
        var description = _state.RoomDescription;
        var exitKey     = string.Join(",", exits.Order(StringComparer.OrdinalIgnoreCase));

        // Require at least a title before tracking
        if (string.IsNullOrWhiteSpace(title)) return;

        // Re-process whenever any of title / exits / description changes.
        // Description is included because DR's server can deliver the parts
        // of a room transition (title, compass, description, nav) in different
        // orders — if a previous fire failed to match on title alone and we
        // didn't track desc, a later desc-arrival would be silently skipped
        // even though it carries the disambiguating signal the (e) tiebreaker
        // needs.
        bool titleChanged = title       != _lastTitle;
        bool exitsChanged = exitKey     != _lastExitKey;
        bool descChanged  = description != _lastDescription;
        if (!titleChanged && !exitsChanged && !descChanged) return;

        _lastTitle       = title;
        _lastExitKey     = exitKey;
        _lastDescription = description;

        OnRoomChanged(title, description, exits);
    }

    private void OnRoomChanged(string title, string description, IReadOnlyCollection<string> exits)
    {
        var fingerprint     = MapFingerprint.Compute(title, exits);
        var serverRoomId    = _state?.ServerRoomId ?? string.Empty;
        var prevNode        = CurrentNode;
        var usedDir         = _pendingDirection;
        var usedMoveCommand = _pendingMoveCommand;

        // Always clear pending movement after consuming it.
        _pendingDirection   = Direction.None;
        _pendingMoveCommand = string.Empty;

        bool zoneChanged = false;

        // "Allow Duplicate" gate: when the user has opted into duplicates while
        // recording, skip the fuzzy fingerprint/title tiers (c)/(e)/(f) so a
        // new room sharing a fingerprint creates its own node. Definitive tiers
        // (server-id, graph-arc) below are unaffected.
        bool allowFuzzy = !(IsEnabled && AllowDuplicateRooms);

        // ── 1. Find the node ─────────────────────────────────────────────────
        // Priority:
        //   a) Server room ID (definitive if present)
        //   b) Graph walk: follow linked arc from previous node in the
        //      movement direction and verify the destination title matches
        //   c) Fingerprint index (title + exits)
        //   d) Reverse-arc search: find a node matching the fingerprint that
        //      has a reverse exit linking back to prevNode
        //   e) Description tiebreaker among all nodes with the same title
        //   f) Create new (if mapper enabled) or signal zone miss

        MapNode? node = null;

        // (a) Server room ID — definitive match
        if (!string.IsNullOrEmpty(serverRoomId) &&
            _serverRoomIndex.TryGetValue(serverRoomId, out var srvId) &&
            _zone.Nodes.TryGetValue(srvId, out var srvNode))
        {
            node = srvNode;
        }

        // (b) Graph walk: if we know where we were and which movement command
        //     was used, follow the linked arc and verify the destination
        //     title matches. Two flavours:
        //       - Compass direction ("ne", "northwest") → match arc.Direction
        //       - Non-compass ("go small alleyway", "climb trellis") →
        //         match arc.MoveCommand exactly. Both flavours of move are
        //         common in DR; the importer preserves both pieces of data.
        if (node is null && prevNode != null)
        {
            MapExit? arc = null;
            if (usedDir != Direction.None)
            {
                arc = prevNode.GetExit(usedDir);
            }
            else if (!string.IsNullOrEmpty(usedMoveCommand))
            {
                arc = prevNode.Exits.FirstOrDefault(e =>
                    !string.IsNullOrEmpty(e.MoveCommand) &&
                    e.MoveCommand.Equals(usedMoveCommand, StringComparison.OrdinalIgnoreCase));
            }

            if (arc?.DestinationId is { } destId &&
                _zone.Nodes.TryGetValue(destId, out var arcDest) &&
                arcDest.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
            {
                node = arcDest;
            }
        }

        // (c) Fingerprint index (title + cardinal exits). When the fingerprint
        //     resolves to a single node we use it directly. When multiple nodes
        //     share the fingerprint — the common case in dense cities like
        //     Old Throne City where many rooms have identical titles and exit
        //     sets — we disambiguate:
        //
        //       1. Prefer a candidate reachable from prevNode via usedDir
        //          (graph adjacency — strongest signal when the player just
        //          moved in a known direction).
        //       2. Otherwise prefer a candidate whose description matches the
        //          live room description.
        //       3. If neither narrows it down, skip tier (c) entirely and let
        //          tiers (d)/(e)/(f) take over. Picking the first candidate
        //          would lock in a wrong match that then cascades — once
        //          CurrentNode is wrong, every subsequent move's tier (b)
        //          graph walk runs from the wrong room and stays wrong.
        if (node is null && allowFuzzy && _fingerprintIndex.TryGetValue(fingerprint, out var candidateIds))
        {
            if (candidateIds.Count == 1)
            {
                _zone.Nodes.TryGetValue(candidateIds[0], out node);
            }
            else
            {
                // Disambiguation 1: graph adjacency from prevNode. Try the
                // compass arc first; fall back to MoveCommand matching for
                // "go X" / "climb X" non-compass arcs.
                if (prevNode != null)
                {
                    MapExit? fwd = null;
                    if (usedDir != Direction.None)
                        fwd = prevNode.GetExit(usedDir);
                    else if (!string.IsNullOrEmpty(usedMoveCommand))
                        fwd = prevNode.Exits.FirstOrDefault(e =>
                            !string.IsNullOrEmpty(e.MoveCommand) &&
                            e.MoveCommand.Equals(usedMoveCommand, StringComparison.OrdinalIgnoreCase));

                    if (fwd?.DestinationId is { } destId && candidateIds.Contains(destId)
                        && _zone.Nodes.TryGetValue(destId, out var graphHit))
                    {
                        node = graphHit;
                    }
                }

                // Disambiguation 2: description match (first 80 chars)
                if (node is null && !string.IsNullOrEmpty(description))
                {
                    var descA = description.Length > 80 ? description[..80] : description;
                    foreach (var id in candidateIds)
                    {
                        if (!_zone.Nodes.TryGetValue(id, out var cand)) continue;
                        if (string.IsNullOrEmpty(cand.Description)) continue;
                        var descB = cand.Description.Length > 80 ? cand.Description[..80] : cand.Description;
                        if (descA.Equals(descB, StringComparison.OrdinalIgnoreCase))
                        { node = cand; break; }
                    }
                }
                // If still ambiguous, fall through — never blindly pick the
                // first candidate. Tiers (d)/(e)/(f) try other heuristics.
            }
        }

        // (d) Reverse-arc search: among all nodes with matching title, find one
        //     that has a reverse exit linking back to prevNode.
        if (node is null && prevNode != null && usedDir != Direction.None &&
            DirectionHelper.Opposite.TryGetValue(usedDir, out var oppDir))
        {
            foreach (var candidate in _zone.Nodes.Values)
            {
                if (!candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                    continue;
                var backArc = candidate.GetExit(oppDir);
                if (backArc?.DestinationId == prevNode.Id)
                { node = candidate; break; }
            }
        }

        // (e) Description tiebreaker: scan all nodes with matching title and
        //     pick the one whose description matches (handles duplicate titles).
        if (node is null && allowFuzzy && !string.IsNullOrEmpty(description))
        {
            foreach (var candidate in _zone.Nodes.Values)
            {
                if (!candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(candidate.Description)) continue;
                // Compare the first 80 chars of description (handles minor
                // trailing variations like player names in room text).
                var descA = description.Length > 80 ? description[..80] : description;
                var descB = candidate.Description.Length > 80
                    ? candidate.Description[..80] : candidate.Description;
                if (descA.Equals(descB, StringComparison.OrdinalIgnoreCase))
                { node = candidate; break; }
            }
        }

        // (f) Title + exit-set OVERLAP — at least one compass exit in common.
        //     Catches the common case where the XML lists more exits than the
        //     live "Obvious paths" line (e.g., the XML has a `climb` non-
        //     compass exit that the importer can't categorize, or doors are
        //     currently closed in-game so the visible exit set is a subset
        //     of the persisted node's exits). Without this fallback the
        //     fingerprint check (c) would miss every such room.
        if (node is null && allowFuzzy && !string.IsNullOrEmpty(title))
        {
            var liveDirs = exits
                .Select(DirectionHelper.Parse)
                .Where(d => d != Direction.None)
                .ToHashSet();

            foreach (var candidate in _zone.Nodes.Values)
            {
                if (!candidate.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                    continue;
                var nodeDirs = candidate.Exits
                    .Select(e => e.Direction)
                    .Where(d => d != Direction.None)
                    .ToHashSet();
                // Any exit overlap is good enough — picks the first such
                // candidate. Multiple matches are rare in practice; when they
                // happen the graph-walk (b) and server-id (a) tiers above will
                // disambiguate on subsequent moves.
                if (liveDirs.Count == 0 || nodeDirs.Overlaps(liveDirs))
                {
                    node = candidate;
                    break;
                }
            }
        }

        if (node != null)
        {
            // Match found — apply read-write enrichments only when the user
            // has opted into "Auto-create new rooms as I explore". In default
            // lookup-only mode (IsEnabled=false) the imported community map is
            // treated as read-only: a wrong match must not be allowed to
            // silently mutate the right node's data. Once the user runs through
            // a session with a few mismatches under auto-create, the map's
            // on-disk view diverges from upstream — bad for git diffs and bad
            // for accuracy on the next session.
            if (IsEnabled)
            {
                // Update description if it arrives after first visit
                if (string.IsNullOrEmpty(node.Description) && !string.IsNullOrEmpty(description))
                {
                    node.Description = description;
                    zoneChanged = true;
                }
                // Stamp server room ID on nodes that don't have it yet
                if (!string.IsNullOrEmpty(serverRoomId) && string.IsNullOrEmpty(node.ServerRoomId))
                {
                    node.ServerRoomId = serverRoomId;
                    _serverRoomIndex.TryAdd(serverRoomId, node.Id);
                    zoneChanged = true;
                }
            }
        }
        else if (!IsEnabled)
        {
            // Lookup-only mode and no match in the active zone. Signal the
            // controller so it can try loading a zone that contains this
            // room. We used to gate this on `exits.Count > 0` ("wait for
            // the fingerprint to be complete"), but the controller's
            // index-scan already weighs server-room-id and title+desc
            // matches above title-only ones, and gating here just
            // delayed zone transitions until the next move. Fire on
            // every title change instead — better to attempt the
            // lookup once with a partial fingerprint than to leave the
            // player visibly stranded in the wrong zone.
            CurrentNode = null;
            CurrentNodeChanged?.Invoke();
            RoomNotFoundInZone?.Invoke(serverRoomId, title, exits);
            return;
        }
        else
        {
            // New room — create node and assign coordinates
            node = new MapNode
            {
                Id            = NextNodeId(),
                Title         = title,
                Description   = description,
                ServerRoomId  = serverRoomId,
            };

            AssignCoordinates(node, prevNode, usedDir);
            _zone.Nodes[node.Id] = node;

            // Index by fingerprint as a list — same shape as RebuildIndex.
            if (!_fingerprintIndex.TryGetValue(fingerprint, out var fpList))
            {
                fpList = new List<int>();
                _fingerprintIndex[fingerprint] = fpList;
            }
            fpList.Add(node.Id);

            if (!string.IsNullOrEmpty(serverRoomId))
                _serverRoomIndex.TryAdd(serverRoomId, node.Id);
            zoneChanged = true;
        }

        // ── 2. Link exits between prevNode and node ──────────────────────────
        // Read-write — gated on IsEnabled (auto-create mode). In lookup-only
        // mode the existing arc graph is treated as authoritative; we don't
        // create new arcs or change destinations just because a single
        // (potentially wrong) match said so.
        if (IsEnabled && prevNode != null && usedDir != Direction.None && prevNode.Id != node.Id)
        {
            // Forward exit: prevNode → node
            var fwd = prevNode.GetOrAddExit(usedDir, usedDir.ToString().ToLowerInvariant());
            if (fwd.DestinationId != node.Id)
            {
                fwd.DestinationId = node.Id;
                zoneChanged = true;
            }

            // Back exit: node → prevNode
            if (DirectionHelper.Opposite.TryGetValue(usedDir, out var opp) && opp != Direction.None)
            {
                var back = node.GetOrAddExit(opp, opp.ToString().ToLowerInvariant());
                if (back.DestinationId != prevNode.Id)
                {
                    back.DestinationId = prevNode.Id;
                    zoneChanged = true;
                }
            }
        }

        // ── 3. Ensure all compass exits have stub entries on the node ────────
        // Read-write — gated on IsEnabled. This was THE source of the
        // "ghost exits accumulating on the wrong node" bug: a wrong match
        // for room A would silently add A's exits onto whatever node we
        // mismatched into. Over a session, ambiguous-title nodes would
        // collect a union of all live exits from every room that hit them.
        if (IsEnabled)
        {
            foreach (var exitStr in exits)
            {
                var dir = DirectionHelper.Parse(exitStr);
                if (dir == Direction.None) continue;
                if (node.GetExit(dir) is null)
                {
                    node.Exits.Add(new MapExit { Direction = dir, MoveCommand = exitStr });
                    zoneChanged = true;
                }
            }
        }

        // ── 4. Update current node and fire events ───────────────────────────
        CurrentNode = node;
        CurrentNodeChanged?.Invoke();
        if (zoneChanged) MapChanged?.Invoke();
    }

    private static void AssignCoordinates(MapNode node, MapNode? prev, Direction dir)
    {
        if (prev is null || dir == Direction.None ||
            !DirectionHelper.Delta.TryGetValue(dir, out var delta))
        {
            // No context — leave at origin; caller can reposition later
            node.X = 0;
            node.Y = 0;
            node.Z = 0;
            return;
        }

        node.X = prev.X + delta.dx;
        node.Y = prev.Y + delta.dy;
        node.Z = prev.Z + delta.dz;
    }

    /// <summary>
    /// Weighted Dijkstra from <paramref name="start"/> to <paramref name="destination"/>
    /// through linked exits. Skill-gated edges (Requires not satisfied by
    /// the live <see cref="Skills"/> store) are excluded. Returns the
    /// ordered move commands to walk the path, or null if no walkable
    /// path exists with the character's current skills / class / level.
    /// <para>
    /// Weight model (per docs/AUTOMAPPER_DESIGN.md):
    ///   weight(exit) = 1 baseline cost per room
    ///                + wait/4 if the exit has wait times (boats etc.)
    /// </para>
    /// <para>
    /// When <see cref="Skills"/> is null (no character data wired yet)
    /// every exit passes and the behaviour matches the legacy BFS —
    /// Phase 1 of the AutoMapper feature work continues to function
    /// without any per-character state.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? FindPath(MapNode start, MapNode destination)
    {
        if (start.Id == destination.Id) return Array.Empty<string>();

        // Standard Dijkstra: PriorityQueue keyed by accumulated weight.
        // .NET's PriorityQueue is min-heap by the priority argument, which
        // is exactly what we need.
        var distances = new Dictionary<int, int> { [start.Id] = 0 };
        var cameFromNode = new Dictionary<int, int>();
        var cameFromMove = new Dictionary<int, string>();
        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(start.Id, 0);

        while (queue.TryDequeue(out var currentId, out var currentDist))
        {
            // Skip stale entries — we may push the same node multiple times
            // if a shorter path is found later.
            if (currentDist > distances[currentId]) continue;
            if (currentId == destination.Id) break;
            if (!_zone.Nodes.TryGetValue(currentId, out var current)) continue;

            foreach (var exit in current.Exits)
            {
                if (!exit.DestinationId.HasValue) continue;
                var destId = exit.DestinationId.Value;
                if (!_zone.Nodes.TryGetValue(destId, out _)) continue;

                // Check the exit's requirement against the character.
                // ExitRequirement.Empty (or no Requires text) always passes.
                var req = ExitRequirement.Parse(exit.Requires);
                if (!req.IsMet(Skills, CharacterClass, CharacterLevel))
                    continue;  // skill-gated, character can't take this exit

                // Baseline cost = 1 per room. Future: factor in wait times
                // for boats / cross-zone connections (Phase 4 territory).
                int edgeCost = 1;
                int newDist  = currentDist + edgeCost;

                if (!distances.TryGetValue(destId, out var existingDist)
                    || newDist < existingDist)
                {
                    distances[destId]    = newDist;
                    cameFromNode[destId] = currentId;
                    cameFromMove[destId] = string.IsNullOrEmpty(exit.MoveCommand)
                        ? exit.Direction.ToString().ToLowerInvariant()
                        : exit.MoveCommand;
                    queue.Enqueue(destId, newDist);
                }
            }
        }

        // Reconstruct path if we reached the destination.
        if (!distances.ContainsKey(destination.Id)) return null;

        var moves = new List<string>();
        var cursor = destination.Id;
        while (cursor != start.Id)
        {
            if (!cameFromMove.TryGetValue(cursor, out var move)) return null;
            moves.Add(move);
            if (!cameFromNode.TryGetValue(cursor, out var prev)) return null;
            cursor = prev;
        }
        moves.Reverse();
        return moves;
    }

    private int NextNodeId()
    {
        int max = 0;
        foreach (var id in _zone.Nodes.Keys)
            if (id > max) max = id;
        return max + 1;
    }

    private void RebuildIndex()
    {
        _fingerprintIndex.Clear();
        _serverRoomIndex.Clear();
        foreach (var node in _zone.Nodes.Values)
        {
            var fp = MapFingerprint.Compute(node.Title, node.Exits);
            if (!_fingerprintIndex.TryGetValue(fp, out var list))
            {
                list = new List<int>();
                _fingerprintIndex[fp] = list;
            }
            list.Add(node.Id);

            if (!string.IsNullOrEmpty(node.ServerRoomId))
                _serverRoomIndex.TryAdd(node.ServerRoomId, node.Id);
        }
    }
}
