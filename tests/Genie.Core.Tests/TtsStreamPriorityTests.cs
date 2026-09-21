using System;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Per-stream read-aloud priority (<c>ttsstreampriority</c>) — a CSV of
/// <c>stream:low|normal|high</c> overrides on top of the built-in urgency map
/// (whispers/death High; logons/atmospherics/familiar Low; else Normal).
/// The empty default MUST reproduce the previously-hardcoded map exactly —
/// existing users get no behavior change. Edited by <c>#tts priority</c>.
/// </summary>
public class TtsStreamPriorityTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    // NOTE: expected value is a string, not TtsUrgency — a Core enum inside
    // [InlineData] breaks xUnit attribute discovery when the tests run on a
    // rolled-forward runtime (CustomAttributeFormatException resolving
    // Genie.Core during discovery). Kept after the .NET 10 retarget: the
    // hazard is the roll-forward, not the old target framework.
    [Theory]
    [InlineData("whispers", "High")]
    [InlineData("death", "High")]
    [InlineData("logons", "Low")]
    [InlineData("atmospherics", "Low")]
    [InlineData("familiar", "Low")]
    [InlineData("talk", "Normal")]
    [InlineData("thoughts", "Normal")]
    [InlineData("main", "Normal")]
    [InlineData("someunknownstream", "Normal")]
    [InlineData("", "Normal")]
    public void EmptyDefault_ReproducesBuiltInMap(string stream, string expected)
    {
        var cfg = NewConfig();
        Assert.Equal("", cfg.TtsStreamPriorityRaw);
        Assert.Equal(Enum.Parse<TtsUrgency>(expected), cfg.TtsUrgencyFor(stream));
    }

    [Fact]
    public void Override_AppliesCaseAndWhitespaceInsensitive()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "Talk:HIGH,  combat : low ", showException: false);
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("talk"));
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("TALK"));
        Assert.Equal(TtsUrgency.Low, cfg.TtsUrgencyFor("combat"));
        // Non-overridden streams keep their defaults.
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("whispers"));
        Assert.Equal(TtsUrgency.Normal, cfg.TtsUrgencyFor("thoughts"));
    }

    [Fact]
    public void Override_CanDemoteABuiltInHighStream()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "whispers:normal", showException: false);
        Assert.Equal(TtsUrgency.Normal, cfg.TtsUrgencyFor("whispers"));
    }

    [Theory]
    [InlineData("talk")]           // no colon
    [InlineData("talk:loud")]      // unknown urgency word
    [InlineData(":high")]          // empty stream name
    [InlineData("talk:")]          // empty urgency
    [InlineData("::")]
    [InlineData(",,,")]
    public void MalformedPairs_AreIgnored_DefaultsStillApply(string csv)
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", csv, showException: false);
        Assert.Equal(TtsUrgency.Normal, cfg.TtsUrgencyFor("talk"));
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("whispers"));
    }

    [Fact]
    public void MalformedPair_DoesNotPoisonValidNeighbours()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "talk:loud,combat:high", showException: false);
        Assert.Equal(TtsUrgency.Normal, cfg.TtsUrgencyFor("talk"));   // bad pair ignored
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("combat"));   // good pair applied
    }

    [Fact]
    public void DuplicatePairs_LastWins()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "talk:low,talk:high", showException: false);
        Assert.Equal(TtsUrgency.High, cfg.TtsUrgencyFor("talk"));
    }

    [Fact]
    public void RoundTrips_LowercasedThroughGetSettingAndConfigPairs()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "Talk:High", showException: false);
        Assert.Equal("talk:high", cfg.GetSetting("ttsstreampriority"));
        // Present in the persisted pair list so settings.cfg saves it.
        bool found = false;
        foreach (var (k, v) in cfg.ToConfigPairs())
            if (k == "ttsstreampriority") { found = true; Assert.Equal("talk:high", v); }
        Assert.True(found);
    }

    [Fact]
    public void Overrides_ParsedViewMatchesRaw()
    {
        var cfg = NewConfig();
        cfg.SetSetting("ttsstreampriority", "talk:high,combat:low", showException: false);
        var map = cfg.TtsStreamPriorityOverrides();
        Assert.Equal(2, map.Count);
        Assert.Equal(TtsUrgency.High, map["talk"]);
        Assert.Equal(TtsUrgency.Low, map["combat"]);
    }

    [Fact]
    public void PrioritySettings_DoNotAffectStreamEnablement()
    {
        var cfg = NewConfig();
        cfg.TtsRead = true;
        cfg.SetSetting("ttsstreampriority", "combat:high", showException: false);
        // combat gains a priority override but is NOT in ttsreadstreams —
        // priority never implies enablement.
        Assert.False(cfg.TtsReadsStream("combat"));
        Assert.True(cfg.TtsReadsStream("whispers"));
    }
}
