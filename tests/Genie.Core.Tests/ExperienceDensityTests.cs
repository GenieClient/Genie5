using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Config;
using Genie.Core.Events;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public issue #125 — Experience-window line density (Genie 4 EXPTracker parity).
/// One <c>#config experiencedensity</c> value (0–2) drives the line format the
/// "Experience" dock panel renders. 0 = Full (rank, %, learning word, count);
/// 1 = drop the (n/34) count; 2 = numbers only. Levels 3–4 (short names) reserved.
/// </summary>
public class ExperienceDensityTests
{
    // "Small Edged", rank 142, 71%, mindstate 13 ("examining" in the canonical table).
    // Densities 2 (Numbers only) and 3 (Short names) carry the numeric mindstate (#144).
    [Theory]
    [InlineData(0, "Small Edged        142 71%  examining (13/34)")]
    [InlineData(1, "Small Edged        142 71%  examining")]
    [InlineData(2, "Small Edged        142 71%  13")]
    [InlineData(3, "Sm Edged     142 71%  13")]
    [InlineData(4, "Sm Edged     142")]
    public void FormatLine_RendersExpectedFormatPerDensity(int density, string expected)
    {
        var line = ExperienceExtension.FormatLine("Small Edged", 142, 71, 13, density);
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData("Small Edged", "Sm Edged")]        // multi-word: clip all but last
    [InlineData("Twohanded Blunt", "Tw Blunt")]
    [InlineData("Astrology", "Astrology")]          // single word: unchanged
    [InlineData("Scholarship", "Scholarship")]
    public void ShortName_ClipsLeadingWordsOnly(string input, string expected)
    {
        Assert.Equal(expected, ExperienceExtension.ShortName(input));
    }

    [Fact]
    public void FormatLine_FullAndNumbersOnly_ShareColumnPrefix()
    {
        // Name column (18) + rank (3) + percent (2) must line up across densities so
        // a switch doesn't ragged the list. Numbers-only then appends a numeric
        // mindstate column (#144).
        var full = ExperienceExtension.FormatLine("Astrology", 432, 15, 1, 0);
        var nums = ExperienceExtension.FormatLine("Astrology", 432, 15, 1, 2);
        Assert.StartsWith("Astrology          432 15%", full);
        Assert.Equal("Astrology          432 15%   1", nums);
    }

    [Theory]
    [InlineData(101, 5, 100, 34, "+0.71")]   // rank up one, percent down: fractional gain
    [InlineData(100, 34, 100, 34, "+0.00")]  // no change
    [InlineData(105, 0, 100, 0, "+5.00")]    // five whole ranks
    [InlineData(100, 50, 100, 20, "+0.30")]  // same rank, percent climb
    public void GainValue_TracksFractionalRankGain(int rank, int pct, int baseRank, int basePct, string expected)
    {
        var gain = ExperienceExtension.GainValue(rank, pct, baseRank, basePct);
        Assert.Equal(expected, ExperienceExtension.FormatGain(gain));
    }

