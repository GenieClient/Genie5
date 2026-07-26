using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Genie.Core;
using Genie.Core.Scripting;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the Script Manager dock tool (evolved from the original Scripts panel,
/// same <c>"scripts"</c> dock id): a library tree of the scripts folder with
/// New / Run / Edit / Delete, a live running-scripts list fed by
/// <see cref="ScriptEngine.GetStatuses"/> on a 500 ms poll (the performance
/// overlay's pull pattern), a detail strip for the selected running script,
/// and the scrollable <c>[script]</c> output log.
///
/// Every script action routes through <c>Commands.ProcessInput("#…")</c> so the
/// panel, the command bar, and .cmd scripts share one code path. The two
/// exceptions are editor launch and folder open, which the App layer owns —
/// those are raised as events the host (<see cref="MainWindowViewModel"/>)
/// handles, same as the Script Bar's pencil button.
/// </summary>
public class ScriptsViewModel : ReactiveObject
{
    private const int MaxOutputLines = 2000;

    private GenieCore? _core;
    private DispatcherTimer? _poll;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _watchDebounce;

    // ── library (left pane) ──────────────────────────────────────────────────

    /// <summary>Visible tree of the scripts folder — folders first, then
    /// .cmd/.inc/.js files. Rebuilt by <see cref="RefreshLibrary"/> and when
    /// <see cref="Filter"/> changes (filtering flattens folder visibility to
    /// matching descendants and expands them).</summary>
    public ObservableCollection<ScriptFileNode> Library { get; } = [];

    [Reactive] public string Filter { get; set; } = string.Empty;
    [Reactive] public ScriptFileNode? SelectedFile { get; set; }

    /// <summary>Optional arguments appended when running the selected script
    /// (the toolbar "args" box). Kept after a run so repeat launches reuse them.</summary>
    [Reactive] public string RunArgs { get; set; } = string.Empty;

    /// <summary>Two-step delete: first Delete click arms this (button reads
    /// "Confirm?"), the second actually deletes. Reset by selection change.</summary>
    [Reactive] public bool ConfirmingDelete { get; private set; }

    /// <summary>Inline new-script naming row (shown by the New button; Enter
    /// commits, Escape cancels).</summary>
    [Reactive] public bool IsNamingNew { get; set; }
    [Reactive] public string NewScriptName { get; set; } = string.Empty;

    // ── running (right pane) ─────────────────────────────────────────────────

    public ObservableCollection<RunningScriptRow> RunningScripts { get; } = [];
    [Reactive] public RunningScriptRow? SelectedRunning { get; set; }

    /// <summary>Rolling buffer of script-originated echo output.</summary>
    public ObservableCollection<TextLine> Output { get; } = [];

    [Reactive] public string StatusText { get; private set; } = "Not connected.";

    /// <summary>Human-readable description of the editor Edit will launch —
    /// the resolved rung of the ladder (Display override → #config editor →
    /// OS default). Shown in the panel's bottom strip.</summary>
    [Reactive] public string EditorDisplay { get; private set; } = "OS default";

    /// <summary>Resolve the current editor description for
    /// <see cref="EditorDisplay"/>. Set by <see cref="MainWindowViewModel"/>,
    /// which owns the ladder's settings.</summary>
    public Func<string>? ResolveEditorDescription { get; set; }

    /// <summary>Persist a new editor override ("" clears it, falling back to
    /// <c>#config editor</c> / OS default). Set by the host — it owns
    /// <c>DisplaySettings.EditorPath</c> + display.json persistence.</summary>
    public Action<string>? EditorPathChanged { get; set; }

    // ── commands ─────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> RunSelectedCommand    { get; }
    public ReactiveCommand<Unit, Unit> EditSelectedCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRunCommandCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyPathCommand       { get; }
    public ReactiveCommand<Unit, Unit> OpenContainingFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewScriptCommand      { get; }
    public ReactiveCommand<Unit, Unit> CommitNewCommand      { get; }
    public ReactiveCommand<Unit, Unit> CancelNewCommand      { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand        { get; }
    public ReactiveCommand<Unit, Unit> OpenFolderCommand     { get; }
    public ReactiveCommand<Unit, Unit> StopAllCommand        { get; }
    public ReactiveCommand<Unit, Unit> PauseAllCommand       { get; }
    public ReactiveCommand<Unit, Unit> ResumeAllCommand      { get; }
    public ReactiveCommand<Unit, Unit> ClearOutputCommand    { get; }
    public ReactiveCommand<Unit, Unit> ChangeEditorCommand   { get; }
    public ReactiveCommand<Unit, Unit> DefaultEditorCommand  { get; }

    /// <summary>Open a script in the external editor (creates it via the
    /// type dialog when missing). Handled by <see cref="MainWindowViewModel"/>,
    /// which owns editor resolution — same fan-in as the Script Bar pencil.</summary>
    public event Action<string>? EditScriptRequested;

    /// <summary>Open the scripts folder in the OS file manager. Handled by
    /// the host's existing folder-open logic (issue #37 pathing).</summary>
    public event Action? OpenFolderRequested;

    /// <summary>Open an exact file path in the external editor — used when a
    /// bare name can't identify the file (<see cref="EditScriptRequested"/>'s
    /// handler rejects path separators and probes extensions): subfolder
    /// scripts and running rows, whose snapshot carries the real path.</summary>
    public event Action<string>? EditFileRequested;

    /// <summary>"Confirm delete" while the two-step Delete is armed — bound by
    /// the context menu so its Delete item mirrors the toolbar button's state.</summary>
    public string DeleteMenuHeader => _deleteMenuHeader.Value;
    private readonly ObservableAsPropertyHelper<string> _deleteMenuHeader;

    public ScriptsViewModel()
    {
        // File-vs-folder gating shared by the toolbar buttons and the library
        // context menu: Run/Edit/Delete/Copy act on script FILES only.
        var fileSelected = this.WhenAnyValue(x => x.SelectedFile)
                               .Select(f => f is { IsFolder: false });
        var anySelected  = this.WhenAnyValue(x => x.SelectedFile)
                               .Select(f => f is not null);

        RunSelectedCommand    = ReactiveCommand.Create(RunSelected,    fileSelected);
        EditSelectedCommand   = ReactiveCommand.Create(EditSelected,   fileSelected);
        DeleteSelectedCommand = ReactiveCommand.Create(DeleteSelected, fileSelected);
        CopyRunCommandCommand = ReactiveCommand.CreateFromTask(
            () => CopyToClipboardAsync(SelectedFile is { IsFolder: false } f
                                           ? $".{f.RelativeName}" : string.Empty),
            fileSelected);
        CopyPathCommand       = ReactiveCommand.CreateFromTask(
            () => CopyToClipboardAsync(SelectedFile?.FullPath ?? string.Empty),
            fileSelected);
        OpenContainingFolderCommand = ReactiveCommand.Create(OpenContainingFolder, anySelected);

        _deleteMenuHeader = this.WhenAnyValue(x => x.ConfirmingDelete)
            .Select(c => c ? "Confirm delete" : "Delete")
            .ToProperty(this, x => x.DeleteMenuHeader);
        NewScriptCommand      = ReactiveCommand.Create(() => { IsNamingNew = true; });
        CommitNewCommand      = ReactiveCommand.Create(CommitNew);
        CancelNewCommand      = ReactiveCommand.Create(() =>
        { IsNamingNew = false; NewScriptName = string.Empty; });
        RefreshCommand        = ReactiveCommand.Create(RefreshLibrary);
        OpenFolderCommand     = ReactiveCommand.Create(() => OpenFolderRequested?.Invoke());
        StopAllCommand        = ReactiveCommand.Create(() => Process("#stopall"));
        PauseAllCommand       = ReactiveCommand.Create(() => Process("#pauseall"));
        ResumeAllCommand      = ReactiveCommand.Create(() => Process("#resumeall"));
        ClearOutputCommand    = ReactiveCommand.Create(() => Output.Clear());
        ChangeEditorCommand   = ReactiveCommand.CreateFromTask(ChangeEditorAsync);
        DefaultEditorCommand  = ReactiveCommand.Create(() =>
        {
            EditorPathChanged?.Invoke(string.Empty);
            RefreshEditorDisplay();
        });

        // Re-filter as the user types; the throttle keeps a fast typist from
        // re-scanning the folder per keystroke.
        this.WhenAnyValue(x => x.Filter)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshLibrary());

        // Arming Delete is per-selection.
        this.WhenAnyValue(x => x.SelectedFile)
            .Subscribe(_ => ConfirmingDelete = false);
    }

    public void Attach(GenieCore core)
    {
        _core = core;
        RunningScripts.Clear();
        Output.Clear();
        StatusText = "Connected.";
        RefreshLibrary();

        // Seed any scripts already running (auto-connect scripts, etc.).
        foreach (var s in core.Scripts.GetStatuses())
            RunningScripts.Add(MakeRow(s.Name, s.IsJs));

        // Add/remove rows on lifecycle events (immediate feedback); the poll
        // below refreshes the per-row status fields.
        Observable.FromEvent<Action<string>, string>(
                h => core.Scripts.ScriptStarted += h,
                h => core.Scripts.ScriptStarted -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                // Reload semantics — a script that re-starts gets its
                // previous row replaced rather than duplicated.
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (string.Equals(RunningScripts[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
                RunningScripts.Add(MakeRow(name, core.Scripts.IsJavaScript(name)));
            });

        Observable.FromEvent<Action<string>, string>(
                h => core.Scripts.ScriptFinished += h,
                h => core.Scripts.ScriptFinished -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (string.Equals(RunningScripts[i].Name, name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
            });

        Observable.FromEvent<Action<string>, string>(
                h => core.ScriptOutputLine += h,
                h => core.ScriptOutputLine -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(msg =>
            {
                Output.Add(new TextLine(msg, StreamColor.System));
                while (Output.Count > MaxOutputLines)
                    Output.RemoveAt(0);
            });

        // 500 ms status poll — the same pull cadence the performance overlay
        // uses for its running-.js list. The engine ticks on this same UI
        // thread, so reading GetStatuses() here is race-free.
        _poll?.Stop();
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500),
                                    DispatcherPriority.Background,
                                    (_, _) => RefreshStatuses());
        _poll.Start();

        SetupWatcher(core.Scripts.ScriptsDir);
        RefreshEditorDisplay();
    }

    /// <summary>Re-resolve <see cref="EditorDisplay"/> from the host's ladder.</summary>
    public void RefreshEditorDisplay()
        => EditorDisplay = ResolveEditorDescription?.Invoke() ?? "OS default";

    /// <summary>Pick an editor executable via the platform file picker and
    /// persist it as the Display-settings override (the top rung of the Edit
    /// launch ladder — same value the Display Settings dialog edits).</summary>
    private async Task ChangeEditorAsync()
    {
        if (Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime desktop) return;
        if (desktop.MainWindow?.StorageProvider is not { } sp) return;

        var picked = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Choose external script editor",
            AllowMultiple  = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Programs")
                {
                    Patterns = OperatingSystem.IsWindows()
                        ? new[] { "*.exe", "*.bat", "*.cmd" }
                        : new[] { "*" },
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });

        var path = picked?.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        EditorPathChanged?.Invoke(path);
        RefreshEditorDisplay();
    }

    /// <summary>
    /// Auto-refresh the library when the scripts folder changes on disk (new
    /// files from an editor's Save As, git pulls, the Scripts updater, our own
    /// New/Delete). Watcher events arrive on a threadpool thread and come in
    /// bursts, so they only poke a 400 ms UI-thread debounce timer that does
    /// one rescan per burst. Content writes (Changed) are ignored — they don't
    /// alter the tree and editors fire them constantly while a file is open.
    /// </summary>
    private void SetupWatcher(string dir)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(dir)) return;

        _watchDebounce?.Stop();
        _watchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(400),
                                             DispatcherPriority.Background,
                                             (_, _) =>
        {
            _watchDebounce!.Stop();
            RefreshLibrary();
        });

        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            void Poke(object? s, FileSystemEventArgs e) =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _watchDebounce!.Stop();
                    _watchDebounce.Start();
                });
            _watcher.Created += Poke;
            _watcher.Deleted += Poke;
            _watcher.Renamed += Poke;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // A watch failure (exotic mounts, permissions) just means manual
            // Refresh — never let it take the panel down.
            _watcher = null;
            Output.Add(new TextLine($"[scripts] folder watch unavailable: {ex.Message}", StreamColor.System));
        }
    }

    // ── running-list poll ────────────────────────────────────────────────────

    private void RefreshStatuses()
    {
        if (_core is null) return;
        var statuses = _core.Scripts.GetStatuses();

        // Reconcile in place: update matching rows, add missing, drop gone.
        // The Started/Finished events usually keep membership right; this is
        // the safety net (plus the field refresh that events don't carry).
        foreach (var s in statuses)
        {
            var row = FindRow(s.Name) ?? AddRow(s.Name, s.IsJs);
            row.Apply(s);
        }
        for (int i = RunningScripts.Count - 1; i >= 0; i--)
            if (!statuses.Any(s => s.Name.Equals(RunningScripts[i].Name, StringComparison.OrdinalIgnoreCase)))
                RunningScripts.RemoveAt(i);
    }

    private RunningScriptRow? FindRow(string name)
    {
        foreach (var r in RunningScripts)
            if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return r;
        return null;
    }

    private RunningScriptRow AddRow(string name, bool isJs)
    {
        var row = MakeRow(name, isJs);
        RunningScripts.Add(row);
        return row;
    }

    /// <summary>Build a row with its per-script commands pre-baked — all
    /// routed through the command pipeline so behavior matches typed input.</summary>
    private RunningScriptRow MakeRow(string name, bool isJs)
    {
        RunningScriptRow row = null!;
        row = new RunningScriptRow(name, isJs,
              stop:        () => Process($"#stop {name}"),
              pauseResume: () => Process($"#script pauseorresume {name}"),
              reload:      () => Process($"#script reload {name}"),
              vars:        () => Process($"#script vars {name}"),
              trace:       () => Process($"#script trace {name}"),
              // Prefer the snapshot's exact source path (set by the first
              // poll) — a name alone can't disambiguate hunt.cmd vs hunt.js
              // or reach a subfolder script.
              edit:        () =>
              {
                  if (!string.IsNullOrEmpty(row.SourcePath)) EditFileRequested?.Invoke(row.SourcePath);
                  else EditScriptRequested?.Invoke(name);
              },
              setDebug:    lvl => Process($"#script debug {lvl} {name}"));
        return row;
    }

    private void Process(string command) => _core?.Commands.ProcessInput(command);

    // ── library actions ──────────────────────────────────────────────────────

    private void RunSelected()
    {
        if (_core is null || SelectedFile is not { IsFolder: false } f) return;
        // `.name` (the canonical invocation, full pipeline: aliases, echo) for
        // the common case. Start by exact path instead when the bare name can't
        // round-trip it: subfolder scripts (a `.sub/name` start would bake the
        // slash into the script's name, breaking `#stop name`), names with
        // spaces (tokenizer), and basenames that exist with two extensions
        // (`.hunt` would resolve hunt.cmd even when hunt.js was clicked).
        var dir       = Path.GetDirectoryName(f.FullPath)!;
        var baseName  = Path.GetFileNameWithoutExtension(f.FullPath);
        bool ambiguous = ScriptExtensions.Count(
            ext => File.Exists(Path.Combine(dir, baseName + ext))) > 1;
        var args = RunArgs.Trim();
        if (f.RelativeName.Contains('/') || f.RelativeName.Contains(' ') || ambiguous)
            _core.Scripts.TryStartFile(f.FullPath,
                args.Length > 0 ? Genie.Core.Parsing.ArgumentParser.ParseArgs(args) : null);
        else
            _core.Commands.ProcessInput(
                args.Length > 0 ? $".{f.RelativeName} {args}" : $".{f.RelativeName}");
    }

    private void EditSelected()
    {
        if (SelectedFile is not { IsFolder: false } f) return;
        // The host's name-based editor path only accepts bare basenames; the
        // filename keeps its extension so the exact clicked file opens.
        if (f.RelativeName.Contains('/'))
            EditFileRequested?.Invoke(f.FullPath);
        else
            EditScriptRequested?.Invoke(f.Name);
    }

    private void DeleteSelected()
    {
        if (_core is null || SelectedFile is not { IsFolder: false } f) return;
        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;   // button relabels to "Confirm?"
            return;
        }
        ConfirmingDelete = false;
        try
        {
            // Only ever delete inside the scripts folder — the tree is built
            // from it, but re-check in case of symlinks/renames since the scan.
            var root = Path.GetFullPath(_core.Scripts.ScriptsDir);
            var full = Path.GetFullPath(f.FullPath);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                Output.Add(new TextLine($"[scripts] not deleting {full} — outside the scripts folder.", StreamColor.System));
                return;
            }
            File.Delete(full);
            Output.Add(new TextLine($"[scripts] deleted {f.RelativeName}", StreamColor.System));
        }
        catch (Exception ex)
        {
            Output.Add(new TextLine($"[scripts] delete failed: {ex.Message}", StreamColor.System));
        }
        RefreshLibrary();
    }

    private void OpenContainingFolder()
    {
        if (SelectedFile is not { } n) return;
        var dir = n.IsFolder ? n.FullPath : Path.GetDirectoryName(n.FullPath);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            // Reveal-only: createIfMissing: false — a missing dir is a no-op
            // inside OpenDirectory itself (see FileBrowser.OpenDirectory).
            Genie.Core.Runtime.FileBrowser.OpenDirectory(dir, createIfMissing: false);
        }
        catch (Exception ex)
        {
            Output.Add(new TextLine($"[scripts] could not open folder: {ex.Message}", StreamColor.System));
        }
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        if (text.Length == 0) return;
        if (Application.Current?.ApplicationLifetime is
                IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void CommitNew()
    {
        var name = NewScriptName.Trim();
        IsNamingNew = false;
        NewScriptName = string.Empty;
        if (name.Length == 0) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Output.Add(new TextLine($"[scripts] invalid script name: {name}", StreamColor.System));
            return;
        }
        // The host's editor path creates the file (via the .cmd/.js type
        // dialog) when it doesn't exist, then opens it — same as #edit.
        EditScriptRequested?.Invoke(name);
    }

    /// <summary>Rescan the scripts folder into <see cref="Library"/>, applying
    /// <see cref="Filter"/> (case-insensitive substring on the relative name;
    /// folders survive only when a descendant matches, and arrive expanded).
    /// Folder expansion and the current selection survive the rebuild.</summary>
    public void RefreshLibrary()
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpanded(Library, expanded);
        var selectedRel = SelectedFile?.RelativeName;

        Library.Clear();
        if (_core is null) return;
        var root = _core.Scripts.ScriptsDir;
        if (!Directory.Exists(root)) return;
        var filter = Filter.Trim();
        foreach (var node in ScanFolder(root, root, filter))
            Library.Add(node);

        ReapplyExpansion(Library, expanded);
        if (selectedRel is not null && FindNode(Library, selectedRel) is { } again)
            SelectedFile = again;
    }

    private static void CollectExpanded(IEnumerable<ScriptFileNode> nodes, HashSet<string> expanded)
    {
        foreach (var n in nodes)
        {
            if (n.IsFolder && n.IsExpanded) expanded.Add(n.RelativeName);
            CollectExpanded(n.Children, expanded);
        }
    }

    private static void ReapplyExpansion(IEnumerable<ScriptFileNode> nodes, HashSet<string> expanded)
    {
        foreach (var n in nodes)
        {
            // OR with the scan's own state so filter auto-expansion survives.
            if (n.IsFolder && expanded.Contains(n.RelativeName)) n.IsExpanded = true;
            ReapplyExpansion(n.Children, expanded);
        }
    }

    private static ScriptFileNode? FindNode(IEnumerable<ScriptFileNode> nodes, string relativeName)
    {
        foreach (var n in nodes)
        {
            if (!n.IsFolder && n.RelativeName.Equals(relativeName, StringComparison.OrdinalIgnoreCase))
                return n;
            if (FindNode(n.Children, relativeName) is { } hit) return hit;
        }
        return null;
    }

    private static readonly string[] ScriptExtensions = [".cmd", ".inc", ".js"];

    private static List<ScriptFileNode> ScanFolder(string dir, string root, string filter)
    {
        var nodes = new List<ScriptFileNode>();
        IEnumerable<string> subdirs, files;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            files   = Directory.EnumerateFiles(dir)
                               .Where(f => ScriptExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                               .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch { return nodes; /* unreadable folder — skip */ }

        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.')) continue;   // .git etc.
            var children = ScanFolder(sub, root, filter);
            if (children.Count == 0 && filter.Length > 0) continue;
            nodes.Add(new ScriptFileNode(name, sub, RelName(sub, root), isFolder: true, children)
            {
                IsExpanded = filter.Length > 0,   // searching auto-expands hits
            });
        }
        foreach (var file in files)
        {
            var rel = RelName(file, root);
            if (filter.Length > 0 &&
                !rel.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            nodes.Add(new ScriptFileNode(Path.GetFileName(file), file, rel, isFolder: false, []));
        }
        return nodes;
    }

    /// <summary>Path relative to the scripts root, forward-slashed, extension
    /// stripped for files — the exact <c>.name</c> invocation form.</summary>
    private static string RelName(string path, string root)
    {
        var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
        return Directory.Exists(path)
            ? rel
            : Path.ChangeExtension(rel, null) ?? rel;
    }
}

