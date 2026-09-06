using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #331 — a panel re-opened into its home dock must actually be visible,
/// even when the column that home lives in has been squeezed to nothing.
///
/// <para><b>The shape that breaks it</b> (taken verbatim from the reporter's
/// saved layout). Drag every panel out of the left column over time and the
/// column survives as an empty node whose Proportion has been renormalized to
/// <c>0</c> — and a saved layout persists that zero:</para>
/// <code>
/// root-layout        prop=1
///   left-col         prop=0     &lt;- empty, zero width
///   splitter
///   center-col       prop=1     &lt;- everything actually lives here now
/// </code>
/// <para>Re-opening Mobs / Players / Objects then finds no <c>room-dock</c> —
/// it was pruned long ago — so <c>TryRestoreHomeDock</c> rebuilds it inside its
/// recorded grandparent, <c>left-col</c>. That succeeds, the model is perfect,
/// and the panel is invisible: its brand-new dock is sitting in a column with
/// zero width.</para>
///
/// <para>These assert on rendered <see cref="Avalonia.Visual.Bounds"/> rather
/// than model state, because the model was right at every step of the live bug.
/// A model-level test passes while the user stares at a one-pixel sliver.</para>
/// </summary>
public class CollapsedDockRefillHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }
        private readonly DockControl _dockControl;
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_collapse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;

            _dockControl = new DockControl
            {
                Layout = Vm.DockLayout,
                Factory = Factory,
                InitializeLayout = false,
            };
            Main = new Window { Width = 1200, Height = 800, Content = _dockControl };
            Main.Show();
            Pump();
            Pump();
        }

        /// <summary>Swap in a layout built from a snapshot, the way loading a
        /// saved layout does.</summary>
        public void LoadLayout(DockNodeSnapshot snapshot)
        {
            _dockControl.Layout = Factory.BuildLayout(snapshot);
            Pump();
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Rendered width of the container holding the dock with this
        /// id. Zero when it is collapsed, squeezed out, or absent.</summary>
        public double RenderedWidth(string dockId)
        {
            var presenter = Main.GetVisualDescendants()
                .OfType<ContentPresenter>()
                .FirstOrDefault(p => (p.Content as IDockable)?.Id == dockId
                                  || (p.DataContext as IDockable)?.Id == dockId);
            return presenter?.Bounds.Width ?? 0;
        }

        public IDockable? Find(string id) => FindIn(_dockControl.Layout!, id);

        private static IDockable? FindIn(IDockable n, string id)
        {
            if (n.Id == id) return n;
            if (n is IDock d && d.VisibleDockables is not null)
                foreach (var c in d.VisibleDockables)
                    if (FindIn(c, id) is { } f) return f;
            return null;
        }

        public void Dispose()
        {
            Main.Close();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static DockNodeSnapshot Node(string kind, string? id, double prop,
                                         string? orientation = null, string? alignment = null,
                                         string? activeId = null,
                                         params DockNodeSnapshot[] children) =>
        new()
        {
            Kind = kind, Id = id, Proportion = prop,
            Orientation = orientation, Alignment = alignment, ActiveId = activeId,
            Children = children.ToList(),
        };

    /// <summary>The reporter's layout: an empty left-col at Proportion 0, with
    /// every real panel moved into center-col.</summary>
    private static DockNodeSnapshot SqueezedLeftColumn() =>
        Node("proportional", "root-layout", 1, orientation: "Horizontal", children:
        [
            Node("proportional", "left-col", 0, orientation: "Vertical"),
            Node("splitter", null, double.NaN),
            Node("proportional", "center-col", 1, orientation: "Vertical", children:
            [
                Node("tooldock", "auto-roomhome", 0.35, alignment: "Left", activeId: "room",
                     children: [Node("leaf", "room", double.NaN)]),
                Node("splitter", null, double.NaN),
                Node("documentdock", "docs", 0.65, activeId: "game-text",
                     children: [Node("leaf", "game-text", double.NaN)]),
            ]),
        ]);

    // ── Baseline: the default layout is unaffected ───────────────────────

    [AvaloniaFact]
    public void Re_opening_into_a_healthy_column_works()
    {
        using var h = new Harness();
        h.Factory.SetToolVisibility("objects", true);
        h.Pump();

        Assert.True(h.RenderedWidth("room-dock") > 0);
    }

    [AvaloniaFact]
    public void Floating_the_last_panel_out_still_collapses_an_emptied_dock()
    {
        // #219 must keep working — an emptied dock measures at zero so the
        // column doesn't leave a dead gutter.
        using var h = new Harness();
        Assert.True(h.RenderedWidth("room-dock") > 0, "room-dock should start visible");

        h.Factory.RemoveDockable(h.Find("room")!, collapse: false);
        h.Pump();

        Assert.Equal(0, h.RenderedWidth("room-dock"));
    }

    // ── #331 ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Home_dock_rebuilt_into_a_zero_width_column_is_still_visible()
    {
        using var h = new Harness();
        h.LoadLayout(SqueezedLeftColumn());

        // Precondition: this is the reporter's shape — left-col present, empty,
        // and measuring nothing.
        Assert.NotNull(h.Find("left-col"));
        Assert.Equal(0, h.RenderedWidth("left-col"));

        h.Factory.SetToolVisibility("objects", true);
        h.Pump();

        // The model side always worked; it is the pixels that went missing.
        Assert.NotNull(h.Find("objects"));
        Assert.True(h.RenderedWidth("room-dock") > 0,
            "Objects was rebuilt into left-col, which has Proportion 0 — the panel is invisible (#331).");
    }

    [AvaloniaFact]
    public void Every_panel_homed_in_the_squeezed_column_comes_back()
    {
        // Mobs and Players share room-dock as their home, which is why the
        // reporter found all three panels equally unreachable.
        using var h = new Harness();
        h.LoadLayout(SqueezedLeftColumn());

        foreach (var id in new[] { "mobs", "players", "objects" })
        {
            h.Factory.SetToolVisibility(id, true);
            h.Pump();
            Assert.NotNull(h.Find(id));
        }

        Assert.True(h.RenderedWidth("room-dock") > 0,
            "Mobs, Players and Objects all landed in a zero-width column (#331).");
    }
}
