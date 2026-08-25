using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Regression: the Astral Plane sends NO nav tags (live session 2026-08-23,
/// raw_session_Renucci_20260823_095440.xml — zero &lt;nav&gt; elements across the
/// whole plane), so IMapperGameState.ServerRoomId silently keeps the id of the
/// last nav'd room. Tier (a) then matched every astral room back to the
/// Phelim's Sanctum node — stale id 1152010 answering
/// "#196 =&gt; #196 via a:server-id [NO CHANGE]" on every pillar and conduit —
/// pinning the marker there for the entire walk.
///
/// The rule under test: a room delta WITHOUT a srvid delta marks the id stale,
/// and a stale id must behave as if absent — no tier (a) match, no srv-veto,
/// no stamping onto nodes it doesn't belong to.
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

    private static (AutoMapperEngine engine, FakeMapperState state, MapZone zone) StartRecording()
    {
        var zone   = new MapZone { Name = "Ponthilas" };
        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        engine.LoadZone(zone);
        var state  = new FakeMapperState();
        engine.Attach(state);
        return (engine, state, zone);
    }

    [Fact]
    public void Astral_room_with_stale_srvid_is_not_matched_back_by_server_id()
    {
        var (engine, state, zone) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        var sanctum = engine.CurrentNode;
        Assert.NotNull(sanctum);

        // Cross into the astral: full room transition, no nav — state keeps 1152010.
        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);

        Assert.NotNull(engine.CurrentNode);
        Assert.NotEqual(sanctum!.Id, engine.CurrentNode!.Id);
        Assert.Equal(2, zone.Nodes.Count);
    }

    [Fact]
    public void Stale_srvid_is_not_stamped_onto_the_new_node()
    {
        var (engine, state, zone) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        var sanctum = engine.CurrentNode;
        Assert.NotNull(sanctum);

        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);
        var conduit = engine.CurrentNode;
        Assert.NotNull(conduit);
        Assert.NotEqual(sanctum!.Id, conduit!.Id);

        // Return to the sanctum WITH a fresh nav for 1152010: the id must still
        // belong to the sanctum node — if the astral node had stolen the stamp,
        // this would match (or veto onto) the wrong room.
        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        Assert.Equal(sanctum.Id, engine.CurrentNode!.Id);
    }

    [Fact]
    public void Fresh_srvid_after_stale_stretch_resolves_normally()
    {
        var (engine, state, zone) = StartRecording();

        state.EnterRoom(SanctumTitle, SanctumDesc, SanctumExits, SanctumId);
        state.EnterRoomWithoutNav(ConduitTitle, ConduitDesc, NoExits);
        state.EnterRoomWithoutNav(PillarTitle, PillarDesc, PillarExits);
        var pillar = engine.CurrentNode;
        Assert.NotNull(pillar);

        // Leave the plane: a fresh nav arrives for a brand-new room.
        state.EnterRoom("Observatory, Foyer", "A quiet entry hall.", new[] { "north" }, "208076");
        var foyer = engine.CurrentNode;
        Assert.NotNull(foyer);
        Assert.NotEqual(pillar!.Id, foyer!.Id);

        // And that fresh id round-trips: re-entering by the same nav matches it.
        state.EnterRoom("Observatory, Foyer", "A quiet entry hall.", new[] { "north" }, "208076");
        Assert.Equal(foyer.Id, engine.CurrentNode!.Id);
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
    }
}
