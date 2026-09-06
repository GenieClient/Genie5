using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #324 — <c>&lt;dynaStream&gt;</c>, the setter companion to
/// <c>clearStream</c>, and how a #156 server dialog's streamBox gets its
/// content. The open tag in the #324 report (<c>&lt;dynaStream id='spells'&gt;</c>)
/// is the only live sighting on record; no recording holds a BODY, so the
/// body-shape tests here are constructed from the tag's role.
/// </summary>
public class DynaStreamTests
{
    // ── Parser ───────────────────────────────────────────────────────────────

    [Fact]
    public void ADynaStreamBodyBecomesATypedEvent()
    {
        var e = Assert.Single(Feed(
            "<dynaStream id='spellInfo'>Fire Shards: 3 mana</dynaStream>\n")
            .OfType<DynaStreamEvent>());

        Assert.Equal("spellInfo", e.StreamId);
        Assert.Equal("Fire Shards: 3 mana", e.Text);
    }

    [Fact]
    public void MarkupInsideTheBodyIsStrippedToText()
    {
        // The spells panel sends <a> links per spell.
        var e = Assert.Single(Feed(
            "<dynaStream id='spells'><a cmd='choose 1'>Fire Shards</a></dynaStream>\n")
            .OfType<DynaStreamEvent>());

        Assert.Equal("Fire Shards", e.Text);
    }

    [Fact]
    public void EntitiesInTheBodyAreDecoded()
    {
        var e = Assert.Single(Feed(
            "<dynaStream id='box'>Friends &amp;&amp; Enemies</dynaStream>\n")
            .OfType<DynaStreamEvent>());

        Assert.Equal("Friends && Enemies", e.Text);
    }

    [Fact]
    public void ASelfClosingDynaStreamEmitsAnEmptyBody()
    {
        var e = Assert.Single(Feed("<dynaStream id='spells'/>\n").OfType<DynaStreamEvent>());

        Assert.Equal("spells", e.StreamId);
        Assert.Equal("", e.Text);
    }

    [Fact]
    public void TheBodyDoesNotLeakIntoTheGameWindow()
    {
        // The whole point of the tag: this text belongs to a streamBox, not the
        // main output. Before #324 it fell through as unhandled and the body
        // printed into the game window.
        var events = Feed("<dynaStream id='spells'>Fire Shards</dynaStream>\n");

        Assert.DoesNotContain(events.OfType<TextEvent>(), t => t.Text.Contains("Fire Shards"));
    }

    [Fact]
    public void DynaStreamIsNoLongerReportedAsUnhandled()
    {
        // #324 exists because the coverage reporter flagged this tag.
        var events = Feed("<dynaStream id='spells'>x</dynaStream>\n");

        Assert.DoesNotContain(events.OfType<UnknownTagEvent>(),
            u => u.TagName.Equals("dynastream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClearStreamStillEmitsItsOwnEvent()
    {
        // Unchanged by #324 — clearStream and clearDynaStream already worked
        // (#220) and the inv panel plus SpellTimer's percWindow depend on them.
        var events = Feed("<clearStream id='inv'/><clearDynaStream id='spellInfo'/>\n");

        Assert.Equal(new[] { "inv", "spellInfo" },
            events.OfType<ClearStreamEvent>().Select(c => c.StreamId));
    }

    [Fact]
    public void TextAroundADynaStreamStillReachesTheGameWindow()
    {
        var events = Feed(
            "You feel ready.<dynaStream id='spells'>Fire Shards</dynaStream> The list updates.\n");

        var line = string.Concat(events.OfType<TextEvent>().Select(t => t.Text));
        Assert.Contains("You feel ready.",   line);
        Assert.Contains("The list updates.", line);
        Assert.DoesNotContain("Fire Shards", line);
    }

    // ── Engine ───────────────────────────────────────────────────────────────

    [Fact]
    public void StreamContentReachesTheDialogThatOwnsTheControl()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DialogDataEvent("spellChoose",
            [StreamBox("spellInfo")], Clear: false, "<dialogData/>"));

        engine.Observe(new DynaStreamEvent("spellInfo", "Fire Shards: 3 mana"));

        Assert.Equal("Fire Shards: 3 mana", engine.Get("spellChoose")!.Streams["spellInfo"]);
    }

    [Fact]
    public void SuccessiveBlocksAppend()
    {
        // DR's idiom elsewhere (percWindow, inv) is clear-then-send-lines, and
        // the Genie 4 plugin's streamBox renderers both append.
        var engine = new ServerDialogEngine();
        engine.Observe(new DialogDataEvent("spellChoose",
            [StreamBox("spells")], Clear: false, "<dialogData/>"));

        engine.Observe(new DynaStreamEvent("spells", "Fire Shards\n"));
        engine.Observe(new DynaStreamEvent("spells", "Tingle\n"));

        Assert.Equal("Fire Shards\nTingle\n", engine.Get("spellChoose")!.Streams["spells"]);
    }

    [Fact]
    public void ClearStreamIsTheResetBetweenRefreshes()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DialogDataEvent("spellChoose",
            [StreamBox("spells")], Clear: false, "<dialogData/>"));

        engine.Observe(new DynaStreamEvent("spells", "stale"));
        engine.ClearStream("spells");
        engine.Observe(new DynaStreamEvent("spells", "fresh"));

        Assert.Equal("fresh", engine.Get("spellChoose")!.Streams["spells"]);
    }

    [Fact]
    public void ContentArrivingBeforeTheDialogIsNotLost()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DynaStreamEvent("spells", "Fire Shards"));
        engine.Observe(new DialogDataEvent("spellChoose",
            [StreamBox("spells")], Clear: false, "<dialogData/>"));

        Assert.Equal("Fire Shards", engine.Get("spellChoose")!.Streams["spells"]);
    }

    [Fact]
    public void AnIdlessDynaStreamIsIgnored()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DynaStreamEvent("", "orphan"));

        Assert.Empty(engine.Snapshot());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DialogControl StreamBox(string id) =>
        new(DialogControlType.StreamBox, id, null, null, null, null, null, null, null, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    private sealed class Collector(List<GameEvent> sink) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent value) => sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
