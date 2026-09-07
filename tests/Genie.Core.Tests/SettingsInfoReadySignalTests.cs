using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>&lt;settingsInfo/&gt;</c> is the authoritative "server is ready for input"
/// signal: GenieCore takes the FIRST SettingsInfoEvent and fires the initial
/// <c>look</c> off it (GenieCore.cs:1152). DR Platinum serialises a failed
/// lookup straight into the attribute list and sends it malformed —
/// <c>&lt;settingsInfo  space not found crc='0' instance='DRX'/&gt;</c>, a bare
/// token with no <c>=</c> where <c>space='…'</c> would have gone.
///
/// <para>XmlReader rejects that outright. It survives ONLY because ParseTag
/// catches the XmlException and falls through to the manual name-scrape
/// (DrXmlParser.cs:957). That fallback was written for bare <c>&lt;d&gt;</c>,
/// so nothing tied it to this input: narrowing the catch, or making the
/// fallback stricter about malformed attributes, would break every Platinum
/// connect — the client would authenticate, take the room, and then sit there
/// having never sent <c>look</c>. These tests pin it (public #337).</para>
/// </summary>
public class SettingsInfoReadySignalTests
{
    /// <summary>The exact bytes from the DRX capture, double space included.</summary>
    private const string PlatinumMalformed = "<settingsInfo  space not found crc='0' instance='DRX'/>";

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
    public void SettingsInfo_is_classified_as_consumed()
    {
        Assert.Equal(DrXmlParser.TagFate.Consumed, DrXmlParser.ClassifyTag("settingsinfo"));
    }

    [Fact]
    public void Well_formed_settingsInfo_emits_the_ready_signal()
    {
        // Prime's form — the control for the Platinum case below.
        var events = Feed("<settingsInfo space='34' crc='0' instance='DR'/>\n");
        Assert.Single(events.OfType<SettingsInfoEvent>());
    }

    [Fact]
    public void Malformed_platinum_settingsInfo_still_emits_the_ready_signal()
    {
        // The whole point of #337: this parses only via the XmlException
        // fallback. If it ever stops emitting, Platinum never sends `look`.
        var events = Feed(PlatinumMalformed + "\n");
        Assert.Single(events.OfType<SettingsInfoEvent>());
    }

    [Fact]
    public void Malformed_settingsInfo_leaks_no_raw_xml_into_the_game_window()
    {
        // The other half of a fallback regression: a tag the parser fails to
        // recognise falls through to the text path and shows up as garbage in
        // the main window.
        var events = Feed(PlatinumMalformed + "\n");

        Assert.Empty(events.OfType<UnknownTagEvent>());
        Assert.DoesNotContain(events.OfType<TextEvent>(),
            t => t.Text.Contains("settingsInfo", StringComparison.OrdinalIgnoreCase)
              || t.Text.Contains("space not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_platinum_login_run_up_fires_the_ready_signal_exactly_once()
    {
        // The capture's full run-up, in order, fed as one chunk.
        var events = Feed(
            "Please wait for connection to game server.\n" +
            "<playerID id='123456'/>\n" +
            PlatinumMalformed + "\n" +
            "<mode id=\"GAME\"/>\n" +
            "Welcome to DragonRealms Platinum (R) v2.00\n" +
            "<app char=\"Shroom\" game=\"DRX\" title=\"[DR Plat: Shroom] Wrayth\"/>\n");

        Assert.Single(events.OfType<SettingsInfoEvent>());

        // `<app>` still arrives behind it — settingsInfo must not swallow the
        // rest of the run-up on its way through the fallback.
        Assert.Equal("DRX", Assert.Single(events.OfType<AppEvent>()).Game);
    }

    [Fact]
    public void Each_login_in_a_session_fires_its_own_ready_signal()
    {
        // The capture holds two logins in one file (SessionRecorder doesn't
        // stop on disconnect). GenieCore's .Take(1) is what makes the initial
        // `look` one-shot — the parser itself reports every one.
        var events = Feed(
            PlatinumMalformed + "\n",
            PlatinumMalformed + "\n");

        Assert.Equal(2, events.OfType<SettingsInfoEvent>().Count());
    }
}
