using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #208 — <c>&lt;forging outfitting engineering alchemy enchanting
/// custom={set}&gt;</c> is Wrayth's crafting-UI config from the settings dump
/// (value-less flag attributes and a <c>{set}</c> preset placeholder), not live
/// crafting state. It must classify as a discarded setting — not Unknown, which
/// would fire the #audit xmlhunting coverage reporter — and produce no visible
/// output or UnknownTagEvent even though the raw form is not well-formed XML.
/// </summary>
public class ForgingCoverageTests
{
    [Fact]
    public void Forging_classifies_as_dropped_setting()
    {
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("forging"));
        // Case-insensitive — DR may send it camel/upper-cased.
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("Forging"));
    }

    [Fact]
    public void Forging_is_silently_discarded_between_game_lines()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        // The exact (malformed) sample from #208: value-less attributes and an
        // unquoted {set} placeholder. XmlReader rejects it; the manual fallback
        // still scrapes the "forging" name, which the settings-tag skip discards.
        parser.Feed("before\n<forging outfitting engineering alchemy enchanting custom={set}>after\n");

        // No unknown-tag report, and the surrounding text is untouched.
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        var texts = events.OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains("before", texts);
        Assert.Contains("after", texts);
    }

    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<GameEvent> _sink;
        public Collector(List<GameEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) => _sink.Add(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
