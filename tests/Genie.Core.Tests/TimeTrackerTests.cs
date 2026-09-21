using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The Time Tracker's client-side calculator (TimeCalc — ported from the
/// Genie 4 plugin's decompile) and the extension around it: calendar math
/// invariants, moon-cycle behavior, ambient-message calibration with offset
/// persistence, the $Time.* script globals, and the obs-sky observed overlay.
/// </summary>
public class TimeTrackerTests
{
    private static readonly DateTime T0 = new(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);

    // ── TimeCalc calendar ───────────────────────────────────────────────────────

    [Fact]
    public void Date_components_stay_in_range_across_a_full_elanthian_year()
    {
        var calc = new TimeCalc(0, 0, 0);
        // 400 Elanthian days × 6 real hours = 100 real days; sample every 3 h.
        for (var t = T0; t < T0.AddDays(100); t = t.AddHours(3))
        {
            calc.Recalculate(t);   // DRDateTime ctor throws on any out-of-range part
            var d = calc.Date;
            Assert.InRange(d.Month, 1, 10);
            Assert.InRange(d.Day, 1, 40);
            Assert.InRange(d.Anlas, 0, 11);
            Assert.InRange(d.Rois, 0, 29);
            Assert.InRange(d.DaysSince, 0, 399);
            Assert.NotEqual("", calc.TimeOfDay);
            Assert.NotEqual("", calc.Season);
        }
    }

    [Fact]
    public void Elanthian_date_roundtrips_to_the_same_real_instant()
    {
        var calc = new TimeCalc(0, 0, 0);
        var d = calc.DateFromDateTime(T0);
        var back = TimeCalc.LocalDateTimeFrom(d).ToUniversalTime();
        // Roisaen resolution is one real minute; allow the truncation slack.
        Assert.True(Math.Abs((back - T0).TotalSeconds) < 120,
            $"round-trip drifted {(back - T0).TotalSeconds}s");
    }

    [Fact]
    public void One_real_minute_is_one_roisaen()
    {
        var calc = new TimeCalc(0, 0, 0);
        calc.Recalculate(T0);
        var r0 = (long)calc.Date.Rois + calc.Date.Anlas * 30L;
        calc.Recalculate(T0.AddMinutes(1));
        var r1 = (long)calc.Date.Rois + calc.Date.Anlas * 30L;
        // +1 roisaen, allowing an anlas/day wrap.
        Assert.True(r1 == r0 + 1 || r1 == (r0 + 1) % 360);
    }

    // ── TimeCalc moons ──────────────────────────────────────────────────────────

    [Fact]
    public void Moon_state_flips_when_its_countdown_expires()
    {
        var calc = new TimeCalc(0, 0, 0);
        calc.Recalculate(T0);
        var wasUp = calc.KatambaIsUp;
        var wait = calc.KatambaCountdown;
        Assert.InRange(wait, 0, 21088);
        calc.Recalculate(T0.AddSeconds(wait + 5));
        Assert.NotEqual(wasUp, calc.KatambaIsUp);
    }

    [Fact]
    public void Calibration_reanchors_the_moon_and_persists_through_the_offset()
    {
        var calc = new TimeCalc(0, 0, 0);
        var delta = calc.Calibrate("Katamba", rose: true, T0);

        // After "Katamba slowly rises" the model must agree it is up with a
        // (near-)full visible window ahead (10603-2s fudge, small slack).
        Assert.True(calc.KatambaIsUp);
        Assert.InRange(calc.KatambaCountdown, 10595, 10601);

        // Re-creating the calc from the persisted offset reproduces the state.
        var rehydrated = new TimeCalc(calc.KatambaOffsetTotal, 0, 0);
        rehydrated.Recalculate(T0);
        Assert.True(rehydrated.KatambaIsUp);
        Assert.Equal(calc.KatambaCountdown, rehydrated.KatambaCountdown);

        // A second identical calibration is a no-op (already aligned).
        Assert.NotEqual(0, delta);
        Assert.Equal(0, calc.Calibrate("Katamba", rose: true, T0));
    }

