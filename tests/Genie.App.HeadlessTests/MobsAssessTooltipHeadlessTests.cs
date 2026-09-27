using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Genie.Core.Models;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #351 — hover tooltips on the Mobs panel's structured assess rows.
///
/// <para>The report: no tip fires anywhere on a row, not even the creature-name
/// Button's literal "Face this creature". These tests drive the REAL tooltip
/// path — pointer move, Avalonia's ToolTipService, its default 400 ms show
/// timer — over the production MobsTool DataTemplate hosted in the production
/// dock layout. Every tip on the row opens here: the name Button's literal tip,
/// the row Border's raw-line tip under the detail line, and the panel's gear
/// tip, docked in a tool frame and in the Game group. So the XAML, the template
/// nesting, hit-testing, the ItemsControl / ScrollViewer hosting and the Dock
/// content hosts are all cleared; the cause of the live report lies outside
/// what headless can render. What this suite guarantees is that no future
/// template or style change can silently remove a row's tooltip.</para>
///
/// <para>Harness notes: the show delay runs on a DispatcherTimer, which only
/// ticks inside a dispatcher main loop — <c>RunJobs</c> alone never fires it,
/// so <see cref="Harness.Wait"/> spins short main-loop slices. Headless text has
/// no glyph geometry, so a TextBlock without a background is not hit-testable;
/// the pointer lands on its nearest hit-testable ancestor, which resolves to the
/// same tip host either way.</para>
/// </summary>
public class MobsAssessTooltipHeadlessTests
{
    private static Genie.App.App? _app;

    /// <summary>App.axaml's DataTemplates + palette, as the other panel tests
    /// load them.</summary>
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

    private const string RawLine = "A sleazy lout (1: solidly balanced) is facing you at melee range.";
    private const string Detail  = "solidly balanced · facing you · melee range";

    private static AssessRowItem Row(MobsViewModel vm, string raw = RawLine) =>
        new(new AssessRow(1, "123", "a sleazy lout", "solidly balanced", "facing you", "melee range",
                          "look #123", "face #123", raw),
            null, vm);

    /// <summary>Show the rows the way RefreshAssess does, without a live core.</summary>
    private static void ShowAssess(MobsViewModel vm, params AssessRowItem[] rows)
    {
        vm.AssessRows.Clear();
        foreach (var r in rows) vm.AssessRows.Add(r);
        typeof(MobsViewModel).GetProperty(nameof(MobsViewModel.HasAssess))!.SetValue(vm, rows.Length > 0);
    }

    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm      { get; }
        public GenieDockFactory    Factory { get; }
        public Window              Main    { get; }
        private readonly string    _dir;

