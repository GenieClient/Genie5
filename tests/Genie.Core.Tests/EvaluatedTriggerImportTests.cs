using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Import;
using Genie.Core.Triggers;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4's <c>e/…/</c> is an EVALUATED trigger: it fires when the value of the
/// enclosed expression changes, not when a line of game text matches. It is not a
/// delimiter style alongside <c>/…/</c> and <c>/…/i</c>.
///
/// Both the importer and the command engine used to treat it as one, strip the
/// marker and keep the inside, so <c>e/$roomobjs/</c> became a text trigger whose
/// pattern was the literal <c>$roomobjs</c>. Such a rule can never fire (see
/// <see cref="An_imported_var_pattern_could_never_have_matched"/>), yet it counted
/// as imported — which for this one shape defeated the honest-count guarantee
/// <c>7e14c38</c> established, because a downgraded rule never reaches the
/// NOT IMPORTED report. Four rules in one ordinary reference config use the form.
///
/// Both doors are covered here: the import dialog (<see cref="Genie4Importer"/>)
/// and a hand-copied <c>triggers.cfg</c> replayed through the command engine.
/// Public #353.
/// </summary>
public class EvaluatedTriggerImportTests : IDisposable
{
    private static string WriteCfg(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"g4eval_{Guid.NewGuid():N}.cfg");
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── The premise the fix rests on ─────────────────────────────────────────

    /// <summary>
    /// The reason reporting beats degrading: the degraded rule was dead on arrival.
    /// Trigger patterns are run as regexes verbatim and are never variable-
    /// substituted (<c>TriggerRule.SafeMatch</c> calls <c>Regex.Match</c> on the
    /// stored pattern), so <c>$roomobjs</c> is an end-anchor followed by literal
    /// text and cannot match any line.
    /// </summary>
    [Fact]
    public void An_imported_var_pattern_could_never_have_matched()
    {
        var rule = new TriggerRule("$roomobjs", "#echo fired");

        Assert.False(rule.IsMatch("You also see a gold-trimmed plaque and a stone turtle."));
        Assert.False(rule.IsMatch("$roomobjs"));
        Assert.Null(rule.SafeMatch("anything at all"));
    }

    // ── Door 1: the import dialog ────────────────────────────────────────────

