using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

public class MapperLinkRoomsTests
{
    private static (AutoMapperEngine e, MapNode a, MapNode b, MapNode c) Fixture()
    {
        var zone = new MapZone { Name = "z" };
        var a = new MapNode { Id = 1, Title = "A" };
        var b = new MapNode { Id = 2, Title = "B" };
        var c = new MapNode { Id = 3, Title = "C" };
        zone.Nodes[1] = a; zone.Nodes[2] = b; zone.Nodes[3] = c;
        return (new AutoMapperEngine(zone), a, b, c);
    }

    [Fact]
    public void CompassLink_CreatesForwardAndOppositeReturn()
    {
        var (e, a, b, _) = Fixture();
        var r = e.LinkRooms(a, b, "west", addReturn: true);

        Assert.NotNull(r);
        Assert.Equal(2, a.GetExit(Direction.West)!.DestinationId);
        Assert.Equal("west", a.GetExit(Direction.West)!.MoveCommand);
        Assert.Equal(1, b.GetExit(Direction.East)!.DestinationId);
    }

    [Fact]
    public void NonCompassLink_HasNoReturnUnlessOneIsGiven()
    {
        var (e, a, b, _) = Fixture();
        e.LinkRooms(a, b, "go oak door", addReturn: true);
        Assert.Equal(2, a.Exits.Single().DestinationId);
        Assert.Equal("go oak door", a.Exits.Single().ExitToken);
        Assert.Empty(b.Exits);

        e.LinkRooms(a, b, "go oak door", addReturn: true, returnVerb: "go oak door");
        Assert.Single(a.Exits);                       // re-pointed, not duplicated
        Assert.Equal(1, b.Exits.Single().DestinationId);
    }

    [Fact]
    public void ReturnNotAddedWhenNotRequested_AndExistingExitsAreUntouched()
    {
        var (e, a, b, c) = Fixture();
        a.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north", DestinationId = 3 });

        e.LinkRooms(a, b, "south", addReturn: false);

        Assert.Equal(3, a.GetExit(Direction.North)!.DestinationId);
        Assert.Equal(2, a.GetExit(Direction.South)!.DestinationId);
        Assert.Empty(b.Exits);
    }

    [Fact]
    public void LinkingAgain_RepointsTheExistingExit()
    {
        var (e, a, b, c) = Fixture();
        e.LinkRooms(a, b, "east", addReturn: false);
        e.LinkRooms(a, c, "east", addReturn: false);

        Assert.Single(a.Exits);
        Assert.Equal(3, a.GetExit(Direction.East)!.DestinationId);
    }

    [Fact]
    public void InvalidLinks_ReturnNull()
    {
        var (e, a, b, _) = Fixture();
        Assert.Null(e.LinkRooms(a, a, "east", true));
        Assert.Null(e.LinkRooms(a, b, "  ", true));
        Assert.Null(e.LinkRooms(a, new MapNode { Id = 99, Title = "elsewhere" }, "east", true));
        Assert.Empty(a.Exits);
    }

    [Fact]
    public void Link_FiresMapChanged()
    {
        var (e, a, b, _) = Fixture();
        var n = 0; e.MapChanged += () => n++;
        e.LinkRooms(a, b, "north", true);
        Assert.Equal(1, n);
    }
}
