using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Genie.App.Docking;

/// <summary>
/// Keeps the selected tab of a dock tab strip scrolled into view.
///
/// <para>
/// Dock's tab strips host their tabs in a horizontal ScrollViewer, but nothing
/// scrolls it when the selection changes: with more tabs than the window is
/// wide, activating a tab that sits past the edge (Next-window cycling, a
/// restored layout whose active tab is far down the list, a stream window
/// auto-activating on output) leaves the strip showing the wrong stretch of
/// tabs, so the active tab's header is invisible. This behavior scrolls the
/// selected tab's container fully into view whenever the selection changes,
/// and once on load for layouts restored with an off-screen active tab.
/// </para>
///
/// <para>Applied app-wide from App.axaml styles to both <c>ToolTabStrip</c>
/// (docked/stacked tool windows — the stream tabs) and
/// <c>DocumentTabStrip</c> (tabbed documents). Pairs with the App.axaml
/// style that flips the ToolTabStrip ScrollViewer to
/// <c>HorizontalScrollBarVisibility="Auto"</c> so Dock's overflow arrow
/// buttons appear; see the comment there.</para>
/// </summary>
public static class TabStripAutoScroll
{
    /// <summary>Set true (via style) to keep the selected tab in view.</summary>
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SelectingItemsControl, bool>(
            "Enabled", typeof(TabStripAutoScroll));

    public static bool GetEnabled(AvaloniaObject o)         => o.GetValue(EnabledProperty);
    public static void SetEnabled(AvaloniaObject o, bool v) => o.SetValue(EnabledProperty, v);

    static TabStripAutoScroll()
    {
        EnabledProperty.Changed.AddClassHandler<SelectingItemsControl>((strip, e) =>
        {
            if (e.NewValue is true)
            {
                strip.SelectionChanged += OnSelectionChanged;
                strip.Loaded += OnLoaded;
            }
            else
            {
                strip.SelectionChanged -= OnSelectionChanged;
                strip.Loaded -= OnLoaded;
            }
        });
    }

    private static void OnLoaded(object? sender, RoutedEventArgs e) =>
        ScrollSelectedIntoView(sender as SelectingItemsControl);

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ScrollSelectedIntoView(sender as SelectingItemsControl);

    private static void ScrollSelectedIntoView(SelectingItemsControl? strip)
    {
        if (strip is null) return;

        // Post at Loaded priority: at SelectionChanged time the new tab's
        // container may not be realized or arranged yet (tab just added, layout
        // just restored), so BringIntoView would use stale/empty bounds.
        Dispatcher.UIThread.Post(() =>
        {
            var index = strip.SelectedIndex;
            if (index < 0) return;
            strip.ContainerFromIndex(index)?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
