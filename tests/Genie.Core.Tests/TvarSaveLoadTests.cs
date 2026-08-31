using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#tvar save</c> / <c>#tvar load</c> — formerly honest "not yet
/// implemented" stubs. A Genie 5 convenience (Genie 4 tvars are
/// VariableType.Temporary and never touch disk): save writes ONLY the names
/// set via <c>#tvar</c> this session to <c>tvars.cfg</c> as replayable
/// <c>#tvar name value</c> cfg lines — never the parser-pumped live game
/// state that shares Scripts.Globals — and load replays the file through the
/// guarded cfg-line runner.
/// </summary>
public class TvarSaveLoadTests
{
    private static async Task RunAsync(Func<GenieCore, Func<string>, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_tvartest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
            // The engine writes to its resolved ConfigProfileDir under the data
            // dir; locate the file wherever that landed rather than assuming.
            string FindCfg() =>
                Directory.GetFiles(dir, "tvars.cfg", SearchOption.AllDirectories) is { Length: > 0 } hits
                    ? hits[0]
                    : throw new FileNotFoundException("tvars.cfg not written under " + dir);
            await body(core, FindCfg);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public async Task Save_writes_only_user_tvars_as_cfg_lines()
    {
        await RunAsync((core, findCfg) =>
        {
            // Live game state shares Scripts.Globals — it must NOT be saved.
            core.Scripts.Globals["health"]   = "97";
            core.Scripts.Globals["roomname"] = "Somewhere";

            core.ProcessInput("#tvar huntzone rats");
            core.ProcessInput("#tvar loot.mode greedy");
            core.ProcessInput("#tvar save");

            var text = File.ReadAllText(findCfg());
            Assert.Contains("#tvar {huntzone} {rats}",     text);
            Assert.Contains("#tvar {loot.mode} {greedy}",  text);
            Assert.DoesNotContain("health",   text);
            Assert.DoesNotContain("roomname", text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Untvar_removes_the_name_from_the_save_set()
    {
        await RunAsync((core, findCfg) =>
        {
            core.ProcessInput("#tvar keepme 1");
            core.ProcessInput("#tvar dropme 2");
            core.ProcessInput("#untvar dropme");
            core.ProcessInput("#tvar save");

            var text = File.ReadAllText(findCfg());
            Assert.Contains("keepme", text);
            Assert.DoesNotContain("dropme", text);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Load_round_trips_saved_tvars_into_globals()
    {
        await RunAsync((core, findCfg) =>
        {
            core.ProcessInput("#tvar huntzone rats and mice");   // multi-word value
            core.ProcessInput("#tvar save");

            core.ProcessInput("#untvar huntzone");
            Assert.False(core.Scripts.Globals.ContainsKey("huntzone"));

            core.ProcessInput("#tvar load");
            Assert.Equal("rats and mice", core.Scripts.Globals["huntzone"]);

            // Reloaded names are tracked again — a subsequent save keeps them.
            core.ProcessInput("#tvar save");
            Assert.Contains("huntzone", File.ReadAllText(findCfg()));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Load_with_no_file_reports_instead_of_throwing()
    {
        await RunAsync((core, findCfg) =>
        {
            core.ProcessInput("#tvar load");                     // no tvars.cfg yet
            Assert.Throws<FileNotFoundException>(() => findCfg());
            return Task.CompletedTask;
        });
    }
}
