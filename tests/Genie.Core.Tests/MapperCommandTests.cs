using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #146 — the `#mapper` command routes `reset` to the shared-engine re-resolve
/// (Genie 3/4 #75) and every other subcommand to the App mapper host
/// (save / load / clear / zone / color / allowdupes / record). A stray subcommand
/// never reaches the game; it forwards to the host, which echoes usage.
/// </summary>
public class MapperCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public MapperCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_mapper_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieMapperTest", _root);
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
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        return (host, engine);
    }

    [Fact]
    public void Reset_calls_MapperReset_not_MapperCommand()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#mapper reset");

        Assert.Equal(1, host.ResetCalls);
        Assert.Null(host.LastMapperCommand);
        Assert.DoesNotContain(host.GameCommands, c => c.Contains("mapper"));   // never reaches the game
    }

    [Theory]
    [InlineData("#mapper save", "save")]
    [InlineData("#mapper load", "load")]
    [InlineData("#mapper clear", "clear")]
    [InlineData("#mapper allowdupes on", "allowdupes on")]
    [InlineData("#mapper record off", "record off")]
    [InlineData("#mapper zone Crossing", "zone Crossing")]
    [InlineData("#mapper color bg #202020", "color bg #202020")]
    public void Subcommands_forward_the_args_to_the_host(string input, string expected)
    {
        var (host, engine) = Make();
        engine.ProcessInput(input);

        Assert.Equal(expected, host.LastMapperCommand);
        Assert.Equal(0, host.ResetCalls);
        Assert.Empty(host.GameCommands);   // nothing leaks to the game
    }

    [Fact]
    public void Bare_mapper_forwards_empty_args_for_usage()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#mapper");

        Assert.Equal("", host.LastMapperCommand);   // App echoes usage
        Assert.Empty(host.GameCommands);
    }

    [Fact]
    public void Unknown_subcommand_forwards_rather_than_reaching_the_game()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#mapper wibble");

        Assert.Equal("wibble", host.LastMapperCommand);
        Assert.Empty(host.GameCommands);
    }

    // ── #401: with the mapper off (#config automapper false), Genie 4 parity ──

    [Theory]
    [InlineData("#mapper reset")]
    [InlineData("#mapper record on")]
    [InlineData("#goto 42")]
    [InlineData("#go2 bank")]
    public void Mapper_off_answers_disabled_and_does_nothing(string input)
    {
        _config.SetSetting("automapper", "False");
        var (host, engine) = Make();
        engine.ProcessInput(input);

        Assert.Equal(0, host.ResetCalls);
        Assert.Null(host.LastMapperCommand);
        Assert.Null(host.LastGoto);
        Assert.Empty(host.GameCommands);
        Assert.Contains(Genie.Core.Mapper.AutoMapperEngine.DisabledMessage, host.Echoes);
        Assert.False(_config.AutoMapperRecord);
    }

    [Fact]
    public void Mapper_on_by_default_goto_reaches_the_host()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#goto 42");
        Assert.Equal("42", host.LastGoto);
    }

    private sealed class FakeCommandHost : ICommandHost
    {
        public int     ResetCalls        { get; private set; }
        public string? LastMapperCommand { get; private set; }
        public List<string> GameCommands { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void MapperReset() => ResetCalls++;
        public void MapperCommand(string args) => LastMapperCommand = args;
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => GameCommands.Add(text);

        public List<string> Echoes { get; } = new();
        public void Echo(string text) => Echoes.Add(text);
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
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
        public string? LastGoto { get; private set; }
        public void MapperGoto(string args) => LastGoto = args;
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
