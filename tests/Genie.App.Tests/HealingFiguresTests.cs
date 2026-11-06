using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Health;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The Healing panel's body figures: the four-figure and overlay layouts, the
/// layout setting, and what a click on a figure sends. Readings come from the
/// perceive-health parser, as in <see cref="HealingViewModelTests"/>. These
/// run without the asset pipeline, so a part's hit area is its whole box.
/// </summary>
public class HealingFiguresTests
{
    private static PatientHealth Read(params string[] lines)
    {
        var p = new PerceiveHealthParser(() => new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        foreach (var l in lines) p.Feed(l);
        return p.TakeCompleted() ?? throw new InvalidOperationException("block did not complete");
    }

    /// <summary>Left arm: fresh outside 10 and an inside scar 3. Head: fresh inside 5.</summary>
    private static PatientHealth Renucci() => Read(
        "Renucci's injuries include...",
        "Wounds to the LEFT ARM:",
        "  Fresh External:  a deep gash -- very severe",
        "  Scars Internal:  old scarring -- minor",
        "Wounds to the HEAD:",
        "  Fresh Internal:  bruising -- harmful",
        "Renucci has little vitality (60%).");

    private static (HealingViewModel Vm, List<string> Sent) New(HealingSettings? s = null)
    {
        var sent = new List<string>();
        var vm = new HealingViewModel(s);
        vm.AttachForTest(sent.Add);
        vm.Apply(Renucci());
        return (vm, sent);
    }

    private static Point Centre(HealingViewModel.BodyFigure fig, string region)
    {
        var p = fig.Parts.Single(x => x.RegionId == region);
        return new Point(p.Left + p.Width / 2, p.Top + p.Height / 2);
    }

    private static HealingViewModel.BodyFigure Figure(HealingViewModel vm, InjuryAxis axis)
        => vm.PanelFigures.Single(f => f.Axis == axis);

    // ── Layout ───────────────────────────────────────────────────────────────

    [Fact]
    public void Four_figures_one_per_axis_and_an_overlay_figure()
    {
        var (vm, _) = New();
        Assert.Equal(new[] { InjuryAxis.FreshExternal, InjuryAxis.FreshInternal, InjuryAxis.ScarExternal, InjuryAxis.ScarInternal },
                     vm.PanelFigures.Select(f => f.Axis!.Value));
        Assert.Null(vm.OverlayFigure.Axis);
        Assert.False(vm.OverlayLayout);   // four figures is the default
        Assert.True(vm.PanelLayout);
    }

    [Fact]
    public void Each_figure_carries_only_its_own_axis_and_the_overlay_carries_all()
    {
        var (vm, _) = New();
        Assert.True(Figure(vm, InjuryAxis.FreshExternal).Parts.Single(p => p.RegionId == "leftArm").IsWounded);
        Assert.False(Figure(vm, InjuryAxis.ScarExternal).Parts.Single(p => p.RegionId == "leftArm").IsWounded);
        Assert.True(Figure(vm, InjuryAxis.ScarInternal).Parts.Single(p => p.RegionId == "leftArm").IsWounded);

        var arm = vm.OverlayFigure.Parts.Single(p => p.RegionId == "leftArm");
        Assert.Contains("Fresh External", arm.Tip);
        Assert.Contains("Scar Internal", arm.Tip);
    }

    [Fact]
    public void The_layout_choice_is_saved()
    {
        var saves = 0;
        var s = new HealingSettings();
        var vm = new HealingViewModel(s);
        vm.AttachForTest(_ => { }, save: () => saves++);

        vm.OverlayLayout = true;
        Assert.True(s.OverlayFigure);
        Assert.False(vm.PanelLayout);
        Assert.Equal(1, saves);

        var path = Path.Combine(Path.GetTempPath(), $"healing-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(s.Save(path));
            var back = new HealingSettings();
            Assert.True(back.Load(path));
            Assert.True(back.OverlayFigure);
        }
        finally { File.Delete(path); }
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    [Fact]
    public void Overlapping_boxes_go_to_the_part_drawn_on_top()
    {
        var (vm, _) = New();
        var fig = vm.OverlayFigure;
        // (200, 400) is inside both the chest's box and the right arm's; the
        // arm is drawn later, so it is on top.
        Assert.Equal("rightArm", HealingViewModel.PartAt(fig, new Point(200, 400))?.RegionId);
        Assert.Equal("chest",    HealingViewModel.PartAt(fig, Centre(fig, "chest"))?.RegionId);
        Assert.Null(HealingViewModel.PartAt(fig, new Point(5, 600)));
    }

    // ── Clicks: four figures ─────────────────────────────────────────────────

    [Fact]
    public void A_click_on_a_figure_heals_that_part_on_the_figures_axis()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";

        var scarIn = Figure(vm, InjuryAxis.ScarInternal);
        vm.FigureClicked(scarIn, Centre(scarIn, "leftArm"));
        Assert.Equal(new[] { "take Renucci left arm internal scar" }, sent);
    }

    [Fact]
    public void A_click_on_a_part_healthy_on_that_figure_sends_nothing()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";
        var scarOut = Figure(vm, InjuryAxis.ScarExternal);
        vm.FigureClicked(scarOut, Centre(scarOut, "leftArm"));
        Assert.Empty(sent);
    }

