using System;
using System.IO;
using System.Linq;
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

    // ── Rides on saved layouts, next to the RT position ───────────────────────

    [AvaloniaFact]
    public void A_saved_layout_captures_keep_visible_and_loading_restores_it()
    {
        var vm = NewVm();
        Run(vm.ToggleKeepRoundTimeVisibleCommand);
        var saved = SavedLayout.FromJson(vm.CaptureCurrentLayout().ToJson());   // through disk form
        Assert.True(saved!.KeepRoundTimeVisible);

        Run(vm.ToggleKeepRoundTimeVisibleCommand);   // off, as another layout would leave it
        Assert.False(vm.ShowRtInCommandBar);

        vm.ApplyLayout(saved);

        Assert.True(vm.Display.KeepRoundTimeVisible);
        Assert.True(vm.ShowRtInCommandBar);
    }

    [AvaloniaFact]
    public void Loading_a_layout_persists_keep_visible_so_a_restart_keeps_it()
    {
        var vm = NewVm();
        vm.ApplyLayout(new SavedLayout { KeepRoundTimeVisible = true });

        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        Assert.True(DisplaySettings.Load(path).KeepRoundTimeVisible);
    }

    /// <summary>Every layout on disk predates the key; loading one must not
    /// switch the user's choice off.</summary>
    [AvaloniaFact]
    public void A_layout_saved_before_keep_visible_existed_leaves_it_alone()
    {
        var vm = NewVm();
        Run(vm.ToggleKeepRoundTimeVisibleCommand);

        var legacy = SavedLayout.FromJson("{\"Name\":\"Legacy\"}");
        Assert.Null(legacy!.KeepRoundTimeVisible);
        vm.ApplyLayout(legacy);

        Assert.True(vm.Display.KeepRoundTimeVisible);
    }

    [AvaloniaFact]
    public void Reset_layout_leaves_keep_visible_alone_like_the_other_bars()
    {
        var vm = NewVm();
        Run(vm.ToggleKeepRoundTimeVisibleCommand);

        Run(vm.ResetLayoutCommand);

        Assert.True(vm.Display.KeepRoundTimeVisible);
    }
}
