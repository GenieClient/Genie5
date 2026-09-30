using System;
using System.Collections.Generic;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Health;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #263 / #375 — DR's other-character injuries dialog
/// (<c>injuries-&lt;charnum&gt;</c>) carried into the Healing window: its health
/// bar and buttons in the header for the same patient, a ⇄ on the parts DR
/// marked, right-click to transfer. DR's own commands go through the dialog
/// host (resolved and escaped); the Healing panel's own sends go through its
/// send seam. Fixtures are the 2026-09-28 recording and the dialog journal,
/// verbatim, through the real parser and engine.
/// </summary>
public class HealingDialogMergeTests
{
    // raw_session_Naper_20260928_215038.xml, lines 5-7 (healthy parts, bar,
    // Transfer Vit, Re-Link).
    private const string RecordedOpen =
        "<openDialog type=\"dynamic\" id=\"injuries-10224090\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\"><dialogData id=\"injuries\"><skin id=\"injuredSkin\" name=\"InjuriesPanel\" controls=\"nsys,leftArm,rightArm,rightLeg,leftLeg,head,rightFoot,leftFoot,rightHand,leftHand,rightEye,leftEye,back,neck,chest,abdomen\" top=\"5\" left=\"5\" width=\"100\" height=\"150\" align=\"nw\"/><radio id=\"injrRadExt\" value=\"0\" text=\"E Wound\" cmd=\"_injury 0 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"20\"/><radio id=\"injrRadInt\" value=\"0\" text=\"I Wound\" cmd=\"_injury 3 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"40\"/><radio id=\"scarRadExt\" value=\"0\" text=\"E Scar\" cmd=\"_injury 1 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"60\"/><radio id=\"scarRadInt\" value=\"0\" text=\"I Scar\" cmd=\"_injury 4 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"80\"/><radio id=\"bothRadExt\" value=\"0\" text=\"E Both\" cmd=\"_injury 2 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"100\"/><radio id=\"bothRadInt\" value=\"1\" text=\"I Both\" cmd=\"_injury 5 -10224090\" group=\"injureMode\" autosend=\"\" anchor_left=\"injuredSkin\" width=\"80\" left=\"5\" top=\"120\"/></dialogData></openDialog>\n" +
        "<dialogData id=\"injuries-10224090\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/><image id=\"neck\" name=\"neck\" height=\"0\" width=\"0\"/><image id=\"rightArm\" name=\"rightArm\" height=\"0\" width=\"0\"/><image id=\"leftArm\" name=\"leftArm\" height=\"0\" width=\"0\"/><image id=\"rightLeg\" name=\"rightLeg\" height=\"0\" width=\"0\"/><image id=\"leftLeg\" name=\"leftLeg\" height=\"0\" width=\"0\"/><image id=\"rightHand\" name=\"rightHand\" height=\"0\" width=\"0\"/><image id=\"leftHand\" name=\"leftHand\" height=\"0\" width=\"0\"/><image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/><image id=\"abdomen\" name=\"abdomen\" height=\"0\" width=\"0\"/><image id=\"back\" name=\"back\" height=\"0\" width=\"0\"/><image id=\"rightEye\" name=\"rightEye\" height=\"0\" width=\"0\"/><image id=\"leftEye\" name=\"leftEye\" height=\"0\" width=\"0\"/><image id=\"rightFoot\" name=\"rightFoot\" height=\"0\" width=\"0\"/><image id=\"nsys\" name=\"nsys\" height=\"0\" width=\"0\"/></dialogData>\n" +
        "<dialogData id=\"injuries-10224090\"><skin id=\"healthSkin\" name=\"healthBar2\" controls=\"health2\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/><progressBar id=\"health2\" value=\"100\" text=\"HEALTH 100%\" customText=\"t\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/><cmdButton id=\"tranblood-10224090\" value=\"Transfer Vit\" cmd=\"transfer Renucci\" align=\"n\" top=\"180\" left=\"-45\" width=\"80\" height=\"15\"/><cmdButton id=\"touch-10224090\" value=\"Re-Link\" cmd=\"touch Renucci\" anchor_left=\"tranblood-10224090\" left=\"5\" width=\"80\" height=\"15\"/></dialogData>You lay your hand on Renucci's arm.\n";

    // dialog_journal.xml, the 2026-09-02 delta: DR marks two parts transferable.
    private const string MarkedParts =
        "<dialogData id=\"injuries-10224090\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/><image id=\"neck\" name=\"neck\" height=\"0\" width=\"0\"/><image id=\"rightArm\" name=\"rightArm\" height=\"0\" width=\"0\"/><image id=\"leftArm\" name=\"leftArm\" height=\"0\" width=\"0\"/><image id=\"rightLeg\" name=\"rightLeg\" height=\"0\" width=\"0\"/><image id=\"leftLeg\" name=\"Injury1\" cmd=\"transfer Renucci internal left leg\" tooltip=\"transfer internal left leg\" height=\"0\" width=\"0\"/><image id=\"rightHand\" name=\"rightHand\" height=\"0\" width=\"0\"/><image id=\"leftHand\" name=\"leftHand\" height=\"0\" width=\"0\"/><image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/><image id=\"abdomen\" name=\"Injury1\" cmd=\"transfer Renucci internal abdomen\" tooltip=\"transfer internal abdomen\" height=\"0\" width=\"0\"/><image id=\"back\" name=\"back\" height=\"0\" width=\"0\"/><image id=\"rightEye\" name=\"rightEye\" height=\"0\" width=\"0\"/><image id=\"leftEye\" name=\"leftEye\" height=\"0\" width=\"0\"/><image id=\"rightFoot\" name=\"rightFoot\" height=\"0\" width=\"0\"/><image id=\"nsys\" name=\"nsys\" height=\"0\" width=\"0\"/></dialogData>\n";