    [Fact]
    public void Duration_between_close_instants_reads_in_roisaen()
    {
        var span = TimeCalc.DurationBetween(T0.AddMinutes(90), T0);
        Assert.True(span.IsRange);
        Assert.Equal(3, span.Anlas);    // 90 real minutes = 3 anlaen
        Assert.Equal(0, span.Rois);
    }

    // ── DRDateTime parsing ──────────────────────────────────────────────────────

    [Fact]
    public void DrDate_parses_the_genie4_command_format()
    {
        var d = DRDateTime.Parse("447-06-34 08:17");
        Assert.Equal(447, d.Year);
        Assert.Equal(6, d.Month);
        Assert.Equal(34, d.Day);
        Assert.Equal(8, d.Anlas);
        Assert.Equal(17, d.Rois);
        Assert.Equal("447-06-34 08:17", d.ToString());
        Assert.Throws<FormatException>(() => DRDateTime.Parse("447-11-34"));   // month > 10
    }

    // ── SkyState (observed overlay) ─────────────────────────────────────────────

    [Fact]
    public void Obs_sky_block_populates_moon_visibility()
    {
        var sky = new SkyState();
        var now = DateTimeOffset.UtcNow;
        Assert.True(sky.Feed("The following heavenly bodies are visible:", now));
        Assert.True(sky.Feed("Katamba is obscured by clouds.", now));
        Assert.True(sky.Feed("The planet Dawgolesh is unobscured by clouds.", now));
        Assert.True(sky.Feed("Yavash is below the horizon.", now));
        Assert.True(sky.Feed("Roundtime: 1 sec.", now));
        Assert.Equal(Visibility.Cloudy, sky.MoonVisibility("Katamba"));
        Assert.Equal(Visibility.BelowHorizon, sky.MoonVisibility("Yavash"));
        Assert.Equal(Visibility.Unknown, sky.MoonVisibility("Xibar"));
        Assert.Equal(1, sky.BodiesUp());   // the planet; moons excluded
    }

    /// <summary>#355: an unrecognised wording is a gap in the grammar, not the end
    /// of the block. Before the fix the scan stopped at the first non-canonical
    /// line, so everything after it was discarded — measured at 48% of bodies
    /// captured across 304 recorded blocks, with 32% capturing none at all.</summary>
    [Fact]
    public void Obs_sky_scan_survives_an_unrecognised_line()
    {
        var sky = new SkyState();
        var now = DateTimeOffset.UtcNow;
        sky.Feed("The following heavenly bodies are visible:", now);
        sky.Feed("Something the parser has never seen before happens.", now);
        sky.Feed("The planet Dawgolesh is unobscured by clouds.", now);
        sky.Feed("Katamba is obscured by clouds.", now);
        sky.Feed("Roundtime: 1 sec.", now);

        Assert.Equal(Visibility.Clear,  sky.Bodies["Dawgolesh"]);
        Assert.Equal(Visibility.Cloudy, sky.MoonVisibility("Katamba"));
    }

    /// <summary>#355: every partial-cloud wording observed in the recorded
    /// corpus resolves to the same body key the clear wording produces.</summary>
    [Theory]
    [InlineData("Two-thirds of the planet Szeldia is blocked by cloud cover above.", "Szeldia")]
    [InlineData("Two-thirds of the early afternoon sun is blocked by cloud cover above.", "early afternoon sun")]
    [InlineData("You focus your enhanced sight, through some of the cloud cover, upon the planet Yoakena.", "Yoakena")]
    [InlineData("A rather large cloud has covered nearly a third of the Wolf.", "Wolf")]
    [InlineData("One half of the Raven has been obscured by clouds above.", "Raven")]
    [InlineData("A cloud has obscured parts of the Magpie.", "Magpie")]
    [InlineData("Most of the Cat is obscured from view.", "Cat")]
    [InlineData("Clouds obscure the sky where the Heart should appear.", "Heart")]
    [InlineData("The clouds covering the planet Dawgolesh melt away under your gaze.", "Dawgolesh")]
    public void Obs_sky_reads_every_partial_cloud_wording(string line, string body)
    {
        var sky = new SkyState();
        var now = DateTimeOffset.UtcNow;
        sky.Feed("The following heavenly bodies are visible:", now);
        Assert.True(sky.Feed(line, now));
        Assert.Equal(Visibility.Cloudy, sky.Bodies[body]);
    }

