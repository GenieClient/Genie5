using System;
using System.Collections.Generic;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #156 Phase 1 — resolving a dialog control's <c>cmd</c> at click time.
/// The bank_debt fixture (journal, 2026-08-30) is the live proof that
/// <c>%placeholder%</c> tokens name sibling control ids.
/// </summary>
public class ServerDialogCommandTests
{
    // ── The live bank_debt fixture ───────────────────────────────────────────

    private static IReadOnlyList<DialogControl> BankDebtControls() =>
    [
        Ctl(DialogControlType.DropDownBox, "province1", value: "Zoluren", attrs: new()
        {
            ["content_text"]  = "Zoluren,Therengia,Ilithi,Qi,Forfedhdar",
            ["content_value"] = "1,2,3,4,5",
        }),
        Ctl(DialogControlType.UpDownEditBox, "bank_amount", value: "1",
            attrs: new() { ["min"] = "1", ["max"] = "50000000" }),
    ];

    [Fact]
    public void PlaceholdersResolveFromSiblingControls()
    {
        var action = ServerDialogCommand.Resolve(
            "bank debt %province1% %bank_amount%",
            BankDebtControls(),
            new Dictionary<string, string> { ["province1"] = "Ilithi", ["bank_amount"] = "5000" });

        // Ilithi is the third entry, so its DATA value is 3 — the command wants
        // the value list, not the display text.
        Assert.Equal("bank debt 3 5000", action.Value);
        Assert.Equal(ServerDialogActionKind.GameCommand, action.Kind);
        Assert.True(action.CanSend);
    }

    [Fact]
    public void ServerSuppliedValuesApplyBeforeTheUserTouchesAnything()
    {
        var action = ServerDialogCommand.Resolve(
            "bank debt %province1% %bank_amount%", BankDebtControls());

        Assert.Equal("bank debt 1 1", action.Value);   // Zoluren → 1, amount 1
    }

    [Fact]
    public void ACommandWithNoPlaceholdersPassesThrough()
    {
        var action = ServerDialogCommand.Resolve(
            "bank debt %province1% all", BankDebtControls());

        Assert.Equal("bank debt 1 all", action.Value);
    }

    // ── Control kinds ────────────────────────────────────────────────────────

    [Fact]
    public void ADropDownResolvesToItsDataValueNotItsLabel()
    {
        var action = ServerDialogCommand.Resolve("go %province1%", BankDebtControls(),
            new Dictionary<string, string> { ["province1"] = "Forfedhdar" });

        Assert.Equal("go 5", action.Value);
    }

    [Fact]
    public void ADropDownWithoutAValueListFallsBackToItsText()
    {
        var controls = new[] { Ctl(DialogControlType.DropDownBox, "pick", value: "Second") };
        Assert.Equal("choose Second", ServerDialogCommand.Resolve("choose %pick%", controls).Value);
    }

    [Fact]
    public void ACheckBoxContributesItsCheckedOrUncheckedValue()
    {
        var controls = new[]
        {
            Ctl(DialogControlType.CheckBox, "hidden",
                attrs: new() { ["checked_value"] = "on", ["unchecked_value"] = "off" }),
        };

        Assert.Equal("set off", ServerDialogCommand.Resolve("set %hidden%", controls).Value);
        Assert.Equal("set on", ServerDialogCommand.Resolve("set %hidden%", controls,
            new Dictionary<string, string> { ["hidden"] = "t" }).Value);
    }

    [Fact]
    public void ACheckBoxWithoutExplicitValuesUsesOneAndZero()
    {
        var controls = new[] { Ctl(DialogControlType.CheckBox, "flag") };

        Assert.Equal("set 0", ServerDialogCommand.Resolve("set %flag%", controls).Value);
        Assert.Equal("set 1", ServerDialogCommand.Resolve("set %flag%", controls,
            new Dictionary<string, string> { ["flag"] = "1" }).Value);
    }

    [Fact]
    public void ARadioGroupResolvesToTheCheckedMember()
    {
        // The group name is the token; whichever member is on supplies the value.
        var controls = new[]
        {
            Ctl(DialogControlType.Radio, "ext", cmd: "_injury 0 -1",
                attrs: new() { ["group"] = "injrRad" }),
            Ctl(DialogControlType.Radio, "int", cmd: "_injury 1 -1",
                attrs: new() { ["group"] = "injrRad", ["checked"] = "t" }),
        };

        Assert.Equal("_injury 1 -1", ServerDialogCommand.Resolve("%injrRad%", controls).Value);
    }

    [Fact]
    public void AnUncheckedRadioGroupLeavesItsTokenUnresolved()
    {
        var controls = new[]
        {
            Ctl(DialogControlType.Radio, "a", cmd: "x", attrs: new() { ["group"] = "g" }),
        };

        var action = ServerDialogCommand.Resolve("do %g%", controls);
        Assert.Equal("g", Assert.Single(action.UnresolvedTokens));
        Assert.False(action.CanSend);
    }

