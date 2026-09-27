using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Docking;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #365 — turning "Always show scrollbars" OFF must give scrollbars inside
/// the DOCK their auto-hide back, live.
///
/// <para>Walked 2026-09-26: after <c>[display] always show scrollbars off</c> the
/// Game window's scrollbar (and Active Spells') stayed full width through pointer
/// and wheel activity, until a restart. The first cut switched the setting by
/// adding and removing an Application style; adding reached every control, but
/// removing did not reach content hosted in the dock, so the Game window's
/// ScrollViewer kept <c>AllowAutoHide = false</c>. The existing tests used bare
/// windows, where removal does propagate, which is why they stayed green. These
/// build the production dock layout (App.axaml templates + the real factory) and
/// pin the fix: the style stays installed and a resource carries the value.</para>
/// </summary>
public class DockedScrollbarAutoHideHeadlessTests : IDisposable
{
    private static Genie.App.App? _app;
    private readonly string _dir;
    private Window? _main;

    public DockedScrollbarAutoHideHeadlessTests()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "genie_dockscroll_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ScrollBarAutoHide.SetAlwaysVisible(false);
        try { _main?.Close(); } catch { }
        try { System.IO.Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Production App.axaml DataTemplates + palette on the current
    /// application (the runner hands each test a fresh one), as
    /// MapperDetailsFlyoutHeadlessTests does.</summary>
    private static void EnsureProductionTemplates()
    {
        _app ??= Build();
        static Genie.App.App Build() { var a = new Genie.App.App(); a.Initialize(); return a; }
        var cur = Application.Current!;
        if (!cur.DataTemplates.OfType<Avalonia.Markup.Xaml.Templates.DataTemplate>().Any(t => t.DataType == typeof(GameTextDocument)))
            foreach (var t in _app.DataTemplates) cur.DataTemplates.Add(t);
        if (!cur.Resources.ContainsKey("Theme.PanelBg"))
            cur.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Genie.App"))
            { Source = new Uri("avares://Genie5/Themes/ThemePalette.axaml") });
    }

    private void Pump()
    {
        for (int i = 0; i < 6; i++) { _main!.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    }

    /// <summary>The Game window's scrollbar, docked, with enough text to scroll.</summary>
    private ScrollBar GameWindowBar()
    {
        EnsureProductionTemplates();
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        for (int i = 0; i < 300; i++) vm.GameText.AddSystemLine("line " + i);
        var factory = (GenieDockFactory)vm.DockFactory!;
        _main = new Window
        {
            Width = 1200, Height = 800,
            Content = new Dock.Avalonia.Controls.DockControl { Layout = vm.DockLayout, Factory = factory, InitializeLayout = false },
        };
        _main.Show();
        Pump();
        return _main.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(b => b.IsVisible && b.FindAncestorOfType<ContentControl>()?.DataContext is GameTextDocument);
    }

    [AvaloniaFact]
    public void Turning_it_off_gives_the_docked_game_window_its_auto_hide_back()
    {
        var bar = GameWindowBar();
        Assert.True(bar.AllowAutoHide);                    // stock

        ScrollBarAutoHide.SetAlwaysVisible(true);  Pump();
        Assert.False(bar.AllowAutoHide);
        Assert.True(bar.IsExpanded);                       // full width

        ScrollBarAutoHide.SetAlwaysVisible(false); Pump();
        Assert.True(bar.AllowAutoHide);                    // the walk's failure: stayed False
        Assert.False(bar.IsExpanded);                      // thin again, no restart
    }

    [AvaloniaFact]
    public void It_survives_repeated_toggling()
    {
        var bar = GameWindowBar();
        foreach (var on in new[] { true, false, true, false, true, false })
        {
            ScrollBarAutoHide.SetAlwaysVisible(on); Pump();
            Assert.Equal(!on, bar.AllowAutoHide);
        }
    }

    [AvaloniaFact]
    public void Nothing_is_installed_until_someone_turns_it_on()
    {
        EnsureProductionTemplates();
        ScrollBarAutoHide.SetAlwaysVisible(false);
        Assert.False(ScrollBarAutoHide.IsInstalled);

        ScrollBarAutoHide.SetAlwaysVisible(true);
        Assert.True(ScrollBarAutoHide.IsInstalled);
        ScrollBarAutoHide.SetAlwaysVisible(false);
        Assert.True(ScrollBarAutoHide.IsInstalled);        // stays; the resource carries "off"
        Assert.Equal(true, Application.Current!.Resources[ScrollBarAutoHide.ResourceKey]);
    }
}
