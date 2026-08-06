using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;

namespace Genie.Core.Persistence;

/// <summary>
/// Offline replay of a profile directory's saved <c>.cfg</c> rule files into
/// engines, through the SAME <see cref="CommandEngine"/> loaders the connect
/// path runs (<c>#alias load</c>, <c>#macro load</c>, …) — including their
/// clear-then-replay and JSON-heal behaviors.
///
/// <para>Why this exists: the connect sequence loads the host's <c>.json</c>
/// rules first and then replays the <c>.cfg</c> files over them, and each
/// <c>.cfg</c> loader clears its engine before replaying — so for any rule
/// type with a <c>.cfg</c> on disk, the <c>.cfg</c> is the effective
/// persisted truth. An offline consumer (the Configuration dialog's draft
/// engines) that read only the <c>.json</c> would both hide every cfg-only
/// rule and, on a subsequent cfg rewrite, silently drop them.</para>
/// </summary>
public static class CfgReplay
{
    /// <summary>
    /// Replay each wired engine's <c>.cfg</c> file from
    /// <paramref name="profileDir"/> (when the file exists) into that engine.
    /// Parameter order mirrors the connect-time auto-load order. Engines left
    /// null are untouched; a missing directory or an empty wired set is a
    /// no-op. Variables use the loader's merge semantics (<c>#var load</c>
    /// never clears), replayed via a scratch engine into
    /// <paramref name="variables"/>.
    /// </summary>
    public static void LoadInto(
        string              profileDir,
        ClassEngine?        classes     = null,
        AliasEngine?        aliases     = null,
        VariableStore?      variables   = null,
        HighlightEngine?    highlights  = null,
        TriggerEngineFinal? triggers    = null,
        SubstituteEngine?   substitutes = null,
        GagEngine?          gags        = null,
        MacroEngine?        macros      = null)
    {
        if (string.IsNullOrWhiteSpace(profileDir) || !Directory.Exists(profileDir)) return;

        var wanted = new (object? Engine, string File, string LoadCommand)[]
        {
            (classes,     "classes.cfg",     "#class load"),
            (aliases,     "aliases.cfg",     "#alias load"),
            (variables,   "variables.cfg",   "#var load"),
            (highlights,  "highlights.cfg",  "#highlight load"),
            (triggers,    "triggers.cfg",    "#trigger load"),
            (substitutes, "substitutes.cfg", "#substitute load"),
            (gags,        "gags.cfg",        "#gag load"),
            (macros,      "macros.cfg",      "#macro load"),
        };
        if (!wanted.Any(w => w.Engine is not null && File.Exists(Path.Combine(profileDir, w.File))))
            return;

        // A headless CommandEngine whose ConfigProfileDir IS profileDir, so
        // the real loaders resolve exactly the files we were pointed at.
        var lds = new LocalDirectoryService("Genie5", profileDir);
        lds.UseExplicitRoot(profileDir);
        var cfg = new GenieConfig(lds) { ProfileConfigDirRaw = "" };
        var cmd = new CommandEngine(cfg, new CommandQueue(), new EventQueue(), SilentHost.Instance)
        {
            Classes     = classes,
            Aliases     = aliases,
            Highlights  = highlights,
            Triggers    = triggers,
            Substitutes = substitutes,
            Gags        = gags,
            Macros      = macros,
        };

        // #var routes through a VariableEngine that owns its store, so replay
        // into a scratch engine and merge the result into the caller's store.
        VariableEngine? scratchVars = null;
        if (variables is not null)
            cmd.Variables = scratchVars = new VariableEngine();

        foreach (var (engine, file, load) in wanted)
            if (engine is not null && File.Exists(Path.Combine(profileDir, file)))
                cmd.ProcessInput(load, interactive: false);

        if (scratchVars is not null && variables is not null)
            foreach (var kvp in scratchVars.Store.GetAll())
                variables.Set(kvp.Key, kvp.Value.Value);
    }

    /// <summary>
    /// No-op <see cref="ICommandHost"/> for the offline replay: loader echoes
    /// go nowhere, nothing can reach a game socket, and — critically —
    /// <see cref="ExpandVariables"/> is the identity so replayed rules are
    /// stored verbatim (the same no-expand-on-load invariant the connect-time
    /// loaders pin in CfgFileGuardTests).
    /// </summary>
    private sealed class SilentHost : ICommandHost
    {
        public static readonly SilentHost Instance = new();
        private static readonly Dictionary<string, string> EmptyGlobals = new();
        private SilentHost() { }

        public void Echo(string text) { }
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
        public void SetGlobalVariable(string name, string value) { }
        public void RemoveGlobalVariable(string name) { }
        public IReadOnlyDictionary<string, string> GetGlobalVariables() => EmptyGlobals;
        public string SetLiveAudit(Diagnostics.AuditMode mode) => string.Empty;
        public string ExpandVariables(string text) => text;
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
