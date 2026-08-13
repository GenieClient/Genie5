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
        SaveRuleJson("highlights.json", path => _persistence.SaveHighlights(path, engine.Rules));
        SyncCfg("highlights.cfg", () => CfgFormat.HighlightLines(engine.Rules));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnNamesChanged()
    {
        var engine = NameHighlightEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveNames(PathFor("names.json"), engine.Rules));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();   // #154 repaint visible lines
    }

    public void OnPresetsChanged()
    {
        var engine = PresetEngine;
        if (engine is not null)
            TrySave(() => _persistence.SavePresets(PathFor("presets.json"), engine));   // #149
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnTriggersChanged()
    {
        var engine = TriggerEngine;
        if (engine is null) return;
        SaveRuleJson("triggers.json", path => _persistence.SaveTriggers(path, engine.Triggers));
        SyncCfg("triggers.cfg", () => CfgFormat.TriggerLines(engine.Triggers));
    }

    public void OnSubstitutesChanged()
    {
        var engine = SubstituteEngine;
        if (engine is null) return;
        SaveRuleJson("substitutes.json", path => _persistence.SaveSubstitutes(path, engine.Rules));
        SyncCfg("substitutes.cfg", () => CfgFormat.SubstituteLines(engine.Rules));
        if (EditingConnected) UserHighlights.NotifyRulesChanged();
    }

    public void OnGagsChanged()
    {
        var engine = GagEngine;
        if (engine is null) return;
        SaveRuleJson("gags.json", path => _persistence.SaveGags(path, engine.Rules));
        SyncCfg("gags.cfg", () => CfgFormat.GagLines(engine.Rules));
    }

    public void OnAliasesChanged()
    {
        var engine = AliasEngine;
        if (engine is null) return;
        SaveRuleJson("aliases.json", path => _persistence.SaveAliases(path, engine.Aliases));
        SyncCfg("aliases.cfg", () => CfgFormat.AliasLines(engine.Aliases));
    }

    public void OnMacrosChanged()
    {
        var engine = MacroEngine;
        if (engine is null) return;
        TrySave(() => _persistence.SaveMacros(PathFor("macros.json"), engine.Rules));
        SyncCfg("macros.cfg", () => CfgFormat.MacroLines(engine.Rules));
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
    /// Replay the selected profile's .cfg files over freshly built draft
    /// engines — the same thing the connect sequence does after the .json
    /// load, via the same CommandEngine loaders (<see cref="CfgReplay"/>).
    /// Without this the draft shows a json-only view that hides every
    /// cfg-only rule — and a <see cref="SyncCfg"/> from such a draft would
    /// drop those rules from the .cfg. Best-effort: on any failure the json
    /// view remains usable.
    /// </summary>
    private void OverlayDraftCfg(
        ClassEngine?        classes     = null,
        AliasEngine?        aliases     = null,
        VariableStore?      variables   = null,
        HighlightEngine?    highlights  = null,
        TriggerEngineFinal? triggers    = null,
        SubstituteEngine?   substitutes = null,
        GagEngine?          gags        = null,
        MacroEngine?        macros      = null)
    {
        try
        {
            CfgReplay.LoadInto(_profileDirResolver(SelectedProfile),
                classes, aliases, variables, highlights,
                triggers, substitutes, gags, macros);
        }
        catch { /* draft overlay is best-effort */ }
    }

    /// <summary>Path inside the currently-selected profile's config directory,
    /// or the global <c>Config/</c> dir when no profile is selected.</summary>
    private string PathFor(string fileName)
    {
        var dir = _profileDirResolver(SelectedProfile);
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
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadHighlights(path))
                    _draftHighlights.AddRule(
                        m.Pattern, m.ForegroundColor, m.BackgroundColor,
                        Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                        m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
            }
            catch { }
        }
        OverlayDraftCfg(highlights: _draftHighlights);
        return _draftHighlights;
    }

    private NameHighlightEngine GetDraftNames()   => _draftNames   ??= new NameHighlightEngine();
    private PresetEngine        GetDraftPresets() => _draftPresets ??= new PresetEngine();

    private TriggerEngineFinal GetDraftTriggers()
    {
        if (_draftTriggers is not null) return _draftTriggers;
        _draftTriggers = new TriggerEngineFinal();
        var path = ReadPathFor("triggers.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadTriggers(path))
                    _draftTriggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                              m.SoundFile, m.Speak, m.Eval, m.MatchAll);
            }
            catch { }
        }
        OverlayDraftCfg(triggers: _draftTriggers);
        return _draftTriggers;
    }

    private SubstituteEngine GetDraftSubstitutes()
    {
        if (_draftSubstitutes is not null) return _draftSubstitutes;
        _draftSubstitutes = new SubstituteEngine();
        var path = ReadPathFor("substitutes.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadSubstitutes(path))
                    _draftSubstitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
            catch { }
        }
        OverlayDraftCfg(substitutes: _draftSubstitutes);
        return _draftSubstitutes;
    }

    private GagEngine GetDraftGags()
    {
        if (_draftGags is not null) return _draftGags;
        _draftGags = new GagEngine();
        var path = ReadPathFor("gags.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadGags(path))
                    _draftGags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
            catch { }
        }
        OverlayDraftCfg(gags: _draftGags);
        return _draftGags;
    }

    private AliasEngine GetDraftAliases()
    {
        if (_draftAliases is not null) return _draftAliases;
        _draftAliases = new AliasEngine();
        var path = ReadPathFor("aliases.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadAliases(path))
                    _draftAliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
            }
            catch { }
        }
        OverlayDraftCfg(aliases: _draftAliases);
        return _draftAliases;
    }

    private MacroEngine GetDraftMacros()
    {
        if (_draftMacros is not null) return _draftMacros;
        _draftMacros = new MacroEngine();
        var path = ReadPathFor("macros.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadMacros(path))
                    _draftMacros.Add(m.Key, m.Action);
            }
            catch { }
        }
        OverlayDraftCfg(macros: _draftMacros);
        return _draftMacros;
    }

    private ClassEngine GetDraftClasses()
    {
        if (_draftClasses is not null) return _draftClasses;
        _draftClasses = new ClassEngine();
        var path = ReadPathFor("classes.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadClasses(path))
                    _draftClasses.Set(m.Name, m.IsActive);
            }
            catch { }
        }
        OverlayDraftCfg(classes: _draftClasses);
        return _draftClasses;
    }

    private VariableStore GetDraftVariables()
    {
        if (_draftVariables is not null) return _draftVariables;
        _draftVariables = new VariableStore();
        var path = ReadPathFor("variables.json");
        if (File.Exists(path))
        {
            try
            {
                foreach (var m in _persistence.LoadVariables(path))
                    _draftVariables.Set(m.Name, m.Value);
            }
            catch { }
        }
        OverlayDraftCfg(variables: _draftVariables);
        return _draftVariables;
    }
}
