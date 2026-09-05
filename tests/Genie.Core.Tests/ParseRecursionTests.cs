using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#parse</c> re-entry bounding (2026-08-31 stability review).
///
/// <para>An injected line runs the full per-line pipeline, so a trigger that fires
/// on the injected text and itself issues <c>#parse</c> nests synchronously on the
/// one pipeline thread with no unwind between levels. Unbounded, that is a
/// <c>StackOverflowException</c> — uncatchable, no dialog, the process simply
/// vanishes mid-session.</para>
///
/// <para><c>GenieCore.InjectParsedLine</c> caps the nesting at
/// <c>MaxParseDepth</c>. That cap was added with the game-thread work and had no
/// test of its own, which is precisely how the review came to re-report the bug
/// against a tree that already contained the fix. These tests pin it so it cannot
/// quietly regress: the guard is the only thing standing between a two-line
/// trigger and process death.</para>
/// </summary>
public class ParseRecursionTests
{
    /// <summary>The shipped cap in GenieCore.InjectParsedLine. Update deliberately
    /// — lowering it changes how deep a legitimate #parse chain may nest, raising
    /// it brings the stack back into play.</summary>
    private const int ExpectedMaxParseDepth = 16;

    private static async Task WithCore(Func<GenieCore, List<string>, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_parserec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            var echoes = new List<string>();
            core.EchoLine += l => echoes.Add(l);
            await body(core, echoes);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task A_self_feeding_parse_trigger_stops_at_the_depth_limit()
    {
        await WithCore(async (core, echoes) =>
        {
            // Fires on its own injected text and re-parses it: unbounded without
            // the cap. The #echo marks each level so the nesting is countable.
            core.Commands.ProcessInput("#trigger {loopme} {#echo LEVEL;#parse loopme}");
            core.InjectParsedLine("loopme");

            // Exact match, not Contains: the "Trigger added: …" confirmation echoes
            // the action text back, so a substring test counts the setup line too.
            var levels = echoes.FindAll(e => e == "LEVEL").Count;

            Assert.Equal(ExpectedMaxParseDepth, levels);
            Assert.Contains(echoes, e => e.Contains("re-entry depth limit"));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_guard_resets_between_injections()
    {
        // The depth counter is decremented in a finally, so a second injection
        // starts from zero rather than inheriting the first one's exhausted
        // budget — otherwise one runaway trigger would permanently disable
        // #parse for the rest of the session.
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {loopme} {#echo LEVEL;#parse loopme}");

            core.InjectParsedLine("loopme");
            var first = echoes.FindAll(e => e == "LEVEL").Count;
            echoes.Clear();

            core.InjectParsedLine("loopme");
            var second = echoes.FindAll(e => e == "LEVEL").Count;

            Assert.Equal(ExpectedMaxParseDepth, first);
            Assert.Equal(first, second);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_script_looping_on_put_parse_does_not_kill_the_process()
    {
        // The repro as filed. It is iterative rather than recursive — OnGameLine
        // only un-pauses a matched script, it does not execute its next line
        // inline — so this one never nested even before the cap existed. Kept
        // because it is the reported case and must stay survivable.
        await WithCore(async (core, _) =>
        {
            var scripts = core.Config.ScriptDir;
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "rec.cmd"), "top:\nput #parse x\ngoto top\n");

            core.Scripts.TryStart("rec", new List<string>());
            for (int i = 0; i < 200; i++) core.Scripts.Tick();
            core.Scripts.StopAll();

            await Task.CompletedTask;
        });
    }
}