    [Fact]
    public void Evaluated_trigger_is_reported_as_dropped_not_imported()
    {
        var path = WriteCfg(@"#trigger {e/$roomobjs/} {#var monsterdead #eval {count(""$roomobjs"",""appears dead"")}}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(0, result.Imported);
        Assert.Empty(engine.Triggers);

        var skip = Assert.Single(result.Skips);
        Assert.Equal(ImportSkipKind.Dropped, skip.Kind);
        Assert.Equal(1, skip.LineNumber);
        Assert.Contains("evaluated trigger", skip.Reason, StringComparison.OrdinalIgnoreCase);
        // The user is told what was lost, not just that something was.
        Assert.Contains("e/$roomobjs/", skip.Line);
    }

    /// <summary>
    /// The shape seen in the reference config: four evaluated rules among ordinary
    /// ones. The ordinary rules must still import, and the count must be honest
    /// about the four — that is the whole point of #353 over a silent downgrade.
    /// </summary>
    [Fact]
    public void Ordinary_triggers_around_them_still_import_and_the_count_is_honest()
    {
        var path = WriteCfg(
            @"#trigger {^You feel fully rested} {#echo rested}",
            @"#trigger {e/$monstercount/} {#statusbar 4 Monstercount: $monstercount/$monsterdead}",
            @"#trigger {e/$monsterdead/} {#statusbar 4 Monstercount: $monstercount/$monsterdead}",
            @"#trigger {e/$roomobjs/} {#var monsterdead 0}",
            @"#trigger {e/$Time.timeOfDay/} {#statusbar 1 $Time.season: $Time.timeOfDay}",
            @"#trigger {^The wind picks up} {#echo windy}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(2, result.Imported);
        Assert.Equal(4, result.Skipped);
        Assert.Equal(4, result.Skips.Count(s => s.Kind == ImportSkipKind.Dropped));
        Assert.Equal(new[] { 2, 3, 4, 5 }, result.Skips.Select(s => s.LineNumber).ToArray());

        // Nothing carrying a bare $var pattern was banked.
        Assert.DoesNotContain(engine.Triggers, t => t.Pattern.Contains('$'));
        Assert.Equal(new[] { "^You feel fully rested", "^The wind picks up" },
                     engine.Triggers.Select(t => t.Pattern).ToArray());
    }

    /// <summary>
    /// The discriminator stays narrow: only a pattern that both opens <c>e/</c> and
    /// closes <c>/</c> is the evaluated form. Genuine patterns that merely start
    /// with those characters, or that use the normal delimiter styles, are
    /// unaffected — a regression here would silently eat ordinary rules, which is
    /// the failure mode this change exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(@"#trigger {e/mail has arrived} {#echo mail}", "e/mail has arrived")] // opens e/ but never closes
    [InlineData(@"#trigger {/^You bow/} {#echo bow}",          "^You bow")]           // ordinary /…/
    [InlineData(@"#trigger {^escape velocity} {#echo go}",     "^escape velocity")]   // starts with 'e'
    // Slashes but no e/ prefix: the trailing one is stripped by the pre-existing
    // delimiter handling, which this change does not touch.
    [InlineData(@"#trigger {value/rate/} {#echo r}",           "value/rate")]
    public void Lookalike_patterns_are_still_imported(string line, string expectedPattern)
    {
        var path = WriteCfg(line);
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(expectedPattern, Assert.Single(engine.Triggers).Pattern);
    }

    [Fact]
    public void The_case_insensitive_delimiter_style_still_works()
    {
        var path = WriteCfg(@"#trigger {/^you Bow/i} {#echo bow}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(1, result.Imported);
        var t = Assert.Single(engine.Triggers);
        Assert.Equal("^you Bow", t.Pattern);
        Assert.False(t.CaseSensitive);
        Assert.True(t.IsMatch("You BOW deeply."));
    }


    // ── Door 2: a hand-copied triggers.cfg replayed through the command engine ──

    private readonly string _root;
    private readonly GenieConfig _config;

    public EvaluatedTriggerImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_evaltrig_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieEvalTrigTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (CommandEngine Engine, TriggerEngineFinal Triggers, FakeCommandHost Host) NewEngine()
    {
        var host    = new FakeCommandHost();
        var engine  = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        var trigs   = new TriggerEngineFinal();
        engine.Triggers = trigs;
        return (engine, trigs, host);
    }

    /// <summary>
    /// The importer is not the only door: a Genie 4 <c>triggers.cfg</c> copied
    /// straight into the config folder is replayed through the command engine at
    /// connect, exactly as the #highlight / #name argument-order handling exists
    /// for. That path used to bank <c>e/$var/</c> verbatim as a dead text rule.
    /// </summary>
    [Fact]
    public void A_replayed_Genie4_evaluated_line_is_refused_with_a_reason()
    {
        var (engine, trigs, host) = NewEngine();

        engine.ProcessInput(@"#trigger {e/$roomobjs/} {#var monsterdead 0}");

        Assert.Empty(trigs.Triggers);
        Assert.Contains(host.Echoes, e => e.Contains("Evaluated trigger not imported", StringComparison.Ordinal));
        Assert.Contains(host.Echoes, e => e.Contains("e/$roomobjs/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_explicit_add_form_is_refused_too()
    {
        var (engine, trigs, host) = NewEngine();

        engine.ProcessInput(@"#trigger add {e/$monstercount/} {#echo x}");

        Assert.Empty(trigs.Triggers);
        Assert.Contains(host.Echoes, e => e.Contains("Evaluated trigger not imported", StringComparison.Ordinal));
    }

    /// <summary>Genie 5's own dialect pays nothing for the discriminator.</summary>
    [Fact]
    public void An_ordinary_replayed_trigger_still_lands()
    {
        var (engine, trigs, _) = NewEngine();

        engine.ProcessInput(@"#trigger add {^You feel fully rested} {#echo rested}");

        var t = Assert.Single(trigs.Triggers);
        Assert.Equal("^You feel fully rested", t.Pattern);
    }

    /// <summary>
    /// <c>e/</c> alone is degenerate: its single slash closes the marker it opens.
    /// The old strip computed the reversed range <c>pat[2..^1]</c> on it and threw.
    /// It must be reported — NOT handed to the delimiter path, which would strip the
    /// trailing slash and import the pattern <c>e</c>, a trigger firing on almost
    /// every line of game text.
    /// </summary>
    [Fact]
    public void A_degenerate_marker_does_not_throw()
    {
        var path = WriteCfg(@"#trigger {e/} {#echo x}");
        var engine = new TriggerEngineFinal();

        var result = Genie4Importer.ImportTriggers(path, engine, ImportMode.Replace);
        File.Delete(path);

        Assert.Equal(0, result.Imported);
        Assert.Single(result.Skips);
        Assert.Equal(ImportSkipKind.Dropped, result.Skips[0].Kind);
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
