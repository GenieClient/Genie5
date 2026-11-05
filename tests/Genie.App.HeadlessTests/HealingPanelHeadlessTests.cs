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
/// Public #263 — the Healing panel's view draws its body figures (four, or one
/// overlay) and its clicks reach the view-model's entry points, so a click on
/// a wounded part sends a take and nothing else does.
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
        var window = new Window { Width = 480, Height = 1800, Content = panel };
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

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>The drawn figure for an axis (four-figure layout) or, with
    /// null, the overlay figure.</summary>
    private static Panel FigureFor(Control panel, InjuryAxis? axis) =>
        panel.GetVisualDescendants().OfType<Panel>()
             .Single(p => p.Tag is HealingViewModel.BodyFigure f && f.Axis == axis);

    /// <summary>A part's centre, in window coordinates, through the figure's
    /// Viewbox scaling.</summary>
    private static Point PartCentre(Window window, Panel figure, string region)
    {
        var fig  = (HealingViewModel.BodyFigure)figure.Tag!;
        var part = fig.Parts.Single(p => p.RegionId == region);
        var local = new Point(part.Left + part.Width / 2, part.Top + part.Height / 2);
        return figure.TranslatePoint(local, window)!.Value;
    }

    private static void Press(Window w, Point at, MouseButton button)
    {
        w.MouseDown(at, button);
        w.MouseUp(at, button);
        Pump(w);
    }

    [AvaloniaFact]
    public void Four_figures_show_by_default_with_every_part_placed_on_the_canvas()
    {
        var (panel, window, vm, _) = Host();
        try
        {
            foreach (var axis in new[] { InjuryAxis.FreshExternal, InjuryAxis.FreshInternal,
                                         InjuryAxis.ScarExternal, InjuryAxis.ScarInternal })
                Assert.True(FigureFor(panel, axis).IsEffectivelyVisible);

            // Each part's container carries its Canvas placement.
            var figure = FigureFor(panel, InjuryAxis.FreshExternal);
            var presenters = figure.GetVisualDescendants().OfType<ContentPresenter>()
                                   .Where(p => p.DataContext is HealingViewModel.FigurePart).ToList();
            Assert.Equal(vm.PanelFigures[0].Parts.Count, presenters.Count);
            var head = presenters.Single(p => ((HealingViewModel.FigurePart)p.DataContext!).RegionId == "head");
            Assert.Equal(242, Canvas.GetLeft(head));
            Assert.Equal(48,  Canvas.GetTop(head));

            vm.OverlayLayout = true;
            Pump(window);
            Assert.True(FigureFor(panel, null).IsEffectivelyVisible);
            Assert.False(FigureFor(panel, InjuryAxis.FreshExternal).IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    /// <summary>A REAL pointer click (press + release over the part), not a
    /// raised event: the 2026-09-30 walk found tiles that showed the reading
    /// but sent nothing when clicked.</summary>
    [AvaloniaFact]
    public void A_pointer_click_on_a_wounded_part_sends_the_take()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            vm.Apply(Naper());
            Pump(window);

            Press(window, PartCentre(window, FigureFor(panel, InjuryAxis.FreshExternal), "leftArm"), MouseButton.Left);
            Assert.Equal(new[] { "take Naper left arm" }, sent);

            // The same part on a figure where it is healthy sends nothing.
            Press(window, PartCentre(window, FigureFor(panel, InjuryAxis.ScarExternal), "leftArm"), MouseButton.Left);
            Assert.Single(sent);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void An_overlay_click_opens_the_card_and_its_take_button_sends()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            vm.Apply(Naper());
            vm.OverlayLayout = true;
            Pump(window);

            Press(window, PartCentre(window, FigureFor(panel, null), "leftArm"), MouseButton.Left);
            Assert.Empty(sent);
            Assert.True(vm.HasSelectedPart);

            var take = panel.GetVisualDescendants().OfType<Button>()
                            .Single(b => b.Content as string == "Take" && b.IsEffectivelyVisible);
            Click(take);
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

    /// <summary>A REAL right-button press and release over the part DR marked
    /// sends DR's transfer (and no take); its tooltip names it.</summary>
    [AvaloniaFact]
    public void A_pointer_right_click_on_a_marked_part_sends_DRs_transfer()
    {
        var (panel, window, vm, sent) = Host();
        try
        {
            var hostSent = AttachRenucciDialog(vm);
            vm.Apply(RenucciTouch());
            Pump(window);

            var figure = FigureFor(panel, InjuryAxis.FreshExternal);
            var leg = vm.PanelFigures[0].Parts.Single(p => p.RegionId == "leftLeg");
            Assert.Contains("right-click: transfer internal left leg", leg.Tip);   // DR's own tooltip text

            var centre = PartCentre(window, figure, "leftLeg");
            Press(window, centre, MouseButton.Right);
            Assert.Equal(new[] { "transfer Renucci internal left leg" }, hostSent.Select(a => a.Value));
            Assert.Empty(sent);                                   // a right-click never takes

            Press(window, centre, MouseButton.Left);              // left-click still takes
            Assert.Equal(new[] { "take Renucci left leg" }, sent);
            Assert.Single(hostSent);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Live 2026-10-01: a right-click on a marked tile opened the panel's own
    /// window menu (Flash on Activity / Float / Close Window) and sent nothing.
    /// The dock hangs that ContextMenu on an ancestor of the panel, so the
    /// tile has to claim the context request itself. Reproduced here by
    /// wrapping the panel in a parent that owns a real ContextMenu.
    /// </summary>
    [AvaloniaFact]
    public void A_right_click_on_a_figure_beats_the_window_menu()
    {
        var sent = new List<string>();
        var vm = new HealingViewModel();
        vm.AttachForTest(sent.Add);
        var panel = new HealingPanel { DataContext = vm };
        var menu = new ContextMenu { ItemsSource = new[] { "Flash on Activity", "Float", "Close Window" } };
        var owner = new Border { Child = panel, ContextMenu = menu };
        var window = new Window { Width = 480, Height = 1800, Content = owner };
        window.Show();
        Pump(window);
        try
        {
            var hostSent = AttachRenucciDialog(vm);
            vm.Apply(RenucciTouch());
            Pump(window);

            var centre = PartCentre(window, FigureFor(panel, InjuryAxis.FreshExternal), "leftLeg");
            Press(window, centre, MouseButton.Right);

            Assert.False(menu.IsOpen, "the window menu opened over a figure");
            Assert.Equal(new[] { "transfer Renucci internal left leg" }, hostSent.Select(a => a.Value));
            Assert.Empty(sent);

            // Off the figures, the window menu still opens as before.
            window.MouseDown(new Point(4, 4), MouseButton.Right);
            window.MouseUp(new Point(4, 4), MouseButton.Right);
            Pump(window);
            Assert.True(menu.IsOpen, "the window menu must still open off the figures");
            menu.Close();
        }
        finally { window.Close(); }
    }
}
