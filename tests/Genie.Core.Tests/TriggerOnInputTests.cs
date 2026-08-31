using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 <c>triggeroninput</c> parity (Config.cs:19 default TRUE;
/// FormMain.ClassCommand_SendText → ParseTriggers): text SENT to the game
/// also runs the trigger/action pipeline. mm_train's typed-input capture —
/// <c>action (input) var input $1;put #parse input.done when ~(.*)</c> plus
/// "All typed user input MUST be preceded by tilde (~)" — depends on it:
/// typing <c>~500</c> must fire the action even though the server never
/// echoes the command back.
/// </summary>
public class TriggerOnInputTests
{
    private static async Task<string?> RunAsync(string typed, bool triggerOnInput)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_trigoninput_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            core.Config.TriggerOnInput = triggerOnInput;

            // mm_train's input-capture idiom, ending in a global we can read.
            var scriptsDir = core.Config.ScriptDir;
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, "inputcap.cmd"),
                "action (input) var input $1;put #parse input.done when ~(.*)\n" +
                "action (input) on\n" +
                "waitforre input.done\n" +
                "put #tvar GOT %input\n");

            Assert.True(core.Scripts.TryStart("inputcap", Array.Empty<string>()));
            for (int i = 0; i < 50; i++) core.Scripts.Tick();   // reach the waitforre

            core.ProcessInput("~500");                          // user types ~500
            for (int i = 0; i < 50; i++) core.Scripts.Tick();   // let the action + resume run

            return core.Scripts.Globals.TryGetValue("GOT", out var v) ? v : null;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Typed_tilde_input_fires_script_actions()
    {
        Assert.Equal("500", await RunAsync("~500", triggerOnInput: true));
    }

    [Fact]
    public async Task Disabled_triggeroninput_does_not_feed_input_to_actions()
    {
        Assert.Null(await RunAsync("~500", triggerOnInput: false));
    }

    [Fact]
    public async Task Mycommandchar_input_feeds_actions_but_never_reaches_the_send_path()
    {
        // Genie 4 mycommandchar ("parsed but not sent to game"): with
        // `#config mycommandchar ~`, a typed "~armband" reply fires the
        // script's capture action but skips the socket / $lastcommand /
        // type-ahead leg — DR never sees it, so no "Please rephrase that
        // command." Default is '/' (Genie 4 Config.cs:17).
        var dir = Path.Combine(Path.GetTempPath(), "gc_mycmdchar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            core.Config.MyCommandChar = '~';

            var scriptsDir = core.Config.ScriptDir;
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, "inputcap.cmd"),
                "action (input) var input $1;put #parse input.done when ~(.*)\n" +
                "action (input) on\n" +
                "waitforre input.done\n" +
                "put #tvar GOT %input\n");

            Assert.True(core.Scripts.TryStart("inputcap", Array.Empty<string>()));
            for (int i = 0; i < 50; i++) core.Scripts.Tick();

            core.ProcessInput("~armband");
            for (int i = 0; i < 50; i++) core.Scripts.Tick();

            Assert.Equal("armband", core.Scripts.Globals.TryGetValue("GOT", out var v) ? v : null);
            // The send leg was skipped: $lastcommand never saw the reply.
            Assert.False(core.Scripts.Globals.TryGetValue("lastcommand", out var lc) && lc == "~armband");
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Default_slash_prefix_is_parsed_but_not_sent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_slashchar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);

            core.ProcessInput("/notaplugin");                 // default mycommandchar '/'
            Assert.False(core.Scripts.Globals.TryGetValue("lastcommand", out var lc) && lc == "/notaplugin");

            core.ProcessInput("look");                        // ordinary input still sends
            Assert.Equal("look", core.Scripts.Globals.TryGetValue("lastcommand", out var lc2) ? lc2 : null);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
