using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// The window menu's Float / Re-dock item names the wrong verb the first time
/// the menu opens on a window whose float state changed without the menu being
/// told: a tab double-clicked or dragged out (Dock floats it, no menu involved),
/// or a float dragged back into the dock.
///
/// <para><see cref="WindowMenuBehavior"/> re-reads the live state when the menu
/// opens, but it did so only in <c>Opening</c>, and on a window's first open the
/// menu has not inherited that window's DataContext yet (the same first-open trap
/// its own <c>OnOpened</c> already works around for the Copy target). The refresh
/// found no host and was skipped, so a floating window offered "Float". The item
/// still re-docked when clicked, because the command reads the live state; only
/// the label was stale. Opening the same menu a second time corrected it.</para>
///
/// <para>The existing tests call <c>RefreshFloatState()</c> directly ("what
/// opening the menu does"). These go through the real path: the production
/// layout and skins, a real <see cref="GenieHostWindow"/>, and a
/// <c>ContextRequested</c> on the window's content, which is what a right-click
/// raises.</para>
/// </summary>
public class WindowMenuFloatLabelHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public GenieDockFactory Factory { get; }
        public Window           Main    { get; }
        public IRootDock        Root    { get; }
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_float_label_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var vm  = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)vm.DockFactory!;
            Main    = new Window
            {
                Width = 1400, Height = 900,
                Content = new DockControl { Layout = vm.DockLayout, Factory = Factory, InitializeLayout = false },
            };
            Main.Show();
            Root = (IRootDock)vm.DockLayout!;
            Pump(); Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        private IEnumerable<IDockWindow> Floats => Root.Windows ?? new List<IDockWindow>();

        /// <summary>The dockable, wherever it lives: the docked tree or a float.</summary>
        public IDockable Tool(string id)
        {
            if (FindIn(Root, id) is { } docked) return docked;
            foreach (var w in Floats)
                if (w.Layout is { } layout && FindIn(layout, id) is { } floating) return floating;
            throw new Xunit.Sdk.XunitException($"no dockable '{id}'");
        }

        private static IDockable? FindIn(IDockable node, string id)
        {
            if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) return node;
            if (node is IDock d && d.VisibleDockables is { } kids)
                foreach (var k in kids)
                    if (FindIn(k, id) is { } f) return f;
            return null;
        }

        public WindowMenuModel Model(string id) => ((IWindowMenuHost)Tool(id)).WindowMenu!;

        /// <summary>Ids sharing the tool's tab group, or empty when it floats.</summary>
        public string[] DockedBeside(string id) =>
            FindIn(Root, id)?.Owner is IDock { VisibleDockables: { } sibs }
                ? sibs.Select(s => s.Id ?? "").ToArray()
                : Array.Empty<string>();

        /// <summary>What a double-click on the tab does: Dock floats the dockable
        /// itself. Genie's own Float item is not involved, so nothing refreshes
        /// the menu model.</summary>
        public void FloatByDoubleClick(string id)
        {
            Factory.FloatDockable(Tool(id));
            Pump(); Pump();
        }

        /// <summary>Right-click the window: raise ContextRequested on the control
        /// that carries its menu, and return the menu, open.</summary>
        public ContextMenu RightClick(string id)
        {
            var tool = Tool(id);
            var tops = new List<Window> { Main };
            tops.AddRange(Floats.Select(w => w.Host).OfType<Window>());
            // The window's content host (ToolControlCachedSkin) carries Genie's
            // menu. A docked tool's tab carries Dock's own small menu with the
            // same DataContext, so pick the host by name.
            var target = tops.SelectMany(t => t.GetVisualDescendants().OfType<Control>())
                             .FirstOrDefault(c => c.Name == "PART_CachedContentPresenter"
                                               && c.ContextMenu is not null
                                               && ReferenceEquals(c.DataContext, tool))
                         ?? throw new Xunit.Sdk.XunitException($"'{id}' has no control carrying the window menu");
            var menu = target.ContextMenu!;
            target.RaiseEvent(new ContextRequestedEventArgs());
            Pump();
            Assert.True(menu.IsOpen, "the window menu should have opened");
            return menu;
        }

        /// <summary>The Float / Re-dock item of an open menu, as the user reads it.</summary>
        public MenuItem FloatItem(ContextMenu menu, string id) =>
            menu.Items.OfType<MenuItem>().First(mi => ReferenceEquals(mi.Command, Model(id).ToggleFloatCommand));

        public void Dispose()
        {
            foreach (var w in Floats.ToList())
                if (w.Host is Window host) { try { host.Close(); } catch { /* teardown */ } }
            try { Main.Close(); } catch { /* teardown */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public void A_docked_window_offers_Float_the_first_time_its_menu_opens()
    {
        using var h = new Harness();

        var menu = h.RightClick("combat");

        Assert.Equal("Float", h.FloatItem(menu, "combat").Header as string);
    }

    [AvaloniaFact]
    public void A_window_floated_by_double_click_offers_Re_dock_the_first_time_its_menu_opens()
    {
        using var h = new Harness();
        h.FloatByDoubleClick("combat");
        Assert.True(h.Factory.IsToolFloating("combat"));

        var menu = h.RightClick("combat");

        Assert.Equal("Re-dock", h.FloatItem(menu, "combat").Header as string);
    }

    [AvaloniaFact]
    public void Clicking_that_item_puts_the_window_back_in_its_tab_group()
    {
        using var h = new Harness();
        var home = h.DockedBeside("combat");
        Assert.Contains("thoughts", home);
        h.FloatByDoubleClick("combat");

        var menu = h.RightClick("combat");
        h.FloatItem(menu, "combat").Command!.Execute(null);
        h.Pump(); h.Pump();

        Assert.False(h.Factory.IsToolFloating("combat"));
        Assert.Equal(home, h.DockedBeside("combat"));
    }

    [AvaloniaFact]
    public void A_float_put_back_without_the_menu_offers_Float_the_first_time_its_menu_opens()
    {
        // The other direction: the model last heard "floating" (its menu was
        // opened while it floated), then the window was dragged back into the
        // dock. Its docked content is a new control, so this is a first open again.
        using var h = new Harness();
        h.FloatByDoubleClick("combat");
        h.RightClick("combat").Close();
        h.RightClick("combat").Close();                 // second open: the model now knows it floats
        Assert.Equal("Re-dock", h.Model("combat").FloatHeader);

        h.Factory.RedockTool("combat");                 // back in the dock, menu not involved
        h.Pump(); h.Pump();
        Assert.False(h.Factory.IsToolFloating("combat"));

        var menu = h.RightClick("combat");

        Assert.Equal("Float", h.FloatItem(menu, "combat").Header as string);
    }

    [AvaloniaFact]
    public void A_floating_window_offers_Hide_Title_Bar_the_first_time_its_menu_opens()
    {
        // Same refresh, second symptom: the item only shows for a float.
        using var h = new Harness();
        Assert.False(h.Model("combat").ShowHideTitleBar);
        h.FloatByDoubleClick("combat");

        h.RightClick("combat");

        Assert.True(h.Model("combat").ShowHideTitleBar);
    }
}
