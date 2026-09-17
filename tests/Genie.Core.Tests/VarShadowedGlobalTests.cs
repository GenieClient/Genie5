using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #340 (Azothy) — <c>put #var preparedSymbiosis 0</c> stored the 0 but
/// left <c>$preparedSymbiosis</c> expanding to the 1 a <c>#tvar</c> had planted,
/// so <c>if (!$preparedSymbiosis)</c> read <c>if (!1)</c> forever; a brand-new
/// name in the same script worked.
///
/// Root cause: Genie 5 splits Genie 4's single variable list into the live
/// session globals and the persisted <c>#var</c> store, and the read path
/// resolves globals FIRST (<c>ScriptEngine.TryResolveVar</c>), so a store-only
/// write is silently inert for any name a global already holds. #294
/// ($connected) and #226 ($roundtime) each patched one name; the fix here
/// mirrors every <c>#var</c> write — and every <c>#unvar</c> / <c>#var
/// remove</c> — onto a shadowing global, restoring the one-list behavior.
/// </summary>
public class VarShadowedGlobalTests : IDisposable
{
    private readonly string      _root;
    private readonly GenieConfig _config;

    public VarShadowedGlobalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_varshadow_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var lds = new LocalDirectoryService("GenieVarShadowTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (CommandEngine Engine, VariableEngine Vars, FakeCommandHost Host) NewEngine(
        IDictionary<string, string>? globals = null)
    {
        var host   = new FakeCommandHost(globals);
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        var vars   = new VariableEngine(engine);
        engine.Variables = vars;
        return (engine, vars, host);
    }

    // -- The reported bug ---------------------------------------------------

    [Fact]
    public void Var_overwrites_a_shadowing_live_global_and_the_store()
    {
        var (engine, vars, host) = NewEngine();
        host.Globals["preparedSymbiosis"] = "1";   // planted by an earlier #tvar

        engine.ProcessInput("#var preparedSymbiosis 0");

        Assert.Equal("0", host.Globals["preparedSymbiosis"]);
        Assert.Equal("0", vars.Store.Get("preparedSymbiosis"));
    }

    [Fact]
    public void Var_set_form_overwrites_the_shadowing_global_too()
    {
        var (engine, vars, host) = NewEngine();
        host.Globals["preparedSymbiosis"] = "1";

        engine.ProcessInput("#var set preparedSymbiosis 0");

        Assert.Equal("0", host.Globals["preparedSymbiosis"]);
        Assert.Equal("0", vars.Store.Get("preparedSymbiosis"));
    }

    [Fact]
    public void Var_matches_a_shadowing_global_case_insensitively()
    {
        // Globals is an OrdinalIgnoreCase dictionary and so is the read path --
        // the mirror has to use the same comparison or a differently-cased
        // #var would leave the shadow in place.
        var (engine, _, host) = NewEngine();
        host.Globals["PreparedSymbiosis"] = "1";

        engine.ProcessInput("#var preparedsymbiosis 0");

        Assert.Equal("0", host.Globals["PreparedSymbiosis"]);
        Assert.Single(host.Globals);
    }

    [Fact]
    public void Var_on_an_unshadowed_name_still_touches_only_the_store()
    {
        // The mirror is strictly "keep an EXISTING global honest" -- #var must
        // not start planting session globals of its own (that would make every
        // persisted variable outrank the store it lives in).
        var (engine, vars, host) = NewEngine();

        engine.ProcessInput("#var TestVar 0");

        Assert.Equal("0", vars.Store.Get("TestVar"));
        Assert.False(host.Globals.ContainsKey("TestVar"));
    }

    // -- Removal symmetry ---------------------------------------------------

    [Fact]
    public void Unvar_clears_a_shadowing_global_too()
    {
        var (engine, vars, host) = NewEngine();
        host.Globals["preparedSymbiosis"] = "1";
        engine.ProcessInput("#var preparedSymbiosis 0");

        engine.ProcessInput("#unvar preparedSymbiosis");

        Assert.Null(vars.Store.Get("preparedSymbiosis"));
        Assert.False(host.Globals.ContainsKey("preparedSymbiosis"));
    }

    [Fact]
    public void Var_remove_form_clears_a_shadowing_global_too()
    {
        var (engine, vars, host) = NewEngine();
        host.Globals["hunt"] = "rats";
        engine.ProcessInput("#var hunt bandits");

        engine.ProcessInput("#var remove hunt");

        Assert.Null(vars.Store.Get("hunt"));
        Assert.False(host.Globals.ContainsKey("hunt"));
    }

    [Fact]
    public void Unvar_leaves_the_reserved_connection_global_alone()
    {
        // #294 owns $connected: dropping it mid-session would leave a polling
        // reconnect script reading the literal "$connected".
        var (engine, _, host) = NewEngine();
        host.Globals["connected"] = "1";

        engine.ProcessInput("#unvar connected");

        Assert.Equal("1", host.Globals["connected"]);
    }

    // -- End to end: the script read path -----------------------------------

    [Fact]
    public void Var_then_dollar_expansion_reads_the_new_value()
    {
        // Azothy's repro across both engines: #tvar plants the global, #var
        // overwrites it, and the $ read (which resolves globals before the
        // store) must see 0 so `if (!$preparedSymbiosis)` fires.
        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        var scripts = new ScriptEngine(dir, new TypeAheadSession(),
                                       sendCommand: _ => { }, echo: _ => { });
        var (engine, vars, _) = NewEngine(scripts.Globals);
        scripts.UserVarLookup = n => vars.Store.Get(n);

        engine.ProcessInput("#tvar preparedSymbiosis 1");
        Assert.Equal("1", scripts.ExpandGlobalVars("$preparedSymbiosis"));

        engine.ProcessInput("#var preparedSymbiosis 0");

        Assert.Equal("0", scripts.ExpandGlobalVars("$preparedSymbiosis"));
    }

    /// <summary>ICommandHost double: records Echo lines; the globals dictionary
    /// is injectable so a test can hand over a real ScriptEngine.Globals.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public FakeCommandHost(IDictionary<string, string>? globals = null)
            => Globals = globals ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Echoed { get; } = new();
        public IDictionary<string, string> Globals { get; }

        public IReadOnlyDictionary<string, string> GetGlobalVariables()
            => new Dictionary<string, string>(Globals, StringComparer.OrdinalIgnoreCase);
        public string ExpandVariables(string text) => text;

        public void Echo(string text) => Echoed.Add(text);
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
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
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
