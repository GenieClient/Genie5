using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Issue #221 — separate repo-scripts directory. The Scripts updater pulls
/// into <c>reposcriptdir</c> when set, and the script engine resolves names
/// from the user's ScriptDir first with the repo dir as a fallback, so a
/// locally-edited copy in ScriptDir shadows the repo copy and survives
/// updates. Blank <c>reposcriptdir</c> (the default) keeps the pre-#221
/// behavior: everything happens in ScriptDir.
/// </summary>
public class RepoScriptDirTests : IDisposable
{
    private readonly string _root;
    private readonly string _primary;
    private readonly string _repo;
    private readonly GenieConfig _config;
    private readonly List<string> _echoed = new();
    private readonly ScriptEngine _engine;

    public RepoScriptDirTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "gc_repodir_" + Guid.NewGuid().ToString("N"));
        _primary = Path.Combine(_root, "Scripts");
        _repo    = Path.Combine(_root, "RepoScripts");
        Directory.CreateDirectory(_primary);
        Directory.CreateDirectory(_repo);

        var lds = new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);

        _engine = new ScriptEngine(_primary, new TypeAheadSession(),
                                   sendCommand: _ => { }, echo: l => _echoed.Add(l))
        {
            Config = _config,
        };
    }

    public void Dispose()
    {
        _engine.StopAll();
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── config plumbing ─────────────────────────────────────────────────────

    [Fact]
    public void Blank_reposcriptdir_resolves_to_scriptdir()
    {
        Assert.Equal("", _config.RepoScriptDirRaw);
        Assert.Equal(_config.ScriptDir, _config.RepoScriptDir);
    }

    [Fact]
    public void Set_reposcriptdir_resolves_to_the_configured_folder()
    {
        _config.SetSetting("reposcriptdir", _repo);
        Assert.Equal(Path.GetFullPath(_repo), Path.GetFullPath(_config.RepoScriptDir));
    }

    [Fact]
    public void Setting_reposcriptdir_blank_turns_the_feature_back_off()
    {
        _config.SetSetting("reposcriptdir", _repo);
        _config.SetSetting("reposcriptdir", "");
        Assert.Equal("", _config.RepoScriptDirRaw);
        Assert.Equal(_config.ScriptDir, _config.RepoScriptDir);
    }

    [Fact]
    public void Reposcriptdir_roundtrips_through_getsetting()
    {
        _config.SetSetting("reposcriptdir", _repo);
        Assert.Equal(_repo, _config.GetSetting("reposcriptdir"));
    }

    // ── engine resolution ───────────────────────────────────────────────────

    [Fact]
    public void Script_only_in_repo_dir_is_found_via_fallback()
    {
        _config.SetSetting("reposcriptdir", _repo);
        File.WriteAllText(Path.Combine(_repo, "pulled.cmd"), "echo hi");

        Assert.True(_engine.TryStart("pulled", Array.Empty<string>()));
        Assert.Equal(Path.GetFullPath(Path.Combine(_repo, "pulled.cmd")),
                     _engine.Instances.Single().SourcePath);
    }

    [Fact]
    public void Local_copy_shadows_the_repo_copy()
    {
        _config.SetSetting("reposcriptdir", _repo);
        File.WriteAllText(Path.Combine(_primary, "hunt.cmd"), "echo local");
        File.WriteAllText(Path.Combine(_repo, "hunt.cmd"), "echo repo");

        Assert.True(_engine.TryStart("hunt", Array.Empty<string>()));
        Assert.Equal(Path.GetFullPath(Path.Combine(_primary, "hunt.cmd")),
                     _engine.Instances.Single().SourcePath);
    }

    [Fact]
    public void Local_copy_shadows_regardless_of_extension()
    {
        // Directory-major search: every extension is probed in ScriptDir
        // before the repo dir is consulted, so even a different-extension
        // local script wins over the repo's default-extension one.
        _config.SetSetting("reposcriptdir", _repo);
        File.WriteAllText(Path.Combine(_primary, "hunt.js"), "// local");
        File.WriteAllText(Path.Combine(_repo, "hunt.cmd"), "echo repo");

        Assert.True(_engine.TryStart("hunt", Array.Empty<string>()));
        // The .js runtime's start echo is "[script] hunt started (js)" — its
        // presence proves the LOCAL hunt.js won; the repo's hunt.cmd would
        // have produced a path-bearing .cmd start line instead.
        Assert.Contains(_echoed, l => l.Contains("started (js)"));
        Assert.Empty(_engine.Instances);
    }

    [Fact]
    public void Repo_subfolder_scripts_resolve_like_scriptdir_ones()
    {
        _config.SetSetting("reposcriptdir", _repo);
        Directory.CreateDirectory(Path.Combine(_repo, "GenieHunter"));
        File.WriteAllText(Path.Combine(_repo, "GenieHunter", "hunt.cmd"), "echo hi");

        Assert.True(_engine.TryStart("GenieHunter/hunt", Array.Empty<string>()));
    }

    [Fact]
    public void Blank_reposcriptdir_searches_scriptdir_only()
    {
        File.WriteAllText(Path.Combine(_repo, "pulled.cmd"), "echo hi");

        Assert.False(_engine.TryStart("pulled", Array.Empty<string>()));
        Assert.DoesNotContain(_echoed, l => l.Contains(_repo));
    }

    [Fact]
    public void Reposcriptdir_equal_to_scriptdir_is_treated_as_off()
    {
        _config.SetSetting("reposcriptdir", _primary);
        Assert.Null(_engine.RepoScriptsDir);
    }

    [Fact]
    public void Not_found_echo_names_both_search_dirs()
    {
        _config.SetSetting("reposcriptdir", _repo);

        Assert.False(_engine.TryStart("nosuch", Array.Empty<string>()));
        var line = _echoed.Single(l => l.Contains("not found"));
        Assert.Contains(_primary, line);
        Assert.Contains(_repo, line);
    }
}
