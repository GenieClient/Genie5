using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Genie.Core.Connection;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The LIVE path's line splitter, <see cref="GameConnection.EmitChunks"/>, used to
/// drop every whitespace-only chunk. A blank line arrives as exactly that (its own
/// "\n" chunk), so blank lines never reached the parser on a real connection and
/// the #176 blank-line preservation in <c>DrXmlParser.EmitLine</c> had nothing to
/// preserve — INFO/LOOK/HELP spacing collapsed in the game window.
///
/// <see cref="BlankLinePreservationTests"/> could not catch this: it calls
/// <c>parser.Feed(...)</c> directly, as does <c>TestHarness</c> REPLAY. These tests
/// deliberately run the bytes through the chunker FIRST, so they cover the seam the
/// other suites skip.
/// </summary>
public class BlankLineChunkingTests
{
    private sealed class Collector<T> : IObserver<T>
    {
        private readonly List<T> _sink;
        public Collector(List<T> sink) => _sink = sink;
        public void OnNext(T value) => _sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>Run <paramref name="wire"/> through the real chunker and hand back
    /// the chunks it published, in order.</summary>
    private static List<string> Chunk(string wire)
    {
        var conn = new GameConnection(
            new ConnectionConfig(), null!, NullLogger<GameConnection>.Instance);
        var chunks = new List<string>();
        using var _ = conn.RawXmlStream.Subscribe(new Collector<string>(chunks));
        conn.EmitChunks(new StringBuilder(wire));
        return chunks;
    }

    /// <summary>Chunk <paramref name="wire"/> exactly as the live read loop would,
    /// then feed those chunks to the parser — the full wire→display path.</summary>
    private static List<string> TextLines(string wire)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector<GameEvent>(events));
        foreach (var chunk in Chunk(wire)) parser.Feed(chunk);
        return events.OfType<TextEvent>().Select(t => t.Text).ToList();
    }

    [Fact]
    public void Blank_line_chunk_reaches_the_parser()
    {
        Assert.Equal(new[] { "line1\n", "\n", "line2\n" }, Chunk("line1\n\nline2\n"));
    }

    [Fact]
    public void Whitespace_only_line_is_not_swallowed()
    {
        // An all-spaces line (INFO/EXP column spacers, blank rows inside a mono
        // map block) is content as far as the chunker is concerned.
        Assert.Equal(new[] { "line1\n", "   \n", "line2\n" }, Chunk("line1\n   \nline2\n"));
    }

    [Fact]
    public void Real_blank_survives_the_full_wire_to_text_path()
    {
        // The #176 shape, but routed through the chunker the way a live session is.
        Assert.Equal(
            new[] { "Redeemer.", "", "Your birthday is more than 1 month away.", "", "Strength : 12" },
            TextLines("Redeemer.\n\nYour birthday is more than 1 month away.\n\nStrength : 12\n"));
    }

    [Fact]
    public void Tag_adjacent_newline_still_emits_no_blank()
    {
        // The formatting newline after </component> and </prompt> now REACHES the
        // parser (it used to be dropped here). Suppressing it is the parser's job
        // — _emittedTextLine — and it must still do it, or every tag would spam a
        // blank line into the window.
        Assert.Equal(
            new[] { "line1", "", "line2", "line3", "line4" },
            TextLines(
                "line1\n\nline2\n" +
                "<component id='room objs'>a rat</component>\n" +
                "line3\n" +
                "<prompt time='1'>&gt;</prompt>\n" +
                "line4\n"));
    }

    [Fact]
    public void Leading_blank_before_any_text_emits_nothing()
    {
        Assert.Equal(new[] { "first real line" }, TextLines("\nfirst real line\n"));
    }

    [Fact]
    public void Partial_trailing_line_stays_buffered()
    {
        // Unchanged behaviour: an incomplete tag / unterminated line is retained
        // for the next read rather than published early.
        var conn = new GameConnection(
            new ConnectionConfig(), null!, NullLogger<GameConnection>.Instance);
        var chunks = new List<string>();
        using var _ = conn.RawXmlStream.Subscribe(new Collector<string>(chunks));

        var pending = new StringBuilder("done\npartial<comp");
        conn.EmitChunks(pending);

        Assert.Equal(new[] { "done\n" }, chunks);
        Assert.Equal("partial<comp", pending.ToString());
    }
}
