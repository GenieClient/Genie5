using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #326 — the sibling gap to #325. <c>CommandEngine.ProcessInput</c>
/// offered a '/'-prefixed command to the built-in extensions but never to
/// external plugins, so a slash command produced by an alias expansion, a
/// trigger action, a quick-send segment or one plugin's own
/// <c>host.SendCommand</c> went to the game verbatim and bounced with
/// "Please rephrase that command." #325 fixed the script layer; these pin the
/// command-engine layer, where triggers and aliases live.
/// <para>Ordering is the contract: extensions get first refusal, then plugins,
/// matching <c>GenieCore.ProcessInputCore</c> for typed input.</para>
/// </summary>
public class PluginCommandEngineSlashTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        public CommandEngine Engine      { get; }
        public FakeHost      Host        { get; } = new();
        public List<string>  ToPlugin    { get; } = new();
        public List<string>  ToExtension { get; } = new();

        public Harness(Func<string, string?>? pluginInput = null,
                       Func<string, bool>?    extension   = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "gc_326_" + Guid.NewGuid().ToString("N"));
            var lds = new Genie.Core.Runtime.LocalDirectoryService("Genie326Test", _root);
            lds.UseExplicitRoot(_root);
            Engine = new CommandEngine(new Genie.Core.Config.GenieConfig(lds),
                                       new Genie.Core.Queue.CommandQueue(),
                                       new Genie.Core.Queue.EventQueue(), Host);
            Engine.ClientSlashCommand = s => { ToExtension.Add(s); return extension?.Invoke(s) ?? false; };
            Engine.PluginInput        = s => { ToPlugin.Add(s); return pluginInput is null ? s : pluginInput(s); };
        }

        public void Dispose()
        { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void TriggerAction_SlashCommand_IsSwallowedByPlugin_AndNeverSent()
    {
        using var h = new Harness(pluginInput: _ => null);          // plugin swallows
        var triggers = new Genie.Core.Triggers.TriggerEngineFinal(h.Host, h.Engine);
        triggers.AddTrigger("You feel your spell fade", "/timers start BLESS");

        triggers.ProcessLine("You feel your spell fade.");

        Assert.Empty(h.Host.Sent);
        Assert.Equal("/timers start BLESS", Assert.Single(h.ToPlugin));
    }

    [Fact]
    public void AliasExpansion_SlashCommand_IsSwallowedByPlugin_AndNeverSent()
    {
        using var h = new Harness(pluginInput: _ => null);
        var aliases = new Genie.Core.Aliases.AliasEngine(h.Engine);
        aliases.AddAlias("bl", "/timers start BLESS");
        h.Engine.Aliases = aliases;

        h.Engine.ProcessInput("bl");

        Assert.Empty(h.Host.Sent);
        Assert.Equal("/timers start BLESS", Assert.Single(h.ToPlugin));
    }

    [Fact]
    public void Plugin_MayRewriteTheCommand_AndOnlyTheRewriteIsTransmitted()
    {
        using var h = new Harness(pluginInput: s => s == "/old" ? "/new" : s);

        h.Engine.ProcessInput("/old");

        Assert.Equal("/new", Assert.Single(h.Host.Sent));
    }

    [Fact]
    public void UnhandledSlashCommand_KeepsTheGameSendFallback()
    {
        using var h = new Harness();                                // nobody claims it

        h.Engine.ProcessInput("/unknown thing");

        Assert.Equal("/unknown thing", Assert.Single(h.Host.Sent));
    }

    [Fact]
    public void OrdinaryCommand_NeverEntersPluginInputDispatch()
    {
        using var h = new Harness(pluginInput: _ => null);          // would swallow if offered

        h.Engine.ProcessInput("bob");

        Assert.Empty(h.ToPlugin);
        Assert.Empty(h.ToExtension);
        Assert.Equal("bob", Assert.Single(h.Host.Sent));
    }

    [Fact]
    public void Extension_GetsFirstRefusal_AheadOfPlugins()
    {
        using var h = new Harness(pluginInput: _ => null, extension: _ => true);

        h.Engine.ProcessInput("/track clear");

        Assert.Equal("/track clear", Assert.Single(h.ToExtension));
        Assert.Empty(h.ToPlugin);      // claimed — never offered onward
        Assert.Empty(h.Host.Sent);
    }

    [Fact]
    public void QueuedSendSlashCommand_IsOfferedToPluginsOnThePumpTick()
    {
        // `#send /cmd` and the quick-send dash form ride the RT-gated queue and
        // re-enter ProcessInput on the pump tick — the offer must happen there
        // too, not only on the direct path.
        using var h = new Harness(pluginInput: _ => null);

        h.Engine.ProcessInput("#send /timers stop BLESS");
        h.Engine.Tick();

        Assert.Empty(h.Host.Sent);
        Assert.Equal("/timers stop BLESS", Assert.Single(h.ToPlugin));
    }

    [Fact]
    public void PluginReentrancy_SendCommandFromInsideOnInput_DoesNotRecurse()
    {
        // A plugin emitting a command from inside its own OnInput routes back
        // through ProcessInput to the same hook. The production guard lives in
        // PluginManager.DispatchInput; this pins that the engine tolerates the
        // re-entry and still delivers the nested command exactly once.
        Harness? h = null;
        int depth = 0, maxDepth = 0;
        h = new Harness(pluginInput: s =>
        {
            maxDepth = Math.Max(maxDepth, ++depth);
            try
            {
                if (s == "/outer") { h!.Engine.ProcessInput("/inner"); return null; }
                return s;                       // "/inner" falls through to the game
            }
            finally { depth--; }
        });
        using (h)
        {
            h.Engine.ProcessInput("/outer");

            Assert.True(maxDepth <= 2, $"unbounded re-entry (depth {maxDepth})");
            Assert.Equal("/inner", Assert.Single(h.Host.Sent));
        }
    }

    /// <summary>Minimal <see cref="ICommandHost"/> double — records game sends.</summary>
    private sealed class FakeHost : ICommandHost
    {
        public List<string> Sent { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void Echo(string text) { }
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null)
            => Sent.Add(text);
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
