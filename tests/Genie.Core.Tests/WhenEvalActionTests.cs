using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #224 (root cause) — <c>action … when eval (expr)</c> registered the
/// expression raw (correct, so it can be re-evaluated live) but then handed
/// that raw text to <c>ScriptExpression.EvalBool</c> at fire time WITHOUT
/// variable substitution. The evaluator treats <c>$name</c>/<c>%name</c> as
/// literal undefined-var remnants (it expects substitution upstream), so any
/// eval condition mentioning a variable was silently false forever — which is
/// why <c>when eval ($spelltime &gt; N)</c> never fired and the reserved var
/// looked missing. Genie 4 re-parses variables on every action evaluation
/// pass; the fix substitutes at fire time exactly like <c>wait eval</c> does.
/// </summary>
public class WhenEvalActionTests
{
    /// <summary>Runs a script body and pumps prompts (eval actions are polled
    /// on OnGameLine/OnPrompt, not on bare ticks). The mutate hook runs midway
    /// so tests can flip state and prove the expression is evaluated live.</summary>
    private static List<string> Run(string body,
                                    Action<ScriptEngine>? setup = null,
                                    Action<ScriptEngine>? mutate = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_evalact_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            setup?.Invoke(engine);
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 20; i++) { engine.Tick(); engine.OnPrompt(); }
            mutate?.Invoke(engine);
            for (int i = 0; i < 20; i++) { engine.Tick(); engine.OnPrompt(); }
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>Keeps the script instance (and its actions) alive while the
    /// harness pumps prompts.</summary>
    private const string Park = ":loop\npause 1\ngoto loop\n";

    [Fact]
    public void Spelltime_pseudo_var_fires_in_when_eval()
    {
        // The #224 report: a reserved pseudo-var in an eval action. Resolved
        // through the same TryResolveVariable path as plain script lines.
        var o = Run("action echo FIRED when eval ($spelltime > 5)\n" + Park,
                    se => se.SpellTimeSeconds = () => 42);
        Assert.Contains(o, l => l.Contains("FIRED"));
    }

    [Fact]
    public void Global_var_fires_in_when_eval()
    {
        var o = Run("action echo FIRED when eval ($testflag = 1)\n" + Park,
                    se => se.Globals["testflag"] = "1");
        Assert.Contains(o, l => l.Contains("FIRED"));
    }

    [Fact]
    public void Local_var_fires_in_when_eval()
    {
        var o = Run("var myflag 1\naction echo FIRED when eval (%myflag = 1)\n" + Park);
        Assert.Contains(o, l => l.Contains("FIRED"));
    }

    [Fact]
    public void When_eval_reads_the_live_value_not_a_registration_snapshot()
    {
        // travel.cmd's `action put #tvar spellROC 0 when eval
        // ($SpellTimer.RiteofContrition.active = 0)` shape: the global is in
        // the non-firing state at registration and flips later. Substituting
        // at registration instead of fire time would freeze the initial value.
        var o = Run("action echo FIRED when eval ($SpellTimer.Test.active = 1)\n" + Park,
                    se => se.Globals["SpellTimer.Test.active"] = "0",
                    se => se.Globals["SpellTimer.Test.active"] = "1");
        Assert.Contains(o, l => l.Contains("FIRED"));
    }

    [Fact]
    public void When_eval_is_edge_triggered_not_level_triggered()
    {
        // Genie 4 fires an eval action on the false→true TRANSITION only; a
        // condition that stays true must not spam the command every prompt.
        var o = Run("action echo FIRED when eval ($testflag = 1)\n" + Park,
                    se => se.Globals["testflag"] = "1");
        Assert.Single(o, l => l.Contains("FIRED"));
    }

    [Fact]
    public void Undefined_var_in_when_eval_stays_false_without_error()
    {
        // An undefined $var survives substitution as a literal remnant; the
        // evaluator's G4-parity rule makes the comparison false — no fire, no
        // "action error" spam.
        var o = Run("action echo FIRED when eval ($nosuchvar = 1)\n" + Park);
        Assert.DoesNotContain(o, l => l.Contains("FIRED"));
        Assert.DoesNotContain(o, l => l.Contains("action error"));
    }
}
