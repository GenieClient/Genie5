using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 undefined-variable parity: a variable that resolves to nothing stays
/// LITERAL in the substituted text — G4's shrink loops end <c>return Line;</c>
/// unchanged for both sigils (local %vars: Script.cs ParseVariable:2469;
/// $globals: Globals.cs ParseVariable:306).
///
/// The bug this locks out (smoke 2026-08-03): substituting empty instead turned
/// uber.cmd's never-defined <c>%superjump</c> inside
/// <c>matchre("%command", "\b(?i)%superjump\b")</c> (uber.cmd:4130) into the
/// degenerate always-match <c>\b(?i)\b</c>, so the superjump block fired on
/// every command and its <c>goto %2</c> (no arg 2) killed the script with
/// "unknown label:".
///
/// In conditions the literal remnant is a plain string atom (ScriptExpression
/// ParseSigilLiteral): G4's Eval makes ordering comparisons with a non-numeric
/// operand silently false (Eval.cs:744) and equality a string compare — no
/// "bad condition" warnings.
/// </summary>
public class UndefinedVarLiteralTests
{
    private static List<string> Run(string body,
                                    IDictionary<string, string>? globals = null,
                                    IReadOnlyList<string>? args = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_udvlit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            if (globals is not null)
                foreach (var (k, v) in globals) engine.Globals[k] = v;
            engine.TryStart("t", args ?? new List<string>());
            for (int i = 0; i < 300; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Uber_superjump_repro_undefined_var_in_regex_never_matches()
    {
        // The exact uber.cmd:4130 shape. %superjump is never defined, so the
        // pattern must stay literally "\b(?i)%superjump\b" and NOT match the
        // command "quick". Before the fix the block fired and goto'd nowhere.
        const string body =
            "var command quick\n" +
            "if matchre(\"%command\", \"\\b(?i)%superjump\\b\") then echo SUPERJUMP-FIRED\n" +
            "echo done\n";

        var o = Run(body);

        Assert.Contains("done", o);
        Assert.DoesNotContain("SUPERJUMP-FIRED", o);
        Assert.DoesNotContain(o, l => l.Contains("unknown label"));
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
    }

    [Fact]
    public void Undefined_local_stays_literal_in_echo()
    {
        var o = Run("echo V=[%never_set]\n");
        Assert.Contains("V=[%never_set]", o);
    }

    [Fact]
    public void Undefined_global_stays_literal_in_echo()
    {
        var o = Run("echo G=[$never_set_global]\n");
        Assert.Contains("G=[$never_set_global]", o);
    }

    [Fact]
    public void Defined_vars_still_resolve_normally()
    {
        var o = Run("var x hello\necho X=[%x]\necho Y=[$srv]\n",
                    new Dictionary<string, string> { ["srv"] = "world" });
        Assert.Contains("X=[hello]", o);
        Assert.Contains("Y=[world]", o);
    }

    [Theory]
    // Ordering comparison with a literal remnant: silently false (G4 Eval.cs:744
    // makes every non-numeric ordering compare "0"; our string fallback orders
    // "$..."/"%..." below digits, same outcome), and never a warning.
    [InlineData("if ($Undefined.Ranks >= 1750) then echo HIT\necho done\n")]
    [InlineData("if (%undefined_count > 3) then echo HIT\necho done\n")]
    // Equality against a number: string-compare, false.
    [InlineData("if ($Undefined.active = 1) then echo HIT\necho done\n")]
    public void Undefined_var_conditions_are_silently_false(string body)
    {
        var o = Run(body);
        Assert.Contains("done", o);
        Assert.DoesNotContain("HIT", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
    }

    [Fact]
    public void Undefined_var_in_contains_is_false_not_matched()
    {
        // mm_train's guard shape: contains("%pouchname", "gem") with pouchname
        // never pushed — the literal "%pouchname" doesn't contain "gem".
        const string body =
            "if contains(\"%pouchname\", \"gem\") then echo HASGEM\n" +
            "echo done\n";
        var o = Run(body);
        Assert.Contains("done", o);
        Assert.DoesNotContain("HASGEM", o);
    }

    [Fact]
    public void Numbered_args_are_prefilled_empty_not_literal()
    {
        // G4 pre-fills %1..%9 with "" at launch (Script.cs:2114-2118), so an
        // unpassed numbered arg is DEFINED-empty — it must NOT fall into the
        // literal-undefined path. `eval jumplabel replacere("%2", ...)` in
        // uber gets "" here, not the text "%2".
        var o = Run("echo A2=[%2]\necho done\n", args: new List<string> { "one" });
        Assert.Contains("A2=[]", o);
        Assert.Contains("done", o);
    }

    [Fact]
    public void Defined_empty_var_still_substitutes_empty()
    {
        // Empty-VALUED is not undefined: `var x` with an empty value resolves
        // to "" exactly as before — only truly-unknown names stay literal.
        var o = Run("var x\necho E=[%x]\necho done\n");
        Assert.Contains("E=[]", o);
    }
}
