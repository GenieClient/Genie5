using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
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
