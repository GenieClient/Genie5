using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia.Data.Converters;
using Genie.Core;
using Genie.Core.Import;
using Genie.Core.Persistence;
using Genie.Core.Runtime;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Value converter for the import dialog: returns true when the bound
/// int is strictly positive. Used to gate the "— N rules" count text so
/// it only shows for files that actually contain rules (a probed count
/// of -1 = file not present, 0 = file present but empty; both render as
/// "no count shown").
/// </summary>
public sealed class PositiveIntConverter : IValueConverter
{
    public static readonly PositiveIntConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// View-model for the "Import from Genie 4" dialog. Walks the user through:
/// (1) picking a source directory of Genie-4 cfg files, (2) previewing how
/// many rules each file contains, (3) choosing which types to import and how
/// they should merge with existing rules, (4) choosing whether the imported
/// rules apply globally (all profiles) or only to the connected character,
/// (5) running the import and seeing a results summary.
///
/// <para>
/// Everything heavy lives in <see cref="Genie4Importer"/> in Core — this VM
/// is just the wiring + UI state.
/// </para>
/// </summary>
public sealed class Genie4ImportViewModel : ReactiveObject
{
    /// <summary>Static handle to the int>0 converter so XAML can <c>{x:Static}</c> it.</summary>
    public static PositiveIntConverter PositiveConverter => PositiveIntConverter.Instance;

    private readonly GenieCore _core;
    private readonly string    _globalConfigDir;
    private readonly string?   _profileConfigDir;
    private readonly string?   _connectedCharacterName;
    private readonly Settings.LayoutStore? _globalLayouts;
    private readonly Settings.LayoutStore? _profileLayouts;

    // ── Source ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Path the user has selected (or auto-detected) for the Genie-4 cfg
    /// files. The watchdog re-runs Probe whenever this changes so the
    /// counts stay current.
    /// </summary>
    [Reactive] public string SourcePath { get; set; } = "";

    /// <summary>
    /// Friendly status string for the source-path row: "Found X cfg files"
    /// or "No Genie 4 install found at default locations — browse to your
    /// Genie 4 Config folder." Updates on probe.
    /// </summary>
    [Reactive] public string SourceStatus { get; private set; } = "";

    // ── Per-type checkboxes ─────────────────────────────────────────────────

    [Reactive] public bool ImportHighlights  { get; set; } = true;
    [Reactive] public bool ImportTriggers    { get; set; } = true;
    [Reactive] public bool ImportSubstitutes { get; set; } = true;
    [Reactive] public bool ImportGags        { get; set; } = true;
    [Reactive] public bool ImportAliases     { get; set; } = true;
    [Reactive] public bool ImportMacros      { get; set; } = true;
    [Reactive] public bool ImportVariables   { get; set; } = true;
    [Reactive] public bool ImportClasses     { get; set; } = true;
    [Reactive] public bool ImportNames       { get; set; } = true;
    [Reactive] public bool ImportPresets     { get; set; } = true;

    /// <summary>
    /// Off by default, unlike every other type. Settings are app-wide
    /// rather than per-profile, and a Genie 4 settings.cfg carries keys
    /// Genie 5 either renames or cannot honour at all — so this one is a
    /// deliberate choice rather than part of "bring everything over".
    /// </summary>
    [Reactive] public bool ImportSettings    { get; set; }

    /// <summary>Genie 4 saved window layouts (<c>*.layout</c>, public #319),
    /// converted to windowed-mode Genie 5 layouts named "G4 …". Written to the
    /// same Global / per-character target as the rules.</summary>
    [Reactive] public bool ImportLayouts     { get; set; } = true;

    // Per-type counts populated by Probe. Shown next to each checkbox in
    // the dialog ("Highlights — 47 rules found"). -1 means "not yet probed
    // / no file present"; 0 means "file exists but no rules" — both render
    // as "no rules" in the dialog but the distinction may matter to a
    // future iteration.
    [Reactive] public int CountHighlights   { get; private set; } = -1;
    [Reactive] public int CountTriggers     { get; private set; } = -1;
    [Reactive] public int CountSubstitutes  { get; private set; } = -1;
    [Reactive] public int CountGags         { get; private set; } = -1;
    [Reactive] public int CountAliases      { get; private set; } = -1;
    [Reactive] public int CountMacros       { get; private set; } = -1;
    [Reactive] public int CountVariables    { get; private set; } = -1;
    [Reactive] public int CountClasses      { get; private set; } = -1;
    [Reactive] public int CountNames        { get; private set; } = -1;
    [Reactive] public int CountPresets      { get; private set; } = -1;
    [Reactive] public int CountSettings     { get; private set; } = -1;
    [Reactive] public int CountLayouts      { get; private set; } = -1;

