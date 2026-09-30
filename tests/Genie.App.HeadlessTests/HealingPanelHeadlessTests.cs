using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Health;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #263 — the Healing panel's view renders its body diagram as a real
/// grid and its buttons reach the view-model's click entry points, so a click
/// on a wounded region sends a take and nothing else does.
/// </summary>
public sealed class HealingPanelHeadlessTests
{
    private static PatientHealth Naper()
    {
        var p = new PerceiveHealthParser();
        foreach (var l in new[]
                 {
                     "Naper's injuries include...",
                     "Wounds to the LEFT ARM:",
                     "  Fresh External:  -- severe",
                     "Naper has some vitality.",
                 })
            p.Feed(l);
        return p.TakeCompleted()!;
    }

    private static (HealingPanel Panel, Window Window, HealingViewModel Vm, List<string> Sent) Host()
    {
        var sent = new List<string>();
        var vm = new HealingViewModel();
        vm.AttachForTest(sent.Add);
        var panel = new HealingPanel { DataContext = vm };
        var window = new Window { Width = 420, Height = 900, Content = panel };
        window.Show();
        Pump(window);
        return (panel, window, vm, sent);
    }

    private static void Pump(Window w)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    private static List<Button> Regions(Control panel) =>
        panel.GetVisualDescendants().OfType<Button>()
             .Where(b => b.Classes.Contains("region")).ToList();

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void The_body_diagram_lays_every_region_out_on_its_grid_cell()
    {
        var (panel, window, vm, _) = Host();
        try
        {
            var buttons = Regions(panel);
            Assert.Equal(vm.Cells.Count, buttons.Count);

            // The container carries the Grid placement from the cell's Row/Column.
            var head = buttons.Single(b => ((HealingViewModel.RegionCell)b.Tag!).RegionId == "head");
            var presenter = head.GetVisualAncestors().OfType<ContentPresenter>()
                                .First(p => p.GetVisualParent() is Grid);
            Assert.Equal(0, Grid.GetRow(presenter));
            Assert.Equal(1, Grid.GetColumn(presenter));
        }
        finally { window.Close(); }
    }

    /// <summary>A REAL pointer click (press + release over the tile), not a
    /// raised Click event: the 2026-09-30 walk found tiles that showed the
    /// reading but sent nothing when clicked.</summary>
    [AvaloniaFact]
    public void A_pointer_click_on_a_wounded_tile_sends_the_take()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            vm.Apply(Naper());
            Pump(window);

            var arm = Regions(panel).Single(b => ((HealingViewModel.RegionCell)b.Tag!).RegionId == "leftArm");
            var centre = arm.TranslatePoint(new Point(arm.Bounds.Width / 2, arm.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Pump(window);

            Assert.Equal(new[] { "take Naper left arm" }, sent);
        }
        finally { window.Close(); }
    }

    // ── DR's injuries dialog in the panel (public #263) ──────────────────────

    // The 2026-09-28 recording's openDialog and control line, plus one part
    // carrying DR's transfer cmd in the journal's shape. The part keeps its
    // healthy sprite name: a wound name needs a tinted sprite, which the
    // headless renderer can't build (no CopyPixels). The ⇄ comes from the cmd,
    // not the sprite.
    private const string RenucciDialogXml =
        "<openDialog type=\"dynamic\" id=\"injuries-10224090\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\">\n" +
        "<dialogData id=\"injuries-10224090\"><image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/><image id=\"leftLeg\" name=\"leftLeg\" cmd=\"transfer Renucci internal left leg\" tooltip=\"transfer internal left leg\" height=\"0\" width=\"0\"/></dialogData>\n" +
        "<dialogData id=\"injuries-10224090\"><skin id=\"healthSkin\" name=\"healthBar2\" controls=\"health2\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/><progressBar id=\"health2\" value=\"100\" text=\"HEALTH 100%\" customText=\"t\" align=\"n\" top=\"160\" width=\"140\" left=\"0\" height=\"15\"/><cmdButton id=\"tranblood-10224090\" value=\"Transfer Vit\" cmd=\"transfer Renucci\" align=\"n\" top=\"180\" left=\"-45\" width=\"80\" height=\"15\"/><cmdButton id=\"touch-10224090\" value=\"Re-Link\" cmd=\"touch Renucci\" anchor_left=\"tranblood-10224090\" left=\"5\" width=\"80\" height=\"15\"/></dialogData>\n";

