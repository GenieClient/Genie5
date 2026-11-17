using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #401: <see cref="AutoMapperEngine.IsTracking"/> is Genie 4's automapper on/off
/// switch. Off, the engine ignores room changes and sent commands (Genie 4 stopped
/// passing text and input to its mapper), so the current room keeps its last value;
/// back on, it re-resolves the room at once.
/// </summary>
public class AutoMapperTrackingSwitchTests
{
    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    private static (AutoMapperEngine engine, FakeGameState game, MapZone zone) Make()
    {
        var zone = new MapZone { Name = "z" };
        var a = new MapNode { Id = 1, Title = "Alpha", ServerRoomId = "100" };
        var b = new MapNode { Id = 2, Title = "Beta",  ServerRoomId = "200" };
        a.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north", DestinationId = 2 });
        b.Exits.Add(new MapExit { Direction = Direction.South, MoveCommand = "south", DestinationId = 1 });
        zone.Nodes[1] = a; zone.Nodes[2] = b;
        var engine = new AutoMapperEngine(zone);
        var game = new FakeGameState();
        engine.Attach(game);
        engine.LoadZone(zone);
        return (engine, game, zone);
    }

    [Fact]
    public void On_by_default()
    {
        var (engine, _, _) = Make();
        Assert.True(engine.IsTracking);
    }

    [Fact]
    public void Off_room_changes_are_ignored_and_the_last_room_is_kept()
    {
        var (engine, game, _) = Make();
        game.RoomTitle = "Alpha"; game.Exits = new[] { "north" }; game.ServerRoomId = "100"; game.Fire();
        Assert.Equal(1, engine.CurrentNode?.Id);

        engine.IsTracking = false;
        engine.OnCommandSent("north");
        game.RoomTitle = "Beta"; game.Exits = new[] { "south" }; game.ServerRoomId = "200"; game.Fire();

        Assert.Equal(1, engine.CurrentNode?.Id);
        Assert.Equal(string.Empty, engine.LastSentCommand);
    }

    [Fact]
    public void Back_on_the_current_room_is_resolved_at_once()
    {
        var (engine, game, _) = Make();
        game.RoomTitle = "Alpha"; game.Exits = new[] { "north" }; game.ServerRoomId = "100"; game.Fire();

        engine.IsTracking = false;
        game.RoomTitle = "Beta"; game.Exits = new[] { "south" }; game.ServerRoomId = "200"; game.Fire();
        Assert.Equal(1, engine.CurrentNode?.Id);

        engine.IsTracking = true;
        Assert.Equal(2, engine.CurrentNode?.Id);
    }

    [Fact]
    public void Off_record_mode_creates_no_rooms()
    {
        var (engine, game, zone) = Make();
        engine.IsEnabled = true;    // Record on
        engine.IsTracking = false;  // but the mapper is off
        game.RoomTitle = "Gamma"; game.Exits = new[] { "east" }; game.ServerRoomId = "300"; game.Fire();

        Assert.Equal(2, zone.Nodes.Count);
    }
}
