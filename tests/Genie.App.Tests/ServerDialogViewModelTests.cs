using System;
using System.Collections.Generic;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #156 Phase 1 — the renderer's view-model layer. No Avalonia needed:
/// the VM owns control lifecycle and command resolution, the view only binds.
/// The bank_debt fixture is the verbatim journal block (2026-08-30).
/// </summary>
public class ServerDialogViewModelTests
{
    // ── Building controls from state ─────────────────────────────────────────

    [Fact]
    public void EachControlKindGetsItsOwnViewModel()
    {
        var vm = Build(BankDebt());

        Assert.IsType<DialogLabelViewModel>(Find(vm, "label2"));
        Assert.IsType<DialogComboViewModel>(Find(vm, "province1"));
        Assert.IsType<DialogTextBoxViewModel>(Find(vm, "bank_amount"));
        Assert.IsType<DialogButtonViewModel>(Find(vm, "sendbankCommand"));
    }

    [Fact]
    public void TheDropdownIsPopulatedFromTheContentTextList()
    {
        var combo = (DialogComboViewModel)Find(Build(BankDebt()), "province1")!;

        Assert.Equal(
            new[] { "Zoluren", "Therengia", "Ilithi", "Qi", "Forfedhdar" },
            combo.Items);
        Assert.Equal("Zoluren", combo.Selected);   // the server's own default
    }

    [Fact]
    public void TheNumberFieldCarriesItsBounds()
    {
        var box = (DialogTextBoxViewModel)Find(Build(BankDebt()), "bank_amount")!;

        Assert.True(box.IsNumeric);
        Assert.Equal(1, box.Minimum);
        Assert.Equal(50_000_000, box.Maximum);
    }

    [Fact]
    public void BottomAnchoredButtonsAreSeparatedFromTheBody()
    {
        var vm = Build(SpellChoose());

        Assert.Contains(vm.BottomControls, c => c.Id == "chooseSpell");
        Assert.DoesNotContain(vm.Controls, c => c.Id == "chooseSpell");
    }

    [Fact]
    public void FullWidthControlsSpanEveryColumn()
    {
        var vm = Build(BankDebt());
        var footnote = Find(vm, "label3")!;

        Assert.True(footnote.FullWidth);
        Assert.Equal(vm.Columns, footnote.ColumnSpan);
    }

    [Fact]
    public void TheTitleFallsBackToTheDialogIdWhenTheServerSendsNone()
    {
        var vm = new ServerDialogViewModel("bank_debt");
        vm.Apply(State("bank_debt", title: null));

        Assert.Equal("bank_debt", vm.Title);
    }

    // ── Merge, not rebuild ───────────────────────────────────────────────────

    [Fact]
    public void ADeltaDoesNotDiscardWhatTheUserHasTyped()
    {
        // The server re-sends its own default on every block. Rebuilding, or
        // re-seeding, would make the field impossible to fill in.
        var vm  = Build(BankDebt());
        var box = (DialogTextBoxViewModel)Find(vm, "bank_amount")!;
        box.Value = "12345";

        vm.Apply(BankDebt());

        Assert.Equal("12345", box.Value);
        Assert.Same(box, Find(vm, "bank_amount"));   // same instance, not replaced
    }

    [Fact]
    public void ADeltaDoesNotDiscardADropdownSelection()
    {
        var vm    = Build(BankDebt());
        var combo = (DialogComboViewModel)Find(vm, "province1")!;
        combo.Selected = "Ilithi";

        vm.Apply(BankDebt());

        Assert.Equal("Ilithi", combo.Selected);
    }

    [Fact]
    public void ControlsTheServerStopsSendingAreRemoved()
    {
        var vm = Build(BankDebt());
        Assert.NotNull(Find(vm, "province1"));

        vm.Apply(State("bank_debt", controls: [Label("only", "Just this")]));

        Assert.Null(Find(vm, "province1"));
        Assert.NotNull(Find(vm, "only"));
    }

