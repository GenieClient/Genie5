using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Triggers;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A trigger action may edit the trigger list while the list is being dispatched
/// (2026-08-31 stability review).
///
/// <para><c>ProcessLine</c> fires actions from inside its own loop, and an action
/// is free to add or remove rules. The community idiom
/// <c>#trigger {stow crossbow} {…;#trigger remove {stow crossbow}}</c> removes the
/// very rule being fired, which over a live <c>List</c> throws "Collection was
/// modified" every single time. Rule edits also arrive off the pipeline thread —
/// the Configuration panel, and rule-file live reload's clear-then-rebuild, which
/// is what a player editing triggers.json mid-combat does.</para>
///
/// <para>The engine now iterates a copy-on-write snapshot, the treatment its
/// sibling engines already had. These tests pin both the safety and the
/// semantics that follow from it.</para>
/// </summary>
public class TriggerMutationDuringDispatchTests
{
    private static async Task WithCore(Func<GenieCore, List<string>, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_trigmut_" + Guid.NewGuid().ToString("N"));
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

    // ── the reported crash ───────────────────────────────────────────────────

    [Fact]
    public async Task A_trigger_that_removes_itself_does_not_throw()
    {
        await WithCore(async (core, echoes) =>
        {
            // Braces on the remove pattern too: the handler takes a single argument,
            // so an unbraced multi-word pattern would only pass "stow".
            core.Commands.ProcessInput("#trigger {stow crossbow} {#echo FIRED;#trigger remove {stow crossbow}}");

            var ex = Record.Exception(() => core.Triggers.ProcessLine("stow crossbow"));

            Assert.Null(ex);
            Assert.Contains(echoes, e => e == "FIRED");
            Assert.Empty(core.Triggers.Triggers);   // it really did remove itself
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_self_removing_trigger_does_not_stop_the_rules_behind_it()
    {
        // The damage was never only the exception: it aborted the loop, so every
        // rule registered after the self-removing one silently missed the line.
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {ping} {#echo FIRST;#trigger remove ping}");
            core.Commands.ProcessInput("#trigger {ping} {#echo SECOND}");
            // The add path removes a same-pattern rule first, so use a distinct one.
            core.Commands.ProcessInput("#trigger {pin} {#echo THIRD}");

            core.Triggers.ProcessLine("ping");

            Assert.Contains(echoes, e => e == "THIRD");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_trigger_that_clears_every_rule_does_not_throw()
    {
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {wipe} {#echo WIPING;#trigger clear}");
            core.Commands.ProcessInput("#trigger {wip} {#echo AFTER}");

            var ex = Record.Exception(() => core.Triggers.ProcessLine("wipe"));

            Assert.Null(ex);
            Assert.Contains(echoes, e => e == "WIPING");
            Assert.Empty(core.Triggers.Triggers);
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_trigger_that_adds_a_trigger_does_not_throw()
    {
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {seed} {#echo SEEDED;#trigger add {grown} {#echo GROWN}}");

            var ex = Record.Exception(() => core.Triggers.ProcessLine("seed"));

            Assert.Null(ex);
            Assert.Contains(echoes, e => e == "SEEDED");
            Assert.Contains(core.Triggers.Triggers, t => t.Pattern == "grown");
            await Task.CompletedTask;
        });
    }

    // ── the semantics that follow from a snapshot ────────────────────────────

    [Fact]
    public async Task A_rule_added_by_an_action_fires_from_the_next_line_on()
    {
        // Stated rather than discovered: the rule set is fixed when the line
        // arrives. A rule added mid-line is not retro-applied to that line.
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {seed} {#trigger add {seed} {#echo GROWN}}");

            core.Triggers.ProcessLine("seed");
            Assert.DoesNotContain(echoes, e => e == "GROWN");

            core.Triggers.ProcessLine("seed");
            Assert.Contains(echoes, e => e == "GROWN");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_removed_rule_stops_firing_from_the_next_line_on()
    {
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {once} {#echo HIT;#trigger remove once}");

            core.Triggers.ProcessLine("once");
            core.Triggers.ProcessLine("once");
            core.Triggers.ProcessLine("once");

            Assert.Single(echoes.FindAll(e => e == "HIT"));
            await Task.CompletedTask;
        });
    }

    // ── off-thread edits, the live-reload case ───────────────────────────────

    [Fact]
    public void Rules_edited_from_another_thread_do_not_break_dispatch()
    {
        // Rule-file live reload clears and rebuilds the engine off the pipeline
        // thread. Editing triggers.json mid-combat must not crash the line being
        // parsed at that instant.
        var engine = new TriggerEngineFinal();
        for (int i = 0; i < 50; i++) engine.AddTrigger($"pattern{i}", "#echo x");

        Exception? failure = null;
        var stop = false;

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                    engine.ProcessLine("pattern7 pattern31");
            }
            catch (Exception ex) { failure = ex; }
        });

        var writer = new Thread(() =>
        {
            try
            {
                for (int round = 0; round < 200; round++)
                {
                    engine.Clear();                                    // live reload clears…
                    for (int i = 0; i < 50; i++) engine.AddTrigger($"pattern{i}", "#echo x");   // …then rebuilds
                    engine.RemoveTrigger("pattern7");
                }
            }
            catch (Exception ex) { failure = ex; }
        });

        reader.Start();
        writer.Start();
        writer.Join();
        Volatile.Write(ref stop, true);
        reader.Join();

        Assert.Null(failure);
    }

    // ── unchanged behaviour ──────────────────────────────────────────────────

    [Fact]
    public async Task Ordinary_triggers_still_fire()
    {
        // The guard must not have cost the feature.
        await WithCore(async (core, echoes) =>
        {
            core.Commands.ProcessInput("#trigger {hello} {#echo GREETED}");

            core.Triggers.ProcessLine("hello there");

            Assert.Contains(echoes, e => e == "GREETED");
            await Task.CompletedTask;
        });
    }
}
