using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;   // FindResource — the TextPrimary fallback below
using Genie.Core;
using Genie.Core.Config;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the dockable Mobs panel — the creatures currently in the room, one per
/// line (Genie 3/4 "Mobs window" parity, issue #86).
///
/// Sourced from the engine's already-filtered <c>Room.Creatures</c> list (the
/// bold creature phrases from <c>room objs</c> minus the ignore list — the same
/// data behind <c>$monsterlist</c>/<c>$monstercount</c>), NOT the raw
/// room-objects text, which also contains non-creature ground items.
///
/// Also hosts the in-panel <b>ignore-list editor</b>: the server bolds
/// familiars, pets, and spell entities identically to hostile creatures, so the
/// only way to keep them out of the panel is the <c>monstercountignorelist</c>
/// regex. The gear button toggles an editor showing the list one alternative
/// per row; right-clicking a mob adds its exact (escaped) phrase. Edits go
/// through <see cref="GenieConfig.SetSetting"/> so the typed
/// <c>#config monstercountignorelist</c> path and this UI stay one mechanism —
/// both fire <see cref="ConfigFieldUpdated.MonsterIgnore"/>, GenieCore
/// re-filters Room.Creatures, and this VM reloads its rows.
///
/// Hidden by default; re-open via Window → Mobs.
/// </summary>
public sealed class MobsViewModel : ReactiveObject
{
    private GenieCore? _core;

    /// <summary>Creature phrases in the room, e.g. "a brown lynx that is
    /// sleeping". Rebuilt on every <c>room objs</c> update.</summary>
    public ObservableCollection<MobItem> Mobs { get; } = new();

    /// <summary>Creature count — mirrors <c>$monstercount</c>. Drives the panel
    /// header.</summary>
    [Reactive] public int  Count   { get; private set; }

    /// <summary>True when no creatures are present — drives the empty-state
    /// placeholder.</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    // ── Ignore-list editor ────────────────────────────────────────────────

    /// <summary>Gear-button state — shows/hides the editor pane.</summary>
    [Reactive] public bool IsEditingIgnores { get; set; }

    /// <summary>The ignore list, one top-level regex alternative per row.
    /// Rebuilt whenever <c>monstercountignorelist</c> changes (this editor,
    /// a typed <c>#config</c>, or <c>#config load</c>).</summary>
    public ObservableCollection<IgnorePatternItem> IgnorePatterns { get; } = new();

    /// <summary>The add-row TextBox contents.</summary>
    [Reactive] public string NewPattern  { get; set; } = "";

    /// <summary>Validation message under the add row; empty when the last
    /// edit was accepted.</summary>
    [Reactive] public string EditorError { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> AddPatternCommand      { get; }
    public ReactiveCommand<Unit, Unit> RestoreDefaultsCommand { get; }

    public MobsViewModel()
    {
        AddPatternCommand      = ReactiveCommand.Create(AddPattern);
        RestoreDefaultsCommand = ReactiveCommand.Create(RestoreDefaults);
    }

    public void Attach(GenieCore core)
    {
        _core = core;

        // "room objs" is the carrier for creatures. GameStateEngine processes
        // the same event first (it subscribes in GenieCore's ctor, before the
        // App attaches) and replaces Room.Creatures, so by the time this
        // UI-thread handler runs the filtered list is ready to read — the same
        // ordering guarantee the $monsterlist script sync relies on.
        core.GameEvents
            .OfType<ComponentEvent>()
            .Where(e => string.Equals(e.ComponentId, "room objs", StringComparison.OrdinalIgnoreCase))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => Refresh());

        // Preset / highlight / name rule edits (the Presets and Highlights
        // panels + rule-file live reload all fire RulesChanged): rebuild the
        // rows so their creatures-preset foreground (#236) and tokenized
        // inlines pick up the new colours — same repaint contract as the
        // Game/Experience windows.
        Highlighting.UserHighlights.RulesChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

        // Ignore-list changes from ANY source — GenieCore recomputed
        // Room.Creatures before this fires (it subscribed in its ctor), so
        // refresh both the mob rows and the pattern rows.
        Observable.FromEvent<Action<ConfigFieldUpdated>, ConfigFieldUpdated>(
                h => core.Config.ConfigChanged += h,
                h => core.Config.ConfigChanged -= h)
            .Where(f => f == ConfigFieldUpdated.MonsterIgnore)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => { ReloadPatterns(); Refresh(); });

