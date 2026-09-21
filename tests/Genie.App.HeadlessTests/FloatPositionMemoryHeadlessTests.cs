using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #359 — a panel the user floats, sizes and positions must come back
/// there on the next open. <see cref="GenieDockFactory"/> deliberately refuses
/// to record a float's ephemeral dock parent (#35), which left float geometry
/// with nowhere to live: every reopen landed at Dock's default placement.
///
/// <para>Exercised over the PRODUCTION path — the real factory, a real
/// DockControl, a real <see cref="GenieHostWindow"/> — so the close hook
/// (<see cref="GenieHostWindow.BeforeClose"/>, which has to run before Dock
/// detaches the window from the root) is covered by the same close a user's
/// click on the chrome's X performs.</para>
/// </summary>
public class FloatPositionMemoryHeadlessTests
{
    /// <summary>A live main window bound to the real factory, as the app runs it.</summary>
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }

        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_float_pos_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;

            Main = new Window
            {
                Width = 1200, Height = 800,
                Content = new DockControl
                {
                    Layout           = Vm.DockLayout,
                    Factory          = Factory,
                    InitializeLayout = false,
                },
            };
            Main.Show();
            Pump(Main);
        }

        public IRootDock Root => (IRootDock)Vm.DockLayout!;

        /// <summary>The realized host window currently showing <paramref name="id"/>,
        /// or null when the tool isn't floating.</summary>
        public GenieHostWindow? FloatFor(string id)
        {
            foreach (var w in Root.Windows ?? new System.Collections.Generic.List<IDockWindow>())
            {
                if (w.Layout is not { } layout) continue;
                if (Contains(layout, id) && w.Host is GenieHostWindow g) return g;
            }
            return null;

            static bool Contains(IDockable node, string id)
            {
                if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
                if (node is IDock d && d.VisibleDockables is { } kids)
                    foreach (var k in kids)
                        if (Contains(k, id)) return true;
                return false;
            }
        }

        public static void Pump(Window w)
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        public void Dispose()
        {
            foreach (var w in (Root.Windows ?? new System.Collections.Generic.List<IDockWindow>()).ToList())
                if (w.Host is Window host) { try { host.Close(); } catch { /* teardown */ } }
            try { Main.Close(); } catch { /* teardown */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Move + resize a float the way a user drags and stretches it.</summary>
    private static void Place(GenieHostWindow host, int x, int y, double w, double h)
    {
        host.Position = new PixelPoint(x, y);
        host.Width    = w;
        host.Height   = h;
        Harness.Pump(host);
    }

    // ── The report itself ────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Reopened_float_returns_to_where_the_user_left_it()
    {
        using var h = new Harness();

        h.Factory.FloatTool("thoughts");
        Harness.Pump(h.Main);
        var first = h.FloatFor("thoughts");
        Assert.NotNull(first);

        Place(first!, 420, 260, 700, 500);

        // The chrome's X — the close path that names no tool id, and the one
        // Azothy's report goes through.
        first!.Close();
        Harness.Pump(h.Main);
        Assert.Null(h.FloatFor("thoughts"));

        h.Factory.ShowToolFloating("thoughts");
        Harness.Pump(h.Main);

        var second = h.FloatFor("thoughts");
        Assert.NotNull(second);
        Assert.Equal(420, second!.Position.X);
        Assert.Equal(260, second.Position.Y);
        Assert.Equal(700, second.Width,  precision: 0);
        Assert.Equal(500, second.Height, precision: 0);
    }

    [AvaloniaFact]
    public void Window_menu_hide_also_remembers_the_float_geometry()
    {
        using var h = new Harness();

        h.Factory.FloatTool("thoughts");
        Harness.Pump(h.Main);
        Place(h.FloatFor("thoughts")!, 150, 90, 640, 480);

        // Window → Thoughts (uncheck) rather than the chrome's X.
        h.Factory.SetToolVisibility("thoughts", false);
        Harness.Pump(h.Main);

        h.Factory.ShowToolFloating("thoughts");
        Harness.Pump(h.Main);

        var again = h.FloatFor("thoughts");
        Assert.NotNull(again);
        Assert.Equal(150, again!.Position.X);
        Assert.Equal(90,  again.Position.Y);
    }

    [AvaloniaFact]
    public void Re_docking_then_floating_again_reuses_the_remembered_geometry()
    {
        using var h = new Harness();

        h.Factory.FloatTool("thoughts");
        Harness.Pump(h.Main);
        Place(h.FloatFor("thoughts")!, 310, 210, 660, 420);

        h.Factory.RedockTool("thoughts");
        Harness.Pump(h.Main);
        Assert.Null(h.FloatFor("thoughts"));

        h.Factory.FloatTool("thoughts");
        Harness.Pump(h.Main);

        var again = h.FloatFor("thoughts");
        Assert.NotNull(again);
        Assert.Equal(310, again!.Position.X);
        Assert.Equal(210, again.Position.Y);
    }

    // ── Which placement a reopen should prefer ───────────────────────────────

    [AvaloniaFact]
    public void Never_placed_tool_prefers_a_float()
    {
        using var h = new Harness();

        // The Script Manager is hidden by default, so nothing has ever captured
        // a docked position for it — its registered home is the 22%-wide right
        // column, which is what #359 is about.
        Assert.False(h.Factory.IsToolVisible("scripts"));
        Assert.True(h.Factory.PrefersFloatingReopen("scripts"));
    }

    [AvaloniaFact]
    public void Docked_tool_does_not_prefer_a_float()
    {
        using var h = new Harness();

        // Thoughts ships docked in the stream column, so the initial
        // CaptureAllPositions recorded a real docked home for it.
        Assert.True(h.Factory.IsToolVisible("thoughts"));
        Assert.False(h.Factory.PrefersFloatingReopen("thoughts"));
    }

    [AvaloniaFact]
    public void Preference_follows_the_most_recent_placement()
    {
        using var h = new Harness();

        h.Factory.FloatTool("thoughts");
        Harness.Pump(h.Main);
        Assert.True(h.Factory.PrefersFloatingReopen("thoughts"));

        // Docking it again is the user saying where they want it; a stale float
        // record must not drag it back out to a window on the next open.
        h.Factory.RedockTool("thoughts");
        Harness.Pump(h.Main);
        Assert.False(h.Factory.PrefersFloatingReopen("thoughts"));
    }

    [AvaloniaFact]
    public void Script_manager_opens_as_its_own_window_on_first_show()
    {
        using var h = new Harness();

        h.Vm.ToggleScriptsCommand.Execute().Subscribe();
        Harness.Pump(h.Main);

        Assert.True(h.Factory.IsToolVisible("scripts"));
        Assert.NotNull(h.FloatFor("scripts"));
    }

    [AvaloniaFact]
    public void Script_manager_stays_docked_once_the_user_docks_it()
    {
        using var h = new Harness();

        h.Vm.ToggleScriptsCommand.Execute().Subscribe();
        Harness.Pump(h.Main);
        Assert.NotNull(h.FloatFor("scripts"));

        h.Factory.RedockTool("scripts");
        Harness.Pump(h.Main);
        Assert.Null(h.FloatFor("scripts"));

        // Close and reopen from the menu: the docked choice wins from here on.
        h.Vm.ToggleScriptsCommand.Execute().Subscribe();
        Harness.Pump(h.Main);
        Assert.False(h.Factory.IsToolVisible("scripts"));

        h.Vm.ToggleScriptsCommand.Execute().Subscribe();
        Harness.Pump(h.Main);
        Assert.True(h.Factory.IsToolVisible("scripts"));
        Assert.Null(h.FloatFor("scripts"));
    }
}
