using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #286 — DrXmlParser.Feed scans each chunk with an advancing cursor
/// instead of re-materializing and shifting a StringBuilder for every
/// segment. These pin the buffer contract the rewrite must keep: the event
/// stream does not depend on where chunk boundaries fall, an incomplete tail
/// waits for the next feed, a re-entrant feed is drained in order, and a
/// handler that throws does not lose the rest of the input.
/// </summary>
public class ParserFeedBufferTests
{
    private const string Sample =
        "<pushStream id='inv'/>a small pouch<popStream/>\n" +
        "<streamWindow id='room' title='Room' subtitle=' - [The Crossing, Hodierna Way] (10015)'/>\n" +
        "<style id='roomName'/>[The Crossing, Hodierna Way]\n<style id=''/>" +
        "<preset id='roomDesc'>A cobbled road.</preset>  Obvious paths: <d>north</d>, <d>east</d>.\n" +
        "You may roll <1-20> for damage, and a < b holds.\n" +
        "<pushBold/>A goblin<popBold/> glares &amp; snarls at you.\n" +
        "<right noun='jug'>whiskey jug</right><left>Empty</left>\n" +
        "<prompt time=\"1700000000\">&gt;</prompt>\n";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(64)]
    public void Event_stream_is_independent_of_chunk_boundaries(int size)
    {
        var whole = Serialize(FeedAndCollect(Sample));
        var chunks = Enumerable.Range(0, (Sample.Length + size - 1) / size)
            .Select(i => Sample.Substring(i * size, Math.Min(size, Sample.Length - i * size)))
            .ToArray();
        Assert.Equal(whole, Serialize(FeedAndCollect(chunks)));
    }

    [Fact]
    public void Incomplete_tag_waits_for_the_next_feed()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(events.Add);

        parser.Feed("A goblin <pushBo");
        Assert.DoesNotContain(events, e => e is TextEvent t && t.Text.Contains('<'));
        parser.Feed("ld/>snarls<popBold/>.\n");

        var text = string.Concat(events.OfType<TextEvent>().Select(e => e.Text));
        Assert.Contains("A goblin snarls.", text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public void Reentrant_feed_is_drained_after_the_outer_chunk_in_order()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var texts = new List<string>();
        bool fedBack = false;
        using var _ = parser.GameEvents.Subscribe(e =>
        {
            if (e is not TextEvent t) return;
            texts.Add(t.Text);
            if (!fedBack) { fedBack = true; parser.Feed("third line\n"); }
        });

        parser.Feed("first line\nsecond line\n");

        Assert.Equal(new[] { "first line", "second line", "third line" }, texts);
    }

    [Fact]
    public void Handler_throw_keeps_the_unconsumed_tail_for_the_next_feed()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        bool thrown = false;
        using var _ = parser.GameEvents.Subscribe(e =>
        {
            events.Add(e);
            if (e is SettingsInfoEvent && !thrown) { thrown = true; throw new InvalidOperationException("boom"); }
        });

        Assert.Throws<InvalidOperationException>(() =>
            parser.Feed("<settingsInfo crc='0' instance='DR'/>Hello there\n"));
        parser.Feed("");   // an empty feed still drains what was left

        Assert.Single(events.OfType<SettingsInfoEvent>());
        Assert.Contains(events, e => e is TextEvent t && t.Text == "Hello there");
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private static List<GameEvent> FeedAndCollect(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(events.Add);
        foreach (var chunk in chunks)
            parser.Feed(chunk);
        return events;
    }

    // Records holding lists compare by reference, so compare the serialized
    // form (runtime type + every public property) instead.
    private static List<string> Serialize(List<GameEvent> events) =>
        events.Select(e => e.GetType().Name + " " + JsonSerializer.Serialize(e, e.GetType())).ToList();
}
