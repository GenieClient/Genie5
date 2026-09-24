using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public issue #30 — DR's <c>news</c> listing arrives as PLAIN TEXT with no
/// <c>&lt;d&gt;</c>/<c>&lt;a&gt;</c> tags, so the numbered item lines aren't
/// clickable on their own. The parser tracks the listing context plus the
/// current category and synthesizes a click link ("news &lt;cat&gt; &lt;item&gt;")
/// over each numbered line. These tests feed the exact raw format captured from
/// a live Renucci session (raw_session_Renucci_20260628_221016.xml).
/// </summary>
public class NewsAutoLinkTests
{
    private static List<TextEvent> Feed(string raw)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<TextEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        parser.Feed(raw);
        return events;
    }

    // Minimal IObserver so the test project needn't reference System.Reactive
    // just for the Subscribe(Action<T>) extension.
    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<TextEvent> _sink;
        public Collector(List<TextEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) { if (e is TextEvent t) _sink.Add(t); }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static TextEvent? Line(IEnumerable<TextEvent> events, string startsWith)
        => events.FirstOrDefault(e => e.Text.TrimStart().StartsWith(startsWith));

    // The category-1 item block, verbatim from the capture (with the inline
    // <output class="mono"/> header the server actually sends).
    private const string CategoryOneListing =
        "<output class=\"mono\"/>ITEM # - HEADLINE\n" +
        "** Category 1 - GENERAL ANNOUNCEMENTS **\n" +
        "     1 - WELCOME TO DRAGONREALMS\n" +
        "     2 - COMMUNICATION WITH STAFF\n" +
        "    11 - GUIDELINES FOR RUMOR SUBMISSIONS\n" +
        "Type NEWS HELP for additional information on how to get the most from the NEWS command.\n";

    [Fact]
    public void NumberedItem_GetsSynthesizedNewsLink()
    {
        var events = Feed(CategoryOneListing);

        var item1 = Line(events, "1 - WELCOME TO DRAGONREALMS");
        Assert.NotNull(item1);
        Assert.NotNull(item1!.Links);
        var span = Assert.Single(item1.Links!);
        Assert.Equal("news 1 1", span.Command);
        Assert.False(span.IsUrl);

        // The link covers the number through the end of the headline, not the
        // leading layout whitespace.
        var linked = item1.Text.Substring(span.Start, span.Length);
        Assert.Equal("1 - WELCOME TO DRAGONREALMS", linked);
    }

    [Fact]
    public void MultiDigitItem_UsesItsOwnNumber()
    {
        var events = Feed(CategoryOneListing);

        var item11 = Line(events, "11 - GUIDELINES FOR RUMOR SUBMISSIONS");
        Assert.NotNull(item11);
        var span = Assert.Single(item11!.Links!);
        Assert.Equal("news 1 11", span.Command);
        Assert.Equal("11 - GUIDELINES FOR RUMOR SUBMISSIONS",
            item11.Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void CategoryNumber_FlowsIntoCommand()
    {
        var raw =
            "ITEM # - HEADLINE\n" +
            "** Category 5 - BILLING AND POLICY INFO **\n" +
            "     7 - VULGARITY POLICY\n" +
            "Type NEWS HELP for more.\n";

        var events = Feed(raw);
        var item = Line(events, "7 - VULGARITY POLICY");
        Assert.Equal("news 5 7", Assert.Single(item!.Links!).Command);
    }

    [Fact]
    public void HeaderAndFooterLines_AreNotLinked()
    {
        var events = Feed(CategoryOneListing);

        Assert.Null(Line(events, "** Category 1")!.Links);
        Assert.Null(Line(events, "Type NEWS HELP")!.Links);
    }

    [Fact]
    public void InterleavedPromptAndOutputTags_DoNotBreakDetection()
    {
        // Verbatim from the capture: the header line arrives prefixed by empty
        // <output> tags and a <prompt> (which triggers a mid-line flush). The
        // news context must still open on the trailing "ITEM # - HEADLINE".
        var raw =
            "<output class=\"\"/><output class=\"\"/><prompt time=\"1782699030\">&gt;</prompt>" +
            "<output class=\"mono\"/>ITEM # - HEADLINE\n" +
            "** Category 1 - GENERAL ANNOUNCEMENTS **\n" +
            "     2 - COMMUNICATION WITH STAFF\n" +
            "Type NEWS HELP for more.\n";

        var events = Feed(raw);
        var item = Line(events, "2 - COMMUNICATION WITH STAFF");
        Assert.NotNull(item);
        Assert.Equal("news 1 2", Assert.Single(item!.Links!).Command);
    }

    [Fact]
    public void NumberedLine_OutsideNewsContext_IsNotLinked()
    {
        // A bare numbered line with no preceding news header must stay plain —
        // the gate is the listing context, not the digit pattern.
        var events = Feed("     1 - some random numbered text\n");
        var line = Line(events, "1 - some random numbered text");
        Assert.NotNull(line);
        Assert.Null(line!.Links);
    }

    [Fact]
    public void NumberedLine_InsideArticleBody_IsNotLinked()
    {
        // After "END NEWS ITEM" the listing context is closed, so a numbered
        // line inside a read article's body must NOT be turned into a link.
        var raw =
            "ITEM # - HEADLINE\n" +
            "** Category 1 - GENERAL ANNOUNCEMENTS **\n" +
            "     1 - WELCOME TO DRAGONREALMS\n" +
            "END NEWS ITEM\n" +
            "     1 - this is body text that merely looks numbered\n";

        var events = Feed(raw);
        // The real listing item is still linked...
        Assert.NotNull(Line(events, "1 - WELCOME TO DRAGONREALMS")!.Links);
        // ...but the look-alike body line after the footer is not.
        var body = events.Last(e => e.Text.Contains("body text that merely looks numbered"));
        Assert.Null(body.Links);
    }

    [Fact]
    public void OverflowingCategoryNumber_DoesNotThrow_AndIsNotAHeader()
    {
        // 2026-08-31 stability review: the category regex runs against every emitted line, and
        // int.Parse on an 11+ digit run threw OverflowException up through Feed
        // (→ the read loop → a dropped connection). It must parse as plain text.
        var raw =
            "** Category 99999999999 - X **\n" +
            "     1 - numbered text after it\n" +
            "still flowing\n";

        var events = Feed(raw);

        Assert.NotNull(Line(events, "** Category 99999999999 - X **"));
        // Not a header, so it opened no listing: the next numbered line stays plain.
        Assert.Null(Line(events, "1 - numbered text after it")!.Links);
        Assert.NotNull(Line(events, "still flowing"));
    }
}
