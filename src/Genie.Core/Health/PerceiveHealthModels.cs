using System;
using System.Collections.Generic;
using System.Linq;

namespace Genie.Core.Health;

/// <summary>
/// DragonRealms' wound-severity ladder as reported by <c>perceive health</c> —
/// thirteen rungs, not the three the injuries dialog can express (public #277).
///
/// <para><b>This is deliberately NOT Genie 4 Crutch's mapping.</b> That one
/// collapses <c>severe</c> and <c>very severe</c> into one bucket, folds
/// <c>devastating</c> / <c>very devastating</c> / <c>useless</c> into another,
/// and invents a rung DR does not have ("insignificant to negligible") — so the
/// four worst severities in the game render identically, which is precisely the
/// range an empath needs to tell apart.</para>
/// </summary>
public enum WoundSeverity
{
    /// <summary>No wound reported for this axis.</summary>
    None            = 0,
    Insignificant   = 1,
    Negligible      = 2,
    Minor           = 3,
    MoreThanMinor   = 4,
    Harmful         = 5,
    VeryHarmful     = 6,
    Damaging        = 7,
    VeryDamaging    = 8,
    Severe          = 9,
    VerySevere      = 10,
    Devastating     = 11,
    VeryDevastating = 12,
    Useless         = 13,
}

/// <summary>
/// The four readings <c>perceive health</c> gives for every body region. The
/// injuries dialog can only report ONE of these at a time — which one depends
/// on the player's E/I Wound/Scar/Both display mode — so this is the axis
/// information that source structurally cannot carry.
/// </summary>
public enum InjuryAxis
{
    /// <summary>Fresh wound, external.</summary>
    FreshExternal,
    /// <summary>Fresh wound, internal.</summary>
    FreshInternal,
    /// <summary>Healed scar, external.</summary>
    ScarExternal,
    /// <summary>Healed scar, internal.</summary>
    ScarInternal,
}

/// <summary>One body region's four-axis reading.</summary>
public sealed class RegionInjuries
{
    /// <summary>Normalised region id — the SAME ids the injuries dialog uses
    /// (<c>leftArm</c>, <c>rightEye</c>, …) so a consumer can join a perceive
    /// reading onto <c>GameState.Injuries</c> without a second lookup table.
    /// Regions with no dialog counterpart (<c>skin</c>, <c>tail</c>) keep their
    /// own lowercase id.</summary>
    public string Region { get; init; } = "";

    /// <summary>Severity per axis. An axis DR did not mention reads
    /// <see cref="WoundSeverity.None"/>.</summary>
    public IReadOnlyDictionary<InjuryAxis, WoundSeverity> Axes { get; init; }
        = new Dictionary<InjuryAxis, WoundSeverity>();

    public WoundSeverity this[InjuryAxis axis] =>
        Axes.TryGetValue(axis, out var s) ? s : WoundSeverity.None;

    /// <summary>The worst severity across all four axes — what a single-number
    /// display (a panel cell, a script compare) wants.</summary>
    public WoundSeverity Worst =>
        Axes.Count == 0 ? WoundSeverity.None : Axes.Values.Max();

    /// <summary>True when any axis reports a FRESH wound. Fresh and scarred are
    /// different problems for an empath — one is bleeding, the other is not —
    /// so they are asked separately rather than through <see cref="Worst"/>.</summary>
    public bool HasFresh =>
        this[InjuryAxis.FreshExternal] > WoundSeverity.None ||
        this[InjuryAxis.FreshInternal] > WoundSeverity.None;

    /// <summary>True when any axis reports a scar.</summary>
    public bool HasScar =>
        this[InjuryAxis.ScarExternal] > WoundSeverity.None ||
        this[InjuryAxis.ScarInternal] > WoundSeverity.None;
}

/// <summary>
/// A complete <c>perceive health</c> reading for one patient (public #277).
///
/// <para>Deliberately a DIFFERENT shape from <c>GameState.Injuries</c>, which
/// is a single self-keyed region map fed by the injuries dialog. This one is
/// keyed by patient and carries all four axes on the true 1–13 ladder, because
/// that is the data the dialog cannot express and the reason the text block is
/// worth parsing at all.</para>
/// </summary>
public sealed class PatientHealth
{
    /// <summary>Patient name as DR spelled it, or <c>"self"</c> for the
    /// player's own reading (<c>Your injuries include…</c>).</summary>
    public string Patient { get; init; } = "";

    /// <summary>True when this reading is the player's own.</summary>
    public bool IsSelf => string.Equals(Patient, SelfPatient, StringComparison.OrdinalIgnoreCase);

    /// <summary>The patient key used for the player's own reading.</summary>
    public const string SelfPatient = "self";

    /// <summary>Injured regions, keyed by normalised region id. A region DR did
    /// not mention is absent — perceive lists only what is wounded, so absence
    /// means healthy rather than unknown.</summary>
    public IReadOnlyDictionary<string, RegionInjuries> Regions { get; init; }
        = new Dictionary<string, RegionInjuries>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remaining vitality as a percentage (0–100), or null when the block did
    /// not report one.
    /// </summary>
    /// <remarks>
    /// Derived as <c>100 - N</c> from the <c>(NN%)</c> DR prints, per the
    /// grammar in #277. <b>Unverified against a real capture</b> — #277 records
    /// that no recording of a perceive on another player exists yet, and this
    /// inversion is the one part of the grammar a capture would settle. The raw
    /// number is kept in <see cref="VitalityRawPercent"/> so a wrong inversion
    /// is a one-line fix rather than a re-parse.
    /// </remarks>
    public int? VitalityPercent { get; init; }

    /// <summary>The number DR actually printed inside the parentheses, before
    /// the <c>100 - N</c> inversion. See <see cref="VitalityPercent"/>.</summary>
    public int? VitalityRawPercent { get; init; }

    /// <summary>True when the block reported poison.</summary>
    public bool IsPoisoned { get; init; }

    /// <summary>True when the block reported disease or infection.</summary>
    public bool IsDiseased { get; init; }

    /// <summary>When the block completed.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Regions carrying a fresh wound, worst first — the order an
    /// empath heals in.</summary>
    public IReadOnlyList<RegionInjuries> FreshWorstFirst =>
        Regions.Values.Where(r => r.HasFresh)
                      .OrderByDescending(r => r.Worst)
                      .ToList();

    /// <summary>Regions carrying a scar, worst first.</summary>
    public IReadOnlyList<RegionInjuries> ScarsWorstFirst =>
        Regions.Values.Where(r => r.HasScar)
                      .OrderByDescending(r => r.Worst)
                      .ToList();
}