/// <summary>One node of the Script Manager's library tree: a folder or a
/// runnable script file. <see cref="RelativeName"/> is the <c>.name</c>
/// invocation form (forward slashes, no extension).</summary>
public sealed class ScriptFileNode : ReactiveObject
{
    public ScriptFileNode(string name, string fullPath, string relativeName,
                          bool isFolder, List<ScriptFileNode> children)
    {
        Name         = name;
        FullPath     = fullPath;
        RelativeName = relativeName;
        IsFolder     = isFolder;
        Children     = new ObservableCollection<ScriptFileNode>(children);
        IsJs         = !isFolder && fullPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase);
    }

    public string Name         { get; }
    public string FullPath     { get; }
    public string RelativeName { get; }
    public bool   IsFolder     { get; }
    public bool   IsJs         { get; }
    public ObservableCollection<ScriptFileNode> Children { get; }

    [Reactive] public bool IsExpanded { get; set; }
}

/// <summary>
/// One row in the running-scripts list. Mutable + reactive (unlike the old
/// record) because the 500 ms status poll updates its fields in place; carries
/// its own per-script commands so the AXAML bindings need no parameters.
/// Top-level so compiled <c>x:DataType</c> resolution finds it.
/// </summary>
public sealed class RunningScriptRow : ReactiveObject
{
    private readonly ObservableAsPropertyHelper<string> _pauseGlyph;
    private readonly ObservableAsPropertyHelper<string> _pauseTip;

