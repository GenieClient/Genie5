using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #151 — small Genie 4 parity gaps: <c>#ignore</c> (synonym for <c>#gag</c>),
/// <c>ignorescriptwarnings</c> config, and reserved script variables
/// <c>$year</c> / <c>$month</c> / <c>$spellstarttime</c>. (<c>#tvar save/load</c>
/// already existed; <c>$spellpreptime</c> is deferred — Genie 5 doesn't capture a
/// spell prep duration.)
/// </summary>
public class GrabBagParityTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public GrabBagParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_grabbag_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieGrabBagTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── Script-var + warning harness ─────────────────────────────────────────
    private static List<string> RunScript(string body, Action<ScriptEngine>? configure = null)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_grabbag_run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(), _ => { }, l => echoed.Add(l));
            configure?.Invoke(engine);
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Year_variable_resolves_the_current_year()
    {
        var o = RunScript("echo $year\n");
        Assert.Contains(DateTime.Now.ToString("yyyy"), o);
    }

    [Fact]
    public void Month_variable_resolves_the_two_digit_month_not_minutes()
    {
        // Genie 4's Globals used "mm" (minutes); we use "MM" so $month is the month.
        var o = RunScript("echo $month\n");
        Assert.Contains(DateTime.Now.ToString("MM"), o);
    }

    [Fact]
    public void SpellStartTime_variable_resolves_the_host_epoch()
    {
        var o = RunScript("echo $spellstarttime\n", e => e.SpellStartTimeEpoch = () => 1700000000L);
        Assert.Contains("1700000000", o);
    }

    [Fact]
    public void SpellStartTime_defaults_to_zero_when_no_spell()
    {
        var o = RunScript("echo $spellstarttime\n");   // no delegate → 0
        Assert.Contains(o, l => l.Trim() == "0");
    }

    [Fact]
    public void IgnoreScriptWarnings_suppresses_the_condition_warnings()
    {
        // Unbalanced parens — auto-balanced (G4 compat) to a true comparison, so
        // the then-branch runs either way; only the advisory is gated by #151.
        // (%a/%b are DEFINED so this fixture tests warning gating alone — an
        // undefined var now stays literal per G4, which would compare false.)
        const string body = "var a x\nvar b x\nif ((\"%a\" = \"%b\") then echo M\nelse echo N\n";

        var suppressed = RunScript(body, e => e.WarningsSuppressed = () => true);
        Assert.DoesNotContain(suppressed, l => l.Contains("unbalanced parentheses"));
        Assert.Contains("M", suppressed);   // leniency applies even when quiet

        var shown = RunScript(body);        // default — warnings visible
        Assert.Contains(shown, l => l.Contains("auto-balanced"));
    }

    // ── #ignore command + config ─────────────────────────────────────────────
    private (FakeCommandHost host, CommandEngine engine, GagEngine gags) MakeCmd()
    {
        var host = new FakeCommandHost();
        var gags = new GagEngine();
        var eng  = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host) { Gags = gags };
        return (host, eng, gags);
    }

    [Fact]
    public void Ignore_adds_a_gag()
    {
        var (_, eng, gags) = MakeCmd();
        eng.ProcessInput("#ignore goblin");
        Assert.Contains(gags.Rules, r => r.Pattern == "goblin");
    }

    [Fact]
    public void Unignore_removes_a_gag()
    {
        var (_, eng, gags) = MakeCmd();
        gags.AddRule("goblin");
        eng.ProcessInput("#unignore goblin");
        Assert.DoesNotContain(gags.Rules, r => r.Pattern == "goblin");
    }

    [Fact]
    public void IgnoreScriptWarnings_config_round_trips()
    {
        Assert.False(_config.IgnoreScriptWarnings);           // default off
        _config.SetSetting("ignorescriptwarnings", "True", showException: false);
        Assert.True(_config.IgnoreScriptWarnings);
        _config.SetSetting("ignorescriptwarnings", "False", showException: false);
        Assert.False(_config.IgnoreScriptWarnings);
    }

    private sealed class FakeCommandHost : ICommandHost
    {
        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void Echo(string text) { }
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
