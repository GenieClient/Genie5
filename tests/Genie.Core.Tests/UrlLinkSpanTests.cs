using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// External <c>&lt;a href='URL'&gt;label&lt;/a&gt;</c> links, fed verbatim from a
/// live capture of DR's login "Helpful Information and Resources" block — the
/// only place the game sends them. Until this capture existed the URL path had
/// no sample at all, so <c>IsUrl=true</c> spans were untested end to end and
/// the AvaloniaEdit game-window work had nothing to compare against.
/// </summary>
public class UrlLinkSpanTests
{
    private static List<TextEvent> Feed(string raw)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<TextEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        parser.Feed(raw);
        return events;
    }

    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<TextEvent> _sink;
        public Collector(List<TextEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) { if (e is TextEvent t) _sink.Add(t); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static TextEvent Line(IEnumerable<TextEvent> events, string contains)
        => events.First(e => e.Text.Contains(contains));

    // Verbatim from raw_xml_capture_link.stream: the two "Websites:" rows, each
    // carrying three <a href> links separated by layout whitespace, plus the
    // <d> verb row below them for contrast.
    private const string ResourceBlock =
        "<pushBold/>Websites:\n" +
        "<popBold/>     <a href='https://store.play.net/store/purchase/dr'>Simucoin Store</a>    <a href='http://forums.play.net/calendar?game=dragonrealms'>Events Calendar</a>     <a href='https://elanthipedia.play.net/Category:New_player_guides'>Starter Guides</a>\n" +
        "       <a href='https://elanthipedia.play.net/'>Elanthipedia</a>           <a href='http://www.olwydd.org'>Olwydd's</a>               <a href='https://elanthipedia.play.net/Ranik_Maps'>Maps</a>\n" +
        "\n" +
        "<pushBold/>Useful Verbs:\n" +
        "<popBold/>  <d>SIMUCOINS</d>......Shows your current Simucoin balance and allows you to deliver purchased items.\n";

    [Fact]
    public void AnchorTag_CommitsUrlLinkSpan_OverItsLabel()
    {
        var line = Line(Feed(ResourceBlock), "Simucoin Store");

        Assert.NotNull(line.Links);
        var span = line.Links!.First();
        Assert.True(span.IsUrl);
        Assert.Equal("https://store.play.net/store/purchase/dr", span.Command);
        Assert.Equal("Simucoin Store", line.Text.Substring(span.Start, span.Length));

        // Routing: the resources block arrives inline on the main game stream
        // (no <pushStream> in the capture), so the spans land on the window that
        // actually renders them. Anywhere else they'd parse fine and be dead.
        Assert.Equal("main", line.Stream);
    }

    [Fact]
    public void HrefAndTags_DoNotLeakIntoVisibleText()
    {
        var line = Line(Feed(ResourceBlock), "Simucoin Store");

        Assert.DoesNotContain("href", line.Text);
        Assert.DoesNotContain("<a", line.Text);
        Assert.DoesNotContain("store.play.net", line.Text);
    }

    [Fact]
    public void ThreeLinksOnOneLine_EachGetsItsOwnSpan()
    {
        var line = Line(Feed(ResourceBlock), "Events Calendar");

        Assert.Equal(3, line.Links!.Count);
        Assert.All(line.Links!, s => Assert.True(s.IsUrl));

        // Spans are disjoint, in reading order, and each covers exactly its label.
        var byStart = line.Links!.OrderBy(s => s.Start).ToList();
        Assert.Equal(
            new[] { "Simucoin Store", "Events Calendar", "Starter Guides" },
            byStart.Select(s => line.Text.Substring(s.Start, s.Length)));
        Assert.Equal(
            new[]
            {
                "https://store.play.net/store/purchase/dr",
                "http://forums.play.net/calendar?game=dragonrealms",
                "https://elanthipedia.play.net/Category:New_player_guides",
            },
            byStart.Select(s => s.Command));
        for (var i = 1; i < byStart.Count; i++)
            Assert.True(byStart[i].Start >= byStart[i - 1].Start + byStart[i - 1].Length);
    }

    [Fact]
    public void QueryStringAndTrailingSlashUrls_SurviveIntact()
    {
        var events = Feed(ResourceBlock);

        // '?' and '=' in the href must not be mangled or truncated.
        var calendar = Line(events, "Events Calendar").Links!
            .Single(s => s.Command.Contains("forums.play.net"));
        Assert.Equal("http://forums.play.net/calendar?game=dragonrealms", calendar.Command);

        // A bare-host URL keeps its trailing slash.
        var elanthipedia = Line(events, "Elanthipedia").Links!
            .Single(s => s.Command == "https://elanthipedia.play.net/");
        Assert.Equal("Elanthipedia",
            Line(events, "Elanthipedia").Text.Substring(elanthipedia.Start, elanthipedia.Length));
    }

    [Fact]
    public void ApostropheInLabel_DoesNotTruncateTheSpan()
    {
        // "Olwydd's" — the label contains the quote char used for href delimiting.
        var line = Line(Feed(ResourceBlock), "Olwydd");
        var span = line.Links!.Single(s => s.Command == "http://www.olwydd.org");
        Assert.Equal("Olwydd's", line.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void GameLinkInSameBlock_StaysNonUrl()
    {
        // The contrast case: <d> in the same block must NOT be flagged IsUrl,
        // or clicking a game command would open a browser.
        var line = Line(Feed(ResourceBlock), "SIMUCOINS");
        var span = line.Links!.Single();
        Assert.False(span.IsUrl);
        Assert.Equal("SIMUCOINS", span.Command);
    }
}
