using System;
using System.IO;
using System.Linq;
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
/// Public #305 — a plugin window named "Spider" showed up in the Atmospherics
/// window.
///
/// <para>The text itself was never misrouted: <c>EchoToWindow("Spider", …)</c>
/// reaches its own <c>pluginwin:spider</c> panel by exact name. The panel was
/// put in the wrong place. A new plugin window homes in the right column
/// (<c>backpack-dock</c>); when a rearranged layout no longer has that dock or
/// the column that holds it — drag-created containers carry synthetic
/// <c>auto-</c> ids — <c>SetToolVisibility</c> fell back to the FIRST tool dock
/// in the tree, which in a customised layout is easily the stream group with
/// Atmospherics in it. The window then opened as one more tab of that
/// group.</para>
/// </summary>
public sealed class PluginWindowPlacementHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_pwplace_" + Guid.NewGuid().ToString("N"));
    private readonly MainWindowViewModel _vm;
    private readonly GenieDockFactory _factory;
    private readonly DockControl _dock;
    private readonly Window _main;

    public PluginWindowPlacementHeadlessTests()
    {
        Directory.CreateDirectory(_dir);
        _vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        _factory = (GenieDockFactory)_vm.DockFactory!;
        _dock    = new DockControl { Layout = _vm.DockLayout, Factory = _factory, InitializeLayout = false };
        _main    = new Window { Width = 1200, Height = 800, Content = _dock };
        _main.Show();
        Pump();
    }

    public void Dispose()
    {
        _main.Close();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        _main.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static DockNodeSnapshot Node(string kind, string? id, double prop,
                                         string? orientation = null, string? alignment = null,
                                         string? activeId = null, params DockNodeSnapshot[] children) =>
        new()
        {
            Kind = kind, Id = id, Proportion = prop,
            Orientation = orientation, Alignment = alignment, ActiveId = activeId,
            Children = children.ToList(),
        };

    /// <summary>A rearranged layout: the columns were dragged, so none of the
    /// shipped container ids survive. The stream group — Atmospherics active —
    /// is the first tool dock in the tree.</summary>
    private static DockNodeSnapshot RearrangedLayout(bool withBackpackGroup) =>
        Node("proportional", "auto-cols", 1, orientation: "Horizontal", children: withBackpackGroup
            ?
            [
                Node("tooldock", "auto-streams", 0.3, alignment: "Left", activeId: "atmospherics",
                     children: [Node("leaf", "atmospherics", double.NaN), Node("leaf", "combat", double.NaN)]),
                Node("splitter", null, double.NaN),
                Node("documentdock", "docs", 0.5, activeId: "game-text",
                     children: [Node("leaf", "game-text", double.NaN)]),
                Node("splitter", null, double.NaN),
                Node("tooldock", "auto-pack", 0.2, alignment: "Right", activeId: "backpack",
                     children: [Node("leaf", "backpack", double.NaN)]),
            ]
            :
            [
                Node("tooldock", "auto-streams", 0.3, alignment: "Left", activeId: "atmospherics",
                     children: [Node("leaf", "atmospherics", double.NaN), Node("leaf", "combat", double.NaN)]),
                Node("splitter", null, double.NaN),
                Node("documentdock", "docs", 0.7, activeId: "game-text",
                     children: [Node("leaf", "game-text", double.NaN)]),
            ]);

    private IDockable? Find(string id) => FindIn(_dock.Layout!, id);

    private static IDockable? FindIn(IDockable n, string id)
    {
        if (n.Id == id) return n;
        if (n is IDock d && d.VisibleDockables is not null)
            foreach (var c in d.VisibleDockables)
                if (FindIn(c, id) is { } f) return f;
        if (n is IRootDock r && r.Windows is not null)
            foreach (var w in r.Windows)
                if (w.Layout is { } l && FindIn(l, id) is { } f) return f;
        return null;
    }

    private IDock? ParentOf(string id)
    {
        var target = Find(id);
        return target is null ? null : ParentIn(_dock.Layout!, target);
    }

    private static IDock? ParentIn(IDockable n, IDockable target)
    {
        if (n is not IDock d) return null;
        if (d.VisibleDockables is not null)
            foreach (var c in d.VisibleDockables)
            {
                if (ReferenceEquals(c, target)) return d;
                if (ParentIn(c, target) is { } p) return p;
            }
        if (n is IRootDock r && r.Windows is not null)
            foreach (var w in r.Windows)
                if (w.Layout is { } l && ParentIn(l, target) is { } p) return p;
        return null;
    }

    private bool IsFloating(string id) =>
        (_dock.Layout as IRootDock)?.Windows?.Any(w => w.Layout is { } l && FindIn(l, id) is not null) ?? false;

    /// <summary>What <c>EchoToWindow("Spider", text)</c> does in the host once
    /// the name has cleared the reserved-window check.</summary>
    private void PluginEcho(string window, string text) =>
        _factory.GetOrCreatePluginWindow(window, show: false).AppendLine(text);

    [AvaloniaFact]
    public void Default_layout_first_write_opens_Spider_beside_the_Backpack()
    {
        _factory.SetToolVisibility("atmospherics", true);
        Pump();

        PluginEcho("Spider", "a spider skitters past");
        Pump();

        Assert.Equal("backpack-dock", ParentOf("pluginwin:spider")?.Id);
        Assert.Single(_factory.TryGetPluginWindow("Spider")!.Lines);
        Assert.Empty(_vm.StreamTabs.Atmospherics.Lines);
    }

    [AvaloniaFact]
    public void Rearranged_layout_never_puts_Spider_in_the_Atmospherics_group()
    {
        _dock.Layout = _factory.BuildLayout(RearrangedLayout(withBackpackGroup: false));
        Pump();

        PluginEcho("Spider", "a spider skitters past");
        Pump();

        var parent = ParentOf("pluginwin:spider");
        Assert.NotNull(parent);
        Assert.NotEqual("auto-streams", parent!.Id);
        Assert.DoesNotContain(parent.VisibleDockables!, d => d.Id == "atmospherics");
        // Nothing of its family is open, so it floats as its own window.
        Assert.True(IsFloating("pluginwin:spider"));

        // Stream group untouched: Atmospherics is still the active tab there.
        Assert.Equal("atmospherics", ((IDock)Find("auto-streams")!).ActiveDockable?.Id);
        Assert.Empty(_vm.StreamTabs.Atmospherics.Lines);
    }

    [AvaloniaFact]
    public void Rearranged_layout_puts_Spider_where_the_Backpack_went()
    {
        _dock.Layout = _factory.BuildLayout(RearrangedLayout(withBackpackGroup: true));
        Pump();

        PluginEcho("Spider", "a spider skitters past");
        Pump();

        Assert.Equal("auto-pack", ParentOf("pluginwin:spider")?.Id);
    }

    [AvaloniaFact]
    public void Later_writes_to_an_existing_Spider_append_in_place_without_moving_it()
    {
        _dock.Layout = _factory.BuildLayout(RearrangedLayout(withBackpackGroup: true));
        Pump();
        PluginEcho("Spider", "one");
        Pump();

        // The user closes it; a passive append must not re-open or re-home it.
        _factory.SetToolVisibility("pluginwin:spider", false);
        Pump();
        PluginEcho("spider", "two");     // names are case-insensitive
        Pump();

        Assert.Null(Find("pluginwin:spider"));
        Assert.Equal(2, _factory.TryGetPluginWindow("Spider")!.Lines.Count);
        Assert.Empty(_vm.StreamTabs.Atmospherics.Lines);

        // Re-opened from the Window menu it comes back where it was.
        _factory.SetToolVisibility("pluginwin:spider", true);
        Pump();
        Assert.Equal("auto-pack", ParentOf("pluginwin:spider")?.Id);
    }

    [AvaloniaFact]
    public void Windowed_mode_opens_a_new_plugin_window_as_an_MDI_child()
    {
        _dock.Layout = _factory.BuildMdiLayout();
        Pump();

        PluginEcho("Spider", "a spider skitters past");
        Pump();

        Assert.Equal("mdi", ParentOf("pluginwin:spider")?.Id);
    }
}
