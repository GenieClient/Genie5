using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Layout → Combined Spell Timer: the hands strip's spell box works Genie 4's
/// two spell widgets into one. The box fills with the castbar preset and
/// empties over the prep time, and the label reads "(N) Spell (M)": seconds
/// since prep started in front, seconds until fully prepared on the end.
/// Off (the default) keeps Genie 4's separate label and cast bar.
/// </summary>
public class CombinedSpellTimerHeadlessTests : IDisposable
{
    private readonly string _dir;

    public CombinedSpellTimerHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_spellbox_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    private static void Run(ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> cmd)
        => cmd.Execute().Subscribe();

    private static DateTimeOffset In(double seconds) => DateTimeOffset.UtcNow.AddSeconds(seconds);

    [AvaloniaFact]
    public void Default_is_off_and_the_menu_toggle_is_saved()
    {
        var vm = NewVm();
        Assert.False(vm.Display.CombinedSpellTimer);

        Run(vm.ToggleCombinedSpellTimerCommand);

        Assert.True(vm.Display.CombinedSpellTimer);
        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        Assert.True(DisplaySettings.Load(path).CombinedSpellTimer);
    }

    [AvaloniaFact]
    public void Label_has_the_prep_seconds_left_on_the_end_while_preparing()
    {
        var v = new VitalsViewModel();
        v.ApplyPreparedSpell("Swirling Winds");
        v.ApplyCastTimeEnd(In(3.5));

        Assert.Equal("(0) Swirling Winds", v.PreparedSpellLabel);              // Genie 4's label is unchanged
        Assert.Equal("(0) Swirling Winds (3)", v.PreparedSpellCountdownLabel);
        Assert.True(v.IsPrepping);
        Assert.Equal(3, v.CastBarSecondsTotal);
        Assert.InRange(v.CastBarSecondsExact, 3.0, 3.5);                      // fraction kept for a smooth fill
    }

    [AvaloniaFact]
    public void Countdown_drops_off_the_end_once_the_spell_is_prepared()
    {
        var v = new VitalsViewModel();
        v.ApplyPreparedSpell("Swirling Winds");
        v.ApplyCastTimeEnd(In(-1));                                           // already fully prepared

        Assert.False(v.IsPrepping);
        Assert.Equal(0, v.CastBarSecondsExact);
        Assert.Equal("(0) Swirling Winds", v.PreparedSpellCountdownLabel);
    }

    [AvaloniaFact]
    public void Releasing_the_spell_empties_the_box()
    {
        var v = new VitalsViewModel();
        v.ApplyPreparedSpell("Swirling Winds");
        v.ApplyCastTimeEnd(In(5));

        v.ApplyPreparedSpell("None");

        Assert.Equal("", v.PreparedSpellCountdownLabel);
        Assert.Equal(0, v.CastBarSecondsExact);
        Assert.False(v.IsPrepping);
    }

    [AvaloniaFact]
    public void Layout_carries_the_choice_and_an_older_layout_leaves_it_alone()
    {
        var vm = NewVm();
        Run(vm.ToggleCombinedSpellTimerCommand);

        var saved = SavedLayout.FromJson(vm.CaptureCurrentLayout().ToJson());   // through disk form
        Assert.True(saved!.CombinedSpellTimer);

        Run(vm.ToggleCombinedSpellTimerCommand);
        vm.ApplyLayout(saved);
        Assert.True(vm.Display.CombinedSpellTimer);

        vm.ApplyLayout(new SavedLayout());                                    // older layout: null
        Assert.True(vm.Display.CombinedSpellTimer);
    }
}
