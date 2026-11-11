using System;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

public partial class ConfigurationDialog : ReactiveWindow<ConfigurationViewModel>
{
    /// <summary>How long to wait for the first frame before filling anyway, so
    /// a window that never renders (minimised, no compositor) still fills.</summary>
    private static readonly TimeSpan FillFallback = TimeSpan.FromMilliseconds(250);

    private ConfigurationViewModel? _pendingFill;

    public ConfigurationDialog()
    {
        EditorFonts.EnsureRegistered();   // #402: resolve the rule editors' monospace font once
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ConfigurationViewModel vm) return;

            vm.RequestClose += () => Close();

            // Show the window first, then fill it (public #402): filling every
            // tab takes about a second on a slow machine, and doing it before
            // the first frame left Genie frozen with no window to show for it.
            // The tabs stay disabled until they are filled.
            Tabs.IsEnabled = false;
            LoadingText.IsVisible = true;
            _pendingFill = vm;
            if (IsVisible) ScheduleFill();
        };
        Opened += (_, _) => ScheduleFill();
    }

    /// <summary>Fill the panels just after the first frame is drawn, with a
    /// timer as the fallback; whichever comes first fills, once.</summary>
    private void ScheduleFill()
    {
        if (_pendingFill is null) return;
        RequestAnimationFrame(_ => Dispatcher.UIThread.Post(FillPending, DispatcherPriority.Background));
        DispatcherTimer.RunOnce(FillPending, FillFallback, DispatcherPriority.Background);
    }

    private void FillPending()
    {
        if (_pendingFill is not { } vm) return;
        _pendingFill = null;

        InitializePanels(vm);
        Tabs.IsEnabled = true;
        LoadingText.IsVisible = false;

        // Re-bind every panel whenever the user picks a different profile.
        // The VM swaps live engines for drafts (or vice versa) under the hood;
        // each panel needs its `Initialize(engine, callback)` re-invoked to
        // pick up the new instance, repopulate its DataGrid, and clear its form.
        // Skip(1): WhenAnyValue replays the current profile on subscribe, and
        // the panels were just filled for it (public #402).
        vm.WhenAnyValue(x => x.SelectedProfile)
          .Skip(1)
          .Subscribe(_ => InitializePanels(vm));
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
         && vm.NameHighlightEngine is { } names)
        {
            HighlightsPanelCtrl.Initialize(
                highlights, names,
                onHighlightsChanged: vm.OnHighlightsChanged,
                onNamesChanged:      vm.OnNamesChanged,
                highlightsScope:     vm.ScopeContextFor("highlights.json"),
                namesScope:          vm.ScopeContextFor("names.json"));
        }

        // Presets are their own tab (public #304): they drive panel colours
        // (the Mobs window's creatures colour) as well as highlight styles.
        if (vm.PresetEngine is { } presets)
            PresetsPanelCtrl.Initialize(presets, vm.OnPresetsChanged,
                                        vm.ScriptConfig,               // #131 MonsterBold toggle
                                        vm.OnScriptSettingsChanged,
                                        vm.ScopeContextFor("presets.json"));

        if (vm.TriggerEngine    is { } triggers)    TriggersPanelCtrl   .Initialize(triggers,    vm.OnTriggersChanged,    vm.ScopeContextFor("triggers.json"));
        if (vm.SubstituteEngine is { } substitutes) SubstitutesPanelCtrl.Initialize(substitutes, vm.OnSubstitutesChanged, vm.ScopeContextFor("substitutes.json"));
        if (vm.GagEngine        is { } gags)        GagsPanelCtrl       .Initialize(gags,        vm.OnGagsChanged,        vm.ScopeContextFor("gags.json"));
        if (vm.ShuntEngine      is { } shunts)      ShuntsPanelCtrl     .Initialize(shunts,      vm.OnShuntsChanged,      vm.ScopeContextFor("shunts.json"));
        if (vm.AliasEngine      is { } aliases)     AliasesPanelCtrl    .Initialize(aliases,     vm.OnAliasesChanged,     vm.ScopeContextFor("aliases.json"));
        if (vm.MacroEngine      is { } macros)      MacrosPanelCtrl     .Initialize(macros,      vm.OnMacrosChanged,      vm.ScopeContextFor("macros.json"));
        if (vm.VariableStore    is { } variables)   VariablesPanelCtrl  .Initialize(variables,   vm.OnVariablesChanged,   vm.LiveGlobals, vm.ScopeContextFor("variables.json"));
        if (vm.ClassEngine      is { } classes)     ClassesPanelCtrl    .Initialize(classes,     vm.OnClassesChanged,     vm.ScopeContextFor("classes.json"));

        // Server dialog mappings (#156) — per profile, plus the global
        // serverdialogs master switch (settings.cfg, so null pre-connect).
        ServerDialogsPanelCtrl.Initialize(vm.DialogMappings, vm.OnDialogMappingsChanged,
                                          vm.ScriptConfig, vm.OnScriptSettingsChanged,
                                          vm.DialogTargetWindows);

        // WindowSettingsStore is always present — it's app-level state, not
        // per-profile, so no nullability check. Its rows still save back to
        // the layer they came from (public #315).
        LayoutPanelCtrl.Initialize(vm.WindowSettings, vm.OnWindowSettingsChanged,
                                   vm.ScopeContextFor("windows.json"));

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
                                vm.SpeakSample, vm.TtsVoiceChanged, vm.TtsInstallVoice,
                                vm.TtsListVoices);
    }
}
