using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>A return portal (Fang Cove's EXIT portal) goes back to wherever you entered.</summary>
public sealed class ReturnPortalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "g5rp-" + Guid.NewGuid().ToString("N"));

    public ReturnPortalTests()
    {
        Directory.CreateDirectory(_dir);
        Zone("Map150_Fang_Cove", Node(85, ("north", 86)), Node(86));
        Zone("Map1_Crossing",    Node(50, ("southeast", 51)), Node(51));
        Zone("Map66_Shard",      Node(10, ("east", 11)), Node(11));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MapNode Node(int id, params (string dir, int to)[] exits)
    {
        var n = new MapNode { Id = id, Title = "Room " + id };
        foreach (var (dir, to) in exits)
            n.Exits.Add(new MapExit { Direction = DirectionHelper.Parse(dir), MoveCommand = dir, ExitToken = dir, DestinationId = to });
        return n;
    }

    private void Zone(string name, params MapNode[] nodes)
    {
        var z = new MapZone { Name = name };
        foreach (var n in nodes) z.Nodes[n.Id] = n;
        new MapZoneRepository().Save(Path.Combine(_dir, name + ".xml"), z);
    }

    private static readonly ZoneConnection Portal = new()
    {
        FromZone = "Map150_Fang_Cove", FromRoom = "85", Verb = "go exit portal",
        TransitType = "portal", ReturnsToOrigin = true,
    };

    private MultiZonePathfinder Finder(params (string zone, PortalOrigin origin)[] origins) =>
        new(new MapZoneRepository(), _dir, new List<ZoneConnection> { Portal }, null, null, 0)
        {
            PortalOrigins = origins.ToDictionary(o => o.zone, o => o.origin),
        };

    [Fact]
    public void WithAnOrigin_ThePortalReturnsThere_AndTheWalkContinuesFromIt()
    {
        var f = Finder(("Map150_Fang_Cove", new PortalOrigin("Map1_Crossing", "50")));

        var path = f.FindPath("Map150_Fang_Cove", "85", "Map1_Crossing", "51");

        Assert.NotNull(path);
        Assert.True(path!.HasCrossZoneHop);
        Assert.Equal(new[] { "go exit portal", "southeast" }, path.Steps.Select(s => s.Verb).ToArray());
        Assert.Equal("Map1_Crossing", path.Steps[0].TargetZone);
    }

    [Fact]
    public void TheSamePortal_ReturnsToShard_WhenThatIsWhereYouEntered()
    {
        var f = Finder(("Map150_Fang_Cove", new PortalOrigin("Map66_Shard", "10")));

        Assert.NotNull(f.FindPath("Map150_Fang_Cove", "85", "Map66_Shard", "11"));
        Assert.Null(f.FindPath("Map150_Fang_Cove", "85", "Map1_Crossing", "51"));   // not where you came from
    }

    [Fact]
    public void WithNoOrigin_ThePortalIsNotUsed()
    {
        var f = Finder();
        Assert.Null(f.FindPath("Map150_Fang_Cove", "85", "Map1_Crossing", "51"));
        Assert.Null(f.FindPath("Map150_Fang_Cove", "85", "Map66_Shard", "11"));
    }

    [Fact]
    public void ConnectionsRoundTripTheReturnsToOriginFlag()
    {
        var path = Path.Combine(_dir, "ZoneConnections.xml");
        var repo = new ZoneConnectionsRepository(path);
        repo.Save(new[] { Portal, new ZoneConnection { FromZone = "A", FromRoom = "1", ToZone = "B", ToRoom = "2", Verb = "x" } });

        var loaded = new ZoneConnectionsRepository(path).Load();
        Assert.True(loaded.Single(c => c.FromZone == "Map150_Fang_Cove").ReturnsToOrigin);
        Assert.False(loaded.Single(c => c.FromZone == "A").ReturnsToOrigin);
        Assert.DoesNotContain("returns-to-origin", File.ReadAllText(path).Split('\n').Single(l => l.Contains("from-zone=\"A\"")));
    }
}

public class PortalOriginTrackerTests
{
    [Fact]
    public void EnteringThroughAPortal_RemembersTheRoomYouLeft()
    {
        var t = new PortalOriginTracker();
        t.OnZoneChanged("Map1_Crossing", "50", "Map150_Fang_Cove", "go meeting portal");
        Assert.Equal(new PortalOrigin("Map1_Crossing", "50"), t.Origins["Map150_Fang_Cove"]);
    }

    [Theory]
    [InlineData("board mammoth")]
    [InlineData("north")]
    [InlineData("")]
    [InlineData(null)]
    public void ArrivingAnyOtherWay_LeavesNoOrigin(string? command)
    {
        var t = new PortalOriginTracker();
        t.OnZoneChanged("Map1_Crossing", "50", "Map150_Fang_Cove", "go meeting portal");
        t.OnZoneChanged("Map66_Shard", "10", "Map150_Fang_Cove", command);   // re-entered by boat/walking
        Assert.DoesNotContain("Map150_Fang_Cove", t.Origins.Keys);
    }

    [Fact]
    public void LeavingTheZone_ConsumesItsOrigin_AndAnEnterFromAnotherPortalReplacesIt()
    {
        var t = new PortalOriginTracker();
        t.OnZoneChanged("Map1_Crossing", "50", "Map150_Fang_Cove", "go meeting portal");
        t.OnZoneChanged("Map150_Fang_Cove", "85", "Map66_Shard", "go exit portal");
        Assert.DoesNotContain("Map150_Fang_Cove", t.Origins.Keys);
        Assert.Equal(new PortalOrigin("Map150_Fang_Cove", "85"), t.Origins["Map66_Shard"]);   // Shard's own return portal

        t.OnZoneChanged("Map66_Shard", "10", "Map150_Fang_Cove", "go meeting portal");
        Assert.Equal("Map66_Shard", t.Origins["Map150_Fang_Cove"].Zone);
    }

    [Fact]
    public void NoZoneChange_IsIgnored()
    {
        var t = new PortalOriginTracker();
        t.OnZoneChanged("Map1_Crossing", "50", "Map150_Fang_Cove", "go meeting portal");
        t.OnZoneChanged("Map150_Fang_Cove", "85", "Map150_Fang_Cove", "go exit portal");
        Assert.Equal("Map1_Crossing", t.Origins["Map150_Fang_Cove"].Zone);
    }
}