    /// <summary>#355: a clouded moon keeps a real visibility, so the (cloudy)
    /// suffix on its row no longer silently disappears.</summary>
    [Fact]
    public void Obs_sky_partial_cloud_still_counts_a_moon()
    {
        var sky = new SkyState();
        var now = DateTimeOffset.UtcNow;
        sky.Feed("The following heavenly bodies are visible:", now);
        sky.Feed("A cloud has obscured parts of Katamba.", now);
        sky.Feed("Most of the planet Szeldia is obscured from view.", now);
        sky.Feed("", now);

        Assert.Equal(Visibility.Cloudy, sky.MoonVisibility("Katamba"));
        Assert.Equal(1, sky.BodiesUp());   // Szeldia; moons excluded
    }

    // ── extension end-to-end (fake host) ────────────────────────────────────────

    private sealed class FakeHost : IExtensionHost
    {
        public readonly ConcurrentDictionary<string, string> Vars = new();
        public readonly List<string> Echoed = new();
        public string Window = "";
        public IDictionary<string, string> Globals => Vars;
        public string ConfigDir { get; } =
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie-tt-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Echo(string text) => Echoed.Add(text);
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) => Window = content;
        public void Log(string message) { }
    }

    [Fact]
    public void Extension_paints_countdowns_and_publishes_time_globals()
    {
        var host = new FakeHost();
        var ext = new TimeTrackerExtension();
        ext.Initialize(host);
        try
        {
            ext.OnGameLine("Katamba slowly rises over the horizon.");
            ext.OnPrompt();

            Assert.Contains("Moons", host.Window);
            Assert.Contains("Katamba", host.Window);
            Assert.Contains("SET  in", host.Window);              // it just rose → counting down to set
            Assert.Equal("1", host.Vars["Time.isKatambaUp"]);
            Assert.True(long.Parse(host.Vars["Time.katambaSeconds"]) > 10000);
            Assert.NotEqual("", host.Vars["Time.timeOfDay"]);

            // The calibration must have been persisted to Time_Tracker.xml.
            var saved = TimeTrackerOptions.Load(host.ConfigDir);
            Assert.NotEqual(0, saved.KatambaOffset);
        }
        finally { ext.Shutdown(); }
    }

    [Fact]
    public void Extension_obs_sky_overlay_shows_on_the_moon_rows()
    {
        var host = new FakeHost();
        var ext = new TimeTrackerExtension();
        ext.Initialize(host);
        try
        {
            ext.OnGameLine("You scan the sky from horizon to horizon.");
            ext.OnGameLine("The sky is a patchwork of clouds.");
            ext.OnGameLine("The following heavenly bodies are visible:");
            ext.OnGameLine("Xibar is unobscured by clouds.");
            ext.OnGameLine("Roundtime: 1 sec.");
            ext.OnPrompt();

            Assert.Contains("Sky", host.Window);
            Assert.Contains("patchwork of clouds", host.Window);
            Assert.Contains("(clear)", host.Window);              // Xibar's observed overlay
            Assert.Contains("sky read", host.Window);
        }
        finally { ext.Shutdown(); }
    }

    [Fact]
    public void Time_and_timediff_commands_convert_both_directions()
    {
        var host = new FakeHost();
        var ext = new TimeTrackerExtension();
        ext.Initialize(host);
        try
        {
            Assert.True(ext.OnSlashCommand("/time 2026-07-05 12:00:00"));
            Assert.Contains(" is ", host.Echoed[^1]);

            host.Echoed.Clear();
            Assert.True(ext.OnSlashCommand("/time 447-06-34 08:17"));
            Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", host.Echoed[^1]);

            host.Echoed.Clear();
            Assert.True(ext.OnSlashCommand("/timediff 2026-01-01 00:00:00"));
            Assert.Contains("before now", host.Echoed[^1]);

            host.Echoed.Clear();
            Assert.True(ext.OnSlashCommand("/now"));
            Assert.StartsWith("It is the Year of the", host.Echoed[^1]);

            // Not ours: must fall through to the game.
            Assert.False(ext.OnSlashCommand("/timers"));
        }
        finally { ext.Shutdown(); }
    }
}
