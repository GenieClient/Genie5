using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

/// <summary>
/// Edit ▸ Configuration. Opening it is kept short (public #402):
/// <list type="bullet">
///   <item>The window shows first; the tabs are filled after its first frame.</item>
///   <item>Only the tab on screen is built and filled then. The others are built
///         and filled one at a time in idle time, or at once when you pick one.</item>
///   <item>A profile switch refills the tab on screen at once and the others the
///         same way.</item>
/// </list>
/// Every panel is reached through a <see cref="ConfigPanelSlot"/>, which builds
/// it if it doesn't exist yet.
/// </summary>
public partial class ConfigurationDialog : ReactiveWindow<ConfigurationViewModel>
{
    /// <summary>How long to wait for the first frame before filling anyway, so
    /// a window that never renders (minimised, no compositor) still fills.</summary>
    private static readonly TimeSpan FillFallback = TimeSpan.FromMilliseconds(250);

    private readonly List<ConfigPanelSlot> _slots;
    private ConfigurationViewModel? _pendingFill;
    private ConfigurationViewModel? _vm;
    private IDisposable?            _profileSub;

    /// <summary>Bumped for each fill pass (the first fill, then each profile
    /// switch); a slot filled in an older pass is out of date.</summary>
    private int _pass;

    /// <summary>Fill the tabs that aren't on screen in idle time. Tests turn it
    /// off to look at tabs that haven't been built yet.</summary>
    internal bool FillInBackground { get; set; } = true;

    public ConfigurationDialog()
    {
        EditorFonts.EnsureRegistered();   // #402: resolve the rule editors' monospace font once
        InitializeComponent();
        _slots = CreateSlots();

        // A tab (or a sub-tab) was picked: build and fill what is now on screen.
        // Panels raise SelectionChanged from their own lists too, hence the check.
        AddHandler(SelectingItemsControl.SelectionChangedEvent, (_, e) =>
        {
            if (e.Source is TabControl) FillShowing();
        }, RoutingStrategies.Bubble);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ConfigurationViewModel vm) return;

            vm.RequestClose += () => Close();

