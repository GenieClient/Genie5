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
using Genie.Core.Dialogs;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #156, "Where DR proposes", over the real factory and DockControl:
/// the server's placement decides a dialog's FIRST appearance; after that
/// wherever the user put it wins — except <c>force-center</c>, which re-centres
/// every time.
/// </summary>
public class ServerDialogPlacementHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }
        private readonly string    _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_dlg_place_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;
            Main = new Window
            {
                Width = 1200, Height = 800,
                Content = new DockControl { Layout = Vm.DockLayout, Factory = Factory, InitializeLayout = false },
            };
            Main.Show();
            Pump(Main);
        }

        public IRootDock Root => (IRootDock)Vm.DockLayout!;

        public GenieHostWindow? FloatFor(string id)
        {
            foreach (var w in Root.Windows ?? new System.Collections.Generic.List<IDockWindow>())
                if (w.Layout is { } l && Contains(l, id) && w.Host is GenieHostWindow g) return g;
            return null;
        }

        /// <summary>Id of the docked parent holding <paramref name="id"/>, or null.</summary>
        public string? DockedParentOf(string id) => ParentOf(Root, id)?.Id;

        private static IDock? ParentOf(IDockable node, string id)
        {
            if (node is not IDock d || d.VisibleDockables is not { } kids) return null;
            foreach (var k in kids)
            {
                if (string.Equals(k.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
                if (ParentOf(k, id) is { } p) return p;
            }
            return null;
        }

        private static bool Contains(IDockable node, string id)
        {
            if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            if (node is IDock d && d.VisibleDockables is { } kids)
                foreach (var k in kids) if (Contains(k, id)) return true;
            return false;
        }

        public static void Pump(Window w)
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        public void Open(string dialogId, string? location, string? width = null, string? height = null)
        {
            Factory.GetOrCreateServerDialog(dialogId, dialogId, show: true,
                ServerDialogPlacement.From(location, width, height));
            Pump(Main);
        }

        public void Dispose()
        {
            foreach (var w in (Root.Windows ?? new System.Collections.Generic.List<IDockWindow>()).ToList())
                if (w.Host is Window host) { try { host.Close(); } catch { } }
            try { Main.Close(); } catch { }
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private static string Id(string dialogId) => GenieDockFactory.ServerDialogId(dialogId);

    [AvaloniaFact]
    public void Right_docks_in_the_right_column_as_before()
    {
        using var h = new Harness();
        h.Open("injuries-x", "right");

        Assert.Null(h.FloatFor(Id("injuries-x")));
        Assert.Equal("backpack-dock", h.DockedParentOf(Id("injuries-x")));
    }

    [AvaloniaFact]
    public void Left_docks_in_the_left_column()
    {
        using var h = new Harness();
        h.Open("leftish", "left");

        Assert.Null(h.FloatFor(Id("leftish")));
        Assert.Equal("room-dock", h.DockedParentOf(Id("leftish")));
    }

    [AvaloniaFact]
    public void Center_floats_sized_from_the_servers_hint()
    {
        using var h = new Harness();
        h.Open("spellChoose", "center", "600", "460");

        var host = h.FloatFor(Id("spellChoose"));
        Assert.NotNull(host);
        Assert.True(host!.Width  >= 600, $"width {host.Width}");
        Assert.True(host.Height  >= 460, $"height {host.Height}");
    }

    /// <summary>The hint is a first-appearance default: once the user has
    /// moved a centred dialog, it comes back where they left it.</summary>
    [AvaloniaFact]
    public void A_moved_center_dialog_reopens_where_the_user_left_it()
    {
        using var h = new Harness();
        h.Open("spellChoose", "center", "600", "460");
        var host = h.FloatFor(Id("spellChoose"))!;
        host.Position = new PixelPoint(40, 50);
        Harness.Pump(host);

        h.Factory.HideServerDialog("spellChoose");
        Harness.Pump(h.Main);
        h.Factory.ExposeServerDialog("spellChoose");
        Harness.Pump(h.Main);

        var again = h.FloatFor(Id("spellChoose"));
        Assert.NotNull(again);
        Assert.Equal(new PixelPoint(40, 50), again!.Position);
    }

    /// <summary>A user who docks a centred dialog keeps it docked.</summary>
    [AvaloniaFact]
    public void A_docked_dialog_is_not_refloated_by_its_hint()
    {
        using var h = new Harness();
        h.Open("rightfirst", "right");             // docked first
        h.Factory.HideServerDialog("rightfirst");
        Harness.Pump(h.Main);

        h.Open("rightfirst", "center", "300", "200");   // hint changes, user placement stands

        Assert.Null(h.FloatFor(Id("rightfirst")));
        Assert.Equal("backpack-dock", h.DockedParentOf(Id("rightfirst")));
    }

    /// <summary>Changing a dialog's answer resets where the old one put it, so
    /// the new placement actually happens.</summary>
    [AvaloniaFact]
    public void Resetting_placement_lets_the_new_hint_apply()
    {
        using var h = new Harness();
        h.Open("switcher", "right");
        h.Factory.ResetServerDialogPlacement("switcher");
        Harness.Pump(h.Main);

        Assert.Null(h.DockedParentOf(Id("switcher")));   // reset closes it

        h.Open("switcher", "center", "300", "200");
        Assert.NotNull(h.FloatFor(Id("switcher")));
    }
}
