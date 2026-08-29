using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #272 — Genie 4 EXPTracker parity: sort modes
/// (<c>#config experiencesort</c> 0–3 + the user-orderable category list for
/// "Left to Right"), pulse echo (<c>experienceecho</c> → "Learned:"/"Pulsed:"
/// lines per prompt), and rested EXP (<c>experiencerested</c> + the
/// $RestedEXP.* globals from the <c>exp rexp</c> component).
/// </summary>
public class ExperienceSortEchoRestedTests
{
    private sealed class FakeHost : IExtensionHost
    {
        public IDictionary<string, string> Globals { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Config { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Echoed { get; } = new();
        public List<bool> EchoedParseFlags { get; } = new();
        public string? Window { get; private set; }

        public void Echo(string text) => Echoed.Add(text);
        public void EchoRouted(string text, bool display, bool parse)
        {
            Echoed.Add(text);
            EchoedParseFlags.Add(parse);
        }
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) => Window = content;
        public string ConfigDir => "";
        public void Log(string message) { }
        public string? GetConfig(string key) => Config.TryGetValue(key, out var v) ? v : null;
    }

    private static (ExperienceExtension Ext, FakeHost Host) NewTracker(params (string k, string v)[] config)
    {
        var host = new FakeHost();
        foreach (var (k, v) in config) host.Config[k] = v;
        var ext = new ExperienceExtension();
        ext.Initialize(host);
        return (ext, host);
    }

    private static void Pulse(ExperienceExtension ext, string skill, int rank, int pct, string mindstate)
        => ext.OnGameEvent(new ComponentEvent($"exp {skill}", $"{skill}: {rank} {pct}% {mindstate}"));

    /// <summary>Skill names in panel order (rows that start with a known skill name).</summary>
    private static List<string> RenderedSkills(FakeHost host, params string[] skills)
        => (host.Window ?? "").Split('\n')
            .Select(l => skills.FirstOrDefault(s => l.StartsWith(s, StringComparison.Ordinal)))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

    // ── Master-order table sanity ─────────────────────────────────────────────

    [Theory]
    [InlineData("Shield Usage", "armor")]
    [InlineData("Twohanded Blunt", "weapons")]
    [InlineData("Attunement", "magic")]
    [InlineData("Thanatology", "survival")]
    [InlineData("Trading", "lore")]
    [InlineData("Totally New Skill", "")]
    public void CategoryOf_MapsSkillsToTheirG4Band(string skill, string category)
        => Assert.Equal(category, ExperienceExtension.CategoryOf(skill));

    [Fact]
    public void OrderOf_UnknownSkillSortsAfterEveryKnownOne()
        => Assert.True(ExperienceExtension.OrderOf("Totally New Skill") > ExperienceExtension.OrderOf("Trading"));

    // ── Sort modes ────────────────────────────────────────────────────────────

    [Fact]
    public void Sort0_AlphabeticalByName()
    {
        var (ext, host) = NewTracker(("experiencesort", "0"));
        Pulse(ext, "Evasion",      100, 10, "learning");     // mind 3
        Pulse(ext, "Attunement",   200, 20, "mind lock");    // mind 34
        Pulse(ext, "Small Edged",  300, 30, "dabbling");     // mind 1
        ext.OnPrompt();
        Assert.Equal(new[] { "Attunement", "Evasion", "Small Edged" },
                     RenderedSkills(host, "Attunement", "Evasion", "Small Edged"));
    }

    [Fact]
    public void Sort1_LeftToRight_GroupsByG4CategoryOrder()
    {
        var (ext, host) = NewTracker(("experiencesort", "1"));
        Pulse(ext, "Trading",      100, 10, "learning");     // lore
        Pulse(ext, "Evasion",      100, 10, "dabbling");     // survival
        Pulse(ext, "Shield Usage", 100, 10, "dabbling");     // armor
        Pulse(ext, "Small Edged",  100, 10, "mind lock");    // weapons
        ext.OnPrompt();
        Assert.Equal(new[] { "Shield Usage", "Small Edged", "Evasion", "Trading" },
                     RenderedSkills(host, "Shield Usage", "Small Edged", "Evasion", "Trading"));
    }

    [Fact]
    public void Sort1_CustomCategoryOrder_Reorders()
    {
        var (ext, host) = NewTracker(("experiencesort", "1"),
                                     ("experiencesortorder", "magic, weapons, armor, survival, lore"));
        Pulse(ext, "Shield Usage", 100, 10, "dabbling");
        Pulse(ext, "Attunement",   100, 10, "dabbling");
        Pulse(ext, "Small Edged",  100, 10, "dabbling");
        ext.OnPrompt();
        Assert.Equal(new[] { "Attunement", "Small Edged", "Shield Usage" },
                     RenderedSkills(host, "Attunement", "Small Edged", "Shield Usage"));
    }

    [Fact]
    public void Sort1_UnknownSkillSortsLast()
    {
        var (ext, host) = NewTracker(("experiencesort", "1"));
        Pulse(ext, "Zzz New Skill", 100, 10, "dabbling");
        Pulse(ext, "Trading",       100, 10, "dabbling");
        ext.OnPrompt();
        Assert.Equal(new[] { "Trading", "Zzz New Skill" },
                     RenderedSkills(host, "Trading", "Zzz New Skill"));
    }

