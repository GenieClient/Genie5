using System;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Genie.Core;
using Genie.Core.Config;
using ReactiveUI;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the Experience dock panel. Content is pushed by the Experience
/// extension via the host's <c>SetWindow("Experience", …)</c> — the App doesn't
/// parse exp data itself, it just renders whatever the tracker produces. This is
/// the named-window seam that keeps trackers free of any UI dependency.
///
/// <para>Rendered as a collection of <see cref="TextLine"/> (not a single string)
/// so each row runs through the same tokenizer the game/stream windows use —
/// that's what lets user highlights fire on the Experience window (#144).</para>
///
/// <para>The panel's density slider (public #125) and the Track-gain checkbox
/// (#144) each drive their <c>#config</c> value directly, so the control, the
/// command line, and settings.cfg all stay in sync.</para>
/// </summary>
public class ExperienceViewModel : ReactiveObject
{
    private GenieCore? _core;
    private double _densityValue;
    private int _appliedLevel = -1;
    private bool _trackGain;
    private bool _g4Layout;
    private bool _showConfigBar = true;
    private int _sortIndex = 2;
    private bool _echoExp;
    private bool _showRested;
    private IDisposable? _configSubscription;

    /// <summary>Sort-mode names for the config-bar dropdown, indexed by the
    /// <c>experiencesort</c> value (Genie 4 EXPTracker's SortType, public #272).</summary>
    public static string[] SortModes { get; } =
        { "A to Z", "Left to Right", "Learning Rate", "Learning Rate Rev" };

    /// <summary>Stop names for the 0–4 density slider, indexed by level.</summary>
    private static readonly string[] LevelNames =
        { "Full", "No count", "Numbers only", "Short names", "Brief" };

    private const string Placeholder = "(no experience data yet — train a skill, or type 'exp')";

    /// <summary>Canonical window id these rows tokenize as, for per-window
    /// highlight scoping. Without it TextLine defaults to "main", so a rule
    /// scoped to "Experience" never painted here — and a rule scoped to only
    /// "main" wrongly did (public #232).</summary>
    private const string WindowId = "experience";

    /// <summary>The panel's lines. Rendered via <see cref="TextLine.Inlines"/>, so
    /// user highlight rules colour the Experience window exactly as they do the
    /// stream panels (#144).</summary>
    public ObservableCollection<TextLine> Lines { get; } = new() { new TextLine(Placeholder, StreamColor.Main, Window: WindowId) };

