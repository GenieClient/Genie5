using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Regressions from the 2026-08-23 Astral Plane live session
/// (raw_session_Renucci_20260823_095440.xml), which exposed two coupled
/// failures:
///
/// 1. The plane sends NO nav tags — zero &lt;nav&gt; elements across the whole
///    walk — so IMapperGameState.ServerRoomId silently keeps the id of the
///    last nav'd room. Tier (a) then matched every astral room back to the
///    Phelim's Sanctum node ("#196 =&gt; #196 via a:server-id [NO CHANGE]").
///    Rule: a room delta WITHOUT a srvid delta marks the id stale, and a
///    stale id behaves as if absent — no tier (a), no srv-veto, no stamping.
///
/// 2. Record mode created nodes for teleport arrivals — rooms reached with
///    no walk evidence (no compass direction, no movement command) — seeding
///    foreign rooms into the loaded zone ('Observatory, Foyer' stitched into
///    the Ponthilas map). Rule: no walk evidence ⇒ defer, fire
///    RoomNotFoundInZone so the host's cross-zone auto-detect gets its shot,
///    and only create the node when the host confirms nothing claims it
///    (RecordDeferredRoom).
/// </summary>
public class AstralStaleServerIdTests
{
    private const string SanctumTitle = "Phelim's Sanctum, Tear of Grazhir";
    private const string SanctumDesc  = "A tear of pure crystal dominates the chamber.";
    private const string SanctumId    = "1152010";

    private const string ConduitTitle = "Astral Plane, Mintais Conduit";
    private const string ConduitDesc  = "Silvery-white light bends around the conduit.";

    private const string PillarTitle  = "Astral Plane, Pillar of Fortune";
    private const string PillarDesc   = "The pillar hums with latent possibility.";

    private static readonly string[] SanctumExits = { "south" };
    private static readonly string[] NoExits      = Array.Empty<string>();
    private static readonly string[] PillarExits  = { "east", "west", "up", "down" };

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

