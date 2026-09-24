using System;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #254 — map spoilers: hidden exits (search / objsearch directives, search
/// steps in a chain) and quick-send secret sequences. The rule was checked against
/// all 88 reference maps: every arc that mentions searching is caught, and the only
/// other matches are quick-send chains such as "pull sconce;-1 go door".
/// </summary>
public sealed class MapSpoilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_spoilers_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData("search go faint trail", true)]
    [InlineData("objsearch rug go trapdoor", true)]
    [InlineData("room sear;go tiny hatch", true)]
    [InlineData("search;#send 4 go shadowed opening", true)]
    [InlineData("room sear;-3knock concealed door;-whisp door pw", true)]
    [InlineData("push wall;-3 go wall", true)]              // quick-send secret sequence
    [InlineData("go door", false)]
    [InlineData("climb stairs", false)]
    [InlineData("open door;go door", false)]                // an ordinary chain gives nothing away
    [InlineData("north", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Spoiler_arcs_are_recognised(string? move, bool expected)
    {
        Assert.Equal(expected, MoveVerb.IsSpoilerMove(move));
    }

    private static MapNode Node(int id, params (string Move, int To)[] exits)
    {
        var n = new MapNode { Id = id, Title = $"Room {id}" };
        foreach (var (move, to) in exits) n.Exits.Add(new MapExit { MoveCommand = move, DestinationId = to });
        return n;
    }

    private static AutoMapperEngine Engine(params MapNode[] nodes)
    {
        var zone = new MapZone { Name = "Test" };
        foreach (var n in nodes) zone.Nodes[n.Id] = n;
        var e = new AutoMapperEngine(zone);
        e.LoadZone(zone);
        return e;
    }

    [Fact]
    public void Avoiding_spoilers_routes_around_a_hidden_exit()
    {
        var e = Engine(
            Node(1, ("search go narrow gap", 3), ("go path", 2)),
            Node(2, ("go gate", 3)),
            Node(3));
        var z = e.ActiveZone.Nodes;

        Assert.Equal(new[] { "search go narrow gap" }, e.FindPath(z[1], z[3]));
        e.AvoidSpoilerMoves = true;
        Assert.Equal(new[] { "go path", "go gate" }, e.FindPath(z[1], z[3]));
    }

    [Fact]
    public void Avoiding_spoilers_leaves_no_route_to_a_room_only_a_secret_reaches()
    {
        var e = Engine(Node(1, ("search go trapdoor", 2)), Node(2));
        e.AvoidSpoilerMoves = true;

        Assert.Null(e.FindPath(e.ActiveZone.Nodes[1], e.ActiveZone.Nodes[2]));
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieSpoilerTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    [Fact]
    public void The_settings_default_to_todays_behaviour_and_notify_the_mapper()
    {
        var cfg = NewConfig();
        Assert.True(cfg.ShowMapSpoilers);
        Assert.False(cfg.AvoidMapSpoilers);

        var seen = 0;
        cfg.ConfigChanged += f => { if (f == ConfigFieldUpdated.MapSpoilers) seen++; };
        cfg.SetSetting("showmapspoilers", "off");
        cfg.SetSetting("avoidmapspoilers", "on");

        Assert.False(cfg.ShowMapSpoilers);
        Assert.True(cfg.AvoidMapSpoilers);
        Assert.Equal(2, seen);
        Assert.Contains(GenieConfig.ConfigCategories,
            c => c.Category == "Mapper" && c.Keys.Contains("showmapspoilers") && c.Keys.Contains("avoidmapspoilers"));
    }
}
