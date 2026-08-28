using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Events;
using Genie.Core.Import;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #294 — a stale <c>connected=1</c> row in the persisted #var store
/// (planted by a Genie 4 import: G4's <c>VariableList.Add</c> flips the
/// reserved var to SaveToFile on any <c>#var connected …</c>, after which it
/// lands in variables.cfg) made the Configuration ▸ Variables panel show
/// "connected 1" forever and shadowed <c>$connected</c> for scripts started
/// before the session's first connect. The fix: reserved connection-state
/// names never enter <see cref="VariableStore"/> (one choke point in
/// <c>Set</c> covers every loader), a typed <c>#var connected …</c> routes to
/// the live globals (Genie 4 one-list parity), and <c>$connected</c> is
/// seeded "0" — at app launch (GenieCore ctor, G4 Globals.cs:882 parity) and
/// per connect (<see cref="ScriptGlobalsSync"/>, so a polling reconnect
/// script can't read a false "1" during the dial window; the Connected event
/// writes the real "1").
/// </summary>
public class ReservedConnectionVarTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public ReservedConnectionVarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_resconn_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var lds = new LocalDirectoryService("GenieResConnTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── VariableStore choke point ──────────────────────────────────────────

    [Theory]
    [InlineData("connected")]
    [InlineData("Connected")]
    [InlineData("CONNECTED")]
    public void Store_refuses_reserved_connection_names(string name)
    {
        var store = new VariableStore();

        Assert.False(store.Set(name, "1"));
        Assert.Null(store.Get(name));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Store_still_accepts_normal_names()
    {
        var store = new VariableStore();

        Assert.True(store.Set("hunt", "rats"));
        Assert.Equal("rats", store.Get("hunt"));
    }

    [Fact]
    public void Loader_shaped_set_loop_drops_only_the_reserved_row()
    {
        // The App's variables.json load and #var load both foreach models →
        // Store.Set; a carried-in connected row is dropped, the rest kept.
        var store = new VariableStore();
        var rows  = new (string Name, string Value)[]
        {
            ("connected", "1"),
            ("hunt",      "rats"),
            ("RP",        "OFF"),
        };
        foreach (var (n, v) in rows) store.Set(n, v);

        Assert.Null(store.Get("connected"));
        Assert.Equal("rats", store.Get("hunt"));
        Assert.Equal("OFF",  store.Get("RP"));
        Assert.Equal(2, store.GetAll().Count);
    }

    // ── #var command routing (Genie 4 one-list parity) ─────────────────────

    private (CommandEngine Engine, VariableEngine Vars, FakeCommandHost Host) NewEngine()
    {
        var host   = new FakeCommandHost();
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        var vars   = new VariableEngine(engine);
        engine.Variables = vars;
        return (engine, vars, host);
    }

    [Fact]
    public void Var_connected_routes_to_live_globals_not_the_store()
    {
        var (engine, vars, host) = NewEngine();

        engine.ProcessInput("#var connected 0");

        Assert.Equal("0", host.Globals["connected"]);
        Assert.Null(vars.Store.Get("connected"));
    }

    [Fact]
    public void Var_set_form_routes_connected_too()
    {
        var (engine, vars, host) = NewEngine();

        engine.ProcessInput("#var set connected 1");

        Assert.Equal("1", host.Globals["connected"]);
        Assert.Null(vars.Store.Get("connected"));
    }

    [Fact]
    public void Var_normal_name_still_goes_to_the_store()
    {
        var (engine, vars, host) = NewEngine();

        engine.ProcessInput("#var hunt rats");

        Assert.Equal("rats", vars.Store.Get("hunt"));
        Assert.False(host.Globals.ContainsKey("hunt"));
    }

    // ── Genie 4 importer ───────────────────────────────────────────────────

    [Fact]
    public void Importer_skips_a_connected_row_and_counts_it_skipped()
    {
        var path = Path.Combine(_root, "variables.cfg");
        File.WriteAllLines(path, new[]
        {
            "#var {connected} {1}",
            "#var {hunt} {rats}",
        });
        var store = new VariableStore();

        var result = Genie4Importer.ImportVariables(path, store, ImportMode.Replace);

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Null(store.Get("connected"));
        Assert.Equal("rats", store.Get("hunt"));
    }

    // ── Per-connect seed ───────────────────────────────────────────────────

    private sealed class Feed : IObservable<GameEvent>
    {
        private readonly List<IObserver<GameEvent>> _subs = new();
        public IDisposable Subscribe(IObserver<GameEvent> observer)
        {
            _subs.Add(observer);
            return new Unsub(() => _subs.Remove(observer));
        }
        private sealed class Unsub : IDisposable
        {
            private readonly Action _a;
            public Unsub(Action a) => _a = a;
            public void Dispose() => _a();
        }
    }

    [Fact]
    public void SeedInitial_seeds_connected_zero_until_the_connected_event()
    {
        // SeedInitial runs at BuildConnection, BEFORE the dial — G4 sets the
        // flag only on the socket's EventConnected, so the seed must be "0"
        // (GenieCore's _connectedVarSub writes the "1" on Connected).
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _ = new ScriptGlobalsSync(new Genie.Core.Models.GameState(), globals, new Feed());

        Assert.Equal("0", globals["connected"]);
    }

    /// <summary>ICommandHost double: records Echo lines + globals.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> Echoed { get; } = new();
        public Dictionary<string, string> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
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
