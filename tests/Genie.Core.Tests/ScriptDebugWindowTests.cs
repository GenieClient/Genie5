using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #366 — <c>#config scriptdebugwindow &lt;name&gt;</c> sends script
/// <c>[dbg:N]</c> trace lines to a named window instead of the Game window.
/// </summary>
public sealed class ScriptDebugWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_dbgwin_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieDbgWinTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    private (List<string> Main, List<(string Msg, string? Win)> Windows) Run(GenieConfig cfg)
    {
        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.cmd"), "debug 1\ngoto there\n:there\necho plain output\n");
        var main = new List<string>();
        var windows = new List<(string, string?)>();
        var engine = new ScriptEngine(dir, new TypeAheadSession(), sendCommand: _ => { }, echo: main.Add)
        {
            Config = cfg,
            EchoTo = (msg, win, _) => windows.Add((msg, win)),
        };
        engine.TryStart("t", new List<string>());
        for (int i = 0; i < 50; i++) engine.Tick();
        return (main, windows);
    }

    [Fact]
    public void Unset_debug_lines_stay_in_the_game_window()
    {
        var (main, windows) = Run(NewConfig());

        Assert.Contains(main, l => l.StartsWith("[dbg:1] goto there"));
        Assert.Empty(windows);
    }

    [Fact]
    public void Set_debug_lines_go_to_the_named_window_and_ordinary_output_does_not()
    {
        var cfg = NewConfig();
        cfg.SetSetting("scriptdebugwindow", "ScriptDebug");

        var (main, windows) = Run(cfg);

        Assert.Contains(windows, w => w.Win == "ScriptDebug" && w.Msg.StartsWith("[dbg:1] goto there"));
        Assert.DoesNotContain(main, l => l.StartsWith("[dbg:"));
        Assert.Contains("plain output", main);       // script echo is not debug output
    }

    [Theory]
    [InlineData(">ScriptDebug", "ScriptDebug")]
    [InlineData("  Debug Pane ", "Debug Pane")]
    [InlineData("none", "")]
    [InlineData("main", "")]
    [InlineData("Game", "")]
    [InlineData("off", "")]
    [InlineData("", "")]
    public void The_key_accepts_the_echo_spelling_and_clears_on_main_or_none(string value, string stored)
    {
        var cfg = NewConfig();
        cfg.ScriptDebugWindow = "Previous";

        cfg.SetSetting("scriptdebugwindow", value);

        Assert.Equal(stored, cfg.ScriptDebugWindow);
    }

    [Fact]
    public void The_key_is_saved_listed_and_round_trips()
    {
        var cfg = NewConfig();
        cfg.SetSetting("scriptdebugwindow", "ScriptDebug");
        Assert.Contains(GenieConfig.ConfigCategories,
            c => c.Category == "Scripting" && c.Keys.Contains("scriptdebugwindow"));

        cfg.Save();
        var reloaded = NewConfig();
        reloaded.Load();

        Assert.Equal("ScriptDebug", reloaded.ScriptDebugWindow);
    }

    [Fact]
    public void An_empty_value_round_trips_as_empty()
    {
        var cfg = NewConfig();
        cfg.Save();
        var reloaded = NewConfig();
        reloaded.Load();

        Assert.Equal("", reloaded.ScriptDebugWindow);
    }
}
