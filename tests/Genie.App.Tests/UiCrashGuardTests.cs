using System;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The UI thread's last-resort handler survives a stray exception but not a
/// burst of them (public #398 / #399).
/// </summary>
public class UiCrashGuardTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_single_failure_is_survived()
    {
        Assert.True(new UiCrashGuard().ShouldSurvive(T0));
    }

    [Fact]
    public void A_burst_stops_being_survived()
    {
        var guard = new UiCrashGuard();
        for (var i = 0; i < UiCrashGuard.MaxPerWindow; i++)
            Assert.True(guard.ShouldSurvive(T0.AddMilliseconds(i)));

        Assert.False(guard.ShouldSurvive(T0.AddMilliseconds(UiCrashGuard.MaxPerWindow)));
    }

    [Fact]
    public void Failures_spread_over_time_are_all_survived()
    {
        var guard = new UiCrashGuard();
        var gap   = UiCrashGuard.Window + TimeSpan.FromSeconds(1);
        for (var i = 0; i < UiCrashGuard.MaxPerWindow * 4; i++)
            Assert.True(guard.ShouldSurvive(T0 + gap * i));
    }

    [Fact]
    public void A_burst_is_forgiven_once_the_window_has_passed()
    {
        var guard = new UiCrashGuard();
        for (var i = 0; i <= UiCrashGuard.MaxPerWindow; i++) guard.ShouldSurvive(T0);

        Assert.True(guard.ShouldSurvive(T0 + UiCrashGuard.Window + TimeSpan.FromSeconds(1)));
    }
}