            // Show the window first, then fill it. The tabs stay disabled until
            // the one on screen is filled.
            Tabs.IsEnabled = false;
            LoadingText.IsVisible = true;
            _pendingFill = vm;
            if (IsVisible) ScheduleFill();
        };
        Opened += (_, _) => ScheduleFill();
        Closed += (_, _) =>
        {
            _vm = null;   // stops the background pass
            _profileSub?.Dispose();
        };
    }

    /// <summary>Fill just after the first frame is drawn, with a timer as the
    /// fallback; whichever comes first fills, once.</summary>
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
        _vm = vm;

        Refill();
        Tabs.IsEnabled = true;
        LoadingText.IsVisible = false;

        // Re-bind the panels whenever the user picks a different profile. The
        // VM swaps live engines for drafts (or vice versa) under the hood; each
        // panel needs its `Initialize(engine, callback)` re-invoked to pick up
        // the new instance, repopulate its DataGrid, and clear its form.
        // Skip(1): WhenAnyValue replays the current profile on subscribe, and
        // the panels were just filled for it.
        _profileSub = vm.WhenAnyValue(x => x.SelectedProfile)
                        .Skip(1)
                        .Subscribe(_ => Refill());
    }

    /// <summary>Start a fill pass: the tab on screen now, the rest in idle time.</summary>
    private void Refill()
    {
        _pass++;
        FillShowing();
        if (FillInBackground) QueueNext(_pass);
    }

    private void FillShowing()
    {
        if (_vm is not { } vm) return;
        foreach (var slot in _slots)
            if (slot.IsShowing) slot.Ensure(vm, _pass);
    }

    /// <summary>One tab per idle step, so typing and drawing go first. A newer
    /// pass (profile switch) or closing the window ends this one.</summary>
    private void QueueNext(int pass) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (pass != _pass || _vm is not { } vm) return;
            var next = _slots.FirstOrDefault(s => !s.IsCurrent(pass));
            if (next is null) return;
            next.Ensure(vm, pass);
            QueueNext(pass);
        }, DispatcherPriority.Background);

    /// <summary>The panel of type <typeparamref name="T"/> if it has been built.</summary>
    internal T? BuiltPanel<T>() where T : Control => _slots.Select(s => s.Panel).OfType<T>().FirstOrDefault();

    /// <summary>The panel of type <typeparamref name="T"/>, built (and filled,
    /// once the dialog has its view model) if it wasn't yet.</summary>
    internal T Panel<T>() where T : Control
    {
        var slot = _slots.Single(s => s.PanelType == typeof(T));
        if (_vm is { } vm) slot.Ensure(vm, _pass);
        return (T)slot.Build();   // no view model yet: built, not filled
    }

    /// <summary>
    /// One slot per tab, in tab order, each with how to hand its panel the
    /// current engines. Null engines mean "this engine isn't available right
    /// now"; the panel still exists but stays empty until a profile is picked.
    /// </summary>
    private List<ConfigPanelSlot> CreateSlots() => new()
    {
        // WindowSettingsStore is always present — it's app-level state, not
        // per-profile, so no nullability check. Its rows still save back to
        // the layer they came from (public #315).
        new ConfigPanelSlot<LayoutPanel>(LayoutPanelHost, (p, vm) =>
            p.Initialize(vm.WindowSettings, vm.OnWindowSettingsChanged, vm.ScopeContextFor("windows.json"))),

        // App-wide window-behaviour toggles (Always on Top). Also profile-
        // independent — binds the same DisplaySettings the main window uses.
        new ConfigPanelSlot<LayoutSettingsPanel>(LayoutSettingsPanelHost, (p, vm) =>
            p.Initialize(vm.Display, vm.OnDisplaySettingsChanged)),

        // Server dialog mappings (#156) — per profile, plus the global
        // serverdialogs master switch (settings.cfg, so null pre-connect).
        new ConfigPanelSlot<ServerDialogsPanel>(ServerDialogsPanelHost, (p, vm) =>
            p.Initialize(vm.DialogMappings, vm.OnDialogMappingsChanged,
                         vm.ScriptConfig, vm.OnScriptSettingsChanged,
                         vm.DialogTargetWindows)),

        // Presets are their own tab (public #304): they drive panel colours
        // (the Mobs window's creatures colour) as well as highlight styles.
        new ConfigPanelSlot<PresetsPanel>(PresetsPanelHost, (p, vm) =>
        {
            if (vm.PresetEngine is { } presets)
                p.Initialize(presets, vm.OnPresetsChanged,
                             vm.ScriptConfig,               // #131 MonsterBold toggle
                             vm.OnScriptSettingsChanged,
                             vm.ScopeContextFor("presets.json"));
        }),

        new ConfigPanelSlot<HighlightsPanel>(HighlightsPanelHost, (p, vm) =>
        {
            if (vm.HighlightEngine     is { } highlights
             && vm.NameHighlightEngine is { } names)
                p.Initialize(highlights, names,
                             onHighlightsChanged: vm.OnHighlightsChanged,
                             onNamesChanged:      vm.OnNamesChanged,
                             highlightsScope:     vm.ScopeContextFor("highlights.json"),
                             namesScope:          vm.ScopeContextFor("names.json"));
        }),

        new ConfigPanelSlot<TriggersPanel>(TriggersPanelHost, (p, vm) =>
        {
            if (vm.TriggerEngine is { } triggers) p.Initialize(triggers, vm.OnTriggersChanged, vm.ScopeContextFor("triggers.json"));
        }),
        new ConfigPanelSlot<SubstitutesPanel>(SubstitutesPanelHost, (p, vm) =>
        {
            if (vm.SubstituteEngine is { } substitutes) p.Initialize(substitutes, vm.OnSubstitutesChanged, vm.ScopeContextFor("substitutes.json"));
        }),
        new ConfigPanelSlot<GagsPanel>(GagsPanelHost, (p, vm) =>
        {
            if (vm.GagEngine is { } gags) p.Initialize(gags, vm.OnGagsChanged, vm.ScopeContextFor("gags.json"));
        }),
        new ConfigPanelSlot<ShuntsPanel>(ShuntsPanelHost, (p, vm) =>
        {
            if (vm.ShuntEngine is { } shunts) p.Initialize(shunts, vm.OnShuntsChanged, vm.ScopeContextFor("shunts.json"));
        }),
        new ConfigPanelSlot<AliasesPanel>(AliasesPanelHost, (p, vm) =>
        {
            if (vm.AliasEngine is { } aliases) p.Initialize(aliases, vm.OnAliasesChanged, vm.ScopeContextFor("aliases.json"));
        }),

        // Script settings live on the global GenieConfig (settings.cfg), so
        // they're profile-independent. ScriptConfig is null pre-connect — the
        // panel disables itself and shows a hint in that case.
        new ConfigPanelSlot<ScriptsPanel>(ScriptsPanelHost, (p, vm) =>
            p.Initialize(vm.ScriptConfig, vm.OnScriptSettingsChanged)),

        // TTS settings share the same global GenieConfig + persistence path.
        // The speak-sample / voice-reset hooks come from the main window
        // (which owns the TtsService); null just disables the Test button.
        new ConfigPanelSlot<TtsPanel>(TtsPanelHost, (p, vm) =>
            p.Initialize(vm.ScriptConfig, vm.OnScriptSettingsChanged,
                         vm.SpeakSample, vm.TtsVoiceChanged, vm.TtsInstallVoice,
                         vm.TtsListVoices)),

        new ConfigPanelSlot<MacrosPanel>(MacrosPanelHost, (p, vm) =>
        {
            if (vm.MacroEngine is { } macros) p.Initialize(macros, vm.OnMacrosChanged, vm.ScopeContextFor("macros.json"));
        }),
        new ConfigPanelSlot<VariablesPanel>(VariablesPanelHost, (p, vm) =>
        {
            if (vm.VariableStore is { } variables) p.Initialize(variables, vm.OnVariablesChanged, vm.LiveGlobals, vm.ScopeContextFor("variables.json"));
        }),
        new ConfigPanelSlot<ClassesPanel>(ClassesPanelHost, (p, vm) =>
        {
            if (vm.ClassEngine is { } classes) p.Initialize(classes, vm.OnClassesChanged, vm.ScopeContextFor("classes.json"));
        }),
    };
}
