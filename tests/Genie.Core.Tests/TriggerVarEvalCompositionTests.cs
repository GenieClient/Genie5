using System;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #300 — the unbraced Genie 4 composition <c>#var tmp #eval …</c> in a
/// trigger action stored the LITERAL "#eval replacere(…)" text (with its quotes
/// stripped) instead of the evaluated result. Built from the reporter's exact
/// trigger: pattern, action, Eval action + Match all toggles.
/// </summary>
public class TriggerVarEvalCompositionTests : IDisposable
{
    private readonly string _root;
    private readonly GenieConfig _config;

    public TriggerVarEvalCompositionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_300_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("Genie300Test", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private (CommandEngine cmd, TriggerEngineFinal trig, VariableEngine vars) Make()
    {
        var host = new SilentCommandHost();
        var cmd  = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host)
        {
            Variables = new VariableEngine(),
        };
        var trig = new TriggerEngineFinal(host, cmd);
        cmd.Triggers = trig;
        return (cmd, trig, cmd.Variables!);
    }

    [Fact]
    public void Reporters_trigger_stores_the_evaluated_result_not_the_literal()
    {
        var (_, trig, vars) = Make();
        trig.AddTrigger(
            @"intently\.  You believe you've learned something significant about (.*)\!",
            "#var tmp #eval replacere(\"$1\",\" \",\"_\");#echo >Almanac $time $1 $$tmp.LearningRate/34",
            eval: true, matchAll: true);

        trig.ProcessLine("intently.  You believe you've learned something significant about Bone Armor!");

        Assert.Equal("Bone_Armor", vars.Store.Get("tmp"));
    }

    [Fact]
    public void Unbraced_var_eval_with_quoted_args_works_at_the_command_line()
    {
        var (cmd, _, vars) = Make();
        cmd.ProcessInput("#var tmp #eval replacere(\"Bone Armor\",\" \",\"_\")", interactive: false);

        Assert.Equal("Bone_Armor", vars.Store.Get("tmp"));
    }

    [Fact]
    public void Braced_var_eval_form_still_works()
    {
        var (cmd, _, vars) = Make();
        cmd.ProcessInput("#var tmp {#eval replacere(\"Bone Armor\",\" \",\"_\")}", interactive: false);

        Assert.Equal("Bone_Armor", vars.Store.Get("tmp"));
    }

    private sealed class SilentCommandHost : ICommandHost
    {
        public System.Collections.Generic.Dictionary<string, string> Globals { get; } = new();
        public System.Collections.Generic.IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) { }
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
        public System.Collections.Generic.IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
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