        /// <summary>A room transition with NO nav tag: title/desc/exits change
        /// but ServerRoomId keeps its previous value — exactly what the real
        /// adapter does for the Astral Plane.</summary>
        public void EnterRoomWithoutNav(string title, string description,
                                        IReadOnlyCollection<string> exits)
        {
            RoomTitle       = title;
            RoomDescription = description;
            Exits           = exits;
            StateChanged?.Invoke();
        }
    }

    private static (AutoMapperEngine engine, FakeMapperState state, MapZone zone, List<string> misses)
        StartRecording()
    {
        var zone   = new MapZone { Name = "Ponthilas" };
        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        engine.LoadZone(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);
        var misses = new List<string>();
        engine.RoomNotFoundInZone += (_, title, _) => misses.Add(title);
        return (engine, state, zone, misses);
    }

    [Fact]
    public void Walked_arrival_still_records_immediately_with_arc()
    {
        var (engine, state, zone, misses) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        var sanctum = engine.CurrentNode;
        Assert.NotNull(sanctum);

        engine.OnCommandSent("south");
        state.EnterRoom("Sanctum, South Hall", "A quiet hall.", new[] { "north" }, "1152011");

        // Walk evidence present: node created immediately, arc authored.
        Assert.Equal(2, zone.Nodes.Count);
        Assert.NotEqual(sanctum!.Id, engine.CurrentNode!.Id);
        Assert.Empty(misses);
        Assert.Equal(engine.CurrentNode.Id,
                     sanctum.GetExit(Direction.South)?.DestinationId);
    }

    [Fact]
    public void Astral_room_with_stale_srvid_defers_instead_of_matching_back()
    {
        var (engine, state, zone, misses) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        var sanctum = engine.CurrentNode;
        Assert.NotNull(sanctum);

        // Cross into the astral: full room transition, no nav, no walk
        // evidence — state keeps 1152010 and nothing was typed.
        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);

        // Pre-fix, tier (a) matched the stale 1152010 straight back to the
        // sanctum node. Now: unplaced, deferred, and the host was asked.
        Assert.Null(engine.CurrentNode);
        Assert.Single(zone.Nodes);
        Assert.Contains(ConduitTitle, misses);

        // Host answers "no zone claims it" → the room records here after all.
        Assert.True(engine.RecordDeferredRoom());
        Assert.Equal(2, zone.Nodes.Count);
        Assert.Equal(ConduitTitle, engine.CurrentNode!.Title);
    }

    [Fact]
    public void Stale_srvid_is_not_stamped_onto_the_deferred_node()
    {
        var (engine, state, zone, _) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        var sanctum = engine.CurrentNode;

        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);
        Assert.True(engine.RecordDeferredRoom());
        var conduit = engine.CurrentNode;
        Assert.NotEqual(sanctum!.Id, conduit!.Id);
        Assert.True(string.IsNullOrEmpty(conduit.ServerRoomId));

        // Return to the sanctum WITH a fresh nav for 1152010: the id must
        // still belong to the sanctum node — if the conduit had stolen the
        // stamp, this would resolve to the wrong room.
        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        Assert.Equal(sanctum.Id, engine.CurrentNode!.Id);
    }

    [Fact]
    public void Fresh_srvid_teleport_defers_then_records_with_stamp()
    {
        var (engine, state, zone, misses) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);
        Assert.True(engine.RecordDeferredRoom());

        // Leave the plane: a fresh nav arrives, but it's still a teleport
        // (no walk evidence) → defer, then host-confirmed record WITH stamp.
        state.EnterRoom("Observatory, Foyer", "A quiet entry hall.", new[] { "north" }, "208076");
        Assert.Null(engine.CurrentNode);
        Assert.Contains("Observatory, Foyer", misses);
        Assert.True(engine.RecordDeferredRoom());
        var foyer = engine.CurrentNode;
        Assert.Equal("208076", foyer!.ServerRoomId);

        // The fresh stamp round-trips: walking out and re-entering by the
        // same nav resolves via tier (a).
        engine.OnCommandSent("north");
        state.EnterRoom("Observatory, Hall", "A long hall.", new[] { "south" }, "208077");
        engine.OnCommandSent("south");
        state.EnterRoom("Observatory, Foyer", "A quiet entry hall.", new[] { "north" }, "208076");
        Assert.Equal(foyer.Id, engine.CurrentNode!.Id);
    }

    [Fact]
    public void Next_room_block_supersedes_a_pending_deferred_record()
    {
        var (engine, state, zone, _) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);   // deferred
        state.EnterRoomWithoutNav(PillarTitle, PillarDesc, PillarExits); // supersedes

        // A late host confirmation must record where the player IS, never the
        // room they already drifted out of.
        Assert.True(engine.RecordDeferredRoom());
        Assert.Equal(PillarTitle, engine.CurrentNode!.Title);
        Assert.Equal(2, zone.Nodes.Count);   // sanctum + pillar; conduit skipped
        Assert.False(engine.RecordDeferredRoom());   // context consumed
    }

    [Fact]
    public void Lookup_mode_fires_room_not_found_instead_of_pinning_on_stale_srvid()
    {
        var zone   = new MapZone { Name = "Ponthilas" };
        var sanctumNode = new MapNode
        {
            Id = 1, Title = SanctumTitle, Description = SanctumDesc,
            ServerRoomId = SanctumId,
        };
        zone.Nodes[1] = sanctumNode;

        var engine = new AutoMapperEngine(zone);   // lookup-only
        engine.LoadZone(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);

        var misses = new List<string>();
        engine.RoomNotFoundInZone += (_, title, _) => misses.Add(title);

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        Assert.Equal(1, engine.CurrentNode?.Id);

        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);

        // Pre-fix, tier (a) matched the stale 1152010 straight back to node 1.
        Assert.Null(engine.CurrentNode);
        Assert.Contains(ConduitTitle, misses);

        // And lookup-only mode never records, deferred or otherwise.
        Assert.False(engine.RecordDeferredRoom());
    }
}