        ReloadPatterns();
    }

    private void Refresh()
    {
        var creatures = _core?.State.Room.Creatures ?? Array.Empty<string>();
        Mobs.Clear();
        foreach (var c in creatures) Mobs.Add(new MobItem(c, this));
        Count   = Mobs.Count;
        IsEmpty = Mobs.Count == 0;
    }

    /// <summary>Right-click → Ignore on a mob row: append the exact phrase,
    /// regex-escaped, to the ignore list.</summary>
    internal void IgnoreCreature(string phrase)
    {
        var trimmed = phrase.Trim();
        if (trimmed.Length == 0) return;
        AppendPattern(Regex.Escape(trimmed));
    }

    private void AddPattern()
    {
        var pattern = NewPattern.Trim();
        if (pattern.Length == 0) return;

        try { _ = new Regex(pattern); }
        catch (ArgumentException ex)
        {
            EditorError = "Invalid regex: " + ex.Message;
            return;
        }
        // Unbalanced braces survive regex validation ('{' without a quantifier
        // is a literal) but would corrupt the brace-delimited settings.cfg
        // line on the next Save/Load round-trip. Escape them instead.
        if (pattern.Count(c => c == '{') != pattern.Count(c => c == '}'))
        {
            EditorError = "Unbalanced { } — write \\{ or \\} to match a literal brace.";
            return;
        }

        if (AppendPattern(pattern)) NewPattern = "";
    }

    /// <summary>Append one alternative and persist. False when it was already
    /// present (no-op).</summary>
    private bool AppendPattern(string pattern)
    {
        var list = CurrentPatterns();
        if (list.Contains(pattern, StringComparer.Ordinal))
        {
            EditorError = "";
            return false;
        }
        list.Add(pattern);
        ApplyIgnoreList(list);
        return true;
    }

    internal void RemovePattern(IgnorePatternItem item)
    {
        var list = CurrentPatterns();
        if (!list.Remove(item.Pattern)) return;
        ApplyIgnoreList(list);
    }

    private void RestoreDefaults() =>
        ApplyIgnoreList(GenieConfig.SplitTopLevelAlternatives(GenieConfig.DefaultIgnoreMonsterList));

    private List<string> CurrentPatterns() =>
        GenieConfig.SplitTopLevelAlternatives(_core?.Config.IgnoreMonsterList ?? "");

    /// <summary>Join the rows back into the Genie 4 pipe-delimited regex and
    /// push it through the same SetSetting path as a typed <c>#config</c>.
    /// SetSetting fires <see cref="ConfigFieldUpdated.MonsterIgnore"/>, which
    /// recomputes Room.Creatures (GenieCore) and reloads this VM's rows.</summary>
    private void ApplyIgnoreList(IReadOnlyList<string> patterns)
    {
        if (_core is null) return;
        EditorError = "";
        _core.Config.SetSetting("monstercountignorelist", string.Join("|", patterns));
        _core.Config.Save();
    }

    private void ReloadPatterns()
    {
        IgnorePatterns.Clear();
        foreach (var p in CurrentPatterns())
            IgnorePatterns.Add(new IgnorePatternItem(p, this));
    }

}

/// <summary>One mob row. Carries its own Ignore command so the row's
/// right-click menu needs no visual-tree traversal out of the popup.</summary>
public sealed class MobItem
{
    public string Text { get; }
    /// <summary>The row tokenized through the shared highlight pipeline, so a
    /// user rule on a creature name colours it here too (the row's default
    /// foreground below covers whatever no rule claims).</summary>
    public IReadOnlyList<Avalonia.Controls.Documents.Inline> Inlines { get; }
    /// <summary>Default row colour = the <c>creatures</c> preset — the same
    /// single source the Main window's MonsterBold layer uses (#236), so one
    /// knob governs both. When the preset is "Default" the row falls back to
    /// the theme's normal text colour (matching Main's weight-only fallback;
    /// a null Foreground would render invisible glyphs, so the fallback is
    /// resolved here, not left to inheritance). Rows are rebuilt on
    /// <see cref="Highlighting.UserHighlights.RulesChanged"/>, so Presets-panel
    /// edits apply live.</summary>
    public Avalonia.Media.IBrush? Foreground { get; }
    public ReactiveCommand<Unit, Unit> IgnoreCommand { get; }

    public MobItem(string text, MobsViewModel owner)
    {
        Text          = text;
        Inlines       = Genie.App.Highlighting.DefaultHighlights.Tokenize(text, window: "mobs");
        Foreground    = Genie.App.Highlighting.DefaultHighlights.CreaturesPresetBrush
                        ?? Avalonia.Application.Current?.FindResource(Theming.ThemeKeys.TextPrimary)
                           as Avalonia.Media.IBrush;
        IgnoreCommand = ReactiveCommand.Create(() => owner.IgnoreCreature(text));
    }
}

/// <summary>One ignore-list editor row (a top-level regex alternative).</summary>
public sealed class IgnorePatternItem
{
    public string Pattern { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public IgnorePatternItem(string pattern, MobsViewModel owner)
    {
        Pattern       = pattern;
        RemoveCommand = ReactiveCommand.Create(() => owner.RemovePattern(this));
    }
}
