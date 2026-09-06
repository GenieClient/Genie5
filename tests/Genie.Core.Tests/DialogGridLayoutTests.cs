using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156 Phase 1 — the coordinate→grid inference. The two headline
/// fixtures are VERBATIM from <c>Logs/dialog_journal.xml</c> (Renucci):
/// <c>bank_debt</c> harvested 2026-08-30 20:48:48Z and <c>spellChoose</c>
/// 2026-09-04 03:00:15Z — the first real target dialogs the Phase 0b/0c soak
/// produced. Both are fed through the shipped <see cref="DrXmlParser"/> rather
/// than hand-built controls, so these pin the whole path.
/// </summary>
public class DialogGridLayoutTests
{
    // ── Fixtures (verbatim journal blocks) ───────────────────────────────────

    private const string BankDebt =
        "<dialogData id='bank_debt'>" +
        "<label id='label2' value='Province:' justify='5' align='nw' top='10' left='10'/>" +
        "<dropDownBox id='province1' value='Zoluren' cmd='' " +
        "content_text=\"Zoluren,Therengia,Ilithi,Qi,Forfedhdar\" content_value=\"1,2,3,4,5\" " +
        "top='10' left='75' width='160' height='25' align='nw'/>" +
        "<label id='label' value='Amount:' justify='5' align='nw' top='45' left='10'/>" +
        "<upDownEditBox id='bank_amount' min='1' max='50000000' value='1' top='45' left='75' " +
        "width='85' height='25' align='nw'/>" +
        "<closeButton id='sendbankCommand' value='Pay' cmd='bank debt %province1% %bank_amount%' " +
        "top='80' left='75' width='50' align='nw'/>" +
        "<closeButton id='sendbankCommand2' value='ALL' cmd='bank debt %province1% all' " +
        "top='80' left='135' width='50' align='nw'/>" +
        "<label id='label3' value='Amount is in coppers.' justify='5' align='nw' top='100' left='75'/>" +
        "</dialogData>\n";

    private const string SpellChoose =
        "<dialogData id='spellChoose' clear='t'>" +
        "<streamBox id='spells' top='40' left='15' width='250' height='380' />" +
        "<streamBox id='spellInfo' top='40' left='20' width='300' height='380' anchor_left='spells' />" +
        "<closeButton id='chooseSpell' value='Close' width='200' top='-10' left='0' align='s' />" +
        "<label id='Instructions1' value='Spells available for you to choose are listed in the left " +
        "panel.  Click the spell on the left' top='0' left='10' wrap='true'/>" +
        "<label id='Instructions2' value='panel and then click the button below to choose it from " +
        "your guildleader.' top='16' left='10' wrap='true'/>" +
        "</dialogData>\n";

    // ── bank_debt: the label/value grid ──────────────────────────────────────

    [Fact]
    public void BankDebt_PairsEachLabelWithItsValueOnOneRow()
    {
        var grid = InferFrom(BankDebt);

        // Four bands of `top`: 10, 45, 80, 100.
        Assert.Equal(4, grid.Rows);

        AssertCell(grid, "label2",           row: 0, col: 0);
        AssertCell(grid, "province1",        row: 0, col: 1);
        AssertCell(grid, "label",            row: 1, col: 0);
        AssertCell(grid, "bank_amount",      row: 1, col: 1);
        AssertCell(grid, "sendbankCommand",  row: 2, col: 1);
        AssertCell(grid, "sendbankCommand2", row: 2, col: 2);
    }

    [Fact]
    public void BankDebt_AlignsTheValueColumnAcrossRows()
    {
        var grid = InferFrom(BankDebt);

        // left=75 appears on four different rows — the dropdown, the amount box,
        // the Pay button and the footnote. The column map is global, so they all
        // land in the same column even though nothing links them but coordinates.
        Assert.Equal(new[] { 1, 1, 1 },
            new[] { "province1", "bank_amount", "sendbankCommand" }
                .Select(id => grid[id]!.Column).ToArray());
    }

