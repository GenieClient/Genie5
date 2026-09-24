using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #336 — DR's container windows. <c>&lt;inv id='stow'&gt;</c> items belong to the
/// container named by the id, not to the worn-items list the plain <c>inv</c>
/// stream carries (My Inventory). The fixture is the issue's capture shape.
/// </summary>
public class ContainerStreamParserTests
{
    private static List<GameEvent> Feed(string raw)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        parser.Feed(raw);
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

    private const string Capture =
        "<pushStream id='inv'/>Your worn items are:\n  a leather quiver\n  a gold ring\n<popStream/>" +
        "<exposeContainer id='stow'/>" +
        "<container id='stow' title=\"My Backpack\" target='#1234567' location='right' save='' resident='true'/>" +
        "<clearContainer id=\"stow\"/>" +
        "<inv id='stow'>In the backpack:</inv>" +
        "<inv id='stow'> a coil of rope</inv>" +
        "<inv id='stow'> a chamois cloth</inv>\n" +
        "You look around.\n";

    private static IEnumerable<string> On(List<GameEvent> events, string stream) =>
        events.OfType<TextEvent>().Where(e => e.Stream == stream).Select(e => e.Text.Trim());

    [Fact]
    public void Container_items_go_on_their_own_stream_and_worn_items_stay_on_inv()
    {
        var events = Feed(Capture);

        Assert.Equal(new[] { "In the backpack:", "a coil of rope", "a chamois cloth" }, On(events, "container:stow"));
        Assert.Equal(new[] { "Your worn items are:", "a leather quiver", "a gold ring" }, On(events, "inv"));
        Assert.DoesNotContain("a coil of rope", On(events, "main"));
        Assert.Contains("You look around.", On(events, "main"));   // the stream pops back after each item
    }

    [Fact]
    public void The_container_lifecycle_is_typed()
    {
        var events = Feed(Capture);

        Assert.Equal("stow", Assert.Single(events.OfType<ContainerExposeEvent>()).LogicalId);
        Assert.Equal("stow", Assert.Single(events.OfType<ContainerClearEvent>()).LogicalId);
        var c = Assert.Single(events.OfType<ContainerEvent>());
        Assert.Equal(("stow", "My Backpack"), (c.LogicalId, c.Title));

        // The clear arrives before the refill it announces.
        var order = events.FindIndex(e => e is ContainerClearEvent);
        var firstItem = events.FindIndex(e => e is TextEvent { Stream: "container:stow" });
        Assert.True(order < firstItem);
    }

    [Fact]
    public void An_inv_without_an_id_still_uses_the_inv_stream()
    {
        var events = Feed("<inv>a loose item</inv>\n");

        Assert.Equal(new[] { "a loose item" }, On(events, "inv"));
    }

    [Theory]
    [InlineData(null, "inv")]
    [InlineData("", "inv")]
    [InlineData("inv", "inv")]
    [InlineData("stow", "container:stow")]
    public void Stream_naming(string? id, string stream)
    {
        Assert.Equal(stream, ContainerStreams.For(id));
        Assert.Equal(stream == "inv" ? null : id, ContainerStreams.IdOf(stream));
    }

    [Fact]
    public void Inside_a_server_dialog_clearContainer_stays_a_dialog_control()
    {
        var events = Feed(
            "<openDialog id='befriend' title='Friends'/>" +
            "<dialogData id='befriend'><clearContainer id='list'/>" +
            "<label id='l1' value='Nobody yet' top='0' left='0'/></dialogData>\n");

        Assert.Empty(events.OfType<ContainerClearEvent>());
    }

    [Fact]
    public void The_lifecycle_tags_are_classified_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("exposeContainer"));
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("clearContainer"));
    }
}
