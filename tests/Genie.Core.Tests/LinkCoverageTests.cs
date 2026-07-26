using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #198 — <c>&lt;link id='1' value='Game Info' cmd='url:/dr/info/'/&gt;</c>
/// is a server-defined menu/nav link (it populates the client's Game/Help menus
/// with URLs). It is UI chrome with no game-state data — distinct from the
/// in-text clickable <c>&lt;a&gt;</c>/<c>&lt;d&gt;</c> links, which are handled.
/// It must classify as a discarded setting (not Unknown, which would fire the
/// #audit xmlhunting coverage reporter) and produce no visible output.
/// </summary>
public class LinkCoverageTests
{
    [Fact]
    public void Link_classifies_as_dropped_setting()
    {
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("link"));
        // Case-insensitive — attribute casing/element casing varies across streams.
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("LINK"));
    }

    [Fact]
    public void Link_is_silently_discarded_between_game_lines()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        // The exact sample from the issue, wrapped in surrounding game text.
        parser.Feed("before\n<link id='1' value='Game Info' cmd='url:/dr/info/' />after\n");

        // No unknown-tag report, and the surrounding text is untouched — in
        // particular the value/cmd attributes must not leak into the stream.
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        var texts = events.OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains("before", texts);
        Assert.Contains("after", texts);
        Assert.DoesNotContain(texts, t => t.Contains("Game Info"));
        Assert.DoesNotContain(texts, t => t.Contains("dr/info"));
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
