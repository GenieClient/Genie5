using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The strip across the top of the Experience window (Density slider, Track
/// gain, G4 layout, Sort, Echo, Rested) is a second way to set seven
/// <c>#config</c> values, and the strip's own visibility is an eighth
/// (<c>experienceconfigbar</c>, the window menu's "Show Config Bar").
///
/// <para>Each control wrote its setting, but nothing came back the other way.
/// The view-model read config once, in <c>Attach</c>. A typed
/// <c>#config experienceconfigbar off</c> saved the value and left the strip on
/// screen until the next start; a typed <c>#config experiencedensity 3</c>
/// re-rendered the rows (GenieCore refreshes the tracker on the Trackers
/// notice) while the slider and its label still said "Full". The Objects
/// window, which has the same kind of strip, already follows a typed
/// <c>#config objectsconfigbar</c>.</para>
///
/// <para>Same harness idea as <c>ObjectsViewModelTests</c>: outside a UI app
/// <c>RxApp.MainThreadScheduler</c> delivers synchronously, so the effect of a
/// SetSetting is observable on the next line.</para>
/// </summary>
[Collection("highlight-statics")]   // Attach subscribes to the UserHighlights static
public class ExperienceConfigBarSyncTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public GenieCore Core { get; }
        public ExperienceViewModel Vm { get; } = new();
        private readonly List<string> _dirs = new();

        public Harness()
        {
            Core = NewCore();
            Vm.Attach(Core);
        }

        public GenieCore NewCore()
        {
            var dir = Path.Combine(Path.GetTempPath(), "genie_expbar_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dirs.Add(dir);
            return new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
        }

        /// <summary>What a typed <c>#config key value</c> does.</summary>
        public void Typed(string key, string value) => Core.Config.SetSetting(key, value);

        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            foreach (var dir in _dirs)
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task A_typed_experienceconfigbar_hides_and_shows_the_bar()
    {
        await using var h = new Harness();
        Assert.True(h.Vm.ShowConfigBar);                 // the default

        h.Typed("experienceconfigbar", "off");
        Assert.False(h.Vm.ShowConfigBar);                // no restart, no reconnect

        h.Typed("experienceconfigbar", "on");
        Assert.True(h.Vm.ShowConfigBar);
    }

    [Fact]
    public async Task Hiding_the_bar_by_command_raises_the_change_the_window_binds_to()
    {
        // The strip's IsVisible binding and the window menu's "Show Config Bar"
        // tick (GenieDockFactory: WhenAnyValue(v => v.ShowConfigBar)) both hang
        // off this notification.
        await using var h = new Harness();
        var raised = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Typed("experienceconfigbar", "off");

        Assert.Contains(nameof(ExperienceViewModel.ShowConfigBar), raised);
    }

    [Fact]
    public async Task A_typed_experiencedensity_moves_the_slider_and_its_label()
    {
        await using var h = new Harness();
        Assert.Equal(0, h.Vm.DensityValue);
        Assert.Equal("Full", h.Vm.DensityLabel);

        h.Typed("experiencedensity", "3");

        Assert.Equal(3, h.Vm.DensityValue);
        Assert.Equal("Short names", h.Vm.DensityLabel);
        Assert.Equal(3, h.Core.Config.ExperienceDensity);   // followed, not written back differently
    }

    [Theory]
    [InlineData("experiencetrackgain")]
    [InlineData("experienceg4layout")]
    [InlineData("experienceecho")]
    [InlineData("experiencerested")]
    public async Task A_typed_tickbox_setting_moves_its_tickbox(string key)
    {
        await using var h = new Harness();
        bool Tick() => key switch
        {
            "experiencetrackgain" => h.Vm.TrackGain,
            "experienceg4layout"  => h.Vm.G4Layout,
            "experienceecho"      => h.Vm.EchoExp,
            _                     => h.Vm.ShowRested,
        };
        Assert.False(Tick());                            // all four default to off

        h.Typed(key, "on");
        Assert.True(Tick());

        h.Typed(key, "off");
        Assert.False(Tick());
    }

    [Fact]
    public async Task A_typed_experiencesort_moves_the_sort_dropdown()
    {
        await using var h = new Harness();
        Assert.Equal(2, h.Vm.SortIndex);                 // Learning Rate, the default

        h.Typed("experiencesort", "0");
        Assert.Equal(0, h.Vm.SortIndex);

        h.Typed("experiencesort", "3");
        Assert.Equal(3, h.Vm.SortIndex);
    }

    [Fact]
    public async Task The_bars_own_controls_still_write_their_settings_and_keep_their_value()
    {
        // The other direction, which already worked: following config must not
        // turn a click on the bar into a bounce.
        await using var h = new Harness();

        h.Vm.ShowConfigBar = false;
        Assert.False(h.Core.Config.ExperienceConfigBar);
        Assert.False(h.Vm.ShowConfigBar);

        h.Vm.DensityValue = 2;
        Assert.Equal(2, h.Core.Config.ExperienceDensity);
        Assert.Equal(2, h.Vm.DensityValue);

        h.Vm.TrackGain = true;
        Assert.True(h.Core.Config.ExperienceTrackGain);
        Assert.True(h.Vm.TrackGain);

        h.Vm.SortIndex = 1;
        Assert.Equal(1, h.Core.Config.ExperienceSort);
        Assert.Equal(1, h.Vm.SortIndex);
    }

    [Fact]
    public async Task An_unrelated_tracker_setting_leaves_the_bar_alone()
    {
        // spelltimer / showtimetracker raise the same Trackers notice.
        await using var h = new Harness();
        h.Typed("experiencedensity", "4");
        h.Typed("experiencetrackgain", "on");

        h.Typed("spelltimer", "off");

        Assert.Equal(4, h.Vm.DensityValue);
        Assert.True(h.Vm.TrackGain);
        Assert.True(h.Vm.ShowConfigBar);
    }

    [Fact]
    public async Task After_attaching_to_a_new_session_the_bar_follows_that_one_only()
    {
        await using var h = new Harness();
        var second = h.NewCore();
        try
        {
            second.Config.SetSetting("experiencedensity", "1");
            h.Vm.Attach(second);
            Assert.Equal(1, h.Vm.DensityValue);          // seeded from the new session

            h.Typed("experiencedensity", "4");           // the session it left
            Assert.Equal(1, h.Vm.DensityValue);

            second.Config.SetSetting("experienceconfigbar", "off");
            Assert.False(h.Vm.ShowConfigBar);
        }
        finally
        {
            await second.DisposeAsync();
        }
    }
}
