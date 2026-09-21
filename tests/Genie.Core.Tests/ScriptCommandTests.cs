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
/// Genie 4 parity for the <c>#script</c> dispatcher (Command.cs:2188 + the
/// FormMain Command_Script* handlers): <c>abort</c> / <c>pause</c> /
/// <c>resume</c> / <c>pauseorresume</c> act on the named script (or all
/// scripts for "all" / no name) with an optional trailing
/// <c>except &lt;name&gt;</c>; <c>reload</c> / <c>trace</c> / <c>vars</c> /
/// <c>debug</c> / <c>explorer</c> route to their host members. It NEVER starts
/// a script — scripts start with <c>.name</c>. Before this matched Genie 4,
/// "#script abort foo" was handed to RunScript and tried to start a script
/// named "abort" (hit by mm_train's wait mode, which stops its watched script
/// with "#script abort $MM_WAIT_SCRIPT").
/// </summary>
public class ScriptCommandTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public ScriptCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_script_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieScriptTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private void Run(FakeCommandHost host, string input) =>
        new CommandEngine(_config, new CommandQueue(), new EventQueue(), host).ProcessInput(input);

    // -- #247: bulk control over a '|'-separated list ------------------------
    //
    // $scriptlistactive / $scriptlistpaused expand to "a|b|c" BEFORE the
    // command is parsed, so the dispatcher sees a list rather than a name.
    // That is what makes bulk script control scriptable over an arbitrary set,
    // including one assembled by hand in a #var.

    [Fact]
    public void Abort_over_a_list_stops_exactly_those_scripts()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors", "exptally" });
        Run(host, "#script abort hunt|exptally");

        Assert.False(host.StopAllCalled);
        Assert.Equal(new[] { "hunt", "exptally" }, host.Stopped);
    }

    [Fact]
    public void Pause_over_a_list_pauses_exactly_those_scripts()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors", "exptally" });
        Run(host, "#script pause hunt|favors");

        Assert.Equal(new[] { "hunt", "favors" }, host.Paused);
    }

    /// <summary>The sentinel the $scriptlist* vars yield when the filtered set
    /// is empty. It has to mean "nothing", not "all" — otherwise
    /// `#script abort $scriptlistpaused` with nothing paused would abort every
    /// running script, the exact opposite of what the list said.</summary>
    [Fact]
    public void A_none_list_acts_on_nothing_rather_than_everything()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors" });
        Run(host, "#script abort none");

        Assert.False(host.StopAllCalled);
        Assert.Empty(host.Stopped);
    }

    [Fact]
    public void A_list_entry_that_is_not_running_is_simply_skipped()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt" });
        Run(host, "#script abort hunt|ghost");

        Assert.Equal(new[] { "hunt" }, host.Stopped);
    }

    [Fact]
    public void A_list_still_honours_a_trailing_except()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors", "exptally" });
        Run(host, "#script abort hunt|favors|exptally except favors");

        Assert.Equal(new[] { "hunt", "exptally" }, host.Stopped);
    }

    /// <summary>A single name must keep taking the original fast path, so the
    /// list support cannot change what every existing script already does.</summary>
    [Fact]
    public void A_single_name_still_takes_the_direct_path()
    {
        var host = new FakeCommandHost();
        host.RunningNames.Add("hunt");
        Run(host, "#script pause hunt");

        Assert.True(host.PauseCalled);
        Assert.Equal("hunt", host.PausedScript);
    }

    [Fact]
    public void Abort_with_name_stops_that_script_and_starts_nothing()
    {
        // mm_train: put #script abort $MM_WAIT_SCRIPT
        var host = new FakeCommandHost();
        host.RunningNames.Add("hunt");
        Run(host, "#script abort hunt");

        Assert.Equal("hunt", host.StoppedScript);
        Assert.False(host.StopAllCalled);
        Assert.Null(host.RanScript);
    }

    [Fact]
    public void Abort_all_stops_every_script()
    {
        var host = new FakeCommandHost();
        Run(host, "#script abort all");

        Assert.True(host.StopAllCalled);
        Assert.Null(host.RanScript);
    }

    [Fact]
    public void Abort_all_except_spares_the_excepted_script()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors", "exptally" });
        Run(host, "#script abort all except favors");

        Assert.False(host.StopAllCalled);
        Assert.Equal(new[] { "hunt", "exptally" }, host.Stopped);
    }

    [Fact]
    public void Pause_and_resume_with_name_target_that_script()
    {
        var host = new FakeCommandHost();
        Run(host, "#script pause hunt");
        Run(host, "#script resume hunt");

        Assert.Equal("hunt", host.PausedScript);
        Assert.Equal("hunt", host.ResumedScript);
    }

    [Fact]
    public void Pause_all_maps_to_the_all_form()
    {
        var host = new FakeCommandHost();
        Run(host, "#script pause all");

        Assert.True(host.PauseCalled);
        Assert.Null(host.PausedScript);
    }

    [Fact]
    public void Pause_all_except_pauses_the_others_individually()
    {
        var host = new FakeCommandHost();
        host.RunningNames.AddRange(new[] { "hunt", "favors" });
        Run(host, "#script pause all except hunt");

        Assert.Equal(new[] { "favors" }, host.Paused);
    }

    [Fact]
    public void PauseOrResume_routes_named_and_all_forms()
    {
        var host = new FakeCommandHost();
        Run(host, "#script pauseorresume hunt");
        Assert.True(host.PauseOrResumeCalled);
        Assert.Equal("hunt", host.PauseOrResumeTarget);

        host = new FakeCommandHost();
        Run(host, "#script pauseorresume");
        Assert.True(host.PauseOrResumeCalled);
        Assert.Null(host.PauseOrResumeTarget);
    }

    [Fact]
    public void Reload_routes_named_and_all_forms()
    {
        var host = new FakeCommandHost();
        Run(host, "#script reload hunt");
        Assert.True(host.ReloadCalled);
        Assert.Equal("hunt", host.ReloadTarget);

        host = new FakeCommandHost();
        Run(host, "#script reload all");
        Assert.True(host.ReloadCalled);
        Assert.Null(host.ReloadTarget);
    }

    [Fact]
    public void Debug_parses_level_then_name()
    {
        var host = new FakeCommandHost();
        Run(host, "#script debug 5 hunt");
        Assert.Equal(5, host.DebugLevelSet);
        Assert.Equal("hunt", host.DebugTarget);

        host = new FakeCommandHost();
        Run(host, "#script debug 10");
        Assert.Equal(10, host.DebugLevelSet);
        Assert.Null(host.DebugTarget);

        host = new FakeCommandHost();
        Run(host, "#script debug hunt");
        Assert.Null(host.DebugLevelSet);
        Assert.Contains(host.Echoed, l => l.StartsWith("Usage: #script debug"));
    }

    [Fact]
    public void Vars_parses_name_and_filter()
    {
        var host = new FakeCommandHost();
        Run(host, "#script vars hunt exp");
        Assert.True(host.VarsCalled);
        Assert.Equal("hunt", host.VarsName);
        Assert.Equal("exp", host.VarsFilter);

        host = new FakeCommandHost();
        Run(host, "#script variables");
        Assert.True(host.VarsCalled);
        Assert.Null(host.VarsName);
        Assert.Equal(string.Empty, host.VarsFilter);
    }

    [Fact]
    public void Trace_routes_named_and_all_forms()
    {
        var host = new FakeCommandHost();
        Run(host, "#script trace hunt");
        Assert.True(host.TraceCalled);
        Assert.Equal("hunt", host.TraceName);

        host = new FakeCommandHost();
        Run(host, "#script trace all");
        Assert.True(host.TraceCalled);
        Assert.Null(host.TraceName);
    }

    [Fact]
    public void Explorer_routes_to_the_host()
    {
        var host = new FakeCommandHost();
        Run(host, "#script explorer");
        Assert.True(host.ExplorerShown);
    }

    [Fact]
    public void Unknown_subcommand_lists_scripts_instead_of_running_one()
    {
        var host = new FakeCommandHost();
        Run(host, "#script somescript");

        Assert.Null(host.RanScript);
        Assert.Contains("Active scripts:", host.Echoed);
        Assert.Contains("None.", host.Echoed);
    }

    [Fact]
    public void Bare_script_lists_scripts()
    {
        var host = new FakeCommandHost();
        Run(host, "#script");

        Assert.Null(host.RanScript);
        Assert.Contains("Active scripts:", host.Echoed);
        Assert.Contains("None.", host.Echoed);
    }

    [Fact]
    public void Listing_prints_status_lines_from_the_host()
    {
        // The fake relies on ICommandHost's default ScriptStatusLines, which
        // falls back to the plain running-script names.
        var host = new FakeCommandHost();
        host.RunningNames.Add("hunt");
        Run(host, "#scripts");

        Assert.Contains("Active scripts:", host.Echoed);
        Assert.Contains("hunt", host.Echoed);
        Assert.DoesNotContain("None.", host.Echoed);
    }

    /// <summary>ICommandHost double recording script-lifecycle calls.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public string?      RanScript     { get; private set; }
        public string?      StoppedScript { get; private set; }
        public List<string> Stopped       { get; } = new();
        public bool         StopAllCalled { get; private set; }
        public bool         PauseCalled   { get; private set; }
        public string?      PausedScript  { get; private set; }
        public List<string> Paused        { get; } = new();
        public string?      ResumedScript { get; private set; }
        public bool         PauseOrResumeCalled  { get; private set; }
        public string?      PauseOrResumeTarget  { get; private set; }
        public bool         ReloadCalled  { get; private set; }
        public string?      ReloadTarget  { get; private set; }
        public int?         DebugLevelSet { get; private set; }
        public string?      DebugTarget   { get; private set; }
        public bool         VarsCalled    { get; private set; }
        public string?      VarsName      { get; private set; }
        public string?      VarsFilter    { get; private set; }
        public bool         TraceCalled   { get; private set; }
        public string?      TraceName     { get; private set; }
        public bool         ExplorerShown { get; private set; }
        public List<string> RunningNames  { get; } = new();
        public List<string> Echoed        { get; } = new();

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => new Dictionary<string, string>();
        public string ExpandVariables(string text) => text;

        public void Echo(string text) => Echoed.Add(text);
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) { }
        public void RunScript(string text) => RanScript = text;
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { StoppedScript = name; if (name is not null) Stopped.Add(name); }
        public void StopAllScripts() => StopAllCalled = true;
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void PauseScript(string? name)
        { PauseCalled = true; PausedScript = name; if (name is not null) Paused.Add(name); }
        public void ResumeScript(string? name) => ResumedScript = name;
        public void SetTraceLevelAll(int level) { }
        public void PauseOrResumeScript(string? name) { PauseOrResumeCalled = true; PauseOrResumeTarget = name; }
        public void ReloadScript(string? name) { ReloadCalled = true; ReloadTarget = name; }
        public void ShowScriptVars(string? name, string filter) { VarsCalled = true; VarsName = name; VarsFilter = filter; }
        public void ShowScriptTrace(string? name) { TraceCalled = true; TraceName = name; }
        public void SetScriptDebugLevel(int level, string? name) { DebugLevelSet = level; DebugTarget = name; }
        public void ShowScriptExplorer() => ExplorerShown = true;
        public IReadOnlyList<string> RunningScripts() => RunningNames;
        public void SetGlobalVariable(string name, string value) { }
        public void RemoveGlobalVariable(string name) { }
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
