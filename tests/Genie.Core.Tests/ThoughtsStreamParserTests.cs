using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Ground truth for the ESP / thought-net stream, pinned to the only live
/// capture of it we hold — <c>raw_session_20260521_200919.xml</c> lines 324-325.
/// Unlike talk/whispers, DR sends a thought ONCE (on the <c>thoughts</c> stream
/// only); the bare <c>main</c> line that follows the <c>&lt;popStream/&gt;</c> is the
/// separate "You concentrate on projecting your thoughts." confirmation, not a
/// duplicate echo — so nothing here may be flagged
/// <see cref="TextEvent.DuplicateEcho"/>.
/// </summary>
public class ThoughtsStreamParserTests
{
    private static List<TextEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new C(events));
        foreach (var c in chunks) parser.Feed(c);
        return events.OfType<TextEvent>().ToList();
    }
    private sealed class C(List<GameEvent> s) : IObserver<GameEvent>
    { public void OnNext(GameEvent e) => s.Add(e); public void OnError(Exception e) { } public void OnCompleted() { } }

    [Fact]
    public void Outbound_thought_keeps_attribution_and_body_on_one_line()
    {
        var texts = Feed(
            "<pushStream id=\"thoughts\"/><preset id='thought'>[General] You hear your mental voice echo, </preset>\"Hello Fellow travelers\"\n",
            "<popStream/>You concentrate on projecting your thoughts.\n",
            "<prompt time=\"1779408649\">&gt;</prompt>");

        // The quoted body sits OUTSIDE </preset> on the same raw line — same
        // shape as whisper/speech, so </preset> must NOT flush (see the preset
        // flush rule: roomDesc and inv only). A flush would split this in two.
        var thought = Assert.Single(texts, t => t.Stream == "thoughts");
        Assert.Equal("[General] You hear your mental voice echo, \"Hello Fellow travelers\"", thought.Text);
        Assert.False(thought.DuplicateEcho);

        // The preset span covers the attribution only, and carries the raw XML
        // id "thought" — DefaultHighlights.MapPresetKey remaps it to the
        // "thoughts" palette key at render time.
        var span = Assert.Single(thought.PresetSpans!);
        Assert.Equal("thought", span.PresetId);
        Assert.Equal(0, span.Start);
        Assert.Equal("[General] You hear your mental voice echo, ".Length, span.Length);

        // The post-pop line is the send confirmation, on main, not an echo.
        var main = Assert.Single(texts, t => t.Stream == "main");
        Assert.Equal("You concentrate on projecting your thoughts.", main.Text);
        Assert.False(main.DuplicateEcho);
    }
}
