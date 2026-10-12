using System;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>Record (automapper) is session-only: never restored from disk, never saved as True.</summary>
public sealed class AutoMapperSessionOnlyConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "g5cfg-" + Guid.NewGuid().ToString("N"));
    public AutoMapperSessionOnlyConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieRecordCfgTest", _dir);
        lds.UseExplicitRoot(_dir);
        return new GenieConfig(lds);
    }

    [Fact]
    public void Load_IgnoresASavedAutomapperTrue_ButStillLoadsOtherSettings()
    {
        var path = Path.Combine(_dir, "settings.cfg");
        File.WriteAllLines(path, new[] { "#config {automapper} {True}", "#config {automapperalpha} {96}" });

        var cfg = NewConfig();
        cfg.Load(path);

        Assert.False(cfg.AutoMapper);
        Assert.Equal(96, cfg.AutoMapperAlpha);
    }

    [Fact]
    public void RecordOnInTheSession_IsReportedOn_ButSavedAsFalse()
    {
        var cfg = NewConfig();
        cfg.SetSetting("automapper", "True");
        Assert.True(cfg.AutoMapper);
        Assert.Equal("True", cfg.GetSetting("automapper"));          // the live value is honoured

        var path = Path.Combine(_dir, "out.cfg");
        Assert.True(cfg.Save(path));
        Assert.Contains("#config {automapper} {False}", File.ReadAllLines(path));
        Assert.DoesNotContain(File.ReadAllLines(path), l => l.Contains("{automapper} {True}"));
    }
}
