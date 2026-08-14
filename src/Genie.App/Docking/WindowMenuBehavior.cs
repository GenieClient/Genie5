using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Genie.App.Controls;

namespace Genie.App.Docking;

/// <summary>
/// Attached behaviour for the per-window right-click <see cref="ContextMenu"/>.
///
/// <para><b>RefreshOnOpen</b> — re-reads the host's live float state just before
/// the menu opens, so the "Float" / "Re-dock" verb is always correct (the user
/// can drag a window out of / back into the dock without the menu model ever
/// being told). It also records the menu's target control so the shared
/// <see cref="CopySelectionCommand"/> knows which window's selection to copy,
/// and refreshes that command's enabled state.</para>
///
/// <para>The ContextMenu inherits the DataContext of the control it's attached
/// to — the dockable (an <see cref="IWindowMenuHost"/>) — which is how the menu
/// items bind <c>WindowMenu.*</c>.</para>
/// </summary>
public static class WindowMenuBehavior
{
    public static readonly AttachedProperty<bool> RefreshOnOpenProperty =
        AvaloniaProperty.RegisterAttached<ContextMenu, bool>(
            "RefreshOnOpen", typeof(WindowMenuBehavior));

    public static bool GetRefreshOnOpen(AvaloniaObject o) => o.GetValue(RefreshOnOpenProperty);
    public static void SetRefreshOnOpen(AvaloniaObject o, bool v) => o.SetValue(RefreshOnOpenProperty, v);

    /// <summary>The control the currently-open window menu was invoked on (its
    /// PlacementTarget). The Copy command reads the live text selection from this
    /// control's subtree. Only one context menu is open at a time, so a single
    /// static slot is sufficient.</summary>
    private static Control? _currentTarget;

    private static readonly CopyCommand _copy = new();

    /// <summary>Shared "Copy" command for the window menu — copies the current
    /// highlighted selection of whichever window the menu was opened on. Bound
    /// from XAML via <c>{x:Static docking:WindowMenuBehavior.CopySelectionCommand}</c>.</summary>
    public static ICommand CopySelectionCommand => _copy;

    /// <summary>The Copy gesture the <b>editor</b> game window actually answers to.
    /// That renderer inherits Copy/Select-All from AvaloniaEdit's own
    /// <c>CommandBindings</c>, which match the platform gesture — Cmd+C on macOS —
    /// rather than the Ctrl-only binding Genie 4 inherited from Windows. Labelling
    /// it "Ctrl+C" there would advertise a shortcut that does nothing.
    ///
    /// <para>Deliberately <b>not</b> used by the legacy game window or the tool
    /// windows: those handle Ctrl+C themselves (<c>SelectableLinesControl</c>'s key
    /// handler) and really are Ctrl-bound on every platform, so their menus keep the
    /// hardcoded label. Resolved per read rather than cached, since
    /// <c>Application.Current</c> may not be up when this type is first touched.</para></summary>
    public static KeyGesture CopySelectionGesture =>
        new(Key.C,
            Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
            ?? KeyModifiers.Control);

    /// <summary>The Find gesture to advertise on the <b>editor</b> game window.
    /// MainWindow answers the platform combo — Cmd+F on macOS — and routes it to
    /// AvaloniaEdit's search panel there, which is also the gesture that renderer
    /// answers by itself once the text has focus. Ctrl+F still opens the in-window
    /// bar on macOS, but the platform gesture is the one worth labelling.
    ///
    /// <para>As with <see cref="CopySelectionGesture"/>: not used by the legacy
    /// game window or the tool windows, whose Find is Ctrl+F on every platform,
    /// and resolved per read because <c>Application.Current</c> may not be up
    /// when this type is first touched.</para></summary>
    public static KeyGesture FindGesture =>
        new(Key.F,
            Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
            ?? KeyModifiers.Control);

    static WindowMenuBehavior()
    {
        RefreshOnOpenProperty.Changed.AddClassHandler<ContextMenu>((menu, e) =>
        {
            // Re-subscribe defensively so toggling the flag never double-hooks.
            menu.Opening -= OnOpening;
            menu.Opened  -= OnOpened;
            if (e.NewValue is true)
            {
                menu.Opening += OnOpening;
                menu.Opened  += OnOpened;
            }
        });
    }

    private static void OnOpening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        if (ResolveMenuTarget(menu) is { } target)
            _currentTarget = target;

        if (menu.DataContext is IWindowMenuHost host)
            host.WindowMenu?.RefreshFloatState();

