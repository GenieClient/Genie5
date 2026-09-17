using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Dialogs;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #343 — the first-appearance prompt told users they could change a
/// dialog's placement "later in Configuration", and no such page exists. Worse,
/// a mistaken "Never show it" was unrecoverable in-app: the answer lives in
/// dialogmappings.json, <c>ServerDialogMappings.Remove</c> had no caller, and
/// <c>#dialogs</c> offered only <c>list</c> and <c>report</c>.
///
/// <c>#dialogs forget &lt;id&gt;</c> is the way back, wired to the store that
/// was already there. The settings grid (#156) is still the full answer; this
/// is the part that ships today.
/// </summary>
public class DialogsForgetCommandTests : IDisposable
{
    private readonly string      _root;
    private readonly GenieConfig _config;

    public DialogsForgetCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_dlgforget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var lds = new LocalDirectoryService("GenieDlgForgetTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (CommandEngine Engine, FakeCommandHost Host) NewEngine()
    {
        var host   = new FakeCommandHost();
        var engine = new CommandEngine(_config, new CommandQueue(), new EventQueue(), host)
        {
            DialogTracker = new DialogSessionTracker(),
        };
        return (engine, host);
    }

    // ── The command surface ────────────────────────────────────────────────

    [Fact]
    public void Forget_routes_the_id_to_the_host_and_confirms()
    {
        var (engine, host) = NewEngine();
        host.Forgettable.Add("befriend");

        engine.ProcessInput("#dialogs forget befriend");

        Assert.Equal(new[] { "befriend" }, host.Forgotten);
        Assert.Contains(host.Echoed, l => l.Contains("forgot 'befriend'"));
        Assert.Contains(host.Echoed, l => l.Contains("ask again"));
    }

    [Fact]
    public void Reset_is_accepted_as_an_alias()
    {
        var (engine, host) = NewEngine();
        host.Forgettable.Add("befriend");

        engine.ProcessInput("#dialogs reset befriend");

        Assert.Equal(new[] { "befriend" }, host.Forgotten);
    }

    [Fact]
    public void Forget_says_so_when_no_answer_was_stored()
    {
        var (engine, host) = NewEngine();

        engine.ProcessInput("#dialogs forget nosuchdialog");

        Assert.Contains(host.Echoed, l => l.Contains("no saved answer for 'nosuchdialog'"));
    }

    [Fact]
    public void Forget_without_an_id_falls_through_to_usage()
    {
        var (engine, host) = NewEngine();

        engine.ProcessInput("#dialogs forget");

        Assert.Empty(host.Forgotten);
        Assert.Contains(host.Echoed, l => l.Contains("#dialogs forget <id>"));
    }

    [Fact]
    public void Usage_advertises_the_new_subcommand()
    {
        var (engine, host) = NewEngine();

        engine.ProcessInput("#dialogs wat");

        Assert.Contains(host.Echoed, l => l.Contains("Usage: #dialogs [list] | #dialogs report <id> | #dialogs forget <id>"));
    }

    // ── The store behaviour the command depends on ─────────────────────────

    [Fact]
    public void Removing_an_ignore_answer_makes_the_dialog_prompt_again_this_session()
    {
        // The point of the feature: not just "the row is gone at next launch"
        // but "Genie asks me again now" — Remove also clears the session's
        // prompted/deferred marks.
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "befriend", Mode = ServerDialogMode.Ignore });
        Assert.False(m.Resolve("befriend").NeedsPrompt);

        Assert.True(m.Remove("befriend"));

        Assert.Null(m.Find("befriend"));
        Assert.True(m.Resolve("befriend").NeedsPrompt);
        Assert.True(m.TryClaimPrompt("befriend"));
    }

    [Fact]
    public void Removing_an_unknown_id_reports_false()
    {
        var m = new ServerDialogMappings();

        Assert.False(m.Remove("nosuchdialog"));
        Assert.False(m.Remove(""));
    }

    [Fact]
    public void A_forgotten_mapping_does_not_come_back_from_disk()
    {
        // #dialogs forget saves after removing; prove the round-trip, since a
        // removal that is not persisted would silently return at next connect.
        var path = Path.Combine(_root, ServerDialogMappings.FileName);
        var m    = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "befriend", Mode = ServerDialogMode.Ignore });
        m.Set(new ServerDialogMapping { Id = "keepme",   Mode = ServerDialogMode.NewWindow });
        Assert.True(m.Save(path));

        Assert.True(m.Remove("befriend"));
        Assert.True(m.Save(path));

        var reloaded = new ServerDialogMappings();
        Assert.True(reloaded.Load(path));
        Assert.Null(reloaded.Find("befriend"));
        Assert.NotNull(reloaded.Find("keepme"));
    }

    /// <summary>ICommandHost double: records Echo lines and forget calls.
    /// <c>ForgetDialog</c> is a default interface method, so a host that does
    /// not override it keeps compiling — this one overrides it.</summary>
    private sealed class FakeCommandHost : ICommandHost
    {
        public List<string> Echoed      { get; } = new();
        public List<string> Forgotten   { get; } = new();
        public HashSet<string> Forgettable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool ForgetDialog(string dialogId)
        {
            if (!Forgettable.Contains(dialogId)) return false;
            Forgotten.Add(dialogId);
            return true;
        }

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
