using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.Core.Events;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="GenieCore.ProcessGameTextEvent"/>'s trigger gate for DR's
/// talk/whispers duplicate-echo (<see cref="TextEvent.DuplicateEcho"/>):
/// exactly one trigger pass per talk line in both <c>ParseGameOnly</c> modes.
/// Before this fix, ParseGameOnly-off double-fired (both the stream copy and
/// the bare main copy satisfy "every stream"); ParseGameOnly-on relies on the
/// duplicate being the only "main"-stream representation of the line — Genie
/// 4 parity fires TriggerParse on exactly that copy.
/// </summary>
public class DuplicateEchoTriggerGatingTests
{
    private static async Task RunAsync(Func<GenieCore, List<string>, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_dupecho_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            var commands = new List<string>();
            core.Commands.CommandObserved = c => commands.Add(c);
            await body(core, commands);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task ParseGameOnlyOff_TalkPair_FiresExactlyOnce()
    {
        await RunAsync((core, commands) =>
        {
            core.Config.ParseGameOnly = false;
            core.Triggers.AddTrigger("Look here", "wave");

            core.ProcessGameTextEvent(new TextEvent("talk", "You say, \"Look here.\""));
            core.ProcessGameTextEvent(new TextEvent("main", "You say, \"Look here.\"", DuplicateEcho: true));

            Assert.Single(commands, c => c.Contains("wave"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ParseGameOnlyOn_TalkPair_FiresOnlyOnTheDuplicateMainCopy()
    {
        await RunAsync((core, commands) =>
        {
            core.Config.ParseGameOnly = true;
            core.Triggers.AddTrigger("Look here", "wave");

            core.ProcessGameTextEvent(new TextEvent("talk", "You say, \"Look here.\""));
            core.ProcessGameTextEvent(new TextEvent("main", "You say, \"Look here.\"", DuplicateEcho: true));

            // The talk-stream copy alone must NOT fire (ParseGameOnly restricts
            // to "main"); the duplicate-flagged main copy must fire — it's the
            // only main-stream representation of this line.
            Assert.Single(commands, c => c.Contains("wave"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ParseGameOnlyOff_OrdinaryMainLine_StillFires()
    {
        // Sanity: the DuplicateEcho gate must not swallow normal, non-flagged
        // main-stream trigger matches.
        await RunAsync((core, commands) =>
        {
            core.Config.ParseGameOnly = false;
            core.Triggers.AddTrigger("shadowy figure", "look");

            core.ProcessGameTextEvent(new TextEvent("main", "a shadowy figure arrives."));

            Assert.Single(commands, c => c.Contains("look"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ParseGameOnlyOn_SideStreamLine_NeverFires()
    {
        // Sanity: ParseGameOnly still excludes ordinary (non-duplicate)
        // side-stream lines, e.g. combat, exactly as before this change.
        await RunAsync((core, commands) =>
        {
            core.Config.ParseGameOnly = true;
            core.Triggers.AddTrigger("clean hit", "cheer");

            core.ProcessGameTextEvent(new TextEvent("combat", "A clean hit!"));

            Assert.DoesNotContain(commands, c => c.Contains("cheer"));
            return Task.CompletedTask;
        });
    }
}
