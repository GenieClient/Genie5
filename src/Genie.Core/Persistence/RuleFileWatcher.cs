using System.Collections.Concurrent;

namespace Genie.Core.Persistence;

/// <summary>
/// Watches config directories for external edits to the JSON rule files
/// (<c>highlights.json</c>, <c>triggers.json</c>, <c>substitutes.json</c>,
/// <c>gags.json</c>, <c>aliases.json</c>, <c>variables.json</c>,
/// <c>classes.json</c>) so the host can reload them into the live engines
/// without a reconnect. Raises <see cref="RuleFileChanged"/> with the bare
/// file name (lower-case) after a per-file debounce — editors typically emit
/// several FS events per save (truncate + write, or temp-file + rename).
///
/// <para>The app's own saves must not bounce back as "external" edits: writers
/// call <see cref="MarkAppWrite"/> just before writing a watched file, and any
/// FS event for that path within <see cref="SuppressWindow"/> is ignored.
/// The registry is static because the writer (the Configuration dialog) and
/// the watcher owner (the main window) are different objects with no direct
/// reference to each other. Missing a suppression is safe — the reload is
/// idempotent against a file the app just serialized — it would only cost a
/// redundant reload.</para>
///
/// <para>Events are raised on a thread-pool thread; the host is responsible
/// for marshaling to the UI thread before touching engines.</para>
/// </summary>
public sealed class RuleFileWatcher : IDisposable
{
    /// <summary>The rule files that live-reload on external edit. Names only —
    /// the same set is watched in every scoped directory.</summary>
    public static readonly IReadOnlySet<string> WatchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "highlights.json",
        "triggers.json",
        "substitutes.json",
        "gags.json",
        "aliases.json",
        "variables.json",
        "classes.json",
    };

    private static readonly TimeSpan SuppressWindow = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<string, DateTime> AppWrites =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record that the app itself is about to write <paramref name="path"/>,
    /// so the FS events from that save are not reported as an external edit.
    /// Call immediately before the write.</summary>
    public static void MarkAppWrite(string path)
    {
        try { AppWrites[Path.GetFullPath(path)] = DateTime.UtcNow; }
        catch { /* invalid path — nothing to suppress */ }
    }

    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, Timer> _debounce =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _debounceMs;
    private bool _disposed;

    /// <summary>An external edit settled for the named rule file. Argument is
    /// the bare lower-case file name (e.g. <c>"triggers.json"</c>) — the host
    /// re-resolves which directory's copy is effective, so which copy changed
    /// doesn't matter.</summary>
    public event Action<string>? RuleFileChanged;

    public RuleFileWatcher(int debounceMs = 400) => _debounceMs = debounceMs;

    /// <summary>
    /// Point the watcher at the given directories, replacing any previous
    /// scope. Duplicates and null/empty entries are skipped; directories are
    /// created if missing (a fresh profile has no config dir until its first
    /// save, and a watcher can't attach to a nonexistent path). A directory
    /// that still can't be watched (permissions, exotic mounts) is skipped —
    /// rules there just don't live-reload.
    /// </summary>
    public void Rescope(params string?[] directories)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string full;
                try { full = Path.GetFullPath(dir); } catch { continue; }
                if (!seen.Add(full)) continue;

                try
                {
                    Directory.CreateDirectory(full);
                    var watcher = new FileSystemWatcher(full, "*.json")
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Changed += (_, e) => OnFsEvent(e.Name, e.FullPath);
                    watcher.Created += (_, e) => OnFsEvent(e.Name, e.FullPath);
                    watcher.Deleted += (_, e) => OnFsEvent(e.Name, e.FullPath);
                    watcher.Renamed += (_, e) => OnFsEvent(e.Name, e.FullPath);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch { /* unwatchable dir — no live reload there */ }
            }
        }
    }

    private void OnFsEvent(string? name, string fullPath)
    {
        if (name is null) return;
        var fileName = Path.GetFileName(name);
        if (!WatchedFiles.Contains(fileName)) return;

        // App's own save — swallow the whole event burst for it.
        try
        {
            if (AppWrites.TryGetValue(Path.GetFullPath(fullPath), out var marked) &&
                DateTime.UtcNow - marked < SuppressWindow)
                return;
        }
        catch { /* path normalization failed — treat as external */ }

        // Debounce per file name: (re)arm a one-shot timer so a burst of FS
        // events collapses into one RuleFileChanged after the file settles.
        var key = fileName.ToLowerInvariant();
        var timer = _debounce.GetOrAdd(key, k => new Timer(_ => Fire(k), null,
            Timeout.Infinite, Timeout.Infinite));
        try { timer.Change(_debounceMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* disposed mid-event */ }
    }

    private void Fire(string fileName)
    {
        if (_disposed) return;
        try { RuleFileChanged?.Invoke(fileName); }
        catch { /* a handler failure must not kill the timer thread */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
        foreach (var t in _debounce.Values) t.Dispose();
        _debounce.Clear();
    }
}
