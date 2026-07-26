using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #188 — <c>&lt;switchQuickBar id='quick-simu'/&gt;</c> is Wrayth/
/// StormFront quick-bar UI chrome with no game data. It must classify as a
/// discarded setting (not Unknown, which would fire the #audit xmlhunting
/// coverage reporter) and produce no visible output or UnknownTagEvent.
/// </summary>
public class SwitchQuickBarCoverageTests
{
    [Fact]
    public void SwitchQuickBar_classifies_as_dropped_setting()
    {
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("switchquickbar"));
        // Case-insensitive — DR sends it camelCased.
        Assert.Equal(DrXmlParser.TagFate.DroppedSetting, DrXmlParser.ClassifyTag("switchQuickBar"));
    }

    [Fact]
    public void SwitchQuickBar_is_silently_discarded_between_game_lines()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        parser.Feed("before\n<switchQuickBar id='quick-simu'/>after\n");

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
