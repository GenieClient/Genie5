using System.Collections;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.Controls;

/// <summary>
/// Attached behaviour that keeps a ScrollViewer scrolled to the newest item
/// unless the user has manually scrolled up.
///
/// Usage (AXAML):
///   &lt;ScrollViewer x:Name="sv"
///                 controls:AutoScrollBehavior.ItemsSource="{Binding MyList}"&gt;
///
/// After setting ItemsSource, the ScrollViewer's Tag property is populated
/// with an AutoScrollState object. Bind to it for the overlay button:
///   &lt;Button IsVisible="{Binding #sv.Tag.IsScrolledUp}"
///           Command="{Binding #sv.Tag.JumpToBottomCommand}"/&gt;
/// </summary>
public static class AutoScrollBehavior
{
    public static readonly AttachedProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, IEnumerable?>(
            "ItemsSource", typeof(AutoScrollBehavior));

    /// <summary>When true, the ScrollViewer stops auto-following new items —
    /// the "Pause Scrolling" window-menu toggle. Bound to a reactive flag on
    /// the dockable; turning it off snaps back to the newest line.</summary>
    public static readonly AttachedProperty<bool> PausedProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>(
            "Paused", typeof(AutoScrollBehavior));

    static AutoScrollBehavior()
    {
        ItemsSourceProperty.Changed.AddClassHandler<ScrollViewer>(OnItemsSourceChanged);
        PausedProperty.Changed.AddClassHandler<ScrollViewer>(OnPausedChanged);
    }

    public static IEnumerable? GetItemsSource(AvaloniaObject o)         => o.GetValue(ItemsSourceProperty);
    public static void         SetItemsSource(AvaloniaObject o, IEnumerable? v) => o.SetValue(ItemsSourceProperty, v);

    public static bool GetPaused(AvaloniaObject o)         => o.GetValue(PausedProperty);
    public static void SetPaused(AvaloniaObject o, bool v) => o.SetValue(PausedProperty, v);

    private static void OnPausedChanged(ScrollViewer sv, AvaloniaPropertyChangedEventArgs e)
    {
        // The state object is created by the ItemsSource handler; if Paused is
        // set before ItemsSource (binding order isn't guaranteed), make one now
        // so the flag isn't lost.
        if (sv.Tag is not AutoScrollState state)
        {
            state  = new AutoScrollState(sv);
            sv.Tag = state;
        }
        state.Paused = e.NewValue is true;
    }

    private static void OnItemsSourceChanged(
        ScrollViewer sv, AvaloniaPropertyChangedEventArgs e)
    {
        // Create (or reuse) a state object and expose it through Tag so the
        // host template can bind IsScrolledUp / JumpToBottomCommand without
        // needing the ugly attached-property-in-path syntax.
        if (sv.Tag is not AutoScrollState state)
        {
            state  = new AutoScrollState(sv);
            sv.Tag = state;
        }

        if (e.OldValue is INotifyCollectionChanged old)
            old.CollectionChanged -= state.OnCollectionChanged;

        if (e.NewValue is INotifyCollectionChanged @new)
        {
            @new.CollectionChanged += state.OnCollectionChanged;
            Dispatcher.UIThread.Post(state.ScrollToBottom, DispatcherPriority.Loaded);
        }
    }
}

/// <summary>
/// Per-ScrollViewer state exposed via <see cref="ScrollViewer.Tag"/>.
/// Bind the overlay button to its <see cref="IsScrolledUp"/> and
/// <see cref="JumpToBottomCommand"/> properties.
/// </summary>
public sealed class AutoScrollState : ReactiveObject
{
    private readonly ScrollViewer _sv;
    private bool _atBottom = true;
    private bool _paused;

    // Trim compensation (#293): the scrollback cap trims lines from the TOP of
    // the buffer, which slides the remaining content up under a numerically
    // unchanged Offset.Y — the reader's text drifts through a "held" viewport
    // (and the positional selection under it copies the wrong lines, #298).
    //
    // The offset is compensated SYNCHRONOUSLY inside the CollectionChanged
    // event, before any layout or render pass runs: the panel is a plain
    // (non-virtualized) StackPanel, so at that instant every remaining
    // container still carries its pre-trim Bounds, and the first remaining
    // container's stale Y IS the height being removed above it. Subtracting
    // that from Offset.Y means the offset change and the content shift land in
    // the same layout pass — nothing to observe, no dispatcher/LayoutUpdated
    // ordering to race (both event-based restores failed live: a Loaded post
    // runs after the frame paints → one-frame blink; a LayoutUpdated hook can
    // fire on a pass queued before the trim was measured → no hold at all).
    //
    // _trimComp accumulates the compensation applied since the last completed
    // layout pass: when several trims land in one frame, the later removals
    // still read STALE bounds that already include the earlier removals'
    // heights, so each removal's height is (first remaining Y − _trimComp).
    // Any LayoutUpdated marks bounds fresh again (a pass processes every
    // pending invalidation, so bounds and offset are reconciled by then).
    private double _trimComp;

