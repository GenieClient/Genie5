using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.Core;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Config;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using Genie.Core.Macros;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Genie.Core.Profiles;
using Genie.Core.Runtime;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Top-level Configuration dialog VM. Profile-scoped: every config file lives
/// under <c>Profiles/{Char}-{Acct}/</c> (the same per-character dir Core loads
/// from at connect) so each character can have its own highlights, triggers,
/// aliases, etc.
///
/// <para>Engine selection rules:</para>
/// <list type="bullet">
///   <item>When <see cref="SelectedProfile"/> equals the connected profile,
///         engine accessors return the LIVE engines from <see cref="GenieCore"/>
///         — edits take effect immediately on incoming game text.</item>
///   <item>When <see cref="SelectedProfile"/> is a different profile (or null),
///         accessors return draft engines pre-loaded from that profile's
///         directory on disk. Edits save to disk and pick up on the next
///         connect to that profile.</item>
/// </list>
///
/// <para>Switching <see cref="SelectedProfile"/> in the dropdown clears all
/// draft engines so the next access re-loads against the new profile's files.</para>
/// </summary>
public class ConfigurationViewModel : ReactiveObject
{
    private readonly GenieCore?           _core;
    private readonly string               _configRoot;
    private readonly Func<ConnectionProfile?, string> _profileDirResolver;
    private readonly ConnectionProfile?   _connectedProfile;
    private readonly WindowSettingsStore  _windowSettings;
    private readonly DisplaySettings?     _display;
    private readonly string?              _displayPath;
    private readonly PersistenceService   _persistence = new();

    /// <summary>List of saved profiles for the picker dropdown. Plus a synthetic
    /// "(no profile / global)" entry when there's no connected profile so users
    /// with legacy global config can still see and edit it.</summary>
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    /// <summary>Which profile is currently being edited. Driving signal for
    /// path scoping + live-vs-draft engine selection.</summary>
    [Reactive] public ConnectionProfile? SelectedProfile { get; set; }

    /// <summary>Display string ("Editing: Renucci" or "Editing: (no profile)").</summary>
    public extern string EditingLabel { [ObservableAsProperty] get; }

