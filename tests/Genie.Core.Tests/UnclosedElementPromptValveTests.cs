using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// 2026-08-31 stability review — <c>&lt;left&gt;</c>/<c>&lt;right&gt;</c>/<c>&lt;spell&gt;</c>/
/// <c>&lt;component&gt;</c>/<c>&lt;dynaStream&gt;</c>/<c>&lt;compass&gt;</c>
/// route all text into a side buffer until their close tag. A close tag lost
/// on the wire used to swallow every later line of game text indefinitely.
/// A server <c>&lt;prompt&gt;</c> (DR's per-message boundary) now resets them,
/// so the loss is bounded to the one message the tag was dropped in.
/// </summary>
public class UnclosedElementPromptValveTests
{
    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
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

    private static bool Shows(List<GameEvent> events, string text) =>
        events.OfType<TextEvent>().Any(e => e.Text.Contains(text, StringComparison.Ordinal));

    private const string Prompt = "<prompt time=\"1790000000\">&gt;</prompt>\n";

    [Theory]
    [InlineData("<right exist=\"1\" noun=\"jug\">whiskey jug\n")]
    [InlineData("<left exist=\"1\" noun=\"jug\">whiskey jug\n")]
    [InlineData("<spell>Shadows\n")]
    [InlineData("<component id='room objs'>You also see a rock.\n")]
    [InlineData("<dynaStream id='spells'>Shadows\n")]
    [InlineData("<compass><dir value=\"n\"/>\n")]
    public void Text_after_the_next_prompt_is_shown_when_a_close_tag_was_lost(string unclosed)
    {
        var events = Feed(
            unclosed,
            "You unlock and open your pack.\n",   // lost inside the stranded element
            Prompt,
            "A kobold arrives.\n");                // the next message must display

        Assert.True(Shows(events, "A kobold arrives."),
            "text after the prompt boundary was swallowed by the unclosed element");
    }

    [Fact]
    public void Prompt_still_fires_after_the_reset()
    {
        var events = Feed("<right exist=\"1\" noun=\"jug\">whiskey jug\n", Prompt);

        Assert.Single(events.OfType<PromptEvent>());
    }

    [Fact]
    public void The_next_hand_update_after_the_reset_parses_normally()
    {
        var events = Feed(
            "<right exist=\"1\" noun=\"jug\">whiskey jug\n",
            Prompt,
            "<right exist=\"2\" noun=\"saber\">Imperial saber</right>\n");

        var held = events.OfType<HeldItemEvent>().Single();
        Assert.Equal("saber", held.Noun);
        Assert.Equal("Imperial saber", held.Display);
    }

    [Fact]
    public void Balanced_elements_before_a_prompt_are_untouched()
    {
        var events = Feed(
            "<right exist=\"1\" noun=\"jug\">whiskey jug</right>\n",
            "<compass><dir value=\"n\"/><dir value=\"s\"/></compass>\n",
            Prompt,
            "A kobold arrives.\n");

        Assert.Equal("whiskey jug", events.OfType<HeldItemEvent>().Single().Display);
        Assert.Equal("n s", events.OfType<CompassEvent>().Single().RawXml);
        Assert.True(Shows(events, "A kobold arrives."));
    }
}
