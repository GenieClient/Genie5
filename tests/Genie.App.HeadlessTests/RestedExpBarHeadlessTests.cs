using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.ViewModels;
using Genie.Core.Extensions.Builtin;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Hands-strip rested-EXP gauge: hidden until the first reading, then green /
/// yellow / red by spendable time, fill = spendable over the 6-hour cap, and the
/// Window menu toggle persists through Display.
/// </summary>
public class RestedExpBarHeadlessTests : IDisposable
{
    private readonly string _dir;

    public RestedExpBarHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_rexpbar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    private static RestedExpReading Read(string stored, string usable)
    {
        Assert.True(RestedExpGauge.TryParse(
            $"Rested EXP Stored: {stored}  Usable This Cycle: {usable}  Cycle Refreshes: 4:16 hours", out var r));
        return r;
    }

    private static Color ColorOf(IBrush b) => ((ISolidColorBrush)b).Color;

    [AvaloniaFact]
    public void Hidden_until_the_first_reading_and_off_by_default()
    {
        var vm = NewVm();

        Assert.False(vm.Display.ShowRestedExpBar);
        Assert.False(vm.Vitals.HasRestedExp);
    }

    [AvaloniaFact]
    public void Green_when_plenty_is_spendable()
    {
        var vm = NewVm();

        vm.Vitals.ApplyRestedExp(Read("5:03 hours", "5:51 hours"));

        Assert.True(vm.Vitals.HasRestedExp);
        Assert.Equal("REXP 5:03", vm.Vitals.RestedLabel);
        Assert.Equal(Color.Parse("#40A040"), ColorOf(vm.Vitals.RestedBarBrush));
        Assert.Equal(5.05 / 6.0 * 100.0, vm.Vitals.RestedFill, 1);
    }

    [AvaloniaFact]
    public void Allowance_limits_the_bar_even_with_a_full_bank()
    {
        var vm = NewVm();

        vm.Vitals.ApplyRestedExp(Read("5:03 hours", "1:46 hour"));

        Assert.Equal("REXP 1:46", vm.Vitals.RestedLabel);
        Assert.Equal(Color.Parse("#40A040"), ColorOf(vm.Vitals.RestedBarBrush));
        Assert.True(vm.Vitals.RestedFill < 31 && vm.Vitals.RestedFill > 28);
    }

    [AvaloniaFact]
    public void Yellow_when_under_an_hour_then_red_when_nothing_is_spendable()
    {
        var vm = NewVm();

        vm.Vitals.ApplyRestedExp(Read("0:35 hours", "6 hours"));
        Assert.Equal(Color.Parse("#D8A020"), ColorOf(vm.Vitals.RestedBarBrush));

        vm.Vitals.ApplyRestedExp(Read("none", "1:46 hour"));
        Assert.Equal(Color.Parse("#C04040"), ColorOf(vm.Vitals.RestedBarBrush));
        Assert.Equal(0, vm.Vitals.RestedFill);
        Assert.Equal("REXP 0:00", vm.Vitals.RestedLabel);
        Assert.Contains("Bank is empty", vm.Vitals.RestedTip);
    }

    [AvaloniaFact]
    public void Menu_toggle_flips_and_persists_the_setting()
    {
        var vm = NewVm();

        vm.ToggleRestedExpBarCommand.Execute().Subscribe();
        Assert.True(vm.Display.ShowRestedExpBar);

        vm.ToggleRestedExpBarCommand.Execute().Subscribe();
        Assert.False(vm.Display.ShowRestedExpBar);
    }
}
