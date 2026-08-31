using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Genie.Core;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Thin status strip showing which <c>.cmd</c> scripts are currently running.
/// Mirrors Genie 4's "Script Bar" muscle memory — you can see at a glance
/// what's executing and stop individual scripts without typing
/// <c>#stop &lt;name&gt;</c>.
///
/// <para>
/// The bar is auto-hidden when no scripts are running (see <see cref="HasScripts"/>),
/// so it occupies zero vertical space during ordinary play. Genie 4's bar
/// is always-visible; we go invisible-when-empty to keep the alpha UI quiet.
/// </para>
/// </summary>
public sealed class ScriptBarViewModel : ReactiveObject
{
    private GenieCore? _core;
    private DispatcherTimer? _pauseSync;

    /// <summary>
    /// Scripts currently in the engine's instance list (both <c>.cmd</c> and
    /// <c>.js</c>). Updated on <see cref="ScriptEngine.ScriptStarted"/> /
    /// <see cref="ScriptEngine.ScriptFinished"/> events, always marshalled to the
    /// UI thread before mutating the ObservableCollection (Avalonia's ItemsControl
    /// requires UI-thread modifications). Each item carries its language so the
    /// bar can tag <c>.js</c> rows.
    /// </summary>
    public ObservableCollection<ScriptBarItem> RunningScripts { get; } = new();

    /// <summary>
    /// True when at least one script is in <see cref="RunningScripts"/>.
    /// Bound to the strip's <c>IsVisible</c> in XAML so the bar collapses
    /// to zero height between sessions.
    /// </summary>
    [Reactive] public bool HasScripts { get; private set; }

    /// <summary>
    /// Sends <c>#stop &lt;name&gt;</c> to the script engine. Parameterised
    /// so the XAML <c>Button.Command</c> can pass the per-item name via
    /// <c>CommandParameter</c>.
    /// </summary>
    public ReactiveCommand<string, Unit> StopScriptCommand { get; }

    /// <summary>
    /// Opens the named script in the user-configured external editor (or
    /// the OS default if none configured). Wired in Task #188 (Edit Script).
    /// Parameterised same as <see cref="StopScriptCommand"/>.
    /// </summary>
    public ReactiveCommand<string, Unit> EditScriptCommand { get; }

    /// <summary>Toggle pause/resume for one script chip (#94). Mirrors the engine's
    /// per-script <see cref="ScriptEngine.PauseScript"/>/<c>ResumeScript</c>.</summary>
    public ReactiveCommand<ScriptBarItem, Unit> PauseResumeCommand { get; }

    /// <summary>Cycle one script chip's debug/trace level 0→1→5→10→0 (#94),
    /// pushing it to the engine via <see cref="ScriptEngine.SetTrace"/>.</summary>
    public ReactiveCommand<ScriptBarItem, Unit> CycleDebugCommand { get; }

    public ScriptBarViewModel()
    {
        StopScriptCommand = ReactiveCommand.Create<string, Unit>(name =>
        {
            if (!string.IsNullOrWhiteSpace(name))
                _core?.Scripts.Stop(name);
            return Unit.Default;
        });

        EditScriptCommand = ReactiveCommand.Create<string, Unit>(name =>
        {
            if (string.IsNullOrWhiteSpace(name) || _core is null) return Unit.Default;
            EditScript?.Invoke(name);
            return Unit.Default;
        });

        PauseResumeCommand = ReactiveCommand.Create<ScriptBarItem, Unit>(item =>
        {
            if (item is null || _core is null) return Unit.Default;
            if (item.IsPaused) { _core.Scripts.ResumeScript(item.Name); item.IsPaused = false; }
            else               { _core.Scripts.PauseScript(item.Name);  item.IsPaused = true;  }
            return Unit.Default;
        });

        CycleDebugCommand = ReactiveCommand.Create<ScriptBarItem, Unit>(item =>
        {
            if (item is null || _core is null) return Unit.Default;
            item.DebugLevel = item.DebugLevel switch { 0 => 1, 1 => 5, 5 => 10, _ => 0 };
            _core.Scripts.SetTrace(item.Name, item.DebugLevel);
            return Unit.Default;
        });
    }

    /// <summary>
    /// Raised when the user picks Edit on a script. The host
    /// (<see cref="MainWindowViewModel"/>) handles the actual editor
    /// launch since it owns the <c>DisplaySettings.EditorPath</c>
    /// setting plus the cross-platform <c>Process.Start</c> details.
    /// </summary>
    public event Action<string>? EditScript;

    /// <summary>
    /// Build one chip with its context-menu commands baked in. The menu items
    /// live in a popup (detached visual tree), so <c>$parent</c> reflection
    /// bindings can't reach the VM-level commands — each chip carries its own,
    /// all routed through the command pipeline like the Script Manager panel.
    /// </summary>
    private ScriptBarItem MakeItem(string name, bool isJs, int debugLevel)
    {
        var item = new ScriptBarItem(name, isJs) { DebugLevel = debugLevel };
        item.InitMenuCommands(
            pauseResume: () => Process($"#script pauseorresume {name}"),
            abort:       () => Process($"#stop {name}"),
            vars:        () => Process($"#script vars {name}"),
            trace:       () => Process($"#script trace {name}"),
            reload:      () => Process($"#script reload {name}"),
            edit:        () => EditScript?.Invoke(name),
            setDebug:    lvl => Process($"#script debug {lvl} {name}"));
        return item;
    }