        public Harness()
        {
            EnsureProductionTemplates();
            _dir = Path.Combine(Path.GetTempPath(), "genie_mobstip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm      = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
            Factory = (GenieDockFactory)Vm.DockFactory!;
            Main    = new Window
            {
                Width = 1400, Height = 900,
                Content = new DockControl { Layout = Vm.DockLayout, Factory = Factory, InitializeLayout = false },
            };
            Main.Show();
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Let real time pass so the ToolTipService show timer can fire.</summary>
        public void Wait(int ms = 900)
        {
            for (var left = ms; left > 0; left -= 100)
            {
                using var cts = new CancellationTokenSource(100);
                Dispatcher.UIThread.MainLoop(cts.Token);
                Pump();
            }
        }

        public void Hover(Control c)
        {
            var p = c.TranslatePoint(new Point(Math.Min(5, c.Bounds.Width / 2), c.Bounds.Height / 2), Main);
            Assert.True(p.HasValue, $"{c.GetType().Name} is not in the main window's tree");
            Main.MouseMove(p!.Value);
            Pump();
        }

        public Button NameButton() =>
            Main.GetVisualDescendants().OfType<Button>()
                .First(b => ToolTip.GetTip(b) as string == "Face this creature");

        public Border RowBorder(string raw = RawLine) =>
            Main.GetVisualDescendants().OfType<Border>()
                .First(b => ToolTip.GetTip(b) as string == raw);

        public TextBlock DetailLine(string raw = RawLine) =>
            RowBorder(raw).GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == Detail);

        public void Dispose()
        {
            try { Main.Close(); } catch { }
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    private static Harness Docked()
    {
        var h = new Harness();
        ShowAssess(h.Vm.Mobs, Row(h.Vm.Mobs));
        h.Factory.SetToolVisibility("mobs", true);
        h.Pump();
        h.Pump();
        return h;
    }

    [AvaloniaFact]
    public void Every_row_part_carries_a_tip()
    {
        using var h = Docked();
        Assert.Equal("Face this creature", ToolTip.GetTip(h.NameButton()));
        Assert.Equal(RawLine, ToolTip.GetTip(h.RowBorder()));
        // The Button sits inside the Border: hovering the name finds the
        // Button's tip first, and the rest of the row finds the raw line.
        Assert.Contains(h.RowBorder(), h.NameButton().GetVisualAncestors());
    }

    [AvaloniaFact]
    public void Hovering_the_creature_name_opens_Face_this_creature()
    {
        using var h = Docked();
        var button = h.NameButton();
        Assert.True(button.IsEffectivelyEnabled, "a disabled Button would skip its own tip");

        h.Hover(button);
        Assert.True(button.IsPointerOver);
        h.Wait();

        Assert.True(ToolTip.GetIsOpen(button), "the name Button's tooltip did not open");
    }

    [AvaloniaFact]
    public void Hovering_the_detail_line_opens_the_raw_server_line()
    {
        using var h = Docked();
        h.Hover(h.DetailLine());
        h.Wait();

        Assert.True(ToolTip.GetIsOpen(h.RowBorder()), "the row's raw-line tooltip did not open");
    }

    [AvaloniaFact]
    public void The_gear_button_tip_opens_too()
    {
        using var h = Docked();
        var gear = h.Main.GetVisualDescendants().OfType<ToggleButton>()
            .First(b => (ToolTip.GetTip(b) as string)?.StartsWith("Edit the ignore list") == true);

        h.Hover(gear);
        h.Wait();

        Assert.True(ToolTip.GetIsOpen(gear));
    }

    [AvaloniaFact]
    public void A_rebuild_under_a_resting_pointer_still_shows_the_new_rows_tip()
    {
        // RefreshAssess clears and re-adds every row on each assess line and
        // crtrStatus update, so the control under the pointer is replaced while
        // the pointer rests on it.
        using var h = Docked();
        h.Hover(h.DetailLine());
        h.Wait();

        ShowAssess(h.Vm.Mobs, Row(h.Vm.Mobs, "second reading"));
        h.Pump();
        h.Wait();

        Assert.True(ToolTip.GetIsOpen(h.RowBorder("second reading")),
            "the rebuilt row's tooltip never opened under the stationary pointer");
    }

    [AvaloniaFact]
    public void The_tip_also_opens_with_the_panel_in_the_Game_group()
    {
        using var h = new Harness();
        ShowAssess(h.Vm.Mobs, Row(h.Vm.Mobs));
        var docs = (IDock)FindIn((IDockable)h.Vm.DockLayout!, "docs")!;
        var mobs = new MobsTool(h.Vm.Mobs, h.Vm.WindowSettings.Get("mobs")) { Id = "mobs-in-docs", Title = "Mobs" };
        mobs.Owner = docs;
        docs.VisibleDockables!.Add(mobs);
        docs.ActiveDockable = mobs;
        h.Pump();
        h.Pump();

        var button = h.NameButton();
        h.Hover(button);
        h.Wait();

        Assert.True(ToolTip.GetIsOpen(button));
    }

    private static IDockable? FindIn(IDockable n, string id)
    {
        if (n.Id == id) return n;
        if (n is IDock d && d.VisibleDockables is not null)
            foreach (var c in d.VisibleDockables)
                if (FindIn(c, id) is { } f) return f;
        return null;
    }
}
