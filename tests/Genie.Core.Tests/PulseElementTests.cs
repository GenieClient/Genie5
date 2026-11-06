using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #379 — DR sends a self-closing <c>&lt;pulse mana="1"/&gt;</c> (first seen
/// live on beta.9.7; in no recorded session, unhandled by Genie 4 and Lich 5).
/// Before the fix the parser classified it Unknown and fired an UnknownTagEvent,
/// which `#audit xmlhunting` drafted as a coverage-gap issue on every pulse. It is
/// now consumed as a typed <see cref="PulseEvent"/> and leaks no text.
/// </summary>
public class PulseElementTests
{
    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<GameEvent> _sink;
        public Collector(List<GameEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) => _sink.Add(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    [Fact]
    public void Pulse_is_classified_as_consumed_not_unknown()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("pulse"));
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("PULSE"));
    }

    [Fact]
    public void Pulse_emits_a_typed_event_with_its_mana_attribute_and_no_unknown_tag()
    {
        var events = Feed("<pulse mana=\"1\"/>\n");

        var pulse = Assert.Single(events.OfType<PulseEvent>());
        Assert.Equal(1, pulse.Mana);
        Assert.Empty(events.OfType<UnknownTagEvent>());
    }

    [Fact]
    public void Pulse_between_text_lines_leaks_nothing_into_the_text()
    {
        var events = Feed("You feel a surge of mana.\n<pulse mana=\"1\"/>\nAnother line.\n");

        var texts = events.OfType<TextEvent>().Select(t => t.Text.TrimEnd('\r', '\n')).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("pulse", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("You feel a surge of mana.", texts);
        Assert.Contains("Another line.", texts);
    }
}
