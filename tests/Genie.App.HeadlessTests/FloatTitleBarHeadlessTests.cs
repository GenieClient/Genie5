using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #181 "Hide Title Bar" — end-to-end over the PRODUCTION float path: the real
/// <see cref="GenieDockFactory"/> layout hosted in a real DockControl, a tool
/// floated exactly as the Window-menu "Float" does, then
/// <see cref="GenieDockFactory.ToggleFloatTitleBar"/> — the same call the
/// "Hide Title Bar" menu item makes. Materializes Dock's real ToolChromeControl
/// template (themes loaded by <see cref="HeadlessApp"/>) under the shipping
/// Avalonia version, so a Dock/Avalonia bump that turns the collapse into a
/// silent no-op fails here instead of in a live session.
/// <para>Also regression-guards the title-bar flyout item (the beta.3
/// "add to both" follow-up): its floating-only gate must bind through the
/// LOGICAL tree — the visual-tree FindAncestor it shipped with never matched
/// from inside the popup, so the item showed on docked chromes where its click
/// (then resolved via "whichever float IsActive") silently did nothing.</para>
/// </summary>
public class FloatTitleBarHeadlessTests
{
    private sealed class FloatHarness : IDisposable
    {
        public GenieDockFactory  Factory { get; }
        public Window            Main    { get; }
        public GenieHostWindow   Float   { get; }
        public ToolChromeControl Chrome  { get; }
        public const string      ToolId = "thoughts";

        private readonly string _dir;

        public FloatHarness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)vm.DockFactory!;

            Main = new Window
            {
                Width = 1200, Height = 800,
                Content = new DockControl
                {
                    Layout = vm.DockLayout,
                    Factory = Factory,
                    InitializeLayout = false,
                },
            };
            Main.Show();
            Pump(Main);

            Factory.FloatTool(ToolId);
            Pump(Main);

            var root = (IRootDock)vm.DockLayout!;
            var host = root.Windows!
                .Select(w => w.Host)
                .OfType<GenieHostWindow>()
                .FirstOrDefault();
            Assert.True(host is not null, "floating the tool did not create a GenieHostWindow");
            Float = host!;
            // Two pumps: the chrome materializes on a layout pass after Opened,
            // and the flyout-item injection retries on a later dispatcher tick.
            Pump(Float);
            Pump(Float);

