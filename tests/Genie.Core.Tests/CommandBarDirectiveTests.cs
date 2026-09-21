using System;
using Genie.Core.Commanding;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #348 — Genie 4's two command-bar directives in SENT text: `@` places
/// the line in the command input with the caret where the marker was, and `\x`
/// clears the input first. Genie 5 implemented neither, so the text went to
/// DragonRealms verbatim.
///
/// <para>This is not an exotic corner. The stock `macros.cfg` in the reference
/// settings tree ships <c>#macro {F1} {look @}</c> — so anyone importing a
/// Genie 4 settings folder pressed F1 and got "Please rephrase that command."
/// on their first keypress.</para>
/// </summary>
public class CommandBarDirectiveTests
{
    // ── The overwhelmingly common case: no directive, no interference ────────

    [Theory]
    [InlineData("look")]
    [InlineData("get my pack")]
    [InlineData("#echo hello")]
    [InlineData("send email to bob")]      // contains no marker despite the words
    public void OrdinaryCommandsAreNotDirectives(string text)
    {
        Assert.False(CommandBarDirective.TryParse(text, out _));
        Assert.Equal(text, CommandBarDirective.Unescape(text));
    }

    [Fact]
    public void EmptyAndNullAreNotDirectives()
    {
        Assert.False(CommandBarDirective.TryParse("", out _));
        Assert.False(CommandBarDirective.TryParse(null, out _));
    }

    // ── @ — the caret marker ─────────────────────────────────────────────────

    /// <summary>The shipped macro. F1 must put "look " in the bar with the
    /// caret ready for a target, and send nothing.</summary>
    [Fact]
    public void TheStockF1MacroPutsLookInTheBarWithATrailingCaret()
    {
        Assert.True(CommandBarDirective.TryParse("look @", out var r));

        Assert.Equal("look ", r.Text);
        Assert.Equal(5, r.CaretIndex);      // after the space, where the @ was
        Assert.False(r.ClearFirst);
    }

    [Fact]
    public void TheMarkerCanSitMidTextAndTheCaretLandsThere()
    {
        Assert.True(CommandBarDirective.TryParse("put @ in my pack", out var r));

        Assert.Equal("put  in my pack", r.Text);
        Assert.Equal(4, r.CaretIndex);
    }

    /// <summary>Only the FIRST marker positions the caret; a second is ordinary
    /// text, so a line cannot ask for two caret positions.</summary>
    [Fact]
    public void OnlyTheFirstMarkerPositionsTheCaret()
    {
        Assert.True(CommandBarDirective.TryParse("a@b@c", out var r));

        Assert.Equal("ab@c", r.Text);
        Assert.Equal(1, r.CaretIndex);
    }

    // ── \x — clear the input first ────────────────────────────────────────────

    [Fact]
    public void ClearAloneReplacesTheBarWithTheCaretAtTheEnd()
    {
        Assert.True(CommandBarDirective.TryParse(@"\xlook", out var r));

        Assert.Equal("look", r.Text);
        Assert.Equal(4, r.CaretIndex);
        Assert.True(r.ClearFirst);
    }

    [Fact]
    public void ClearAndMarkerTogetherReplaceAndPositionTheCaret()
    {
        Assert.True(CommandBarDirective.TryParse(@"\xlook @ carefully", out var r));

        Assert.Equal("look  carefully", r.Text);
        Assert.Equal(5, r.CaretIndex);
        Assert.True(r.ClearFirst);
    }

    // ── \@ — the escape ───────────────────────────────────────────────────────

    /// <summary>An escaped marker is a literal @ bound for the game — it must
    /// NOT divert the line into the command bar.</summary>
    [Fact]
    public void AnEscapedMarkerIsNotADirective()
    {
        Assert.False(CommandBarDirective.TryParse(@"look \@home", out _));
    }

    /// <summary>…and the backslash must not survive into what DR receives.
    /// Genie 4 rewrote the escape to a bare letter x here, which suppressed the
    /// diversion but corrupted the text (`look \@home` would arrive as `look
    /// xhome`). We implement the documented intent instead.</summary>
    [Fact]
    public void AnEscapedMarkerReachesTheGameAsAPlainAt()
    {
        Assert.Equal("look @home", CommandBarDirective.Unescape(@"look \@home"));
    }

    /// <summary>An escape alongside a real marker: the real one still wins, and
    /// the escaped one survives as text.</summary>
    [Fact]
    public void AnEscapeAndARealMarkerCoexist()
    {
        Assert.True(CommandBarDirective.TryParse(@"mail \@bob @", out var r));

        Assert.Equal("mail @bob ", r.Text);
        Assert.Equal(10, r.CaretIndex);
    }
}
