using System;
using System.Collections.Generic;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity: DR indents the room-objects line ("  You also see …") in
/// plain-text output such as PEER WINDOW. Genie 4 trimmed that line before
/// scripts saw it, so `matchre … ^You also see` must match in Genie 5 too.
/// Other indented lines keep their leading whitespace.
/// </summary>
public class AlsoSeeTrimTests
{
    private static List<TextEvent> Feed(string xml)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var lines = new List<TextEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(e =>
        {
            if (e is TextEvent t && t.Text.Length > 0) lines.Add(t);
        }));
        parser.Feed(xml);
        return lines;
    }

    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly Action<GameEvent> _on;
        public Collector(Action<GameEvent> on) => _on = on;
        public void OnNext(GameEvent e) => _on(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    [Fact]
    public void Indented_also_see_line_is_trimmed()
    {
        var lines = Feed("[Kertigen's Honor]\n  You also see an aging caravan and the north bank docks.\nObvious paths: none.\n");
        Assert.Contains(lines, l => l.Text == "You also see an aging caravan and the north bank docks.");
        Assert.DoesNotContain(lines, l => l.Text.StartsWith(" "));
    }

    [Fact]
    public void Bold_span_shifts_with_the_trim()
    {
        var lines = Feed("  You also see <pushBold/>a ship's rat<popBold/> and a poster.\n");
        var line = Assert.Single(lines);
        Assert.StartsWith("You also see", line.Text);
        var span = Assert.Single(line.BoldSpans!);
        Assert.Equal("a ship's rat", line.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Other_indented_lines_keep_their_leading_whitespace()
    {
        var lines = Feed("     Strength : 30\n");
        var line = Assert.Single(lines);
        Assert.Equal("     Strength : 30", line.Text);
    }
}
