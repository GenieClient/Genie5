using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Dialogs;
using Genie.Core.Diagnostics;
using Genie.Core.Events;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #156 Phase 0c — the session dialog inventory and the user-initiated,
/// human-in-the-loop dialog coverage report.
/// </summary>
public class DialogGapReportTests
{
    private static DialogControl Control(DialogControlType type, string id, string? cmd = null) =>
        new(type, id, null, null, cmd, null, null, null, null, null,
            new Dictionary<string, string>());

    private static DialogSessionTracker TrackedBank()
    {
        var t = new DialogSessionTracker();
        t.Observe(new OpenDialogEvent("bank_debt", "Debt", "center", "200", "100", true, "dynamic", "<openDialog id='bank_debt'/>"));
        t.Observe(new DialogDataEvent("bank_debt",
            new[]
            {
                Control(DialogControlType.Label, "lbl"),
                Control(DialogControlType.CmdButton, "pay", "pay debt %amt%"),
                Control(DialogControlType.Label, "lbl2"),
            },
            Clear: false,
            RawXml: "<dialogData id='bank_debt'><label id='lbl' value='You owe 100 Kronars'/></dialogData>"));
        return t;
    }

    [Fact]
    public void Tracker_CountsBlocksAndControls_AndCarriesTheTitle()
    {
        var row = TrackedBank().TryGet("bank_debt");
        Assert.NotNull(row);
        Assert.Equal("Debt", row!.Title);
        Assert.Equal(1, row.Blocks);
        Assert.Equal(2, row.ControlCounts[DialogControlType.Label]);
        Assert.Equal(1, row.ControlCounts[DialogControlType.CmdButton]);
        Assert.Contains("You owe", row.LastRaw);
    }

    [Fact]
    public void Draft_IsPrefillOnly_WithCensusSampleAndReviewWarning()
    {
        var row = TrackedBank().TryGet("bank_debt")!;
        var ctx = new XmlGapReport.ReportContext("5.0.0-test", "TestOS", "abc123", "#dialogs report");

        var draft = DialogGapReport.Build(row, ctx);

        Assert.Equal("[Dialog coverage] Server dialog 'bank_debt'", draft.Title);
        Assert.Equal("xml-coverage", draft.Labels);
        Assert.StartsWith("https://github.com/GenieClient/Genie5/issues/new?", draft.Url);
        Assert.Contains("Label ×2", draft.Body);
        Assert.Contains("CmdButton ×1", draft.Body);
        Assert.Contains("window title: **Debt**", draft.Body);
        Assert.Contains("double-check before submitting", draft.Body);   // own-data warning
        Assert.Contains("dialog_journal.xml", draft.Body);
        Assert.DoesNotContain("token", draft.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DialogsCommand_List_EchoesTheInventory()
    {
        var (cmd, host) = MakeEngine(TrackedBank());
        cmd.ProcessInput("#dialogs list", interactive: false);

        var text = string.Join("\n", host.Echoed);
        Assert.Contains("bank_debt", text);
        Assert.Contains("CmdButton×1", text);
        Assert.Contains("#dialogs report", text);
    }

    [Fact]
    public void DialogsCommand_ReportUnseenId_ExplainsInsteadOfDrafting()
    {
        var (cmd, host) = MakeEngine(TrackedBank());
        cmd.ProcessInput("#dialogs report nosuch", interactive: false);
        Assert.Contains(host.Echoed, l => l.Contains("no dialog 'nosuch'"));
    }

    [Fact]
    public void DialogsCommand_WithoutTracker_SaysConnectFirst()
    {
        var (cmd, host) = MakeEngine(tracker: null);
        cmd.ProcessInput("#dialogs", interactive: false);
        Assert.Contains(host.Echoed, l => l.Contains("connect first"));
    }

    private static (CommandEngine, EchoHost) MakeEngine(DialogSessionTracker? tracker)
    {
        var host = new EchoHost();
        var lds  = new LocalDirectoryService("Genie156Test",
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "g5-156-" + Guid.NewGuid().ToString("N")));
        var cmd  = new CommandEngine(new GenieConfig(lds), new CommandQueue(), new EventQueue(), host)
        {
            DialogTracker = tracker,
        };
        return (cmd, host);
    }

    private sealed class EchoHost : ICommandHost
    {
        public List<string> Echoed { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void Echo(string text) => Echoed.Add(text);
        public void EchoTo(string text, string? window, string? color) => Echoed.Add(text);
        public void EchoMain(string text, string? color, bool mono) => Echoed.Add(text);
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
