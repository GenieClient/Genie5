using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>Crossing "The Strand, Sandy Path" exists twice; the boundary stub we came through picks the right one.</summary>
public class MapperArrivalHintTests
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

    private static MapNode Room(int id, params string[] gos)
    {
        var n = new MapNode { Id = id, Title = "The Strand, Sandy Path" };
        n.Exits.Add(new MapExit { Direction = Direction.SouthEast, MoveCommand = "southeast" });
        n.Exits.Add(new MapExit { Direction = Direction.NorthWest, MoveCommand = "northwest" });
        foreach (var g in gos) n.Exits.Add(new MapExit { Direction = Direction.None, MoveCommand = g, ExitToken = g });
        return n;
    }

    private static (AutoMapperEngine e, MapZone z) Crossing()
    {
        var z = new MapZone { Name = "Map1_Crossing" };
        z.Nodes[49] = Room(49, "go rickety wagon", "go whitewashed gate");
        z.Nodes[50] = Room(50, "go meeting portal");
        var e = new AutoMapperEngine(new MapZone { Name = "Map150_Fang_Cove" });
        e.Attach(new FakeGameState
        {
            RoomTitle = "The Strand, Sandy Path", Exits = new[] { "nw", "se" }, ServerRoomId = "10171",
        });
        return (e, z);
    }

    [Fact]
    public void TheStubsPortalExit_PicksTheRoomThatHasIt()
    {
        var (e, z) = Crossing();
        var stub = Room(139, "go meeting portal");           // Fang Cove's copy of the Crossing portal room

        e.LoadZone(z, stub);

        Assert.Equal(50, e.CurrentNode?.Id);
    }

    [Fact]
    public void AStubWithDifferentExits_PicksTheOtherTwin()
    {
        var (e, z) = Crossing();
        e.LoadZone(z, Room(900, "go rickety wagon"));
        Assert.Equal(49, e.CurrentNode?.Id);
    }

    [Fact]
    public void TheHintIsUsedOnce_NotOnLaterResolutions()
    {
        var (e, z) = Crossing();
        e.LoadZone(z, Room(139, "go meeting portal"));
        Assert.Equal(50, e.CurrentNode?.Id);

        // A fresh load with no hint is back to the unhinted behaviour (first candidate).
        e.LoadZone(z);
        Assert.Equal(49, e.CurrentNode?.Id);
    }

    [Fact]
    public void RecordMode_DoesNotStampAServerIdOnAWeakTwinGuess_ButDoesOnAHintedMatch()
    {
        // No hint: the twin is a tier-f guess — the server id must not be written onto it.
        var (e, z) = Crossing();
        e.IsEnabled = true;
        e.LoadZone(z);
        Assert.Equal(49, e.CurrentNode?.Id);
        Assert.Equal("", z.Nodes[49].ServerRoomId);

        // With the stub hint the match is trustworthy, so the id is stamped on the RIGHT room.
        var (e2, z2) = Crossing();
        e2.IsEnabled = true;
        e2.LoadZone(z2, Room(139, "go meeting portal"));
        Assert.Equal(50, e2.CurrentNode?.Id);
        Assert.Equal("10171", z2.Nodes[50].ServerRoomId);
        Assert.Equal("", z2.Nodes[49].ServerRoomId);
    }
}
