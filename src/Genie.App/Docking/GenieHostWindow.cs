using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using System.Linq;
using Avalonia.Platform;
using Avalonia.Reactive;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Genie.App.Docking;

/// <summary>
/// Dock floating-panel window (used when a Tool/Document is floated out to its own
/// OS window). Adds the window affordances Dock's stock <see cref="HostWindow"/>
/// chrome doesn't wire: <b>double-click-to-maximize/restore</b> on the title bar
/// (from any state, incl. restoring a minimized float — #196), an injected
/// <b>minimize button</b> (#170), and a title-bar <b>right-click menu</b> with
/// Restore / Maximize / Minimize / Close (#196). Floats have no taskbar button
/// (#170), so these plus the Window menu are how one is controlled.
/// </summary>
public sealed class GenieHostWindow : HostWindow
{
    /// <summary>Dock's HostWindow chrome title-bar height is ~30px; allow slack for
    /// the window-level fallback band when the named part can't be bound.</summary>
    private const double TitleBarBandHeight = 34;

    private Control? _titleBar;

    /// <summary>#181: Dock's chrome control (accent title bar with the tool name),
    /// cached once resolved from the visual tree. This — not <see cref="_titleBar"/> —
    /// is the bar the user sees on a float; <see cref="SetTitleBarHidden"/> collapses
    /// its header band (leaving the content presenter intact).</summary>
    private ToolChromeControl? _toolChrome;

    /// <summary>#181: title bar collapsed to reclaim vertical space. Session-only —
    /// a reopened float starts with the bar shown. Restore by double-clicking the
    /// window's top edge (the bar and its menu are hidden, so that's the on-window
    /// way back).</summary>
    private bool _titleBarHidden;

    public GenieHostWindow()
    {
        // #170: floated panels are secondary tool windows of the one Genie
        // instance — they must not each claim a taskbar button (a layout with
        // several floats filled the taskbar with identical Genie entries).
        // One instance = one button (the main window's).
        ShowInTaskbar = false;

        // The other half of the tool-window model: with no taskbar button of
        // their own (and no minimize button in Dock's chrome — the button that
        // USED to minimize a float was its taskbar entry), floats minimize and
        // restore WITH the main window, like IDE tool palettes. Minimize Genie
        // → the whole layout goes; restore Genie → floats come back in their
        // prior state (a maximized float restores maximized). Individual
        // dismissal stays what it was: Close, then reopen from the Window menu.
        DoubleTapped += OnWindowDoubleTapped;

        // Issue #3: a floated panel that was maximized then closed can reopen
        // off-screen (saved restore-bounds land beyond the visible desktop on a
        // multi-monitor setup — seen as a sliver at a monitor's far edge). On open,
        // pull it back fully onto a visible screen so it can never vanish.
        Opened += (_, _) =>
        {
            EnsureOnVisibleScreen();
            FollowMainWindowMinimize();
            // The chrome's template is applied by now, but give the visual
            // tree one more tick before searching it for the button strip.
            PostChromeInjection();
        };
        Closed += (_, _) =>
        {
            _mainStateSub?.Dispose(); _mainStateSub = null;
            // Don't let the shared-flyout target tracker outlive its window.
            if (ReferenceEquals(_flyoutSourceWindow, this)) _flyoutSourceWindow = null;
        };
    }

    /// <summary>Run the chrome injections (minimize button + flyout item) once the
    /// visual tree can serve them. The chrome materializes on a layout pass that
    /// can land AFTER the Opened-time Loaded dispatch, and a missed one-shot left
    /// the flyout item absent for the whole session — so retry a few ticks.
    /// <see cref="TryInjectMinimizeButton"/> is idempotent per window.</summary>
    private void PostChromeInjection(int attempt = 0)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            TryInjectMinimizeButton();
            if (!TryAddHideTitleBarToChromeFlyout() && attempt < 3)
                PostChromeInjection(attempt + 1);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Add a minimize button to the float's chrome, before the maximize
    /// button. Dock's ToolChromeControl (which acts as the float's title bar)
    /// ships ▾ / maximize / close only — and since #170 removed the per-float
    /// taskbar buttons, there was NO minimize affordance left (field report).
    /// The injected button mimics the maximize button's styling; recovery for
    /// a minimized float is the Window menu (SetToolVisibility restores a
    /// minimized host) or minimizing/restoring the main window.
    /// Best-effort: if Dock's template changes, this quietly does nothing.
    /// </summary>
    private void TryInjectMinimizeButton()
    {
        Button? maximize = null;
        foreach (var b in this.GetVisualDescendants().OfType<Button>())
        {
            if (b.Name == "GenieMinimizeButton") return;   // already injected
            if (b.Name == "PART_MinimizeButton")
            {
                // The theme ships a native minimize button (HostWindowTitleBar
                // chrome) — if it's in this tree, just make sure it shows.
                b.IsVisible = true;
                return;
            }
            if (b.Name == "PART_MaximizeRestoreButton") maximize = b;
        }
        if (maximize?.Parent is not Panel strip) return;

        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Avalonia.Media.Geometry.Parse("M0,0 L8,0"),
            StrokeThickness   = 1,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin            = new Thickness(0, 0, 0, 2),
        };
        glyph.Bind(Avalonia.Controls.Shapes.Path.StrokeProperty,
                   maximize.GetObservable(ForegroundProperty));

