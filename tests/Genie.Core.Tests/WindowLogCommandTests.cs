using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.WindowLogging;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #270 — the <c>#windowlog</c> command surface: rules are added,
/// toggled, re-stamped and removed, every change lands in the profile's
/// <c>windowlog.json</c>, a filename template survives with its <c>{…}</c>
/// tokens intact (the ordinary argument parser strips braces), and a template
/// that would leave the Logs folder is refused before it is stored.
/// </summary>
public class WindowLogCommandTests : IDisposable
{
    private readonly string      _root;
    private readonly GenieConfig _config;

    public WindowLogCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_winlogcmd_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var lds = new LocalDirectoryService("GenieWinLogCmdTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (CommandEngine Engine, FakeHost Host, WindowLogSink Sink) NewEngine()
    {
        var host = new FakeHost();
        host.Globals["charactername"] = "Renucci";
        host.Globals["game"] = "DR";
        var sink = new WindowLogSink(new WindowLogRules(), () => _config.LogDir, startTimer: false);
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host)
        {
            WindowLogs = sink,
        };
        return (engine, host, sink);
    }

    private string SavedPath => Path.Combine(_config.ConfigProfileDir, WindowLogRules.FileName);

    [Fact]
    public void Add_keeps_the_template_tokens_and_saves()
    {
        var (engine, host, sink) = NewEngine();

        engine.ProcessInput(@"#windowlog add thoughts \GenieWindows\Thoughts\{charactername}\Thoughts-{charactername}-{yyyy}.txt");

        var rule = sink.Rules.TryGet("thoughts");
        Assert.NotNull(rule);
        Assert.Equal(@"\GenieWindows\Thoughts\{charactername}\Thoughts-{charactername}-{yyyy}.txt", rule!.File);
        Assert.True(rule.Enabled);
        Assert.Equal(WindowLogPath.DefaultTimestampFormat, rule.TimestampFormat);
        Assert.Contains(host.Echoed, l => l.Contains(Path.Combine("Renucci", $"Thoughts-Renucci-{DateTime.Now:yyyy}.txt")));

        var reloaded = new WindowLogRules();
        Assert.True(reloaded.Load(SavedPath));
        Assert.Equal(rule.File, reloaded.TryGet("thoughts")!.File);
    }

    [Fact]
    public void Add_accepts_a_quoted_template_with_spaces()
    {
        var (engine, _, sink) = NewEngine();
        engine.ProcessInput("#windowlog add talk \"My Logs\\{charactername} talk.txt\"");
        Assert.Equal(@"My Logs\{charactername} talk.txt", sink.Rules.TryGet("talk")!.File);
    }

    [Fact]
    public void Add_without_a_template_uses_the_default()
    {
        var (engine, _, sink) = NewEngine();
        engine.ProcessInput("#windowlog add logons");
        Assert.Equal(WindowLogPath.DefaultTemplate, sink.Rules.TryGet("logons")!.File);
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"C:\Windows\escape.txt")]
    public void Add_refuses_a_template_that_leaves_the_logs_folder(string template)
    {
        var (engine, host, sink) = NewEngine();
        engine.ProcessInput($"#windowlog add talk {template}");
        Assert.Null(sink.Rules.TryGet("talk"));
        Assert.Contains(host.Echoed, l => l.Contains("can't be used"));
    }

    [Fact]
    public void Add_warns_about_an_unknown_token_but_keeps_the_rule()
    {
        var (engine, host, sink) = NewEngine();
        engine.ProcessInput("#windowlog add talk {zone}-{yyyy}.txt");
        Assert.NotNull(sink.Rules.TryGet("talk"));
        Assert.Contains(host.Echoed, l => l.Contains("{zone}") && l.Contains("not a token"));
    }

    [Fact]
    public void On_off_timestamp_and_remove_edit_the_saved_rule()
    {
        var (engine, host, sink) = NewEngine();
        engine.ProcessInput("#windowlog add talk t.txt");

        engine.ProcessInput("#windowlog off talk");
        Assert.False(sink.Rules.TryGet("talk")!.Enabled);

        engine.ProcessInput("#windowlog timestamp talk HH:mm:ss");
        Assert.Equal("HH:mm:ss", sink.Rules.TryGet("talk")!.TimestampFormat);

        engine.ProcessInput("#windowlog timestamp talk none");
        Assert.Equal("", sink.Rules.TryGet("talk")!.TimestampFormat);

        var saved = new WindowLogRules();
        saved.Load(SavedPath);
        Assert.False(saved.TryGet("talk")!.Enabled);

        engine.ProcessInput("#windowlog remove talk");
        Assert.Null(sink.Rules.TryGet("talk"));
        saved.Load(SavedPath);
        Assert.Null(saved.TryGet("talk"));

        engine.ProcessInput("#windowlog off nosuch");
        Assert.Contains(host.Echoed, l => l.Contains("no rule for 'nosuch'"));
    }

    [Fact]
    public void Defaults_add_the_Genie4_set_without_overwriting_a_user_rule()
    {
        var (engine, _, sink) = NewEngine();
        engine.ProcessInput("#windowlog add talk mine.txt");

        engine.ProcessInput("#windowlog defaults");

        Assert.Equal("mine.txt", sink.Rules.TryGet("talk")!.File);
        foreach (var s in new[] { "thoughts", "whispers", "logons", "death" })
            Assert.NotNull(sink.Rules.TryGet(s));
    }

    [Fact]
    public void List_shows_rules_resolved_paths_and_the_streams_seen()
    {
        var (engine, host, sink) = NewEngine();
        engine.ProcessInput("#windowlog add talk t-{yyyy}.txt");
        sink.Observe("percWindow", "x", "Renucci", "DR");
        host.Echoed.Clear();

        engine.ProcessInput("#windowlog");

        Assert.Contains(host.Echoed, l => l.Contains("talk") && l.Contains("t-{yyyy}.txt"));
        Assert.Contains(host.Echoed, l => l.Contains($"t-{DateTime.Now:yyyy}.txt"));
        Assert.Contains(host.Echoed, l => l.StartsWith("Streams seen") && l.Contains("percWindow"));
    }

    [Fact]
    public void An_unknown_subcommand_prints_usage()
    {
        var (engine, host, _) = NewEngine();
        engine.ProcessInput("#windowlog frobnicate");
        Assert.Contains(host.Echoed, l => l.StartsWith("Usage: #windowlog"));
    }

    private sealed class FakeHost : ICommandHost
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