    // ── Target (Global vs Profile-Specific) ────────────────────────────────

    /// <summary>
    /// True when the imported rules should land in the shared root config
    /// dir (every profile inherits). False when they go only into the
    /// connected character's per-profile config dir. Defaults to false
    /// when a profile is connected (safer), true when no profile (because
    /// per-profile makes no sense without a profile).
    /// </summary>
    [Reactive] public bool TargetIsGlobal { get; set; }

    /// <summary>
    /// Convenience inverse so the second radio button can two-way-bind.
    /// </summary>
    public bool TargetIsProfile
    {
        get => !TargetIsGlobal;
        set => TargetIsGlobal = !value;
    }

    /// <summary>
    /// Disables the per-profile radio when there's no connected character —
    /// imports HAVE to go to the global dir in that case (no profile dir
    /// exists yet).
    /// </summary>
    public bool ProfileTargetEnabled => _profileConfigDir is not null;

    /// <summary>
    /// Friendly label for the per-profile radio: "This character (Renucci)"
    /// or "This character (none connected)" when disabled.
    /// </summary>
    public string ProfileTargetLabel =>
        _profileConfigDir is null
            ? "This character (none connected — connect first to enable)"
            : $"This character only ({_connectedCharacterName ?? "current"})";

    // ── Import mode (Merge / Replace / AddOnly) ────────────────────────────

    [Reactive] public ImportMode Mode { get; set; } = ImportMode.Merge;

    // Convenience boolean bindings for the three radios (Avalonia's RadioButton
    // works best with separate bool properties rather than enum binding).
    public bool ModeIsMerge   { get => Mode == ImportMode.Merge;   set { if (value) Mode = ImportMode.Merge;   } }
    public bool ModeIsReplace { get => Mode == ImportMode.Replace; set { if (value) Mode = ImportMode.Replace; } }
    public bool ModeIsAddOnly { get => Mode == ImportMode.AddOnly; set { if (value) Mode = ImportMode.AddOnly; } }

    // ── Results ────────────────────────────────────────────────────────────

    /// <summary>Multi-line result text after a successful import.</summary>
    [Reactive] public string ResultMessage { get; private set; } = "";

    [Reactive] public bool IsBusy { get; private set; }

    // ── Commands ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit>  BrowseCommand { get; }
    public ReactiveCommand<Unit, Unit>  ProbeCommand  { get; }
    public ReactiveCommand<Unit, bool>  ImportCommand { get; }
    public ReactiveCommand<Unit, bool>  CancelCommand { get; }

    /// <summary>
    /// Raised when the Browse button is clicked. The view handles it
    /// (folder picker dialogs are easier from code-behind than VM).
    /// </summary>
    public event Action? BrowseRequested;

    public Genie4ImportViewModel(GenieCore core, string globalConfigDir,
                                 string? profileConfigDir, string? connectedCharacterName,
                                 Settings.LayoutStore? globalLayouts = null,
                                 Settings.LayoutStore? profileLayouts = null)
    {
        _core                   = core;
        _globalConfigDir        = globalConfigDir;
        _profileConfigDir       = profileConfigDir;
        _connectedCharacterName = connectedCharacterName;
        _globalLayouts          = globalLayouts;
        _profileLayouts         = profileLayouts;

        // Default the target: per-character if a profile is connected
        // (safer — doesn't bleed into other characters), global otherwise
        // (no choice — there's no profile dir to write to).
        TargetIsGlobal = _profileConfigDir is null;

        BrowseCommand = ReactiveCommand.Create(() =>
        {
            BrowseRequested?.Invoke();
        });

        ProbeCommand = ReactiveCommand.Create(Probe);
        ProbeCommand.ThrownExceptions.Subscribe(ex =>
            SourceStatus = $"Probe failed: {ex.Message}");

        // Re-probe whenever the source path changes so the counts stay
        // current as the user edits or browses.
        this.WhenAnyValue(x => x.SourcePath)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.TaskpoolScheduler)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => Probe());

