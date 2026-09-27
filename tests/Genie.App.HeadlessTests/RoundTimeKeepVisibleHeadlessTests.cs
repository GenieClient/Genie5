using System;
using System.IO;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Layout → Roundtime Position → Keep Visible. The RT badge lives in two slots
/// (command bar, hands strip) and by default collapses whenever there is no
/// roundtime. Keep Visible pins it on in whichever slot is chosen, dimmed
/// while idle, so the row doesn't shift as RT starts and ends.
/// </summary>
public class RoundTimeKeepVisibleHeadlessTests : IDisposable
{
    private readonly string _dir;

    public RoundTimeKeepVisibleHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_rtkeep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    private static void Run(ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> cmd)
        => cmd.Execute().Subscribe();

    [AvaloniaFact]
    public void Default_is_off_so_the_badge_still_collapses_outside_rt()
    {
        var vm = NewVm();

        Assert.False(vm.Display.KeepRoundTimeVisible);
        Assert.False(vm.Vitals.InRoundTime);
        Assert.False(vm.ShowRtInCommandBar);
        Assert.False(vm.ShowRtOnHandsStrip);
    }

    [AvaloniaFact]
    public void Keep_visible_pins_the_badge_in_exactly_the_chosen_slot_dimmed()
    {
        var vm = NewVm();

        Run(vm.ToggleKeepRoundTimeVisibleCommand);

        Assert.True(vm.Display.KeepRoundTimeVisible);
        Assert.True(vm.ShowRtInCommandBar);
        Assert.False(vm.ShowRtOnHandsStrip);
        Assert.True(vm.RtBadgeOpacity < 1.0);

        Run(vm.RoundTimeToHandsStripCommand);
        Assert.False(vm.ShowRtInCommandBar);
        Assert.True(vm.ShowRtOnHandsStrip);

        Run(vm.ToggleKeepRoundTimeVisibleCommand);
        Assert.False(vm.ShowRtInCommandBar);
        Assert.False(vm.ShowRtOnHandsStrip);
    }

    [AvaloniaFact]
    public void The_choice_persists_to_display_json()
    {
        var path = Path.Combine(_dir, "display.json");
        var settings = new DisplaySettings { KeepRoundTimeVisible = true };
        settings.Save(path);

        Assert.True(DisplaySettings.Load(path).KeepRoundTimeVisible);
    }
}
