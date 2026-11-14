using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #426, the room-miss half: walking through a gate with no arc onto its
/// boundary stub leaves the room unplaced instead of on the stub. A stub in
/// the active zone with the same title still names the zone the room belongs
/// to, so the engine follows it in the same update rather than asking the host
/// (which answers a UI-thread hop later, after a script has read the old
/// <c>$zoneid</c>). Shaped on the Faldesu crossing: Map14c's "The Marsh, Stone
/// Road" is a stub noting Map7_Northern_Trade_Road.xml, where the Marsh is
/// room 1. travel.cmd guards its swim on <c>$zoneid = "14c"</c> and looped
/// there when the zone lagged.
/// </summary>
public class MapperLinkedZoneMissFollowTests
{
    private const string Ntr   = "Map7_Northern_Trade_Road";
    private const string Marsh = "The Marsh, Stone Road";

    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    private static MapExit Arc(Direction dir, string move, int? to = null) =>
        new() { Direction = dir, MoveCommand = move, DestinationId = to };

    /// <summary>Map14c: South Bank (2) —climb stone bridge→ Marsh stub (21, notes Map7).</summary>
    private static MapZone FaldesuZone()
    {
        var z = new MapZone { Name = "Faldesu River", Genie4Id = "14c" };
        var bank = new MapNode { Id = 2, Title = "Faldesu River, South Bank", Notes = "south" };
        bank.Exits.Add(Arc(Direction.North, "north"));
        bank.Exits.Add(Arc(Direction.None, "climb stone bridge", 21));
        var stub = new MapNode { Id = 21, Title = Marsh, Notes = Ntr + ".xml|NTR|Crossing" };
        stub.Exits.Add(Arc(Direction.SouthEast, "southeast"));
        z.Nodes[2]  = bank;
        z.Nodes[21] = stub;
        return z;
    }

    /// <summary>Map7: the Marsh is room 1 with its real exits.</summary>
    private static MapZone NtrZone()
    {
        var z = new MapZone { Name = "Northern Trade Road", Genie4Id = "7" };
        var marsh = new MapNode { Id = 1, Title = Marsh, Notes = "Marsh" };
        marsh.Exits.Add(Arc(Direction.North, "north", 2));
        marsh.Exits.Add(Arc(Direction.SouthEast, "southeast", 64));
        z.Nodes[1]  = marsh;
        z.Nodes[2]  = new MapNode { Id = 2,  Title = "Faldesu River, South Bank" };
        z.Nodes[64] = new MapNode { Id = 64, Title = "Northern Trade Road, Marsh" };
        return z;
    }

    private sealed class Rig
    {
        public required AutoMapperEngine Engine { get; init; }
        public required FakeGameState State { get; init; }
        public List<string> Loads { get; } = new();
        public List<(string zone, int? room)> Seen { get; } = new();
        public int Misses;
    }

    /// <summary>Engine on Map14c with the character somewhere the map doesn't
    /// know (unplaced), so the next room has no arc and no previous node to
    /// walk from. <c>Seen</c> records what a globals sync would read.</summary>
    private static Rig Unplaced(bool ntrAvailable = true)
    {
        var s = new FakeGameState { RoomTitle = "Somewhere Else", Exits = new[] { "up" } };
        var e = new AutoMapperEngine(new MapZone());
        e.Attach(s);
        e.LoadZone(FaldesuZone());
        Assert.Null(e.CurrentNode);

        var rig = new Rig { Engine = e, State = s };
        e.LinkedZoneLoader = file =>
        {
            rig.Loads.Add(file);
            return ntrAvailable && file == Ntr ? NtrZone() : null;
        };
        e.CurrentNodeChanged += () => rig.Seen.Add((e.ActiveZone.Genie4Id ?? "", e.CurrentNode?.Id));
        e.RoomNotFoundInZone += (_, _, _) => rig.Misses++;
        return rig;
    }

    /// <summary>The live Marsh shows only "north" while the stub shows only
    /// "southeast", so every match tier misses.</summary>
    private static void ArriveAtTheMarsh(Rig rig)
    {
        rig.State.RoomTitle = Marsh;
        rig.State.Exits     = new[] { "north" };
        rig.State.Fire();
    }

    [Fact]
    public void A_miss_with_a_same_titled_stub_switches_zones_in_the_same_update()
    {
        var rig = Unplaced();

        ArriveAtTheMarsh(rig);

        Assert.Equal(0, rig.Misses);
        Assert.Equal(new[] { Ntr }, rig.Loads);
        Assert.Equal("7", rig.Engine.ActiveZone.Genie4Id);
        Assert.Equal(1, rig.Engine.CurrentNode?.Id);
        // The last thing a globals sync saw is the room in zone 7.
        Assert.Equal(("7", 1), rig.Seen[^1]);
    }

    [Fact]
    public void A_miss_whose_stub_zone_cannot_be_loaded_still_asks_the_host()
    {
        var rig = Unplaced(ntrAvailable: false);

        ArriveAtTheMarsh(rig);

        Assert.Equal(1, rig.Misses);
        Assert.Equal("14c", rig.Engine.ActiveZone.Genie4Id);
        Assert.Null(rig.Engine.CurrentNode);
    }

    [Fact]
    public void A_miss_while_the_view_is_browsing_does_not_follow()
    {
        var rig = Unplaced();
        rig.Engine.ViewIsBrowsing = true;

        ArriveAtTheMarsh(rig);

        Assert.Empty(rig.Loads);
        Assert.Equal(1, rig.Misses);
        Assert.Equal("14c", rig.Engine.ActiveZone.Genie4Id);
    }

    [Fact]
    public void A_miss_with_no_same_titled_stub_asks_the_host()
    {
        var rig = Unplaced();

        rig.State.RoomTitle = "Faldesu River, Midstream";
        rig.State.Exits     = new[] { "north", "south" };
        rig.State.Fire();

        Assert.Empty(rig.Loads);
        Assert.Equal(1, rig.Misses);
    }

    [Fact]
    public void Climbing_onto_the_Marsh_stub_reads_zone_7_before_the_update_returns()
    {
        // The arc-landing path, on the crossing that looped travel.cmd.
        var s = new FakeGameState { RoomTitle = "Faldesu River, South Bank", Exits = new[] { "north" } };
        var e = new AutoMapperEngine(new MapZone());
        e.Attach(s);
        e.LoadZone(FaldesuZone());
        Assert.Equal(2, e.CurrentNode?.Id);
        e.LinkedZoneLoader = file => file == Ntr ? NtrZone() : null;
        var seen = new List<(string zone, int? room)>();
        e.CurrentNodeChanged += () => seen.Add((e.ActiveZone.Genie4Id ?? "", e.CurrentNode?.Id));

        e.OnCommandSent("climb stone bridge");
        s.RoomTitle = Marsh;
        s.Exits     = new[] { "southeast" };
        s.Fire();

        Assert.Equal(("7", 1), seen[^1]);
    }
}
