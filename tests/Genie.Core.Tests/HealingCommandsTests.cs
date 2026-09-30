using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Health;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #263 — the command lines behind the Healing panel's buttons, and the
/// per-spell mana / wait settings they read. The readings come from the #277
/// parser, fed the same block shapes its own tests use.
/// </summary>
public class HealingCommandsTests
{
    private static PatientHealth Read(params string[] lines)
    {
        var p = new PerceiveHealthParser(() => new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        foreach (var l in lines) p.Feed(l);
        return p.TakeCompleted() ?? throw new InvalidOperationException("block did not complete");
    }

    /// <summary>The #277 pipeline fixture: one patient, a fresh external wound
    /// and an internal scar on the same arm.</summary>
    private static PatientHealth Renucci() => Read(
        "Renucci's injuries include...",
        "Wounds to the LEFT ARM:",
        "  Fresh External:  a deep gash -- very severe",
        "  Scars Internal:  old scarring -- minor",
        "Wounds to the HEAD:",
        "  Fresh Internal:  bruising -- harmful",
        "Renucci has little vitality (60%).");

    private static HealingCommandBuilder Builder(HealingSettings? s = null, char cc = '#',
                                                 Func<string, string?>? vars = null)
        => new(s ?? new HealingSettings(), cc, vars);

    // ── Reading a patient ────────────────────────────────────────────────────

    [Fact]
    public void Perceive_is_bare_for_yourself_and_names_a_patient()
    {
        Assert.Equal(new[] { "perceive health" }, Builder().Perceive("").Lines);
        Assert.Equal(new[] { "perceive health" }, Builder().Perceive("self").Lines);
        Assert.Equal(new[] { "perceive health Naper" }, Builder().Perceive("Naper").Lines);
    }

    [Fact]
    public void Touch_names_the_patient_and_is_nothing_for_yourself()
    {
        Assert.Equal(new[] { "touch Naper" }, Builder().Touch("Naper").Lines);
        Assert.True(Builder().Touch("").IsEmpty);
        Assert.True(Builder().Touch("yourself").IsEmpty);
    }

    /// <summary>A typed name is one word of name characters — a separator or a
    /// command character must never ride along into a second command.</summary>
    [Theory]
    [InlineData("Naper;drop sword", "Naper")]   // separator stripped, leaving one token
    [InlineData("Naper #quit",      "Naper")]   // second word dropped
    [InlineData("  O'Riley ",       "O'Riley")]
    [InlineData("#$;",              "")]
    public void Patient_names_are_sanitised(string typed, string expected)
        => Assert.Equal(expected, HealingCommandBuilder.CleanPatient(typed));

    [Fact]
    public void A_name_with_nothing_usable_sends_nothing()
        => Assert.True(Builder().Touch("#;").IsEmpty);

    [Theory]
    [InlineData("leftArm",   "left arm")]
    [InlineData("rightEye",  "right eye")]
    [InlineData("rightFoot", "right foot")]
    [InlineData("nsys",      "nerves")]
    [InlineData("chest",     "chest")]
    [InlineData("skin",      "skin")]
    public void Region_ids_become_the_words_DR_takes(string id, string words)
        => Assert.Equal(words, HealingCommandBuilder.PartWords(id));

    // ── Healing a patient ────────────────────────────────────────────────────

    [Theory]
    [InlineData(InjuryAxis.FreshExternal, "take Naper left arm")]
    [InlineData(InjuryAxis.FreshInternal, "take Naper left arm internal")]
    [InlineData(InjuryAxis.ScarExternal,  "take Naper left arm scar")]
    [InlineData(InjuryAxis.ScarInternal,  "take Naper left arm internal scar")]
    public void A_patient_region_click_takes_that_wound_on_that_axis(InjuryAxis axis, string expected)
        => Assert.Equal(new[] { expected }, Builder().HealRegion("Naper", "leftArm", axis).Lines);

    /// <summary>Public #375: DR's own word order, as its injuries dialog writes
    /// it (<c>transfer Renucci internal left leg</c>) and as typed in the
    /// 2026-09-28 session (<c>transfer Renucci internal chest scar</c>, which
    /// DR answered with "internal chest scars are fully healed").</summary>
    [Theory]
    [InlineData(InjuryAxis.FreshInternal, "leftLeg", "transfer Renucci internal left leg")]
    [InlineData(InjuryAxis.ScarInternal,  "chest",   "transfer Renucci internal chest scar")]
    [InlineData(InjuryAxis.FreshInternal, "nsys",    "transfer Renucci internal nerves")]
    public void A_transfer_uses_DRs_internal_wording(InjuryAxis axis, string region, string expected)
        => Assert.Equal(new[] { expected }, Builder().Transfer("Renucci", region, axis).Lines);

    /// <summary>No external transfer line has been seen yet, so none is
    /// guessed; and there is nothing to transfer from yourself.</summary>
    [Fact]
    public void No_transfer_for_an_external_axis_or_for_yourself()
    {
        Assert.True(Builder().Transfer("Renucci", "chest", InjuryAxis.FreshExternal).IsEmpty);
        Assert.True(Builder().Transfer("Renucci", "chest", InjuryAxis.ScarExternal).IsEmpty);
        Assert.True(Builder().Transfer("", "chest", InjuryAxis.FreshInternal).IsEmpty);
        Assert.True(Builder().Transfer("self", "chest", InjuryAxis.FreshInternal).IsEmpty);
    }

    [Fact]
    public void A_transfer_cannot_carry_a_second_command_in_the_name()
        => Assert.Equal(new[] { "transfer Renucci internal chest" },
                        Builder().Transfer("Renucci;#quit", "chest", InjuryAxis.FreshInternal).Lines);

    [Fact]
    public void Quick_is_appended_to_takes()
    {
        var s = new HealingSettings { QuickTake = true };
        Assert.Equal(new[] { "take Naper chest scar quick" },
                     Builder(s).HealRegion("Naper", "chest", InjuryAxis.ScarExternal).Lines);
        Assert.Equal(new[] { "take Naper poison quick" },
                     Builder(s).TakeCondition("Naper", HealingCondition.Poison).Lines);
    }

    [Theory]
    [InlineData(HealingCondition.Vitality, "take Naper vitality")]
    [InlineData(HealingCondition.Poison,   "take Naper poison")]
    [InlineData(HealingCondition.Disease,  "take Naper disease")]
    public void Conditions_are_taken_from_a_patient(HealingCondition c, string expected)
        => Assert.Equal(new[] { expected }, Builder().TakeCondition("Naper", c).Lines);

    /// <summary>Take All: one click, one queued take per wound — fresh before
    /// scars, worst first — through the roundtime-gated #send queue.</summary>
    [Fact]
    public void Take_all_queues_one_take_per_wound_fresh_first_worst_first()
    {
        var lines = Builder().TakeAll(Renucci()).Lines;

        Assert.Equal(new[]
        {
            "#send take Renucci left arm",               // fresh, very severe (10)
            "#send take Renucci head internal",          // fresh, harmful (5)
            "#send take Renucci left arm internal scar", // scar, minor (3)
        }, lines);
    }

    [Fact]
    public void Take_all_uses_the_players_command_character()
        => Assert.All(Builder(cc: '!').TakeAll(Renucci()).Lines,
                      l => Assert.StartsWith("!send take Renucci", l));

    [Fact]
    public void Take_all_is_nothing_for_your_own_chart_or_a_healthy_one()
    {
        var self = Read("Your injuries include...",
                        "Wounds to the HEAD:",
                        "  Fresh External:  -- minor",
                        "You have some vitality.");
        var healthy = Read("Naper's injuries include...", "Naper has some vitality.");

        Assert.True(Builder().TakeAll(self).IsEmpty);
        Assert.True(Builder().TakeAll(healthy).IsEmpty);
        Assert.True(Builder().TakeAll(null).IsEmpty);
    }

    // ── Healing yourself ─────────────────────────────────────────────────────

    [Fact]
    public void Your_own_fresh_wound_prepares_heal_wounds_and_casts_at_the_part()
    {
        var s = new HealingSettings();
        s.SetMana(HealingSpell.HW, 15);
        s.SetDelay(HealingSpell.HW, 4);

        Assert.Equal(new[] { "prep hw 15", "#send 4 cast left arm" },
                     Builder(s).HealRegion("", "leftArm", InjuryAxis.FreshExternal).Lines);
    }

    [Fact]
    public void Your_own_scar_prepares_heal_scars()
    {
        var s = new HealingSettings();
        s.SetMana(HealingSpell.HS, 8);
        s.SetDelay(HealingSpell.HS, 2.5);

        Assert.Equal(new[] { "prep hs 8", "#send 2.5 cast chest" },
                     Builder(s).HealRegion("self", "chest", InjuryAxis.ScarInternal).Lines);
    }

    /// <summary>No wait configured → prepare only. The cast comes back as a
    /// pending line for the player's own Cast click; nothing queues it.</summary>
    [Fact]
    public void Without_a_wait_only_the_prep_is_sent_and_the_cast_waits_for_a_click()
    {
        var s = new HealingSettings();
        s.SetMana(HealingSpell.HW, 10);

        var action = Builder(s).HealRegion("", "head", InjuryAxis.FreshExternal);

        Assert.Equal(new[] { "prep hw 10" }, action.Lines);
        Assert.Equal("cast head", action.PendingCast);
    }

    [Fact]
    public void No_mana_prepares_at_the_minimum()
        => Assert.Equal(new[] { "prep regen" }, Builder().Cast(HealingSpell.REGE, null).Lines);

    [Theory]
    [InlineData(HealingCondition.Vitality, "prep vh 20")]
    [InlineData(HealingCondition.Poison,   "prep fp 20")]
    [InlineData(HealingCondition.Disease,  "prep cd 20")]
    public void Your_own_conditions_cast_the_matching_spell(HealingCondition c, string prep)
    {
        var s = new HealingSettings();
        foreach (var sp in new[] { HealingSpell.VH, HealingSpell.FP, HealingSpell.CD }) s.SetMana(sp, 20);

        var action = Builder(s).TakeCondition("", c);
        Assert.Equal(new[] { prep }, action.Lines);
        Assert.Equal("cast", action.PendingCast);
    }

    /// <summary>All nine slots, with the prep names DR takes.</summary>
    [Theory]
    [InlineData(HealingSpell.BS,   "prep bs")]
    [InlineData(HealingSpell.CD,   "prep cd")]
    [InlineData(HealingSpell.FP,   "prep fp")]
    [InlineData(HealingSpell.HEAL, "prep heal")]
    [InlineData(HealingSpell.HS,   "prep hs")]
    [InlineData(HealingSpell.HW,   "prep hw")]
    [InlineData(HealingSpell.REFR, "prep refresh")]
    [InlineData(HealingSpell.REGE, "prep regen")]
    [InlineData(HealingSpell.VH,   "prep vh")]
    public void Every_spell_button_prepares_its_spell(HealingSpell spell, string prep)
        => Assert.Equal(new[] { prep }, Builder().Cast(spell, null).Lines);

    [Fact]
    public void Stop_clears_the_queue()
        => Assert.Equal(new[] { "#queue clear" }, Builder().StopQueue().Lines);

    // ── Settings ─────────────────────────────────────────────────────────────

    /// <summary>Genie 4 carry-over: an unset spell reads its GCTextBox*
    /// variable; a value set in the panel wins over it.</summary>
    [Fact]
    public void An_unset_spell_falls_back_to_its_Genie_4_variable()
    {
        var vars = new Dictionary<string, string>
        {
            ["GCTextBoxManaHW"]  = "12",
            ["GCTextBoxDelayHW"] = "3",
            ["GCTextBoxManaHS"]  = "9",
        };
        var s = new HealingSettings();
        s.SetMana(HealingSpell.HS, 30);

        var b = Builder(s, vars: n => vars.TryGetValue(n, out var v) ? v : null);

        Assert.Equal(new[] { "prep hw 12", "#send 3 cast neck" },
                     b.HealRegion("", "neck", InjuryAxis.FreshExternal).Lines);
        Assert.Equal("prep hs 30", b.HealRegion("", "neck", InjuryAxis.ScarExternal).Lines[0]);
    }

    [Fact]
    public void Spell_variables_keep_the_Genie_4_names()
    {
        var hw = HealingSpells.Get(HealingSpell.HW);
        Assert.Equal("GCTextBoxManaHW",  hw.ManaVariable);
        Assert.Equal("GCTextBoxDelayHW", hw.DelayVariable);
        Assert.Equal(new[] { "BS", "CD", "FP", "HEAL", "HS", "HW", "REFR", "REGE", "VH" },
                     HealingSpells.All.Select(s => s.Abbreviation).OrderBy(a => a, StringComparer.Ordinal));
    }

    [Fact]
    public void Settings_round_trip_through_the_file()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "g5-healing-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, HealingSettings.FileName);
        try
        {
            var a = new HealingSettings { QuickTake = true };
            a.SetMana(HealingSpell.HW, 15);
            a.SetDelay(HealingSpell.HW, 4.5);
            a.SetMana(HealingSpell.VH, 40);
            Assert.True(a.Save(path));

            var b = new HealingSettings();
            Assert.True(b.Load(path));
            Assert.True(b.QuickTake);
            Assert.Equal(15,  b.GetMana(HealingSpell.HW));
            Assert.Equal(4.5, b.GetDelay(HealingSpell.HW));
            Assert.Equal(40,  b.GetMana(HealingSpell.VH));
            Assert.Null(b.GetDelay(HealingSpell.VH));
            Assert.Null(b.GetMana(HealingSpell.BS));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>A missing file clears (a different character's mana must not
    /// carry over); a torn one leaves the values alone.</summary>
    [Fact]
    public void A_missing_file_clears_and_a_torn_file_does_not()
    {
        var dir  = Path.Combine(Path.GetTempPath(), "g5-healing-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, HealingSettings.FileName);
        try
        {
            var s = new HealingSettings();
            s.SetMana(HealingSpell.HW, 15);

            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{ not json");
            Assert.False(s.Load(path));
            Assert.Equal(15, s.GetMana(HealingSpell.HW));

            File.Delete(path);
            s.Load(path);
            Assert.Null(s.GetMana(HealingSpell.HW));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Set_values_mirror_out_under_their_variable_names_and_unset_ones_do_not()
    {
        var s = new HealingSettings();
        s.SetMana(HealingSpell.HW, 15);
        s.SetDelay(HealingSpell.HW, 4);

        var written = new Dictionary<string, string>();
        s.MirrorTo((n, v) => written[n] = v);

        Assert.Equal("15", written["GCTextBoxManaHW"]);
        Assert.Equal("4",  written["GCTextBoxDelayHW"]);
        Assert.False(written.ContainsKey("GCTextBoxManaHS"));
    }
}