    public RunningScriptRow(string name, bool isJavaScript,
                            Action stop, Action pauseResume, Action reload,
                            Action vars, Action trace, Action edit,
                            Action<string> setDebug)
    {
        Name          = name;
        IsJavaScript  = isJavaScript;
        StopCommand        = ReactiveCommand.Create(stop);
        PauseResumeCommand = ReactiveCommand.Create(pauseResume);
        ReloadCommand      = ReactiveCommand.Create(reload);
        VarsCommand        = ReactiveCommand.Create(vars);
        TraceCommand       = ReactiveCommand.Create(trace);
        EditCommand        = ReactiveCommand.Create(edit);
        SetDebugCommand    = ReactiveCommand.Create<string>(lvl => setDebug(lvl ?? "0"));

        _pauseGlyph = this.WhenAnyValue(x => x.Paused)
            .Select(p => p ? "▶" : "⏸")
            .ToProperty(this, x => x.PauseGlyph);
        _pauseTip = this.WhenAnyValue(x => x.Paused)
            .Select(p => p ? "Resume" : "Pause")
            .ToProperty(this, x => x.PauseTip);
    }

    public string Name         { get; }
    public bool   IsJavaScript { get; }

    [Reactive] public bool   Paused         { get; set; }
    [Reactive] public string State          { get; set; } = "Running";
    [Reactive] public string Summary        { get; set; } = string.Empty;
    [Reactive] public string DetailText     { get; set; } = string.Empty;
    [Reactive] public int    DebugLevel     { get; set; }
    [Reactive] public bool   PendingReload  { get; set; }
    public string SourcePath { get; private set; } = string.Empty;