    [Fact]
    public void AnEditBoxContributesWhatTheUserTyped()
    {
        var controls = new[] { Ctl(DialogControlType.EditBox, "note") };

        Assert.Equal("say hello there", ServerDialogCommand.Resolve("say %note%", controls,
            new Dictionary<string, string> { ["note"] = "hello there" }).Value);
    }

    // ── Unresolved tokens ────────────────────────────────────────────────────

    [Fact]
    public void AnUnknownTokenIsReportedAndLeftVisible()
    {
        var action = ServerDialogCommand.Resolve("bank debt %nosuch% 5", BankDebtControls());

        Assert.Equal("nosuch", Assert.Single(action.UnresolvedTokens));
        Assert.False(action.CanSend);
        Assert.Contains("%nosuch%", action.Value);   // visible, not silently blank
    }

    [Fact]
    public void OrdinaryPercentagesAreNotMistakenForTokens()
    {
        // A token cannot contain whitespace, so prose survives.
        var action = ServerDialogCommand.Resolve("say I gave 50% of 100% away");

        Assert.Empty(action.UnresolvedTokens);
        Assert.Equal("say I gave 50% of 100% away", action.Value);
    }

    // ── Separator escaping ───────────────────────────────────────────────────

    [Fact]
    public void ATypedValueCannotSplitIntoASecondCommand()
    {
        var controls = new[] { Ctl(DialogControlType.EditBox, "note") };

        var action = ServerDialogCommand.Resolve("say %note%", controls,
            new Dictionary<string, string> { ["note"] = "hi; quit" });

        Assert.Equal(@"say hi\; quit", action.Value);
    }

    [Fact]
    public void TheServersOwnTemplateCannotSplitEither()
    {
        var action = ServerDialogCommand.Resolve("look; quit");
        Assert.Equal(@"look\; quit", action.Value);
    }

    [Fact]
    public void EscapingFollowsTheConfiguredSeparator()
    {
        // The separator is user-configurable, so this must never hardcode ';'.
        var action = ServerDialogCommand.Resolve("look, quit", separator: ',');

        Assert.Equal(@"look\, quit", action.Value);
        Assert.Equal("look; quit", ServerDialogCommand.Resolve("look; quit", separator: ',').Value);
    }

    // ── The url: scheme ──────────────────────────────────────────────────────

    [Fact]
    public void ARootRelativeUrlResolvesAgainstPlayNet()
    {
        var action = ServerDialogCommand.Resolve("url:/dr/info/");

        Assert.Equal(ServerDialogActionKind.WebLink, action.Kind);
        Assert.Equal("https://www.play.net/dr/info/", action.Value);
    }

    [Fact]
    public void TheBounceRedirectorSurvivesIntact()
    {
        // Verbatim from the quick-simu journal entry.
        var action = ServerDialogCommand.Resolve(
            "url:/bounce/redirect.asp?URL=https://store.play.net/store/purchase/dr");

        Assert.Equal(
            "https://www.play.net/bounce/redirect.asp?URL=https://store.play.net/store/purchase/dr",
            action.Value);
    }

    [Fact]
    public void AnAbsoluteUrlIsLeftAlone()
    {
        Assert.Equal("https://elanthipedia.play.net/Main_Page",
            ServerDialogCommand.Resolve("url:https://elanthipedia.play.net/Main_Page").Value);
    }

    [Fact]
    public void AWebLinkIsNotSeparatorEscaped()
    {
        // Escaping a URL's query string would corrupt it; only game commands
        // pass through the command engine's split.
        var action = ServerDialogCommand.Resolve("url:/x?a=1;b=2");

        Assert.Equal("https://www.play.net/x?a=1;b=2", action.Value);
    }

    [Fact]
    public void AQuickBarGameCommandIsNotAWebLink()
    {
        // The same quick bar mixes both: Forums is `bbs`, Game Info is a url:.
        var action = ServerDialogCommand.Resolve("bbs");

        Assert.Equal(ServerDialogActionKind.GameCommand, action.Kind);
        Assert.Equal("bbs", action.Value);
    }

    // ── Degenerate input ─────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyCommandDoesNothing()
    {
        foreach (var cmd in new string?[] { null, "", "   " })
        {
            var action = ServerDialogCommand.Resolve(cmd);
            Assert.Equal(ServerDialogActionKind.None, action.Kind);
            Assert.False(action.CanSend);
        }
    }

    [Fact]
    public void ControlsWithoutIdsAreSkippedRatherThanThrowing()
    {
        var controls = new[] { Ctl(DialogControlType.EditBox, "") };

        Assert.Equal("say %x%", ServerDialogCommand.Resolve("say %x%", controls).Value);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private static DialogControl Ctl(
        DialogControlType type, string id, string? value = null, string? cmd = null,
        Dictionary<string, string>? attrs = null)
    {
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (attrs is not null) foreach (var kv in attrs) bag[kv.Key] = kv.Value;
        return new DialogControl(type, id, value, null, cmd, null, null, null, null, null, bag);
    }
}
