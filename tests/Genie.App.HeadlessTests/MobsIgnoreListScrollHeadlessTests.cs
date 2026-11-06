using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// The Mobs panel's ignore-list editor: a long pattern list must scroll
/// inside the editor instead of growing it. The editor sits in the panel's
/// Auto row, so before the bound a dozen patterns pushed the Add box and
/// Restore defaults below the window with no way to reach them.
/// </summary>
public class MobsIgnoreListScrollHeadlessTests
{
    private static Genie.App.App? _app;

    private static void EnsureProductionTemplates()
    {
        _app ??= Build();
        static Genie.App.App Build() { var a = new Genie.App.App(); a.Initialize(); return a; }
        var cur = Application.Current!;
        if (!cur.DataTemplates.OfType<Avalonia.Markup.Xaml.Templates.DataTemplate>().Any(t => t.DataType == typeof(MobsTool)))
            foreach (var t in _app.DataTemplates) cur.DataTemplates.Add(t);
        if (!cur.Resources.ContainsKey("Theme.PanelBg"))
            cur.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Genie.App"))
            {
                Source = new Uri("avares://Genie5/Themes/ThemePalette.axaml")
            });
    }

    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm   { get; }
        public Window              Main { get; }
        private readonly string    _dir;

        public Harness()
        {
            EnsureProductionTemplates();
            _dir = Path.Combine(Path.GetTempPath(), "genie_mobsignore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm   = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            var factory = (GenieDockFactory)Vm.DockFactory!;
            Main = new Window
            {
                Width = 1400, Height = 600,
                Content = new DockControl { Layout = Vm.DockLayout, Factory = factory, InitializeLayout = false },
            };
            Main.Show();
            factory.SetToolVisibility("mobs", true);
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public ScrollViewer ListScroll() =>
            Main.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "IgnoreListScroll");

        public Button RestoreDefaults() =>
            Main.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Restore defaults");

        public void Dispose()
        {
            try { Main.Close(); } catch { }
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private static void OpenEditorWith(Harness h, int patterns)
    {
        var vm = h.Vm.Mobs;
        vm.IgnorePatterns.Clear();
        for (var i = 0; i < patterns; i++)
            vm.IgnorePatterns.Add(new IgnorePatternItem($"creature {i}", vm));
        vm.IsEditingIgnores = true;
        h.Pump();
        h.Pump();
    }

    [AvaloniaFact]
    public void A_long_ignore_list_scrolls_inside_the_editor()
    {
        using var h = new Harness();
        OpenEditorWith(h, 60);

        var scroll = h.ListScroll();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1,
            $"60 patterns should overflow the list: extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
        Assert.True(scroll.Bounds.Height <= 180.5, $"the list grew to {scroll.Bounds.Height}px instead of scrolling");
    }

    [AvaloniaFact]
    public void Restore_defaults_stays_inside_the_window_with_a_long_list()
    {
        using var h = new Harness();
        OpenEditorWith(h, 60);

        var restore = h.RestoreDefaults();
        var bottom  = restore.TranslatePoint(new Point(0, restore.Bounds.Height), h.Main);
        Assert.True(bottom.HasValue, "Restore defaults is not in the window's tree");
        Assert.True(bottom!.Value.Y <= h.Main.Bounds.Height,
            $"Restore defaults ends at y={bottom.Value.Y} but the window is only {h.Main.Bounds.Height}px tall");
    }

    [AvaloniaFact]
    public void A_short_ignore_list_does_not_scroll()
    {
        using var h = new Harness();
        OpenEditorWith(h, 3);

        var scroll = h.ListScroll();
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
            $"3 patterns should fit: extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
    }
}
