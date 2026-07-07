using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Diagnostics;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// "Report XML Gap" data path. When the parser meets an element it does not
/// consume it emits an <see cref="UnknownTagEvent"/>; <see cref="XmlGapReport"/>
/// turns that into a review-ready, redacted GitHub new-issue prefill URL. This
/// is exactly the Core path the App's notice strip consumes: subscribe to
/// UnknownTagEvent → Build a draft → open draft.Url in the browser.
/// </summary>
public class XmlGapReportTests
{
    private sealed class Collector : IObserver<GameEvent>
    {
        private readonly List<GameEvent> _sink;
        public Collector(List<GameEvent> sink) => _sink = sink;
        public void OnNext(GameEvent e) => _sink.Add(e);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    [Fact]
    public void Parser_emits_UnknownTagEvent_for_an_unconsumed_element()
    {
        var events  = Feed("<frobnitz kind='7'>whatever</frobnitz>\n");
        var unknown = events.OfType<UnknownTagEvent>().FirstOrDefault();

        Assert.NotNull(unknown);
        Assert.Equal("frobnitz", unknown!.TagName, ignoreCase: true);
        Assert.Contains("frobnitz", unknown.RawXml);
    }

    [Fact]
    public void Build_produces_a_prefilled_github_new_issue_url()
    {
        var ctx   = new XmlGapReport.ReportContext("5.0.0-alpha.test", "TestOS", "local", "unit test");
        var draft = XmlGapReport.Build("frobnitz", DrXmlParser.TagFate.Unknown, "<frobnitz kind='7'/>", ctx);

        Assert.Contains("/issues/new", draft.Url);
        Assert.Contains("frobnitz", draft.Title);
        Assert.Equal("xml-coverage", draft.Labels);
        // The App hands draft.Url straight to the browser, so it must be a single
        // percent-encoded token — no raw spaces or newlines.
        Assert.DoesNotContain(' ', draft.Url);
        Assert.DoesNotContain('\n', draft.Url);
    }

    [Fact]
    public void Build_redacts_other_players_speech_from_the_sample()
    {
        var ctx = new XmlGapReport.ReportContext("5.0.0", "TestOS", "local", "unit test");
        // A sample that (implausibly) carried another player's speech on its own
        // line: the redactor (policy gate G2) must strip the spoken text before
        // it can reach the drafted issue body.
        var sample = "<frobnitz/>\nSomeone says, \"secret plan\"\n";
        var draft  = XmlGapReport.Build("frobnitz", DrXmlParser.TagFate.Unknown, sample, ctx);

        Assert.DoesNotContain("secret plan", draft.Body);
    }
}
