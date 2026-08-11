using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #225 — a variable embedded in the MIDDLE of another variable's name
/// must compose like Genie 4: with `counter` defined, `%spell%countermana`
/// resolves the inner `%countermana` as `%counter` + "mana", forming
/// `%spell1mana`. The first cut of the #171 word-boundary rule rejected ALL
/// mid-word shrink breaks, which killed this idiom (the common
/// loop-over-numbered-vars pattern). The reconciliation: a mid-word break is
/// legal exactly when the matched var is stored with EXACT case — the match
/// G4's case-sensitive VariableList would have made — while
/// case-insensitive-only hits stay boundary-restricted so #171's protection
/// (undefined `$Outdoorsmanship.Ranks` not eaten by the compass `$out`)
/// survives. UndefinedDottedVarTests covers the #171 side.
/// </summary>
public class ScriptMidNameVarTests
{
    private static List<string> RunFixture(string body,
                                           IDictionary<string, string>? globals = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_midname_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            if (globals is not null)
                foreach (var (k, v) in globals) engine.Globals[k] = v;
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 400; i++) engine.Tick();
            return echoed.FindAll(l => l.StartsWith("T:"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // The issue's repro, condensed: numbered spell/buff vars walked by a
    // loop counter, with the counter at the END (%spell%counter), in the
    // MIDDLE (%spell%countermana), and through a double-eval type prefix
    // (%%spelltype%countermana).
    private const string ReproVars =
        "var buffnum 2\n"    +
        "var buff1 seer\n"   +
        "var buff1mana 100\n" +
        "var buff2 tksh\n"   +
        "var buff2mana 100\n" +
        "var spellnum 2\n"   +
        "var spell1 cv\n"    +
        "var spell1mana 100\n" +
        "var spell2 fm\n"    +
        "var spell2mana 100\n";

    [Fact]
    public void Mid_name_counter_composes_like_genie4()   // MIDTESTLOOP
    {
        var o = RunFixture(ReproVars +
            "var counter 1\n" +
            "gosub MIDTESTLOOP\n" +
            "exit\n" +
            "MIDTESTLOOP:\n" +
            "  if (%counter > %spellnum) then return\n" +
            "  echo T:spell%counter %spell%counter\n" +
            "  echo T:spell%countermana %spell%countermana\n" +
            "  math counter add 1\n" +
            "  goto MIDTESTLOOP\n");

        Assert.Equal(new[]
        {
            "T:spell1 cv",
            "T:spell1mana 100",
            "T:spell2 fm",
            "T:spell2mana 100",
        }, o.ToArray());
    }

    [Fact]
    public void Double_eval_type_prefix_composes_like_genie4()   // FULLTESTLOOP
    {
        var o = RunFixture(ReproVars +
            "var counter 1\n" +
            "gosub FULLTESTLOOP spell %spellnum\n" +
            "var counter 1\n" +
            "gosub FULLTESTLOOP buff %buffnum\n" +
            "exit\n" +
            "FULLTESTLOOP:\n" +
            "  var spelltype $1\n" +
            "  var counternum $2\n" +
            "  if (%counter > %counternum) then return\n" +
            "  echo T:%spelltype%counter %%spelltype%counter\n" +
            "  echo T:%spelltype%countermana %%spelltype%countermana\n" +
            "  math counter add 1\n" +
            "  goto FULLTESTLOOP\n");

        Assert.Equal(new[]
        {
            "T:spell1 cv",
            "T:spell1mana 100",
            "T:spell2 fm",
            "T:spell2mana 100",
            "T:buff1 seer",
            "T:buff1mana 100",
            "T:buff2 tksh",
            "T:buff2mana 100",
        }, o.ToArray());
    }

    [Fact]
    public void Global_mid_name_composition_with_exact_case()
    {
        // Same idiom through $ globals: $counter mid-name forms $spell3mana.
        var o = RunFixture("echo T:G=$spell$countermana\n",
            new Dictionary<string, string>
            {
                ["counter"]    = "3",
                ["spell3mana"] = "99",
            });

        Assert.Equal(new[] { "T:G=99" }, o.ToArray());
    }

    [Fact]
    public void Case_insensitive_only_hit_still_rejected_mid_word()
    {
        // `Count` (capital C) must NOT be eaten out of the undefined
        // `%countdown` — G4's case-sensitive lookup would miss it, and the
        // #171 protection relies on exactly this rejection.
        var o = RunFixture(
            "var Count 5\n" +
            "echo T:C=%countdown\n");

        Assert.Equal(new[] { "T:C=%countdown" }, o.ToArray());
    }

    [Fact]
    public void End_position_and_begin_position_still_work()
    {
        // The issue noted begin/end placements kept working — regression-guard
        // them alongside the fix: counter at the end (%spell%counter) and a
        // var-typed prefix at the beginning (%%spelltype1).
        var o = RunFixture(ReproVars +
            "var counter 2\n" +
            "var spelltype spell\n" +
            "echo T:E=%spell%counter\n" +
            "echo T:B=%%spelltype1\n");

        Assert.Equal(new[] { "T:E=fm", "T:B=cv" }, o.ToArray());
    }
}
