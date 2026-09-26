using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Mapper Details flyout, over the PRODUCTION MapperTool DataTemplate from
/// App.axaml: hovering the collapsed "DETAILS" strip slides the panel out and
/// moving the pointer off it hides it again; CLICKING the strip pins it open.
/// <para>The click path exists because hover alone is not reliable in the
/// floated Mapper on Windows: Dock's tool float extends the client area, and
/// Avalonia's Win32 backend treats the outer 8 px of such a window as a resize
/// grip (HitTestNCA), so the edge third of the strip never sees the pointer
/// and crossing into it clears IsPointerOver. The headless platform has no
/// non-client band, so that failure can't be reproduced here — these tests
/// pin down the Avalonia-side contract (hover + click) the fix relies on.</para>
/// </summary>
public class MapperDetailsFlyoutHeadlessTests
{
    private static Genie.App.App? _app;

    /// <summary>Register the production App.axaml DataTemplates (the MapperTool
    /// view) + the theme palette on the CURRENT application. The XAML is loaded
    /// once, but the registration runs per test: the headless runner hands each
    /// test a fresh Application, so a one-shot static guard would leave every
    /// test after the first rendering MapperTool as its ToString().</summary>
    private static void EnsureProductionTemplates()
    {
        // App.Initialize() is the compiled-in App.axaml populate; the runtime
        // AvaloniaXamlLoader can't see another assembly's precompiled XAML.
        _app ??= Build();
        static Genie.App.App Build() { var a = new Genie.App.App(); a.Initialize(); return a; }

        var cur = Application.Current!;
        if (!cur.DataTemplates.OfType<Avalonia.Markup.Xaml.Templates.DataTemplate>().Any(t => t.DataType == typeof(MapperTool)))
            foreach (var t in _app.DataTemplates) cur.DataTemplates.Add(t);
        if (!cur.Resources.ContainsKey("Theme.PanelBg"))
            cur.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Genie.App"))
            {
                Source = new Uri("avares://Genie5/Themes/ThemePalette.axaml")
            });
    }

    private static void Pump(Window w)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    private static T Named<T>(Visual root, string name) where T : Control
    {
        var hit = root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        if (hit is not null) return hit;
        var all = root.GetVisualDescendants().ToList();
        var names = string.Join(",", all.OfType<Control>().Where(c => c.Name is not null).Select(c => c.Name));
        var types = string.Join(">", all.Take(30).Select(c => c.GetType().Name));
        throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named {name}; {all.Count} visuals; names=[{names}]; types=[{types}]");
    }

    [AvaloniaFact]
    public void Hovering_the_collapsed_strip_opens_the_details_panel()
    {
        EnsureProductionTemplates();
        var tool   = new MapperTool(new MapperViewModel());
        var window = new Window { Width = 1000, Height = 700, Content = new ContentControl { Content = tool } };
        window.Show();
        Pump(window);
        try
        {
            var strip = Named<ToggleButton>(window, "DetailsStrip");
            var panel = Named<Border>(window, "DetailsPanel");
            Assert.True(strip.IsVisible, "strip should show while the panel is hidden");
            Assert.False(panel.IsVisible, "panel should start hidden");

            var centre = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), window);
            Assert.NotNull(centre);
            window.MouseMove(centre!.Value);
            Pump(window);

            Assert.True(panel.IsVisible, $"hovering the strip at {centre} did not open the panel (strip.IsPointerOver={strip.IsPointerOver}, flyout.IsPointerOver={Named<Border>(window, "DetailsFlyout").IsPointerOver})");

            // Move well away → auto-hide.
            window.MouseMove(new Point(50, 300));
            Pump(window);
            Assert.False(panel.IsVisible, "panel should auto-hide once the pointer leaves");
            Assert.True(strip.IsVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>The screenshot case: the Mapper floated in its own
    /// GenieHostWindow through the production dock factory.</summary>
    [AvaloniaFact]
    public void Hovering_the_strip_opens_the_panel_in_a_floated_mapper()
    {
        EnsureProductionTemplates();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "genie_flyout_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: dir);
        var factory = (GenieDockFactory)vm.DockFactory!;
        var main = new Window
        {
            Width = 1200, Height = 800,
            Content = new Dock.Avalonia.Controls.DockControl { Layout = vm.DockLayout, Factory = factory, InitializeLayout = false },
        };
        main.Show();
        Pump(main);
        factory.FloatTool("mapper");
        Pump(main);
        var windows = ((Dock.Model.Controls.IRootDock)vm.DockLayout!).Windows!;
        var host = windows.Select(w => w.Host).OfType<GenieHostWindow>().First();
        Pump(host); Pump(host);
        try
        {
            var strip = Named<ToggleButton>(host, "DetailsStrip");
            var panel = Named<Border>(host, "DetailsPanel");
            Assert.False(panel.IsVisible);

            // The float's content must sit clear of the Win32 resize grip, or
            // the edge third of this strip is non-client (see GenieHostWindow).
            Assert.True(host.ExtendClientAreaToDecorationsHint,
                "Dock's theme extends the client area for a lone floated tool; the inset keys off that");
            var presenter = host.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_ContentPresenter");
            if (OperatingSystem.IsWindows())
            {
                var band = Math.Ceiling(GenieHostWindow.ResizeGripBandPx / Math.Max(host.RenderScaling, 0.5));
                Assert.Equal(new Thickness(band, 0, band, band), host.ResizeGripInset);
                Assert.Equal(host.ResizeGripInset, presenter.Margin);
                var stripRight = strip.TranslatePoint(new Point(strip.Bounds.Width, 0), host)!.Value.X;
                Assert.True(stripRight <= host.Bounds.Width - band,
                    $"strip right edge {stripRight} reaches into the grip band of a {host.Bounds.Width}-wide window");
            }
            else
            {
                Assert.Equal(default, host.ResizeGripInset);
            }

            var centre = strip.TranslatePoint(new Point(strip.Bounds.Width / 2, strip.Bounds.Height / 2), host);
            Assert.NotNull(centre);
            host.MouseMove(centre!.Value);
            Pump(host);
            Assert.True(panel.IsVisible, $"floated: hover at {centre} (host {host.Bounds}) did not open the panel; strip.IsPointerOver={strip.IsPointerOver}");

            host.MouseMove(new Point(30, 300));
            Pump(host);
            Assert.False(panel.IsVisible);
        }
        finally
        {
            try { host.Close(); } catch { }
            try { main.Close(); } catch { }
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>The strip is a ToggleButton bound to DetailsPinned so a pointer
    /// that never hovers (touch, pen — Avalonia sets IsPointerOver for non-touch
    /// pointers only) still has a way in: tapping it pins the panel open. A mouse
    /// can't press it in practice, since hovering opens the panel under the
    /// cursor first — hence the toggle is driven directly here.</summary>
    [AvaloniaFact]
    public void Toggling_the_strip_pins_the_panel_open_until_unpinned()
    {
        EnsureProductionTemplates();
        var vm     = new MapperViewModel();
        var tool   = new MapperTool(vm);
        var window = new Window { Width = 1000, Height = 700, Content = new ContentControl { Content = tool } };
        window.Show();
        Pump(window);
        try
        {
            var strip = Named<ToggleButton>(window, "DetailsStrip");
            var panel = Named<Border>(window, "DetailsPanel");
            Assert.False(panel.IsVisible);

            strip.IsChecked = true;      // what a tap on the strip does
            Pump(window);

            Assert.True(vm.DetailsPinned, "checking the strip should pin the panel");
            Assert.True(panel.IsVisible);
            Assert.False(strip.IsVisible, "the strip yields to the open panel");

            // Pinned: the pointer being elsewhere does not hide it.
            window.MouseMove(new Point(50, 300));
            Pump(window);
            Assert.True(panel.IsVisible, "a pinned panel must survive the pointer being away");

            // Unpin (the pin button in the panel header does exactly this) → auto-hide resumes.
            vm.DetailsPinned = false;
            Pump(window);
            Assert.False(panel.IsVisible);
            Assert.True(strip.IsVisible);
            Assert.False(strip.IsChecked ?? false, "the strip mirrors the pin state");
        }
        finally { window.Close(); }
    }
}
