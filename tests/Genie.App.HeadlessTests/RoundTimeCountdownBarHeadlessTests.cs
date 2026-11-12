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
/// Layout → Roundtime → Countdown Bar (public #404): Genie 4's roundtime bar
/// (<c>ComponentRoundtime</c>) as an option beside today's text badge. Fill =
/// remaining ÷ starting RT, the whole seconds left drawn on it, coloured by the
/// <c>roundtime</c> preset. Off by default.
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

    [AvaloniaFact]
    public void Off_by_default_and_the_menu_toggle_saves_it()
    {
        var vm = NewVm();
        Assert.False(vm.Display.RoundTimeCountdownBar);

        Run(vm.ToggleRoundTimeCountdownBarCommand);

        Assert.True(vm.Display.RoundTimeCountdownBar);
        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        Assert.True(DisplaySettings.Load(path).RoundTimeCountdownBar);
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
        Run(vm.ToggleRoundTimeCountdownBarCommand);

        var saved = SavedLayout.FromJson(vm.CaptureCurrentLayout().ToJson());   // through disk form
        Assert.True(saved!.RoundTimeCountdownBar);

        Run(vm.ToggleRoundTimeCountdownBarCommand);          // off again
        vm.ApplyLayout(saved);
        Assert.True(vm.Display.RoundTimeCountdownBar);

        vm.ApplyLayout(new SavedLayout());                    // pre-#404 layout: null
        Assert.True(vm.Display.RoundTimeCountdownBar);
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
