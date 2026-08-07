using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Script-side <c>put #tvar</c> must run the same value pipeline as a typed
/// <c>#tvar</c> (ArgumentParser brace grouping + ResolveValueCommand). The
/// uber-combat back-training timer does
/// <c>put #tvar UC_Combat.Last {#evalmath ($gametime - $UC_Combat.Time)}</c>
/// and then <c>if ($UC_Combat.Last &gt; $UC_MaxTrain)</c> — before this fix the
/// ScriptEngine's local #tvar handler stored the literal
/// <c>{#evalmath (…)}</c> text, and the later if-condition died on the '{'
/// ("expression: unexpected '{' — treated as false"), so the escape timer
/// never fired.
/// </summary>
public class ScriptPutTvarEvalTests
{
    private static async Task<GenieCore> NewCoreWithScript(string dir, string body)
    {
        var core = new GenieCore(dataDirectoryOverride: dir);
        var scriptsDir = core.Config.ScriptDir;
        Directory.CreateDirectory(scriptsDir);
        File.WriteAllText(Path.Combine(scriptsDir, "tvartest.cmd"), body);
        Assert.True(core.Scripts.TryStart("tvartest", Array.Empty<string>()));
        for (int i = 0; i < 50; i++) core.Scripts.Tick();
        await Task.CompletedTask;
        return core;
    }

    [Fact]
    public async Task Put_tvar_with_braced_evalmath_stores_the_numeric_result()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_puttvar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // The uber idiom with the vars pre-substituted (the script engine
            // expands $gametime/$UC_Combat.Time before the put dispatches).
            await using var core = await NewCoreWithScript(dir,
                "put #tvar UC_Combat.Last {#evalmath (1786076118 - 1786075985)}\n");

            Assert.Equal("133",
                core.Scripts.Globals.TryGetValue("UC_Combat.Last", out var v) ? v : null);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Stored_eval_result_is_comparable_in_a_later_if()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_puttvarif_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // End-to-end: the stored value must be a clean number the
            // expression evaluator can compare (uber's ESCAPE.TIMER gate).
            await using var core = await NewCoreWithScript(dir,
                "put #tvar UC_Combat.Last {#evalmath (10 + 5)}\n" +
                "if ($UC_Combat.Last > 2500) then put #tvar RES escaped\n" +
                "if ($UC_Combat.Last <= 2500) then put #tvar RES fighting\n");

            Assert.Equal("fighting",
                core.Scripts.Globals.TryGetValue("RES", out var v) ? v : null);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Put_tvar_plain_value_still_stores_verbatim()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_puttvarplain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = await NewCoreWithScript(dir,
                "put #tvar huntzone rats and mice\n");

            Assert.Equal("rats and mice",
                core.Scripts.Globals.TryGetValue("huntzone", out var v) ? v : null);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
