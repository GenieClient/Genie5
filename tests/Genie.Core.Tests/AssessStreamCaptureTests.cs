using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #313 — the live assess-stream capture, committed as a fixture.
/// <para>
/// Captured by simtel12 in a live session and posted to #genie-5-development on
/// 2026-08-14, with the proposal that the Mobs panel and the Assess window be
/// fed structured per-creature rows joined from the assess stream and
/// <c>&lt;crtrStatus&gt;</c>. It lived only in Discord scrollback and an
/// untracked scratch note until this file; #313 quotes the same bytes.
/// </para>
/// <para>
/// Nothing in #313 is built yet. This class does NOT test the feature — it
/// pins what today's parser already hands a future Mobs/Assess panel, so the
/// raw material survives in the repo and a regression in it shows up here
/// rather than in whoever picks #313 up. The finding worth recording: the
/// join #313 asks for needs no new parser work. Every assess line already
/// arrives as its own <see cref="TextEvent"/> on the <c>assess</c> stream, in
/// the game's own assess order, carrying the creature's exist id in its
/// <see cref="LinkSpan"/>s — the same ids <c>&lt;crtrStatus&gt;</c> keys
/// <c>GameState.Combat.CreatureStatuses</c> by. The gap is entirely in the UI
/// layer: line parsing (balance / position / range), the join, and the rows.
/// </para>
/// <para>
/// Related: <see cref="CrtrStatusCoverageTests"/> (public #202) pins the
/// <c>&lt;crtrStatus&gt;</c> half of that join.
/// </para>
/// </summary>
public class AssessStreamCaptureTests
{
    /// <summary>
    /// simtel12's capture, verbatim, in DR's CRLF wire framing.
    /// <para>
    /// Four creatures, numbered in assess order, each line linking
    /// <c>look #ID</c> on the name and <c>face #ID</c> on the trailing "F".
    /// Note the leading <c>pushStream</c>+<c>clearStream</c> pair, the blank
    /// line after the header, and that every line re-pushes the stream after a
    /// <c>popStream</c> — the block is not one continuous push.
    /// </para>
    /// </summary>
    private const string Capture =
        "<pushStream id=\"assess\"/><clearStream id=\"assess\"/>You assess your combat situation...\r\n" +
        "\r\n" +
        "<popStream/><pushStream id=\"assess\"/>You (solidly balanced) are facing <d cmd='look #45029699'>a sleazy lout</d> (1) at pole weapon range.\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029699'>A sleazy lout</d> (1: nimbly balanced) is facing you at pole weapon range. | <d cmd='face #45029699'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029702'>A sleazy lout</d> (2: solidly balanced) is behind you at pole weapon range. | <d cmd='face #45029702'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029705'>A sleazy lout</d> (3: badly balanced) is behind you at pole weapon range. | <d cmd='face #45029705'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/><d cmd='look #45029711'>A sleazy lout</d> (4: badly balanced) is moving to flank you at pole weapon range. | <d cmd='face #45029711'>F</d>\r\n" +
        "<popStream/><pushStream id=\"assess\"/>\r\n";

    [Fact]
    public void Capture_produces_no_unknown_tags()
    {
        // Nothing in an assess block should reach the #audit xmlhunting reporter.
        Assert.Empty(FeedAndCollect(Capture).OfType<UnknownTagEvent>());
    }

    [Fact]
    public void Every_line_lands_on_the_assess_stream_and_none_on_main()
    {
        var text = FeedAndCollect(Capture).OfType<TextEvent>().ToList();

        Assert.All(text, t => Assert.Equal("assess", t.Stream));
        Assert.DoesNotContain(text, t => t.Stream == "main");
    }

    [Fact]
    public void Lines_arrive_individually_in_assess_order()
    {
        var text = FeedAndCollect(Capture).OfType<TextEvent>().Select(t => t.Text).ToList();

        // The header, the blank line DR sends after it (emitted, not swallowed —
        // a panel decides whether to render it), then one event per creature in
        // the game's own numbering. The trailing bare pushStream contributes
        // nothing.
        Assert.Equal(new[]
        {
            "You assess your combat situation...",
            "",
            "You (solidly balanced) are facing a sleazy lout (1) at pole weapon range.",
            "A sleazy lout (1: nimbly balanced) is facing you at pole weapon range. | F",
            "A sleazy lout (2: solidly balanced) is behind you at pole weapon range. | F",
            "A sleazy lout (3: badly balanced) is behind you at pole weapon range. | F",
            "A sleazy lout (4: badly balanced) is moving to flank you at pole weapon range. | F",
        }, text);
    }

