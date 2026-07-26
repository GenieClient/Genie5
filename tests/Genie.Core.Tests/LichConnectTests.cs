using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 <c>#lc</c> parity: the two-letter <c>#lc</c> and <c>#lconnect</c>
/// aliases route to the same Lich connect path as <c>#lichconnect</c>
/// (<see cref="ConnectRequest.IsLich"/> = true), the password stays masked in
/// the 4-arg form, and the auto-launch config keys round-trip.
/// </summary>
public class LichConnectTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public LichConnectTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_lich_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieLichTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (FakeConnectHost host, CommandEngine engine) Make()
    {
        var host   = new FakeConnectHost();
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host);
        return (host, engine);
    }

    [Theory]
    [InlineData("lc")]
    [InlineData("lconnect")]
    [InlineData("lichconnect")]
    public void Lich_aliases_route_to_a_Lich_connect(string verb)
    {
        var (host, engine) = Make();
        engine.ProcessInput($"#{verb} MyProfile");

        Assert.NotNull(host.LastRequest);
        Assert.True(host.LastRequest!.IsLich);
        Assert.Equal(new[] { "MyProfile" }, host.LastRequest.Args.ToArray());
        Assert.Empty(host.GameCommands);   // never leaks to the game
    }

    [Fact]
    public void Plain_connect_is_not_a_Lich_connect()
    {
        var (host, engine) = Make();
        engine.ProcessInput("#connect MyProfile");

        Assert.NotNull(host.LastRequest);
        Assert.False(host.LastRequest!.IsLich);
    }

    [Theory]
    [InlineData("#lc acct secret Char DR")]
    [InlineData("#lconnect acct secret Char DR")]
    public void Password_is_masked_for_the_short_aliases(string line)
    {
        var masked = ConnectCommandMask.Mask(line);
        Assert.DoesNotContain("secret", masked);
        Assert.Contains(ConnectCommandMask.Masked, masked);
    }

    [Fact]
    public void AutoLaunch_keys_round_trip_through_config()
    {
        _config.SetSetting("lichautolaunch", "on", showException: false);
        _config.SetSetting("lichruby", @"C:\Ruby\bin\ruby.exe", showException: false);
        _config.SetSetting("lichpath", @"C:\lich\lich.rbw", showException: false);
        _config.SetSetting("lichargs", "--login Char --without-frontend", showException: false);
        _config.SetSetting("lichstartpause", "12", showException: false);
        _config.SetSetting("lichdebug", "on", showException: false);

        Assert.True(_config.LichAutoLaunch);
        Assert.Equal(@"C:\Ruby\bin\ruby.exe", _config.LichRubyPath);
        Assert.Equal(@"C:\lich\lich.rbw", _config.LichPath);
        Assert.Equal("--login Char --without-frontend", _config.LichArguments);
        Assert.Equal(12, _config.LichStartPause);
        Assert.True(_config.LichDebug);

        // Present in the persisted pair list so settings.cfg saves them.
        var keys = _config.ToConfigPairs().Select(p => p.Key).ToList();
        foreach (var k in new[] { "lichautolaunch", "lichruby", "lichpath", "lichargs", "lichstartpause", "lichdebug" })
            Assert.Contains(k, keys);

        Assert.Equal("12", _config.GetSetting("lichstartpause"));
        Assert.Equal("True", _config.GetSetting("lichdebug"));
    }

    [Theory]
    [InlineData("0", 1)]     // floored
    [InlineData("500", 120)] // capped
    [InlineData("8", 8)]
    public void LichStartPause_is_clamped(string input, int expected)
    {
        _config.SetSetting("lichstartpause", input, showException: false);
        Assert.Equal(expected, _config.LichStartPause);
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("lichsettings")]
    public void LichSettings_dumps_the_config_without_connecting(string verb)
    {
        _config.SetSetting("lichautolaunch", "on", showException: false);
        _config.SetSetting("lichpath", @"C:\lich\lich.rbw", showException: false);
        var (host, engine) = Make();

        engine.ProcessInput($"#{verb}");

        var dump = string.Join("\n", host.Echoes);
        Assert.Contains("Lich Settings", dump);
        Assert.Contains(@"C:\lich\lich.rbw", dump);
        Assert.Contains("Lich Debug:", dump);
        Assert.Null(host.LastRequest);     // it's a report, not a connect
        Assert.Empty(host.GameCommands);   // never reaches the game
    }

    [Fact]
    public void LichSettings_mentions_placeholders_when_lichargs_uses_them()
    {
        _config.SetSetting("lichargs", "--login {character} --headless {port}", showException: false);
        var (host, engine) = Make();

        engine.ProcessInput("#ls");

        var dump = string.Join("\n", host.Echoes);
        Assert.Contains("Placeholders:", dump);
        Assert.Contains("{character}", dump);
        Assert.Contains("{port}", dump);
    }

    private sealed class FakeConnectHost : ICommandHost
    {
        public ConnectRequest? LastRequest { get; private set; }
        public List<string> GameCommands { get; } = new();
        public List<string> Echoes { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();

        public void Connect(ConnectRequest request) => LastRequest = request;

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void MapperReset() { }
        public void MapperCommand(string args) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => GameCommands.Add(text);
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
        public void MapperGoto(string args) { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
    }
}
