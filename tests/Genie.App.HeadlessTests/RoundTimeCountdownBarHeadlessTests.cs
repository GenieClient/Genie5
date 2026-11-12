using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.Core.Presets;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Layout → Roundtime styles (public #404): Badge (default, the red text
/// badge), Countdown Badge (that badge with a shrinking fill) and Countdown Bar
/// (Genie 4's <c>ComponentRoundtime</c>: fill = remaining ÷ starting RT, whole
/// seconds drawn on it, coloured by the <c>roundtime</c> preset).
/// </summary>
public class RoundTimeCountdownBarHeadlessTests : IDisposable
{
    private readonly string _dir;

    public RoundTimeCountdownBarHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_rtbar_" + Guid.NewGuid().ToString("N"));
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

    private static void Run(ReactiveUI.ReactiveCommand<string, System.Reactive.Unit> cmd, string arg)
        => cmd.Execute(arg).Subscribe();

    [AvaloniaFact]
    public void Default_is_the_plain_badge_and_the_menu_choice_is_saved()
    {
        var vm = NewVm();
        Assert.Equal(DisplaySettings.RoundTimeStyles.Badge, vm.Display.RoundTimeStyle);
        Assert.True(vm.Display.RoundTimeStyleBadge);
        Assert.True(vm.Display.RoundTimeStyleAnyBadge);

        Run(vm.SetRoundTimeStyleCommand, "CountdownBadge");

        Assert.True(vm.Display.RoundTimeStyleCountdownBadge);
        Assert.True(vm.Display.RoundTimeStyleAnyBadge);
        Assert.False(vm.Display.RoundTimeStyleBadge);
        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        Assert.Equal("CountdownBadge", DisplaySettings.Load(path).RoundTimeStyle);

        Run(vm.SetRoundTimeStyleCommand, "CountdownBar");

        Assert.True(vm.Display.RoundTimeStyleCountdownBar);
        Assert.False(vm.Display.RoundTimeStyleAnyBadge);
    }

    [AvaloniaFact]
    public void An_unknown_style_falls_back_to_the_plain_badge()
    {
        var vm = NewVm();
        Run(vm.SetRoundTimeStyleCommand, "Sparkly");
        Assert.Equal(DisplaySettings.RoundTimeStyles.Badge, vm.Display.RoundTimeStyle);

        vm.Display.RoundTimeStyle = "Sparkly";   // e.g. hand-edited display.json
        Assert.True(vm.Display.RoundTimeStyleBadge);
    }

    [AvaloniaFact]
    public void A_new_roundtime_fills_the_bar_and_labels_whole_seconds_rounded_up()
    {
        var v = new VitalsViewModel();

        v.ApplyRoundTimeEnd(In(5.0));

        Assert.True(v.InRoundTime);
        Assert.InRange(v.RoundTimeTotalSeconds, 4.9, 5.0);
        Assert.InRange(v.RoundTimeSeconds / v.RoundTimeTotalSeconds, 0.98, 1.0);
        Assert.Equal("5", v.RoundTimeLabel);
    }

    [AvaloniaFact]
    public void Label_is_blank_with_no_roundtime()
    {
        var v = new VitalsViewModel();
        Assert.Equal("", v.RoundTimeLabel);

        v.ApplyRoundTimeEnd(In(-1));

        Assert.False(v.InRoundTime);
        Assert.Equal("", v.RoundTimeLabel);
    }

    /// <summary>Genie 4 FormMain.cs L6488: restart the bar only when no RT is
    /// running or the new RT is more than a second longer than what is left.</summary>
    [AvaloniaFact]
    public void A_resent_or_shorter_roundtime_keeps_the_bar_scale()
    {
        var v = new VitalsViewModel();
        v.ApplyRoundTimeEnd(In(10));
        var total = v.RoundTimeTotalSeconds;

        v.ApplyRoundTimeEnd(In(6));     // shorter: same scale, fill drops to ~60%

        Assert.Equal(total, v.RoundTimeTotalSeconds);
        Assert.InRange(v.RoundTimeSeconds / v.RoundTimeTotalSeconds, 0.55, 0.61);
    }

    [AvaloniaFact]
    public void A_longer_roundtime_restarts_the_bar_from_full()
    {
        var v = new VitalsViewModel();
        v.ApplyRoundTimeEnd(In(3));

        v.ApplyRoundTimeEnd(In(8));

        Assert.InRange(v.RoundTimeTotalSeconds, 7.9, 8.0);
        Assert.Equal("8", v.RoundTimeLabel);
    }

    [AvaloniaFact]
    public void Layout_carries_the_style_and_an_older_layout_leaves_it_alone()
    {
        var vm = NewVm();
        Run(vm.SetRoundTimeStyleCommand, "CountdownBadge");

        var saved = SavedLayout.FromJson(vm.CaptureCurrentLayout().ToJson());   // through disk form
        Assert.Equal("CountdownBadge", saved!.RoundTimeStyle);

        Run(vm.SetRoundTimeStyleCommand, "Badge");
        vm.ApplyLayout(saved);
        Assert.True(vm.Display.RoundTimeStyleCountdownBadge);

        vm.ApplyLayout(new SavedLayout());                    // pre-#404 layout: null
        Assert.True(vm.Display.RoundTimeStyleCountdownBadge);
    }

    [AvaloniaFact]
    public void Bar_is_coloured_by_the_roundtime_preset_and_repaints_when_it_changes()
    {
        var prev   = DefaultHighlights.PresetEngine;
        var engine = new PresetEngine();
        DefaultHighlights.PresetEngine = engine;
        try
        {
            var bar = new ProgressBar();
            PresetBar.SetPreset(bar, "roundtime");
            PresetBar.Apply(bar);

            // Stock roundtime preset is Genie 4's: MediumBlue fill, no track.
            Assert.Equal(Colors.MediumBlue, Assert.IsAssignableFrom<ISolidColorBrush>(bar.Foreground).Color);

            engine.Apply(new PresetRule { Id = "roundtime", ForegroundColor = "#112233", BackgroundColor = "#445566" });
            PresetBar.Apply(bar);

            Assert.Equal(Color.Parse("#112233"), Assert.IsAssignableFrom<ISolidColorBrush>(bar.Foreground).Color);
            Assert.Equal(Color.Parse("#445566"), Assert.IsAssignableFrom<ISolidColorBrush>(bar.Background).Color);
        }
        finally { DefaultHighlights.PresetEngine = prev; }
    }
}
