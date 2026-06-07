using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Genie.App.Highlighting;
using Genie.Core;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Gags;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using Genie.Core.Macros;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Genie.Core.Profiles;
using Genie.Core.Substitutes;
using Genie.Core.Triggers;
using Genie.Core.Variables;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Top-level Configuration dialog VM. Profile-scoped: every config file lives
/// under <c>Config/Profiles/{ProfileId}/</c> so each character can have its own
/// highlights, triggers, aliases, etc.
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
    private readonly ConnectionProfile?   _connectedProfile;
    private readonly WindowSettingsStore  _windowSettings;
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
        WindowSettingsStore  windowSettings)
    {
        _core             = core;
        _configRoot       = configRoot;
        _connectedProfile = connectedProfile;
        _windowSettings   = windowSettings;

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

    // ── Persistence hooks (called by every panel after an edit) ──────────────

    public void OnHighlightsChanged()
    {
        var engine = HighlightEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveHighlights(PathFor("highlights.json"), engine.Rules));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnPresetsChanged()
    {
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
        // TODO: presets.json persistence
    }

    public void OnTriggersChanged()
    {
        var engine = TriggerEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveTriggers(PathFor("triggers.json"), engine.Triggers));
    }

    public void OnSubstitutesChanged()
    {
        var engine = SubstituteEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveSubstitutes(PathFor("substitutes.json"), engine.Rules));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnGagsChanged()
    {
        var engine = GagEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveGags(PathFor("gags.json"), engine.Rules));
    }

    public void OnAliasesChanged()
    {
        var engine = AliasEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveAliases(PathFor("aliases.json"), engine.Aliases));
    }

    public void OnMacrosChanged()
    {
        var engine = MacroEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveMacros(PathFor("macros.json"), engine.Rules));
    }

    public void OnVariablesChanged()
    {
        var store = VariableStore;
        if (store is null) return;
        TrySave(() => _persistence.SaveVariables(PathFor("variables.json"), store));
    }

    public void OnClassesChanged()
    {
        // ClassEngine has no PersistenceService writer yet — see follow-ups.
    }

    public void OnWindowSettingsChanged()
    {
        TrySave(() => _persistence.SaveWindowSettings(PathFor("windows.json"), _windowSettings));
    }

    private static void TrySave(Action save)
    {
        try { save(); } catch { /* non-fatal */ }
    }

    /// <summary>Path inside the currently-selected profile's config directory,
    /// or the global <c>Config/</c> dir when no profile is selected.</summary>
    private string PathFor(string fileName)
    {
        var dir = SelectedProfile is null
            ? _configRoot
            : Path.Combine(_configRoot, "Profiles", SelectedProfile.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>
    /// Read path with profile-over-global precedence: the selected profile's
    /// own copy when present, otherwise the shared global Config file (so a
    /// profile that hasn't customised a rule type still shows the global set,
    /// including legacy / earlier-prototype configs). Saves always go to the
    /// profile dir via <see cref="PathFor"/>, so the first edit promotes a
    /// global config into a per-profile override. Returns the profile path
    /// (which may not exist) when neither location has the file, so callers'
    /// existing <c>File.Exists</c> guards still work.
    /// </summary>
    private string ReadPathFor(string fileName)
    {
        var profilePath = PathFor(fileName);
        if (File.Exists(profilePath)) return profilePath;
        var globalPath = Path.Combine(_configRoot, fileName);
        return File.Exists(globalPath) ? globalPath : profilePath;
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
        var path = ReadPathFor("highlights.json");
        if (!File.Exists(path)) return _draftHighlights;
        try
        {
            foreach (var m in _persistence.LoadHighlights(path))
                _draftHighlights.AddRule(
                    m.Pattern, m.ForegroundColor, m.BackgroundColor,
                    Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                    m.CaseSensitive, m.IsEnabled, m.ClassName);
        }
        catch { }
        return _draftHighlights;
    }

    private NameHighlightEngine GetDraftNames()   => _draftNames   ??= new NameHighlightEngine();
    private PresetEngine        GetDraftPresets() => _draftPresets ??= new PresetEngine();

    private TriggerEngineFinal GetDraftTriggers()
    {
        if (_draftTriggers is not null) return _draftTriggers;
        _draftTriggers = new TriggerEngineFinal();
        var path = ReadPathFor("triggers.json");
        if (!File.Exists(path)) return _draftTriggers;
        try
        {
            foreach (var m in _persistence.LoadTriggers(path))
                _draftTriggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName);
        }
        catch { }
        return _draftTriggers;
    }

    private SubstituteEngine GetDraftSubstitutes()
    {
        if (_draftSubstitutes is not null) return _draftSubstitutes;
        _draftSubstitutes = new SubstituteEngine();
        var path = ReadPathFor("substitutes.json");
        if (!File.Exists(path)) return _draftSubstitutes;
        try
        {
            foreach (var m in _persistence.LoadSubstitutes(path))
                _draftSubstitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
        }
        catch { }
        return _draftSubstitutes;
    }

    private GagEngine GetDraftGags()
    {
        if (_draftGags is not null) return _draftGags;
        _draftGags = new GagEngine();
        var path = ReadPathFor("gags.json");
        if (!File.Exists(path)) return _draftGags;
        try
        {
            foreach (var m in _persistence.LoadGags(path))
                _draftGags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
        }
        catch { }
        return _draftGags;
    }

    private AliasEngine GetDraftAliases()
    {
        if (_draftAliases is not null) return _draftAliases;
        _draftAliases = new AliasEngine();
        var path = ReadPathFor("aliases.json");
        if (!File.Exists(path)) return _draftAliases;
        try
        {
            foreach (var m in _persistence.LoadAliases(path))
                _draftAliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
        }
        catch { }
        return _draftAliases;
    }

    private MacroEngine GetDraftMacros()
    {
        if (_draftMacros is not null) return _draftMacros;
        _draftMacros = new MacroEngine();
        var path = ReadPathFor("macros.json");
        if (!File.Exists(path)) return _draftMacros;
        try
        {
            foreach (var m in _persistence.LoadMacros(path))
                _draftMacros.Add(m.Key, m.Action);
        }
        catch { }
        return _draftMacros;
    }

    private ClassEngine GetDraftClasses()
    {
        if (_draftClasses is not null) return _draftClasses;
        _draftClasses = new ClassEngine();
        var path = ReadPathFor("classes.json");
        if (!File.Exists(path)) return _draftClasses;
        try
        {
            foreach (var m in _persistence.LoadClasses(path))
                _draftClasses.Set(m.Name, m.IsActive);
        }
        catch { }
        return _draftClasses;
    }

    private VariableStore GetDraftVariables()
    {
        if (_draftVariables is not null) return _draftVariables;
        _draftVariables = new VariableStore();
        var path = ReadPathFor("variables.json");
        if (!File.Exists(path)) return _draftVariables;
        try
        {
            foreach (var m in _persistence.LoadVariables(path))
                _draftVariables.Set(m.Name, m.Value);
        }
        catch { }
        return _draftVariables;
    }
}