    [Fact]
    public void BankDebt_TreatsTheLoneFootnoteLabelAsFullWidth()
    {
        var grid = InferFrom(BankDebt);

        // "Amount is in coppers." is alone on its row with no width — a spanning
        // note. It must not drive a column, or its text widens the whole dialog.
        var footnote = grid["label3"]!;
        Assert.True(footnote.FullWidth);
        Assert.Equal(0, footnote.Column);
        Assert.Equal(3, footnote.Row);
    }

    [Fact]
    public void BankDebt_KeepsThePlaceholderCommandAndPairedDropdownValues()
    {
        var grid = InferFrom(BankDebt);

        // %placeholder% tokens name sibling control ids and are substituted at
        // click time — the renderer's dispatch depends on them surviving intact.
        Assert.Equal("bank debt %province1% %bank_amount%", grid["sendbankCommand"]!.Control.Cmd);

        var province = grid["province1"]!.Control;
        Assert.Equal("Zoluren,Therengia,Ilithi,Qi,Forfedhdar", province.Attributes["content_text"]);
        Assert.Equal("1,2,3,4,5",                              province.Attributes["content_value"]);

        var amount = grid["bank_amount"]!.Control;
        Assert.Equal("1",        amount.Attributes["min"]);
        Assert.Equal("50000000", amount.Attributes["max"]);
    }

    // ── spellChoose: anchors, panels and the button strip ────────────────────

    [Fact]
    public void SpellChoose_PlacesTheAnchoredPanelBesideItsAnchor()
    {
        var grid = InferFrom(SpellChoose);

        // anchor_left='spells' names a SIBLING CONTROL ID, not a boolean.
        var spells    = grid["spells"]!;
        var spellInfo = grid["spellInfo"]!;

        Assert.Equal(spells.Row, spellInfo.Row);
        Assert.Equal(spells.Column + 1, spellInfo.Column);
    }

    [Fact]
    public void SpellChoose_DoesNotSpanEitherSideBySidePanel()
    {
        var grid = InferFrom(SpellChoose);

        // Both are streamBoxes, and the Genie 4 original spanned every one of
        // them — which stacked these two on top of each other.
        Assert.False(grid["spells"]!.FullWidth);
        Assert.False(grid["spellInfo"]!.FullWidth);
    }

    [Fact]
    public void SpellChoose_SplitsTheBottomAnchoredButtonOutOfTheBody()
    {
        var grid = InferFrom(SpellChoose);

        var close = Assert.Single(grid.Bottom);
        Assert.Equal("chooseSpell", close.Id);
        Assert.True(close.BottomAnchored);
        Assert.Equal(-10, close.OriginalTop);          // raw signed top, kept
        Assert.DoesNotContain(grid.Body, c => c.Id == "chooseSpell");
    }

    [Fact]
    public void SpellChoose_StacksTheTwoInstructionLinesAboveThePanels()
    {
        var grid = InferFrom(SpellChoose);

        // tops 0 and 16 are further apart than the snap distance, so they are
        // separate rows rather than one merged line.
        Assert.Equal(0, grid["Instructions1"]!.Row);
        Assert.Equal(1, grid["Instructions2"]!.Row);
        Assert.Equal(2, grid["spells"]!.Row);
        Assert.True(grid["Instructions1"]!.FullWidth);
    }

    // ── Row/column banding ───────────────────────────────────────────────────

    [Fact]
    public void TopsWithinTheSnapDistanceShareARow()
    {
        var grid = InferFrom(Data(
            Ctl("label", "a", "top='100' left='10'"),
            Ctl("label", "b", "top='106' left='90'"),      // 6px — same row
            Ctl("label", "c", "top='140' left='10'")));    // 40px — next row

        Assert.Equal(grid["a"]!.Row, grid["b"]!.Row);
        Assert.NotEqual(grid["a"]!.Row, grid["c"]!.Row);
    }

