using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #437: Genie 4 expands <c>$variables</c> in every line a script sends to
/// the game, after the script's own substitution. A <c>$name</c> that arrives
/// inside a substituted value (a map move handed to automapper.cmd as an
/// argument, then sent with <c>put %movement</c>) was sent as the name.
/// </summary>
public class ScriptSendExpandsGlobalsTests
{
    private static List<string> Run(string body, IReadOnlyList<string>? args = null,
                                    Action<ScriptEngine>? setup = null)
    {
        var sent = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_sendvars_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sendvars.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => sent.Add(c), echo: _ => { });
            engine.Globals["shard.pw"] = "swordfish";
            setup?.Invoke(engine);
            engine.TryStart("sendvars", new List<string>(args ?? Array.Empty<string>()));
            // Each game-bound send holds the type-ahead budget until a prompt.
            for (int i = 0; i < 40; i++) { engine.Tick(); engine.OnPrompt(); }
            return sent;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Put_expands_a_global_that_arrived_inside_a_local()
    {
        // The automapper.cmd shape: the move is an argument, sent with put %var.
        var sent = Run("var movement %1\nput %movement\n", new[] { "'$shard.pw" });

        Assert.Equal(new[] { "'swordfish" }, sent);
    }

    [Fact]
    public void Every_semicolon_segment_is_expanded()
    {
        var sent = Run("var movement %1\nput %movement\n",
                       new[] { "knock door;'$shard.pw;go door" });

        Assert.Equal(new[] { "knock door", "'swordfish", "go door" }, sent);
    }

    [Fact]
    public void Send_expands_too()
    {
        var sent = Run("var movement %1\nsend %movement\n", new[] { "'$shard.pw" });

        Assert.Equal(new[] { "'swordfish" }, sent);
    }

    [Fact]
    public void A_bare_line_expands_too()
    {
        var sent = Run("var movement %1\n%movement\n", new[] { "'$shard.pw" });

        Assert.Equal(new[] { "'swordfish" }, sent);
    }

    [Fact]
    public void Move_expands_too()
    {
        var sent = Run("var movement %1\nmove %movement\n", new[] { "go $shard.pw door" });

        Assert.Equal(new[] { "go swordfish door" }, sent);
    }

    [Fact]
    public void An_undefined_name_stays_as_written()
    {
        var sent = Run("var movement %1\nput %movement\n", new[] { "'$nosuch.pw" });

        Assert.Equal(new[] { "'$nosuch.pw" }, sent);
    }

    [Fact]
    public void A_dollar_amount_stays_as_written()
    {
        // `$5` is an ordinary, undefined name at this level, not an argument slot:
        // the script's own arguments must not leak into a value's text.
        var sent = Run("var line %2\nput %line\n", new[] { "first", "say it costs $5 and $1" });

        Assert.Equal(new[] { "say it costs $5 and $1" }, sent);
    }

    [Fact]
    public void A_direct_global_is_unchanged()
    {
        // Already resolved by the script's own pass; the second pass finds nothing.
        var sent = Run("put say $shard.pw\n");

        Assert.Equal(new[] { "say swordfish" }, sent);
    }

    [Fact]
    public void The_expanded_value_is_not_expanded_again()
    {
        var sent = Run("var movement %1\nput %movement\n", new[] { "say $outer" },
                       e => { e.Globals["outer"] = "$shard.pw"; });

        Assert.Equal(new[] { "say $shard.pw" }, sent);
    }
}