    public string PauseGlyph => _pauseGlyph.Value;
    public string PauseTip   => _pauseTip.Value;

    public ReactiveCommand<Unit, Unit>   StopCommand        { get; }
    public ReactiveCommand<Unit, Unit>   PauseResumeCommand { get; }
    public ReactiveCommand<Unit, Unit>   ReloadCommand      { get; }
    public ReactiveCommand<Unit, Unit>   VarsCommand        { get; }
    public ReactiveCommand<Unit, Unit>   TraceCommand       { get; }
    public ReactiveCommand<Unit, Unit>   EditCommand        { get; }
    public ReactiveCommand<string, Unit> SetDebugCommand    { get; }

    /// <summary>Copy the polled snapshot into the row's bindable fields.</summary>
    public void Apply(ScriptStatus s)
    {
        Paused        = s.Paused;
        State         = s.State;
        DebugLevel    = s.DebugLevel;
        PendingReload = s.PendingReload;
        SourcePath    = s.SourcePath;

        var elapsed = FmtElapsed(s.ElapsedSeconds);
        Summary = s.IsJs
            ? $"{s.State} · {elapsed}"
            : $"{s.State} · L{s.CurrentLine} · {elapsed}"
              + (s.DebugLevel > 0 ? $" · dbg {s.DebugLevel}" : "")
              + (s.PendingReload ? " · reload pending" : "");
        DetailText = s.IsJs
            ? $"{s.State} · running {elapsed}"
            : $"L{s.CurrentLine}: {s.State}"
              + (s.PendingMatchCount > 0 ? $" · {s.PendingMatchCount} patterns pending" : "")
              + (s.GosubDepth > 0 ? $" · gosub depth {s.GosubDepth}" : "")
              + (s.PendingReload ? " · reload pending" : "");
    }

    private static string FmtElapsed(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
