using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Conditions that fail to PARSE surface a one-time "[script] …" advisory
/// instead of failing silently. Unbalanced parentheses additionally get
/// Genie 4's tolerance — the expression is auto-balanced and evaluated
/// (community scripts shipped both too-few- and too-many-close shapes for
/// years; see ConditionParenLeniencyTests) — while anything still unparseable
/// after balancing is treated as false with the "bad condition" warning.
/// Conditions that merely evaluate false, and the benign empty-substitution
/// case (<c>if (%unset)</c> → "()"), stay silent as before.
/// </summary>
public class ConditionParseWarningTests
{
    private static List<string> RunFixture(string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_condtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Unbalanced_parens_auto_balance_and_warn()
    {
        // The original community repro: two '(' one ')'. G4 evaluated it (the
        // values DO match), G5 used to false it silently — now it auto-balances
        // and says so.
        var o = RunFixture(
            "var a armband\n" +
            "var b armband\n" +
            "if ((\"%a\" = \"%b\") then echo MATCH\n" +
            "else echo NOMATCH\n");

        Assert.Contains(o, l => l.Contains("auto-balanced"));
        Assert.Contains("MATCH", o);
        Assert.DoesNotContain("NOMATCH", o);
    }

    [Fact]
    public void Unbalanced_condition_in_a_loop_warns_once()
    {
        var o = RunFixture(
            "var i 0\n" +
            "top:\n" +
            "math i add 1\n" +
            "if ((%i > 99) then echo NEVER\n" +
            "if (%i < 3) then goto top\n" +
            "echo done\n");

        Assert.Contains("done", o);
        Assert.DoesNotContain("NEVER", o);   // auto-balanced, still false
        Assert.Equal(1, o.Count(l => l.Contains("auto-balanced")));
    }

    [Fact]
    public void Still_unparseable_after_balancing_warns_bad_condition()
    {
        // Balancing appends '))' but the stray '((' group is trailing junk the
        // grammar rejects either way → hard "bad condition", treated as false.
        var o = RunFixture(
            "if (1 = 1) (( then echo HIT\n" +
            "echo done\n");

        Assert.Contains("done", o);
        Assert.DoesNotContain("HIT", o);
        Assert.Contains(o, l => l.Contains("bad condition"));
    }

    [Fact]
    public void Empty_substitution_stays_silent()
    {
        // The designed-for benign case: an unset %var stays LITERAL (G4
        // parity), and a bare string section is never true in G4's evaluator
        // (Eval.cs GetBooleanResult only counts NumberType sections) — so
        // `if (%unsetvar)` is false, and NOT a warning.
        var o = RunFixture(
            "if (%unsetvar) then echo Y\n" +
            "echo after\n");

        Assert.Contains("after", o);
        Assert.DoesNotContain("Y", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
        Assert.DoesNotContain(o, l => l.Contains("auto-balanced"));
    }

    [Fact]
    public void Missing_then_from_quote_typo_hints_unbalanced_quotes()
    {
        // #135: `("%v = "baz")` — quote typo swallows the real `then`, so the
        // scanner can't find it. The error should point at the quotes.
        var o = RunFixture(
            "var testvar foo\n" +
            "if ((\"%testvar\" = \"foo\") || (\"%testvar\" = \"bar\") || (\"%testvar = \"baz\")) then echo Y\n" +
            "else echo N\n");

        Assert.Contains(o, l => l.Contains("missing 'then'")
                             && l.Contains("unbalanced \" quotes?"));
        Assert.Contains("N", o);
    }

    [Fact]
    public void Balanced_condition_still_matches()
    {
        // The corrected form of the community repro evaluates true, no warning.
        var o = RunFixture(
            "var a armband\n" +
            "var b armband\n" +
            "if (\"%a\" = \"%b\") then echo MATCH\n" +
            "else echo NOMATCH\n");

        Assert.Contains("MATCH", o);
        Assert.DoesNotContain("NOMATCH", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
        Assert.DoesNotContain(o, l => l.Contains("auto-balanced"));
    }
}
