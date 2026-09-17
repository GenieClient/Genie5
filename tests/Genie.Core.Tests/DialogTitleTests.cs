using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #344 — DR sends dialog titles with the ampersand doubled, the Win32
/// mnemonic escape Wrayth was built against. Avalonia gives <c>&amp;</c> no
/// meaning in a window title, so the doubling rendered verbatim AND was
/// persisted as the remembered name in <c>dialogmappings.json</c>.
/// </summary>
public class DialogTitleTests
{
    // ── The normaliser ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Friends && Enemies", "Friends & Enemies")]
    [InlineData("&&", "&")]
    [InlineData("A && B && C", "A & B & C")]
    [InlineData("Injuries", "Injuries")]
    [InlineData("", "")]
    public void TheDoubledFormCollapses(string sent, string shown) =>
        Assert.Equal(shown, DialogTitle.Normalize(sent));

    [Fact]
    public void ANullTitlePassesThrough() =>
        Assert.Null(DialogTitle.Normalize(null));

    [Fact]
    public void ALoneAmpersandIsLeftAlone()
    {
        // Under the Win32 reading a single & marks the next character as a
        // mnemonic, but DR has never been seen to send one, and eating it
        // would silently drop a real ampersand from an unescaped title.
        Assert.Equal("Friends & Enemies", DialogTitle.Normalize("Friends & Enemies"));
    }

    // ── Through the parser ───────────────────────────────────────────────────

    [Fact]
    public void TheParserNormalisesTheTitleAndKeepsTheRawTag()
    {
        // Verbatim from Logs/dialog_journal.xml, first sighting 2026-08-30 —
        // the wire form is &amp;&amp;, which decodes to && before we see it.
        var events = Feed(
            "<openDialog type=\"dynamic\" id=\"befriend\" title=\"Friends &amp;&amp; Enemies\" " +
            "location=\"right\" target=\"befriend\" height=\"165\" resident=\"true\">" +
            "<dialogData id=\"befriend\"></dialogData></openDialog>\n");

        var open = Assert.Single(events.OfType<OpenDialogEvent>());
        Assert.Equal("Friends & Enemies", open.Title);

        // RawXml is the audit trail — it must still carry what DR sent.
        Assert.Contains("Friends &amp;&amp; Enemies", open.RawXml);
    }

    [Fact]
    public void AnOrdinaryTitleIsUntouchedByTheParser()
    {
        var events = Feed(
            "<openDialog type=\"dynamic\" id=\"injuries\" title=\"Renucci's Injuries\" " +
            "location=\"right\"><dialogData id=\"injuries\"></dialogData></openDialog>\n");

        Assert.Equal("Renucci's Injuries",
                     Assert.Single(events.OfType<OpenDialogEvent>()).Title);
    }

    // ── Through the mapping table ────────────────────────────────────────────

    [Fact]
    public void NoteTitleStoresTheCollapsedForm()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "befriend", Mode = ServerDialogMode.NewWindow });

        m.NoteTitle("befriend", "Friends && Enemies");

        Assert.Equal("Friends & Enemies", Assert.Single(m.All()).Title);
    }

    [Fact]
    public void AProfileSavedBeforeTheFixIsRepairedOnLoad()
    {
        // The exact shape written by the shipped build: the doubled title went
        // into the file, so an existing profile carries it until it is reread.
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path,
            "[{\"Id\":\"befriend\",\"Mode\":\"NewWindow\",\"Target\":null," +
            "\"AutoOpen\":true,\"Title\":\"Friends && Enemies\"}]");

        try
        {
            var m = new ServerDialogMappings();
            Assert.True(m.Load(path));

            var only = Assert.Single(m.All());
            Assert.Equal("befriend", only.Id);
            Assert.Equal("Friends & Enemies", only.Title);
            Assert.Equal(ServerDialogMode.NewWindow, only.Mode);   // nothing else disturbed
            Assert.True(only.AutoOpen);
        }
        finally { File.Delete(path); }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static List<GameEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        foreach (var chunk in chunks) parser.Feed(chunk);
        return events;
    }

    private sealed class Collector(List<GameEvent> sink) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent value) => sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
