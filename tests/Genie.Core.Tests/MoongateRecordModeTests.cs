using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Live 2026-10-03: with record mode on, <c>go moongate</c> from Throne City into
/// Crossing counts as walk evidence (movement verb), so the engine seeded an
/// orphan node into the Throne City map instead of asking the host to switch
/// zones — the mapper stayed on the wrong map. A non-compass movement verb that
/// lands in a room another zone owns must defer to the host's zone auto-detect.
/// </summary>
public class MoongateRecordModeTests
{
    private sealed class FakeState : IMapperGameState
    {
        public string RoomTitle       { get; private set; } = string.Empty;
        public string RoomDescription { get; private set; } = string.Empty;
        public string ServerRoomId    { get; private set; } = string.Empty;
        public IReadOnlyCollection<string> Exits { get; private set; } = Array.Empty<string>();
        public event Action? StateChanged;

        public void Enter(string title, string desc, string[] exits, string srv)
        {
            RoomTitle = title; RoomDescription = desc; Exits = exits; ServerRoomId = srv;
            StateChanged?.Invoke();
        }
    }

    private static (AutoMapperEngine engine, FakeState state, MapZone zone, List<string> misses) Start()
    {
        var zone = new MapZone { Name = "Throne City" };
        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        engine.LoadZone(zone);
        var state = new FakeState();
        engine.Attach(state);
        var misses = new List<string>();
        engine.RoomNotFoundInZone += (_, t, _) => misses.Add(t);
        state.Enter("Throne City, Moongate Plaza", "A plaza.", new[] { "north" }, "100");
        return (engine, state, zone, misses);
    }

    [Fact]
    public void Go_moongate_into_a_room_another_zone_owns_defers_instead_of_recording()
    {
        var (engine, state, zone, misses) = Start();
        engine.ForeignRoomProbe = (_, title, _) => title == "Phelim's Shrine, Exterior";

        engine.OnCommandSent("go moongate");
        state.Enter("Phelim's Shrine, Exterior", "A shrine.", new[] { "southeast" }, "200");

        Assert.Single(zone.Nodes);
        Assert.Null(engine.CurrentNode);
        Assert.Contains("Phelim's Shrine, Exterior", misses);
    }

    [Fact]
    public void Go_door_into_virgin_territory_still_records()
    {
        var (engine, state, zone, misses) = Start();
        engine.ForeignRoomProbe = (_, _, _) => false;

        engine.OnCommandSent("go door");
        state.Enter("Back Room", "Dusty.", new[] { "out" }, "300");

        Assert.Equal(2, zone.Nodes.Count);
        Assert.Empty(misses);
    }
}
