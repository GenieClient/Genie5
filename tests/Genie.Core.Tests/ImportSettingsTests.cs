using System;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Genie.Core.Import;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A Genie 4 <c>settings.cfg</c> was never offered by the import dialog at
/// all. Wiring it up naively would have been worse than leaving it out: the
/// reference file carries <c>scriptdir {Scripts}</c>, <c>configdir {Config}</c>
/// and <c>logdir {Logs}</c> — paths relative to the Genie 4 install — and
/// applying those would repoint Genie 5's data directories.
/// </summary>
public class ImportSettingsTests
{
    private static string WriteCfg(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g4settings_{Guid.NewGuid():N}.cfg");
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>A config rooted in a throwaway temp dir, like the other suites.</summary>
    private static GenieConfig NewConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "genie_import_settings_" + Guid.NewGuid().ToString("N"));
        var lds  = new LocalDirectoryService("GenieImportSettingsTest", root);
        lds.UseExplicitRoot(root);
        return new GenieConfig(lds);
    }

    private static (ImportResult Result, GenieConfig Config) Import(params string[] lines)
    {
        var path = WriteCfg(lines);
        var cfg  = NewConfig();
        var r    = Genie4Importer.ImportSettings(path, cfg, ImportMode.Merge);
        File.Delete(path);
        return (r, cfg);
    }

    // ── The dangerous ones ───────────────────────────────────────────────

    [Theory]
    [InlineData("scriptdir", "Scripts")]
    [InlineData("configdir", "Config")]
    [InlineData("logdir",    "Logs")]
    [InlineData("mapdir",    "Maps")]
    [InlineData("plugindir", "Plugins")]
    public void Directory_keys_are_refused_and_reported(string key, string value)
    {
        var (r, _) = Import($"#config {{{key}}} {{{value}}}");

        Assert.Equal(0, r.Imported);
        var drop = Assert.Single(r.Dropped);
        Assert.Contains("Genie 4 folder path", drop.Reason);
        Assert.Contains("repoint", drop.Reason);
    }

    [Fact]
    public void A_directory_key_does_not_reach_the_config()
    {
        // The real failure mode: Genie 5's script directory silently becoming
        // a relative "Scripts" folder from a Genie 4 install. Must be the SAME
        // config before and after — each NewConfig() gets its own temp root,
        // so two instances legitimately disagree about ScriptDir.
        var cfg    = NewConfig();
        var before = cfg.ScriptDir;

        var path = WriteCfg(@"#config {scriptdir} {Scripts}");
        Genie4Importer.ImportSettings(path, cfg, ImportMode.Merge);
        File.Delete(path);

        Assert.Equal(before, cfg.ScriptDir);
        Assert.NotEqual("Scripts", cfg.ScriptDir);
    }

    // ── Keys that carry over cleanly ─────────────────────────────────────

    [Fact]
    public void Ordinary_settings_are_applied()
    {
        var (r, cfg) = Import(
            @"#config {scriptchar} {.}",
            @"#config {separatorchar} {;}",
            @"#config {triggeroninput} {True}",
            @"#config {autolog} {True}");

        Assert.Equal(4, r.Imported);
        Assert.Equal(0, r.DroppedCount);
        Assert.Equal('.', cfg.ScriptChar);
        Assert.Equal(';', cfg.SeparatorChar);
        Assert.True(cfg.TriggerOnInput);
    }

    [Fact]
    public void A_renamed_key_is_applied_under_its_new_name()
    {
        // Genie 4's maxrowbuffer is Genie 5's scrollbacklines.
        var (r, cfg) = Import(@"#config {maxrowbuffer} {5000}");

        Assert.Equal(1, r.Imported);
        Assert.Equal(0, r.DroppedCount);
        Assert.Equal(5000, cfg.ScrollbackLines);
    }

    // ── Keys that need a human decision ──────────────────────────────────

    [Fact]
    public void Connectstring_is_reported_with_the_command_to_run_instead()
    {
        var (r, _) = Import(@"#config {connectstring} {FE:STORMFRONT /VERSION:1.0.1.26 /P:WIN_XP /XML}");

        var drop = Assert.Single(r.Dropped);
        Assert.Contains("#config frontend wrayth", drop.Reason);
    }

    [Theory]
    [InlineData("servertimeout")]
    [InlineData("usertimeout")]
    [InlineData("servertimeoutcommand")]
    [InlineData("usertimeoutcommand")]
    public void The_four_timeout_keys_point_at_activitytimeout(string key)
    {
        var (r, _) = Import($"#config {{{key}}} {{300}}");

        Assert.Contains("activitytimeout", Assert.Single(r.Dropped).Reason);
    }

    [Fact]
    public void An_unknown_key_says_so_rather_than_vanishing()
    {
        var (r, _) = Import(@"#config {nosuchsetting} {1}");

        var drop = Assert.Single(r.Dropped);
        Assert.Contains("not a Genie 5 setting", drop.Reason);
        Assert.Equal(1, drop.LineNumber);
    }

    [Fact]
    public void A_malformed_line_is_reported()
    {
        var (r, _) = Import("#config {onlyakey}");

        Assert.Contains("not a recognised #config directive", Assert.Single(r.Dropped).Reason);
    }

    // ── The real file, end to end ────────────────────────────────────────

    [Fact]
    public void The_reference_settings_file_applies_what_it_can_and_reports_the_rest()
    {
        // Shape taken from the reference Genie 4 settings.cfg: a mix of keys
        // that carry over, three folder paths, and the timeout family.
        var (r, cfg) = Import(
            @"#config {scriptchar} {.}",
            @"#config {commandchar} {#}",
            @"#config {triggeroninput} {True}",
            @"#config {maxrowbuffer} {10}",
            @"#config {scriptdir} {Scripts}",
            @"#config {configdir} {Config}",
            @"#config {logdir} {Logs}",
            @"#config {connectstring} {FE:STORMFRONT /XML}",
            @"#config {servertimeout} {300}",
            @"#config {ignoreclosealert} {True}");

        Assert.Equal(5, r.Imported);        // scriptchar, commandchar, triggeroninput, maxrowbuffer, ignoreclosealert
        Assert.Equal(5, r.DroppedCount);    // 3 dirs + connectstring + servertimeout
        Assert.All(r.Dropped, d => Assert.Equal(ImportSkipKind.Dropped, d.Kind));

        // maxrowbuffer 10 is below the floor Genie 5 clamps to, which is a
        // value decision inside SetSetting and not this importer's business —
        // what matters is that it was applied rather than dropped.
        Assert.True(cfg.ScrollbackLines >= 100);
    }

    [Fact]
    public void Comments_and_blank_lines_are_not_counted()
    {
        var (r, _) = Import("", "// a note", "#! shebangish", @"#config {scriptchar} {.}");

        Assert.Equal(1, r.Imported);
        Assert.Equal(0, r.Skipped);
    }
}
