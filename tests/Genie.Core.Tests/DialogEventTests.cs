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
/// Public #156 Phase 0b — typed server-dialog events. Raw forms verbatim from
/// live captures (dual_20260521_*_Renucci_raw.xml login block; the 2026-08-29
/// Renucci session's minivitals deltas).
/// </summary>
public class DialogEventTests
{
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

    // ── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public void OpenDialog_EmitsTypedEvent_FromTheLoginBlockForm()
    {
        // Verbatim from the May 2026 login block (double-quoted, wraps its
        // dialogData child).
        var events = Feed(
            "<openDialog type=\"dynamic\" id=\"injuries\" title=\"Injuries\" target=\"injuries\" " +
            "location=\"right\" height=\"180\" width=\"190\" resident=\"true\">" +
            "<dialogData id=\"injuries\"><radio id=\"injrRadExt\" value=\"1\" text=\"E Wound\" cmd=\"_injury 0 -1\"/>" +
            "</dialogData></openDialog>\n");

        var open = Assert.Single(events.OfType<OpenDialogEvent>());
        Assert.Equal("injuries", open.Id);
        Assert.Equal("Injuries", open.Title);
        Assert.Equal("right",    open.Location);
        Assert.Equal("190",      open.Width);
        Assert.Equal("180",      open.Height);
        Assert.True(open.Resident);
        Assert.Equal("dynamic",  open.DialogType);

        var data = Assert.Single(events.OfType<DialogDataEvent>());
        Assert.Equal("injuries", data.DialogId);
        var radio = Assert.Single(data.Controls);
        Assert.Equal(DialogControlType.Radio, radio.Type);
        Assert.Equal("injrRadExt", radio.Id);
        Assert.Equal("_injury 0 -1", radio.Cmd);
        Assert.Equal("E Wound", radio.Text);
    }

    [Fact]
    public void CloseAndExposeDialog_EmitTypedEvents()
    {
        var events = Feed("<closeDialog id=\"bank_debt\"/><exposeDialog id=\"encum\"/>\n");
        Assert.Equal("bank_debt", Assert.Single(events.OfType<CloseDialogEvent>()).Id);
        Assert.Equal("encum",     Assert.Single(events.OfType<ExposeDialogEvent>()).Id);
    }

    // ── dialogData capture ───────────────────────────────────────────────────

    [Fact]
    public void Minivitals_Delta_CapturesControls_AndStillFeedsTheVitalsBar()
    {
        // Verbatim from the 2026-08-29 session (single-quoted live form): one
        // dialogData block per vitals update — a DELTA, not a redraw.
        var events = Feed(
            "<dialogData id='minivitals'><skin id='manaSkin' name='manaBar' controls='mana' " +
            "left='20%' top='0%' width='20%' height='100%'/><progressBar id='mana' value='87' " +
            "text='mana 87%' left='20%' customText='t' top='0%' width='20%' height='100%'/></dialogData>\n");

        var data = Assert.Single(events.OfType<DialogDataEvent>());
        Assert.Equal("minivitals", data.DialogId);
        Assert.False(data.Clear);
        Assert.Equal(2, data.Controls.Count);
        Assert.Equal(DialogControlType.Skin,        data.Controls[0].Type);
        Assert.Equal(DialogControlType.ProgressBar, data.Controls[1].Type);
        Assert.Equal("20%", data.Controls[1].Left);                 // %-form geometry survives
        Assert.Equal("t", data.Controls[1].Attributes["customText"]); // full attribute bag

        // The vitals bar's existing feed is untouched.
        var bar = Assert.Single(events.OfType<ProgressBarEvent>());
        Assert.Equal("mana", bar.BarId);
    }

    [Fact]
    public void Injuries_DualEmits_InjuryEventAndDialogControls()
    {
        var events = Feed(
            "<dialogData id=\"injuries\"><image id=\"rightLeg\" name=\"Injury2\"/></dialogData>\n");

        var injury = Assert.Single(events.OfType<InjuryEvent>());
        Assert.Equal("rightLeg", injury.Area);

        var data = Assert.Single(events.OfType<DialogDataEvent>());
        Assert.Equal("injuries", data.DialogId);
        var img = Assert.Single(data.Controls);
        Assert.Equal(DialogControlType.Image, img.Type);
        Assert.Equal("Injury2", img.Attributes["name"]);
    }