    [Reactive] public bool IsScrolledUp { get; private set; }

    /// <summary>"Pause Scrolling" — when true, new items no longer drag the view
    /// to the bottom; the user reads the frozen scrollback while the buffer
    /// keeps filling underneath. Flipping it back to false resumes auto-follow
    /// and jumps to the newest line.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            // Resuming: catch up to whatever arrived while paused.
            if (!value) JumpToBottom();
        }
    }

    public ICommand JumpToBottomCommand { get; }

    internal AutoScrollState(ScrollViewer sv)
    {
        _sv = sv;
        _sv.ScrollChanged += OnScrollChanged;
        JumpToBottomCommand = ReactiveCommand.Create(JumpToBottom);
    }

    // ── Collection tracking ────────────────────────────────────────────────

    internal void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (!_paused && _atBottom)
                    Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex == 0:
                // Scrollback trim. Compensate only while the view holds still —
                // when following the tail, the bottom pin already covers it.
                if (!_paused && _atBottom) break;
                CompensateTrim(e.OldItems);
                break;

            case NotifyCollectionChangedAction.Reset:
                _trimComp = 0;   // buffer cleared — nothing left to hold
                break;
        }
    }

    // ── Trim compensation (#293) ───────────────────────────────────────────

    /// <summary>
    /// Runs inside the CollectionChanged event for a removal at index 0, before
    /// any layout pass reacts. The first container that is NOT being removed
    /// still carries pre-trim Bounds, so its Y (minus compensation already
    /// applied this frame) is exactly the height about to vanish above the
    /// content. Subtract it from Offset.Y now and the offset change rides the
    /// same layout pass as the content shift — the held text never moves on
    /// screen. First LayoutUpdated marks bounds fresh and zeroes the counter.
    /// </summary>
    private void CompensateTrim(IList? removedItems)
    {
        if (_sv.Content is not ItemsControl ic) return;

        // First remaining container = smallest stale Y among survivors.
        var firstY = double.MaxValue;
        foreach (var c in ic.GetRealizedContainers())
        {
            if (ContainsRef(removedItems, c.DataContext)) continue;   // being trimmed
            if (c.Bounds.Y < firstY) firstY = c.Bounds.Y;
        }
        if (firstY == double.MaxValue) return;   // nothing left to hold against

        var removedHeight = firstY - _trimComp;
        if (removedHeight <= 0) return;          // stale/unlaid-out bounds — skip

        if (!_boundsFreshHooked)
        {
            _boundsFreshHooked = true;
            _sv.LayoutUpdated += OnBoundsFresh;
        }
        _trimComp += removedHeight;
        _sv.Offset = _sv.Offset.WithY(Math.Max(0, _sv.Offset.Y - removedHeight));
    }

    private bool _boundsFreshHooked;

    /// <summary>Any completed layout pass reconciles bounds with the removals
    /// (a pass processes every pending invalidation), so the stale-bounds
    /// compensation counter resets and the hook detaches until the next trim.</summary>
    private void OnBoundsFresh(object? sender, EventArgs e)
    {
        _sv.LayoutUpdated -= OnBoundsFresh;
        _boundsFreshHooked = false;
        _trimComp = 0;
    }

    private static bool ContainsRef(IList? items, object? candidate)
    {
        if (items is null) return false;
        foreach (var i in items)
            if (ReferenceEquals(i, candidate)) return true;
        return false;
    }

    // ── Scroll tracking ────────────────────────────────────────────────────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var max = _sv.Extent.Height - _sv.Viewport.Height;
        _atBottom    = max <= 0 || _sv.Offset.Y >= max - 10;
        IsScrolledUp = !_atBottom;
    }

    // ── Scroll helpers ─────────────────────────────────────────────────────

    public void ScrollToBottom()
    {
        // ScrollToEnd() uses the ScrollViewer's own up-to-date extent, so it
        // lands on the last line even when extent/viewport are mid-update
        // (right after a line is added, or after an MDI relayout) — more
        // robust than computing Offset from Extent - Viewport.
        _sv.ScrollToEnd();
    }

    private void JumpToBottom()
    {
        _atBottom    = true;
        IsScrolledUp = false;
        Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
    }
}
