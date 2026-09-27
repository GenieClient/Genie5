using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #243 — the built-in script editor. <c>#config externaleditor on|off</c>
/// keeps the old open-in-another-program behaviour available, and the engine
/// exposes its start-time resolver so <c>#edit</c> opens the copy a start runs.
/// </summary>
public class ExternalEditorConfigTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "genie_exteditor_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieExtEditorTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    private ScriptEngine NewEngine(string dir, GenieConfig? cfg = null)
    {
        Directory.CreateDirectory(dir);
        return new ScriptEngine(dir, new Genie.Core.Scripting.TypeAheadSession(), _ => { }, _ => { })
        {
            Config = cfg,
        };
    }

    [Fact]
    public void Defaults_off_so_the_built_in_editor_is_used()
    {
        Assert.False(NewConfig().ExternalEditor);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    public void Config_command_sets_it(string value, bool expected)
    {
        var cfg = NewConfig();
        cfg.ExternalEditor = !expected;
        cfg.SetSetting("externaleditor", value);
        Assert.Equal(expected, cfg.ExternalEditor);
    }

    [Fact]
    public void Key_is_saved_listed_under_scripting_and_round_trips()
    {
        var cfg = NewConfig();
        cfg.ExternalEditor = true;

        Assert.Equal("True", cfg.ToConfigPairs().Single(p => p.Key == "externaleditor").Value);
        Assert.Contains(GenieConfig.ConfigCategories,
            c => c.Category == "Scripting" && c.Keys.Contains("externaleditor"));

        cfg.Save();
        var reloaded = NewConfig();
        reloaded.Load();
        Assert.True(reloaded.ExternalEditor);
    }

    [Fact]
    public void ResolveScriptFile_matches_the_start_lookup_order()
    {
        var dir = Path.Combine(_root, "Scripts");
        var engine = NewEngine(dir);
        File.WriteAllText(Path.Combine(dir, "hunt.js"), "");
        Assert.Equal(Path.Combine(dir, "hunt.js"), engine.ResolveScriptFile("hunt"));

        File.WriteAllText(Path.Combine(dir, "hunt.cmd"), "");
        Assert.Equal(Path.Combine(dir, "hunt.cmd"), engine.ResolveScriptFile("hunt"));   // .cmd before .js
        Assert.Equal(Path.Combine(dir, "hunt.js"), engine.ResolveScriptFile("hunt.js")); // typed extension wins
        Assert.Null(engine.ResolveScriptFile("nosuch"));
    }

    [Fact]
    public void IsUnderScriptRoots_accepts_the_scripts_tree_only()
    {
        var dir = Path.Combine(_root, "Scripts");
        var engine = NewEngine(dir);

        Assert.True(engine.IsUnderScriptRoots(Path.Combine(dir, "a.cmd")));
        Assert.True(engine.IsUnderScriptRoots(Path.Combine(dir, "sub", "b.cmd")));
        Assert.False(engine.IsUnderScriptRoots(Path.Combine(_root, "settings.cfg")));
        Assert.False(engine.IsUnderScriptRoots(Path.Combine(dir, "..", "escape.cmd")));
        // A sibling folder whose name merely starts with the root's is outside.
        Assert.False(engine.IsUnderScriptRoots(Path.Combine(_root, "Scripts2", "c.cmd")));
    }
}
