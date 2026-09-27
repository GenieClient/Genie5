using System;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
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
/// Public #299 — "Hide Title Bar" for a window docked ALONE in its frame.
///
/// <para>Dock already shrinks a lone tool's tab strip to a sliver, so the row a
/// single-tab frame wastes is the chrome's title band (and, for the Game group,
/// the document tab strip). The window's right-click menu offered "Hide Title
/// Bar" only for floats; these tests pin the docked half over the PRODUCTION
/// dock layout and the app's own chrome styles (HeadlessApp loads
/// BannerChromeStyles.axaml, where the behavior is switched on): the item is
/// offered exactly when the window is alone, it collapses the right part, it
/// persists per window, and the header comes back as soon as the frame is
/// shared.</para>
/// </summary>
public class DockedTitleBarHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }
        public string              Dir     { get; }
        private readonly DockControl _dockControl;

        public Harness()
        {
            Dir = Path.Combine(Path.GetTempPath(), "genie_dtb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: Dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;
            _dockControl = new DockControl { Layout = Vm.DockLayout, Factory = Factory, InitializeLayout = false };
            Main = new Window { Width = 1400, Height = 900, Content = _dockControl };
            Main.Show();
            Pump();
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public IDockable Find(string id) => FindIn(_dockControl.Layout!, id)
            ?? throw new Xunit.Sdk.XunitException($"no dockable '{id}' in the layout");

        private static IDockable? FindIn(IDockable n, string id)
        {
            if (n.Id == id) return n;
            if (n is IDock d && d.VisibleDockables is not null)
                foreach (var c in d.VisibleDockables)
                    if (FindIn(c, id) is { } f) return f;
            return null;
        }

        public WindowMenuModel Menu(string id)
        {
            var menu = ((IWindowMenuHost)Find(id)).WindowMenu!;
            menu.RefreshFloatState();   // what opening the menu does
            return menu;
        }

        /// <summary>The chrome whose frame (tool dock) holds <paramref name="id"/>.</summary>
        public ToolChromeControl Chrome(string id)
        {
            var owner = Find(id).Owner;
            return Main.GetVisualDescendants().OfType<ToolChromeControl>()
                .First(c => ReferenceEquals(c.DataContext, owner));
        }

        /// <summary>The chrome's header band — a DIRECT child of its template
        /// Grid (the ToolControl skin nested in the content also has a
        /// PART_Border, which must not be the one we read).</summary>
        public static Control Band(ToolChromeControl chrome) =>
            chrome.GetVisualChildren().OfType<Grid>().First()
                  .GetVisualChildren().OfType<Control>().First(c => c.Name == "PART_Border");

        public DocumentTabStrip GameStrip =>
            Main.GetVisualDescendants().OfType<DocumentTabStrip>().First(t => t.Name == "PART_TabStrip");

        public void Toggle(string id)
        {
            ICommand cmd = Menu(id).ToggleTitleBarCommand!;
            cmd.Execute(null);
            Pump();
        }

        public void Dispose()
        {
            Main.Close();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>The Room panel sits alone in its frame in the default layout.</summary>
    private const string Lone = "room";

    /// <summary>The shared Streams frame (talk, thoughts, …).</summary>
    private const string Shared = "thoughts";

    [AvaloniaFact]
    public void A_window_alone_in_its_frame_is_offered_Hide_Title_Bar()
    {
        using var h = new Harness();
        var menu = h.Menu(Lone);

        Assert.True(GenieDockFactory.IsAloneInFrame(h.Find(Lone)));
        Assert.True(menu.ShowHideTitleBar, "a lone docked window must offer the item");
        Assert.Equal("Hide Title Bar", menu.TitleBarHeader);
    }

    [AvaloniaFact]
    public void A_window_sharing_its_frame_is_not_offered_the_item()
    {
        using var h = new Harness();
        Assert.False(GenieDockFactory.IsAloneInFrame(h.Find(Shared)));
        Assert.False(h.Menu(Shared).ShowHideTitleBar,
            "the tabs are how a shared frame switches windows; its header stays");
    }

    [AvaloniaFact]
    public void Hiding_collapses_the_chrome_band_and_showing_restores_it()
    {
        using var h = new Harness();
        var band = Harness.Band(h.Chrome(Lone));
        Assert.True(band.IsVisible);

        h.Toggle(Lone);
        Assert.False(Harness.Band(h.Chrome(Lone)).IsVisible, "Hide Title Bar left the band showing");
        Assert.Equal("Show Title Bar", h.Menu(Lone).TitleBarHeader);
        Assert.True(h.Menu(Lone).ShowHideTitleBar, "the way back must stay in the menu");

        h.Toggle(Lone);
        Assert.True(Harness.Band(h.Chrome(Lone)).IsVisible, "Show Title Bar did not bring the band back");
        Assert.Equal("Hide Title Bar", h.Menu(Lone).TitleBarHeader);
    }

    [AvaloniaFact]
    public void Only_the_lone_frames_band_is_collapsed()
    {
        using var h = new Harness();
        h.Toggle(Lone);

        var others = h.Main.GetVisualDescendants().OfType<ToolChromeControl>()
            .Where(c => !ReferenceEquals(c, h.Chrome(Lone)))
            .ToList();
        Assert.NotEmpty(others);
        Assert.All(others, c => Assert.True(Harness.Band(c).IsVisible));
    }

    [AvaloniaFact]
    public void The_choice_is_persisted_per_window()
    {
        using var h = new Harness();
        h.Toggle(Lone);

        Assert.True(h.Vm.WindowSettings.Get(Lone).HideTitleBarWhenAlone);
        Assert.False(h.Vm.WindowSettings.Get(Shared).HideTitleBarWhenAlone);

        var files = Directory.GetFiles(h.Dir, "windows.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => File.ReadAllText(f).Contains("\"HideTitleBarWhenAlone\": true"));
    }

    [AvaloniaFact]
    public void A_setting_loaded_at_connect_applies_without_the_menu()
    {
        using var h = new Harness();

        // The connect-time windows.json load mutates the settings, then fires
        // Changed on every window so the menus resync — the same two steps.
        var s = h.Vm.WindowSettings.Get(Lone);
        s.HideTitleBarWhenAlone = true;
        s.NotifyChanged();
        h.Pump();

        Assert.False(Harness.Band(h.Chrome(Lone)).IsVisible);
        Assert.Equal("Show Title Bar", h.Menu(Lone).TitleBarHeader);
    }

    [AvaloniaFact]
    public void The_header_returns_when_a_second_tab_joins_and_hides_again_when_it_leaves()
    {
        using var h = new Harness();
        h.Toggle(Lone);
        var chrome = h.Chrome(Lone);
        Assert.False(Harness.Band(chrome).IsVisible);

        var dock  = (IDock)h.Find(Lone).Owner!;
        var guest = new MobsTool(h.Vm.Mobs) { Id = "mobs-guest", Title = "Mobs" };
        guest.Owner = dock;
        dock.VisibleDockables!.Add(guest);
        h.Pump();
        Assert.True(Harness.Band(chrome).IsVisible, "a shared frame must show its header");

        dock.VisibleDockables!.Remove(guest);
        h.Pump();
        Assert.False(Harness.Band(chrome).IsVisible, "alone again, the saved choice applies again");
    }

    [AvaloniaFact]
    public void The_global_banner_switch_still_wins_when_it_is_off()
    {
        using var h = new Harness();
        try
        {
            BannerChrome.SetVisible(false);
            h.Pump();
            h.Toggle(Lone);   // hide
            h.Toggle(Lone);   // show again — per window
            Assert.False(Harness.Band(h.Chrome(Lone)).IsVisible,
                "turning the per-window hide off must not re-show a band banners-off hides");
        }
        finally
        {
            BannerChrome.SetVisible(true);
        }
    }

    [AvaloniaFact]
    public void The_Game_window_alone_hides_its_tab_strip()
    {
        using var h = new Harness();
        var game = h.Find("docs") is IDock docs ? docs.VisibleDockables!.Single() : null;
        Assert.NotNull(game);
        var id = game!.Id;

        Assert.True(h.Menu(id).ShowHideTitleBar);
        Assert.True(h.GameStrip.IsVisible);

        h.Toggle(id);
        Assert.False(h.GameStrip.IsVisible, "the Game group's tab strip should collapse");

        h.Toggle(id);
        Assert.True(h.GameStrip.IsVisible);
    }
}
