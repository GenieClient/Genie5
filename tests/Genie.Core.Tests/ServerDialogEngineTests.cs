using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156 Phase 1 — the server dialog state engine. Delta merge, the
/// <c>clear</c> reset, lifecycle, stream routing, and the bespoke-id exclusions.
/// </summary>
public class ServerDialogEngineTests
{
    // ── Delta merge ──────────────────────────────────────────────────────────

    [Fact]
    public void ControlsMergeByIdAcrossBlocks()
    {
        var engine = new ServerDialogEngine();

        engine.Observe(Data("bank_debt", Label("label2", "Province:"), Label("label", "Amount:")));
        engine.Observe(Data("bank_debt", Label("label", "Amount (coppers):")));

        var state = engine.Get("bank_debt")!;
        Assert.Equal(2, state.Controls.Count);
        Assert.Equal("Amount (coppers):", state.Controls.Single(c => c.Id == "label").Value);
    }

    [Fact]
    public void AMergedControlKeepsItsOriginalPosition()
    {
        var engine = new ServerDialogEngine();

        engine.Observe(Data("d", Label("first", "1"), Label("second", "2"), Label("third", "3")));
        engine.Observe(Data("d", Label("second", "updated")));

        // An update is not a re-append: the layout order the server established
        // has to survive, or controls shuffle on every delta.
        var state = engine.Get("d")!;
        Assert.Equal(new[] { "first", "second", "third" }, state.Controls.Select(c => c.Id));
        Assert.Equal("updated", state.Controls[1].Value);
    }

    [Fact]
    public void PartialUpdatesAccumulateTheWayMinivitalsSendsThem()
    {
        // The captures show one or two controls per block, never a full redraw.
        var engine = new ServerDialogEngine();

        engine.Observe(Data("vitals2", Bar("health", "100")));
        engine.Observe(Data("vitals2", Bar("mana",   "80")));
        engine.Observe(Data("vitals2", Bar("spirit", "95")));

        Assert.Equal(3, engine.Get("vitals2")!.Controls.Count);
    }

    [Fact]
    public void TheClearAttributeResetsTheControlList()
    {
        var engine = new ServerDialogEngine();

        engine.Observe(Data("d", Label("stale", "old")));
        engine.Observe(Data("d", clear: true, controls: Label("fresh", "new")));

        var only = Assert.Single(engine.Get("d")!.Controls);
        Assert.Equal("fresh", only.Id);
    }

    [Fact]
    public void ABareClearEmptiesTheDialog()
    {
        var engine = new ServerDialogEngine();

        engine.Observe(Data("d", Label("a", "x")));
        engine.Observe(Data("d", clear: true));

        Assert.Empty(engine.Get("d")!.Controls);
    }

    [Fact]
    public void IdlessControlsOverwriteTheirSlotRatherThanAccumulating()
    {
        var engine = new ServerDialogEngine();

        for (int i = 0; i < 5; i++)
            engine.Observe(Data("d", Ctl(DialogControlType.Label, id: "", value: $"pass{i}")));

        var only = Assert.Single(engine.Get("d")!.Controls);
        Assert.Equal("pass4", only.Value);
    }

    // ── clearContainer ───────────────────────────────────────────────────────

    [Fact]
    public void ClearContainerClearsThatStreamNotTheControlList()
    {
        // The design doc called clearContainer "the explicit reset"; the
        // maintained Genie 4 source says otherwise — it clears the NAMED
        // container's text and leaves every control in place.
        var engine = new ServerDialogEngine();

        engine.Observe(Data("spellChoose", Stream("spells"), Label("note", "pick one")));
        engine.SetStream("spells", "Fire Shards\nTingle");
        Assert.Equal("Fire Shards\nTingle", engine.Get("spellChoose")!.Streams["spells"]);

        engine.Observe(Data("spellChoose", Ctl(DialogControlType.ClearContainer, "spells")));

        var state = engine.Get("spellChoose")!;
        Assert.Equal(2, state.Controls.Count);                  // controls untouched
        Assert.False(state.Streams.ContainsKey("spells"));      // text gone
    }

