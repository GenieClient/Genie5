using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Genie.App.Docking;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #302 / #320 — the docked panel banner toggle, over the production layout.
///
/// <para>The banner is Dock's <see cref="ToolChromeControl"/> header: the accent bar
/// above the content showing the active panel's name. It duplicates the tab strip
/// directly below it, so on a short stacked panel the two chrome bands can outweigh
/// the text between them.</para>
///
/// <para>The collapse is a local <c>IsVisible</c> assignment on a template part the
/// app does not own, reached through a style that marks each chrome as it appears
/// (see <see cref="Genie.App.Docking.BannerChrome"/>). Every link in that chain is
/// silent when it breaks: a Dock or Avalonia bump renaming the part, a selector
/// matching nothing, or the marker style not being loaded all leave the menu item
/// doing nothing with no error anywhere. These tests materialize the real template
/// under the shipping versions so that fails here instead.</para>
///
/// <para>That last case is not hypothetical — <c>HeadlessApp</c> built its test
/// Application from the theme stack alone and loaded none of the app's own styles,
/// so the first version of these tests failed for a reason that had nothing to do
/// with the code under test. It now loads the same rule the app does.</para>
/// </summary>
public class WindowBannerHeadlessTests
{
    private sealed class DockedHarness : IDisposable
    {
        public MainWindowViewModel Vm   { get; }
        public Window              Main { get; }
        private readonly string _dir;

        public DockedHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_banner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);

            Main = new Window
            {
                Width = 1200, Height = 800,
                Content = new DockControl
                {
                    Layout = Vm.DockLayout,
                    Factory = (GenieDockFactory)Vm.DockFactory!,
                    InitializeLayout = false,
                },
            };
            Main.Show();
            Pump();
            Pump();   // the chrome materializes on a later layout pass
        }

        /// <summary>Every docked chrome in the main window (one per tool group).</summary>
        public ToolChromeControl[] Chromes =>
            Main.GetVisualDescendants().OfType<ToolChromeControl>().ToArray();

        /// <summary>The header bands the style targets, one per docked chrome.
        ///
        /// <para>Scoped to DIRECT children of each chrome's template-root Grid, the
        /// way GenieHostWindow does it. A blanket descendant search is wrong here:
        /// the app's own ToolControl skin also names a Border "PART_Border", but
        /// that one sits inside the content presenter and the style's
        /// <c>/template/</c> qualifier deliberately does not reach it.</para></summary>
        public Control[] HeaderBands => Chromes
            .Select(c => c.GetVisualChildren().OfType<Grid>().FirstOrDefault())
            .Where(g => g is not null)
            .SelectMany(g => g!.GetVisualChildren().OfType<Control>())
            .Where(c => c.Name == "PART_Border")
            .ToArray();

        /// <summary>The divider beside the header band. Ships hidden when docked and
        /// keeps its own state logic — the toggle must leave it alone rather than
        /// force it visible.</summary>
        public Control[] Dividers => Chromes
            .Select(c => c.GetVisualChildren().OfType<Grid>().FirstOrDefault())
            .Where(g => g is not null)
            .SelectMany(g => g!.GetVisualChildren().OfType<Control>())
            .Where(c => c.Name == "PART_Panel")
            .ToArray();

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.Measure(new Size(Main.Width, Main.Height));
            Main.Arrange(new Rect(0, 0, Main.Width, Main.Height));
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            try { Main.Close(); } catch { }
            try { Directory.Delete(_dir, true); } catch { }
        }
    }

    [AvaloniaFact]
    public void The_production_layout_materializes_docked_chrome_headers()
    {
        // Guards the premise of every other test here: if Dock renames these parts
        // the selectors stop matching and the toggle silently does nothing.
        using var h = new DockedHarness();

        Assert.NotEmpty(h.Chromes);
        Assert.NotEmpty(h.HeaderBands);
    }

    [AvaloniaFact]
    public void Banners_are_visible_by_default()
    {
        using var h = new DockedHarness();

        Assert.True(h.Vm.Display.ShowWindowBanners);
        Assert.All(h.HeaderBands, p => Assert.True(p.IsVisible));
    }

    [AvaloniaFact]
    public void Turning_the_setting_off_collapses_every_docked_banner()
    {
        using var h = new DockedHarness();
        var before = h.HeaderBands.Length;
        Assert.True(before > 0);

        h.Vm.Display.ShowWindowBanners = false;
        h.Pump();

        Assert.All(h.HeaderBands, p => Assert.False(p.IsVisible));
        Assert.Equal(before, h.HeaderBands.Length);   // collapsed, not removed
    }

    [AvaloniaFact]
    public void The_toggle_is_reversible()
    {
        using var h = new DockedHarness();

        h.Vm.Display.ShowWindowBanners = false;
        h.Pump();
        Assert.All(h.HeaderBands, p => Assert.False(p.IsVisible));

        h.Vm.Display.ShowWindowBanners = true;
        h.Pump();
        Assert.All(h.HeaderBands, p => Assert.True(p.IsVisible));
    }

    [AvaloniaFact]
    public void The_menu_command_flips_the_setting_and_persists_it()
    {
        using var h = new DockedHarness();

        h.Vm.ToggleWindowBannersCommand.Execute().Subscribe();
        h.Pump();

        Assert.False(h.Vm.Display.ShowWindowBanners);
        Assert.All(h.HeaderBands, p => Assert.False(p.IsVisible));
    }

    [AvaloniaFact]
    public void Hiding_the_banner_leaves_the_tab_strip_naming_the_panel()
    {
        // The whole reason the banner is safe to remove: the tab strip is bound to
        // the group's dockables with no single-tab special case, so every panel
        // keeps a visible tab carrying its name. Without this the reporter's
        // request would trade chrome for an anonymous panel.
        using var h = new DockedHarness();

        h.Vm.Display.ShowWindowBanners = false;
        h.Pump();

        var strips = h.Main.GetVisualDescendants().OfType<ToolTabStrip>().ToArray();
        Assert.NotEmpty(strips);
        Assert.All(strips, s => Assert.True(s.IsVisible));
        Assert.Contains(strips, s => s.ItemCount > 0);
    }

    [AvaloniaFact]
    public void The_divider_beside_the_band_is_left_alone()
    {
        // Binding the divider to the same value would force it VISIBLE whenever
        // banners are on, inventing a separator the theme deliberately hides.
        using var h = new DockedHarness();
        var before = h.Dividers.Select(d => d.IsVisible).ToArray();

        h.Vm.Display.ShowWindowBanners = false;
        h.Pump();
        h.Vm.Display.ShowWindowBanners = true;
        h.Pump();

        Assert.Equal(before, h.Dividers.Select(d => d.IsVisible).ToArray());
    }

    [AvaloniaFact]
    public void The_setting_round_trips_through_display_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_banner_io_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "display.json");
            var settings = new DisplaySettings { ShowWindowBanners = false };
            settings.Save(path);

            var reloaded = DisplaySettings.Load(path);

            Assert.False(reloaded.ShowWindowBanners);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
