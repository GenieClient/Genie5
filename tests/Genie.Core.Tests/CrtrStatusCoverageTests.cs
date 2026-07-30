using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #202 (dup #201) — <c>&lt;crtrStatus exist=… hostile=… disengaged=…
/// flying=…/&gt;</c> is per-creature combat status. It must be consumed into a
/// typed <see cref="CreatureStatusEvent"/> (not Unknown, which fires the #audit
/// xmlhunting coverage reporter) and applied to
/// <c>GameState.Combat.CreatureStatuses</c>, keyed by exist id and cleared on
/// room change.
/// </summary>
public class CrtrStatusCoverageTests
{
    [Fact]
    public void CrtrStatus_classifies_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("crtrstatus"));
        // Case-insensitive — DR sends it camelCased.
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("crtrStatus"));
    }

    [Fact]
    public void CrtrStatus_emits_event_with_all_flags()
    {
        var events = FeedAndCollect("<crtrStatus exist=\"91586721\" hostile=\"0\" disengaged=\"1\" flying=\"1\"/>");

        var ev = Assert.Single(events.OfType<CreatureStatusEvent>());
        Assert.Equal("91586721", ev.ExistId);
        Assert.False(ev.Hostile);
        Assert.True(ev.Disengaged);
        Assert.True(ev.Flying);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void CrtrStatus_defaults_missing_flag_to_false()
    {
        // The #201 sample has no flying attribute — it must read as false, not throw.
        var events = FeedAndCollect("<crtrStatus exist=\"19267\" hostile=\"1\" disengaged=\"0\"/>");

        var ev = Assert.Single(events.OfType<CreatureStatusEvent>());
        Assert.True(ev.Hostile);
        Assert.False(ev.Disengaged);
        Assert.False(ev.Flying);
    }

    [Fact]
    public void CrtrStatus_without_exist_is_dropped_not_unknown()
    {
        // No exist id = nothing to key on. Drop it silently — and it must not
        // fall through to the unknown-tag coverage reporter.
        var events = FeedAndCollect("<crtrStatus hostile=\"1\" disengaged=\"0\"/>");

        Assert.Empty(events.OfType<CreatureStatusEvent>());
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void CrtrStatus_updates_game_state_keyed_by_exist()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        using var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state,
            NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);

        parser.Feed("<crtrStatus exist=\"555\" hostile=\"1\" disengaged=\"0\" flying=\"0\"/>");
        Assert.True(state.Combat.CreatureStatuses.ContainsKey("555"));
        var s = state.Combat.CreatureStatuses["555"];
        Assert.True(s.Hostile);
        Assert.False(s.Disengaged);

        // A later reading for the same creature overwrites in place (disengaged now).
        parser.Feed("<crtrStatus exist=\"555\" hostile=\"1\" disengaged=\"1\" flying=\"0\"/>");
        Assert.True(state.Combat.CreatureStatuses["555"].Disengaged);
        Assert.Single(state.Combat.CreatureStatuses);
    }

    [Fact]
    public void CrtrStatus_is_cleared_on_room_change()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        using var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state,
            NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);

        parser.Feed("<crtrStatus exist=\"555\" hostile=\"1\" disengaged=\"0\"/>");
        Assert.Single(state.Combat.CreatureStatuses);

        // Moving to a different room drops the old room's creatures — engagement
        // can't survive a room change.
        parser.Feed("<nav rm='12345'/>");
        Assert.Empty(state.Combat.CreatureStatuses);
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