    [Fact]
    public void LeftsWithinTheSnapDistanceShareAColumnAcrossDifferentRows()
    {
        // The profileEdit case: a control at left=105 must line up with one at
        // left=100 several rows above it.
        var grid = InferFrom(Data(
            Ctl("label",      "lbl1", "top='10' left='10'"),
            Ctl("editBox",    "val1", "top='10' left='100'"),
            Ctl("label",      "lbl2", "top='40' left='10'"),
            Ctl("dropDownBox","val2", "top='40' left='105'")));

        Assert.Equal(grid["val1"]!.Column, grid["val2"]!.Column);
        Assert.Equal(grid["lbl1"]!.Column, grid["lbl2"]!.Column);
        Assert.NotEqual(grid["lbl1"]!.Column, grid["val1"]!.Column);
    }

    [Fact]
    public void CentredControlsDoNotMergeIntoANeighbouringRow()
    {
        // customStringDialog: an editBox at top=15 must not merge with a centred
        // label at top=10 despite being inside the snap distance.
        var grid = InferFrom(Data(
            Ctl("label",   "title", "top='10' left='0' align='center'"),
            Ctl("editBox", "entry", "top='15' left='10'")));

        Assert.NotEqual(grid["title"]!.Row, grid["entry"]!.Row);
        Assert.True(grid["title"]!.CentreAligned);
        Assert.Contains(grid.CentreBody, c => c.Id == "title");
        Assert.DoesNotContain(grid.Body, c => c.Id == "title");
    }

    [Fact]
    public void NegativeTopsSortBelowEveryTopMeasuredControl()
    {
        // A negative top is measured from the bottom of the dialog.
        var grid = InferFrom(Data(
            Ctl("label", "bottomish", "top='-20' left='10'"),
            Ctl("label", "first",     "top='10' left='10'"),
            Ctl("label", "second",    "top='50' left='10'")));

        Assert.True(grid["bottomish"]!.Row > grid["second"]!.Row);
        Assert.Equal(-20, grid["bottomish"]!.OriginalTop);
    }

    // ── Anchor resolution ────────────────────────────────────────────────────

    [Fact]
    public void AnchorsResolveWhenTheTargetIsDeclaredAfterThem()
    {
        // Declaration order is not resolution order — the queue re-runs until
        // every anchor target is placed.
        var grid = InferFrom(Data(
            Ctl("streamBox", "follower", "top='40' left='60' anchor_left='leader'"),
            Ctl("streamBox", "leader",   "top='40' left='10'")));

        Assert.Equal(grid["leader"]!.Column + 1, grid["follower"]!.Column);
        Assert.Equal(grid["leader"]!.Row, grid["follower"]!.Row);
    }

    [Fact]
    public void AnchorTopPlacesTheControlOnTheRowBelowItsTarget()
    {
        var grid = InferFrom(Data(
            Ctl("label",   "head", "top='10' left='10'"),
            Ctl("editBox", "under", "anchor_top='head'")));

        Assert.Equal(grid["head"]!.Row + 1, grid["under"]!.Row);
    }

    [Fact]
    public void AnAnchorCycleTerminatesAndStillRendersBothControls()
    {
        var grid = InferFrom(Data(
            Ctl("label", "a", "anchor_left='b'"),
            Ctl("label", "b", "anchor_left='a'")));

        // Neither can resolve; both must still appear rather than vanish.
        Assert.NotNull(grid["a"]);
        Assert.NotNull(grid["b"]);
        Assert.NotEqual(grid["a"]!.Row, grid["b"]!.Row);
    }

    [Fact]
    public void AnAnchorPointingAtNothingStillRendersTheControl()
    {
        var grid = InferFrom(Data(
            Ctl("label", "real",   "top='10' left='10'"),
            Ctl("label", "orphan", "anchor_left='doesNotExist'")));

        Assert.NotNull(grid["orphan"]);
        Assert.Equal(2, grid.Cells.Count);
    }

