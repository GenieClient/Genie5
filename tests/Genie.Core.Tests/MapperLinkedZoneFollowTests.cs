using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #426: when a room update lands on a boundary stub (a node whose note names
/// another map), the engine switches to that map and re-resolves in the SAME
/// update. A listener reading the zone and room on CurrentNodeChanged (as
/// GenieCore.SyncMapperGlobals does for $zoneid/$roomid) must end on the new
/// zone before the update returns. Shaped on Shard's East Gate: Map67 node 132
/// is the stub copy of Map66's East Bridge (node 44), and Map66 node 216 is the
/// stub copy of Map67's East City Gates (node 32).
/// </summary>
public class MapperLinkedZoneFollowTests
{
    private const string Map66 = "Map66_Shard_to_Gondola_Area";
    private const string Map67 = "Map67_Shard";

    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    private static MapNode Node(int id, string title, string notes = "", params (Direction dir, string move, int? dest)[] exits)
    {
        var n = new MapNode { Id = id, Title = title, Notes = notes };
        foreach (var (dir, move, dest) in exits)
            n.Exits.Add(new MapExit { Direction = dir, MoveCommand = move, DestinationId = dest });
        return n;
    }

    private static MapZone Shard() => new()
    {
        Name = "Shard", Genie4Id = "67",
        Nodes =
        {
            [32]  = Node(32, "Shard, East City Gates", "",
                         (Direction.North, "north", 31), (Direction.South, "south", 33),
                         (Direction.West, "west", 93), (Direction.None, "go city gate", 132)),
            [132] = Node(132, "Shard, East Bridge", Map66 + ".xml|E Gate|East Gate|Portal|egate",
                         (Direction.East, "east", null), (Direction.None, "go city gate", 32)),
        },
    };

    private static MapZone GondolaArea() => new()
    {
        Name = "Shard to Gondola Area", Genie4Id = "66",
        Nodes =
        {
            [43]  = Node(43, "Shard, East Bridge", "",
                         (Direction.East, "east", 42), (Direction.West, "west", 44)),
            [44]  = Node(44, "Shard, East Bridge", "East Bridge|Portal",
                         (Direction.East, "east", 43), (Direction.None, "go city gate", 216),
                         (Direction.None, "go meeting portal", null)),
            [216] = Node(216, "Shard, East City Gates", Map67 + ".xml|E Gate|egate",
                         (Direction.North, "north", null), (Direction.South, "south", null),
                         (Direction.West, "west", null), (Direction.None, "go city gate", 44)),
        },
    };

    /// <summary>Engine on Map67 at the East City Gates, with a loader for both maps.
    /// <c>seen</c> records (zone id, room id) on every CurrentNodeChanged.</summary>
    private static (AutoMapperEngine e, FakeGameState s, List<(string zone, int? room)> seen, List<string> loads)
        AtTheGate(bool withLoader = true)
    {
        var s = new FakeGameState { RoomTitle = "Shard, East City Gates", Exits = new[] { "n", "s", "w" } };
        var e = new AutoMapperEngine(new MapZone());
        e.Attach(s);
        e.LoadZone(Shard());
        Assert.Equal(32, e.CurrentNode?.Id);

        var loads = new List<string>();
        if (withLoader)
            e.LinkedZoneLoader = file =>
            {
                loads.Add(file);
                return file == Map66 ? GondolaArea() : file == Map67 ? Shard() : null;
            };

        var seen = new List<(string, int?)>();
        e.CurrentNodeChanged += () => seen.Add((e.ActiveZone.Genie4Id ?? "", e.CurrentNode?.Id));
        return (e, s, seen, loads);
    }

    private static void GoThroughTheGate(AutoMapperEngine e, FakeGameState s)
    {
        e.OnCommandSent("go city gate");
        s.RoomTitle = "Shard, East Bridge";
        s.Exits     = new[] { "e" };
        s.Fire();
    }

    [Fact]
    public void Landing_on_a_stub_switches_zones_in_the_same_update()
    {
        var (e, s, seen, loads) = AtTheGate();
        string? followed = null;
        e.LinkedZoneFollowing += (file, _) => followed = file;

        GoThroughTheGate(e, s);

        // By the time the update returns, the engine is on Map66's own room.
        Assert.Equal("66", e.ActiveZone.Genie4Id);
        Assert.Equal(44, e.CurrentNode?.Id);
        Assert.Equal(Map66, followed);
        Assert.Equal(new[] { Map66 }, loads);

        // What a $zoneid/$roomid listener saw last is the new zone, not the stub.
        Assert.Equal(("66", 44), seen[^1]);
    }

    [Fact]
    public void The_new_zone_does_not_bounce_back_through_its_own_stub()
    {
        // Coming back the other way lands on Map66's stub for the gate, which
        // names Map67. One update follows it once, then stops.
        var (e, s, _, loads) = AtTheGate();
        GoThroughTheGate(e, s);
        loads.Clear();

        e.OnCommandSent("go city gate");
        s.RoomTitle = "Shard, East City Gates";
        s.Exits     = new[] { "n", "s", "w" };
        s.Fire();

        Assert.Equal("67", e.ActiveZone.Genie4Id);
        Assert.Equal(32, e.CurrentNode?.Id);
        Assert.Equal(new[] { Map67 }, loads);
    }

    [Fact]
    public void While_the_view_is_browsing_the_engine_stays_on_the_stub()
    {
        var (e, s, _, loads) = AtTheGate();
        e.ViewIsBrowsing = true;

        GoThroughTheGate(e, s);

        Assert.Equal("67", e.ActiveZone.Genie4Id);
        Assert.Equal(132, e.CurrentNode?.Id);
        Assert.Empty(loads);
    }

    [Fact]
    public void When_the_linked_zone_cannot_be_loaded_the_engine_stays_on_the_stub()
    {
        var (e, s, _, _) = AtTheGate();
        e.LinkedZoneLoader = _ => null;

        GoThroughTheGate(e, s);

        Assert.Equal("67", e.ActiveZone.Genie4Id);
        Assert.Equal(132, e.CurrentNode?.Id);
    }

    [Fact]
    public void Without_a_loader_nothing_changes()
    {
        var (e, s, seen, _) = AtTheGate(withLoader: false);

        GoThroughTheGate(e, s);

        Assert.Equal(132, e.CurrentNode?.Id);
        Assert.Equal(("67", 132), seen[^1]);
    }
}
