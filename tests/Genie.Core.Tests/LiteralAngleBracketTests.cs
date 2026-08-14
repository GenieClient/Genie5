using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #238 — the parser treated EVERY '&lt;' as a tag opener and consumed
/// through the next '&gt;', so angle-bracket literals in game text — dice
/// notation ("&lt;1-20&gt;"), speech ("I &lt;3 you"), comparisons
/// ("a &lt; b") — silently disappeared from output (the #audit xmlhunting
/// reporter caught "&lt;1-20&gt;" live as a bogus unknown element named "1").
/// A '&lt;' now opens a tag only when followed by a legal tag start: a name
/// character (letter/'_'), "&lt;/x", "&lt;!", or "&lt;?". Anything else is
/// literal text. The decision needs one char of lookahead (two for "&lt;/"),
/// so the split-across-chunks cases are locked down here too.
/// </summary>
public class LiteralAngleBracketTests
{
    // ── literals preserved ──────────────────────────────────────────────────

    [Theory]
    [InlineData("You may roll <1-20> for damage.")]   // the reported repro
    [InlineData("Naper says, \"I <3 elves.\"")]        // digit after '<'
    [InlineData("The formula reads a < b here.")]      // space after '<'
    [InlineData("Strange runes spell out <> twice.")]  // empty pair
    [InlineData("A sign lists <*special*> deals.")]    // punctuation after '<'
    public void Angle_bracket_literals_survive_to_text(string line)
    {
        var events = FeedAndCollect(line + "\n");
        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains(line, text);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    [Fact]
    public void Reported_repro_is_not_an_unknown_element()
    {
        // #238 was filed by the xmlhunting coverage reporter seeing "<1-20>" as
        // an unknown element named "1" — that misclassification is the bug.
        var events = FeedAndCollect("<1-20>\n");
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains("<1-20>", text);
    }

    // ── chunk-boundary lookahead ────────────────────────────────────────────

    [Fact]
    public void Literal_split_after_the_open_bracket_survives()
    {
        // The classifier needs the char AFTER '<'; a chunk ending exactly at
        // '<' must wait, then resume as literal text when "1-20>" arrives.
        var events = FeedAndCollect("You may roll <", "1-20> now.\n");
        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains("You may roll <1-20> now.", text);
    }

    [Fact]
    public void Closing_tag_split_after_the_slash_still_parses()
    {
        // "</" at a chunk boundary needs a second lookahead char; the closing
        // tag must still close (bold here), not degrade to literal text.
        var events = FeedAndCollect("<pushBold/>troll<popBold/> ahead", "\n");
        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains("troll ahead", text);
        Assert.DoesNotContain("<", text);
    }

    // ── real tags unaffected ────────────────────────────────────────────────

    [Fact]
    public void Real_tags_still_parse_around_literals()
    {
        var events = FeedAndCollect("<pushBold/>a troll<popBold/> rolls <1-20> at you.\n");
        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains("a troll rolls <1-20> at you.", text);
        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static List<GameEvent> FeedAndCollect(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks)
            parser.Feed(chunk);
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