        var canImport = this.WhenAnyValue(x => x.SourcePath, x => x.IsBusy,
            (path, busy) => !busy && !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
        ImportCommand = ReactiveCommand.CreateFromTask(RunImportAsync, canImport);
        ImportCommand.ThrownExceptions.Subscribe(ex =>
            ResultMessage = $"Import failed: {ex.Message}");

        CancelCommand = ReactiveCommand.Create(() => false);

        // Auto-detect the most likely Genie 4 install location on first
        // open. Setting SourcePath fires the throttled Probe above.
        SourcePath = AutoDetectGenie4ConfigDir();
    }

    /// <summary>
    /// Walks the standard install paths for Genie 4 looking for a Config
    /// folder with at least one recognised .cfg file. Returns the first
    /// match, or empty string if no install detected.
    /// <para>
    /// Standard Genie 4 locations on Windows (per the community wiki):
    ///   - <c>%APPDATA%\Genie Client 4\Config\</c> (per-user, most common)
    ///   - <c>%USERPROFILE%\Documents\Genie Client 4\Config\</c> (older default)
    ///   - <c>%LOCALAPPDATA%\Genie Client 4\Config\</c> (rare)
    /// </para>
    /// </summary>
    private static string AutoDetectGenie4ConfigDir()
    {
        // Linux / macOS users won't have a Windows-shaped Genie 4 install;
        // empty source → user has to browse to a copied cfg directory.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "";

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Genie Client 4", "Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),    "Genie Client 4", "Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genie Client 4", "Config"),
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.cfg").Any())
                return c;
        }
        return "";
    }

    /// <summary>
    /// Re-runs <see cref="Genie4Importer.ProbeDirectory"/> against the
    /// current source path and updates every per-type count + the
    /// source-status string.
    /// </summary>
    private void Probe()
    {
        // Reset all counts to "not present" — we set positive values only
        // for files that exist.
        CountHighlights   = -1;
        CountTriggers     = -1;
        CountSubstitutes  = -1;
        CountGags         = -1;
        CountAliases      = -1;
        CountMacros       = -1;
        CountVariables    = -1;
        CountClasses      = -1;
        CountLayouts      = -1;

        if (string.IsNullOrWhiteSpace(SourcePath) || !Directory.Exists(SourcePath))
        {
            SourceStatus = "No Genie 4 install found — Browse to your Genie 4 Config folder.";
            return;
        }

        var counts = Genie4Importer.ProbeDirectory(SourcePath);
        if (counts.TryGetValue(Genie4ImportTypes.Highlights,  out var n)) CountHighlights   = n;
        if (counts.TryGetValue(Genie4ImportTypes.Triggers,    out n))      CountTriggers     = n;
        if (counts.TryGetValue(Genie4ImportTypes.Substitutes, out n))      CountSubstitutes  = n;
        if (counts.TryGetValue(Genie4ImportTypes.Gags,        out n))      CountGags         = n;
        if (counts.TryGetValue(Genie4ImportTypes.Aliases,     out n))      CountAliases      = n;
        if (counts.TryGetValue(Genie4ImportTypes.Macros,      out n))      CountMacros       = n;
        if (counts.TryGetValue(Genie4ImportTypes.Variables,   out n))      CountVariables    = n;
        if (counts.TryGetValue(Genie4ImportTypes.Classes,     out n))      CountClasses      = n;
        if (counts.TryGetValue(Genie4ImportTypes.Names,       out n))      CountNames        = n;
        if (counts.TryGetValue(Genie4ImportTypes.Presets,     out n))      CountPresets      = n;
        if (counts.TryGetValue(Genie4ImportTypes.Settings,    out n))      CountSettings     = n;
        var layoutFiles = Settings.Genie4LayoutConverter.FindLayoutFiles(SourcePath).Count;
        if (layoutFiles > 0) CountLayouts = layoutFiles;

        var totalRules = counts.Values.Sum();
        SourceStatus = totalRules > 0
            ? $"Found {totalRules} rules across {counts.Count} cfg files in {Path.GetFileName(SourcePath)}."
            : $"No Genie 4 rules found in {Path.GetFileName(SourcePath)}.";
    }

    /// <summary>
    /// Runs the import: build the type flags from the checkboxes, call
    /// <see cref="Genie4Importer.ImportDirectory"/> to mutate the live
    /// engines in memory, then save each engine back to disk in the
    /// chosen target directory so the rules survive a restart.
    /// </summary>
    private async Task<bool> RunImportAsync()
    {
        IsBusy = true;
        ResultMessage = "";
        try
        {
            // ── Build the type-flag mask from checkboxes ─────────────────
            var types = Genie4ImportTypes.None;
            if (ImportHighlights)  types |= Genie4ImportTypes.Highlights;
            if (ImportTriggers)    types |= Genie4ImportTypes.Triggers;
            if (ImportSubstitutes) types |= Genie4ImportTypes.Substitutes;
            if (ImportGags)        types |= Genie4ImportTypes.Gags;
            if (ImportAliases)     types |= Genie4ImportTypes.Aliases;
            if (ImportMacros)      types |= Genie4ImportTypes.Macros;
            if (ImportVariables)   types |= Genie4ImportTypes.Variables;
            if (ImportClasses)     types |= Genie4ImportTypes.Classes;
            if (ImportNames)       types |= Genie4ImportTypes.Names;
            if (ImportPresets)     types |= Genie4ImportTypes.Presets;
            if (ImportSettings)    types |= Genie4ImportTypes.Settings;

            bool layouts = ImportLayouts && LayoutTarget is not null;
            if (types == Genie4ImportTypes.None && !layouts)
            {
                ResultMessage = "Nothing selected to import.";
                return false;
            }

            // ── Run the import on a worker thread — file I/O + regex ────
            // shouldn't block the UI even for a few hundred rules.
            var ctx = new Genie4ImportContext
            {
                Aliases     = _core.Aliases,
                Triggers    = _core.Triggers,
                Highlights  = _core.Highlights,
                Substitutes = _core.Substitutes,
                Gags        = _core.Gags,
                Macros      = _core.Macros,
                Names       = _core.NameHighlights,
                Presets     = _core.Presets,
                Variables   = _core.Variables.Store,
                Classes     = _core.Classes,
                Settings    = _core.Config,
            };

            var result = types == Genie4ImportTypes.None
                ? null
                : await Task.Run(() => Genie4Importer.ImportDirectory(SourcePath, ctx, Mode, types));

            // ── Persist the merged engine state to the target dir ───────
            // so the imported rules survive a restart. The choice between
            // global and per-profile is the target-dir difference. MUST be
            // cfg format (CfgFormat): the engine replays these files through
            // the command pipeline at connect, so a JSON serialization here
            // isn't just unreadable — its lines get dispatched as commands.
            var targetDir = TargetIsGlobal ? _globalConfigDir : _profileConfigDir!;
            Directory.CreateDirectory(targetDir);

            if (ImportHighlights)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "highlights.cfg"), CfgFormat.HighlightLines(_core.Highlights.Rules));
            if (ImportTriggers)
                // TriggerEngineFinal exposes its rule list as `.Triggers`
                // (not `.Rules` like the other engines).
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "triggers.cfg"), CfgFormat.TriggerLines(_core.Triggers.Triggers));
            if (ImportSubstitutes)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "substitutes.cfg"), CfgFormat.SubstituteLines(_core.Substitutes.Rules));
            if (ImportGags)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "gags.cfg"), CfgFormat.GagLines(_core.Gags.Rules));
            if (ImportAliases)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "aliases.cfg"), CfgFormat.AliasLines(_core.Aliases.Aliases));
            if (ImportMacros)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "macros.cfg"), CfgFormat.MacroLines(_core.Macros.Rules));
            if (ImportVariables)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "variables.cfg"), CfgFormat.VariableLines(_core.Variables.Store));
            if (ImportClasses)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "classes.cfg"), CfgFormat.ClassLines(_core.Classes.GetAll()));
            if (ImportNames)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "names.cfg"), CfgFormat.NameLines(_core.NameHighlights.Rules));
            if (ImportPresets)
                ConfigPersistence.WriteLines(Path.Combine(targetDir, "presets.cfg"), CfgFormat.PresetLines(_core.Presets.Presets));
            // Settings are app-wide and live in the global settings.cfg that
            // GenieConfig owns, so they are NOT written into the per-profile
            // target dir here - ImportSettings applied them to the live config
            // and the normal config save persists them.

            var layoutSummary = layouts ? ImportLayoutFiles(LayoutTarget!) : "";
            ResultMessage = result is null
                ? layoutSummary.TrimEnd()
                : (BuildResultSummary(result, targetDir)
                   + (layoutSummary.Length > 0 ? Environment.NewLine + Environment.NewLine + layoutSummary : "")).TrimEnd();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The layout store the chosen target maps to — the connected
    /// character's when per-character, else the global one.</summary>
    private Settings.LayoutStore? LayoutTarget =>
        TargetIsGlobal ? _globalLayouts : (_profileLayouts ?? _globalLayouts);

    /// <summary>Convert and save every Genie 4 <c>.layout</c> file (#319). Add
    /// Only keeps an existing "G4 …" layout; Merge and Replace overwrite it.
    /// Returns the summary block for the dialog footer.</summary>
    public string ImportLayoutFiles(Settings.LayoutStore store)
    {
        var files = Settings.Genie4LayoutConverter.FindLayoutFiles(SourcePath);
        if (files.Count == 0) return "Layouts: no .layout files found.";

        var sb = new System.Text.StringBuilder();
        int saved = 0, kept = 0, unreadable = 0;
        var skippedWindows = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var file in files)
        {
            var name = Settings.Genie4LayoutConverter.ImportedName(file);
            string xml;
            try { xml = File.ReadAllText(file); }
            catch { unreadable++; continue; }

            var (layout, report) = Settings.Genie4LayoutConverter.Convert(xml, name);
            if (layout is null) { unreadable++; continue; }
            foreach (var s in report.Skipped) skippedWindows.Add(s);

            if (Mode == ImportMode.AddOnly && store.HasUserCopy(name)) { kept++; continue; }
            store.Save(layout);
            saved++;
            names.Add($"{name} ({report.Converted} windows)");
        }

        sb.AppendLine($"Layouts: {saved} imported"
                      + (kept > 0 ? $", {kept} kept (already there)" : "")
                      + (unreadable > 0 ? $", {unreadable} unreadable" : "") + ".");
        foreach (var n in names) sb.AppendLine($"  {n}");
        if (skippedWindows.Count > 0)
            sb.AppendLine($"  Windows with no Genie 5 panel (skipped): {string.Join(", ", skippedWindows)}");
        if (saved > 0)
            sb.AppendLine("  They open in windowed (MDI) mode — Layout ▸ Load. Fonts, colours and IfClosed are not part of a layout and were not converted.");
        return sb.ToString();
    }

    /// <summary>
    /// Pretty-print the import outcome for the dialog footer. Shows
    /// per-type imported/skipped counts and the final on-disk location
    /// so the user knows where the rules actually live.
    /// </summary>
    private string BuildResultSummary(ImportAllResult result, string targetDir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Imported into: {targetDir}");
        sb.AppendLine($"Target scope: {(TargetIsGlobal ? "GLOBAL (all characters)" : "PER-CHARACTER (current profile only)")}");
        sb.AppendLine($"Mode: {Mode}");
        sb.AppendLine();
        // Every type the importer actually ran, including Names and Presets —
        // they were missing from this list, so their counts never surfaced.
        foreach (var (label, r) in result.All)
            AppendLine(sb, label, r);

        if (result.MissingFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Missing files (skipped): {string.Join(", ", result.MissingFiles)}");
        }

        AppendDropped(sb, result);
        return sb.ToString().TrimEnd();
    }

    private static void AppendLine(System.Text.StringBuilder sb, string label, ImportResult r)
    {
        if (r.Imported == 0 && r.Skipped == 0) return;

        // Separate the skips that lost a rule from the ones that were
        // expected, so "12 skipped" can no longer hide "12 destroyed".
        var dropped = r.DroppedCount;
        var detail  = dropped == 0
            ? $"{r.Skipped} skipped"
            : $"{r.Skipped} skipped ({dropped} NOT imported)";
        sb.AppendLine($"  {label}: {r.Imported} imported, {detail}");
    }

    /// <summary>
    /// List the lines that carried a rule and lost it, with the reason and the
    /// line number so the user can go look. Capped per type so one malformed
    /// file cannot bury the rest of the report.
    /// </summary>
    private static void AppendDropped(System.Text.StringBuilder sb, ImportAllResult result)
    {
        if (!result.AnyDropped) return;

        const int PerTypeCap = 10;

        sb.AppendLine();
        sb.AppendLine($"NOT IMPORTED — {result.DroppedCount} rule(s) were in the files but could not be read:");

        foreach (var (label, r) in result.All)
        {
            var dropped = r.Dropped.ToList();
            if (dropped.Count == 0) continue;

            sb.AppendLine();
            sb.AppendLine($"  {label} ({dropped.Count}):");
            foreach (var d in dropped.Take(PerTypeCap))
                sb.AppendLine($"    {d}");
            if (dropped.Count > PerTypeCap)
                sb.AppendLine($"    … and {dropped.Count - PerTypeCap} more");
        }
    }

    /// <summary>
    /// Called by the view's Browse handler after the user picks a folder
    /// in the OS dialog. Setting SourcePath fires the throttled probe
    /// automatically.
    /// </summary>
    public void SetSourcePathFromBrowse(string path)
    {
        SourcePath = path;
    }
}
