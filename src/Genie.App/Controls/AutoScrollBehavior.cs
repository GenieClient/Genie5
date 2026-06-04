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

    static AutoScrollBehavior()
    {
        ItemsSourceProperty.Changed.AddClassHandler<ScrollViewer>(OnItemsSourceChanged);
    }

    public static IEnumerable? GetItemsSource(AvaloniaObject o)         => o.GetValue(ItemsSourceProperty);
    public static void         SetItemsSource(AvaloniaObject o, IEnumerable? v) => o.SetValue(ItemsSourceProperty, v);

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

    [Reactive] public bool IsScrolledUp { get; private set; }

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
        if (e.Action == NotifyCollectionChangedAction.Add && _atBottom)
            Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Loaded);
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