    [Fact]
    public void AnIdReusedForADifferentKindIsReplaced()
    {
        var vm = Build(State("d", controls: [Label("thing", "text")]));
        Assert.IsType<DialogLabelViewModel>(Find(vm, "thing"));

        vm.Apply(State("d", controls: [Ctl(DialogControlType.EditBox, "thing")]));

        Assert.IsType<DialogTextBoxViewModel>(Find(vm, "thing"));
        Assert.Single(vm.Controls);
    }

    // ── Activation ───────────────────────────────────────────────────────────

    [Fact]
    public void ClickingAButtonResolvesItsPlaceholdersFromLiveInput()
    {
        var vm = Build(BankDebt());
        ((DialogComboViewModel)Find(vm, "province1")!).Selected = "Ilithi";
        ((DialogTextBoxViewModel)Find(vm, "bank_amount")!).Value = "5000";

        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;

        vm.Activate("sendbankCommand");

        var action = Assert.Single(sent);
        Assert.Equal("bank debt 3 5000", action.Value);   // Ilithi → data value 3
        Assert.Equal(ServerDialogActionKind.GameCommand, action.Kind);
    }

    [Fact]
    public void AButtonWithAnUnsatisfiedPlaceholderSendsNothing()
    {
        var vm = Build(State("d", controls:
        [
            Ctl(DialogControlType.CmdButton, "go", value: "Go", cmd: "cast %nosuch%"),
        ]));

        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;

        vm.Activate("go");

        Assert.Empty(sent);
        Assert.NotEmpty(vm.Preview("go").UnresolvedTokens);
    }

    [Fact]
    public void ACloseButtonAsksForTheWindowToClose()
    {
        var vm = Build(SpellChoose());
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.Activate("chooseSpell");

        Assert.True(closed);
    }

    [Fact]
    public void ACloseButtonWithACommandSendsItBeforeClosing()
    {
        var vm = Build(BankDebt());
        var sent = new List<ServerDialogAction>();
        var closed = false;
        vm.ActionRequested += sent.Add;
        vm.CloseRequested  += () => closed = true;

        vm.Activate("sendbankCommand2");

        Assert.Equal("bank debt 1 all", Assert.Single(sent).Value);
        Assert.True(closed);
    }

    [Fact]
    public void TheConfiguredSeparatorIsEscapedNotAHardcodedSemicolon()
    {
        var vm = Build(State("d", controls:
        [
            Ctl(DialogControlType.CmdButton, "go", value: "Go", cmd: "look, quit"),
        ]));
        vm.SeparatorChar = ',';

        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;
        vm.Activate("go");

        Assert.Equal(@"look\, quit", Assert.Single(sent).Value);
    }

    [Fact]
    public void AUrlCommandResolvesToAWebLink()
    {
        var vm = Build(State("quick-simu", controls:
        [
            Ctl(DialogControlType.Link, "l1", value: "Game Info", cmd: "url:/dr/info/"),
        ]));

        var sent = new List<ServerDialogAction>();
        vm.ActionRequested += sent.Add;
        vm.Activate("l1");

        var action = Assert.Single(sent);
        Assert.Equal(ServerDialogActionKind.WebLink, action.Kind);
        Assert.Equal("https://www.play.net/dr/info/", action.Value);
    }

    // ── Radio groups ─────────────────────────────────────────────────────────

    [Fact]
    public void SelectingARadioClearsTheRestOfItsGroup()
    {
        var vm = Build(State("d", controls:
        [
            Ctl(DialogControlType.Radio, "ext", cmd: "_injury 0", attrs: new() { ["group"] = "g" }),
            Ctl(DialogControlType.Radio, "int", cmd: "_injury 1", attrs: new() { ["group"] = "g" }),
        ]));

        vm.SelectRadio("ext");
        vm.SelectRadio("int");

        Assert.False(((DialogRadioViewModel)Find(vm, "ext")!).IsChecked);
        Assert.True(((DialogRadioViewModel)Find(vm, "int")!).IsChecked);
    }

