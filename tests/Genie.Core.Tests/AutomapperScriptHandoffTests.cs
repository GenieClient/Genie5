using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Issue #226 — #goto hands the walk to the community automapper.cmd when one
/// is present (Genie 4 parity: G4's #goto only ever sent
/// <c>.automapper &lt;moves&gt;</c>; the script owned special-move directives
/// and pacing globals). Core surface under test: the
/// <c>automapperscript</c> config key that gates the hand-off, and
/// <see cref="ScriptEngine.ScriptFileExists"/>, the presence probe the mapper
/// uses to choose script vs built-in walker — which must follow the same
/// ScriptDir-then-repo-dir lookup that actually starting the script uses
/// (#221), or the probe could say yes and the launch could fail.
/// </summary>
public class AutomapperScriptHandoffTests : IDisposable
{
    private readonly string _root;
    private readonly string _primary;
    private readonly string _repo;
    private readonly GenieConfig _config;
    private readonly ScriptEngine _engine;

    public AutomapperScriptHandoffTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "gc_amhand_" + Guid.NewGuid().ToString("N"));
        _primary = Path.Combine(_root, "Scripts");
        _repo    = Path.Combine(_root, "RepoScripts");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_repo);

        var lds = new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);

        _engine = new ScriptEngine(_primary, new TypeAheadSession(),
                                   sendCommand: _ => { }, echo: _ => { })
        {
            Config = _config,
        };
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── automapperscript config key ─────────────────────────────────────────

    [Fact]
    public void Automapperscript_defaults_on()
    {
        // Default-on is the design: users with the community scripts installed
        // get G4-parity walking with zero setup.
        Assert.True(_config.AutoMapperScript);
    }

    [Fact]
    public void Automapperscript_round_trips_through_config()
    {
        _config.SetSetting("automapperscript", "false");
        Assert.False(_config.AutoMapperScript);
        _config.SetSetting("automapperscript", "true");
        Assert.True(_config.AutoMapperScript);
    }

    // ── ScriptFileExists (the hand-off presence probe) ──────────────────────

    [Fact]
    public void ScriptFileExists_false_when_absent()
    {
        Assert.False(_engine.ScriptFileExists("automapper"));
    }

    [Fact]
    public void ScriptFileExists_finds_cmd_in_scripts_dir()
    {
        File.WriteAllText(Path.Combine(_primary, "automapper.cmd"), "exit\n");
        Assert.True(_engine.ScriptFileExists("automapper"));
    }

    [Fact]
    public void ScriptFileExists_finds_repo_dir_copy()
    {
        // A repo-scripts-only install (#221: updater pulls into reposcriptdir)
        // must still trigger the hand-off.
        _config.SetSetting("reposcriptdir", _repo);
        File.WriteAllText(Path.Combine(_repo, "automapper.cmd"), "exit\n");
        Assert.True(_engine.ScriptFileExists("automapper"));
    }

    [Fact]
    public void ScriptFileExists_probe_agrees_with_TryStart()
    {
        // The invariant the mapper depends on: probe-yes must mean start-yes.
        File.WriteAllText(Path.Combine(_primary, "automapper.cmd"), "exit\n");
        Assert.True(_engine.ScriptFileExists("automapper"));
        Assert.True(_engine.TryStart("automapper", new List<string> { "north", "go door" }));
    }
}
