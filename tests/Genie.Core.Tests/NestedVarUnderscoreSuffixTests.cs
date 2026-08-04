using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;
using Xunit.Abstractions;

namespace Genie.Core.Tests;

/// <summary>
/// Live-repro from Jason's mm_train run (alpha.8.15): the menu lines
///   if !(%extra_message = "") then put #echo ">%this.window" cyan %extra_message
/// reported `bad condition '!(% = "")' — unexpected '%'`. Reproduce the exact
/// substitution + condition-eval to see how %extra_message collapses to a bare %.
/// </summary>
public class MmTrainExtraMessageRepro
{
    private readonly ITestOutputHelper _out;
    public MmTrainExtraMessageRepro(ITestOutputHelper o) => _out = o;

    private static List<string> Run(string body, IDictionary<string, string>? globals = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_mmrepro_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            if (globals is not null)
                foreach (var (k, v) in globals) engine.Globals[k] = v;
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 300; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Nested_var_with_underscore_suffix_resolves_the_inner_global()
    {
        // The real mm_train flow (line 1071): gosub GlobalSet "%$selection_DESC".
        // Genie 4 shrinks $selection_DESC → $selection ("MAGIC") + "_DESC",
        // forming %MAGIC_DESC. The #171 word-boundary rule wrongly rejected the
        // '_' boundary, so $selection_DESC collapsed to "" and the leading %
        // survived as a bare sigil — the value that became extra_message="%",
        // producing `!(% = "")`.
        const string body =
            "var MAGIC_DESC Train your magic skills\n" +
            "var extra_message %$selection_DESC\n" +
            "echo GOT=[%extra_message]\n" +
            "if !(%extra_message = \"\") then echo HASMSG\n" +
            "echo done\n";

        var o = Run(body, new Dictionary<string, string> { ["selection"] = "MAGIC" });
        foreach (var l in o) _out.WriteLine(l);

        Assert.Contains("GOT=[Train your magic skills]", o);
        Assert.Contains("HASMSG", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
        Assert.DoesNotContain(o, l => l.Contains("%extra_message"));
    }

    [Fact]
    public void Nested_var_underscore_suffix_undefined_inner_desc_stays_literal()
    {
        // Same pattern but no matching _DESC local. Genie 4 parity: the shrink
        // resolves $selection → "WARDING_SPELL", forming %WARDING_SPELL_DESC,
        // and THAT name being undefined stays LITERAL (G4 `return Line;`) — so
        // extra_message holds the literal token, the != "" check is true (it IS
        // non-empty text), and mm_train would display the raw %WARDING_SPELL_DESC
        // in its menu, exactly as Genie 4 does for a missing _DESC entry. The
        // one thing that must never come back is the bare-% parse error from
        // the first cut of the #171 boundary rule.
        const string body =
            "var extra_message %$selection_DESC\n" +
            "echo GOT=[%extra_message]\n" +
            "if !(%extra_message = \"\") then echo HASMSG\n" +
            "echo done\n";

        var o = Run(body, new Dictionary<string, string> { ["selection"] = "WARDING_SPELL" });
        foreach (var l in o) _out.WriteLine(l);

        Assert.Contains("GOT=[%WARDING_SPELL_DESC]", o);
        Assert.Contains("HASMSG", o);
        Assert.DoesNotContain(o, l => l.Contains("bad condition"));
    }
}