    [Fact]
    public void The_picked_amount_rides_on_a_figure_click()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";
        vm.SelectedAmount = HealingViewModel.AmountOptions.Single(a => a.Amount == TakeAmount.Half);
        var fresh = Figure(vm, InjuryAxis.FreshExternal);
        vm.FigureClicked(fresh, Centre(fresh, "leftArm"));
        Assert.Equal(new[] { "take Renucci left arm half" }, sent);
    }

    // ── Clicks: overlay ──────────────────────────────────────────────────────

    [Fact]
    public void An_overlay_click_opens_the_parts_card_and_sends_nothing()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";
        vm.OverlayLayout = true;

        vm.FigureClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "leftArm"));

        Assert.Empty(sent);
        Assert.True(vm.HasSelectedPart);
        Assert.Equal("leftArm", vm.SelectedPart!.RegionId);
        Assert.True(vm.SelectedPart.IsSelected);
        Assert.Equal(new[] { InjuryAxis.FreshExternal, InjuryAxis.ScarInternal },
                     vm.SelectedPartWounds.Select(w => w.Axis));
        Assert.False(vm.SelectedPartWounds[0].Striped);
        Assert.True(vm.SelectedPartWounds[1].Striped);
        Assert.All(vm.SelectedPartWounds, w => Assert.True(w.CanTransfer)); // both axes, public #375
    }

    [Fact]
    public void The_card_takes_and_transfers_the_chosen_wound()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";
        vm.OverlayLayout = true;
        vm.FigureClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "leftArm"));

        vm.TakeSelected(vm.SelectedPartWounds[1]);
        vm.TransferSelected(vm.SelectedPartWounds[1]);

        Assert.Equal(new[] { "take Renucci left arm internal scar", "transfer Renucci internal left arm scar" }, sent);
    }

    [Fact]
    public void A_click_on_a_healthy_part_closes_the_card()
    {
        var (vm, _) = New();
        vm.OverlayLayout = true;
        vm.FigureClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "leftArm"));
        vm.FigureClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "chest"));
        Assert.False(vm.HasSelectedPart);
        Assert.Empty(vm.SelectedPartWounds);
    }

    [Fact]
    public void Switching_back_to_four_figures_closes_the_card()
    {
        var (vm, _) = New();
        vm.OverlayLayout = true;
        vm.FigureClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "head"));
        vm.OverlayLayout = false;
        Assert.False(vm.HasSelectedPart);
    }

    [Fact]
    public void An_overlay_right_click_transfers_the_worst_inside_wound()
    {
        var (vm, sent) = New();
        vm.PatientName = "Renucci";
        vm.FigureRightClicked(vm.OverlayFigure, Centre(vm.OverlayFigure, "head"));
        Assert.Equal(new[] { "transfer Renucci internal head" }, sent);
    }

    [Fact]
    public void Healing_yourself_from_a_figure_casts_at_the_part()
    {
        var sent = new List<string>();
        var vm = new HealingViewModel();
        vm.AttachForTest(sent.Add);
        vm.Apply(Read("Your injuries include...",
                      "Wounds to the HEAD:",
                      "  Fresh External:  a cut -- harmful",
                      "You have some vitality."));
        vm.PatientName = "";
        var fig = Figure(vm, InjuryAxis.FreshExternal);
        vm.FigureClicked(fig, Centre(fig, "head"));
        Assert.Equal("prep hw", sent[0]);
    }

    // ── The overlay's centre mask ────────────────────────────────────────────

    [Fact]
    public void The_centre_of_a_bar_is_its_middle_band_and_the_edge_is_not()
    {
        // A 60×20 solid bar inside a 70×30 transparent field.
        int w = 70, h = 30;
        var alpha = new byte[w * h];
        for (int y = 5; y < 25; y++) for (int x = 5; x < 65; x++) alpha[y * w + x] = 255;

        var core = HealingMasks.Core(alpha, w, h);

        Assert.Equal(1, core[15 * w + 35]);     // middle of the bar
        Assert.Equal(0, core[5 * w + 35]);      // top edge row
        Assert.Equal(0, core[15 * w + 5]);      // left edge column
        Assert.Equal(0, core[0]);               // outside the bar
        // The band is a real fraction of the thickness, not a sliver.
        var band = Enumerable.Range(5, 20).Count(y => core[y * w + 35] == 1);
        Assert.InRange(band, 6, 16);
    }

    [Fact]
    public void A_thin_diagonal_limb_still_has_a_centre()
    {
        // A 6-pixel-thick diagonal stroke: its bounding box is wide, its
        // thickness small — the centre must follow the stroke, not the box.
        int n = 80;
        var alpha = new byte[n * n];
        for (int i = 0; i < n; i++)
            for (int t = -3; t < 3; t++)
            {
                int x = i + t;
                if (x >= 0 && x < n) alpha[i * n + x] = 255;
            }
        var core = HealingMasks.Core(alpha, n, n);
        Assert.Contains((byte)1, core);
    }

    [Fact]
    public void Scar_colours_are_blue_and_fresh_colours_are_not()
    {
        var scar  = HealingSprites.ColourFor(InjuryAxis.ScarInternal, 7);
        var fresh = HealingSprites.ColourFor(InjuryAxis.FreshExternal, 7);
        Assert.True(scar.B > scar.R);
        Assert.True(fresh.R > fresh.B);
        // Worse is darker.
        var mild = HealingSprites.ColourFor(InjuryAxis.FreshExternal, 1);
        var bad  = HealingSprites.ColourFor(InjuryAxis.FreshExternal, 13);
        Assert.True(mild.R + mild.G + mild.B > bad.R + bad.G + bad.B);
    }
}
