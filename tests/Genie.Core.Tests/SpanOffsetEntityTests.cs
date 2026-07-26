using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #199 — bold/preset/link span offsets were recorded against the raw
/// <c>_textLineBuffer</c> but applied to the decoded text, so any HTML entity
/// (or tag) BEFORE a span shifted it right in the rendered line. DR prefixes
/// combat lines with a literal <c>&amp;lt;</c> (<c>&lt;</c>, 4 raw chars → 1),
/// which pushed the damage-text bold three characters into the next sentence.
/// EmitLine now rebases every span into decoded-text space.
/// </summary>
public class SpanOffsetEntityTests
{
    private static List<TextEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new C(events));
        foreach (var c in chunks) parser.Feed(c);
        return events.OfType<TextEvent>().ToList();
    }
    private sealed class C : IObserver<GameEvent>
    { private readonly List<GameEvent> s; public C(List<GameEvent> x) => s = x;
      public void OnNext(GameEvent e) => s.Add(e); public void OnError(Exception e) { } public void OnCompleted() { } }

    [Fact]
    public void Combat_bold_lands_on_the_damage_text_not_offset()
    {
        // The scenario from the issue: a combat line that opens with `&lt;`
        // before the <pushBold/> damage phrase.
        var texts = Feed(
            "<pushStream id=\"combat\" />" +
            "&lt; You feint a cutlass at a scout ogre.  A scout ogre attempts to dodge.  " +
            "<pushBold/>The cutlass lands a strong hit to the ogre's chest.<popBold/>" +
            "[You're nimbly balanced with no advantage.]\n" +
            "<popStream id=\"combat\" />");

        var line = texts.First(t => t.Text.Contains("The cutlass lands"));

        // The entity decoded to a single '<'.
        Assert.StartsWith("< You feint", line.Text);

        // Exactly one bold span, and it covers the damage sentence — not shifted
        // into the trailing "[You're nimbly balanced …]".
        var bold = Assert.Single(line.BoldSpans!);
        Assert.Equal(
            "The cutlass lands a strong hit to the ogre's chest.",
            line.Text.Substring(bold.Start, bold.Length));
    }

    [Fact]
    public void Ampersand_before_bold_does_not_shift_the_span()
    {
        // `&amp;` is 5 raw chars → 1 decoded; without rebasing the bold would
        // start four characters late.
        var texts = Feed("Sword &amp; Board <pushBold/>bold<popBold/> tail\n");
        var line = texts.First(t => t.Text.Contains("bold"));

        Assert.Equal("Sword & Board bold tail", line.Text);
        var bold = Assert.Single(line.BoldSpans!);
        Assert.Equal("bold", line.Text.Substring(bold.Start, bold.Length));
    }

    [Fact]
    public void Preset_span_after_entities_is_rebased_too()
    {
        // The fix is holistic — preset spans drift on the same entity math.
        var texts = Feed("&lt;&lt; <preset id='thought'>a stray musing</preset>\n");
        var line = texts.First(t => t.Text.Contains("musing"));

        Assert.Equal("<< a stray musing", line.Text);
        var preset = Assert.Single(line.PresetSpans!);
        Assert.Equal("a stray musing", line.Text.Substring(preset.Start, preset.Length));
        Assert.Equal("thought", preset.PresetId);
    }
}
