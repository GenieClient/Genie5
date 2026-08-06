using System;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Semantics of <c>#config activitytimeout</c> — the server-activity watchdog
/// window that closes the "DR ended the session but never closed the socket"
/// hole (observed live 2026-08-04/06: $connected stuck at 1 all night). The
/// default must be ON (a positive window), 0 must mean off, and typo-scale
/// values must clamp into the safe 60s–1h band: DR's server heartbeats at
/// least every ~30s on a healthy link, so anything under a minute would
/// false-positive on normal quiet stretches.
/// </summary>
public class ActivityTimeoutConfigTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    [Fact]
    public void DefaultIsFiveMinutes()
    {
        Assert.Equal(300, NewConfig().ActivityTimeout);
    }

    [Fact]
    public void ZeroDisablesTheWatchdog()
    {
        var cfg = NewConfig();
        cfg.SetSetting("activitytimeout", "0", showException: false);
        Assert.Equal(0, cfg.ActivityTimeout);
    }

    [Theory]
    [InlineData("600", 600)]    // in-band value kept as-is
    [InlineData("60", 60)]      // lower bound inclusive
    [InlineData("3600", 3600)]  // upper bound inclusive
    [InlineData("30", 60)]      // below the heartbeat-safe floor → clamps up
    [InlineData("99999", 3600)] // absurdly large → clamps down
    public void ValuesClampIntoTheSafeBand(string input, int expected)
    {
        var cfg = NewConfig();
        cfg.SetSetting("activitytimeout", input, showException: false);
        Assert.Equal(expected, cfg.ActivityTimeout);
    }

    [Fact]
    public void NegativeInputIsIgnored()
    {
        var cfg = NewConfig();
        cfg.SetSetting("activitytimeout", "-5", showException: false);
        Assert.Equal(300, cfg.ActivityTimeout);
    }

    [Fact]
    public void RoundTripsThroughGetSetting()
    {
        var cfg = NewConfig();
        cfg.SetSetting("activitytimeout", "900", showException: false);
        Assert.Equal("900", cfg.GetSetting("activitytimeout"));
    }
}
