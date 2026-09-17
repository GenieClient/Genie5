using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;   // FindResource — the TextPrimary fallback below
using Genie.Core;
using Genie.Core.Combat;
using Genie.Core.Config;
using Genie.Core.Events;
using Genie.Core.Models;
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
/// <para><b>Assess rows (public #313).</b> When the character has run
/// <c>assess</c>, the panel switches from the room-objs phrase list to the
/// structured rows the engine parsed from the <c>assess</c> stream — same
/// creatures, but with the game's own ordering and numbering, each one's
/// balance/position/range, the live <c>&lt;crtrStatus&gt;</c> flags joined on
/// exist id, and the server's own <c>face</c>/<c>look</c> commands as click
/// targets. It falls back to the phrase list when there is no assess reading
/// (none yet this room, or the room changed and the snapshot was dropped).
/// The two are never shown together — they are the same creatures, and the
/// phrase list has no ids to join on when the room holds several of the same
/// creature.</para>
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

    // ── Assess rows (public #313) ─────────────────────────────────────────

    /// <summary>The latest assess reading as structured rows, in the game's own
    /// assess order. Empty when no assess is live for this room.</summary>
    public ObservableCollection<AssessRowItem> AssessRows { get; } = new();

    /// <summary>True when <see cref="AssessRows"/> has something to show — the
    /// panel then renders those instead of the room-objs phrase list.</summary>
    [Reactive] public bool HasAssess { get; private set; }

    /// <summary>The "You (solidly balanced) are facing …" opener, rendered
    /// above the rows. Empty when the block carried none.</summary>
    [Reactive] public string SelfLine { get; private set; } = "";

    /// <summary>Panel header — flags when the rows are assess-sourced so the
    /// ordering and numbering are explained rather than mysterious.</summary>
    [Reactive] public string HeaderText { get; private set; } = "MOBS (0)";

    /// <summary>The "No creatures here." placeholder shows only when BOTH
    /// sources are empty.</summary>
    [Reactive] public bool ShowEmptyPlaceholder { get; private set; } = true;

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

        // Assess rows (public #313). The engine subscribed in GenieCore's ctor,
        // so by the time these UI-thread handlers run, State.Combat.Assess is
        // already updated — the same ordering guarantee the room-objs path
        // above relies on.
        //
        //   assess text  — a row (or the self line) just landed
        //   clearStream  — a new assess opened; drop the old rows immediately
        //   nav          — room change; the engine cleared the snapshot
        //   crtrStatus   — the live flags half of a row changed
        core.GameEvents
            .Where(e => e is TextEvent { Stream: AssessTracker.StreamId }
                          or ClearStreamEvent { StreamId: AssessTracker.StreamId }
                          or NavEvent
                          or CreatureStatusEvent)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshAssess());

        ReloadPatterns();
    }

    /// <summary>Rebuild the assess rows from the live snapshot, joining each
    /// row's <c>&lt;crtrStatus&gt;</c> flags by exist id.</summary>
    private void RefreshAssess()
    {
        var combat = _core?.State.Combat;
        AssessRows.Clear();

        // Rows come from the snapshot; their flags come from the live
        // crtrStatus map, which keeps updating after the assess — so the join
        // happens here, on every rebuild, rather than being baked into the row.
        if (combat is not null)
            foreach (var row in combat.Assess.Rows)
                AssessRows.Add(new AssessRowItem(
                    row,
                    combat.TryGetCreatureStatus(row.ExistId, out var reading)
                        ? reading
                        : (CreatureStatusReading?)null,
                    this));

        var self = combat?.Assess.Self;
        SelfLine  = self is null        ? ""
                  : self.FacingName.Length > 0
                    ? $"You ({self.Balance}) — facing {self.FacingName} ({self.FacingNumber})"
                    : $"You ({self.Balance})";
        HasAssess = AssessRows.Count > 0;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        HeaderText = HasAssess
            ? $"MOBS ({AssessRows.Count}) · ASSESS"
            : $"MOBS ({Count})";
        ShowEmptyPlaceholder = IsEmpty && !HasAssess;
    }

    /// <summary>Send a row's command exactly as if the user had clicked the
    /// server's own link in the game window — one dispatch path, so the echo,
    /// the command queue and script visibility all behave identically.</summary>
    internal void SendRowCommand(string command, string display)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        Genie.App.Highlighting.DefaultHighlights.OnLinkClicked?.Invoke(command, display);
    }

    private void Refresh()
    {
        var creatures = _core?.State.Room.Creatures ?? Array.Empty<string>();
        Mobs.Clear();
        foreach (var c in creatures) Mobs.Add(new MobItem(c, this));
        Count   = Mobs.Count;
        IsEmpty = Mobs.Count == 0;
        UpdateHeader();
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

/// <summary>
/// One assess row (public #313): the creature, its assess number, the
/// balance/position/range the server reported, and the live
/// <c>&lt;crtrStatus&gt;</c> flags joined on exist id.
///
/// The row carries its own commands so its click and context menu need no
/// visual-tree traversal out of the popup — the same shape as
/// <see cref="MobItem"/>. Commands are the server's OWN link commands
/// ("face #45029702"), which work as typed input, so nothing here depends on
/// new server support.
/// </summary>
public sealed class AssessRowItem
{
    private readonly AssessRow _row;

    /// <summary>The game's own ordinal, rendered as the row's "2." prefix —
    /// this is the number the player sees in the game's own output.</summary>
    public int    Number  { get; }
    public string Name    { get; }

    /// <summary>Balance · position · range, with empty parts dropped (an
    /// unrecognised line shape leaves them empty rather than guessing).</summary>
    public string Detail  { get; }

    /// <summary>Live crtrStatus flags as a short badge ("hostile · flying"),
    /// or empty when the creature has sent no status this room.</summary>
    public string Flags   { get; }

    /// <summary>Whether to show the flags badge at all.</summary>
    public bool   HasFlags => Flags.Length > 0;

    /// <summary>Name tokenized through the shared highlight pipeline, so a user
    /// rule on a creature name paints it here exactly as in the Main and Mobs
    /// lists.</summary>
    public IReadOnlyList<Avalonia.Controls.Documents.Inline> Inlines { get; }

    /// <summary>Default row colour = the <c>creatures</c> preset (#236), the
    /// same single knob the phrase list and Main's MonsterBold layer use.</summary>
    public Avalonia.Media.IBrush? Foreground { get; }

    /// <summary>Tooltip: the raw line, always kept, so nothing the server said
    /// is lost to the structured view.</summary>
    public string RawText => _row.RawText;

    public ReactiveCommand<Unit, Unit> FaceCommand   { get; }
    public ReactiveCommand<Unit, Unit> LookCommand   { get; }
    public ReactiveCommand<Unit, Unit> IgnoreCommand { get; }

    public AssessRowItem(AssessRow row, CreatureStatusReading? status, MobsViewModel owner)
    {
        _row   = row;
        Number = row.Number;
        Name   = row.Name;

        Detail = string.Join(" · ", new[] { row.Balance, row.Position, row.Range }
                                    .Where(p => !string.IsNullOrWhiteSpace(p)));

        Flags = status is null ? "" : string.Join(" · ", FlagWords(status.Value));

        Inlines    = Genie.App.Highlighting.DefaultHighlights.Tokenize(row.Name, window: "mobs");
        Foreground = Genie.App.Highlighting.DefaultHighlights.CreaturesPresetBrush
                     ?? Avalonia.Application.Current?.FindResource(Theming.ThemeKeys.TextPrimary)
                        as Avalonia.Media.IBrush;

        // A row with no exist id (no link on the line) has no commands to send —
        // canExecute keeps the menu items and the click inert rather than
        // sending a malformed "face #".
        var canAct = Observable.Return(row.FaceCommand.Length > 0);
        var canLook = Observable.Return(row.LookCommand.Length > 0);

        FaceCommand = ReactiveCommand.Create(
            () => owner.SendRowCommand(row.FaceCommand, "face " + row.Name), canAct);
        LookCommand = ReactiveCommand.Create(
            () => owner.SendRowCommand(row.LookCommand, "look " + row.Name), canLook);
        IgnoreCommand = ReactiveCommand.Create(() => owner.IgnoreCreature(row.Name));
    }

    /// <summary>crtrStatus is a snapshot of three independent flags; only the
    /// ones that are true are worth screen space. <c>Disengaged</c> is YOU
    /// relative to the creature (false = engaged), so it reads as "engaged"
    /// when clear — that is the state a player is watching for.</summary>
    private static IEnumerable<string> FlagWords(CreatureStatusReading s)
    {
        if (s.Hostile)   yield return "hostile";
        if (s.Flying)    yield return "flying";
        yield return s.Disengaged ? "disengaged" : "engaged";
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