    private sealed class Sink(Genie.Core.Dialogs.ServerDialogEngine engine) : IObserver<Genie.Core.Events.GameEvent>
    {
        public void OnNext(Genie.Core.Events.GameEvent e)
        {
            switch (e)
            {
                case Genie.Core.Events.OpenDialogEvent od: engine.Observe(od); break;
                case Genie.Core.Events.DialogDataEvent dd: engine.Observe(dd); break;
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>A dialog host fed the fixture and followed by the panel; its
    /// resolved commands land in the returned list.</summary>
    private static List<Genie.Core.Dialogs.ServerDialogAction> AttachRenucciDialog(HealingViewModel vm)
    {
        var engine = new Genie.Core.Dialogs.ServerDialogEngine();
        var parser = new Genie.Core.Parser.DrXmlParser(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Genie.Core.Parser.DrXmlParser>.Instance);
        using (parser.GameEvents.Subscribe(new Sink(engine))) parser.Feed(RenucciDialogXml);

        var host = new ServerDialogViewModel("injuries-10224090");
        var hostSent = new List<Genie.Core.Dialogs.ServerDialogAction>();
        host.ActionRequested += hostSent.Add;
        vm.AttachDialog((OtherInjuriesViewModel)host.Bespoke!);
        host.Apply(engine.Get("injuries-10224090")!);
        return hostSent;
    }

    private static PatientHealth RenucciTouch()
    {
        var p = new PerceiveHealthParser();
        foreach (var l in new[]
                 {
                     "Renucci's injuries include...",
                     "Wounds to the LEFT LEG:",
                     "  Fresh External:  light scratches -- insignificant",
                     "Renucci has normal vitality.",
                 })
            p.Feed(l);
        return p.TakeCompleted()!;
    }

    [AvaloniaFact]
    public void The_header_shows_DRs_bar_and_buttons_and_a_click_sends_through_the_host()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            var hostSent = AttachRenucciDialog(vm);
            vm.Apply(RenucciTouch());
            Pump(window);

            var header = panel.GetVisualDescendants().OfType<ItemsControl>()
                              .Single(i => i.Classes.Contains("drdialog"));
            Assert.True(header.IsEffectivelyVisible);
            Assert.Contains(header.GetVisualDescendants().OfType<ProgressBar>(), b => b.Value == 100);
            var buttons = header.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(new[] { "Transfer Vit", "Re-Link" }, buttons.Select(b => b.Content as string));

            Click(buttons[0]);
            Click(buttons[1]);

            Assert.Equal(new[] { "transfer Renucci", "touch Renucci" }, hostSent.Select(a => a.Value));
            Assert.Empty(sent);
        }
        finally { window.Close(); }
    }

    /// <summary>The ⇄ renders on the part DR marked, and a REAL right-button
    /// press and release over it sends DR's transfer (and no take).</summary>
    [AvaloniaFact]
    public void A_pointer_right_click_on_a_marked_tile_sends_DRs_transfer()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            var hostSent = AttachRenucciDialog(vm);
            vm.Apply(RenucciTouch());
            Pump(window);

            Button Tile(string id) => Regions(panel).Single(b => ((HealingViewModel.RegionCell)b.Tag!).RegionId == id);
            TextBlock Marker(Button b) => b.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("xfer"));

            var leg = Tile("leftLeg");
            Assert.True(Marker(leg).IsEffectivelyVisible);
            Assert.Equal("⇄", Marker(leg).Text);
            Assert.False(Marker(Tile("head")).IsEffectivelyVisible);

            var centre = leg.TranslatePoint(new Point(leg.Bounds.Width / 2, leg.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Right);
            window.MouseUp(centre, MouseButton.Right);
            Pump(window);

            Assert.Equal(new[] { "transfer Renucci internal left leg" }, hostSent.Select(a => a.Value));
            Assert.Empty(sent);                                   // a right-click never takes

            window.MouseDown(centre, MouseButton.Left);           // left-click still takes
            window.MouseUp(centre, MouseButton.Left);
            Pump(window);
            Assert.Equal(new[] { "take Renucci left leg" }, sent);
            Assert.Single(hostSent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Clicking_a_wounded_region_takes_it_and_a_healthy_one_sends_nothing()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            vm.Apply(Naper());
            Pump(window);
            Assert.Empty(sent);   // a reading arriving never sends

            var buttons = Regions(panel);
            Click(buttons.Single(b => ((HealingViewModel.RegionCell)b.Tag!).RegionId == "head"));
            Assert.Empty(sent);

            Click(buttons.Single(b => ((HealingViewModel.RegionCell)b.Tag!).RegionId == "leftArm"));
            Assert.Equal(new[] { "take Naper left arm" }, sent);
        }
        finally { window.Close(); }
    }
}
