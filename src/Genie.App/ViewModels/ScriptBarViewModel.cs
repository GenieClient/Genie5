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
    /// Genie 4's ten <c>#statusbar</c> slots (1-10, stored 0-indexed). Scripts
    /// write progress here via <c>#statusbar [N] {text}</c>; we compose the
    /// non-empty slots into <see cref="StatusText"/>. Cleared when the last
    /// script finishes so the strip returns to zero height during ordinary play.
    /// </summary>
    private readonly string[] _statusSlots = new string[10];

    /// <summary>
    /// The composed <c>#statusbar</c> text shown to the right of the Script Bar
    /// (#111) — the non-empty slots joined left-to-right. Empty when no script
    /// has set a status, which keeps <see cref="HasStatus"/> false and the
    /// status block collapsed.
    /// </summary>
    [Reactive] public string StatusText { get; private set; } = "";

    /// <summary>True when any status slot is non-empty. Bound to the status
    /// block's <c>IsVisible</c> so it shows only once a script writes one.</summary>
    [Reactive] public bool HasStatus { get; private set; }

    /// <summary>
    /// Apply a <c>#statusbar</c> write (Genie 4 <c>#statusbar [N] {text}</c>),
    /// routed from <see cref="GenieCore.StatusBarRequested"/>. <paramref name="index"/>
    /// is the 1-10 slot; out-of-range indices clamp to 1. Must be called on the
    /// UI thread (the caller marshals) since it mutates reactive state.
    /// </summary>
    public void SetStatus(int index, string text)
    {
        var slot = index is >= 1 and <= 10 ? index - 1 : 0;
        _statusSlots[slot] = text ?? "";
        RecomposeStatus();
    }

    private void RecomposeStatus()
    {
        StatusText = string.Join("   ", _statusSlots.Where(s => !string.IsNullOrEmpty(s)));
        HasStatus  = StatusText.Length > 0;
    }

    private void ClearStatus()
    {
        Array.Clear(_statusSlots, 0, _statusSlots.Length);
        StatusText = "";
        HasStatus  = false;
    }

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

    public void Attach(GenieCore core)
    {
        _core = core;

        core.Scripts.ScriptStarted += name =>
        {
            // Resolve the language now, on the engine thread — an ultra-short
            // .js script could finish before the marshalled add runs.
            var isJs = core.Scripts.IsJavaScript(name);
            Dispatcher.UIThread.Post(() =>
            {
                // Reload semantics: replace an existing same-named row.
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (RunningScripts[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
                RunningScripts.Add(new ScriptBarItem(name, isJs));
                HasScripts = RunningScripts.Count > 0;
            });
        };

        core.Scripts.ScriptFinished += name =>
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = RunningScripts.Count - 1; i >= 0; i--)
                    if (RunningScripts[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        RunningScripts.RemoveAt(i);
                HasScripts = RunningScripts.Count > 0;
                // Once nothing is running, drop any leftover #statusbar text so
                // the strip collapses to zero height during ordinary play (#111).
                if (!HasScripts) ClearStatus();
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
}