    /// <summary>True when the picker is on the same profile that's currently
    /// connected — edits in the dialog write directly to the live engines.</summary>
    public extern bool IsEditingConnectedProfile { [ObservableAsProperty] get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public event Action? RequestClose;

    public ConfigurationViewModel(
        GenieCore?           core,
        string               configRoot,
        ProfileStore         profiles,
        ConnectionProfile?   connectedProfile,
        WindowSettingsStore  windowSettings,
        DisplaySettings?     display     = null,
        string?              displayPath = null,
        Func<ConnectionProfile?, string>? profileDirResolver = null)
    {
        _core             = core;
        _configRoot       = configRoot;
        // The host's resolver (MainWindowViewModel.GetProfileConfigDir) keeps
        // this dialog writing to the SAME per-character dir Core loads from.
        // Fallback (tests / design-time): global Config for null, else the
        // per-character path under the default root.
        _profileDirResolver = profileDirResolver ?? (p => p is null
            ? configRoot
            : Genie.Core.Config.GenieConfig.ProfileDirFor(
                  Path.GetDirectoryName(Path.GetFullPath(configRoot))!,
                  p.CharacterName, p.AccountName));
        _connectedProfile = connectedProfile;
        _windowSettings   = windowSettings;
        _display          = display;
        _displayPath      = displayPath;

        foreach (var p in profiles.Profiles) Profiles.Add(p);

        // Sensible default: editing the connected profile if there is one,
        // otherwise the first saved profile, otherwise null (legacy global mode).
        SelectedProfile = connectedProfile ?? Profiles.FirstOrDefault();

        // Reset drafts whenever the editing target changes so the next engine
        // accessor re-loads from the new profile's directory.
        this.WhenAnyValue(x => x.SelectedProfile)
            .Subscribe(_ => ClearDrafts());

        this.WhenAnyValue(x => x.SelectedProfile)
            .Select(p => p is null ? "Editing: (no profile / global)" : $"Editing: {p.Name}")
            .ToPropertyEx(this, x => x.EditingLabel);

        this.WhenAnyValue(x => x.SelectedProfile)
            .Select(p => p is not null && _connectedProfile is not null && p.Id == _connectedProfile.Id)
            .ToPropertyEx(this, x => x.IsEditingConnectedProfile);

        CloseCommand = ReactiveCommand.Create(() => { RequestClose?.Invoke(); });
    }

    // ── Engine refs — live when on the connected profile, draft otherwise ────

    private bool EditingConnected =>
        _connectedProfile is not null && SelectedProfile?.Id == _connectedProfile.Id;

    public HighlightEngine?     HighlightEngine     => EditingConnected ? _core?.Highlights     : GetDraftHighlights();
    public NameHighlightEngine? NameHighlightEngine => EditingConnected ? _core?.NameHighlights : GetDraftNames();
    public PresetEngine?        PresetEngine        => EditingConnected ? _core?.Presets        : GetDraftPresets();
    public TriggerEngineFinal?  TriggerEngine       => EditingConnected ? _core?.Triggers       : GetDraftTriggers();
    public SubstituteEngine?    SubstituteEngine    => EditingConnected ? _core?.Substitutes    : GetDraftSubstitutes();
    public GagEngine?           GagEngine           => EditingConnected ? _core?.Gags           : GetDraftGags();
    public AliasEngine?         AliasEngine         => EditingConnected ? _core?.Aliases        : GetDraftAliases();
    public MacroEngine?         MacroEngine         => EditingConnected ? _core?.Macros         : GetDraftMacros();
    public ClassEngine?         ClassEngine         => EditingConnected ? _core?.Classes        : GetDraftClasses();
    public VariableStore?       VariableStore       => EditingConnected ? _core?.Variables.Store : GetDraftVariables();

    /// <summary>
    /// Per-window display settings. Currently always the live app-wide store
    /// — per-profile draft layouts could be added later but in practice users
    /// expect consistent window appearance regardless of which character is
    /// active.
    /// </summary>
    public WindowSettingsStore WindowSettings => _windowSettings;

    /// <summary>
    /// App-wide display / window-behaviour settings (Always on Top, …) — the
    /// live <see cref="DisplaySettings"/> instance the main window binds to, so
    /// edits here update the window and the Layout-menu checkmarks immediately.
    /// Stored in <c>display.json</c>, not per-profile. Null only when the dialog
    /// is opened without one (defensive; the app always supplies it).
    /// </summary>
    public DisplaySettings? Display => _display;

    /// <summary>
    /// Global script-engine settings (script/command characters, timeout,
    /// GoSub depth, connect script, …). These live on <see cref="GenieConfig"/>
    /// in <c>settings.cfg</c> — app-wide, not per-profile — so the value is the
    /// same regardless of <see cref="SelectedProfile"/>. Null until a core is
    /// connected; the Scripts panel disables itself in that case.
    /// </summary>
    public GenieConfig? ScriptConfig => _core?.Config;

    /// <summary>Optional TTS hooks handed in by the main window (which owns
    /// the TtsService): speak a sample line from the Text-to-Speech tab's
    /// Test button, and drop the cached synth engine after a voice change.
    /// Null when TTS isn't available — the tab disables the Test button.</summary>
    public Action<string>? SpeakSample { get; set; }

    /// <inheritdoc cref="SpeakSample"/>
    public Action? TtsVoiceChanged { get; set; }

    // ── Persistence hooks (called by every panel after an edit) ──────────────

    public void OnHighlightsChanged()
    {
        var engine = HighlightEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("highlights.json", engine.Rules,
            r => r.Scope, r => r.Pattern, DiskGlobal().Highlights.Rules,
            (path, subset) => _persistence.SaveHighlights(path, subset));
        SyncCfgSplit("highlights.cfg", engine.Rules, r => r.Scope, mergedGlobal, CfgFormat.HighlightLines);
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnNamesChanged()
    {
        var engine = NameHighlightEngine;
        if (engine is null) return;
        List<NameRule> diskGlobal;
        try
        {
            diskGlobal = _persistence.LoadNames(Path.Combine(_configRoot, "names.json"))
                .Select(m => new NameRule(m.Name, m.ForegroundColor, m.BackgroundColor)
                             { Scope = RuleScope.Global })
                .ToList();
        }
        catch { diskGlobal = new(); }
        SaveRuleJsonSplit("names.json", engine.Rules, r => r.Scope, r => r.Name, diskGlobal,
            (path, subset) => _persistence.SaveNames(path, subset));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();   // #154 repaint visible lines
    }

    public void OnPresetsChanged()
    {
        var engine = PresetEngine;
        if (engine is not null)
        {
            List<PresetRule> diskGlobal;
            try
            {
                diskGlobal = _persistence.LoadPresets(Path.Combine(_configRoot, "presets.json"))
                    .Select(m => new PresetRule
                    {
                        Id = m.Id, ForegroundColor = m.ForegroundColor,
                        BackgroundColor = m.BackgroundColor, HighlightLine = m.HighlightLine,
                        Scope = RuleScope.Global,
                    })
                    .ToList();
            }
            catch { diskGlobal = new(); }
            SaveRuleJsonSplit("presets.json", engine.Presets.Values, r => r.Scope, r => r.Id,
                diskGlobal, (path, subset) => _persistence.SavePresets(path, subset));   // #149
        }
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnTriggersChanged()
    {
        var engine = TriggerEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("triggers.json", engine.Triggers,
            r => r.Scope, r => r.Pattern, DiskGlobal().Triggers.Triggers,
            (path, subset) => _persistence.SaveTriggers(path, subset));
        SyncCfgSplit("triggers.cfg", engine.Triggers, r => r.Scope, mergedGlobal, CfgFormat.TriggerLines);
    }

    public void OnSubstitutesChanged()
    {
        var engine = SubstituteEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("substitutes.json", engine.Rules,
            r => r.Scope, r => r.Pattern, DiskGlobal().Substitutes.Rules,
            (path, subset) => _persistence.SaveSubstitutes(path, subset));
        SyncCfgSplit("substitutes.cfg", engine.Rules, r => r.Scope, mergedGlobal, CfgFormat.SubstituteLines);
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnGagsChanged()
    {
        var engine = GagEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("gags.json", engine.Rules,
            r => r.Scope, r => r.Pattern, DiskGlobal().Gags.Rules,
            (path, subset) => _persistence.SaveGags(path, subset));
        SyncCfgSplit("gags.cfg", engine.Rules, r => r.Scope, mergedGlobal, CfgFormat.GagLines);
    }

    public void OnAliasesChanged()
    {
        var engine = AliasEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("aliases.json", engine.Aliases,
            r => r.Scope, r => r.Name, DiskGlobal().Aliases.Aliases,
            (path, subset) => _persistence.SaveAliases(path, subset));
        SyncCfgSplit("aliases.cfg", engine.Aliases, r => r.Scope, mergedGlobal, CfgFormat.AliasLines);
    }

    public void OnMacrosChanged()
    {
        var engine = MacroEngine;
        if (engine is null) return;
        var mergedGlobal = SaveRuleJsonSplit("macros.json", engine.Rules,
            r => r.Scope, r => r.Key, DiskGlobal().Macros.Rules,
            (path, subset) => _persistence.SaveMacros(path, subset));
        SyncCfgSplit("macros.cfg", engine.Rules, r => r.Scope, mergedGlobal, CfgFormat.MacroLines);
    }

    public void OnVariablesChanged()
    {
        var store = VariableStore;
        if (store is null) return;
        SaveRuleJson("variables.json", path => _persistence.SaveVariables(path, store));
        SyncCfg("variables.cfg", () => CfgFormat.VariableLines(store));
    }

    public void OnClassesChanged()
    {
        // ClassEngine still has no PersistenceService (.json) writer — see
        // follow-ups — but when the profile carries a classes.cfg (Genie 4
        // import / #class save) the connect-time replay makes that file the
        // effective truth, so keep it current with panel edits.
        var engine = ClassEngine;
        if (engine is null) return;
        SyncCfg("classes.cfg", () => CfgFormat.ClassLines(engine.GetAll()));
    }

    public void OnWindowSettingsChanged()
    {
        TrySave(() => _persistence.SaveWindowSettings(PathFor("windows.json"), _windowSettings));
    }

    /// <summary>Persist global script settings to <c>settings.cfg</c>. The
    /// panel mutates the live <see cref="GenieConfig"/> directly (so changes
    /// take effect immediately); this just flushes them to disk.</summary>
    public void OnScriptSettingsChanged()
    {
        var config = _core?.Config;
        if (config is null) return;
        TrySave(() => config.Save());
    }

    /// <summary>Persist display.json after a window-behaviour edit (Always on
    /// Top). The panel mutates the live <see cref="DisplaySettings"/> directly —
    /// so the window's <c>Topmost</c> and the Layout-menu checkmark update at
    /// once — and this flushes it to disk. Always on Top is also mirrored into
    /// <c>settings.cfg</c> so <c>#config alwaysontop</c> / <c>#config list</c>
    /// stay in step, matching the Layout-menu toggle's behaviour.</summary>
    public void OnDisplaySettingsChanged()
    {
        if (_display is null) return;
        if (_displayPath is not null) TrySave(() => _display.Save(_displayPath));
        if (_core?.Config is { } cfg && cfg.AlwaysOnTop != _display.AlwaysOnTop)
        {
            cfg.AlwaysOnTop = _display.AlwaysOnTop;
            TrySave(() => cfg.Save());
        }
    }

    private static void TrySave(Action save)
    {
        try { save(); } catch { /* non-fatal */ }
    }

    /// <summary>Save one of the live-reload-watched rule .json files, marking
    /// the write first so the host's <see cref="RuleFileWatcher"/> doesn't
    /// bounce our own save back as an "external edit" reload.</summary>
    private void SaveRuleJson(string fileName, Action<string> save)
    {
        var path = PathFor(fileName);
        RuleFileWatcher.MarkAppWrite(path);
        TrySave(() => save(path));
    }

    /// <summary>True when the selected profile's dir IS the global Config dir
    /// (profile-less / legacy-global editing) — a single config layer.</summary>
    private bool SingleLayer =>
        ScopedRuleLoader.SameDirectory(_profileDirResolver(SelectedProfile), _configRoot);

    /// <summary>
    /// Keys the user explicitly deleted (or renamed away) at Global scope,
    /// per rule .json file, since that file's last save (#257 Phase 2). The
    /// live engine holds a LAYERED view — a character override shadows its
    /// global twin out of the engine entirely — so the global file is written
    /// as engine-Global-subset MERGED with the on-disk global entries the
    /// engine doesn't carry. Without this set, deleting a global rule via the
    /// panel would be silently resurrected by that merge; with it, only
    /// intentional deletes stick. Panels report through
    /// <see cref="NoteGlobalDelete"/>.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _pendingGlobalDeletes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A panel deleted (or renamed away) a Global-scoped rule; make
    /// the next save of <paramref name="fileName"/> drop <paramref name="key"/>
    /// from the shared file instead of preserving it via the twin merge.</summary>
    public void NoteGlobalDelete(string fileName, string key)
    {
        if (!_pendingGlobalDeletes.TryGetValue(fileName, out var set))
            _pendingGlobalDeletes[fileName] = set = new(StringComparer.OrdinalIgnoreCase);
        set.Add(key);
    }

    /// <summary>Build the per-panel #257 scope-editing handle: whether two
    /// config layers exist for the current selection, and the explicit
    /// global-delete channel bound to that panel's rule file.</summary>
    public Views.ScopeEditingContext ScopeContextFor(string fileName) => new()
    {
        TwoLayers        = !SingleLayer,
        NoteGlobalDelete = key => NoteGlobalDelete(fileName, key),
    };

    private IReadOnlyCollection<string> PendingDeletes(string fileName) =>
        _pendingGlobalDeletes.TryGetValue(fileName, out var set)
            ? set
            : (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>
    /// The #257 split-save: each rule goes back to the file its
    /// <see cref="RuleScope"/> names — Character rules to the profile copy,
    /// Global rules to the shared Config copy — so a panel edit never forks
    /// the global set into the profile. The global side is the engine's
    /// Global subset merged with <paramref name="diskGlobal"/> (on-disk
    /// entries a character override shadows out of the engine), minus keys
    /// explicitly deleted via <see cref="NoteGlobalDelete"/> — deriving it
    /// from the engine alone wiped shadowed twins on fully-forked profiles.
    /// A scope's file is only created when it has rules to hold (an existing
    /// file is always rewritten, so deletions apply). Single-layer editing
    /// writes everything to the one file. Writes are marked so the
    /// RuleFileWatcher doesn't bounce them back as external edits — it
    /// watches BOTH dirs. Returns the merged global list so the .cfg sync
    /// writes the same content.
    /// </summary>
    private List<T> SaveRuleJsonSplit<T>(string fileName, IEnumerable<T> rules,
        Func<T, RuleScope> scopeOf, Func<T, string> keyOf, IEnumerable<T> diskGlobal,
        Action<string, IReadOnlyList<T>> save)
    {
        var all         = rules.ToList();
        var profilePath = PathFor(fileName);
        if (SingleLayer)
        {
            RuleFileWatcher.MarkAppWrite(profilePath);
            TrySave(() => save(profilePath, all));
            return all;
        }
        var globalPath = Path.Combine(_configRoot, fileName);
        var character  = all.Where(r => scopeOf(r) == RuleScope.Character).ToList();
        var global     = ScopedRuleLoader.MergeGlobalForSave(
            all.Where(r => scopeOf(r) == RuleScope.Global), diskGlobal, keyOf,
            PendingDeletes(fileName));
        if (character.Count > 0 || File.Exists(profilePath))
        {
            RuleFileWatcher.MarkAppWrite(profilePath);
            TrySave(() => save(profilePath, character));
        }
        if (global.Count > 0 || File.Exists(globalPath))
        {
            RuleFileWatcher.MarkAppWrite(globalPath);
            TrySave(() => save(globalPath, global));
        }
        _pendingGlobalDeletes.Remove(fileName);   // applied — don't re-drop later
        InvalidateDraftScopes();                  // disk changed under the cache
        return global;
    }

    /// <summary>Scope-split companion to <see cref="SyncCfg"/>: the profile
    /// .cfg is rewritten from the Character subset and the global .cfg from
    /// the SAME merged global content the .json save produced, so the .cfg
    /// dual-write can't re-fork global rules or drop shadowed twins. Same
    /// only-rewrites-an-existing-file rule as ever.</summary>
    private void SyncCfgSplit<T>(string fileName, IEnumerable<T> rules,
        Func<T, RuleScope> scopeOf, IReadOnlyList<T> mergedGlobal,
        Func<IEnumerable<T>, IEnumerable<string>> lines)
    {
        var all        = rules.ToList();
        var profileCfg = Path.Combine(_profileDirResolver(SelectedProfile), fileName);
        if (SingleLayer)
        {
            SyncCfgAt(profileCfg, () => lines(all));
            return;
        }
        SyncCfgAt(profileCfg, () => lines(all.Where(r => scopeOf(r) == RuleScope.Character)));
        SyncCfgAt(Path.Combine(_configRoot, fileName), () => lines(mergedGlobal));
    }

    /// <summary>The on-disk global layer a panel save must not drop (twin
    /// source for <see cref="SaveRuleJsonSplit"/>) — the cached effective
    /// global scope, rebuilt lazily after every save.</summary>
    private LayeredRuleLoad.EffectiveScope DiskGlobal() => DraftScopes().Glob;

    private void InvalidateDraftScopes()
    {
        _draftGlobalScope  = null;
        _draftProfileScope = null;
        _draftScopesBuilt  = false;
    }

    private void SyncCfgAt(string path, Func<IEnumerable<string>> lines)
    {
        if (!File.Exists(path)) return;
        TrySave(() => ConfigPersistence.WriteLines(path, lines()));
    }

    /// <summary>
    /// Keep a coexisting Genie 4-style .cfg in lockstep with the .json we just
    /// wrote. The connect sequence replays .cfg files AFTER the host's .json
    /// load, and each .cfg loader clears its engine first — so when both
    /// stores exist the .cfg wins, and before this sync a stale .cfg (written
    /// by the Genie 4 import or a "#x save") silently reverted every panel
    /// edit at the next connect. Only rewrites a file that already exists:
    /// json-only profiles never get a .cfg forked for them.
    /// </summary>
    private void SyncCfg(string fileName, Func<IEnumerable<string>> lines)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path)) return;
        TrySave(() => ConfigPersistence.WriteLines(path, lines()));
    }

    /// <summary>
    /// Cached per-scope effective sets backing the draft engines — the same
    /// <see cref="LayeredRuleLoad"/> machinery the connect-time load uses
    /// (json + cfg-over-json per dir, then Character-over-Global layering),
    /// so the dialog and a live connect always agree. Rebuilt lazily after
    /// every <see cref="ClearDrafts"/>. Profile side is null in single-layer
    /// (profile-less) editing.
    /// </summary>
    private LayeredRuleLoad.EffectiveScope? _draftGlobalScope;
    private LayeredRuleLoad.EffectiveScope? _draftProfileScope;
    private bool _draftScopesBuilt;

    private (LayeredRuleLoad.EffectiveScope Glob, LayeredRuleLoad.EffectiveScope? Prof) DraftScopes()
    {
        if (!_draftScopesBuilt)
        {
            _draftGlobalScope  = LayeredRuleLoad.BuildEffectiveScope(_configRoot, _persistence);
            _draftProfileScope = SingleLayer
                ? null
                : LayeredRuleLoad.BuildEffectiveScope(_profileDirResolver(SelectedProfile), _persistence);
            _draftScopesBuilt = true;
        }
        return (_draftGlobalScope!, _draftProfileScope);
    }

    /// <summary>Path inside the currently-selected profile's config directory,
    /// or the global <c>Config/</c> dir when no profile is selected.</summary>
    private string PathFor(string fileName)
    {
        var dir = _profileDirResolver(SelectedProfile);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    // ── Draft engines (cleared whenever SelectedProfile changes) ─────────────

    private HighlightEngine?     _draftHighlights;
    private NameHighlightEngine? _draftNames;
    private PresetEngine?        _draftPresets;
    private TriggerEngineFinal?  _draftTriggers;
    private SubstituteEngine?    _draftSubstitutes;
    private GagEngine?           _draftGags;
    private AliasEngine?         _draftAliases;
    private MacroEngine?         _draftMacros;
    private ClassEngine?         _draftClasses;
    private VariableStore?       _draftVariables;

    private void ClearDrafts()
    {
        _draftHighlights  = null;
        _draftNames       = null;
        _draftPresets     = null;
        _draftTriggers    = null;
        _draftSubstitutes = null;
        _draftGags        = null;
        _draftAliases     = null;
        _draftMacros      = null;
        _draftClasses     = null;
        _draftVariables   = null;
        _draftGlobalScope  = null;
        _draftProfileScope = null;
        _draftScopesBuilt  = false;
        // Tell every panel "your engine ref changed" so they re-Initialize.
        this.RaisePropertyChanged(nameof(HighlightEngine));
        this.RaisePropertyChanged(nameof(NameHighlightEngine));
        this.RaisePropertyChanged(nameof(PresetEngine));
        this.RaisePropertyChanged(nameof(TriggerEngine));
        this.RaisePropertyChanged(nameof(SubstituteEngine));
        this.RaisePropertyChanged(nameof(GagEngine));
        this.RaisePropertyChanged(nameof(AliasEngine));
        this.RaisePropertyChanged(nameof(MacroEngine));
        this.RaisePropertyChanged(nameof(ClassEngine));
        this.RaisePropertyChanged(nameof(VariableStore));
    }

    private HighlightEngine GetDraftHighlights()
    {
        if (_draftHighlights is not null) return _draftHighlights;
        _draftHighlights = new HighlightEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, highlights: _draftHighlights); }
        catch { /* draft stays usable empty */ }
        return _draftHighlights;
    }

    private NameHighlightEngine GetDraftNames()   => _draftNames   ??= new NameHighlightEngine();
    private PresetEngine        GetDraftPresets() => _draftPresets ??= new PresetEngine();

    private TriggerEngineFinal GetDraftTriggers()
    {
        if (_draftTriggers is not null) return _draftTriggers;
        _draftTriggers = new TriggerEngineFinal();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, triggers: _draftTriggers); }
        catch { }
        return _draftTriggers;
    }

    private SubstituteEngine GetDraftSubstitutes()
    {
        if (_draftSubstitutes is not null) return _draftSubstitutes;
        _draftSubstitutes = new SubstituteEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, substitutes: _draftSubstitutes); }
        catch { }
        return _draftSubstitutes;
    }

    private GagEngine GetDraftGags()
    {
        if (_draftGags is not null) return _draftGags;
        _draftGags = new GagEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, gags: _draftGags); }
        catch { }
        return _draftGags;
    }

    private AliasEngine GetDraftAliases()
    {
        if (_draftAliases is not null) return _draftAliases;
        _draftAliases = new AliasEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, aliases: _draftAliases); }
        catch { }
        return _draftAliases;
    }

    private MacroEngine GetDraftMacros()
    {
        if (_draftMacros is not null) return _draftMacros;
        _draftMacros = new MacroEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, macros: _draftMacros); }
        catch { }
        return _draftMacros;
    }

    private ClassEngine GetDraftClasses()
    {
        if (_draftClasses is not null) return _draftClasses;
        _draftClasses = new ClassEngine();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, classes: _draftClasses); }
        catch { }
        return _draftClasses;
    }

    private VariableStore GetDraftVariables()
    {
        if (_draftVariables is not null) return _draftVariables;
        _draftVariables = new VariableStore();
        try { var (g, c) = DraftScopes(); LayeredRuleLoad.ApplyLayered(g, c, variables: _draftVariables); }
        catch { }
        return _draftVariables;
    }
}
