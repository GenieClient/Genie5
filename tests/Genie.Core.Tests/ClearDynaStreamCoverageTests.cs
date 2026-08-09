using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #220 — <c>&lt;clearDynaStream id='spellInfo'/&gt;</c> is the
/// dynamic-stream variant of <c>&lt;clearStream&gt;</c>: same clear-this-window
/// meaning, so it must emit the same <see cref="ClearStreamEvent"/> (not fall
/// through to Unknown, which fires the #audit xmlhunting coverage reporter).
/// </summary>
public class ClearDynaStreamCoverageTests
{
    [Fact]
    public void ClearDynaStream_classifies_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("cleardynastream"));
        // Case-insensitive — DR sends it camelCased.
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("clearDynaStream"));
    }

    [Fact]
    public void ClearDynaStream_emits_clear_stream_event()
    {
        // Verbatim sample from the #220 report.
        var events = FeedAndCollect("<clearDynaStream id='spellInfo'/>");

        var ev = Assert.Single(events.OfType<ClearStreamEvent>());
        Assert.Equal("spellInfo", ev.StreamId);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void ClearStream_still_emits_clear_stream_event()
    {
        // The shared case must keep the original tag working.
        var events = FeedAndCollect("<clearStream id='percWindow'/>");

        var ev = Assert.Single(events.OfType<ClearStreamEvent>());
        Assert.Equal("percWindow", ev.StreamId);
    }

    [Fact]
    public void ClearDynaStream_without_id_emits_empty_stream_id()
    {
        // Defensive: a missing id clears nothing but must not throw or go Unknown.
        var events = FeedAndCollect("<clearDynaStream/>");

        var ev = Assert.Single(events.OfType<ClearStreamEvent>());
        Assert.Equal("", ev.StreamId);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    private static List<GameEvent> FeedAndCollect(string xml)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        parser.Feed(xml);
        return events;
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