    [Theory]
    [InlineData(0, 0, 45, "0:45")]           // under a minute
    [InlineData(0, 3, 7, "3:07")]            // minutes:seconds, zero-padded
    [InlineData(1, 2, 9, "1:02:09")]         // past an hour → H:MM:SS
    [InlineData(-5, 0, 0, "0:00")]           // negative (replay) clamps to zero
    public void FormatElapsed_FormatsSessionClock(int hours, int minutes, int seconds, string expected)
    {
        var t = new TimeSpan(hours, minutes, seconds);
        Assert.Equal(expected, ExperienceExtension.FormatElapsed(t));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("", false)]                  // unset defaults off
    public void Config_ExperienceTrackGain_RoundTrips(string input, bool expected)
    {
        var cfg = new GenieConfig(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));
        cfg.SetSetting("experiencetrackgain", input, showException: false);
        Assert.Equal(expected, cfg.ExperienceTrackGain);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("4", 4)]      // top live level
    [InlineData("7", 4)]      // above-range clamps down to Brief
    [InlineData("-1", 0)]     // below-range clamps up to Full
    [InlineData("", 0)]       // unset / unparseable falls back to Full
    [InlineData("abc", 0)]
    public void Config_ExperienceDensity_ClampsAndRoundTrips(string input, int expected)
    {
        var cfg = new GenieConfig(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));
        cfg.SetSetting("experiencedensity", input, showException: false);
        Assert.Equal(expected, cfg.ExperienceDensity);
        Assert.Equal(expected.ToString(), cfg.GetSetting("experiencedensity"));
    }

    // ── extension end-to-end render (fake host) — #144 header + gain ────────────

    /// <summary>Minimal host: feeds the extension the two <c>#config</c> keys it reads
    /// (density + track-gain) and captures the last SetWindow payload.</summary>
    private sealed class FakeHost : IExtensionHost
    {
        private readonly Dictionary<string, string> _config;
        public FakeHost(int density = 0, bool trackGain = false, bool g4Layout = false) => _config = new()
        {
            ["experiencedensity"]   = density.ToString(),
            ["experiencetrackgain"] = trackGain.ToString(),
            ["experienceg4layout"]  = g4Layout.ToString(),
        };
        public string Window = "";
        public IDictionary<string, string> Globals { get; } = new Dictionary<string, string>();
        public string ConfigDir { get; } = Path.GetTempPath();
        public void Echo(string text) { }
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) => Window = content;
        public void Log(string message) { }
        public string? GetConfig(string key) => _config.TryGetValue(key, out var v) ? v : null;
    }

    private static ComponentEvent Exp(string skill, string body) =>
        new($"exp {skill}", $"{skill}: {body}");

    [Fact]
    public void Render_ShowsLearningCountAndSessionClock()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Attunement", "550 73% dabbling"));
        ext.OnPrompt();

        Assert.Contains("Learning Skills: 1", host.Window);
        Assert.Contains("Session ", host.Window);   // clock starts at first datum
        Assert.Contains("Attunement", host.Window);
        // Default (G5) layout: summary is the header, above the skill row.
        Assert.True(host.Window.IndexOf("Learning Skills:", StringComparison.Ordinal)
                  < host.Window.IndexOf("Attunement", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_G4Layout_MovesSummaryBelowTheList()
    {
        var host = new FakeHost(g4Layout: true);
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Attunement", "550 73% dabbling"));
        ext.OnPrompt();

        // G4 EXPTracker look: the "Learning Skills" summary sits below the skill list.
        Assert.Contains("Learning Skills: 1", host.Window);
        Assert.True(host.Window.IndexOf("Attunement", StringComparison.Ordinal)
                  < host.Window.IndexOf("Learning Skills:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("", false)]                  // unset defaults off (keep G5 layout)
    public void Config_ExperienceG4Layout_RoundTrips(string input, bool expected)
    {
        var cfg = new GenieConfig(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));
        cfg.SetSetting("experienceg4layout", input, showException: false);
        Assert.Equal(expected, cfg.ExperienceG4Layout);
    }

    [Fact]
    public void Render_TrackGain_ShowsPerSkillDeltaAndSessionTotal()
    {
        var host = new FakeHost(trackGain: true);
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Attunement", "550 73% dabbling"));   // baseline 550/73
        ext.OnGameEvent(Exp("Attunement", "551 10% dabbling"));   // +1 rank, −63% → +0.37
        ext.OnPrompt();

        Assert.Contains("+0.37", host.Window);
        Assert.Contains("Total gained: +0.37 ranks", host.Window);
    }

    [Fact]
    public void Render_TrackGainOff_OmitsGainTotal()
    {
        var host = new FakeHost(trackGain: false);
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Attunement", "551 10% dabbling"));
        ext.OnPrompt();

        Assert.DoesNotContain("Total gained", host.Window);
    }

    [Fact]
    public void Render_CharacterSwitch_ResetsSessionBaseline()
    {
        var host = new FakeHost(trackGain: true);
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Attunement", "550 73% dabbling"));
        ext.OnGameEvent(Exp("Attunement", "560 0% dabbling"));   // +9.27 vs the pre-switch baseline
        ext.OnReset();                                           // new character clears baselines
        ext.OnGameEvent(Exp("Attunement", "560 50% dabbling"));  // becomes the fresh baseline
        ext.OnPrompt();

        // Baseline re-captured after the reset, so the session total starts at zero.
        Assert.Contains("Total gained: +0.00 ranks", host.Window);
    }

    // ── BRIEFEXP ON: numeric "[ n/34]" mindstate in the live pulse ─────────────
    // DR's per-character BRIEFEXP setting swaps the mindstate word for a number, so
    // the Experience window went stale between manual `exp` dumps on any character
    // that had it on — the dump always spells the mindstate out, which is why the
    // manual path kept working while the live pulses were silently dropped.

    [Fact]
    public void OnGameEvent_BriefExpOn_UpdatesWindow()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Stealth", "550 73% [ 5/34]"));   // BRIEFEXP ON pulse
        ext.OnPrompt();

        Assert.Contains("Learning Skills: 1", host.Window);
        Assert.Contains("Stealth", host.Window);
        // Rendered through the same table as the word form: mindstate 5 = "thinking".
        Assert.Contains("550 73%  thinking (5/34)", host.Window);
    }

    [Fact]
    public void OnGameEvent_BriefExpOn_PublishesSameGlobalsAsWordForm()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        ext.OnGameEvent(Exp("Small Edged", "142 71% [13/34]"));

        Assert.Equal("142", host.Globals["Small_Edged.Ranks"]);
        Assert.Equal("13",  host.Globals["Small_Edged.LearningRate"]);
        Assert.Equal("examining", host.Globals["Small_Edged.LearningRateName"]);
    }

    [Fact]
    public void OnGameEvent_BriefExpOn_RaisesSkillUpdated()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        (string Name, int Rank, int Pct, int Mind)? seen = null;
        ext.SkillUpdated += (n, r, p, m) => seen = (n, r, p, m);

        ext.OnGameEvent(Exp("Thievery", "1750 0% [34/34]"));   // mind lock, top of range

        Assert.Equal(("Thievery", 1750, 0, 34), seen);
    }

    [Fact]
    public void OnGameEvent_BriefExpOn_UsesComponentIdNameNotTheAbbreviation()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        // BRIEFEXP ON abbreviates the name in the body; the id keeps the full one.
        ext.OnGameEvent(new ComponentEvent("exp Inner Magic", "IM: 112 20% [32/34]"));
        ext.OnGameEvent(new ComponentEvent("exp Outdoorsmanship", "Outdoors: 243 76% [22/34]"));
        ext.OnPrompt();

        Assert.Contains("Inner Magic", host.Window);
        Assert.Contains("Outdoorsmanship", host.Window);
        Assert.Equal("32", host.Globals["Inner_Magic.LearningRate"]);
        Assert.DoesNotContain("IM ", host.Window);
        Assert.False(host.Globals.ContainsKey("IM.Ranks"));
    }

    [Fact]
    public void OnGameEvent_BriefExpOn_DoesNotDuplicateSkillsSeenInTheDump()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        // `exp` dump (full names) then a live brief pulse (abbreviated body) for the
        // same skills — one row each, not two.
        ext.OnGameLine("      Inner Magic:  112 20% clear        Locksmithing:  135 72% dabbling");
        ext.OnGameEvent(new ComponentEvent("exp Inner Magic", "IM: 112 20% [32/34]"));
        ext.OnGameEvent(new ComponentEvent("exp Locksmithing", "Locks: 135 72% [ 1/34]"));
        ext.OnPrompt();

        Assert.Contains("Learning Skills: 2", host.Window);
    }

    [Fact]
    public void OnGameLine_BriefExpOn_IsNotMisreadAsWordForm()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        // A frontend-echoed brief pulse arriving as plain text must parse as the
        // numeric form, not fall through to the word regex.
        ext.OnGameLine("Athletics:  300 22% [ 9/34]");
        ext.OnPrompt();

        Assert.Equal("9", host.Globals["Athletics.LearningRate"]);
        Assert.Contains("concentrating", host.Window);
    }

    [Fact]
    public void OnGameLine_WordForm_StillParsesUnderTheBriefCheck()
    {
        var host = new FakeHost();
        var ext  = new ExperienceExtension();
        ext.Initialize(host);

        // The `exp` dump packs two skills per line and spells the mindstate out.
        ext.OnGameLine("      Attunement:  550 73% dabbling        Stealth:  142 71% examining");
        ext.OnPrompt();

        Assert.Contains("Learning Skills: 2", host.Window);
        Assert.Equal("1",  host.Globals["Attunement.LearningRate"]);
        Assert.Equal("13", host.Globals["Stealth.LearningRate"]);
    }
}