    /// <summary>Slider position (0 Full … 4 Brief). Snapped to whole steps by the
    /// slider; a new level is applied + persisted <b>quietly</b> — straight to config,
    /// not through <c>#config</c> — so dragging doesn't spam the Game window with
    /// "[config] … (saved)" lines. The config change still fires the tracker notify,
    /// which re-renders the panel live.</summary>
    public double DensityValue
    {
        get => _densityValue;
        set
        {
            this.RaiseAndSetIfChanged(ref _densityValue, value);
            this.RaisePropertyChanged(nameof(DensityLabel));

            var level = Math.Clamp((int)Math.Round(value), 0, 4);
            if (_core is not null && level != _appliedLevel)
            {
                _appliedLevel = level;
                _core.Config.SetSetting("experiencedensity", level.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Human-readable name of the current density stop, shown beside the slider.</summary>
    public string DensityLabel => LevelNames[Math.Clamp((int)Math.Round(_densityValue), 0, 4)];

    /// <summary>Track-gain toggle (#144). Writes <c>experiencetrackgain</c> quietly (like
    /// the density slider); the config change fires the tracker notify, which re-renders
    /// the panel with the gain column + session total.</summary>
    public bool TrackGain
    {
        get => _trackGain;
        set
        {
            this.RaiseAndSetIfChanged(ref _trackGain, value);
            if (_core is not null && _core.Config.ExperienceTrackGain != value)
            {
                _core.Config.SetSetting("experiencetrackgain", value.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>G4-layout toggle. Writes <c>experienceg4layout</c> quietly (like the
    /// density slider); the config change fires the tracker notify, which re-renders the
    /// panel with the "Learning Skills" summary as a footer instead of the header.</summary>
    public bool G4Layout
    {
        get => _g4Layout;
        set
        {
            this.RaiseAndSetIfChanged(ref _g4Layout, value);
            if (_core is not null && _core.Config.ExperienceG4Layout != value)
            {
                _core.Config.SetSetting("experienceg4layout", value.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Config-bar (Density / Track gain / G4 layout strip) visibility.
    /// Toggled from the window right-click menu ("Show Config Bar"); writes
    /// <c>experienceconfigbar</c> quietly like the strip's own controls. Pure
    /// UI — hiding the bar reclaims the row, the settings behind it still apply.</summary>
    public bool ShowConfigBar
    {
        get => _showConfigBar;
        set
        {
            this.RaiseAndSetIfChanged(ref _showConfigBar, value);
            if (_core is not null && _core.Config.ExperienceConfigBar != value)
            {
                _core.Config.SetSetting("experienceconfigbar", value.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Sort-mode dropdown index == the <c>experiencesort</c> value
    /// (public #272). Written quietly like the density slider; the config change
    /// fires the tracker notify, which re-renders the panel in the new order.</summary>
    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _sortIndex, value);
            var mode = Math.Clamp(value, 0, 3);
            if (_core is not null && _core.Config.ExperienceSort != mode)
            {
                _core.Config.SetSetting("experiencesort", mode.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Pulse-echo toggle (public #272). Writes <c>experienceecho</c>
    /// quietly; the tracker flushes "Learned:"/"Pulsed:" lines on each prompt
    /// while it's on.</summary>
    public bool EchoExp
    {
        get => _echoExp;
        set
        {
            this.RaiseAndSetIfChanged(ref _echoExp, value);
            if (_core is not null && _core.Config.ExperienceEcho != value)
            {
                _core.Config.SetSetting("experienceecho", value.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Rested-EXP summary toggle (public #272). Writes
    /// <c>experiencerested</c> quietly; the tracker adds the stored/usable/refresh
    /// line under the summary while it's on.</summary>
    public bool ShowRested
    {
        get => _showRested;
        set
        {
            this.RaiseAndSetIfChanged(ref _showRested, value);
            if (_core is not null && _core.Config.ExperienceRested != value)
            {
                _core.Config.SetSetting("experiencerested", value.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    public void Attach(GenieCore core)
    {
        _core = core;
        SeedFromConfig(core);

        // Follow these settings when they change somewhere other than this bar:
        // a typed #config, or #config load. GenieCore re-renders the rows on the
        // Trackers notice, but nothing told the bar, so
        // `#config experienceconfigbar off` saved the value and left the strip on
        // screen until the next start, and a typed density moved the rows while
        // the slider and its label stayed where they were. The Objects window
        // follows its own settings the same way. One subscription per attached
        // session: a re-attach drops the previous session's.
        _configSubscription?.Dispose();
        _configSubscription = Observable
            .FromEvent<Action<ConfigFieldUpdated>, ConfigFieldUpdated>(
                h => core.Config.ConfigChanged += h,
                h => core.Config.ConfigChanged -= h)
            .Where(f => f == ConfigFieldUpdated.Trackers)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => SeedFromConfig(core));

        core.SetPluginWindow += (window, content) =>
        {
            if (!string.Equals(window, "Experience", StringComparison.OrdinalIgnoreCase)) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SetContent(content));
        };

        // ── Highlight-rule changes: re-tokenize the already-rendered rows so a
        // newly added/edited rule repaints the Experience window immediately —
        // not just after the next exp push. Without this the Game window (which
        // subscribes in GameTextViewModel) repaints on Apply but this panel sits
        // stale, which reads as "highlights don't work on the Experience window".
        Highlighting.UserHighlights.RulesChanged += RetokenizeLines;
    }

    /// <summary>Set every control of the bar from config without writing anything
    /// back. Each setter writes only when its value differs from config (the
    /// density slider compares against <see cref="_appliedLevel"/>), and here they
    /// are equal by construction, so a control that wrote the setting itself sees
    /// no change and nothing bounces. Raises a change only for what changed.</summary>
    private void SeedFromConfig(GenieCore core)
    {
        var config = core.Config;
        _appliedLevel = config.ExperienceDensity;
        DensityValue  = config.ExperienceDensity;
        this.RaiseAndSetIfChanged(ref _trackGain,     config.ExperienceTrackGain, nameof(TrackGain));
        this.RaiseAndSetIfChanged(ref _g4Layout,      config.ExperienceG4Layout,  nameof(G4Layout));
        this.RaiseAndSetIfChanged(ref _showConfigBar, config.ExperienceConfigBar, nameof(ShowConfigBar));
        this.RaiseAndSetIfChanged(ref _sortIndex,     Math.Clamp(config.ExperienceSort, 0, 3), nameof(SortIndex));
        this.RaiseAndSetIfChanged(ref _echoExp,       config.ExperienceEcho,      nameof(EchoExp));
        this.RaiseAndSetIfChanged(ref _showRested,    config.ExperienceRested,    nameof(ShowRested));
    }

    /// <summary>Force each existing <see cref="TextLine"/> to re-tokenize by
    /// replacing it with a fresh instance carrying identical content. The
    /// <see cref="ObservableCollection{T}"/> raises Replace events, the
    /// ItemsControl re-binds each item, and <see cref="TextLine.Inlines"/> is
    /// re-evaluated against the current highlight rule set. Mirrors
    /// <c>GameTextViewModel.RetokenizeAllLines</c>.</summary>
    private void RetokenizeLines()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                // `with { }` clones the record — a NEW instance (so the Replace
                // event fires and Inlines re-evaluates) that keeps every other
                // field, including the Window id the scoping depends on.
                Lines[i] = Lines[i] with { };
            }
        });
    }

    /// <summary>Replace the panel with the tracker's latest render, one
    /// <see cref="TextLine"/> per row so highlights tokenize per line.</summary>
    private void SetContent(string content)
    {
        Lines.Clear();
        if (string.IsNullOrEmpty(content))
        {
            Lines.Add(new TextLine(Placeholder, StreamColor.Main, Window: WindowId));
            return;
        }
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            Lines.Add(new TextLine(line, StreamColor.Main, Window: WindowId));
    }
}
