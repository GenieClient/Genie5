using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="ScriptEngine.ParseSendDelay"/> — the leading-delay parser shared
/// by the in-script <c>send</c> verb and <c>#send</c>. Handles only the
/// dashless <c>N cmd</c> form; dash-prefixed segments are Genie 4's quick-send
/// form (a POSITIVE RT-gated pause) and are normalized by
/// <see cref="Genie.Core.Commanding.QuickSend"/> BEFORE either call site
/// invokes this parser (public #278 — this replaced the earlier "leading '-'
/// = eager/no-wait" reading, which had no Genie 4 basis). A stray dash
/// segment reaching this parser passes through literal.
/// </summary>
public class ParseSendDelayTests
{
    // ---- dashless numeric delays -------------------------------------------

    [Theory]
    [InlineData("0.5 unload my bow", 0.5, "unload my bow")]
    [InlineData("2 stand", 2.0, "stand")]
    [InlineData("5 $lastcommand", 5.0, "$lastcommand")]
    public void Numeric_delay_is_parsed_and_command_follows(string seg, double expectedDelay, string expectedCmd)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(expectedDelay, delay);
        Assert.Equal(expectedCmd, cmd);
    }

    // ---- dash segments: quick-send is the callers' job → literal here -------

    [Theory]
    [InlineData("-cast")]
    [InlineData("-touch my orb")]
    [InlineData("-0.05 cast")]
    [InlineData("-1 flee")]
    [InlineData("-")]
    public void Dash_segments_pass_through_literal(string seg)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(0.0, delay);
        Assert.Equal(seg, cmd);
    }

    // ---- regression guards: things that must NOT be touched -----------------

    [Theory]
    [InlineData("gesture")]        // plain verb, no delay
    [InlineData("release mana")]   // plain multiword
    [InlineData("2nd")]            // starts with digit but no boundary → literal
    [InlineData("5fire")]          // number glued to word → literal, not a delay
    [InlineData("swap-weapon")]    // hyphen mid-token is not a marker
    public void Non_delay_segments_pass_through_unchanged(string seg)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(0.0, delay);
        Assert.Equal(seg, cmd);
    }
}