    [Fact]
    public void Sort3_LearningRateLowToHigh()
    {
        var (ext, host) = NewTracker(("experiencesort", "3"));
        Pulse(ext, "Evasion",     100, 10, "mind lock");
        Pulse(ext, "Attunement",  100, 10, "dabbling");
        ext.OnPrompt();
        Assert.Equal(new[] { "Attunement", "Evasion" },
                     RenderedSkills(host, "Attunement", "Evasion"));
    }

    [Fact]
    public void DefaultSort_IsLearningRateHighToLow_TheHistoricalOrder()
    {
        var (ext, host) = NewTracker();   // no experiencesort set
        Pulse(ext, "Evasion",     100, 10, "dabbling");
        Pulse(ext, "Attunement",  100, 10, "mind lock");
        ext.OnPrompt();
        Assert.Equal(new[] { "Attunement", "Evasion" },
                     RenderedSkills(host, "Attunement", "Evasion"));
    }

    // ── EchoExp ───────────────────────────────────────────────────────────────

    [Fact]
    public void EchoExp_On_FlushesLearnedAndPulsedLinesAtPrompt()
    {
        var (ext, host) = NewTracker(("experienceecho", "True"));
        Pulse(ext, "Evasion",    100, 10, "learning");    // new: 0 → 3 = Learned(+3)
        Pulse(ext, "Attunement", 200, 20, "dabbling");    // new: 0 → 1 = Learned(+1)
        ext.OnPrompt();
        Pulse(ext, "Evasion",    100, 10, "dabbling");    // 3 → 1 = Pulsed(-2)
        ext.OnPrompt();

        Assert.Equal("Learned: Evasion(+3), Attunement(+1)", host.Echoed[0]);
        Assert.Equal("Pulsed: Evasion(-2)", host.Echoed[1]);
    }

    [Fact]
    public void EchoExp_Off_EchoesNothingAndDoesNotBacklog()
    {
        var (ext, host) = NewTracker();   // echo off (default)
        Pulse(ext, "Evasion", 100, 10, "learning");
        ext.OnPrompt();
        Assert.Empty(host.Echoed);

        // Turning it on later must not replay the pre-toggle pulses.
        host.Config["experienceecho"] = "True";
        ext.OnPrompt();
        Assert.Empty(host.Echoed);
    }

    [Fact]
    public void EchoExp_DefaultIsDisplayOnly_ParseLegOff()
    {
        // The 2026-08-29 flood: echo lines fed the parse pipeline every combat
        // prompt and a running script's actions fired per pulse until DR
        // disconnected for flooding. The trigger feed is opt-in now.
        var (ext, host) = NewTracker(("experienceecho", "True"));
        Pulse(ext, "Evasion", 100, 10, "learning");
        ext.OnPrompt();
        Assert.Single(host.Echoed);
        Assert.All(host.EchoedParseFlags, p => Assert.False(p));
    }

    [Fact]
    public void EchoExp_ParseLegOn_WithExperienceEchoParse()
    {
        var (ext, host) = NewTracker(("experienceecho", "True"),
                                     ("experienceechoparse", "True"));
        Pulse(ext, "Evasion", 100, 10, "learning");
        ext.OnPrompt();
        Assert.Single(host.Echoed);
        Assert.All(host.EchoedParseFlags, p => Assert.True(p));
    }

    [Fact]
    public void EchoExp_IdenticalPulse_DoesNotEcho()
    {
        var (ext, host) = NewTracker(("experienceecho", "True"));
        Pulse(ext, "Evasion", 100, 10, "learning");
        ext.OnPrompt();
        host.Echoed.Clear();
        Pulse(ext, "Evasion", 100, 10, "learning");       // no change
        ext.OnPrompt();
        Assert.Empty(host.Echoed);
    }

    // ── Rested EXP ────────────────────────────────────────────────────────────

    private const string RexpBody =
        "Rested EXP Stored: 5:58 hours  Usable This Cycle: 5:56 hours  Cycle Refreshes: 17:11 hours";

    [Fact]
    public void Rexp_PopulatesGlobals_Always()
    {
        var (ext, host) = NewTracker();   // display toggle off — globals still fill
        ext.OnGameEvent(new ComponentEvent("exp rexp", RexpBody));
        Assert.Equal("5:58",  host.Globals["RestedEXP.Stored"]);
        Assert.Equal("5:56",  host.Globals["RestedEXP.Usable"]);
        Assert.Equal("17:11", host.Globals["RestedEXP.Refresh"]);
    }

    [Fact]
    public void Rexp_RendersSummaryLine_OnlyWhenEnabled()
    {
        var (ext, host) = NewTracker(("experiencerested", "True"));
        Pulse(ext, "Evasion", 100, 10, "learning");
        ext.OnGameEvent(new ComponentEvent("exp rexp", RexpBody));
        ext.OnPrompt();
        Assert.Contains("Rested: stored 5:58 · usable 5:56 · refreshes 17:11", host.Window);

        var (ext2, host2) = NewTracker();   // toggle off
        Pulse(ext2, "Evasion", 100, 10, "learning");
        ext2.OnGameEvent(new ComponentEvent("exp rexp", RexpBody));
        ext2.OnPrompt();
        Assert.DoesNotContain("Rested:", host2.Window);
    }

    [Theory]
    [InlineData("5:58 hours", "5:58")]
    [InlineData("6 hours", "6")]
    [InlineData("1 hour", "1")]
    [InlineData("17:11", "17:11")]
    public void StripHours_DropsTheUnitSuffix(string input, string expected)
        => Assert.Equal(expected, ExperienceExtension.StripHours(input));
}
