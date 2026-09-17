using System;
using System.IO;
using System.Linq;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Import;
using Genie.Core.Macros;
using Genie.Core.Parsing;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Every rule importer used its own regex with <c>[^{}]*</c> for the payload,
/// so any rule whose own text contained a brace was counted as "skipped" and
/// silently never imported — and Genie 4 payloads very often contain braces,
/// because <c>#if</c> and <c>#eval</c> take braced arguments. In the reference
/// settings tree that dropped 9 triggers and 2 macros.
///
/// The importers now tokenize with <see cref="ArgumentParser"/>, the same
/// tokenizer the command engine uses when a <c>.cfg</c> is replayed, so the
/// importer and the running client can no longer disagree about what a valid
/// rule is.
/// </summary>
public class Genie4ImporterBraceTests
{
    private static string WriteCfg(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g4import_{Guid.NewGuid():N}.cfg");
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── Triggers ─────────────────────────────────────────────────────────

    [Fact]
    public void Trigger_action_containing_braces_is_imported()
    {
        var path = WriteCfg(
            @"#trigger {^(\w+) appears to be aiming at you} {#if {$guild = Paladin} ""#send glyph ward $1""}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Skipped);
        var t = Assert.Single(engine.Triggers);
        Assert.Equal(@"^(\w+) appears to be aiming at you", t.Pattern);
        Assert.Equal(@"#if {$guild = Paladin} ""#send glyph ward $1""", t.Action);
    }

    [Fact]
    public void Trigger_action_with_several_brace_groups_is_imported_whole()
    {
        var path = WriteCfg(
            @"#trigger {e/$roomobjs/} {#var monsterdead #eval {count(""$roomobjs"",""appears dead"")} + #eval {count(""$roomobjs"",""(dead)"")}}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        var t = Assert.Single(engine.Triggers);
        // Both #eval groups survive, and the trailing one is not truncated.
        Assert.Contains(@"#eval {count(""$roomobjs"",""appears dead"")}", t.Action);
        Assert.EndsWith(@"#eval {count(""$roomobjs"",""(dead)"")}", t.Action);
    }

    [Fact]
    public void Trigger_class_argument_still_parses_after_a_braced_action()
    {
        var path = WriteCfg(@"#trigger {^you die} {#if {$health < 10} {flee}} {combat}");
        var engine = new TriggerEngineFinal();

        Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal("combat", Assert.Single(engine.Triggers).ClassName);
    }

    // ── Macros ───────────────────────────────────────────────────────────

    [Fact]
    public void Macro_action_containing_an_escaped_brace_is_imported()
    {
        // The escaped } must not close the group. Genie 4 keeps the backslash
        // (it takes substrings and never unescapes at this stage), so the
        // stored action keeps it too.
        var path = WriteCfg(@"#macro {Z, Alt} {\\x;'\}$speak @}");
        var engine = new MacroEngine();

        var result = Genie4Importer.ImportMacros(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Skipped);
        var m = Assert.Single(engine.Rules);
        Assert.Equal("Z, Alt", m.Key);
        Assert.Equal(@"\\x;'\}$speak @", m.Action);
        // …and it is reachable from the key a real keystroke produces.
        Assert.Equal(m.Action, engine.Get("alt+z")?.Action);
    }

    [Fact]
    public void Macro_action_containing_a_brace_group_is_imported()
    {
        var path = WriteCfg(@"#macro {F5} {#if {$stamina > 50} {dance}}");
        var engine = new MacroEngine();

        Genie4Importer.ImportMacros(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(@"#if {$stamina > 50} {dance}", Assert.Single(engine.Rules).Action);
    }

    // ── The other rule types share the fix ───────────────────────────────

    [Fact]
    public void Substitute_replacement_containing_braces_is_imported()
    {
        var path = WriteCfg(@"#subs {^gold ring$} {#eval {1+1} ring}");
        var engine = new SubstituteEngine();

        var result = Genie4Importer.ImportSubstitutes(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Gag_pattern_containing_braces_is_imported()
    {
        var path = WriteCfg(@"#gag {^A \{quirky\} message$}");
        var engine = new GagEngine();

        var result = Genie4Importer.ImportGags(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void Highlight_with_a_braced_pattern_is_imported()
    {
        var path = WriteCfg(@"#highlight {regexp} {red} {^\{tag\} .+$} {combat}");
        var engine = new HighlightEngine();

        var result = Genie4Importer.ImportHighlights(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal("combat", Assert.Single(engine.Rules).ClassName);
    }

    // ── Nothing that used to work may stop working ───────────────────────

    [Fact]
    public void Ordinary_rules_are_unaffected()
    {
        var path = WriteCfg(
            @"#trigger {^you are stunned$} {#echo stunned}",
            @"#trigger {^(\w+) swings at you$} {#echo incoming} {combat}",
            "// a comment",
            "");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(2, result.Imported);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void A_malformed_line_is_still_skipped()
    {
        var path = WriteCfg("#trigger {only-one-argument}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void An_invalid_regex_is_still_skipped()
    {
        var path = WriteCfg(@"#trigger {^([unclosed} {#echo nope}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
    }

    // ── Tokenizer-level guarantees the importers now depend on ───────────

    [Fact]
    public void Escaped_brace_does_not_close_a_group()
    {
        var parts = ArgumentParser.ParseArgs(@"#macro {Z, Alt} {\\x;'\}$speak @}");

        Assert.Equal(new[] { "#macro", "Z, Alt", @"\\x;'\}$speak @" }, parts);
    }

    [Theory]
    // Regex metacharacters are by far the commonest use of a backslash in a
    // Genie 4 config; escape handling must leave them exactly as they were.
    [InlineData(@"{^(\w+) swings$}",  @"^(\w+) swings$")]
    [InlineData(@"{^Kertigen\.$}",    @"^Kertigen\.$")]
    [InlineData(@"{a\\b}",            @"a\\b")]
    [InlineData(@"{say \""hi\""}",    @"say \""hi\""")]
    public void Backslashes_are_preserved_verbatim(string input, string expected)
        => Assert.Equal(expected, Assert.Single(ArgumentParser.ParseArgs(input)));

    [Fact]
    public void RawTail_agrees_with_ParseArgs_about_escaped_braces()
    {
        // RawTail duplicates the tokenizer's state machine; if the two drift,
        // value-position commands slice at the wrong offset.
        const string line = @"#macro {Z, Alt} {\\x;'\}$speak @}";

        Assert.Equal(@"{\\x;'\}$speak @}", ArgumentParser.RawTail(line, 2));
    }
}
