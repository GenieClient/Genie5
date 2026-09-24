using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #365 — <c>#config alwaysshowscrollbars on|off</c>, the settings.cfg
/// alias of the display.json "Always show scrollbars" toggle. The host applies
/// it on <see cref="ConfigFieldUpdated.AlwaysShowScrollbars"/>.
/// </summary>
public class AlwaysShowScrollbarsConfigTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "genie_scrollcfg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieScrollCfgTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    [Fact]
    public void Defaults_off()
    {
        Assert.False(NewConfig().AlwaysShowScrollbars);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    public void Config_command_sets_it_and_notifies_the_host(string value, bool expected)
    {
        var cfg = NewConfig();
        cfg.AlwaysShowScrollbars = !expected;
        var seen = new List<ConfigFieldUpdated>();
        cfg.ConfigChanged += seen.Add;

        cfg.SetSetting("alwaysshowscrollbars", value);

        Assert.Equal(expected, cfg.AlwaysShowScrollbars);
        Assert.Contains(ConfigFieldUpdated.AlwaysShowScrollbars, seen);
    }

    [Fact]
    public void Key_is_saved_listed_and_round_trips()
    {
        var cfg = NewConfig();
        cfg.AlwaysShowScrollbars = true;

        Assert.Equal("True", cfg.ToConfigPairs().Single(p => p.Key == "alwaysshowscrollbars").Value);
        Assert.Contains(GenieConfig.ConfigCategories,
            c => c.Category == "Window / Input" && c.Keys.Contains("alwaysshowscrollbars"));

        cfg.Save();
        var reloaded = NewConfig();
        reloaded.Load();
        Assert.True(reloaded.AlwaysShowScrollbars);
    }
}
