using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="ScriptEngine.ParseSendDelay"/> — the leading-delay / eager-marker
/// parser shared by the in-script <c>send</c> verb and <c>#send</c>. Covers the
/// numeric-delay rules AND the bare-<c>-verb</c> "fire eagerly" strip that lets
/// community idioms like <c>send -touch my orb;-0.05 cast</c> reach the game
/// instead of bouncing as "Please rephrase".
/// </summary>
public class ParseSendDelayTests
{
    // ---- bare leading '-' verb → eager (strip dash, zero delay) --------------

    [Theory]
    [InlineData("-cast", "cast")]
    [InlineData("-gesture", "gesture")]
    [InlineData("-touch my orb", "touch my orb")]
    [InlineData("-release spell", "release spell")]
    [InlineData("-health", "health")]
    public void Bare_dash_verb_is_stripped_and_fires_eagerly(string seg, string expectedCmd)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(0.0, delay);
        Assert.Equal(expectedCmd, cmd);
    }

    // ---- numeric delays unchanged (incl. negative = eager) ------------------

    [Theory]
    [InlineData("-0.05 cast", -0.05, "cast")]
    [InlineData("0.5 unload my bow", 0.5, "unload my bow")]
    [InlineData("-1 flee", -1.0, "flee")]
    [InlineData("2 stand", 2.0, "stand")]
    public void Numeric_delay_is_parsed_and_command_follows(string seg, double expectedDelay, string expectedCmd)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(expectedDelay, delay);
        Assert.Equal(expectedCmd, cmd);
    }

    // ---- regression guards: things that must NOT be touched -----------------

    [Theory]
    [InlineData("gesture")]        // plain verb, no marker
    [InlineData("release mana")]   // plain multiword
    [InlineData("2nd")]            // starts with digit but no boundary → literal
    [InlineData("5fire")]          // number glued to word → literal, not a delay
    [InlineData("swap-weapon")]    // hyphen mid-token is not a leading marker
    public void Non_marker_segments_pass_through_unchanged(string seg)
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay(seg);
        Assert.Equal(0.0, delay);
        Assert.Equal(seg, cmd);
    }

    [Fact]
    public void Lone_dash_yields_empty_command()
    {
        var (delay, cmd) = ScriptEngine.ParseSendDelay("-");
        Assert.Equal(0.0, delay);
        Assert.Equal("", cmd);
    }
}
