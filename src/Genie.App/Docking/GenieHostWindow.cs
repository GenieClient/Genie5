using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
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
            Avalonia.Threading.Dispatcher.UIThread.Post(
                TryInjectMinimizeButton,
                Avalonia.Threading.DispatcherPriority.Loaded);
        };
        Closed += (_, _) => { _mainStateSub?.Dispose(); _mainStateSub = null; };
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

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize(e);

    private void OnWindowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled || _titleBar is not null) return;            // precise handler covers it
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