    [Fact]
    public void ClearAttribute_BothForms()
    {
        // Open/close pair with no children (the May login form)…
        var events = Feed("<dialogData id=\"injuries\" clear=\"t\"></dialogData>\n");
        var d1 = Assert.Single(events.OfType<DialogDataEvent>());
        Assert.True(d1.Clear);
        Assert.Empty(d1.Controls);

        // …and the true self-closing form.
        var events2 = Feed("<dialogData id='encum' clear='t'/>\n");
        var d2 = Assert.Single(events2.OfType<DialogDataEvent>());
        Assert.Equal("encum", d2.DialogId);
        Assert.True(d2.Clear);
    }

    [Fact]
    public void RawXml_RoundTripsTheBlock_ForTheCaptureJournal()
    {
        var raw = "<dialogData id='bank'><label id='lbl' value='Balance: 100 Kronars'/>" +
                  "<cmdButton id='dep' value='Deposit' cmd='deposit %amt%'/></dialogData>";
        var events = Feed(raw + "\n");
        var data = Assert.Single(events.OfType<DialogDataEvent>());
        Assert.Contains("cmdButton", data.RawXml);
        Assert.Contains("deposit %amt%", data.RawXml);
        Assert.StartsWith("<dialogData id='bank'>", data.RawXml);
        Assert.EndsWith("</dialogData>", data.RawXml);

        // The DynamicWindows %placeholder% mechanism must survive into Cmd.
        Assert.Equal("deposit %amt%", data.Controls[1].Cmd);
    }

    [Fact]
    public void ControlTags_OutsideADialog_StayDiscarded()
    {
        // The login settings dump reuses these names; none may leak events.
        var events = Feed("<label id='x' value='settings noise'/><cmdButton id='y' cmd='no'/>\n");
        Assert.Empty(events.OfType<DialogDataEvent>());
    }

    [Fact]
    public void UnknownControlName_IsNotCaptured_ButKnownSetIsForwardSafe()
    {
        // A brand-new control tag inside a dialog isn't in the vocabulary —
        // today it simply isn't captured (the raw journal still records the
        // block'd content via RawXml only for known tags; new tags surface
        // through #audit as before). The block itself still emits.
        var events = Feed("<dialogData id='x'><newfangled id='n'/><label id='l' value='v'/></dialogData>\n");
        var data = Assert.Single(events.OfType<DialogDataEvent>());
        var ctl = Assert.Single(data.Controls);
        Assert.Equal(DialogControlType.Label, ctl.Type);
    }
}

/// <summary>The #156 capture journal: first sighting per dialog id, persisted
/// across instances, excluded ids never journal.</summary>
public sealed class DialogJournalTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "g5-journal-" + Guid.NewGuid().ToString("N"));

    public DialogJournalTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FirstSightingJournals_RepeatsDoNot()
    {
        var j = new DialogJournal(_dir);
        Assert.True(j.Observe("bank", "<dialogData id='bank'/>"));
        Assert.False(j.Observe("bank", "<dialogData id='bank'/>"));
        var text = File.ReadAllText(Path.Combine(_dir, DialogJournal.FileName));
        Assert.Single(text.Split("dialog id=\"bank\"").Skip(1));
    }

    [Fact]
    public void SeenIdsPersistAcrossInstances()
    {
        Assert.True(new DialogJournal(_dir).Observe("encum", "<dialogData id='encum'/>"));
        Assert.False(new DialogJournal(_dir).Observe("encum", "<dialogData id='encum'/>"));
    }

    [Fact]
    public void ExcludedIdsNeverJournal()
    {
        var j = new DialogJournal(_dir);
        Assert.False(j.Observe("injuries",   "<dialogData id='injuries'/>"));
        Assert.False(j.Observe("minivitals", "<dialogData id='minivitals'/>"));
        Assert.False(File.Exists(Path.Combine(_dir, DialogJournal.FileName)));
    }

    [Fact]
    public void ObserveOpen_LogsOnce_WithoutConsumingTheId()
    {
        var j = new DialogJournal(_dir);
        j.ObserveOpen("bank", "<openDialog id='bank' title='Bank'/>");
        j.ObserveOpen("bank", "<openDialog id='bank' title='Bank'/>");   // deduped
        Assert.True(j.Observe("bank", "<dialogData id='bank'/>"));       // still first sighting
        var text = File.ReadAllText(Path.Combine(_dir, DialogJournal.FileName));
        Assert.Contains("openDialog id=\"bank\"", text);
        Assert.Contains("dialog id=\"bank\" first seen", text);
    }
}
