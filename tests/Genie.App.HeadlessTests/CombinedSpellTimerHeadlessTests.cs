using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Genie.App.Views;
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

    [AvaloniaFact]
    public void Label_is_white_over_the_fill_and_purple_over_the_track()
    {
        var vm = NewVm();
        vm.Display.CombinedSpellTimer = true;
        vm.Vitals.ApplyPreparedSpell("Swirling Winds");
        vm.Vitals.ApplyCastTimeEnd(In(10));

        // A 30 px row, like a hands strip with the Enhanced icons on.
        var view = new SpellCountdownBoxView { DataContext = vm, Width = 200, Height = 30 };
        using var host = new Hosted<StackPanel>(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children    = { view },
        });

        var bar   = view.GetLogicalDescendants().OfType<ProgressBar>().Single();
        var texts = view.GetLogicalDescendants().OfType<TextBlock>().ToList();
        var track = texts.Single(t => t.Name == "TrackText");
        var fill  = texts.Single(t => t.Name == "FillText");

        Assert.True(bar.IsVisible);
        Assert.True(bar.Bounds.Width <= 200, $"bar {bar.Bounds.Width} px is wider than its 200 px box");
        Assert.True(bar.Bounds.Height >= 26, $"bar is {bar.Bounds.Height} px tall in a 30 px row; it should stretch");

        // Half the prep time left: the split sits at the middle of the box,
        // in the labels' coordinates (they start 6 px in).
        bar.Maximum = 10;
        bar.Value   = 5;
        Assert.Equal(bar.Bounds.Width / 2 - track.Bounds.X, view.SplitX, 3);
        Assert.Equal(view.SplitX, fill.Clip!.Bounds.Right, 3);
        Assert.Equal(view.SplitX, track.Clip!.Bounds.Left, 3);

        // Prepared: no fill, the whole label is the track colour.
        vm.Vitals.ApplyCastTimeEnd(In(-1));
        host.Pump();
        Assert.False(bar.IsVisible);
        Assert.Equal(0, view.SplitX);
    }
}
