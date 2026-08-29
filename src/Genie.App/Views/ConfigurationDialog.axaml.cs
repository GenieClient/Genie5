using Avalonia.ReactiveUI;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

public partial class ConfigurationDialog : ReactiveWindow<ConfigurationViewModel>
{
    public ConfigurationDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ConfigurationViewModel vm) return;

            vm.RequestClose += () => Close();

            // Initial pass.
            InitializePanels(vm);

            // Re-bind every panel whenever the user picks a different profile.
            // The VM swaps live engines for drafts (or vice versa) under the hood;
            // each panel needs its `Initialize(engine, callback)` re-invoked to
            // pick up the new instance, repopulate its DataGrid, and clear its form.
            vm.WhenAnyValue(x => x.SelectedProfile)
              .Subscribe(_ => InitializePanels(vm));
        };
    }

    /// <summary>
    /// Hand every panel its current engine reference. Called once when the VM
    /// is set and again whenever SelectedProfile changes. Null engines mean
    /// "this engine isn't available right now" — panels still exist but stay
    /// empty until a profile is picked.
    /// </summary>
    private void InitializePanels(ConfigurationViewModel vm)
    {
        if (vm.HighlightEngine     is { } highlights
         && vm.NameHighlightEngine is { } names
         && vm.PresetEngine        is { } presets)
        {
            HighlightsPanelCtrl.Initialize(
                highlights, names, presets,
                onHighlightsChanged: vm.OnHighlightsChanged,
                onNamesChanged:      vm.OnNamesChanged,
                onPresetsChanged:    vm.OnPresetsChanged,
                config:              vm.ScriptConfig,          // #131 MonsterBold toggle
                onConfigChanged:     vm.OnScriptSettingsChanged,
                highlightsScope:     vm.ScopeContextFor("highlights.json"),
                namesScope:          vm.ScopeContextFor("names.json"),
                presetsScope:        vm.ScopeContextFor("presets.json"));
        }

        if (vm.TriggerEngine    is { } triggers)    TriggersPanelCtrl   .Initialize(triggers,    vm.OnTriggersChanged,    vm.ScopeContextFor("triggers.json"));
        if (vm.SubstituteEngine is { } substitutes) SubstitutesPanelCtrl.Initialize(substitutes, vm.OnSubstitutesChanged, vm.ScopeContextFor("substitutes.json"));
        if (vm.GagEngine        is { } gags)        GagsPanelCtrl       .Initialize(gags,        vm.OnGagsChanged,        vm.ScopeContextFor("gags.json"));
        if (vm.AliasEngine      is { } aliases)     AliasesPanelCtrl    .Initialize(aliases,     vm.OnAliasesChanged,     vm.ScopeContextFor("aliases.json"));
        if (vm.MacroEngine      is { } macros)      MacrosPanelCtrl     .Initialize(macros,      vm.OnMacrosChanged,      vm.ScopeContextFor("macros.json"));
        if (vm.VariableStore    is { } variables)   VariablesPanelCtrl  .Initialize(variables,   vm.OnVariablesChanged);
        if (vm.ClassEngine      is { } classes)     ClassesPanelCtrl    .Initialize(classes,     vm.OnClassesChanged);

        // WindowSettingsStore is always present — it's app-level state, not
        // per-profile, so no nullability check.
        LayoutPanelCtrl.Initialize(vm.WindowSettings, vm.OnWindowSettingsChanged);

        // App-wide window-behaviour toggles (Always on Top). Also profile-
        // independent — binds the same DisplaySettings the main window uses.
        LayoutSettingsPanelCtrl.Initialize(vm.Display, vm.OnDisplaySettingsChanged);

        // Script settings live on the global GenieConfig (settings.cfg), so
        // they're profile-independent. ScriptConfig is null pre-connect — the
        // panel disables itself and shows a hint in that case.
        ScriptsPanelCtrl.Initialize(vm.ScriptConfig, vm.OnScriptSettingsChanged);

        // TTS settings share the same global GenieConfig + persistence path.
        // The speak-sample / voice-reset hooks come from the main window
        // (which owns the TtsService); null just disables the Test button.
        TtsPanelCtrl.Initialize(vm.ScriptConfig, vm.OnScriptSettingsChanged,
                                vm.SpeakSample, vm.TtsVoiceChanged);
    }
}
