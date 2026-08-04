using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #171 — an UNDEFINED dotted variable must not be eaten mid-word by a
/// shorter defined variable. `$Outdoorsmanship.Ranks` shrink-matched the compass
/// boolean `$out` ("0doorsmanship.Ranks") and `$SpellTimer.X.active` matched the
/// reserved `$spelltime` pseudo-var ("0r.X.active") — both then failed to parse
/// as conditions ("missing ')'"). A shrunk candidate now only matches at a word
/// boundary (remainder starts with '.', '-', or a digit — never a letter), so
/// the undefined name resolves empty and the condition evaluates false, exactly
/// like a not-yet-populated skill/spell in Genie 4.
/// </summary>
public class UndefinedDottedVarTests
{
    private static List<string> RunFixture(string body,
                                           IDictionary<string, string>? globals = null,
                                           IReadOnlyList<string>? args = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_dottest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            if (globals is not null)
                foreach (var (k, v) in globals) engine.Globals[k] = v;
            engine.TryStart("t", args ?? new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    /// <summary>The compass/status globals ScriptGlobalsSync seeds at connect —
    /// the exact collision surface from the issue ("out" = 0).</summary>
    private static Dictionary<string, string> CompassGlobals() =>
        new(StringComparer.OrdinalIgnoreCase) { ["out"] = "0", ["north"] = "0" };

    [Fact]
    public void Saragos_repro_undefined_skill_and_spell_vars_evaluate_false_without_warning()
    {
        // Verbatim from public #171 — both lines errored "bad condition …
        // missing ')'" because of the mid-word shrink match.
        const string body =
            "if ($Outdoorsmanship.Ranks >= 1750) then var outdoorlock 1\n" +
            "if ($SpellTimer.Sanctuary.active = 1) then echo SANC\n" +
            "echo done\n";

        var o = RunFixture(body, CompassGlobals());

        Assert.Contains("done", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
        Assert.DoesNotContain("SANC", o);
    }

    [Fact]
    public void Defined_dotted_vars_still_resolve_by_full_name()
    {
        const string body =
            "if ($Outdoorsmanship.Ranks >= 1750) then echo LOCKED\n" +
            "if ($SpellTimer.Sanctuary.active = 1) then echo SANC\n";

        var globals = CompassGlobals();
        globals["Outdoorsmanship.Ranks"]      = "1800";
        globals["SpellTimer.Sanctuary.active"] = "1";

        var o = RunFixture(body, globals);

        Assert.Contains("LOCKED", o);
        Assert.Contains("SANC",   o);
    }

    [Fact]
    public void Spelltime_pseudo_var_still_resolves_exactly()
    {
        // The reserved $spelltime must keep working when named in full — only
        // the mid-word match from "$SpellTimer…" is gone.
        var o = RunFixture("echo T=[$spelltime]\n");
        Assert.Contains(o, l => l.StartsWith("T=[") && l.EndsWith("]")
                                && int.TryParse(l[3..^1], out _));
    }

    [Fact]
    public void Shrink_to_dot_and_dash_boundaries_still_works()
    {
        // The two sanctioned Genie 4 shrink patterns must survive the
        // word-boundary rule: `%count-1` → %count + "-1", and the
        // community-documented `%%spell.Prep` → inner %spell + ".Prep".
        const string body =
            "var count 5\n" +
            "echo C=%count-1\n" +
            "var spell Fireball\n" +
            "var Fireball.Prep 10\n" +
            "echo P=%%spell.Prep\n";

        var o = RunFixture(body);

        Assert.Contains("C=5-1", o);
        Assert.Contains("P=10",  o);
    }

    [Fact]
    public void Digit_slot_consumes_one_digit_and_leaves_suffix_literal()
    {
        // Genie 4 replaces $0..$9 as a flat text pass, so `$1s` is
        // arg1 + literal "s" — the suffix must not drag the slot into the
        // shrink-search (where the word-boundary rule would kill it).
        var o = RunFixture("echo W=$1s\n", args: new List<string> { "stone", "gem" });
        Assert.Contains("W=stones", o);
    }

    [Fact]
    public void Undefined_dotted_var_in_echo_stays_literal()
    {
        // Genie 4 parity: an undefined variable survives substitution as
        // LITERAL text (G4 ParseVariable ends `return Line;` — Script.cs:2469 /
        // Globals.cs:306). The #171 word-boundary rule still stops the mid-word
        // shrink mangle ("R=0doorsmanship.Ranks"); what remains is the intact
        // token, exactly as G4 would echo it. (An earlier cut of the #171 fix
        // substituted "" here — that empty-substitution turned uber.cmd's
        // `\b(?i)%superjump\b` into a match-anything regex; see the
        // undefined-var parity fix.)
        var o = RunFixture("echo R=[$Outdoorsmanship.Ranks]\n", CompassGlobals());
        Assert.Contains("R=[$Outdoorsmanship.Ranks]", o);
    }
}
