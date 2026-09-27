using System;
using System.Collections.Generic;
using System.Linq;
using Genie.App.ViewModels;
using Genie.Core.Health;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #263 — the Healing panel's view-model: what it shows for a
/// perceive-health reading, that it keeps the latest reading per patient, what
/// each click sends, and the send policy — nothing goes out unless one of the
/// click entry points is invoked. Readings are built by the #277 parser from
/// the block shapes its own tests use.
/// </summary>
public class HealingViewModelTests
{
    private static PatientHealth Read(params string[] lines)
    {
        var p = new PerceiveHealthParser(() => new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        foreach (var l in lines) p.Feed(l);
        return p.TakeCompleted() ?? throw new InvalidOperationException("block did not complete");
    }

    private static PatientHealth Renucci(string armSeverity = "very severe") => Read(
        "Renucci's injuries include...",
        "Wounds to the LEFT ARM:",
        $"  Fresh External:  a deep gash -- {armSeverity}",
        "  Scars Internal:  old scarring -- minor",
        "Renucci has little vitality (60%).");

    private static PatientHealth Naper() => Read(
        "Naper's injuries include...",
        "Wounds to the CHEST:",
        "  Fresh External:  -- severe",
        "  Fresh Internal:  -- harmful",
        "Naper has some vitality.");

    private static PatientHealth Self() => Read(
        "Your injuries include...",
        "Wounds to the HEAD:",
        "  Fresh External:  a cut -- harmful",
        "  Scars External:  a scar -- negligible",
        "You have some vitality.");

    private static (HealingViewModel Vm, List<string> Sent) New(HealingSettings? settings = null)
    {
        var sent = new List<string>();
        var vm = new HealingViewModel(settings);
        vm.AttachForTest(sent.Add);
        return (vm, sent);
    }

    private static HealingViewModel.RegionCell Cell(HealingViewModel vm, string id)
        => vm.Cells.Single(c => c.RegionId == id);

    // ── State from readings ──────────────────────────────────────────────────

    [Fact]
    public void A_reading_colours_the_diagram_on_the_selected_axis()
    {
        var (vm, _) = New();
        vm.Apply(Renucci());

        Assert.True(vm.HasReading);
        Assert.Equal("Renucci", vm.PatientName);
        Assert.False(vm.IsSelf);

        var arm = Cell(vm, "leftArm");
        Assert.Equal(WoundSeverity.VerySevere, arm.Severity);   // Fresh External by default
        Assert.True(arm.IsWounded);
        Assert.Equal("10", arm.Rung);
        Assert.False(Cell(vm, "head").IsWounded);

        vm.SelectedAxis = HealingViewModel.AxisOptions.Single(o => o.Axis == InjuryAxis.ScarInternal);
        Assert.Equal(WoundSeverity.Minor, arm.Severity);

        vm.SelectedAxis = HealingViewModel.AxisOptions.Single(o => o.Axis == InjuryAxis.FreshInternal);
        Assert.False(arm.IsWounded);
    }

    [Fact]
    public void Vitality_and_the_wound_list_cover_every_axis()
    {
        var (vm, _) = New();
        vm.Apply(Renucci());

        Assert.Equal("Vitality 40%", vm.VitalityText);
        Assert.True(vm.HasWounds);
        Assert.Equal(2, vm.Wounds.Count);
        Assert.Contains(vm.Wounds, w => w.Contains("Left Arm") && w.Contains("fresh external") && w.Contains("10/13"));
        Assert.Contains(vm.Wounds, w => w.Contains("scar internal") && w.Contains("minor (3/13)"));
    }

    /// <summary>The ramp keeps the four worst rungs apart — the range Genie 4's
    /// Crutch drew in a single red.</summary>
    [Fact]
    public void The_worst_rungs_get_distinct_colours()
    {
        var fills = new[] { WoundSeverity.Severe, WoundSeverity.VerySevere, WoundSeverity.Devastating,
                            WoundSeverity.VeryDevastating, WoundSeverity.Useless }
            .Select(s => ((Avalonia.Media.ISolidColorBrush)HealingViewModel.FillFor(s)).Color)
            .ToList();
        Assert.Equal(fills.Count, fills.Distinct().Count());
    }

    [Fact]
    public void Poison_and_disease_show()
    {
        var (vm, _) = New();
        vm.Apply(Read("Naper's injuries include...",
                      "Wounds to the SKIN:",
                      "  Fresh External:  -- minor",
                      "Naper is poisoned.",
                      "Naper has a disease.",
                      "Naper has some vitality."));
        Assert.True(vm.IsPoisoned);
        Assert.True(vm.IsDiseased);
    }

    // ── Multi-patient retention ──────────────────────────────────────────────

    [Fact]
    public void Each_patient_keeps_their_latest_reading()
    {
        var (vm, _) = New();
        vm.Apply(Renucci());
        vm.Apply(Naper());
        vm.Apply(Self());

        Assert.Equal(3, vm.Patients.Count);
        Assert.Equal(PatientHealth.SelfPatient, vm.Patients[0].Key);   // most recent first
        Assert.True(vm.IsSelf);

        // A new reading for an existing patient replaces theirs, not adds.
        vm.Apply(Renucci("minor"));
        Assert.Equal(3, vm.Patients.Count);
        Assert.Equal("Renucci", vm.Patients[0].Key);
        Assert.Equal(WoundSeverity.Minor, Cell(vm, "leftArm").Severity);

        // Choosing another patient shows theirs and retargets the buttons.
        vm.SelectedPatient = vm.Patients.Single(p => p.Key == "Naper");
        Assert.Equal("Naper", vm.PatientName);
        Assert.Equal(WoundSeverity.Severe, Cell(vm, "chest").Severity);
        Assert.False(Cell(vm, "leftArm").IsWounded);
    }

    [Fact]
    public void Forget_drops_only_the_selected_patient()
    {
        var (vm, sent) = New();
        vm.Apply(Renucci());
        vm.Apply(Naper());

        vm.Forget();   // Naper is selected (latest)

        Assert.Single(vm.Patients);
        Assert.Equal("Renucci", vm.SelectedPatient!.Key);
        Assert.Empty(sent);
    }

    // ── Clicks → command strings ─────────────────────────────────────────────

    [Fact]
    public void Clicks_send_the_built_commands()
    {
        var (vm, sent) = New(new HealingSettings { QuickTake = true });
        vm.Apply(Renucci());

        vm.Touch();
        vm.Perceive();
        vm.HealRegion(Cell(vm, "leftArm"));
        vm.TakeCondition(HealingCondition.Vitality);

        Assert.Equal(new[]
        {
            "touch Renucci",
            "perceive health Renucci",
            "take Renucci left arm quick",
            "take Renucci vitality quick",
        }, sent);
    }

    [Fact]
    public void Take_all_queues_every_wound_on_the_selected_patients_reading()
    {
        var (vm, sent) = New();
        vm.Apply(Renucci());

        vm.TakeAll();

        Assert.Equal(new[]
        {
            "#send take Renucci left arm",
            "#send take Renucci left arm internal scar",
        }, sent);
    }

    [Fact]
    public void A_healthy_region_click_sends_nothing()
    {
        var (vm, sent) = New();
        vm.Apply(Renucci());
        vm.HealRegion(Cell(vm, "head"));
        Assert.Empty(sent);
    }

    /// <summary>Yourself, no wait set: the prep goes, and the cast waits for
    /// the Cast click rather than any timer.</summary>
    [Fact]
    public void Your_own_wound_prepares_then_casts_only_on_the_cast_click()
    {
        var settings = new HealingSettings();
        settings.SetMana(HealingSpell.HW, 15);
        var (vm, sent) = New(settings);
        vm.Apply(Self());

        vm.HealRegion(Cell(vm, "head"));
        Assert.Equal(new[] { "prep hw 15" }, sent);
        Assert.True(vm.HasPendingCast);
        Assert.Equal("cast head", vm.PendingCast);

        vm.CastPending();
        Assert.Equal(new[] { "prep hw 15", "cast head" }, sent);
        Assert.False(vm.HasPendingCast);
    }

    [Fact]
    public void Spell_buttons_and_stop()
    {
        var settings = new HealingSettings();
        settings.SetMana(HealingSpell.REGE, 25);
        settings.SetDelay(HealingSpell.REGE, 6);
        var (vm, sent) = New(settings);

        vm.CastSpell(HealingSpell.REGE);
        vm.Stop();

        Assert.Equal(new[] { "prep regen 25", "#send 6 cast", "#queue clear" }, sent);
    }

    [Fact]
    public void Editing_a_spell_row_writes_the_setting_and_saves()
    {
        var settings = new HealingSettings();
        var saves = 0;
        var vm = new HealingViewModel(settings);
        var sent = new List<string>();
        vm.AttachForTest(sent.Add, save: () => saves++);

        var hw = vm.Spells.Single(s => s.Info.Spell == HealingSpell.HW);
        hw.Mana  = 18;
        hw.Delay = 3.5m;

        Assert.Equal(18,  settings.GetMana(HealingSpell.HW));
        Assert.Equal(3.5, settings.GetDelay(HealingSpell.HW));
        Assert.Equal(2, saves);
        Assert.Empty(sent);
    }

    // ── Policy ───────────────────────────────────────────────────────────────

    /// <summary>
    /// docs/POLICY.md: the client never acts on its own. Readings arriving,
    /// patients changing, the axis changing, settings being edited — none of
    /// it may send. Only the explicit click entry points do.
    /// </summary>
    [Fact]
    public void Nothing_is_sent_without_an_explicit_click()
    {
        var (vm, sent) = New();

        vm.Apply(Renucci());
        vm.Apply(Naper());
        vm.Apply(Self());
        vm.Apply(Renucci("devastating"));
        vm.SelectedPatient = vm.Patients.Last();
        foreach (var axis in HealingViewModel.AxisOptions) vm.SelectedAxis = axis;
        vm.QuickTake = true;
        foreach (var row in vm.Spells) { row.Mana = 10; row.Delay = 2; }
        vm.Forget();
        vm.PatientName = "Naper";

        Assert.Empty(sent);

        vm.Touch();
        Assert.Single(sent);
    }
}
