using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #216 — <c>&lt;menuImage&gt;</c> (a Wrayth demeanor-menu image button)
/// arrived live and classified Unknown. Rather than patching one tag, the whole
/// Wrayth server-driven dialog-control vocabulary (bank/store/spells/feats/
/// profile-edit popups — see the server-dialog renderer plan, public #156) is
/// pre-seeded as DroppedData: silently skipped today, but still counted as a
/// coverage gap by <c>#audit xmlhunting</c> instead of drafting a gap-report
/// issue per control the first time each dialog is opened.
/// </summary>
public class DialogControlCoverageTests
{
    public static IEnumerable<object[]> DialogControlTags() =>
        new[]
        {
            "menuimage", "closedialog", "exposedialog", "menulink",
            "label", "cmdbutton", "closebutton", "checkbox",
            "streambox", "dropdownbox", "editbox", "updowneditbox",
        }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(DialogControlTags))]
    public void Dialog_control_classifies_as_dropped_data(string tag)
    {
        Assert.Equal(DrXmlParser.TagFate.DroppedData, DrXmlParser.ClassifyTag(tag));
        // Case-insensitive — DR sends these camelCased (menuImage, cmdButton, …).
        Assert.Equal(DrXmlParser.TagFate.DroppedData, DrXmlParser.ClassifyTag(tag.ToUpperInvariant()));
    }

    [Fact]
    public void MenuImage_live_sample_is_silently_discarded_between_game_lines()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        // Verbatim shape of the live element from public #216 (player name swapped).
        parser.Feed("before\n<menuImage id=\"demeanor1\" name=\"warmFace\" tooltip=\"Warm\" " +
                    "exist=\"-10000001\" noun=\"Someone\" align=\"nw\" top=\"0\" left=\"100\" " +
                    "height=\"25\" width=\"25\"/>after\n");

        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        var texts = events.OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains("before", texts);
        Assert.Contains("after", texts);
    }

    [Fact]
    public void Menulink_live_sample_is_silently_discarded_between_game_lines()
    {
        // beta.4: <menulink> (the in-menu sibling of <link>) arrived live and
        // classified Unknown, firing the "unrecognized game element" warning.
        // Same treatment as its vocabulary siblings — dropped, no leaked text.
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        parser.Feed("before\n<menuLink id=\"1\" value=\"Look\" cmd=\"look\" " +
                    "noun=\"someone\" exist=\"-10000001\"/>after\n");

        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        var texts = events.OfType<TextEvent>().Select(t => t.Text).ToList();
        Assert.Contains("before", texts);
        Assert.Contains("after", texts);
        // Its value/cmd attributes must not leak into the visible text stream.
        Assert.DoesNotContain(texts, t => t.Contains("Look"));
    }

    [Fact]
    public void Dialog_control_burst_emits_no_unknown_and_no_leaked_text()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));

        // A representative server-dialog payload: every pre-seeded control as
        // DR sends it (self-closing, attribute-carried values).
        parser.Feed(
            "<openDialog id='bank' title='Bank'/>" +
            "<dialogData id='bank'>" +
            "<label id='lblBal' value='Balance: 100 Kronars' top='0' left='0'/>" +
            "<editBox id='amt' value='' top='20' left='0'/>" +
            "<upDownEditBox id='qty' value='1' top='20' left='80'/>" +
            "<checkBox id='chk' value='0' text='Auto-deposit' top='40' left='0'/>" +
            "<dropDownBox id='cur' value='Kronars' top='60' left='0'/>" +
            "<streamBox id='log' top='80' left='0'/>" +
            "<cmdButton id='dep' value='Deposit' cmd='deposit' top='100' left='0'/>" +
            "<closeButton id='close' value='Close' top='100' left='80'/>" +
            "<menuImage id='demeanor1' name='warmFace' tooltip='Warm' top='0' left='100'/>" +
            "</dialogData>" +
            "<exposeDialog id='bank'/>" +
            "<closeDialog id='bank'/>\n");

        Assert.DoesNotContain(events, e => e is UnknownTagEvent);
        // No attribute values may leak into the text stream as visible output.
        Assert.DoesNotContain(events.OfType<TextEvent>(),
            t => !string.IsNullOrWhiteSpace(t.Text));
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
