using Genie.Plugins;

namespace Genie.Core.Plugins;

/// <summary>
/// Owns the set of loaded plugins and fans game events out to them. Phase 1 is
/// in-process registration only; the DLL loader (Phase 3) adds discovery +
/// <c>AssemblyLoadContext</c> load/unload on top of this same surface.
///
/// <para>Every dispatch is guarded per-plugin so one misbehaving plugin can't
/// take down the session or block the others.</para>
/// </summary>
public sealed class PluginManager
{
    private readonly List<IGeniePlugin>                       _plugins        = new();
    // plugin id → its DLL load context (in-process plugins have no entry).
    private readonly Dictionary<string, PluginLoadContext>   _pluginContexts =
        new(StringComparer.OrdinalIgnoreCase);
    // plugin id → the source DLL it came from (full path), for per-file load/unload UI.
    private readonly Dictionary<string, string>              _pluginFiles    =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IPluginHost                             _host;

    /// <summary>Plugin ids that have been absorbed into Core as built-in extensions
    /// (Spell Timer, Experience, Time Tracker, Circle Calculator, Inventory View).
    /// A leftover DLL with one of these ids is skipped at registration so it can't
    /// shadow the in-Core version.</summary>
    private static readonly IReadOnlySet<string> RetiredPluginIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "genie.spelltimer", "genie.experience", "genie.timetracker",
            "genie.circlecalc", "genie.inventoryview",
        };

    public PluginManager(IPluginHost host) => _host = host;

    public IReadOnlyList<IGeniePlugin> Plugins => _plugins;

    /// <summary>
    /// Discover and load every <c>*.dll</c> in <paramref name="pluginsDir"/> as
    /// a plugin. Each DLL gets its own collectible load context. Plugins must
    /// expose a public parameterless constructor and implement
    /// <see cref="IGeniePlugin"/>. Failures are logged and skipped — one bad
    /// DLL never aborts the rest. Creates the directory if missing.
    /// </summary>
    public void DiscoverAndLoad(string pluginsDir)
    {
        try
        {
            if (!Directory.Exists(pluginsDir)) { Directory.CreateDirectory(pluginsDir); return; }
        }
        catch (Exception ex) { _host.Log($"[plugin] can't access plugins dir '{pluginsDir}': {ex.Message}"); return; }

        foreach (var dll in Directory.EnumerateFiles(pluginsDir, "*.dll"))
        {
            try { LoadAssembly(dll); }
            catch (Exception ex) { _host.Log($"[plugin] failed to load '{Path.GetFileName(dll)}': {ex.Message}"); }
        }
    }

    /// <summary>Load a single plugin DLL (the per-file counterpart to
    /// <see cref="DiscoverAndLoad"/>). Returns true if it added a plugin.</summary>
    public bool LoadFile(string dllPath)
    {
        var before = _plugins.Count;
        try { LoadAssembly(dllPath); }
        catch (Exception ex) { _host.Log($"[plugin] failed to load '{Path.GetFileName(dllPath)}': {ex.Message}"); }
        return _plugins.Count > before;
    }

    /// <summary>True if any loaded plugin came from this DLL path.</summary>
    public bool IsFileLoaded(string dllPath)
    {
        var full = Path.GetFullPath(dllPath);
        return _pluginFiles.Values.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadAssembly(string dllPath)
    {
        var fullPath = Path.GetFullPath(dllPath);
        var alc = new PluginLoadContext(dllPath);
        var asm = alc.LoadFromAssemblyPath(dllPath);
        var loadedAny = false;

        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IGeniePlugin).IsAssignableFrom(type))  continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                _host.Log($"[plugin] {type.FullName} has no public parameterless constructor — skipped.");
                continue;
            }
            if (Activator.CreateInstance(type) is IGeniePlugin plugin && Register(plugin))
            {
                _pluginContexts[plugin.Id] = alc;
                _pluginFiles[plugin.Id]    = fullPath;
                loadedAny = true;
                _host.Log($"[plugin] loaded '{plugin.Id}' v{plugin.Version} from {Path.GetFileName(dllPath)}");
            }
        }

        if (!loadedAny) alc.Unload();   // nothing useful in it — don't pin the context
    }

    /// <summary>
    /// Fully unload a plugin: shut it down, drop it from the registry, and
    /// unload its <see cref="PluginLoadContext"/> (releasing the .dll so it can
    /// be replaced/deleted). Distinct from disabling — a disabled plugin stays
    /// loaded. In-process plugins (no context) are just removed. Returns false
    /// if no such plugin id is loaded.
    /// </summary>
    public bool Unload(string id)
    {
        var plugin = _plugins.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (plugin is null) return false;

        try { plugin.Shutdown(); }
        catch (Exception ex) { _host.Log($"[plugin] {id}: shutdown error — {ex.Message}"); }
        _plugins.Remove(plugin);
        _pluginFiles.Remove(id);

        if (_pluginContexts.Remove(id, out var alc))
        {
            try { alc.Unload(); } catch { /* best-effort; GC finishes the unload */ }
        }
        _host.Log($"[plugin] unloaded '{id}'");
        return true;
    }

    /// <summary>Register and initialize an in-process plugin. Ignores a
    /// duplicate id. Returns false if rejected (duplicate or host too old).</summary>
    public bool Register(IGeniePlugin plugin)
    {
        if (_plugins.Any(p => p.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase)))
            return false;

        // These trackers are now built in to Core (no longer external plugins).
        // Skip a stale DLL a user may still have in their Plugins folder so it can't
        // double up the dock panel / script globals against the in-Core version.
        if (RetiredPluginIds.Contains(plugin.Id))
        {
            _host.Log($"[plugin] {plugin.Id} is now built in to Genie 5 — skipping the external plugin. You can delete its DLL.");
            return false;
        }

        if (!HostMeetsMinimum(plugin.MinHostVersion))
        {
            _host.Log($"[plugin] {plugin.Id}: needs host >= {plugin.MinHostVersion} (running {_host.HostVersion}) — not loaded.");
            return false;
        }

        _plugins.Add(plugin);
        try { plugin.Initialize(_host); }
        catch (Exception ex) { _host.Log($"[plugin] {plugin.Id}: init failed — {ex.Message}"); }
        return true;
    }

    public void Shutdown()
    {
        foreach (var p in _plugins) { try { p.Shutdown(); } catch { } }
        _plugins.Clear();
        // Unload DLL-loaded plugins' contexts. Best-effort: if a plugin leaked a
        // reference the context won't collect immediately, but Unload starts it.
        foreach (var alc in _pluginContexts.Values) { try { alc.Unload(); } catch { } }
        _pluginContexts.Clear();
        _pluginFiles.Clear();
    }

    // ── Transform dispatch (chained; null = gag/swallow) ─────────────────────

    // A plugin may inject a line from inside OnGameText (host.SendCommand("#parse …")
    // → GenieCore.InjectParsedLine → back here). Per-thread guard: such nested
    // lines pass through undispatched instead of recursing forever.
    [ThreadStatic] private static bool _inGameTextDispatch;

    /// <summary>Run a game-text line through every enabled plugin in registration
    /// order, each seeing the prior's output. Returns the final text — the caller
    /// (GenieCore's per-line pipeline) honors it end-to-end: a rewrite feeds
    /// scripts/triggers/display, null means gagged (short-circuits the chain).
    /// A plugin that throws is skipped with its input preserved.</summary>
    public string? DispatchGameText(string text, string stream)
    {
        if (_inGameTextDispatch) return text;
        _inGameTextDispatch = true;
        try     { return Chain(text, (p, s) => p.OnGameText(s, stream)); }
        finally { _inGameTextDispatch = false; }
    }

    // A plugin may send from inside OnInput (host.SendCommand → the command
    // engine, or a `.script` launch whose own `/command` sends come back
    // through ScriptEngine.PluginInput). Per-thread guard, matching the game-text
    // and echo dispatchers: such nested commands pass through undispatched
    // instead of recursing forever. Before public #325 only genuine typed input
    // reached here, and the bypass on every programmatic path was itself the
    // recursion safety — script sends now enter this path, so the guard has to
    // be explicit.
    [ThreadStatic] private static bool _inInputDispatch;

    /// <summary>Run an input line through every enabled plugin in order
    /// (<c>IGeniePlugin.OnInput</c> — Genie 4 <c>ParseInput</c> parity). Fed by
    /// typed input (all of it) and by script-issued <c>/commands</c> (public
    /// #325). Returns the final command to run, or null if swallowed.</summary>
    public string? DispatchInput(string input)
    {
        if (_inInputDispatch) return input;
        _inInputDispatch = true;
        try     { return Chain(input, (p, s) => p.OnInput(s)); }
        finally { _inInputDispatch = false; }
    }

    // A plugin may echo from inside OnEcho (host.Echo → GenieCore funnel →
    // back here). Per-thread guard: such nested echoes pass through
    // undispatched instead of recursing forever.
    [ThreadStatic] private static bool _inEchoDispatch;

    /// <summary>Run an echoed display line (<c>#echo</c>, script <c>echo</c>,
    /// host messages) through every enabled plugin in order — a deliberate
    /// Genie 5 extension (Genie 4 never ran echoes through ParseText).
    /// Returns the final text to display, or null if a plugin gagged it.</summary>
    public string? DispatchEcho(string text, string window)
    {
        if (_inEchoDispatch) return text;
        _inEchoDispatch = true;
        try     { return Chain(text, (p, s) => p.OnEcho(s, window)); }
        finally { _inEchoDispatch = false; }
    }

    private string? Chain(string initial, Func<IGeniePlugin, string, string?> step)
    {
        string current = initial;
        for (int i = 0; i < _plugins.Count; i++)
        {
            var p = _plugins[i];
            if (!p.Enabled) continue;
            try
            {
                var next = step(p, current);
                if (next is null) return null;   // gagged — stop the chain
                current = next;
            }
            catch (Exception ex) { _host.Log($"[plugin] {p.Id}: {ex.Message}"); }
        }
        return current;
    }

    // ── Observation dispatch ──────────────────────────────────────────────────

    public void DispatchXml(string xml) =>
        Each(p => p.OnXml(xml));

    public void DispatchCommand(string command) =>
        Each(p => p.OnCommandSent(command));

    public void DispatchPrompt() =>
        Each(p => p.OnPrompt());

    public void DispatchVariableChanged(string name, string value) =>
        Each(p => p.OnVariableChanged(name, value));

    private void Each(Action<IGeniePlugin> action)
    {
        // Index loop + enabled check; per-plugin try/catch keeps a bad plugin
        // from aborting the dispatch for the rest.
        for (int i = 0; i < _plugins.Count; i++)
        {
            var p = _plugins[i];
            if (!p.Enabled) continue;
            try { action(p); }
            catch (Exception ex) { _host.Log($"[plugin] {p.Id}: {ex.Message}"); }
        }
    }

    /// <summary>Compare a plugin's MinHostVersion against the host version.
    /// Lenient: unparseable values are treated as compatible.</summary>
    private bool HostMeetsMinimum(string minHostVersion)
    {
        if (string.IsNullOrWhiteSpace(minHostVersion)) return true;
        if (!Version.TryParse(StripPrerelease(minHostVersion), out var min)) return true;
        if (!Version.TryParse(StripPrerelease(_host.HostVersion), out var host)) return true;
        return host >= min;
    }

    private static string StripPrerelease(string v)
    {
        // "5.0.0-alpha.1" → "5.0.0"
        var dash = v.IndexOf('-');
        return dash >= 0 ? v[..dash] : v;
    }
}
