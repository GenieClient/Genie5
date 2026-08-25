using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Regression: the mapper froze while swimming the Segoltha River, so
/// <c>$roomid</c> never advanced and every community swim script
/// (SEGOLTHA_NORTH and friends) looped forever on a stale room id.
///
/// The Segoltha is the worst-case room corridor in the community maps. In
/// <c>Map50_Segoltha_River.xml</c>, ELEVEN nodes — 7, 9, 10, 11, 16, 17, 18,
/// 19, 20, 21 and 32 — share:
/// <list type="bullet">
///   <item>one title, <c>"Segoltha River, Midstream"</c>;</item>
///   <item>one description, <c>"Twisting currents of silt-laden water surge
///         just below the river's surface. …"</c>;</item>
///   <item>one exit set — all eight compass points, with the lateral ones
///         authored as self-loops.</item>
/// </list>
///
/// <see cref="AutoMapperEngine"/> gated its re-resolve on a title / exits /
/// description delta. Across that corridor all three are identical, so
/// <c>swim north</c> produced a zero delta, the guard returned early,
/// <c>OnRoomChanged</c> never ran, and <c>CurrentNode</c> stayed pinned to
/// whichever room the player entered the water at. The server room id — the one
/// field that DOES differ per room — was not part of the test, and neither was
/// "a movement command is outstanding", so both available signals were dropped.
///
/// These tests walk the real node ids and arc shape of the 22→21→…→16→15 run.
/// </summary>
public class SegolthaIdenticalRoomTrackingTests
{
    private const string MidstreamTitle = "Segoltha River, Midstream";

    private const string MidstreamDesc =
        "Twisting currents of silt-laden water surge just below the river's surface.  " +
        "A chill rises from the depths below.";

    private const string SouthBankTitle = "Segoltha River, Near the South Bank";
    private const string SouthBankDesc  = "The current slackens as the river shoulders up against " +
                                          "a low mud bank.";

    private const string BoulderTitle = "Segoltha River, Near the Boulder";
    private const string BoulderDesc  = "Rising majestically above the rushing water, a massive " +
                                        "rock outcropping emerges from the river.";

