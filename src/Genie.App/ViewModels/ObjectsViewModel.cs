using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia.Controls;   // FindResource — the TextPrimary fallback below
using Genie.App.Highlighting;
using Genie.Core;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the dockable Objects panel — what's on the ground in the room, one
/// entry per line (Genie 3/4 "Objects window" parity, issue #329). Completes
/// the trio started in #86: Mobs (creatures), Players, Objects.
///
/// Sourced from the <c>room objs</c> component — the same "You also see …"
/// line behind <c>$roomobjs</c> and the Room panel's objects field. Genie 4's
/// window listed EVERYTHING on that line, creatures included, so this does
/// too; creature rows are marked (<see cref="ObjectRow.IsCreature"/>) from the
/// component's bold spans and painted in the <c>creatures</c> preset, the same
/// single colour knob the Mobs panel and the Main window's MonsterBold layer
/// use. Splitting the line into rows is best-effort — see
/// <see cref="RoomObjectSplitter"/> for why it has to be, and what it can get
/// wrong.
///
/// Hidden by default; re-open via Window → Objects.
/// </summary>
public sealed class ObjectsViewModel : ReactiveObject
{
    /// <summary>One row per object in the room. Rebuilt on every
    /// <c>room objs</c> update.</summary>
    public ObservableCollection<ObjectRow> Objects { get; } = new();

    /// <summary>The raw <c>room objs</c> text, e.g. "You also see a stone urn
    /// and a wide arch." Kept alongside the parsed rows for Copy and for
    /// eyeballing a split that looks wrong.</summary>
    [Reactive] public string RawText { get; private set; } = "";

    /// <summary>Object count — drives the panel header.</summary>
    [Reactive] public int  Count   { get; private set; }

    /// <summary>True when the room holds nothing — drives the empty-state
    /// placeholder.</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    // Last-seen component, so a highlight/preset rules change can repaint
    // without waiting for the next room (same contract as the Room panel).
    private string _lastContent = "";
    private IReadOnlyList<BoldSpan>? _lastBold;

    public void Attach(GenieCore core)
    {
        // Two carriers, one subscription so they stay ordered on the UI thread:
        //   • "room objs"  → (re)populate the list.
        //   • "room title" → a NEW room arrived; clear first.
        // DR does send an EMPTY room objs for a bare room rather than omitting
        // it (unlike room players, which it drops entirely — see
        // PlayersViewModel), so the title clear is belt-and-braces: it costs
        // one assignment and means a room that somehow omits the component
        // can't leave the previous room's contents on screen.
        core.GameEvents
            .OfType<ComponentEvent>()
            .Where(e => string.Equals(e.ComponentId, "room objs",  StringComparison.OrdinalIgnoreCase)
                     || string.Equals(e.ComponentId, "room title", StringComparison.OrdinalIgnoreCase))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var isObjs = string.Equals(e.ComponentId, "room objs", StringComparison.OrdinalIgnoreCase);
                Refresh(isObjs ? e.Content : "", isObjs ? e.BoldSpans : null);
            });

        // Preset / highlight / name rule edits repaint the rows in place, so
        // the creatures-preset colour and tokenized inlines follow an edit
        // live — the same seam the Mobs, Room and Game windows use.
        UserHighlights.RulesChanged += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Refresh(_lastContent, _lastBold));
    }

    private void Refresh(string? content, IReadOnlyList<BoldSpan>? boldSpans)
    {
        _lastContent = content ?? "";
        _lastBold    = boldSpans;
        RawText      = _lastContent;

        Objects.Clear();
        foreach (var span in RoomObjectSplitter.Split(_lastContent))
            Objects.Add(new ObjectRow(_lastContent, span, boldSpans));

        Count   = Objects.Count;
        IsEmpty = Objects.Count == 0;
    }
}

/// <summary>
/// One object row. Creature rows (a bold span from the component starts inside
/// the row) carry the <c>creatures</c> preset colour so the panel reads the
/// same as Mobs and the Room panel; everything else takes the panel's normal
/// text colour. The row's bold spans are re-based to row-local offsets and fed
/// through the shared highlight pipeline, so a user rule on an item or
/// creature name paints here exactly as it does in the game window.
/// </summary>
public sealed class ObjectRow
{
    public string Text { get; }

    /// <summary>True when this row is one of the component's bolded creatures
    /// rather than a plain ground object.</summary>
    public bool IsCreature { get; }

    public IReadOnlyList<Avalonia.Controls.Documents.Inline> Inlines { get; }

    /// <summary>Always non-null: a null Foreground on the bound TextBlock
    /// renders invisible glyphs rather than inheriting (the trap MobItem
    /// documents), so the theme fallback is resolved here.</summary>
    public Avalonia.Media.IBrush? Foreground { get; }

    public ObjectRow(string content, RoomObjectSpan span, IReadOnlyList<BoldSpan>? boldSpans)
    {
        Text = content.Substring(span.Start, span.Length);

        var rowBold = RebaseBold(span, boldSpans);
        IsCreature  = rowBold is { Count: > 0 };
        Inlines     = DefaultHighlights.Tokenize(Text, boldSpans: rowBold, window: "objects");

        var themeDefault = Avalonia.Application.Current?.FindResource(Theming.ThemeKeys.TextPrimary)
                           as Avalonia.Media.IBrush;
        Foreground = IsCreature
            ? DefaultHighlights.CreaturesPresetBrush ?? themeDefault
            : themeDefault;
    }

    /// <summary>Clip the component's bold spans to this row and shift them to
    /// row-local offsets. Null when the row holds no bold at all — the common
    /// case for a plain object, and what keeps <see cref="IsCreature"/> false.</summary>
    private static IReadOnlyList<BoldSpan>? RebaseBold(RoomObjectSpan span, IReadOnlyList<BoldSpan>? boldSpans)
    {
        if (boldSpans is null || boldSpans.Count == 0) return null;

        List<BoldSpan>? rebased = null;
        foreach (var b in boldSpans)
        {
            var from = Math.Max(b.Start, span.Start);
            var to   = Math.Min(b.Start + b.Length, span.End);
            if (to <= from) continue;
            (rebased ??= new List<BoldSpan>()).Add(new BoldSpan(from - span.Start, to - from));
        }
        return rebased;
    }
}
