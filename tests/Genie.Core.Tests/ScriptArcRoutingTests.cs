using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #253 — the Astral Plane's <c>move="script apmove"</c> arcs. Genie 4 handed
/// such directives to the community automapper.cmd, which runs the named script;
/// Genie's own walker can't, and used to send "script apmove" to the game. The
/// built-in walker now plans around them; the automapper hand-off keeps them.
/// </summary>
public class ScriptArcRoutingTests
{
    [Theory]
    [InlineData("script apmove", true)]
    [InlineData("SCRIPT ggbypass north", true)]
    [InlineData("rt script apmove", true)]       // pacing prefix looked through
    [InlineData("script", false)]
    [InlineData("script ", false)]
    [InlineData("go scriptorium", false)]
    [InlineData("north", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Script_directives_are_recognised(string? move, bool expected)
    {
        Assert.Equal(expected, MoveVerb.IsScriptMove(move));
    }

    private static MapNode Node(int id, params (string Move, int To)[] exits)
    {
        var n = new MapNode { Id = id, Title = $"Room {id}" };
        foreach (var (move, to) in exits)
            n.Exits.Add(new MapExit { MoveCommand = move, DestinationId = to });
        return n;
    }

    private static AutoMapperEngine Engine(params MapNode[] nodes)
    {
        var zone = new MapZone { Name = "Microcosm" };
        foreach (var n in nodes) zone.Nodes[n.Id] = n;
        var engine = new AutoMapperEngine(zone);
        engine.LoadZone(zone);
        return engine;
    }

    [Fact]
    public void The_built_in_walker_takes_the_longer_walkable_route()
    {
        // 1 → 3 directly by script, or 1 → 2 → 3 on foot.
        var engine = Engine(
            Node(1, ("script apmove", 3), ("go gate", 2)),
            Node(2, ("go arch", 3)),
            Node(3));

        Assert.Equal(new[] { "script apmove" }, engine.FindPath(engine.Nodes()[1], engine.Nodes()[3]));
        Assert.Equal(new[] { "go gate", "go arch" },
                     engine.FindPath(engine.Nodes()[1], engine.Nodes()[3], allowScriptMoves: false));
    }

    [Fact]
    public void With_only_a_scripted_leg_the_built_in_walker_finds_no_path()
    {
        var engine = Engine(Node(1, ("script apmove", 2)), Node(2));

        Assert.Null(engine.FindPath(engine.Nodes()[1], engine.Nodes()[2], allowScriptMoves: false));
        Assert.Equal(new[] { "script apmove" }, engine.FindPath(engine.Nodes()[1], engine.Nodes()[2]));
    }

    [Fact]
    public void A_self_arc_is_never_part_of_a_route()
    {
        var engine = Engine(Node(1, ("script apmove", 1), ("north", 2)), Node(2));

        Assert.Equal(new[] { "north" }, engine.FindPath(engine.Nodes()[1], engine.Nodes()[2]));
    }
}

internal static class EngineTestExtensions
{
    public static IReadOnlyDictionary<int, MapNode> Nodes(this AutoMapperEngine engine) => engine.ActiveZone.Nodes;
}
