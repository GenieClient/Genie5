using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Genie 4's ten <c>#statusbar</c> slots (1-10), rendered as a positional row
/// directly under the vitals Status Bar (#111 follow-up; the text originally
/// rode on the Script Bar). Positional means <c>#statusbar 5 {text}</c> stays
/// in cell 5 regardless of what the other slots hold — Genie 4 scripts that
/// use the slots as columns keep their columns. Text persists until
/// overwritten or cleared — a bare <c>#statusbar N</c> clears slot N,
/// <c>#statusbar clearall</c> empties all ten — matching Genie 4's StatusStrip
/// labels: deliberately NOT cleared when the last script finishes.
/// </summary>
public sealed class StatusSlotsViewModel : ReactiveObject
{
    /// <summary>The ten slot cells, index 0 = slot 1. Fixed size — the XAML
    /// UniformGrid lays them out as equal columns.</summary>
    public IReadOnlyList<StatusSlot> Slots { get; } =
        Enumerable.Range(1, 10).Select(n => new StatusSlot(n)).ToArray();

    /// <summary>True while the row should be visible: any slot has text, or
    /// every slot cleared less than <see cref="CollapseLinger"/> ago. The
    /// linger stops scripts that clear-then-rewrite in a tight loop from
    /// collapsing and re-expanding the row every few milliseconds, which
    /// shifted the whole layout above it.</summary>
    [Reactive] public bool HasAny { get; private set; }

    /// <summary>How long the emptied row stays visible before collapsing.
    /// Measured from the write that cleared the last non-empty slot.</summary>
    public static readonly TimeSpan CollapseLinger = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _collapseTimer;

    public StatusSlotsViewModel()
    {
        _collapseTimer = new DispatcherTimer { Interval = CollapseLinger };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            HasAny = Slots.Any(s => s.Text.Length > 0);
        };
    }

    /// <summary>
    /// Apply a <c>#statusbar</c> write (Genie 4 <c>#statusbar [N] {text}</c>),
    /// routed from <see cref="Genie.Core.GenieCore.StatusBarRequested"/>.
    /// <paramref name="index"/> is the 1-10 slot; out-of-range indices clamp
    /// to 1. Empty text clears the slot. Must be called on the UI thread (the
    /// caller marshals) since it mutates reactive state.
    /// </summary>
    public void SetStatus(int index, string text)
    {
        var slot = index is >= 1 and <= 10 ? index - 1 : 0;
        Slots[slot].Text = text ?? "";
        if (Slots.Any(s => s.Text.Length > 0))
        {
            _collapseTimer.Stop();
            HasAny = true;
        }
        else if (HasAny && !_collapseTimer.IsEnabled)
        {
            // Just went empty: hold the row open for the linger window. A
            // repopulating write cancels the timer above; further clears while
            // pending don't restart it, so an abandoned bar still collapses
            // CollapseLinger after it first emptied.
            _collapseTimer.Start();
        }
    }
}

/// <summary>One positional <c>#statusbar</c> cell.</summary>
public sealed class StatusSlot : ReactiveObject
{
    public StatusSlot(int number) => Number = number;

    /// <summary>1-based slot number, surfaced in the cell's tooltip.</summary>
    public int Number { get; }

    [Reactive] public string Text { get; set; } = "";
}
