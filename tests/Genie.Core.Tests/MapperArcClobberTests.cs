using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>Record mode must not retarget an existing arc on a non-authoritative room match.</summary>
public class MapperArcClobberTests
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

    [Fact]
    public void RecordMode_FuzzyMatch_DoesNotOverwriteExistingArc()
    {
        var zone = new MapZone { Name = "z" };
        var a = new MapNode { Id = 1, Title = "Alpha" };
        var b = new MapNode { Id = 2, Title = "Beta" };
        var c = new MapNode { Id = 3, Title = "Gamma" };
        a.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north", DestinationId = 2 });
        b.Exits.Add(new MapExit { Direction = Direction.South, MoveCommand = "south", DestinationId = 1 });
        c.Exits.Add(new MapExit { Direction = Direction.South, MoveCommand = "south" });
        zone.Nodes[1] = a; zone.Nodes[2] = b; zone.Nodes[3] = c;

        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        var fake = new FakeGameState();
        engine.Attach(fake);
        engine.LoadZone(zone);

        fake.RoomTitle = "Alpha"; fake.Exits = new[] { "north" }; fake.Fire();
        Assert.Equal(1, engine.CurrentNode?.Id);

        // Walk north but the live room text matches Gamma (wrong/ambiguous match).
        engine.OnCommandSent("north");
        fake.RoomTitle = "Gamma"; fake.Exits = new[] { "south" }; fake.Fire();

        Assert.Equal(2, a.GetExit(Direction.North)!.DestinationId);
    }

    [Fact]
    public void RecordMode_ServerIdMatch_DoesNotOverwriteAsymmetricBackArc()
    {
        var zone = new MapZone { Name = "z" };
        var a = new MapNode { Id = 1, Title = "Alpha", ServerRoomId = "100" };
        var b = new MapNode { Id = 2, Title = "Beta",  ServerRoomId = "200" };
        var c = new MapNode { Id = 3, Title = "Gamma", ServerRoomId = "300" };
        a.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north", DestinationId = 2 });
        // B's south exit really leads to C, not back to A (asymmetric hall).
        b.Exits.Add(new MapExit { Direction = Direction.South, MoveCommand = "south", DestinationId = 3 });
        zone.Nodes[1] = a; zone.Nodes[2] = b; zone.Nodes[3] = c;

        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        var fake = new FakeGameState();
        engine.Attach(fake);
        engine.LoadZone(zone);

        fake.RoomTitle = "Alpha"; fake.Exits = new[] { "north" }; fake.ServerRoomId = "100"; fake.Fire();
        engine.OnCommandSent("north");
        fake.RoomTitle = "Beta"; fake.Exits = new[] { "south" }; fake.ServerRoomId = "200"; fake.Fire();

        Assert.Equal(2, engine.CurrentNode?.Id);
        Assert.Equal(3, b.GetExit(Direction.South)!.DestinationId);
    }
}