    [Fact]
    public void Block_opens_with_a_push_and_a_clear()
    {
        // A Mobs/Assess panel rebuilds its rows from scratch on each assess —
        // clearStream is the signal, and it must precede the first line.
        var events = FeedAndCollect(Capture);

        var clearIndex = events.FindIndex(e => e is ClearStreamEvent { StreamId: "assess" });
        var firstText  = events.FindIndex(e => e is TextEvent);

        Assert.True(clearIndex >= 0, "assess block must carry a clearStream");
        Assert.True(clearIndex < firstText, "clearStream must arrive before the first line");
        Assert.IsType<StreamPushEvent>(events[0]);
    }

    [Fact]
    public void Exist_ids_are_recoverable_from_the_link_spans_in_assess_order()
    {
        var text = FeedAndCollect(Capture).OfType<TextEvent>().Where(t => t.Links is { Count: > 0 }).ToList();

        // This is the join key #313 needs: the id in `look #ID` is the same id
        // <crtrStatus exist=…> reports. First line is the "You are facing …"
        // header, which links only the creature you face.
        var lookIds = text
            .Select(t => t.Links!.First(l => l.Command.StartsWith("look #", StringComparison.Ordinal)))
            .Select(l => l.Command["look #".Length..])
            .ToList();

        Assert.Equal(new[] { "45029699", "45029699", "45029702", "45029705", "45029711" }, lookIds);
    }

    [Fact]
    public void Link_spans_cover_the_creature_name_and_the_face_control()
    {
        var text = FeedAndCollect(Capture).OfType<TextEvent>().Where(t => t.Links is { Count: > 0 }).ToList();

        // Offsets are into the tag-stripped Text, so a renderer can underline
        // the name and dispatch the command on click. Slice rather than pin
        // literal offsets — the spans are what matters, not their arithmetic.
        foreach (var t in text)
        {
            var look = t.Links!.Single(l => l.Command.StartsWith("look #", StringComparison.Ordinal));
            Assert.Equal("sleazy lout", t.Text.Substring(look.Start, look.Length)[2..]);
            Assert.False(look.IsUrl);

            var face = t.Links!.FirstOrDefault(l => l.Command.StartsWith("face #", StringComparison.Ordinal));
            if (face is null) continue;  // the header line has no face control
            Assert.Equal("F", t.Text.Substring(face.Start, face.Length));
            Assert.Equal(look.Command["look #".Length..], face.Command["face #".Length..]);
        }
    }

    [Fact]
    public void Ids_join_against_live_crtrStatus_flags()
    {
        // The other half of #313: the same ids key the per-creature combat
        // flags, so a row can show hostile/disengaged/flying beside the
        // assess-order balance and position text.
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        using var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state,
            NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);

        parser.Feed("<crtrStatus exist=\"45029699\" hostile=\"1\" disengaged=\"0\" flying=\"0\"/>");
        parser.Feed("<crtrStatus exist=\"45029711\" hostile=\"1\" disengaged=\"1\" flying=\"0\"/>");
        parser.Feed(Capture);

        // An assess block reports on the same room, so it must not disturb the
        // statuses collected before it (only a room change clears those).
        Assert.True(state.Combat.CreatureStatuses.ContainsKey("45029699"));
        Assert.True(state.Combat.CreatureStatuses["45029711"].Disengaged);
    }

    [Fact]
    public void Lf_only_framing_parses_identically()
    {
        // The capture was transcribed out of Discord with LF endings; DR sends
        // CRLF. Both must yield the same lines, so a fixture taken from either
        // source is usable.
        var crlf = FeedAndCollect(Capture).OfType<TextEvent>().Select(t => t.Text);
        var lf   = FeedAndCollect(Capture.Replace("\r\n", "\n")).OfType<TextEvent>().Select(t => t.Text);

        Assert.Equal(crlf, lf);
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