            var chrome = Float.GetVisualDescendants().OfType<ToolChromeControl>().FirstOrDefault();
            Assert.True(chrome is not null, "no ToolChromeControl materialized in the float's visual tree");
            Chrome = chrome!;
        }

        public static void Pump(Window w)
        {
            w.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
        }

        /// <summary>A DIRECT child of the chrome's template-root Grid — the same
        /// scope <see cref="GenieHostWindow.SetTitleBarHidden"/> collapses. (A
        /// descendant search is wrong here: the ToolControl skin nests another
        /// Border named PART_Border inside the content presenter.)</summary>
        public Control? ChromePart(string name) =>
            Chrome.GetVisualChildren().OfType<Grid>().FirstOrDefault()
                  ?.GetVisualChildren().OfType<Control>()
                  .FirstOrDefault(c => c.Name == name);

        public MenuFlyout Flyout => Assert.IsAssignableFrom<MenuFlyout>(Chrome.ToolFlyout);

        public MenuItem? HideItem =>
            Flyout.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == "GenieHideTitleBarItem");

        public void Dispose()
        {
            try { Float.Close(); } catch { /* teardown */ }
            try { Main.Close(); } catch { /* teardown */ }
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [AvaloniaFact]
    public void Chrome_template_has_the_parts_the_collapse_targets()
    {
        using var h = new FloatHarness();

        var root = h.Chrome.GetVisualChildren().OfType<Grid>().FirstOrDefault();
        Assert.NotNull(root);

        var direct = root!.GetVisualChildren().OfType<Control>().ToList();
        Assert.Contains(direct, c => c.Name == "PART_Border");
        Assert.Contains(direct, c => c.Name == "PART_Panel");
        Assert.Contains(direct, c => c.Name == "PART_ContentPresenter");
    }

    [AvaloniaFact]
    public void Toggle_hides_header_and_divider_but_keeps_content()
    {
        using var h = new FloatHarness();

        Assert.True(h.ChromePart("PART_Border")!.IsVisible);
        Assert.False(h.Factory.IsFloatTitleBarHidden(FloatHarness.ToolId));

        h.Factory.ToggleFloatTitleBar(FloatHarness.ToolId);
        FloatHarness.Pump(h.Float);

        Assert.True(h.Factory.IsFloatTitleBarHidden(FloatHarness.ToolId));
        Assert.True(h.Float.IsTitleBarHidden);
        Assert.False(h.ChromePart("PART_Border")!.IsVisible);
        Assert.False(h.ChromePart("PART_Panel")!.IsVisible);
        // The regression the beta.3 follow-up fixed: hiding the whole chrome
        // blanked the panel content too. Content must survive the collapse.
        Assert.True(h.ChromePart("PART_ContentPresenter")!.IsVisible);
    }

    [AvaloniaFact]
    public void Second_toggle_restores_the_header()
    {
        using var h = new FloatHarness();

        h.Factory.ToggleFloatTitleBar(FloatHarness.ToolId);
        FloatHarness.Pump(h.Float);
        h.Factory.ToggleFloatTitleBar(FloatHarness.ToolId);
        FloatHarness.Pump(h.Float);

        Assert.False(h.Factory.IsFloatTitleBarHidden(FloatHarness.ToolId));
        Assert.True(h.ChromePart("PART_Border")!.IsVisible);
        Assert.True(h.ChromePart("PART_Panel")!.IsVisible);
    }

    [AvaloniaFact]
    public void Chrome_flyout_gets_the_hide_item_injected()
    {
        using var h = new FloatHarness();
        Assert.NotNull(h.HideItem);
    }

    [AvaloniaFact]
    public void Flyout_item_shows_on_a_floating_chrome_and_hides_on_a_docked_one()
    {
        using var h = new FloatHarness();
        var flyout = h.Flyout;
        var item = h.HideItem!;

        // The gate reads IsFloating off the chrome. Dock only sets it on
        // Windows/macOS (its Linux AttachToWindow branch skips it), so
        // GenieHostWindow asserts it — pin that here, or the Linux CI leg
        // fails the visibility check below with no hint at the cause.
        Assert.True(h.Chrome.IsFloating);

        // Opened over the FLOAT's chrome (right-clicking its title bar).
        flyout.ShowAt(h.Chrome);
        FloatHarness.Pump(h.Float);
        Assert.True(item.IsVisible);
        flyout.Hide();
        FloatHarness.Pump(h.Float);

        // The same shared flyout opened over a DOCKED chrome in the main window:
        // the item must hide — there is no float title bar to act on there, and
        // the shipped visual-tree gate left it visible-but-dead (public #181
        // "isn't working").
        var docked = h.Main.GetVisualDescendants().OfType<ToolChromeControl>().First();
        flyout.ShowAt(docked);
        FloatHarness.Pump(h.Main);
        Assert.False(item.IsVisible);
        flyout.Hide();
        FloatHarness.Pump(h.Main);
    }

    [AvaloniaFact]
    public void Flyout_item_click_hides_the_bar_of_the_window_it_was_opened_over()
    {
        using var h = new FloatHarness();
        var flyout = h.Flyout;
        var item = h.HideItem!;

        // Open over the float's chrome, then click — the handler must resolve
        // the float from the flyout's placement target (the IsActive scan it
        // shipped with found nothing whenever no float held OS activation).
        flyout.ShowAt(h.Chrome);
        FloatHarness.Pump(h.Float);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        flyout.Hide();
        FloatHarness.Pump(h.Float);

        Assert.True(h.Float.IsTitleBarHidden);
        Assert.False(h.ChromePart("PART_Border")!.IsVisible);
        Assert.True(h.ChromePart("PART_ContentPresenter")!.IsVisible);
    }
}