    /// <summary>The eight-point compass exit set every midstream room shows.</summary>
    private static readonly string[] MidstreamExits =
        { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    /// <summary>Node 22's exits — no southwest, which is what makes it distinguishable.</summary>
    private static readonly string[] SouthBankExits =
        { "north", "northeast", "east", "southeast", "south", "west", "northwest" };

    private static readonly string[] BoulderExits = { "south" };

    /// <summary>The identical-title, identical-description run, south to north.</summary>
    private static readonly int[] MidstreamRun = { 21, 20, 19, 18, 17, 16 };

    /// <summary>
    /// Minimal but faithful slice of Map50: the midstream run 21→20→19→18→17→16
    /// chained north/south with "swim &lt;dir&gt;" move commands, and the lateral
    /// directions authored as self-loops exactly as the community map does.
    /// Node 22 ("Near the South Bank") and node 15 ("Near the Boulder") cap the
    /// two ends — real rooms that the fuzzy tiers *can* tell apart, so a walk can
    /// enter the corridor from a legitimately-resolved position.
    /// </summary>
    private static MapZone BuildSegolthaMidstream()
    {
        var zone = new MapZone { Name = "Segoltha River", Genie4Id = "50" };

        zone.Nodes[22] = new MapNode { Id = 22, Title = SouthBankTitle, Description = SouthBankDesc };
        zone.Nodes[15] = new MapNode { Id = 15, Title = BoulderTitle,   Description = BoulderDesc };

        foreach (var id in MidstreamRun)
            zone.Nodes[id] = new MapNode { Id = id, Title = MidstreamTitle, Description = MidstreamDesc };

        // South cap: 22 → 21 north, and the rest of its authored exits.
        zone.Nodes[22].Exits.Add(new MapExit
        {
            Direction = Direction.North, MoveCommand = "swim north", DestinationId = 21,
        });
        foreach (var (dir, cmd) in new[]
        {
            (Direction.NorthEast, "swim northeast"), (Direction.East,      "swim east"),
            (Direction.SouthEast, "swim southeast"), (Direction.South,     "swim south"),
            (Direction.West,      "swim west"),      (Direction.NorthWest, "swim northwest"),
        })
        {
            zone.Nodes[22].Exits.Add(new MapExit { Direction = dir, MoveCommand = cmd, DestinationId = 22 });
        }

        // The corridor itself.
        for (int i = 0; i < MidstreamRun.Length; i++)
        {
            var node    = zone.Nodes[MidstreamRun[i]];
            var northId = i + 1 < MidstreamRun.Length ? MidstreamRun[i + 1] : 15;
            var southId = i > 0 ? MidstreamRun[i - 1] : 22;

            node.Exits.Add(new MapExit
            {
                Direction = Direction.North, MoveCommand = "swim north", DestinationId = northId,
            });
            node.Exits.Add(new MapExit
            {
                Direction = Direction.South, MoveCommand = "swim south", DestinationId = southId,
            });

            // Lateral self-loops, as authored in Map50. These are what make the
            // exit fingerprint identical across the whole corridor.
            foreach (var (dir, cmd) in new[]
            {
                (Direction.NorthEast, "swim northeast"), (Direction.East,      "swim east"),
                (Direction.SouthEast, "swim southeast"), (Direction.SouthWest, "swim southwest"),
                (Direction.West,      "swim west"),      (Direction.NorthWest, "swim northwest"),
            })
            {
                node.Exits.Add(new MapExit { Direction = dir, MoveCommand = cmd, DestinationId = node.Id });
            }
        }

        // North cap.
        zone.Nodes[15].Exits.Add(new MapExit
        {
            Direction = Direction.South, MoveCommand = "swim south", DestinationId = 16,
        });

        return zone;
    }

    /// <summary>
    /// Hand-driven <see cref="IMapperGameState"/>. <see cref="EnterRoom"/> models
    /// one server turn: the room block lands, then the prompt flushes it — which
    /// is exactly when <see cref="MapperGameStateAdapter"/> raises StateChanged.
    /// </summary>
    private sealed class FakeMapperState : IMapperGameState
    {
        public string RoomTitle       { get; private set; } = string.Empty;
        public string RoomDescription { get; private set; } = string.Empty;
        public string ServerRoomId    { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> Exits { get; private set; } = Array.Empty<string>();

        public event Action? StateChanged;

        public void EnterRoom(string title, string description,
                              IReadOnlyCollection<string> exits, string serverRoomId = "")
        {
            RoomTitle       = title;
            RoomDescription = description;
            Exits           = exits;
            ServerRoomId    = serverRoomId;
            StateChanged?.Invoke();
        }

        /// <summary>Re-enter the midstream room block — every one looks like this.</summary>
        public void EnterMidstream(string serverRoomId = "")
            => EnterRoom(MidstreamTitle, MidstreamDesc, MidstreamExits, serverRoomId);
    }

    /// <summary>
    /// Seed a walk at node 22 — a room with a title unique in the zone, so the
    /// ordinary fingerprint tier places it with no movement context needed.
    /// </summary>
    private static (AutoMapperEngine engine, FakeMapperState state) StartAtSouthBank(
        MapZone zone, string serverRoomId = "")
    {
        var engine = new AutoMapperEngine(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);

        state.EnterRoom(SouthBankTitle, SouthBankDesc, SouthBankExits, serverRoomId);
        Assert.Equal(22, engine.CurrentNode?.Id);

        return (engine, state);
    }

    /// <summary>
    /// Sanity check on the fixture itself: if these rooms were actually
    /// distinguishable by title / description / exits, the rest of this file
    /// would be testing nothing.
    /// </summary>
    [Fact]
    public void Midstream_rooms_are_indistinguishable_by_title_desc_and_exits()
    {
        var zone      = BuildSegolthaMidstream();
        var midstream = zone.Nodes.Values.Where(n => n.Title == MidstreamTitle).ToList();

        Assert.Equal(MidstreamRun.Length, midstream.Count);
        Assert.Single(midstream.Select(n => n.Title).Distinct());
        Assert.Single(midstream.Select(n => n.Description).Distinct());
        Assert.Single(midstream.Select(n => MapFingerprint.Compute(n.Title, n.Exits)).Distinct());
    }

    /// <summary>
    /// Room numbers ON. The server id is the only per-room delta, so the engine
    /// must treat a change in it as a room change. This is the case that used to
    /// fail even though the server had told us exactly where we were.
    /// </summary>
    [Fact]
    public void Swimming_north_advances_current_node_when_server_room_ids_are_present()
    {
        var (engine, state) = StartAtSouthBank(BuildSegolthaMidstream(), "10022");

        var serverIds = new Dictionary<int, string>
        {
            [21] = "10021", [20] = "10020", [19] = "10019",
            [18] = "10018", [17] = "10017", [16] = "10016",
        };

        foreach (var expected in MidstreamRun)
        {
            engine.OnCommandSent("swim north");
            state.EnterMidstream(serverIds[expected]);

            Assert.NotNull(engine.CurrentNode);
            Assert.Equal(expected, engine.CurrentNode!.Id);
        }

        // And out the north end onto a room that IS distinguishable.
        engine.OnCommandSent("swim north");
        state.EnterRoom(BoulderTitle, BoulderDesc, BoulderExits, "10015");
        Assert.Equal(15, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// Room numbers OFF — the common default. Nothing observable differs between
    /// the rooms at all, so the engine has to lean on "we sent a real movement
    /// command and the server answered with a room block", then resolve via the
    /// tier (b) arc walk.
    /// </summary>
    [Fact]
    public void Swimming_north_advances_current_node_without_server_room_ids()
    {
        var (engine, state) = StartAtSouthBank(BuildSegolthaMidstream());

        foreach (var expected in MidstreamRun)
        {
            engine.OnCommandSent("swim north");
            state.EnterMidstream();

            Assert.NotNull(engine.CurrentNode);
            Assert.Equal(expected, engine.CurrentNode!.Id);
        }
    }

    /// <summary>
    /// A bare compass primitive is movement too — Genie's own walker sends
    /// "north", not "swim north", when it is walking a plain arc.
    /// </summary>
    [Fact]
    public void Bare_compass_command_also_advances_through_identical_rooms()
    {
        var (engine, state) = StartAtSouthBank(BuildSegolthaMidstream());

        engine.OnCommandSent("north");
        state.EnterMidstream();
        Assert.Equal(21, engine.CurrentNode!.Id);

        engine.OnCommandSent("n");
        state.EnterMidstream();
        Assert.Equal(20, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// The other half of the movement gate. GenieCore hands EVERY outbound
    /// command to OnCommandSent (GenieCore.cs:1088), and <c>look</c> re-sends the
    /// whole room block — so without verifying the pending command against the
    /// arc graph, a look would read as a move and drift the player up the river.
    /// </summary>
    [Fact]
    public void Non_movement_commands_do_not_drift_through_an_identical_corridor()
    {
        var (engine, state) = StartAtSouthBank(BuildSegolthaMidstream());

        engine.OnCommandSent("swim north");
        state.EnterMidstream();
        Assert.Equal(21, engine.CurrentNode!.Id);

        foreach (var cmd in new[] { "look", "appraise river", "glance", "look" })
        {
            engine.OnCommandSent(cmd);
            state.EnterMidstream();
            Assert.Equal(21, engine.CurrentNode!.Id);
        }

        // A real move still works after all that noise.
        engine.OnCommandSent("swim north");
        state.EnterMidstream();
        Assert.Equal(20, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// A server id learned from a graph-grounded match must be reusable, so that
    /// re-entering a known room with no movement context still places the player.
    /// The learn is in-memory only — the imported community map is read-only in
    /// the default lookup-only mode and must come out untouched.
    /// </summary>
    [Fact]
    public void Server_ids_learned_by_graph_walk_are_reusable_without_dirtying_the_map()
    {
        var zone = BuildSegolthaMidstream();
        var (engine, state) = StartAtSouthBank(zone, "10022");

        Assert.False(engine.IsEnabled); // lookup-only: the default

        foreach (var (expected, id) in new[] { (21, "10021"), (20, "10020"), (19, "10019"), (18, "10018") })
        {
            engine.OnCommandSent("swim north");
            state.EnterMidstream(id);
            Assert.Equal(expected, engine.CurrentNode!.Id);
        }

        // The on-disk view must be untouched — no node picked up a ServerRoomId.
        Assert.All(zone.Nodes.Values, n => Assert.Equal(string.Empty, n.ServerRoomId));

        // Re-enter 20 by its learned id with NO movement context. Only the
        // tier (a) index can resolve this: the fingerprint is shared by all six
        // corridor rooms and the descriptions are identical, so the fuzzy tiers
        // would fall through to the first candidate (21), not 20.
        state.EnterMidstream("10020");
        Assert.Equal(20, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// The movement gate is verb-based, not arc-based. This is the case that
    /// broke it live: a map that authors the river arcs as plain
    /// <c>move="north"</c> while the script sends <c>swim north</c>. An
    /// arc-match gate finds nothing and freezes; verb classification does not
    /// care how the community authored the arc.
    /// </summary>
    [Fact]
    public void Swim_command_still_counts_as_movement_when_arcs_are_authored_bare()
    {
        var zone = BuildSegolthaMidstream();

        // Re-author every arc the way a different community map might: bare
        // compass move commands, no "swim" anywhere in the data.
        foreach (var node in zone.Nodes.Values)
            foreach (var exit in node.Exits)
                exit.MoveCommand = exit.Direction.ToString().ToLowerInvariant();

        var (engine, state) = StartAtSouthBank(zone);

        foreach (var expected in MidstreamRun)
        {
            engine.OnCommandSent("swim north");   // player/script sends this…
            state.EnterMidstream();               // …map says just "north"
            Assert.Equal(expected, engine.CurrentNode!.Id);
        }
    }

    /// <summary>
    /// Movement-verb classification: the whole point is telling a move apart
    /// from a command that merely redraws the room.
    /// </summary>
    [Theory]
    [InlineData("north", true)]
    [InlineData("n", true)]
    [InlineData("swim north", true)]
    [InlineData("go shore", true)]
    [InlineData("climb bank", true)]
    [InlineData("dive river", true)]
    [InlineData("out", true)]
    [InlineData("rt north", true)]          // pacing prefix stripped
    [InlineData("search go trampled path", true)]
    [InlineData("look", false)]
    [InlineData("glance", false)]
    [InlineData("appraise river", false)]
    [InlineData("search bushes", false)]    // literal search, not a directive
    [InlineData("attack orc", false)]
    [InlineData("", false)]
    public void Movement_verbs_are_classified_correctly(string command, bool isMove)
        => Assert.Equal(isMove, MoveVerb.IsMovementCommand(command));

    /// <summary>
    /// The `#config mapperdebug` trace has to name the tier that placed the
    /// player and flag a suppressed turn — that is the whole point of it, and
    /// it is what a live bug report will be read from.
    /// </summary>
    [Fact]
    public void Diagnostics_trace_reports_the_matching_tier_and_suppression()
    {
        var lines  = new List<string>();
        var engine = new AutoMapperEngine(BuildSegolthaMidstream()) { Diagnostics = lines.Add };
        var state  = new FakeMapperState();
        engine.Attach(state);

        state.EnterRoom(SouthBankTitle, SouthBankDesc, SouthBankExits);
        Assert.Equal(22, engine.CurrentNode!.Id);

        engine.OnCommandSent("swim north");
        state.EnterMidstream();
        Assert.Contains(lines, l => l.Contains("=> #21 via b:graph-walk"));

        // A repeat room block with no move and no delta must be reported as
        // suppressed rather than silently dropped.
        lines.Clear();
        state.EnterMidstream();
        Assert.Contains(lines, l => l.Contains("SUPPRESSED"));
        Assert.Equal(21, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// The fuzzy tiers must NOT teach the server-id index. In a corridor like
    /// this the description tiebreaker picks the first of six identical rooms;
    /// caching that guess would promote a coin-flip to a definitive tier (a) hit
    /// and pin every future visit to the wrong room.
    /// </summary>
    [Fact]
    public void Fuzzy_matches_do_not_teach_the_server_id_index()
    {
        var zone = BuildSegolthaMidstream();
        var engine = new AutoMapperEngine(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);

        // Cold start straight into the ambiguous corridor: no prevNode, no
        // pending move. Whatever this resolves to is a guess.
        state.EnterMidstream("99001");
        var guessed = engine.CurrentNode?.Id;

        // Now walk the corridor properly from the south bank so 21 is reached by
        // a graph-grounded step, and claim the SAME server id for it.
        state.EnterRoom(SouthBankTitle, SouthBankDesc, SouthBankExits, "10022");
        Assert.Equal(22, engine.CurrentNode!.Id);

        engine.OnCommandSent("swim north");
        state.EnterMidstream("99001");

        // The graph walk wins: we are in 21, not wherever the cold guess landed
        // (unless the guess happened to be 21 anyway).
        Assert.Equal(21, engine.CurrentNode!.Id);
        if (guessed is { } g && g != 21)
            Assert.NotEqual(g, engine.CurrentNode!.Id);
    }
}