    // PostCommand (#251): the command pipeline runs on the game thread.
    private void Process(string command) => _core?.PostCommand(command);

    public void Attach(GenieCore core)
    {
        _core = core;

        core.Scripts.ScriptStarted += name =>
        {
            // Resolve the language + current trace level now, on the engine
            // thread — an ultra-short .js script could finish before the
            // marshalled add runs. Seeding the level (vs assuming 0) makes the
            // chip correct for a script that was already tracing before its
            // chip existed (e.g. a restart); runtime #debug changes arrive via
            // DebugLevelChanged below.
            var isJs = core.Scripts.IsJavaScript(name);
            var lvl  = core.Scripts.GetTrace(name);
            Dispatcher.UIThread.Post(() =>
            {
                // Reload semantics: replace an existing same-named row.
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (RunningScripts[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
                RunningScripts.Add(MakeItem(name, isJs, lvl));
                HasScripts = RunningScripts.Count > 0;
            });
        };

        // Keep each chip's pause glyph honest: pause state can change from
        // outside the chip (the Script Manager panel, #script pauseorresume,
        // #pauseall). There's no engine event for pause, so sync from the
        // status snapshot on a slow poll — same pull pattern as the panel.
        _pauseSync?.Stop();
        _pauseSync = new DispatcherTimer(TimeSpan.FromSeconds(1),
                                         DispatcherPriority.Background,
                                         (_, _) =>
        {
            if (_core is null || RunningScripts.Count == 0) return;
            foreach (var s in _core.Scripts.GetStatuses())
                foreach (var item in RunningScripts)
                    if (item.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
                        item.IsPaused = s.Paused;
        });
        _pauseSync.Start();

        // Keep each chip's dbg:N readout in sync with the engine: fired when a
        // script changes its own level (#debug N) or when SetTrace is called.
        core.Scripts.DebugLevelChanged += (name, level) =>
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var item in RunningScripts)
                    if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        item.DebugLevel = level;
            });

        core.Scripts.ScriptFinished += name =>
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (RunningScripts[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
                HasScripts = RunningScripts.Count > 0;
            });
    }
}

/// <summary>One running-script chip in the script bar. Carries the language so
/// the bar can show a "js" tag; the Stop/Edit/Pause/Debug commands key off
/// <see cref="Name"/>. Reactive so per-chip Pause/Resume (#94) and the debug-level
/// readout update live.</summary>
public sealed class ScriptBarItem : ReactiveObject
{
    private readonly ObservableAsPropertyHelper<string> _debugLabel;
    private readonly ObservableAsPropertyHelper<string> _pauseGlyph;

    public ScriptBarItem(string name, bool isJavaScript)
    {
        Name = name;
        IsJavaScript = isJavaScript;
        _debugLabel = this.WhenAnyValue(x => x.DebugLevel)
            .Select(n => $"dbg:{n}")
            .ToProperty(this, x => x.DebugLabel);
        _pauseGlyph = this.WhenAnyValue(x => x.IsPaused)
            .Select(p => p ? "▶" : "⏸")
            .ToProperty(this, x => x.PauseGlyph);
    }

    public string Name { get; }
    public bool   IsJavaScript { get; }

    /// <summary>User-paused via the chip's ⏸/▶ button (mirrors the engine's
    /// per-script pause). Drives the button glyph.</summary>
    [Reactive] public bool IsPaused { get; set; }

    /// <summary>Per-script debug/trace level set via the chip (0 = off, cycles
    /// 0→1→5→10). <c>.js</c> scripts have no trace level, so the control is
    /// hidden for them.</summary>
    [Reactive] public int DebugLevel { get; set; }

    /// <summary>Debug button caption ("dbg:N"), live-tracking <see cref="DebugLevel"/>.</summary>
    public string DebugLabel => _debugLabel.Value;

    /// <summary>Pause button glyph — ⏸ while running, ▶ while paused.</summary>
    public string PauseGlyph => _pauseGlyph.Value;

    // ── context-menu commands (set by ScriptBarViewModel.MakeItem) ──────────
    // Null until InitMenuCommands runs; Avalonia treats a null Command as
    // disabled, so a chip constructed without them (tests) degrades safely.

    public ReactiveCommand<Unit, Unit>?   MenuPauseResumeCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>?   AbortCommand           { get; private set; }
    public ReactiveCommand<Unit, Unit>?   VarsCommand            { get; private set; }
    public ReactiveCommand<Unit, Unit>?   TraceCommand           { get; private set; }
    public ReactiveCommand<Unit, Unit>?   ReloadCommand          { get; private set; }
    public ReactiveCommand<Unit, Unit>?   EditCommand            { get; private set; }
    public ReactiveCommand<string, Unit>? SetDebugCommand        { get; private set; }

    public void InitMenuCommands(Action pauseResume, Action abort, Action vars,
                                 Action trace, Action reload, Action edit,
                                 Action<string> setDebug)
    {
        MenuPauseResumeCommand = ReactiveCommand.Create(pauseResume);
        AbortCommand           = ReactiveCommand.Create(abort);
        VarsCommand            = ReactiveCommand.Create(vars);
        TraceCommand           = ReactiveCommand.Create(trace);
        ReloadCommand          = ReactiveCommand.Create(reload);
        EditCommand            = ReactiveCommand.Create(edit);
        SetDebugCommand        = ReactiveCommand.Create<string>(lvl => setDebug(lvl ?? "0"));
    }
}
