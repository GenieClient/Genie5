using System;
using System.Linq;
using Genie.Core.Health;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #277 — the <c>perceive health</c> / <c>&lt;patient&gt;'s injuries
/// include</c> text block.
///
/// <para>This is the source the injuries dialog cannot replace: any patient,
/// severity on the true 1–13 ladder, and all four Fresh/Scars ×
/// External/Internal axes at once, where the dialog gives self only, 1–3, and
/// whichever single axis the player's display mode selects.</para>
///
/// <para><b>Grammar provenance.</b> Verified against the community
/// <c>empath_heal.cmd</c> in Tirost/DR-Genie-Scripts, which carries the region
/// patterns and all thirteen severity rungs as separate <c>action</c> lines.
/// No raw capture of a perceive on another player exists yet (#277 records the
/// search), so the block framing is the part a capture would still settle.</para>
/// </summary>
public class PerceiveHealthParserTests
{
    private static PerceiveHealthParser New() =>
        new(() => new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Feed a whole block and return the completed chart.</summary>
    private static PatientHealth Run(params string[] lines)
    {
        var p = New();
        foreach (var l in lines) p.Feed(l);
        Assert.NotNull(p.Completed);
        return p.Completed!;
    }

    private static string[] SelfBlock(params string[] body) =>
        new[] { "Your injuries include..." }
            .Concat(body)
            .Concat(new[] { "You have some vitality." })
            .ToArray();

    // ── The severity ladder ──────────────────────────────────────────────────

    /// <summary>All thirteen rungs, in order, from the community script's own
    /// list. The ladder is the whole point of this parser — the dialog's three
    /// buckets are what it exists to replace.</summary>
    [Theory]
    [InlineData("insignificant",    WoundSeverity.Insignificant)]
    [InlineData("negligible",       WoundSeverity.Negligible)]
    [InlineData("minor",            WoundSeverity.Minor)]
    [InlineData("more than minor",  WoundSeverity.MoreThanMinor)]
    [InlineData("harmful",          WoundSeverity.Harmful)]
    [InlineData("very harmful",     WoundSeverity.VeryHarmful)]
    [InlineData("damaging",         WoundSeverity.Damaging)]
    [InlineData("very damaging",    WoundSeverity.VeryDamaging)]
    [InlineData("severe",           WoundSeverity.Severe)]
    [InlineData("very severe",      WoundSeverity.VerySevere)]
    [InlineData("devastating",      WoundSeverity.Devastating)]
    [InlineData("very devastating", WoundSeverity.VeryDevastating)]
    [InlineData("useless",          WoundSeverity.Useless)]
    public void Every_rung_parses_to_its_own_value(string word, WoundSeverity expected)
    {
        Assert.Equal(expected, PerceiveHealthParser.ParseSeverity(word));
        Assert.Equal(word, PerceiveHealthParser.SeverityName(expected));
    }

    /// <summary>The trap in this ladder: every qualified rung ENDS with its
    /// unqualified twin, so a naive contains-match reads "very severe" as
    /// "severe" — collapsing the top of the scale, which is exactly the range
    /// an empath needs to tell apart.</summary>
    [Theory]
    [InlineData("very harmful",     WoundSeverity.VeryHarmful)]
    [InlineData("very damaging",    WoundSeverity.VeryDamaging)]
    [InlineData("very severe",      WoundSeverity.VerySevere)]
    [InlineData("very devastating", WoundSeverity.VeryDevastating)]
    [InlineData("more than minor",  WoundSeverity.MoreThanMinor)]
    public void A_qualified_rung_is_not_read_as_its_unqualified_twin(string word, WoundSeverity expected)
        => Assert.Equal(expected, PerceiveHealthParser.ParseSeverity(word));

    [Fact]
    public void An_unknown_word_is_no_wound_rather_than_a_guess()
        => Assert.Equal(WoundSeverity.None, PerceiveHealthParser.ParseSeverity("scratched"));

    // ── Region grammar ───────────────────────────────────────────────────────

    /// <summary>The two region forms from the script:
    /// <c>Wounds to the (LEFT|RIGHT) (\w+):</c> and <c>Wounds to the (\w+):</c>.
    /// The sided form has to be tried first or it captures only "LEFT".</summary>
    [Fact]
    public void Sided_and_unsided_regions_both_parse()
    {
        var chart = Run(SelfBlock(
            "Wounds to the LEFT ARM:",
            "  Fresh External:  -- harmful",
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor"));

        Assert.Equal(WoundSeverity.Harmful, chart.Regions["leftArm"][InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Minor,   chart.Regions["head"][InjuryAxis.FreshExternal]);
    }

    /// <summary>Region ids are normalised to the SAME spellings the injuries
    /// dialog uses, so a perceive reading joins onto GameState.Injuries without
    /// a second lookup table.</summary>
    [Theory]
    [InlineData("Wounds to the RIGHT EYE:",  "rightEye")]
    [InlineData("Wounds to the LEFT HAND:",  "leftHand")]
    [InlineData("Wounds to the RIGHT LEG:",  "rightLeg")]
    [InlineData("Wounds to the ABDOMEN:",    "abdomen")]
    [InlineData("Wounds to the BACK:",       "back")]
    [InlineData("Wounds to the SKIN:",       "skin")]
    [InlineData("Wounds to the TAIL:",       "tail")]
    [InlineData("Wounds to the NERVES:",     "nsys")]
    public void Region_ids_match_the_injuries_dialog_spelling(string header, string expectedId)
    {
        var chart = Run(SelfBlock(header, "  Fresh External:  -- minor"));

        Assert.True(chart.Regions.ContainsKey(expectedId),
            $"expected region id '{expectedId}', got: {string.Join(", ", chart.Regions.Keys)}");
    }

    // ── The four axes ────────────────────────────────────────────────────────

    /// <summary>All four at once — the thing the dialog structurally cannot
    /// carry, and the reason #263 (Crutch) depends on this.</summary>
    [Fact]
    public void All_four_axes_are_captured_for_one_region()
    {
        var chart = Run(SelfBlock(
            "Wounds to the CHEST:",
            "  Fresh External:  a gash -- severe",
            "  Fresh Internal:  bruising -- minor",
            "  Scars External:  a scar -- negligible",
            "  Scars Internal:  scarring -- harmful"));

        var chest = chart.Regions["chest"];
        Assert.Equal(WoundSeverity.Severe,     chest[InjuryAxis.FreshExternal]);
        Assert.Equal(WoundSeverity.Minor,      chest[InjuryAxis.FreshInternal]);
        Assert.Equal(WoundSeverity.Negligible, chest[InjuryAxis.ScarExternal]);
        Assert.Equal(WoundSeverity.Harmful,    chest[InjuryAxis.ScarInternal]);

        Assert.Equal(WoundSeverity.Severe, chest.Worst);
        Assert.True(chest.HasFresh);
        Assert.True(chest.HasScar);
    }

    /// <summary>Both display forms, per #277: the bare word and the numeric
    /// suffix variant.</summary>
    [Fact]
    public void The_numeric_suffix_display_form_is_accepted()
    {
        var chart = Run(SelfBlock(
            "Wounds to the HEAD:",
            "  Fresh External:  a cut -- insignificant (1/13)"));

        Assert.Equal(WoundSeverity.Insignificant,
                     chart.Regions["head"][InjuryAxis.FreshExternal]);
    }

    /// <summary>A region DR did not mention is ABSENT, not healthy-with-a-zero:
    /// perceive lists only what is wounded, so absence carries meaning.</summary>
    [Fact]
    public void Unmentioned_regions_are_absent()
    {
        var chart = Run(SelfBlock("Wounds to the HEAD:", "  Fresh External:  -- minor"));

        Assert.True(chart.Regions.ContainsKey("head"));
        Assert.False(chart.Regions.ContainsKey("chest"));
        Assert.Single(chart.Regions);
    }

    // ── Patient identity ─────────────────────────────────────────────────────

    [Fact]
    public void A_self_block_is_keyed_as_self()
    {
        var chart = Run(SelfBlock("Wounds to the HEAD:", "  Fresh External:  -- minor"));

        Assert.Equal(PatientHealth.SelfPatient, chart.Patient);
        Assert.True(chart.IsSelf);
    }

    [Fact]
    public void A_patient_block_is_keyed_by_name()
    {
        var chart = Run(
            "Renucci's injuries include...",
            "Wounds to the LEFT LEG:",
            "  Fresh External:  -- very severe",
            "Renucci has some vitality.");

        Assert.Equal("Renucci", chart.Patient);
        Assert.False(chart.IsSelf);
        Assert.Equal(WoundSeverity.VerySevere, chart.Regions["leftLeg"][InjuryAxis.FreshExternal]);
    }

    /// <summary>"Your injuries include" is always the player, even when a
    /// presence line named somebody else first.</summary>
    [Fact]
    public void A_presence_line_does_not_override_a_self_block()
    {
        var chart = Run(
            "    The presence of Naper.",
            "Your injuries include...",
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor",
            "You have some vitality.");

        Assert.Equal(PatientHealth.SelfPatient, chart.Patient);
    }

    // ── Block framing ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Roundtime: 3 sec.")]
    [InlineData("You sense (something)")]
    public void A_terminator_closes_the_block(string terminator)
    {
        var p = New();
        p.Feed("Your injuries include...");
        p.Feed("Wounds to the HEAD:");
        p.Feed("  Fresh External:  -- minor");
        Assert.True(p.InBlock);

        p.Feed(terminator);

        Assert.False(p.InBlock);
        Assert.NotNull(p.Completed);
        Assert.Single(p.Completed!.Regions);
    }

    /// <summary>A terminator is NOT part of the block, so it must still reach
    /// its own consumers — a swallowed Roundtime line would stall every
    /// RT-gated script.</summary>
    [Fact]
    public void A_terminator_is_reported_as_ordinary_text()
    {
        var p = New();
        p.Feed("Your injuries include...");

        Assert.False(p.Feed("Roundtime: 3 sec."));
    }

    /// <summary>Ordinary game text outside a block is never claimed.</summary>
    [Fact]
    public void Text_outside_a_block_is_not_claimed()
    {
        var p = New();

        Assert.False(p.Feed("A kobold arrives."));
        Assert.False(p.Feed("Wounds to the HEAD:"));   // no block open
        Assert.Null(p.Completed);
    }

    /// <summary>An unrecognised line inside a block costs that line, not the
    /// rest of the chart — the mistake #355 was.</summary>
    [Fact]
    public void An_unrecognised_line_does_not_abort_the_block()
    {
        var chart = Run(SelfBlock(
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor",
            "Some wording nobody has seen before.",
            "Wounds to the CHEST:",
            "  Fresh External:  -- severe"));

        Assert.Equal(2, chart.Regions.Count);
        Assert.Equal(WoundSeverity.Severe, chart.Regions["chest"][InjuryAxis.FreshExternal]);
    }

    [Fact]
    public void A_new_block_replaces_the_previous_reading()
    {
        var p = New();
        foreach (var l in SelfBlock("Wounds to the HEAD:", "  Fresh External:  -- severe")) p.Feed(l);
        foreach (var l in SelfBlock("Wounds to the CHEST:", "  Fresh External:  -- minor")) p.Feed(l);

        Assert.Single(p.Completed!.Regions);
        Assert.True(p.Completed!.Regions.ContainsKey("chest"));
    }

    // ── Extras ───────────────────────────────────────────────────────────────

    [Fact]
    public void Poison_and_disease_markers_are_recorded()
    {
        var chart = Run(SelfBlock(
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor",
            "You have a nerve poison coursing through you.",
            "You have contracted a disease."));

        Assert.True(chart.IsPoisoned);
        Assert.True(chart.IsDiseased);
    }

    /// <summary>Vitality is derived as 100 - N per #277. The raw number is kept
    /// alongside it precisely because that inversion is the one part of the
    /// grammar no capture has confirmed yet.</summary>
    [Fact]
    public void Vitality_keeps_both_the_derived_and_the_raw_number()
    {
        var chart = Run(
            "Your injuries include...",
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor",
            "You have some vitality (30%).");

        Assert.Equal(30, chart.VitalityRawPercent);
        Assert.Equal(70, chart.VitalityPercent);
    }

    [Fact]
    public void A_block_with_no_vitality_line_reports_null_rather_than_zero()
    {
        var p = New();
        p.Feed("Your injuries include...");
        p.Feed("Wounds to the HEAD:");
        p.Feed("  Fresh External:  -- minor");
        p.Feed("Roundtime: 3 sec.");

        Assert.Null(p.Completed!.VitalityPercent);
        Assert.Null(p.Completed!.VitalityRawPercent);
    }

    // ── Ordering helpers ─────────────────────────────────────────────────────

    /// <summary>Worst-first is the order an empath heals in, and fresh and
    /// scarred are asked separately because they are different problems.</summary>
    [Fact]
    public void Fresh_and_scar_lists_are_separate_and_worst_first()
    {
        var chart = Run(SelfBlock(
            "Wounds to the HEAD:",
            "  Fresh External:  -- minor",
            "Wounds to the CHEST:",
            "  Fresh External:  -- devastating",
            "Wounds to the BACK:",
            "  Scars Internal:  -- severe"));

        Assert.Equal(new[] { "chest", "head" },
                     chart.FreshWorstFirst.Select(r => r.Region));
        Assert.Equal(new[] { "back" },
                     chart.ScarsWorstFirst.Select(r => r.Region));
    }

    [Fact]
    public void Reset_abandons_an_open_block()
    {
        var p = New();
        p.Feed("Your injuries include...");
        p.Feed("Wounds to the HEAD:");

        p.Reset();

        Assert.False(p.InBlock);
        Assert.False(p.Feed("  Fresh External:  -- minor"));
    }
}
