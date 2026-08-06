using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Genie.Core.Triggers;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Host-level <c>$variable</c> expansion for the command pipeline
/// (<see cref="ScriptEngine.ExpandGlobalVars"/>, backing
/// <c>ICommandHost.ExpandVariables</c> → <c>CommandEngine.ProcessInput</c>).
/// The old hand-rolled expander in GenieCore parsed one greedy identifier and
/// never shrank, so the classic rank-log trigger action
/// <c>#log &gt;Ranklog-$charactername.txt [$date $time] $1</c> looked up the
/// undefined name "charactername.txt", left the token literal, and created a
/// file literally named "Ranklog-$charactername.txt" (found in Jason's live
/// Logs dir). Window-name args (<c>#echo &gt;$win</c>) failed the same way.
/// Expansion now shares the script engine's Genie 4-parity resolution:
/// shrink-search with the word-boundary rule, clock vars, and
/// undefined-stays-literal.
/// </summary>
public class HostVarExpansionTests : IDisposable
{
    private readonly string       _root;
    private readonly ScriptEngine _engine;

    public HostVarExpansionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_hostvar_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _engine = new ScriptEngine(Path.Combine(_root, "scripts"), new TypeAheadSession(),
                                   sendCommand: _ => { }, echo: _ => { });
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Dotted_filename_arg_shrinks_to_the_defined_var()
    {
        // The exact failure shape: greedy scan sees "charactername.txt"
        // (undefined); the shrink-search must fall back to $charactername +
        // literal ".txt" ('.' is a sanctioned suffix boundary).
        _engine.Globals["charactername"] = "Renucci";
        var s = _engine.ExpandGlobalVars("#log >Ranklog-$charactername.txt gained a rank");
        Assert.Equal("#log >Ranklog-Renucci.txt gained a rank", s);
    }

    [Fact]
    public void Clock_vars_resolve_at_the_command_level()
    {
        // $date / $time are computed reserved vars (TryClockVar) — the old host
        // expander knew nothing about them, so the rank-log line's timestamp
        // was written literally as "[$date $time]".
        var s = _engine.ExpandGlobalVars("[$date $time]");
        Assert.DoesNotContain("$date", s);
        Assert.DoesNotContain("$time", s);
        Assert.Matches(@"^\[\d{1,2}/\d{1,2}/\d{4} \d{2}:\d{2}:\d{2} (AM|PM)\]$", s);
    }

    [Fact]
    public void Defined_window_var_substitutes_in_echo_target()
    {
        // #echo >$win — Genie 4 substitutes window-name args; a defined var
        // must resolve so the echo reaches the intended window.
        _engine.Globals["win"] = "Log";
        Assert.Equal("#echo >Log hello", _engine.ExpandGlobalVars("#echo >$win hello"));
    }

    [Fact]
    public void Undefined_var_stays_literal_for_the_phantom_window_guard()
    {
        // uber.cmd's `#echo >$Log …` ($Log never defined): Genie 4 keeps the
        // token literal; downstream GenieCore.IsUnresolvedVarWindow routes it
        // to Main with a warning. Substituting empty here would silently merge
        // the color token into the window slot instead.
        var s = _engine.ExpandGlobalVars("#echo >$Log Crimson MISSING MATCH");
        Assert.Equal("#echo >$Log Crimson MISSING MATCH", s);
    }

    [Fact]
    public void Numeric_slots_stay_literal_with_no_frame()
    {
        // Trigger capture slots are expanded by TriggerEngineFinal BEFORE
        // ProcessInput; a $3 surviving past that (pattern had fewer captures)
        // must stay literal at the host level, not be eaten as an empty slot.
        Assert.Equal("#echo saw $3", _engine.ExpandGlobalVars("#echo saw $3"));
    }

    [Fact]
    public void Percent_locals_are_untouched_at_the_host_level()
    {
        // %vars are script-local; command-level expansion (G4 ParseGlobalVars)
        // never touches them.
        Assert.Equal("#echo %count done", _engine.ExpandGlobalVars("#echo %count done"));
    }

    [Fact]
    public void Rank_trigger_end_to_end_writes_the_substituted_filename()
    {
        // The live repro: the rank-gain trigger's #log must create
        // Ranklog-Renucci.txt — not a file literally named with the $var.
        _engine.Globals["charactername"] = "Renucci";
        var lds = new LocalDirectoryService("GenieHostVarTest", _root);
        lds.UseExplicitRoot(_root);
        var config = new GenieConfig(lds);

        var host     = new ExpandingHost(_engine);
        var commands = new CommandEngine(config, new CommandQueue(), new EventQueue(), host);
        var triggers = new TriggerEngineFinal(host, commands);
        triggers.AddTrigger(@"^You've gained a new rank in (.*)\.$",
                            "#log >Ranklog-$charactername.txt [$date $time] $1");

        triggers.ProcessLine("You've gained a new rank in Athletics.");

        var expected = Path.Combine(config.LogDir, "Ranklog-Renucci.txt");
        Assert.True(File.Exists(expected), $"expected log file at {expected}");
        var text = File.ReadAllText(expected);
        Assert.Contains("Athletics", text);
        Assert.DoesNotContain("$date", text);
        Assert.DoesNotContain("$charactername", text);
        Assert.False(File.Exists(Path.Combine(config.LogDir, "Ranklog-$charactername.txt")),
                     "the literal-$var filename must not come back");
    }

    /// <summary>Minimal <see cref="ICommandHost"/> whose
    /// <see cref="ExpandVariables"/> delegates to the real engine — the same
    /// wiring GenieCore uses — so ProcessInput exercises the fixed path.</summary>
    private sealed class ExpandingHost : ICommandHost
    {
        private readonly ScriptEngine _engine;
        public ExpandingHost(ScriptEngine engine) { _engine = engine; }

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => _engine.Globals;
        public string ExpandVariables(string text) => _engine.ExpandGlobalVars(text);

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
        public void SetGlobalVariable(string name, string value) => _engine.Globals[name] = value;
        public void RemoveGlobalVariable(string name) => _engine.Globals.TryRemove(name, out _);
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