    [Fact]
    public void SeparateGroupsDoNotInterfere()
    {
        var vm = Build(State("d", controls:
        [
            Ctl(DialogControlType.Radio, "a1", attrs: new() { ["group"] = "one" }),
            Ctl(DialogControlType.Radio, "b1", attrs: new() { ["group"] = "two" }),
        ]));

        vm.SelectRadio("a1");
        vm.SelectRadio("b1");

        Assert.True(((DialogRadioViewModel)Find(vm, "a1")!).IsChecked);
        Assert.True(((DialogRadioViewModel)Find(vm, "b1")!).IsChecked);
    }

    // ── Streams ──────────────────────────────────────────────────────────────

    [Fact]
    public void StreamBoxesTakeTheirTextFromTheDialogsStreams()
    {
        var vm = new ServerDialogViewModel("spellChoose");
        vm.Apply(StateWithStream());

        var panel = (DialogStreamViewModel)Find(vm, "spells")!;
        Assert.Equal("Fire Shards\nTingle", panel.Text);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ServerDialogViewModel Build(ServerDialogState state)
    {
        var vm = new ServerDialogViewModel(state.Id);
        vm.Apply(state);
        return vm;
    }

    private static DialogControlViewModel? Find(ServerDialogViewModel vm, string id) =>
        vm.Controls.Concat(vm.BottomControls)
          .FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drives real state through the real engine, so these tests break
    /// if the engine's merge or the grid inference changes underneath.</summary>
    private static ServerDialogState State(
        string id, string? title = "Test", IReadOnlyList<DialogControl>? controls = null)
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new OpenDialogEvent(
            id, title ?? "", "center", "300", "200", false, "dynamic", "<openDialog/>"));
        engine.Observe(new DialogDataEvent(
            id, controls ?? [], Clear: false, "<dialogData/>"));
        return engine.Get(id)!;
    }

    private static ServerDialogState StateWithStream()
    {
        var engine = new ServerDialogEngine();
        engine.Observe(new DialogDataEvent("spellChoose",
            [Ctl(DialogControlType.StreamBox, "spells", top: "40", left: "15")],
            Clear: false, "<dialogData/>"));
        engine.Observe(new DynaStreamEvent("spells", "Fire Shards\nTingle"));
        return engine.Get("spellChoose")!;
    }

    private static ServerDialogState BankDebt() => State("bank_debt", "Provincial Debt",
    [
        Label("label2", "Province:", top: "10", left: "10"),
        Ctl(DialogControlType.DropDownBox, "province1", value: "Zoluren",
            top: "10", left: "75", attrs: new()
            {
                ["content_text"]  = "Zoluren,Therengia,Ilithi,Qi,Forfedhdar",
                ["content_value"] = "1,2,3,4,5",
            }),
        Label("label", "Amount:", top: "45", left: "10"),
        Ctl(DialogControlType.UpDownEditBox, "bank_amount", value: "1",
            top: "45", left: "75", attrs: new() { ["min"] = "1", ["max"] = "50000000" }),
        Ctl(DialogControlType.CloseButton, "sendbankCommand", value: "Pay",
            cmd: "bank debt %province1% %bank_amount%", top: "80", left: "75"),
        Ctl(DialogControlType.CloseButton, "sendbankCommand2", value: "ALL",
            cmd: "bank debt %province1% all", top: "80", left: "135"),
        Label("label3", "Amount is in coppers.", top: "100", left: "75"),
    ]);

    private static ServerDialogState SpellChoose() => State("spellChoose", "Choose A New Spell",
    [
        Ctl(DialogControlType.StreamBox, "spells", top: "40", left: "15"),
        Ctl(DialogControlType.CloseButton, "chooseSpell", value: "Close",
            top: "-10", left: "0", align: "s"),
    ]);

    private static DialogControl Label(string id, string value, string? top = null, string? left = null) =>
        Ctl(DialogControlType.Label, id, value: value, top: top, left: left);

    private static DialogControl Ctl(
        DialogControlType type, string id, string? value = null, string? cmd = null,
        string? top = null, string? left = null, string? align = null,
        Dictionary<string, string>? attrs = null)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attrs is not null) foreach (var kv in attrs) bag[kv.Key] = kv.Value;
        return new DialogControl(type, id, value, null, cmd, left, top, null, null, align, bag);
    }
}
