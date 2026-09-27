using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #363 and #364 — Window ▸ Windowed Mode (MDI).
///
/// <para><b>#363:</b> the toggle used to throw the current arrangement away and
/// build the other mode's canonical default. It now carries it across: the open
/// panels stay open, closed ones stay closed, and a round trip restores the
/// tabbed arrangement the user left.</para>
///
/// <para><b>#364:</b> windowed mode contains its children, as Genie 4's did.
/// A child dragged by its title text used to start a Dock drag-drop whose
/// no-target release floated it into a native window; and every "reopen it
/// floating" preference (public #359) could bring a panel back outside the
/// host. The drag itself needs a live pointer, so what is asserted here is the
/// gate every float path resolves through — the root capability policy — plus
/// each factory entry point that used to float.</para>
///
/// <para>Driven through the real command on the real view-model, with the
/// DockControl following <see cref="MainWindowViewModel.DockLayout"/> the way
/// MainWindow.axaml's binding does.</para>
/// </summary>
public class WindowedModeToggleHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }
        private readonly DockControl _dock;
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_mdi_toggle_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;
            _dock = new DockControl
            {
                Layout           = Vm.DockLayout,
                Factory          = Factory,
                InitializeLayout = false,
            };
            ((INotifyPropertyChanged)Vm).PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.DockLayout))
                    _dock.Layout = Vm.DockLayout;
            };
            Main = new Window { Width = 1200, Height = 800, Content = _dock };
            Main.Show();
            Pump();
        }

        public IRootDock Root => (IRootDock)Vm.DockLayout!;

        public void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Main.UpdateLayout();
            }
        }

        public void Toggle()
        {
            Vm.ToggleWindowedModeCommand.Execute().Subscribe();
            Pump();
        }

        /// <summary>Ids shown as docked/MDI panels (not counting floats).</summary>
        public HashSet<string> InTree()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Root.VisibleDockables is { } top)
                foreach (var d in top) Collect(d, ids);
            return ids;
        }

        public HashSet<string> Floating()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in Root.Windows ?? new List<IDockWindow>())
                if (w.Layout is { } l) Collect(l, ids);
            return ids;
        }

        public IDockable? Find(string id)
        {
            IDockable? hit = null;
            void Walk(IDockable n)
            {
                if (hit is not null) return;
                if (string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)) { hit = n; return; }
                if (n is IDock d && d.VisibleDockables is { } kids) foreach (var k in kids) Walk(k);
            }
            Walk(Root);
            return hit;
        }

        /// <summary>The MDI host, when windowed.</summary>
        public IDocumentDock? Mdi => Find("mdi") as IDocumentDock;

        private static void Collect(IDockable n, HashSet<string> ids)
        {
            if (n is IDock d)
            {
                if (d.VisibleDockables is { } kids) foreach (var k in kids) Collect(k, ids);
                return;
            }
            if (n is not ISplitter && n.Id is { Length: > 0 } id) ids.Add(id);
        }

        public void Dispose()
        {
            foreach (var w in (Root.Windows ?? new List<IDockWindow>()).ToList())
                if (w.Host is Window host) { try { host.Close(); } catch { /* teardown */ } }
            try { Main.Close(); } catch { /* teardown */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── #363: tabbed → windowed keeps the open set ───────────────────────────

    [AvaloniaFact]
    public void Entering_windowed_mode_opens_exactly_the_panels_that_were_open()
    {
        using var h = new Harness();

        // Arrange a layout that differs from both defaults: close two default
        // panels, open two that are hidden by default.
        h.Factory.SetToolVisibility("backpack", false);
        h.Factory.SetToolVisibility("logons",   false);
        h.Factory.SetToolVisibility("vitals",   true);
        h.Factory.SetToolVisibility("experience", true);
        h.Pump();
        var before = h.InTree();
        before.UnionWith(h.Floating());

        h.Toggle();

        Assert.True(h.Vm.Display.WindowedMode);
        Assert.True(h.Factory.IsWindowedLayout);
        Assert.NotNull(h.Mdi);
        Assert.Equal(before.OrderBy(x => x), h.InTree().OrderBy(x => x));
        Assert.Empty(h.Floating());
        Assert.DoesNotContain("backpack", h.InTree());
        Assert.Contains("vitals", h.InTree());
    }

    [AvaloniaFact]
    public void A_floating_panel_becomes_a_child_window_not_a_float()
    {
        using var h = new Harness();
        h.Factory.FloatTool("thoughts");
        h.Pump();
        Assert.Contains("thoughts", h.Floating());

        h.Toggle();

        Assert.Contains("thoughts", h.InTree());
        Assert.Empty(h.Floating());
    }

    [AvaloniaFact]
    public void Child_windows_tile_where_their_dock_group_was()
    {
        using var h = new Harness();
        h.Toggle();

        var bounds = h.Factory.CaptureMdiBounds();
        // The stream tabs shared one group; as windows they must not all sit on
        // one rectangle (the old canonical cascade put every window at the same
        // 60% size, 24px apart).
        var streams = new[] { "talk", "whispers", "thoughts", "combat" }
            .Select(id => bounds[id]).ToList();
        Assert.True(streams.Select(b => (b.X, b.Y)).Distinct().Count() == streams.Count);
        // The Game window was the centre column, so it starts right of the
        // left column's windows.
        Assert.True(bounds["game-text"].X > bounds["room"].X);
    }

    [AvaloniaFact]
    public void Cached_windowed_geometry_wins_over_the_tile()
    {
        using var h = new Harness();
        h.Toggle();
        var room = (IMdiDocument)h.Find("room")!;
        room.MdiBounds = new DockRect(333, 111, 400, 250);

        h.Toggle();   // back to tabbed — the VM caches the MDI geometry
        h.Toggle();   // and windowed again

        var b = h.Factory.CaptureMdiBounds()["room"];
        Assert.Equal(333, b.X);
        Assert.Equal(111, b.Y);
        Assert.Equal(400, b.Width);
    }

    // ── #363: windowed → tabbed keeps the open set ───────────────────────────

    [AvaloniaFact]
    public void Leaving_windowed_mode_keeps_closed_panels_closed()
    {
        using var h = new Harness();
        h.Toggle();

        // In windowed mode: close two, open one.
        h.Factory.SetToolVisibility("room",   false);
        h.Factory.SetToolVisibility("combat", false);
        h.Factory.SetToolVisibility("injuries", true);
        h.Pump();
        var open = h.InTree();

        h.Toggle();

        Assert.False(h.Vm.Display.WindowedMode);
        Assert.False(h.Factory.IsWindowedLayout);
        Assert.Null(h.Mdi);
        var now = h.InTree();
        now.UnionWith(h.Floating());
        Assert.Equal(open.OrderBy(x => x), now.OrderBy(x => x));
        Assert.DoesNotContain("room", now);
        Assert.Contains("injuries", now);
    }

    [AvaloniaFact]
    public void A_round_trip_restores_the_tabbed_arrangement()
    {
        using var h = new Harness();
        // A custom arrangement: Thoughts dragged out of the streams group into
        // the Room dock, and the left column made wider.
        var streams  = (IDock)h.Find("streams")!;
        var roomDock = (IDock)h.Find("room-dock")!;
        h.Factory.MoveDockable(streams, roomDock, h.Find("thoughts")!, null);
        ((IDock)h.Find("left-col")!).Proportion   = 0.33;
        ((IDock)h.Find("center-col")!).Proportion = 0.45;   // 0.33 + 0.45 + 0.22 = 1
        h.Pump();
        Assert.Equal("room-dock", ParentId(h, "thoughts"));
        var treeBefore = h.Factory.CaptureLayout();

        h.Toggle();
        h.Toggle();

        Assert.Equal("room-dock", ParentId(h, "thoughts"));
        Assert.Equal(0.33, ((IDock)h.Find("left-col")!).Proportion, precision: 3);
        Assert.Equal(Leaves(treeBefore!).OrderBy(x => x), h.InTree().OrderBy(x => x));
    }

    [AvaloniaFact]
    public void Without_a_prior_tabbed_arrangement_the_default_structure_is_used()
    {
        using var h = new Harness();
        h.Toggle();
        // Loading a layout forgets the remembered tabbed arrangement, so leaving
        // windowed mode lands on the default structure — with the open set kept.
        h.Factory.ForgetTabbedArrangement();
        h.Factory.SetToolVisibility("backpack", false);
        h.Pump();

        h.Toggle();

        Assert.NotNull(h.Find("left-col"));
        Assert.NotNull(h.Find("streams"));
        Assert.DoesNotContain("backpack", h.InTree());
        Assert.Contains("room", h.InTree());
    }

    // ── #364: windowed mode contains its children ────────────────────────────

    [AvaloniaFact]
    public void Windowed_root_refuses_every_float()
    {
        using var h = new Harness();
        h.Toggle();

        var talk = h.Find("talk")!;
        // The gate the title-bar drag-out, the context menus and
        // FactoryBase.FloatDockable all resolve through.
        Assert.False(DockCapabilityResolver.IsEnabled(
            talk, DockCapability.Float, DockCapabilityResolver.ResolveOperationDock(talk)));

        h.Factory.FloatDockable(talk);
        h.Pump();
        Assert.Empty(h.Floating());
        Assert.Contains("talk", h.InTree());

        h.Factory.FloatTool("talk");
        h.Pump();
        Assert.Empty(h.Floating());
    }

    [AvaloniaFact]
    public void Tabbed_mode_still_floats()
    {
        using var h = new Harness();
        h.Toggle();
        h.Toggle();

        var talk = h.Find("talk")!;
        Assert.True(DockCapabilityResolver.IsEnabled(
            talk, DockCapability.Float, DockCapabilityResolver.ResolveOperationDock(talk)));
        h.Factory.FloatTool("talk");
        h.Pump();
        Assert.Contains("talk", h.Floating());
    }

    [AvaloniaFact]
    public void A_panel_remembered_as_floating_reopens_inside_the_host()
    {
        using var h = new Harness();
        // Float Thoughts in tabbed mode, then close it: it is "floated last", so
        // the Window menu reopens it floating (public #359, commit 7393f1c).
        h.Factory.FloatTool("thoughts");
        h.Pump();
        h.Vm.ToggleThoughtsCommand.Execute().Subscribe();
        h.Pump();
        Assert.True(h.Factory.FloatedLast("thoughts"));

        h.Toggle();
        Assert.DoesNotContain("thoughts", h.InTree());

        h.Vm.ToggleThoughtsCommand.Execute().Subscribe();
        h.Pump();

        Assert.Empty(h.Floating());
        Assert.Contains("thoughts", h.Mdi!.VisibleDockables!.Select(d => d.Id));
    }

    [AvaloniaFact]
    public void Mapper_and_script_manager_open_inside_the_host()
    {
        using var h = new Harness();
        h.Factory.SetToolVisibility("mapper", false);
        h.Pump();
        h.Toggle();

        // Both toggles prefer a float out of the box.
        h.Vm.ToggleMapperCommand.Execute().Subscribe();
        h.Vm.ToggleScriptsCommand.Execute().Subscribe();
        h.Pump();

        Assert.Empty(h.Floating());
        var inMdi = h.Mdi!.VisibleDockables!.Select(d => d.Id).ToList();
        Assert.Contains("mapper",  inMdi);
        Assert.Contains("scripts", inMdi);
    }

    [AvaloniaFact]
    public void A_reopened_panel_never_builds_a_tabbed_dock_beside_the_host()
    {
        using var h = new Harness();
        // Backpack's tabbed memory is "backpack-dock inside root-layout" — and
        // root-layout exists in the windowed tree too.
        h.Factory.SetToolVisibility("backpack", false);
        h.Pump();
        h.Toggle();

        h.Factory.SetToolVisibility("backpack", true);
        h.Pump();

        Assert.Null(h.Find("backpack-dock"));
        Assert.Contains("backpack", h.Mdi!.VisibleDockables!.Select(d => d.Id));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string? ParentId(Harness h, string id)
    {
        string? parent = null;
        void Walk(IDockable n)
        {
            if (n is not IDock d || d.VisibleDockables is null) return;
            foreach (var k in d.VisibleDockables)
            {
                if (string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)) parent = d.Id;
                Walk(k);
            }
        }
        Walk(h.Root);
        return parent;
    }

    private static List<string> Leaves(DockNodeSnapshot n)
    {
        var ids = new List<string>();
        void Walk(DockNodeSnapshot s)
        {
            if (s.Kind == "leaf" && s.Id is { } id) ids.Add(id);
            foreach (var c in s.Children) Walk(c);
        }
        Walk(n);
        return ids;
    }
}
