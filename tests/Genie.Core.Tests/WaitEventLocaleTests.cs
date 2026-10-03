using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#wait N</c> / <c>#event N</c> parse their delay with the invariant
/// culture, like <c>#send</c>'s ParseSendDelay. On a comma-decimal locale
/// (de-DE) the old culture-default TryParse read "0.5" with '.' as the group
/// separator — 5 seconds instead of half a second.
/// </summary>
public class WaitEventLocaleTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;
    private readonly CultureInfo _savedCulture = CultureInfo.CurrentCulture;

    public WaitEventLocaleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_waitlocale_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieWaitLocaleTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _savedCulture;
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Wait_delay_parses_invariant_on_a_comma_decimal_locale()
    {
        var queue  = new CommandQueue();
        var engine = new CommandEngine(_config, queue, new EventQueue(), new FakeCommandHost());

        engine.ProcessInput("#wait 0.5 look");

        var item = Assert.Single(queue.EventList);
        Assert.Equal(0.5, item.Delay);
        Assert.Equal("look", item.Action);
    }

    [Fact]
    public void Event_delay_parses_invariant_on_a_comma_decimal_locale()
    {
        var events = new EventQueue();
        var engine = new CommandEngine(_config, new CommandQueue(), events, new FakeCommandHost());

        engine.ProcessInput("#event 0.1 look");

        // 0.1 s, not the 1 s that de-DE's "0.1" → 1 would give.
        Thread.Sleep(400);
        Assert.Equal("look", events.Poll());
    }

    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> GameCommands { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();

        public void ClearSendQueue() { }

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => GameCommands.Add(text);

        public void Echo(string text) { }
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
        public void MapperGoto(string args) { }
        public void MapperReset() { }
        public void MapperCommand(string args) { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
