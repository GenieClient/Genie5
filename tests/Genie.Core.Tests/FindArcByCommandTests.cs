using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>A typed `go portal` finds the arc `go exit portal`, but never guesses between two doors.</summary>
public class FindArcByCommandTests
{
    private static MapNode Node(params string[] moves)
    {
        var n = new MapNode { Id = 1, Title = "T" };
        foreach (var m in moves) n.Exits.Add(new MapExit { MoveCommand = m, ExitToken = m });
        return n;
    }

    [Fact]
    public void AnExactArcBeatsALongerOneThatAlsoFits() =>
        Assert.Equal("go portal", AutoMapperEngine.FindArcByCommand(Node("go exit portal", "go portal"), "go portal")!.MoveCommand);

    [Theory]
    [InlineData("go portal", "go exit portal")]
    [InlineData("go door", "go oak door")]
    [InlineData("climb wall", "climb stone wall")]
    public void AShorterTypedCommandMatchesTheOnlyArcThatContainsIt(string typed, string arc) =>
        Assert.Equal(arc, AutoMapperEngine.FindArcByCommand(Node(arc, "go palm cabana"), typed)!.MoveCommand);

    [Fact]
    public void TwoArcsThatBothFit_MatchNeither() =>
        Assert.Null(AutoMapperEngine.FindArcByCommand(Node("go oak door", "go iron door"), "go door"));

    [Theory]
    [InlineData("go")]                  // a bare verb is not abbreviating anything
    [InlineData("look portal")]         // a different verb
    [InlineData("go gate")]             // word not in any arc
    public void UnrelatedCommandsMatchNothing(string typed) =>
        Assert.Null(AutoMapperEngine.FindArcByCommand(Node("go exit portal"), typed));
}
