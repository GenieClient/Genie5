using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Regression: recording southeast through Leth Deriel (live session
/// 2026-08-16, raw_session_Renucci_20260816_205406.xml) collapsed the twin
/// 'Liyos Approach' rooms onto one node. The Southern Trade Route has two
/// ADJACENT rooms with that title, identical nw/se exits, and different
/// server ids (100076 then 100075). Node #5 was created for the first and
/// stamped srvid 100076; when the player moved southeast into the second,
/// the srvid delta correctly triggered a re-resolve (the Segoltha fix), but
/// tier (c) then matched the fingerprint back to #5 — "#5 => #5 via
/// c:fingerprint-unique [NO CHANGE]" — so the second room never got a node
/// and the recorded arc skipped it.
///
/// The rule under test is the srv-veto: tier (a) treats the server id as
/// definitive, so its contrapositive must hold for every text-based tier —
/// a candidate already known (stamped or session-learned) to be a DIFFERENT
/// server room cannot be matched, however well its text agrees.
/// </summary>
public class LethDerielTwinRoomVetoTests
{
    private const string TwinTitle = "Leth Deriel, Liyos Approach";

    private const string FirstDesc =
        "The path bends between banks of medicinal shrubs kept low by careful pruning.";

    private const string SecondDesc =
        "Light and shadow weave a tangled web of enchantment, with dappled forms " +
        "falling along the well-tended path here.";

    private static readonly string[] TwinExits = { "northwest", "southeast" };

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
    }

    private static (AutoMapperEngine engine, FakeMapperState state, MapZone zone) StartRecording()
    {
        var zone   = new MapZone { Name = "Leth Deriel" };
        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        // The scenario is recording INTO a chosen map — go through LoadZone so
        // record mode may seed rooms (post-#295, a never-loaded zone defers to
        // the cross-zone auto-detect instead of creating nodes).
        engine.LoadZone(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);
        return (engine, state, zone);
    }

    /// <summary>
    /// The live repro, verbatim: record the first twin, move southeast into the
    /// second. Same title, same exits, different srvid → the second twin must
    /// become its own node with its own server id, arc-linked from the first.
    /// </summary>
    [Fact]
    public void Recording_through_twin_rooms_creates_two_nodes()
    {
        var (engine, state, zone) = StartRecording();

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100076");
        var first = engine.CurrentNode;
        Assert.NotNull(first);
        Assert.Equal("100076", first!.ServerRoomId);

        engine.OnCommandSent("southeast");
        state.EnterRoom(TwinTitle, SecondDesc, TwinExits, "100075");

        var second = engine.CurrentNode;
        Assert.NotNull(second);
        Assert.NotEqual(first.Id, second!.Id);
        Assert.Equal("100075", second.ServerRoomId);

        // The recorded arc must include the second room, not skip past it.
        Assert.Equal(2, zone.Nodes.Count);
        Assert.Equal(second.Id, first.GetExit(Direction.SouthEast)?.DestinationId);
        Assert.Equal(first.Id,  second.GetExit(Direction.NorthWest)?.DestinationId);
    }

    /// <summary>
    /// Worst case: the twins are identical in description too (a Segoltha-shaped
    /// corridor being recorded for the first time). The srvid contradiction alone
    /// must be enough to fork a new node.
    /// </summary>
    [Fact]
    public void Recording_through_fully_identical_rooms_still_forks_on_server_id()
    {
        var (engine, state, _) = StartRecording();

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100076");
        var first = engine.CurrentNode!;

        engine.OnCommandSent("southeast");
        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100075");

        Assert.NotEqual(first.Id, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// No-regression guard: a re-send of the SAME room (a 'look', a server
    /// re-echo) with the same srvid must keep resolving to the same node — the
    /// veto only fires on a contradiction, never on agreement or absence.
    /// </summary>
    [Fact]
    public void Resending_the_same_room_does_not_fork_a_node()
    {
        var (engine, state, zone) = StartRecording();

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100076");
        var first = engine.CurrentNode!;

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100076");
        Assert.Equal(first.Id, engine.CurrentNode!.Id);
        Assert.Single(zone.Nodes);
    }

    /// <summary>
    /// Rooms with no srvid at all (Wizard mode, room numbers off) must be
    /// completely untouched by the veto — same-fingerprint re-sends keep
    /// resolving to the existing node exactly as before.
    /// </summary>
    [Fact]
    public void Veto_is_inert_when_no_server_ids_flow()
    {
        var (engine, state, _) = StartRecording();

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits);
        var first = engine.CurrentNode!;

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits);
        Assert.Equal(first.Id, engine.CurrentNode!.Id);
    }

    /// <summary>
    /// Lookup-only mode over a map where one twin is stamped and the other is
    /// not: an incoming block whose srvid contradicts the stamped twin must
    /// resolve to the OTHER one. Before the veto, tier (c) saw two candidates
    /// and the description tiebreaker picked the stamped (wrong) twin.
    /// </summary>
    [Fact]
    public void Lookup_prefers_the_twin_that_does_not_contradict_the_server_id()
    {
        var zone = new MapZone { Name = "Leth Deriel" };
        zone.Nodes[1] = new MapNode
        {
            Id = 1, Title = TwinTitle, Description = FirstDesc, ServerRoomId = "100076",
        };
        zone.Nodes[2] = new MapNode
        {
            Id = 2, Title = TwinTitle, Description = FirstDesc,
        };

        var engine = new AutoMapperEngine(zone);   // lookup-only: IsEnabled = false
        var state  = new FakeMapperState();
        engine.Attach(state);

        state.EnterRoom(TwinTitle, FirstDesc, TwinExits, "100075");
        Assert.Equal(2, engine.CurrentNode?.Id);
    }
}
