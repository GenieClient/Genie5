using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156 — the engine keeps streamBox link spans alongside the text:
/// appended blocks move their spans along, and a clear or replace drops them.
/// </summary>
public class ServerDialogStreamLinkTests
{
    private static ServerDialogEngine WithSpells()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DialogDataEvent("spellChoose",
            [new DialogControl(DialogControlType.StreamBox, "spells", null, null, null,
                               "15", "40", "250", "380", null,
                               new System.Collections.Generic.Dictionary<string, string>())],
            Clear: false, "<dialogData/>"));
        return engine;
    }

    [Fact]
    public void AnAppendedBlocksSpansAreRebasedOntoTheWholeText()
    {
        var engine = WithSpells();
        engine.Observe(new DynaStreamEvent("spells", "Fire Shards\n", [new LinkSpan(0, 11, "choose 1")]));
        engine.Observe(new DynaStreamEvent("spells", "Aether Wolves\n", [new LinkSpan(0, 13, "choose 2")]));

        var state = engine.Get("spellChoose")!;
        var text  = state.Streams["spells"];
        var links = state.StreamLinks["spells"];

        Assert.Equal(2, links.Count);
        Assert.Equal("Aether Wolves", text.Substring(links[1].Start, links[1].Length));
    }

    [Fact]
    public void ClearingTheStreamDropsItsLinks()
    {
        var engine = WithSpells();
        engine.Observe(new DynaStreamEvent("spells", "Fire Shards", [new LinkSpan(0, 11, "choose 1")]));
        engine.ClearStream("spells");
        engine.Observe(new DynaStreamEvent("spells", "plain"));

        Assert.False(engine.Get("spellChoose")!.StreamLinks.ContainsKey("spells"));
    }

    [Fact]
    public void ReplacingTheStreamDropsItsLinks()
    {
        var engine = WithSpells();
        engine.Observe(new DynaStreamEvent("spells", "Fire Shards", [new LinkSpan(0, 11, "choose 1")]));
        engine.SetStream("spells", "replaced");

        Assert.False(engine.Get("spellChoose")!.StreamLinks.ContainsKey("spells"));
    }

    [Theory]
    [InlineData("Injury2", InjuryKind.Wound,  2)]
    [InlineData("Scar1",   InjuryKind.Scar,   1)]
    [InlineData("Nsys3",   InjuryKind.Damage, 3)]
    [InlineData("Nsys0",   InjuryKind.None,   0)]
    [InlineData("nsys",    InjuryKind.None,   0)]
    [InlineData("abdomen", InjuryKind.None,   0)]
    [InlineData(null,      InjuryKind.None,   0)]
    public void InjuryImageNamesDecodeAsThePlayersOwnPanelDoes(string? name, InjuryKind kind, int severity)
    {
        Assert.Equal((kind, severity), InjuryImageName.Decode(name));
    }
}