        var minimize = new Button
        {
            Name    = "GenieMinimizeButton",
            Content = glyph,
            Theme   = maximize.Theme,
            Width   = maximize.Width,
            Height  = maximize.Height,
            Padding = maximize.Padding,
            HorizontalAlignment = maximize.HorizontalAlignment,
            VerticalAlignment   = maximize.VerticalAlignment,
        };
        foreach (var c in maximize.Classes) minimize.Classes.Add(c);
        ToolTip.SetTip(minimize, "Minimize");
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;

        var idx = strip.Children.IndexOf(maximize);
        strip.Children.Insert(idx < 0 ? 0 : idx, minimize);
    }

    /// <summary>The <see cref="GenieHostWindow"/> whose chrome the shared tool
    /// flyout is currently (or was most recently) open over, captured from the
    /// flyout's placement target on each open. The flyout's MenuItems live in a
    /// popup whose visual root is the popup itself, so a click handler can't find
    /// its window by visual search — and the previous "whichever float IsActive"
    /// scan silently did nothing whenever no float held OS activation (the exact
    /// field report: the item was there but "isn't working").</summary>
    private static GenieHostWindow? _flyoutSourceWindow;

    /// <summary>#181 follow-up: also expose "Hide Title Bar" on the title-bar
    /// right-click menu (Dock's <see cref="ToolChromeControl"/> flyout), not only on
    /// our content menu — a user reaching to hide the bar naturally right-clicks the
    /// bar itself (field request). That flyout is Dock's own shared MenuFlyout
    /// (Float / Dock / Dock as Tabbed / Close), assigned to every chrome via the
    /// theme, so we add ONE item to it, gated to floating chromes.
    /// <para>The gate must bind through the LOGICAL tree: a popup's MenuItem has no
    /// ToolChromeControl visual ancestor (its visual root is the PopupRoot), while
    /// the logical chain runs MenuItem → MenuFlyoutPresenter → Popup → chrome.
    /// Dock's own AutoHide item gates via XAML <c>$parent[ToolChromeControl]</c>,
    /// which is logical-tree; a code-behind FindAncestor defaults to the VISUAL tree
    /// and never matched — leaving the item visible on every docked chrome's menu,
    /// where clicking it found no float to act on (public #181 "isn't working").
    /// FallbackValue=false keeps it hidden in chrome-less hosts of the same shared
    /// flyout (the MDI window's tool-menu button).</para>
    /// The item hides the bar of the window hosting the chrome the flyout was
    /// opened over (<see cref="_flyoutSourceWindow"/>); a hidden bar can't be
    /// right-clicked, so the item is always "hide" (restore stays the
    /// double-click-top-edge path).
    /// Best-effort: returns false (for a bounded retry) while the chrome or its
    /// flyout aren't materialized yet; if Dock's flyout shape changes, this
    /// quietly does nothing.</summary>
    private bool TryAddHideTitleBarToChromeFlyout()
    {
        if (_toolChrome is null || _toolChrome.GetVisualRoot() is null)
            _toolChrome = this.GetVisualDescendants().OfType<ToolChromeControl>().FirstOrDefault();
        if (_toolChrome?.ToolFlyout is not MenuFlyout flyout) return false;

        // Idempotence by inspection, not a process-wide flag: the flyout is a
        // shared theme resource reused by every chrome, but the INSTANCE can be
        // recreated with the application styles — a static "already added" bit
        // would then suppress re-injection forever and the item would silently
        // vanish for the rest of the session.
        if (flyout.Items.OfType<MenuItem>().Any(i => i.Name == "GenieHideTitleBarItem"))
            return true;

        // Remember which window's chrome the shared flyout opens over, so the
        // click below acts on that window — not on a guess from IsActive.
        flyout.Opened += (s, _) =>
            _flyoutSourceWindow = (s as FlyoutBase)?.Target is Visual v
                ? v.FindAncestorOfType<GenieHostWindow>(includeSelf: true)
                : null;

        static Binding FloatingChromeGate() => new()
        {
            Path           = "IsFloating",
            FallbackValue  = false,
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor)
            {
                AncestorType = typeof(ToolChromeControl),
                Tree         = TreeType.Logical,
            },
        };

        var item = new MenuItem { Name = "GenieHideTitleBarItem", Header = "Hide Title Bar" };
        // Only on floating chromes — a docked tool has no separable title bar to hide.
        item.Bind(IsVisibleProperty, FloatingChromeGate());
        item.Click += (_, _) =>
        {
            var target = _flyoutSourceWindow;
            if (target is null && Application.Current?.ApplicationLifetime is
                    IClassicDesktopStyleApplicationLifetime desktop)
                target = desktop.Windows.OfType<GenieHostWindow>()
                                .FirstOrDefault(w => w.IsActive);
            target?.SetTitleBarHidden(true);
        };

        // The separator only earns its place when the item under it shows —
        // ungated it rendered as a dangling line on every docked chrome's menu.
        var separator = new Separator();
        separator.Bind(IsVisibleProperty, FloatingChromeGate());

        flyout.Items.Add(separator);
        flyout.Items.Add(item);
        return true;
    }

    private IDisposable? _mainStateSub;
    private WindowState  _stateBeforeMainMinimize = WindowState.Normal;
    private bool         _minimizedWithMain;

    /// <summary>Mirror the main window's minimize/restore onto this float
    /// (#170 follow-up). Floats have no taskbar button and Dock's chrome has no
    /// minimize button, so main-window minimize is the one gesture that hides
    /// them — and the one that brings them back.</summary>
    private void FollowMainWindowMinimize()
    {
        if (_mainStateSub is not null) return;
        if (Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is not { } main || ReferenceEquals(main, this))
            return;

        _mainStateSub = main.GetObservable(WindowStateProperty)
            .Subscribe(new AnonymousObserver<WindowState>(s =>
            {
                if (s == WindowState.Minimized)
                {
                    if (WindowState == WindowState.Minimized) return;
                    _stateBeforeMainMinimize = WindowState;
                    _minimizedWithMain       = true;
                    WindowState = WindowState.Minimized;
                }
                else if (_minimizedWithMain)
                {
                    _minimizedWithMain = false;
                    WindowState = _stateBeforeMainMinimize;
                }
            }));
    }

    /// <summary>Re-run the on-screen clamp after external code repositions the
    /// window (layout restore applies saved geometry AFTER Opened, so the
    /// Opened-time clamp saw only the pre-geometry defaults).</summary>
    public void ClampToVisibleScreen() => EnsureOnVisibleScreen();

    /// <summary>Clamp a normal-state floating window's bounds onto the working area
    /// of whichever screen it most overlaps (else the primary), so a stale/off-screen
    /// restore position can't leave it invisible. Maximized windows already fill a
    /// screen, so they're left alone.</summary>
    private void EnsureOnVisibleScreen()
    {
        var screens = Screens;
        if (screens?.All is not { Count: > 0 } all) return;
        if (WindowState == WindowState.Maximized) return;

        var scale = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var w = Math.Max(200, (int)(Bounds.Width  * scale));
        var h = Math.Max(120, (int)(Bounds.Height * scale));
        var rect = new PixelRect(Position.X, Position.Y, w, h);

        // Best-overlapping screen (handles the "sliver at the edge" case), else primary.
        Screen? best = null;
        long bestArea = -1;
        foreach (var s in all)
        {
            var i = s.WorkingArea.Intersect(rect);
            long a = (long)i.Width * i.Height;
            if (a > bestArea) { bestArea = a; best = s; }
        }
        var wa = (best ?? screens.Primary ?? all[0]).WorkingArea;

        var x = Math.Clamp(Position.X, wa.X, Math.Max(wa.X, wa.X + wa.Width  - w));
        var y = Math.Clamp(Position.Y, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - h));
        if (x != Position.X || y != Position.Y)
            Position = new PixelPoint(x, y);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_titleBar is not null)
            _titleBar.DoubleTapped -= OnTitleBarDoubleTapped;

        // Dock's HostWindow chrome names its title bar PART_TitleBar. Bind the
        // double-tap there precisely so a double-tap in the content area is ignored.
        _titleBar = e.NameScope.Find<Control>("PART_TitleBar");
        if (_titleBar is not null)
        {
            _titleBar.DoubleTapped += OnTitleBarDoubleTapped;
            _titleBar.ContextMenu = BuildTitleBarMenu(this);   // #196: right-click state menu
        }
    }

    /// <summary>#181: whether this float's title bar is currently collapsed.</summary>
    public bool IsTitleBarHidden => _titleBarHidden;

    /// <summary>#181: collapse / restore the float's title bar — the header band of
    /// Dock's <see cref="ToolChromeControl"/> (the accent bar showing the tool name;
    /// NOT the HostWindow's PART_TitleBar). The chrome template is a Grid(Auto,*):
    /// row 0 = the header (<c>PART_Border</c>, which holds the title + pin/max/close
    /// buttons) and its divider (<c>PART_Panel</c>); row 1 = <c>PART_ContentPresenter</c>,
    /// the tool's actual content. We collapse ONLY the row-0 header parts, so the Auto
    /// row shrinks to 0 and the content reclaims the ~30px (the request, #181).
    /// <para>Hiding the whole ToolChromeControl — the first cut of this fix — blanked
    /// the content too, since the content presenter lives inside it (field report:
    /// "we hide the content too").</para>
    /// Restore is a double-click on the window's top edge (see
    /// <see cref="OnWindowDoubleTapped"/>) — the header carries the drag handle and its
    /// own menu, so hiding it removes the on-bar way back.</summary>
    public void SetTitleBarHidden(bool hidden)
    {
        _titleBarHidden = hidden;
        // Resolve lazily: the chrome isn't in the tree until the float is shown,
        // and it can be recreated, so re-search whenever we don't hold a live one.
        if (_toolChrome is null || _toolChrome.GetVisualRoot() is null)
            _toolChrome = this.GetVisualDescendants().OfType<ToolChromeControl>().FirstOrDefault();
        if (_toolChrome is null) return;

        // The chrome's template root is the Grid(Auto,*); its DIRECT children are the
        // content presenter (row 1) and the header Border + divider Panel (row 0).
        // Target only those direct children — our ToolControl skin ALSO names a Border
        // "PART_Border", but that one lives deep inside the content presenter, so a
        // blanket descendant search would wrongly collapse the content.
        var root = _toolChrome.GetVisualChildren().OfType<Grid>().FirstOrDefault();
        if (root is null) return;
        foreach (var child in root.GetVisualChildren().OfType<Control>())
        {
            if (child.Name is "PART_Border" or "PART_Panel")
                child.IsVisible = !hidden;
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize(e);

    private void OnWindowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled) return;

        // #181: with the title bar hidden, a double-click in the top band brings it
        // back. The hidden bar receives no pointer events, so this window-level
        // handler is the restore path. Checked before the maximize fallback so it
        // wins over "double-click top → maximize".
        if (_titleBarHidden && e.GetPosition(this).Y <= TitleBarBandHeight)
        {
            SetTitleBarHidden(false);
            e.Handled = true;
            return;
        }

        if (_titleBar is not null) return;            // precise handler covers it
        if (e.GetPosition(this).Y <= TitleBarBandHeight)
            ToggleMaximize(e);
    }

    private void ToggleMaximize(TappedEventArgs e)
    {
        // Normal → Maximized; Maximized OR Minimized → Normal (#196). The
        // Minimized case is the fix: double-clicking a minimized float's
        // title-bar stub to bring it back used to fall into the else branch
        // and MAXIMIZE it (restore-goes-fullscreen) instead of returning to
        // its prior floated bounds.
        WindowState = WindowState == WindowState.Normal
            ? WindowState.Maximized
            : WindowState.Normal;
        e.Handled = true;
    }

    /// <summary>Right-click menu on the float's title bar with explicit
    /// window-state control (#196). Floats have no taskbar button, so this is
    /// the direct way to Restore / Maximize / Minimize / Close one without
    /// hunting for the Window menu. Attached to PART_TitleBar so a right-click
    /// in the panel content (which has its own menus) is unaffected.</summary>
    private static ContextMenu BuildTitleBarMenu(GenieHostWindow win)
    {
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            return mi;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Restore",  () => win.WindowState = WindowState.Normal));
        menu.Items.Add(Item("Maximize", () => win.WindowState = WindowState.Maximized));
        menu.Items.Add(Item("Minimize", () => win.WindowState = WindowState.Minimized));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close",    win.Close));
        return menu;
    }
}
