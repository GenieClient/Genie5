using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Highlights;
using Genie.Core.Import;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A Genie 4 config folder copied straight into Genie 5's Config directory is
/// replayed line by line through the command engine at connect. Genie 4 and
/// Genie 5 order the arguments of <c>#highlight</c> and <c>#name</c>
/// differently, so every one of those lines used to land as a nonsense rule —
/// and because Genie 4 puts the match TYPE first, all 235 highlights in the
/// reference file collided on the two patterns "regexp" and "beginswith" and
/// upserted each other down to two rules.
/// </summary>
public class Genie4CfgDialectTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public Genie4CfgDialectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_dialect_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieDialectTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (CommandEngine Engine, HighlightEngine Highlights, NameHighlightEngine Names) NewEngine()
    {
        var host       = new FakeCommandHost();
        var engine     = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        var highlights = new HighlightEngine();
        var names      = new NameHighlightEngine();
        engine.Highlights = highlights;
        engine.Names      = names;
        return (engine, highlights, names);
    }

    // ── Genie 4's highlight dialect ──────────────────────────────────────

    [Fact]
    public void A_Genie4_highlight_line_lands_as_the_rule_it_describes()
    {
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {beginswith} {#DFEB9E} {Also here:}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal("Also here:", rule.Pattern);            // NOT "beginswith"
        Assert.Equal("#DFEB9E",    rule.ForegroundColor);
        Assert.Equal("",           rule.BackgroundColor);     // NOT the pattern
        Assert.Equal(HighlightMatchType.BeginsWith, rule.MatchType);
    }

    [Fact]
    public void Genie4_spells_regex_as_regexp_and_it_must_not_degrade_to_a_string_match()
    {
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {regexp} {#FFE300} {\b(analyzing)\b}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal(HighlightMatchType.Regex, rule.MatchType);
        Assert.Equal(@"\b(analyzing)\b", rule.Pattern);
    }

    [Fact]
    public void Genie4_highlight_class_and_sound_arguments_are_kept()
    {
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {beginswith} {#B2E6FF} {You've gained a new rank in} {} {NewRank.wav}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal("You've gained a new rank in", rule.Pattern);
        Assert.Equal("NewRank.wav", rule.SoundFile);
    }

    [Fact]
    public void Genie4_comma_packed_colours_split_into_foreground_and_background()
    {
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {string} {#FFFFFF,#000000} {alarm}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal("#FFFFFF", rule.ForegroundColor);
        Assert.Equal("#000000", rule.BackgroundColor);
    }

    [Fact]
    public void A_whole_Genie4_highlight_file_does_not_collapse_onto_two_rules()
    {
        // The actual reported symptom. Distinct patterns must stay distinct
        // rather than all upserting the literal words "regexp"/"beginswith".
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {regexp} {#FF0000} {^first}");
        engine.ProcessInput(@"#highlight {regexp} {#00FF00} {^second}");
        engine.ProcessInput(@"#highlight {beginswith} {#0000FF} {third}");

        Assert.Equal(3, highlights.Rules.Count());
        Assert.Contains(highlights.Rules, r => r.Pattern == "^first");
        Assert.Contains(highlights.Rules, r => r.Pattern == "^second");
        Assert.Contains(highlights.Rules, r => r.Pattern == "third");
    }

    // ── Genie 5's own order must be untouched ────────────────────────────

    [Fact]
    public void Genie5_order_still_works()
    {
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight add {^you die$} {red} {black} {regex}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal("^you die$", rule.Pattern);
        Assert.Equal("red",   rule.ForegroundColor);
        Assert.Equal("black", rule.BackgroundColor);
        Assert.Equal(HighlightMatchType.Regex, rule.MatchType);
    }

    [Fact]
    public void A_two_argument_Genie5_highlight_is_not_mistaken_for_Genie4()
    {
        // Only three-or-more-argument lines can be the Genie 4 shape, so a
        // pattern that happens to be the word "line" still works.
        var (engine, highlights, _) = NewEngine();

        engine.ProcessInput(@"#highlight {line} {red}");

        var rule = Assert.Single(highlights.Rules);
        Assert.Equal("line", rule.Pattern);
        Assert.Equal("red",  rule.ForegroundColor);
    }

    // ── Genie 4's name dialect ───────────────────────────────────────────

    [Fact]
    public void A_Genie4_name_line_with_a_hex_colour_lands_the_right_way_round()
    {
        var (engine, _, names) = NewEngine();

        engine.ProcessInput(@"#name {#FF0000} {Fred}");

        var rule = Assert.Single(names.Rules);
        Assert.Equal("Fred",    rule.Name);              // NOT "#FF0000"
        Assert.Equal("#FF0000", rule.ForegroundColor);
    }

    [Fact]
    public void Genie5_name_order_still_works()
    {
        var (engine, _, names) = NewEngine();

        engine.ProcessInput(@"#names add {Fred} {#FF0000}");

        var rule = Assert.Single(names.Rules);
        Assert.Equal("Fred",    rule.Name);
        Assert.Equal("#FF0000", rule.ForegroundColor);
    }

    [Fact]
    public void A_player_actually_called_Red_is_not_reinterpreted()
    {
        // Named colours stay ambiguous on purpose — only #RRGGBB triggers the
        // Genie 4 reading, and a name can never be that.
        var (engine, _, names) = NewEngine();

        engine.ProcessInput(@"#names {Red} {blue}");

        var rule = Assert.Single(names.Rules);
        Assert.Equal("Red",  rule.Name);
        Assert.Equal("blue", rule.ForegroundColor);
    }

    // ── The importer agrees with the command path ────────────────────────

    [Fact]
    public void The_importer_keeps_the_highlight_sound_file_too()
    {
        var path = Path.Combine(Path.GetTempPath(), $"g4hl_{Guid.NewGuid():N}.cfg");
        File.WriteAllLines(path, [@"#highlight {beginswith} {#B2E6FF} {You've gained a new rank in} {} {NewRank.wav}"]);
        var engine = new HighlightEngine();

        var r = Genie4Importer.ImportHighlights(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, r.Imported);
        Assert.Equal("NewRank.wav", Assert.Single(engine.Rules).SoundFile);
    }

    private sealed class FakeCommandHost : ICommandHost
    {
        public int BeepCalls { get; private set; }
        public List<string> Echoes { get; } = new();

        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;

        public void Beep() => BeepCalls++;
        public void Echo(string text) => Echoes.Add(text);
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) { }
        public void RunScript(string text) { }
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void SetTraceLevelAll(int level) { }
        public IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
        public void SetGlobalVariable(string name, string value) => Globals[name] = value;
        public void RemoveGlobalVariable(string name) => Globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) { }
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) { }
        public void MapperGoto(string args) { }
        public void MapperCommand(string args) { }
        public void MapperReset() { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
