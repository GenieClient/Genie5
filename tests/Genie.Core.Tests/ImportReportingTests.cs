using System;
using System.IO;
using System.Linq;
using Genie.Core.Aliases;
using Genie.Core.Highlights;
using Genie.Core.Config;
using Genie.Core.Import;
using Genie.Core.Runtime;
using Genie.Core.Macros;
using Genie.Core.Triggers;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// An import used to report only "N imported, M skipped", which is why a long
/// run of silent import bugs survived: a user could not tell whether those M
/// were duplicates they expected or rules that had just been lost. Each skip
/// now carries a line number, the offending text, a reason, and — the part
/// that matters — whether a rule was actually <see cref="ImportSkipKind.Dropped"/>
/// or skipped <see cref="ImportSkipKind.ByDesign"/>.
/// </summary>
public class ImportReportingTests
{
    private static GenieConfig NewConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "genie_import_report_" + Guid.NewGuid().ToString("N"));
        var lds  = new LocalDirectoryService("GenieImportReportTest", root);
        lds.UseExplicitRoot(root);
        return new GenieConfig(lds);
    }

    private static string WriteCfg(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g4report_{Guid.NewGuid():N}.cfg");
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── A lost rule is reported, with enough detail to act on ────────────

    [Fact]
    public void A_dropped_rule_reports_its_line_number_text_and_reason()
    {
        var path = WriteCfg(
            "// header",
            @"#trigger {^ok$} {#echo fine}",
            @"#trigger {^([unclosed} {#echo broken}");
        var engine = new TriggerEngineFinal();

        var r = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, r.Imported);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(1, r.DroppedCount);

        var drop = Assert.Single(r.Dropped);
        Assert.Equal(3, drop.LineNumber);                      // 1-based, counts comments
        Assert.Contains("unclosed", drop.Line);
        Assert.Contains("valid regular expression", drop.Reason);
        Assert.Equal(ImportSkipKind.Dropped, drop.Kind);
    }

    [Fact]
    public void A_malformed_directive_is_reported_as_dropped()
    {
        var path = WriteCfg("#trigger {only-one-argument}");
        var engine = new TriggerEngineFinal();

        var r = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        var drop = Assert.Single(r.Dropped);
        Assert.Equal(1, drop.LineNumber);
        Assert.Contains("not a recognised #trigger directive", drop.Reason);
    }

    [Fact]
    public void An_unknown_highlight_type_is_reported_as_dropped()
    {
        var path = WriteCfg(@"#highlight {sideways} {red} {^boom$}");
        var engine = new HighlightEngine();

        var r = Genie4Importer.ImportHighlights(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Contains("unknown highlight type", Assert.Single(r.Dropped).Reason);
    }

    // ── An expected skip is NOT reported as a loss ───────────────────────

    [Fact]
    public void An_already_present_rule_in_AddOnly_mode_is_not_a_loss()
    {
        var path = WriteCfg(@"#macro {F5} {look}");
        var engine = new MacroEngine();
        engine.Add("F5", "something else");

        var r = Genie4Importer.ImportMacros(path, engine, ImportMode.AddOnly);
        File.Delete(path);

        Assert.Equal(0, r.Imported);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(0, r.DroppedCount);          // skipped, but nothing lost
        Assert.Equal(ImportSkipKind.ByDesign, Assert.Single(r.Skips).Kind);
    }

    [Fact]
    public void An_alias_delete_record_is_not_a_loss()
    {
        var path = WriteCfg(@"#alias delete {gr} {get rope}");
        var engine = new AliasEngine();

        var r = Genie4Importer.ImportAliases(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(0, r.DroppedCount);
        Assert.Equal(ImportSkipKind.ByDesign, Assert.Single(r.Skips).Kind);
    }

    // ── Clean files stay silent ──────────────────────────────────────────

    [Fact]
    public void A_clean_import_reports_nothing()
    {
        var path = WriteCfg(
            @"#trigger {^a$} {#echo a}",
            "",
            "// comment",
            @"#trigger {^b$} {#echo b}");
        var engine = new TriggerEngineFinal();

        var r = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(2, r.Imported);
        Assert.Equal(0, r.Skipped);
        Assert.Empty(r.Skips);
        Assert.Empty(r.Dropped);
    }

    [Fact]
    public void Default_ImportResult_exposes_an_empty_skip_list()
    {
        // The property is backed by a nullable field; a result built without
        // skips must not hand callers a null.
        var r = new ImportResult(3, 0);

        Assert.NotNull(r.Skips);
        Assert.Empty(r.Skips);
        Assert.Equal(0, r.DroppedCount);
    }

    // ── Line numbers survive blank lines and comments ────────────────────

    [Fact]
    public void Line_numbers_refer_to_the_real_file_line()
    {
        var path = WriteCfg(
            "",                                   // 1
            "// a comment",                       // 2
            "",                                   // 3
            @"#trigger {^fine$} {#echo ok}",      // 4
            @"#trigger {} {#echo empty pattern}");// 5
        var engine = new TriggerEngineFinal();

        var r = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(5, Assert.Single(r.Dropped).LineNumber);
    }

    // ── Aggregation across a whole directory ─────────────────────────────

    [Fact]
    public void Directory_import_aggregates_losses_across_types()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"g4dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "triggers.cfg"), [@"#trigger {^([bad} {#echo x}"]);
        File.WriteAllLines(Path.Combine(dir, "macros.cfg"),   [@"#macro {}"]);

        var ctx = new Genie4ImportContext
        {
            Aliases     = new AliasEngine(),
            Triggers    = new TriggerEngineFinal(),
            Highlights  = new HighlightEngine(),
            Substitutes = new Genie.Core.Substitutes.SubstituteEngine(),
            Gags        = new Genie.Core.Gags.GagEngine(),
            Macros      = new MacroEngine(),
            Names       = new Genie.Core.Highlights.NameHighlightEngine(),
            Presets     = new Genie.Core.Presets.PresetEngine(),
            Variables   = new Genie.Core.Variables.VariableStore(),
            Classes     = new Genie.Core.Classes.ClassEngine(),
            Settings    = NewConfig(),
        };

        var result = Genie4Importer.ImportDirectory(dir, ctx, ImportMode.Replace);
        Directory.Delete(dir, recursive: true);

        Assert.True(result.AnyDropped);
        Assert.Equal(2, result.DroppedCount);
        // …and the labelled listing covers every type, including the two that
        // the dialog's summary used to leave out entirely.
        Assert.Contains(result.All, x => x.Label == "Names");
        Assert.Contains(result.All, x => x.Label == "Presets");
    }

    [Fact]
    public void Skip_renders_as_a_readable_report_line()
    {
        var skip = new ImportSkip(42, "#trigger {bad", "not a recognised #trigger directive", ImportSkipKind.Dropped);

        Assert.Equal("line 42: not a recognised #trigger directive — #trigger {bad", skip.ToString());
    }

    [Fact]
    public void A_very_long_line_is_truncated_in_the_report()
    {
        var skip = new ImportSkip(1, new string('x', 400), "too long", ImportSkipKind.Dropped);

        Assert.True(skip.ToString().Length < 200);
        Assert.EndsWith("…", skip.ToString());
    }
}