    [Fact]
    public void ClearContainerIsNeverRenderedAsAControl()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("d", Ctl(DialogControlType.ClearContainer, "box"), Label("a", "x")));

        var only = Assert.Single(engine.Get("d")!.Controls);
        Assert.Equal("a", only.Id);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    [Fact]
    public void OpenDialogRecordsTitleAndGeometry()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new OpenDialogEvent(
            "bank_debt", "Provincial Debt", "force-center", "250", "150",
            Resident: false, "dynamic", "<openDialog/>"));

        var state = engine.Get("bank_debt")!;
        Assert.Equal("Provincial Debt", state.Title);
        Assert.Equal("force-center",    state.Location);
        Assert.Equal("250",             state.Width);
        Assert.True(state.IsOpen);
    }

    [Fact]
    public void ClosingADialogHidesItButKeepsItsContent()
    {
        // The Genie 4 plugin dropped deltas for closed windows, so reopening one
        // showed nothing. State has to outlive the window.
        var engine = new ServerDialogEngine();
        engine.Observe(Open("d"));
        engine.Observe(Data("d", Label("a", "value")));
        engine.Observe(new CloseDialogEvent("d"));

        var state = engine.Get("d")!;
        Assert.False(state.IsOpen);
        Assert.Single(state.Controls);
    }

    [Fact]
    public void DataArrivingForAClosedDialogIsStillMerged()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Open("d"));
        engine.Observe(new CloseDialogEvent("d"));
        engine.Observe(Data("d", Label("late", "arrived")));

        Assert.Single(engine.Get("d")!.Controls);
    }

    [Fact]
    public void DataBeforeAnyOpenCreatesTheDialog()
    {
        // Ordering is not guaranteed, and a delta is enough to describe content.
        var engine = new ServerDialogEngine();
        engine.Observe(Data("unheralded", Label("a", "x")));

        Assert.NotNull(engine.Get("unheralded"));
    }

    [Fact]
    public void ExposeReopensAndRaisesTheDialog()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Open("d"));
        engine.Observe(new CloseDialogEvent("d"));

        var changes = Collect(engine);
        engine.Observe(new ExposeDialogEvent("d"));

        Assert.True(engine.Get("d")!.IsOpen);
        Assert.Equal(ServerDialogChangeKind.Exposed, changes.Single().Kind);
    }

    [Fact]
    public void ClosingAnUnknownDialogInventsNothing()
    {
        // DR closes AimTimerDialog without ever opening it in the same session.
        var engine = new ServerDialogEngine();
        var changes = Collect(engine);

        engine.Observe(new CloseDialogEvent("AimTimerDialog"));

        Assert.Null(engine.Get("AimTimerDialog"));
        Assert.Empty(changes);
    }

    // ── Streams (public #324 wiring) ─────────────────────────────────────────

    [Fact]
    public void StreamTextIsCachedBeforeItsDialogExists()
    {
        var engine = new ServerDialogEngine();
        engine.SetStream("spellInfo", "Fire Shards: 3 mana");
        engine.Observe(Data("spellChoose", Stream("spellInfo")));

        Assert.Equal("Fire Shards: 3 mana", engine.Get("spellChoose")!.Streams["spellInfo"]);
    }

    [Fact]
    public void OnlyDialogsOwningTheControlAreNotifiedOfAStreamChange()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("withBox",    Stream("spells")));
        engine.Observe(Data("withoutBox", Label("a", "x")));

        var changes = Collect(engine);
        engine.SetStream("spells", "text");

        Assert.Equal("withBox", changes.Single().DialogId);
        Assert.Equal(ServerDialogChangeKind.StreamChanged, changes[0].Kind);
    }

    [Fact]
    public void ClearStreamDropsTheText()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("d", Stream("box")));
        engine.SetStream("box", "content");
        engine.ClearStream("box");

        Assert.Empty(engine.Get("d")!.Streams);
    }

    // ── Exclusions ───────────────────────────────────────────────────────────

    [Fact]
    public void BespokeDialogsNeverReachTheGenericEngine()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("injuries",   Label("head", "x")));
        engine.Observe(Data("minivitals", Bar("health", "100")));

        Assert.Null(engine.Get("injuries"));
        Assert.Null(engine.Get("minivitals"));
        Assert.Empty(engine.Snapshot());
    }

    [Fact]
    public void ThePerCharacterInjuriesDialogIsNotExcluded()
    {
        // Journaled 2026-09-02 as injuries-10224090 ("Renucci's Injuries", images
        // carrying `cmd="transfer …"`) — a different window from the #18 self
        // panel, which does not render it. A prefix exclusion would lose it.
        var engine = new ServerDialogEngine();
        engine.Observe(Data("injuries-10224090", Label("leftLeg", "x")));

        Assert.NotNull(engine.Get("injuries-10224090"));
        Assert.False(ServerDialogEngine.IsBespoke("injuries-10224090"));
        Assert.True(ServerDialogEngine.IsBespoke("injuries"));
    }

    // ── Snapshots and notifications ──────────────────────────────────────────

    [Fact]
    public void ASnapshotDoesNotChangeUnderTheReader()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("d", Label("a", "first")));

        var before = engine.Get("d")!;
        engine.Observe(Data("d", Label("b", "second")));

        Assert.Single(before.Controls);                     // the handed-out copy
        Assert.Equal(2, engine.Get("d")!.Controls.Count);   // the live state
    }

    [Fact]
    public void EveryChangeCarriesTheStateAndBumpsTheRevision()
    {
        var engine = new ServerDialogEngine();
        var changes = Collect(engine);

        engine.Observe(Open("d"));
        engine.Observe(Data("d", Label("a", "x")));

        Assert.Equal(
            new[] { ServerDialogChangeKind.Opened, ServerDialogChangeKind.Data },
            changes.Select(c => c.Kind));
        Assert.All(changes, c => Assert.NotNull(c.State));
        Assert.True(changes[1].State!.Revision > changes[0].State!.Revision);
    }

    [Fact]
    public void ASnapshotCarriesTheInferredGrid()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("d",
            Ctl(DialogControlType.Label,   "lbl", value: "Province:", left: "10", top: "10"),
            Ctl(DialogControlType.EditBox, "val", value: "Zoluren",   left: "75", top: "10")));

        var grid = engine.Get("d")!.Grid;
        Assert.Equal(grid["lbl"]!.Row, grid["val"]!.Row);
        Assert.NotEqual(grid["lbl"]!.Column, grid["val"]!.Column);
    }

    [Fact]
    public void ResetDropsEverythingAndSaysSoOnce()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("a", Label("x", "1")));
        engine.Observe(Data("b", Label("y", "2")));
        engine.SetStream("x", "text");

        var changes = Collect(engine);
        engine.Reset();

        Assert.Empty(engine.Snapshot());
        var reset = Assert.Single(changes);
        Assert.Equal(ServerDialogChangeKind.Reset, reset.Kind);
        Assert.Equal("", reset.DialogId);
    }

    [Fact]
    public void ResettingAnEmptyEngineSaysNothing()
    {
        var engine = new ServerDialogEngine();
        var changes = Collect(engine);
        engine.Reset();

        Assert.Empty(changes);
    }

    [Fact]
    public void SnapshotListsEveryDialogOrderedById()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(Data("zeta",  Label("a", "1")));
        engine.Observe(Data("alpha", Label("b", "2")));

        Assert.Equal(new[] { "alpha", "zeta" }, engine.Snapshot().Select(s => s.Id));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<ServerDialogChange> Collect(ServerDialogEngine engine)
    {
        var sink = new List<ServerDialogChange>();
        engine.Changes.Subscribe(new Collector(sink));
        return sink;
    }

    private static OpenDialogEvent Open(string id) =>
        new(id, id, "center", "200", "100", Resident: false, "dynamic", "<openDialog/>");

    private static DialogDataEvent Data(string id, params DialogControl[] controls) =>
        new(id, controls, Clear: false, "<dialogData/>");

    private static DialogDataEvent Data(string id, bool clear, params DialogControl[] controls) =>
        new(id, controls, clear, "<dialogData/>");

    private static DialogControl Label(string id, string value) =>
        Ctl(DialogControlType.Label, id, value: value);

    private static DialogControl Bar(string id, string value) =>
        Ctl(DialogControlType.ProgressBar, id, value: value);

    private static DialogControl Stream(string id) =>
        Ctl(DialogControlType.StreamBox, id);

    private static DialogControl Ctl(
        DialogControlType type, string id, string? value = null,
        string? left = null, string? top = null) =>
        new(type, id, value, null, null, left, top, null, null, null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private sealed class Collector(List<ServerDialogChange> sink) : IObserver<ServerDialogChange>
    {
        public void OnNext(ServerDialogChange value) => sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
