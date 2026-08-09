using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// DR sends a talk/whispers line twice on the wire — once inside
/// <c>&lt;pushStream id="talk"&gt;…&lt;popStream/&gt;</c>, then immediately again as
/// bare <c>main</c> text right after the pop (confirmed via a live capture).
/// <see cref="DrXmlParser"/> tags the bare re-send with
/// <see cref="TextEvent.DuplicateEcho"/> rather than dropping it — Core
/// consumers (triggers under <c>ParseGameOnly</c>, scripts, plugins) still
/// need the event exactly as DR sent it; only a display sink should skip it.
/// See also <see cref="GenieCore.ProcessGameTextEvent"/>'s trigger gate,
/// which is the consumer this flag exists for.
/// </summary>
public class DuplicateEchoParserTests
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
    public void Talk_pair_flags_the_bare_main_copy_not_the_stream_copy()
    {
        var texts = Feed(
            "<pushStream id=\"talk\"/>You say, \"Look here.\"\n" +
            "<popStream/>You say, \"Look here.\"\n" +
            "<prompt time=\"1\">&gt;</prompt>\n");

        var talk = Assert.Single(texts, t => t.Stream == "talk");
        var main = Assert.Single(texts, t => t.Stream == "main");
        Assert.False(talk.DuplicateEcho);
        Assert.True(main.DuplicateEcho);
        Assert.Equal(talk.Text, main.Text);
    }

    [Fact]
    public void Two_talk_pairs_in_a_row_both_flag_correctly()
    {
        // Public issue reviewer's "re-arm" case: the same line said twice in a
        // row must not confuse the immediate-adjacency tracking — each pair's
        // bare copy is flagged independently.
        var texts = Feed(
            "<pushStream id=\"talk\"/>You say, \"Hi.\"\n<popStream/>You say, \"Hi.\"\n" +
            "<prompt time=\"1\">&gt;</prompt>\n" +
            "<pushStream id=\"talk\"/>You say, \"Hi.\"\n<popStream/>You say, \"Hi.\"\n" +
            "<prompt time=\"2\">&gt;</prompt>\n");

        var mains = texts.Where(t => t.Stream == "main" && t.Text == "You say, \"Hi.\"").ToList();
        Assert.Equal(2, mains.Count);
        Assert.All(mains, m => Assert.True(m.DuplicateEcho));

        var talks = texts.Where(t => t.Stream == "talk").ToList();
        Assert.Equal(2, talks.Count);
        Assert.All(talks, t => Assert.False(t.DuplicateEcho));
    }

    [Fact]
    public void Interleaved_line_defeats_the_match()
    {
        // A real main-stream line lands between the stream copy and what would
        // otherwise be the bare duplicate — the adjacency requirement means
        // the later identical line is NOT flagged (it isn't DR's echo of the
        // talk line anymore, so a display sink should render it normally).
        var texts = Feed(
            "<pushStream id=\"talk\"/>You say, \"Hi.\"\n<popStream/>" +
            "Something else happens.\n" +
            "You say, \"Hi.\"\n" +
            "<prompt time=\"1\">&gt;</prompt>\n");

        var main = Assert.Single(texts, t => t.Stream == "main" && t.Text == "You say, \"Hi.\"");
        Assert.False(main.DuplicateEcho);
    }

    [Fact]
    public void Two_identical_main_only_lines_are_never_flagged()
    {
        // No talk/whispers stream involved at all — two genuinely identical
        // main lines (e.g. a repeated room-flavor line) must not false-positive.
        var texts = Feed("The wind howls.\nThe wind howls.\n<prompt time=\"1\">&gt;</prompt>\n");

        var mains = texts.Where(t => t.Stream == "main" && t.Text == "The wind howls.").ToList();
        Assert.Equal(2, mains.Count);
        Assert.All(mains, m => Assert.False(m.DuplicateEcho));
    }

    [Fact]
    public void Combat_stream_bare_repeat_is_not_flagged()
    {
        // The duplicate-echo pattern is scoped to the confirmed talk/whispers
        // double-send set — combat (or any other stream) coincidentally
        // repeating text on main must not be swept up by a wider net.
        var texts = Feed(
            "<pushStream id=\"combat\"/>A clean hit!\n<popStream/>A clean hit!\n" +
            "<prompt time=\"1\">&gt;</prompt>\n");

        var main = Assert.Single(texts, t => t.Stream == "main" && t.Text == "A clean hit!");
        Assert.False(main.DuplicateEcho);
    }

    [Fact]
    public void Whispers_pair_is_also_flagged()
    {
        var texts = Feed(
            "<pushStream id=\"whispers\"/>You whisper, \"psst.\"\n<popStream/>You whisper, \"psst.\"\n" +
            "<prompt time=\"1\">&gt;</prompt>\n");

        var main = Assert.Single(texts, t => t.Stream == "main" && t.Text == "You whisper, \"psst.\"");
        Assert.True(main.DuplicateEcho);
    }
}