    // ── Geometry parsing ─────────────────────────────────────────────────────

    [Fact]
    public void PercentGeometryContributesItsNumericPart()
    {
        // DR mixes the two forms. The Genie 4 original int-parsed and silently
        // got 0 for every percent value, collapsing them into one column.
        Assert.True(DialogGridLayout.TryCoord("20%", out int pct));
        Assert.Equal(20, pct);

        var grid = InferFrom(Data(
            Ctl("label",   "a", "top='10' left='10%'"),
            Ctl("editBox", "b", "top='10' left='60%'")));

        Assert.NotEqual(grid["a"]!.Column, grid["b"]!.Column);
    }

    [Fact]
    public void MissingGeometryIsNotZero()
    {
        Assert.False(DialogGridLayout.TryCoord(null, out _));
        Assert.False(DialogGridLayout.TryCoord("",   out _));
        Assert.False(DialogGridLayout.TryCoord("  ", out _));
        Assert.False(DialogGridLayout.TryCoord("abc", out _));
        Assert.True(DialogGridLayout.TryCoord("-10", out int neg));
        Assert.Equal(-10, neg);
    }

    [Fact]
    public void ControlsWithNoGeometryAtAllStillRender()
    {
        // befriend arrives with width-less, coordinate-less children; the
        // original dropped them, which is how a dialog renders empty.
        var grid = InferFrom(Data(
            Ctl("label",     "positioned", "top='10' left='10'"),
            Ctl("cmdButton", "floating",   "value='Go' cmd='go'")));

        Assert.NotNull(grid["floating"]);
        Assert.True(grid["floating"]!.Row > grid["positioned"]!.Row);
    }

    // ── Degenerate input ─────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyDialogYieldsAnEmptyGrid()
    {
        Assert.Empty(DialogGridLayout.Infer(null).Cells);
        Assert.Empty(DialogGridLayout.Infer(Array.Empty<DialogControl>()).Cells);
        Assert.Empty(InferFrom("<dialogData id='befriend'></dialogData>\n").Cells);
    }

    [Fact]
    public void AClearContainerIsAResetMarkerNotAControl()
    {
        var grid = InferFrom(Data(
            Ctl("clearContainer", "reset", ""),
            Ctl("label",          "real", "top='10' left='10'")));

        var only = Assert.Single(grid.Cells);
        Assert.Equal("real", only.Id);
    }

    [Fact]
    public void RowsAreRenumberedContiguouslyWhenAnchorsLeaveGaps()
    {
        var grid = InferFrom(Data(
            Ctl("label",   "head",  "top='10' left='10'"),
            Ctl("editBox", "under", "anchor_top='head'"),
            Ctl("label",   "tail",  "top='400' left='10'")));

        var rows = grid.Body.Select(c => c.Row).Distinct().OrderBy(r => r).ToList();
        Assert.Equal(Enumerable.Range(0, rows.Count), rows);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DialogGrid InferFrom(string raw)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new Collector(events));
        parser.Feed(raw);
        var data = Assert.Single(events.OfType<DialogDataEvent>());
        return DialogGridLayout.Infer(data.Controls);
    }

    private static string Ctl(string tag, string id, string attrs) =>
        $"<{tag} id='{id}'{(attrs.Length > 0 ? " " + attrs : "")}/>";

    private static string Data(params string[] controls) =>
        $"<dialogData id='test'>{string.Concat(controls)}</dialogData>\n";

    private static void AssertCell(DialogGrid grid, string id, int row, int col)
    {
        var cell = grid[id];
        Assert.NotNull(cell);
        Assert.Equal(row, cell!.Row);
        Assert.Equal(col, cell.Column);
    }

    private sealed class Collector(List<GameEvent> sink) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent value) => sink.Add(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
