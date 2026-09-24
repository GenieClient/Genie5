using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #360 — a bare <c>#echo</c> echoes a blank line (Genie 4
/// Core/Command.cs:273: <c>ParseAllArgs</c> over a one-element array is "",
/// echoed with a NewLine). The typed/command-engine path guarded on
/// <c>parts.Count &gt; 1</c> and printed nothing, which collapsed the vertical
/// spacing of banner and ASCII-art scripts. The script engine's own
/// <c>put #echo</c> path already got this right; both are pinned here.
/// </summary>
public class BareEchoTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public BareEchoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_bareecho_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieBareEchoTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private FakeHost Run(string input)
    {
        var host = new FakeHost();
        new CommandEngine(_config, new CommandQueue(), new EventQueue(), host).ProcessInput(input);
        return host;
    }

    [Fact]
    public void Bare_echo_prints_a_blank_line()
    {
        var host = Run("#echo");

        Assert.Equal(new[] { "" }, host.Echoes);
    }

    [Fact]
    public void Bare_echo_with_trailing_space_prints_a_blank_line()
    {
        var host = Run("#echo ");

        Assert.Equal(new[] { "" }, host.Echoes);
    }

    [Fact]
    public void Quoted_empty_echo_still_prints_a_blank_line()
    {
        var host = Run("#echo \"\"");

        Assert.Equal(new[] { "" }, host.Echoes);
    }

    [Fact]
    public void Echo_with_text_is_unchanged()
    {
        var host = Run("#echo hello there");

        Assert.Equal(new[] { "hello there" }, host.Echoes);
    }

    [Fact]
    public void Window_only_echo_prints_a_blank_line_in_that_window()
    {
        var host = Run("#echo >Log");

        Assert.Empty(host.Echoes);
        Assert.Equal(("", "Log"), Assert.Single(host.EchoToCalls));
    }

    [Fact]
    public void Script_bare_echo_prints_a_blank_line()
    {
        var echoes = new List<string>();
        var dir = Path.Combine(_root, "scripts");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "t.cmd"), "put #echo\nput #echo after\n");
        var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: echoes.Add);
        engine.TryStart("t", new List<string>());
        for (int i = 0; i < 200; i++) engine.Tick();

        var blank = echoes.IndexOf("");
        Assert.True(blank >= 0, "bare #echo produced no blank line");
        Assert.True(echoes.IndexOf("after") > blank);
    }

    private sealed class FakeHost : ICommandHost
    {
        public List<string> Echoes { get; } = new();
        public List<(string Text, string? Window)> EchoToCalls { get; } = new();

        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void Echo(string text) => Echoes.Add(text);
        public void EchoTo(string text, string? window, string? color) => EchoToCalls.Add((text, window));
        public void EchoMain(string text, string? color, bool mono) => Echoes.Add(text);
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void SetWindowComment(string window, string comment) { }
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
