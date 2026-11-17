using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Presets;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 parity for <c>#preset</c> / <c>#presets</c> (#149): list / filtered
/// list, <c>#preset {id} {fg} [{bg}]</c> to override a KNOWN token's colour
/// (unknown ids rejected — the palette is a fixed vocabulary), plus save / load
/// / reset. Colour rendering itself is #19.
/// </summary>
public class PresetCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;
    private readonly PresetEngine _presets = new();

    public PresetCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_preset_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GeniePresetTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (FakeCommandHost host, CommandEngine engine) Make()
    {
        var host   = new FakeCommandHost();
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host) { Presets = _presets };
        return (host, engine);
    }

    [Fact]
    public void Set_overrides_the_foreground_of_a_known_token()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#preset roomdesc Red");

        Assert.Equal("Red", _presets.GetForeground("roomdesc"));
        Assert.Contains(host.Echoes, e => e.Contains("Preset roomdesc"));
    }

    [Fact]
    public void Set_with_only_foreground_preserves_the_existing_background()
    {
        var (_, engine) = Make();
        // automapper.line defaults to Black / White.
        engine.ProcessInput("#preset automapper.line Orange");

        Assert.Equal("Orange", _presets.GetForeground("automapper.line"));
        Assert.Equal("White",  _presets.GetBackground("automapper.line"));
    }

    [Fact]
    public void Set_with_background_overrides_both()
    {
        var (_, engine) = Make();
        engine.ProcessInput("#preset speech Lime #001100");

        Assert.Equal("Lime",    _presets.GetForeground("speech"));
        Assert.Equal("#001100", _presets.GetBackground("speech"));
    }

    [Fact]
    public void Set_is_case_insensitive_on_the_token_id()
    {
        var (_, engine) = Make();
        engine.ProcessInput("#preset ROOMDESC Blue");

        Assert.Equal("Blue", _presets.GetForeground("roomdesc"));
    }

    [Fact]
    public void Set_preserves_the_highlight_line_flag()
    {
        var (_, engine) = Make();
        _presets.Apply(new PresetRule { Id = "speech", ForegroundColor = "Lime", HighlightLine = true });
        engine.ProcessInput("#preset speech Red");

        Assert.Equal("Red", _presets.GetForeground("speech"));
        Assert.True(_presets.GetHighlightLine("speech"));
    }

    [Fact]
    public void Unknown_token_is_rejected_and_not_created()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#preset bogustoken Red");

        Assert.Null(_presets.Get("bogustoken"));
        Assert.Contains(host.Echoes, e => e.Contains("Invalid #preset keyword"));
    }

    [Fact]
    public void List_reports_presets_and_filters()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#preset");
        Assert.Contains(host.Echoes, e => e.Contains("roomdesc"));
        Assert.Contains(host.Echoes, e => e.Contains("speech"));

        host.Echoes.Clear();
        engine.ProcessInput("#preset roomdesc");   // lone known token → filtered list, not a set
        Assert.Contains(host.Echoes, e => e.Contains("roomdesc"));
        Assert.DoesNotContain(host.Echoes, e => e.Contains("speech"));
    }

    [Fact]
    public void Reset_restores_defaults()
    {
        var (_, engine) = Make();
        engine.ProcessInput("#preset roomdesc Red");
        Assert.Equal("Red", _presets.GetForeground("roomdesc"));

        engine.ProcessInput("#preset reset");
        Assert.Equal("Silver", _presets.GetForeground("roomdesc"));
    }

    [Fact]
    public void Save_then_load_round_trips_through_presets_cfg()
    {
        var (_, engine) = Make();
        engine.ProcessInput("#preset roomdesc Red");
        engine.ProcessInput("#preset save");

        engine.ProcessInput("#preset reset");
        Assert.Equal("Silver", _presets.GetForeground("roomdesc"));

        engine.ProcessInput("#preset load");
        Assert.Equal("Red", _presets.GetForeground("roomdesc"));
    }

    /// <summary>Minimal <see cref="ICommandHost"/> double that records echoes.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> Echoes { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;

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
