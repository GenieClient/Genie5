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

    // Trim anchor (#293): the scrollback cap trims lines from the TOP of the
    // buffer, which slides the remaining content up under a numerically
    // unchanged Offset.Y — the reader's text drifts through a "held" viewport
    // (and the positional selection under it copies the wrong lines, #298).
    // While the view is not following the tail, the line at the top of the
    // viewport is captured before layout reacts to the removal and restored to
    // the same viewport position after. Anchoring is by REFERENCE: TextLine is
    // a record, and duplicate lines (repeated combat spam) are value-equal.
    private object? _anchorItem;
    private double  _anchorDelta;
    private bool    _restorePending;

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
                // Scrollback trim. Anchor only while the view holds still —
                // when following the tail, the bottom pin already covers it.
                if (!_paused && _atBottom) break;
                CaptureAnchor(e.OldItems);
                if (_anchorItem is not null && !_restorePending)
                {
                    _restorePending = true;
                    Dispatcher.UIThread.Post(RestoreAnchor, DispatcherPriority.Loaded);
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                _anchorItem = null;   // buffer cleared — nothing left to hold
                break;
        }
    }

    // ── Trim anchor (#293) ─────────────────────────────────────────────────

    /// <summary>
    /// Record which line sits at the top of the viewport, and where in it the
    /// viewport top falls. Runs inside the CollectionChanged event, BEFORE the
    /// layout pass reacts: container bounds still reflect the layout that
    /// Offset.Y was last read against, so the pair is self-consistent. First
    /// removal of a frame wins; the batched restore reuses it.
    /// </summary>
    private void CaptureAnchor(IList? removedItems)
    {
        if (_anchorItem is not null) return;
        if (_sv.Content is not ItemsControl ic) return;

        Control? top   = null;
        var      bestY = double.MaxValue;
        foreach (var c in ic.GetRealizedContainers())
        {
            var b = c.Bounds;
            if (b.Y + b.Height <= _sv.Offset.Y) continue;   // fully above the viewport
            if (b.Y >= bestY) continue;
            if (ContainsRef(removedItems, c.DataContext)) continue;   // being trimmed
            bestY = b.Y;
            top   = c;
        }
        if (top is null) return;
        _anchorItem  = top.DataContext;
        _anchorDelta = _sv.Offset.Y - bestY;
    }

    /// <summary>Post-layout: put the anchored line back at the same viewport
    /// position. If the anchor itself got trimmed away (reader parked at the
    /// very top of the buffer), there is nothing to hold — let the view clamp.</summary>
    private void RestoreAnchor()
    {
        _restorePending = false;
        var item = _anchorItem;
        _anchorItem = null;
        if (item is null) return;
        if (!_paused && _atBottom) return;   // user returned to the tail meanwhile
        if (_sv.Content is not ItemsControl ic) return;

        foreach (var c in ic.GetRealizedContainers())
        {
            if (!ReferenceEquals(c.DataContext, item)) continue;
            _sv.Offset = _sv.Offset.WithY(Math.Max(0, c.Bounds.Y + _anchorDelta));
            return;
        }
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