    private sealed class Sink(ServerDialogEngine engine) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent e)
        {
            switch (e)
            {
                case OpenDialogEvent od: engine.Observe(od); break;
                case DialogDataEvent dd: engine.Observe(dd); break;
                case DynaStreamEvent ds: engine.Observe(ds); break;
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static ServerDialogState Feed(params string[] xml)
    {
        var engine = new ServerDialogEngine();
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        using var _ = parser.GameEvents.Subscribe(new Sink(engine));
        foreach (var x in xml) parser.Feed(x);
        return engine.Get("injuries-10224090")!;
    }

    private static PatientHealth Read(params string[] lines)
    {
        var p = new PerceiveHealthParser(() => new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        foreach (var l in lines) p.Feed(l);
        return p.TakeCompleted() ?? throw new InvalidOperationException("block did not complete");
    }

    // The 2026-09-30 touch, plus one internal wound for the #375 fallback.
    private static PatientHealth RenucciTouch() => Read(
        "Renucci's injuries include...",
        "Wounds to the LEFT LEG:",
        "  Fresh External:  light scratches -- insignificant",
        "Wounds to the CHEST:",
        "  Fresh External:  light scratches -- insignificant",
        "  Fresh Internal:  -- harmful",
        "Renucci has normal vitality.");

    private static PatientHealth NaperTouch() => Read(
        "Naper's injuries include...",
        "Wounds to the CHEST:",
        "  Fresh Internal:  -- harmful",
        "Naper has normal vitality.");

    private sealed record Rig(
        HealingViewModel Healing, ServerDialogViewModel Host, OtherInjuriesViewModel Dialog,
        List<string> Sent, List<ServerDialogAction> HostSent);

    /// <summary>A Healing panel following one dialog host, wired the way the
    /// main window wires them (attach before the first Apply).</summary>
    private static Rig Build(params string[] xml)
    {
        var sent = new List<string>();
        var healing = new HealingViewModel();
        healing.AttachForTest(sent.Add);

        var host = new ServerDialogViewModel("injuries-10224090");
        var hostSent = new List<ServerDialogAction>();
        host.ActionRequested += hostSent.Add;
        var dialog = (OtherInjuriesViewModel)host.Bespoke!;
        healing.AttachDialog(dialog);
        host.Apply(Feed(xml));
        return new Rig(healing, host, dialog, sent, hostSent);
    }

    private static HealingViewModel.RegionCell Cell(HealingViewModel vm, string id)
        => vm.Cells.Single(c => c.RegionId == id);

    private static void Axis(HealingViewModel vm, InjuryAxis axis)
        => vm.SelectedAxis = HealingViewModel.AxisOptions.Single(o => o.Axis == axis);

    // ── Header: the bar and DR's buttons ─────────────────────────────────────

    [Fact]
    public void The_patients_dialog_puts_DRs_bar_and_buttons_in_the_header()
    {
        var r = Build(RecordedOpen);
        r.Healing.Apply(RenucciTouch());

        Assert.True(r.Healing.HasDrDialog);
        Assert.Same(r.Dialog, r.Healing.DrDialog);
        var bar = Assert.Single(r.Healing.DrDialog!.Extras.OfType<DialogProgressViewModel>());
        Assert.Equal("HEALTH 100%", bar.Caption);
        Assert.Equal(100, bar.Value);
        Assert.Equal(new[] { "Transfer Vit", "Re-Link" },
                     r.Healing.DrDialog.Extras.OfType<DialogButtonViewModel>().Select(b => b.Caption));
        Assert.Empty(r.Sent);           // a dialog arriving never sends
        Assert.Empty(r.HostSent);
    }

    [Fact]
    public void Transfer_Vit_and_Re_Link_send_DRs_commands_through_the_host()
    {
        var r = Build(RecordedOpen);
        r.Healing.Apply(RenucciTouch());

        r.Healing.ActivateDialogControl("tranblood-10224090");
        r.Healing.ActivateDialogControl("touch-10224090");
        r.Healing.ActivateDialogControl("health2");        // not a button: nothing

        Assert.Equal(new[] { "transfer Renucci", "touch Renucci" }, r.HostSent.Select(a => a.Value));
        Assert.All(r.HostSent, a => Assert.Equal(ServerDialogActionKind.GameCommand, a.Kind));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Another_patients_dialog_does_not_show()
    {
        var r = Build(RecordedOpen);
        r.Healing.Apply(NaperTouch());

        Assert.False(r.Healing.HasDrDialog);
        Assert.Null(r.Healing.DrDialog);
        r.Healing.ActivateDialogControl("tranblood-10224090");
        Assert.Empty(r.HostSent);

        r.Healing.PatientName = "renucci";                  // typed: case doesn't matter
        Assert.True(r.Healing.HasDrDialog);

        r.Healing.PatientName = "";                         // yourself
        Assert.False(r.Healing.HasDrDialog);
    }

    [Fact]
    public void The_dialog_arriving_after_the_reading_still_shows()
    {
        var sent = new List<string>();
        var healing = new HealingViewModel();
        healing.AttachForTest(sent.Add);
        healing.Apply(RenucciTouch());
        Assert.False(healing.HasDrDialog);

        var host = new ServerDialogViewModel("injuries-10224090");
        healing.AttachDialog((OtherInjuriesViewModel)host.Bespoke!);
        host.Apply(Feed(RecordedOpen));

        Assert.True(healing.HasDrDialog);
    }

    [Fact]
    public void A_new_connection_drops_the_dialog()
    {
        var r = Build(RecordedOpen);
        r.Healing.Apply(RenucciTouch());

        r.Healing.ClearDialogs();

        Assert.False(r.Healing.HasDrDialog);
        r.Host.Apply(Feed(RecordedOpen, MarkedParts));     // an old host re-rendering is no longer followed
        Assert.False(r.Healing.HasDrDialog);
    }

    // ── Tiles: left-click takes, right-click transfers ───────────────────────

    [Fact]
    public void A_DR_marked_part_shows_its_marker_and_right_click_sends_DRs_transfer()
    {
        var r = Build(RecordedOpen, MarkedParts);
        r.Healing.Apply(RenucciTouch());

        var leg = Cell(r.Healing, "leftLeg");
        Assert.True(leg.DrTransfer);
        Assert.Contains("right-click: transfer internal left leg", leg.Tip);
        Assert.True(Cell(r.Healing, "abdomen").DrTransfer);   // marked, even with no wound in the reading
        Assert.False(Cell(r.Healing, "head").DrTransfer);

        r.Healing.TransferRegion(leg);
        r.Healing.TransferRegion(Cell(r.Healing, "abdomen"));

        Assert.Equal(new[] { "transfer Renucci internal left leg", "transfer Renucci internal abdomen" },
                     r.HostSent.Select(a => a.Value));
        Assert.Empty(r.Sent);
    }

    [Fact]
    public void Left_click_still_takes()
    {
        var r = Build(RecordedOpen, MarkedParts);
        r.Healing.Apply(RenucciTouch());

        r.Healing.HealRegion(Cell(r.Healing, "leftLeg"));

        Assert.Equal(new[] { "take Renucci left leg" }, r.Sent);
        Assert.Empty(r.HostSent);
    }

    /// <summary>Public #375: a part DR didn't mark, on another patient, sends
    /// the internal-axis transfer. External wording is unconfirmed, so an
    /// external axis stays inert; so does a healthy tile.</summary>
    [Fact]
    public void An_unmarked_wounded_part_transfers_on_an_internal_axis_only()
    {
        var r = Build(RecordedOpen, MarkedParts);
        r.Healing.Apply(RenucciTouch());
        var chest = Cell(r.Healing, "chest");

        r.Healing.TransferRegion(chest);                   // Fresh External: no confirmed wording
        Assert.Empty(r.Sent);
        Assert.DoesNotContain("right-click", chest.Tip);

        Axis(r.Healing, InjuryAxis.FreshInternal);
        Assert.False(chest.DrTransfer);
        Assert.Contains("right-click: transfer Renucci internal chest", chest.Tip);
        r.Healing.TransferRegion(chest);
        r.Healing.TransferRegion(Cell(r.Healing, "head")); // healthy: nothing

        Assert.Equal(new[] { "transfer Renucci internal chest" }, r.Sent);
        Assert.Empty(r.HostSent);
    }

    [Fact]
    public void The_fallback_works_without_any_dialog()
    {
        var sent = new List<string>();
        var healing = new HealingViewModel();
        healing.AttachForTest(sent.Add);
        healing.Apply(NaperTouch());
        Axis(healing, InjuryAxis.FreshInternal);

        healing.TransferRegion(Cell(healing, "chest"));

        Assert.Equal(new[] { "transfer Naper internal chest" }, sent);
    }

    [Fact]
    public void A_right_click_on_yourself_sends_nothing()
    {
        var r = Build(RecordedOpen, MarkedParts);
        r.Healing.Apply(Read(
            "Your injuries include...",
            "Wounds to the LEFT LEG:",
            "  Fresh Internal:  -- harmful",
            "You have normal vitality."));
        Assert.True(r.Healing.IsSelf);
        Axis(r.Healing, InjuryAxis.FreshInternal);

        var leg = Cell(r.Healing, "leftLeg");
        Assert.False(leg.DrTransfer);
        Assert.DoesNotContain("right-click", leg.Tip);
        r.Healing.TransferRegion(leg);

        Assert.Empty(r.Sent);
        Assert.Empty(r.HostSent);
    }
}