        // Grey "Copy" out when there's nothing highlighted in this window.
        _copy.RaiseCanExecuteChanged();
    }

    private static void OnOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        // Re-resolve on Opened as well: on the very first open the menu isn't
        // parented to its Popup until the open sequence completes, so Opening
        // can miss the target. By Opened the Popup parent (and its
        // PlacementTarget) always exists. The open menu updates live via
        // CanExecuteChanged.
        if (ResolveMenuTarget(menu) is { } target)
            _currentTarget = target;
        _copy.RaiseCanExecuteChanged();
    }

    /// <summary>The control the menu is attached to (the window's content
    /// root). Two traps here, both verified on Avalonia 11.3.11:
    /// (1) ContextMenu.PlacementTarget is NOT assigned by the attached-menu
    /// open path — it stays null through Opening AND Opened; the internal
    /// Popup's PlacementTarget is set instead, so read that.
    /// (2) That popup target is the DEEPEST control under the right-click
    /// (e.g. the clicked line's SelectableTextBlock), not the menu's owner —
    /// searching its subtree finds only that one line's selection, silently
    /// truncating Copy to a single line. Walk up to the ancestor that owns
    /// this menu; fall back to the raw target if the walk finds none.</summary>
    private static Control? ResolveMenuTarget(ContextMenu menu)
    {
        var seed = menu.PlacementTarget ?? (menu.Parent as Popup)?.PlacementTarget;
        for (var c = seed; c is not null; c = c.GetVisualParent() as Control)
            if (c.ContextMenu == menu)
                return c;
        return seed;
    }

    // ── Copy current selection ─────────────────────────────────────────────

    private static string? CurrentSelection() => CurrentSelection(out _);

    private static string? CurrentSelection(out string trace)
    {
        // Primary: the open menu's placement target. Fallback: the last
        // right-/left-clicked window's ScrollViewer (PageScroll tracks every
        // press, including the right-click that opened this menu) — covers
        // any path where PlacementTarget was still null when we looked.
        var target = _currentTarget ?? PageScroll.CurrentTarget;
        if (target is null) { trace = "no target"; return null; }

        // 1) Game window on the experimental AvaloniaEdit renderer: selection is
        //    native to the editor, so ask it directly. Checked first because a
        //    TextEditor's subtree contains neither a LineSelection-enabled
        //    ItemsControl nor a SelectableTextBlock, and it never appears in the
        //    legacy path — with the renderer flag off this branch cannot match, so
        //    the shipped resolution order below is unchanged.
        var editor = target.GetSelfAndVisualDescendants()
                           .OfType<AvaloniaEdit.TextEditor>()
                           .FirstOrDefault(e => e.SelectionLength > 0);
        if (editor?.SelectedText is { Length: > 0 } editorSelection)
        {
            trace = $"target={target.GetType().Name} editor len={editorSelection.Length}";
            return editorSelection;
        }

        // 2) Game window: cross-line selection owned by the LineSelection
        //    behavior (its rendered per-line SelectionStart/End survive a
        //    right-click; the behavior holds the authoritative range).
        var list = target.GetSelfAndVisualDescendants()
                         .OfType<ItemsControl>()
                         .FirstOrDefault(LineSelection.GetEnabled);
        if (list is not null && LineSelection.GetSelectedText(list) is { Length: > 0 } cross)
        {
            trace = $"target={target.GetType().Name} behavior len={cross.Length}";
            return cross;
        }

        // 3) Streams / other feeds — and the game window when its behavior
        //    state is empty but rendered per-line selections remain. Aggregate
        //    EVERY selected block in visual (= line) order: taking just the
        //    first can silently truncate a visible multi-line highlight to a
        //    single line. A right-click inside a highlight keeps it (Avalonia
        //    collapses a block's selection only when the click lands outside
        //    that block's selected range).
        var parts = target.GetSelfAndVisualDescendants()
                          .OfType<SelectableTextBlock>()
                          .Where(b => b.SelectionStart != b.SelectionEnd)
                          .Select(b => b.SelectedText)
                          .Where(t => !string.IsNullOrEmpty(t))
                          .ToList();
        trace = $"target={target.GetType().Name} behavior={(list is null ? "no list" : "empty")} blocks={parts.Count}";
        return parts.Count == 0 ? null : string.Join('\n', parts);
    }

    private sealed class CopyCommand : ICommand
    {
        public event System.EventHandler? CanExecuteChanged;
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, System.EventArgs.Empty);

        public bool CanExecute(object? parameter) =>
            !string.IsNullOrEmpty(CurrentSelection());

        public async void Execute(object? parameter)
        {
            var text = CurrentSelection(out var trace);
            // One line per menu-Copy click (a rare user action) so a "Copy
            // grabbed the wrong thing" report comes with the resolution path.
            Diagnostics.ErrorLog.Note("WindowCopy",
                $"{trace} -> {(text is null ? "null" : $"{text.Length} chars, {text.Count(c => c == '\n') + 1} line(s)")}");
            if (string.IsNullOrEmpty(text)) return;
            // Anchor the clipboard lookup on the same fallback chain
            // CurrentSelection() used to find the text — anchoring on
            // _currentTarget alone made Copy a silent no-op whenever only the
            // PageScroll fallback had resolved (the pre-fix state of every
            // right-click, since PlacementTarget was never assigned).
            var anchor = _currentTarget ?? PageScroll.CurrentTarget;
            if (anchor is not null && TopLevel.GetTopLevel(anchor)?.Clipboard is { } cb)
                await cb.SetTextAsync(text);
        }
    }
}
