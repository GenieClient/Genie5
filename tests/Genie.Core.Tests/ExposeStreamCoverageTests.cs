using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #310 — <c>&lt;exposeStream id='ShopWindow'/&gt;</c> is the
/// stream-window counterpart of <c>&lt;exposeDialog&gt;</c>: DR naming a window
/// to raise. It carries no content, so it was falling through to Unknown and
/// firing the #audit xmlhunting coverage reporter on a tag we understand.
/// </summary>
public class ExposeStreamCoverageTests
{
    [Fact]
    public void ExposeStream_classifies_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("exposestream"));
        // Case-insensitive — DR sends it camelCased.
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("exposeStream"));
    }

    [Fact]
    public void ExposeStream_emits_a_typed_event_and_not_an_unknown_tag()
    {
        // Verbatim sample from the #310 report.
        var events = FeedAndCollect("<exposeStream id='ShopWindow'/>");

        var ev = Assert.Single(events.OfType<ExposeStreamEvent>());
        Assert.Equal("ShopWindow", ev.Id);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void ExposeStream_without_an_id_is_ignored_rather_than_unknown()
    {
        // Defensive: nothing to raise, but it must not throw or reopen the gap.
        var events = FeedAndCollect("<exposeStream/>");

        Assert.Empty(events.OfType<ExposeStreamEvent>());
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void ExposeDialog_still_emits_its_own_event()
    {
        // The dialog lifecycle must be untouched by the stream addition.
        var events = FeedAndCollect("<exposeDialog id='Bank'/>");

        Assert.Equal("Bank", Assert.Single(events.OfType<ExposeDialogEvent>()).Id);
        Assert.Empty(events.OfType<ExposeStreamEvent>());
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
