using System;
using System.IO;
using System.Threading.Tasks;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// GenieCore under the #251 game thread: input posted from a foreign (UI/test)
/// thread must land in the command pipeline on the loop, and the config flag /
/// override must gate the whole machinery. Engine-semantics tests elsewhere run
/// with <c>gameThreadOverride: false</c> for determinism; these are the tests
/// for the threaded path itself.
/// </summary>
public class GameThreadCoreTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_gamethread_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<bool> PollAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task ProcessInput_from_a_foreign_thread_is_processed_on_the_loop()
    {
        var dir = NewDir();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: true);
            Assert.True(core.GameThreadEnabled);

            core.ProcessInput("#tvar LOOPTEST arrived");   // posts to the loop

            Assert.True(await PollAsync(() =>
                    core.Scripts.Globals.TryGetValue("LOOPTEST", out var v) && v == "arrived"),
                "posted input never reached the command pipeline");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task PostCommand_routes_programmatic_commands_through_the_pipeline()
    {
        var dir = NewDir();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: true);

            core.PostCommand("#tvar POSTCMD ok");

            Assert.True(await PollAsync(() =>
                    core.Scripts.Globals.TryGetValue("POSTCMD", out var v) && v == "ok"),
                "PostCommand never reached the command pipeline");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Override_false_keeps_the_legacy_inline_pipeline()
    {
        var dir = NewDir();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            Assert.False(core.GameThreadEnabled);

            // Inline: the effect is visible synchronously, no polling needed.
            core.ProcessInput("#tvar INLINE now");
            Assert.True(core.Scripts.Globals.TryGetValue("INLINE", out var v) && v == "now",
                "inline pipeline should apply synchronously");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Script_ticks_are_pumped_by_the_core_heartbeat_without_a_host_pump()
    {
        var dir = NewDir();
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: true);

            var scriptsDir = core.Config.ScriptDir;
            Directory.CreateDirectory(scriptsDir);
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, "hb.cmd"),
                "put #tvar HEARTBEAT ticked\n");

            // No host ScheduleTick / DispatcherTimer wired, no manual Tick():
            // the core's own loop heartbeat must run the script.
            Assert.True(core.Scripts.TryStart("hb", Array.Empty<string>()));

            Assert.True(await PollAsync(() =>
                    core.Scripts.Globals.TryGetValue("HEARTBEAT", out var v) && v == "ticked"),
                "the core-owned heartbeat never ticked the script engine");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Gamethread_config_defaults_on_and_parses()
    {
        var cfg = new GenieConfig(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));
        Assert.True(cfg.GameThread);
        cfg.SetSetting("gamethread", "false", showException: false);
        Assert.False(cfg.GameThread);
        cfg.SetSetting("gamethread", "true", showException: false);
        Assert.True(cfg.GameThread);
    }
}
