using System;
using Genie.Core.Extensions.Builtin;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>Rested-EXP gauge: parsing the <c>exp rexp</c> line (real shapes from
/// Renucci's logs) and the green / yellow / red rules.</summary>
public class RestedExpGaugeTests
{
    private static RestedExpReading Parse(string line)
    {
        Assert.True(RestedExpGauge.TryParse(line, out var r), line);
        return r;
    }

    [Theory]
    [InlineData("5:03 hours", 5, 3)]
    [InlineData("6 hours", 6, 0)]
    [InlineData("1:39 hour", 1, 39)]
    [InlineData("23:29 hours", 23, 29)]
    [InlineData("16 hours", 16, 0)]
    public void ParseDuration_HandlesGameFormats(string text, int h, int m) =>
        Assert.Equal(new TimeSpan(h, m, 0), RestedExpGauge.ParseDuration(text));

    [Theory]
    [InlineData("none")]
    [InlineData("less than a minute")]
    [InlineData("")]
    [InlineData("garbage")]
    public void ParseDuration_UnknownIsZero(string text) =>
        Assert.Equal(TimeSpan.Zero, RestedExpGauge.ParseDuration(text));

    [Fact]
    public void TryParse_ReadsAllThreeNumbers()
    {
        var r = Parse("Rested EXP Stored: 5:03 hours  Usable This Cycle: 5:51 hours  Cycle Refreshes: 4:16 hours");
        Assert.Equal(new TimeSpan(5, 3, 0), r.Stored);
        Assert.Equal(new TimeSpan(5, 51, 0), r.Usable);
        Assert.Equal(new TimeSpan(4, 16, 0), r.Refresh);
    }

    [Fact]
    public void TryParse_StripsTagsAndAcceptsNone()
    {
        var r = Parse("<preset id='x'>Rested EXP Stored: none  Usable This Cycle: 1:46 hour  Cycle Refreshes: 14:27 hours</preset>");
        Assert.Equal(TimeSpan.Zero, r.Stored);
        Assert.Equal(new TimeSpan(1, 46, 0), r.Usable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Overall state of mind: clear")]
    public void TryParse_RejectsOtherLines(string? line) =>
        Assert.False(RestedExpGauge.TryParse(line, out _));

    [Fact]
    public void Spendable_IsTheSmallerOfBankAndAllowance()
    {
        var r = Parse("Rested EXP Stored: 5:03 hours  Usable This Cycle: 1:46 hour  Cycle Refreshes: 4:16 hours");
        Assert.Equal(new TimeSpan(1, 46, 0), r.Spendable);
    }

    [Theory]
    [InlineData("none",      "6 hours",   RestedExpLevel.Red)]     // empty bank
    [InlineData("6 hours",   "none",      RestedExpLevel.Red)]     // allowance spent (10/9 log)
    [InlineData("0:35 hours", "6 hours",  RestedExpLevel.Yellow)]
    [InlineData("5:03 hours", "0:59 hours", RestedExpLevel.Yellow)]
    [InlineData("1 hour",    "6 hours",   RestedExpLevel.Green)]   // boundary: exactly 1:00 is green
    [InlineData("5:03 hours", "5:51 hours", RestedExpLevel.Green)]
    public void LevelOf_FollowsSpendable(string stored, string usable, RestedExpLevel expected)
    {
        var r = Parse($"Rested EXP Stored: {stored}  Usable This Cycle: {usable}  Cycle Refreshes: 4:16 hours");
        Assert.Equal(expected, RestedExpGauge.LevelOf(r));
    }

    [Fact]
    public void FillOf_IsSpendableOverCap_AndClamped()
    {
        var half = Parse("Rested EXP Stored: 3 hours  Usable This Cycle: 6 hours  Cycle Refreshes: 1 hour");
        Assert.Equal(0.5, RestedExpGauge.FillOf(half), 3);

        var over = Parse("Rested EXP Stored: 8 hours  Usable This Cycle: 8 hours  Cycle Refreshes: 1 hour");
        Assert.Equal(1.0, RestedExpGauge.FillOf(over));

        var allowanceLimited = Parse("Rested EXP Stored: 5:03 hours  Usable This Cycle: 1:46 hour  Cycle Refreshes: 4:16 hours");
        Assert.Equal(1.7667 / 6.0, RestedExpGauge.FillOf(allowanceLimited), 3);
    }

    [Fact]
    public void Format_And_Describe_ArePlainLanguage()
    {
        Assert.Equal("1:46", RestedExpGauge.Format(new TimeSpan(1, 46, 0)));
        Assert.Equal("0:00", RestedExpGauge.Format(TimeSpan.FromMinutes(-5)));

        var spent = Parse("Rested EXP Stored: 6 hours  Usable This Cycle: none  Cycle Refreshes: less than a minute");
        var tip = RestedExpGauge.Describe(spent, RestedExpLevel.Red);
        Assert.Contains("allowance is spent", tip);
        Assert.Contains("6:00 banked", tip);

        var empty = Parse("Rested EXP Stored: none  Usable This Cycle: 1:46 hour  Cycle Refreshes: 14:27 hours");
        Assert.Contains("Bank is empty", RestedExpGauge.Describe(empty, RestedExpLevel.Red));
    }
}
