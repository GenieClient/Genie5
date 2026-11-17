using System;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Record (<c>automapperrecord</c>) is session-only: never restored from disk,
/// never saved as True. <c>automapper</c> is the mapper's on/off switch (Genie 4
/// parity, #401): default on and saved, except that a file written before #401
/// (no <c>automapperrecord</c> line) holds a Record value there, which is ignored.
/// </summary>
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

    private string Write(params string[] lines)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Defaults_MapperOn_RecordOff()
    {
        var cfg = NewConfig();
        Assert.True(cfg.AutoMapper);
        Assert.False(cfg.AutoMapperRecord);
    }

    [Fact]
    public void Load_IgnoresASavedRecordTrue_ButStillLoadsOtherSettings()
    {
        var cfg = NewConfig();
        cfg.Load(Write("#config {automapper} {True}", "#config {automapperrecord} {True}", "#config {automapperalpha} {96}"));

        Assert.False(cfg.AutoMapperRecord);
        Assert.True(cfg.AutoMapper);
        Assert.Equal(96, cfg.AutoMapperAlpha);
    }

    [Fact]
    public void RecordOnInTheSession_IsReportedOn_ButSavedAsFalse()
    {
        var cfg = NewConfig();
        cfg.SetSetting("automapperrecord", "True");
        Assert.True(cfg.AutoMapperRecord);
        Assert.Equal("True", cfg.GetSetting("automapperrecord"));     // the live value is honoured

        var path = Path.Combine(_dir, "out.cfg");
        Assert.True(cfg.Save(path));
        var lines = File.ReadAllLines(path);
        Assert.Contains("#config {automapperrecord} {False}", lines);
        Assert.Contains("#config {automapper} {True}", lines);
    }

    [Fact]
    public void PreFix401File_ItsAutomapperFalseIsIgnored_MapperStaysOn()
    {
        // Every Genie 5 settings.cfg before #401 says `automapper False`: that was
        // Record, always saved as False, not the user turning the mapper off.
        var cfg = NewConfig();
        cfg.Load(Write("#config {automapper} {False}", "#config {automapperalpha} {96}"));

        Assert.True(cfg.AutoMapper);
        Assert.Equal(96, cfg.AutoMapperAlpha);
    }

    [Fact]
    public void FileSavedSince401_AutomapperFalseIsHonoured()
    {
        var cfg = NewConfig();
        cfg.SetSetting("automapper", "False");
        var path = Path.Combine(_dir, "off.cfg");
        Assert.True(cfg.Save(path));

        var reloaded = NewConfig();
        reloaded.Load(path);
        Assert.False(reloaded.AutoMapper);
    }

    [Fact]
    public void AutomapperKey_NeverChangesRecord()
    {
        var cfg = NewConfig();
        cfg.SetSetting("automapper", "True");
        Assert.False(cfg.AutoMapperRecord);
        cfg.SetSetting("automapper", "False");
        Assert.False(cfg.AutoMapperRecord);
    }
}
