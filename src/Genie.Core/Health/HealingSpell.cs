using System;
using System.Collections.Generic;
using System.Linq;

namespace Genie.Core.Health;

/// <summary>
/// The nine Empath spells the Healing panel can prepare (public #263). Named by
/// the abbreviation Genie 4's Crutch used for each slot, because those
/// abbreviations are baked into the <c>GCTextBoxMana*</c> / <c>GCTextBoxDelay*</c>
/// script variables that community scripts read.
/// </summary>
public enum HealingSpell
{
    /// <summary>Blood Staunching.</summary>
    BS,
    /// <summary>Cure Disease.</summary>
    CD,
    /// <summary>Flush Poisons.</summary>
    FP,
    /// <summary>Heal.</summary>
    HEAL,
    /// <summary>Heal Scars.</summary>
    HS,
    /// <summary>Heal Wounds.</summary>
    HW,
    /// <summary>Refresh.</summary>
    REFR,
    /// <summary>Regenerate.</summary>
    REGE,
    /// <summary>Vitality Healing.</summary>
    VH,
}

/// <summary>What one <see cref="HealingSpell"/> is called and how it is prepared.</summary>
/// <param name="Spell">The slot.</param>
/// <param name="Name">Full spell name, for the panel.</param>
/// <param name="PrepName">What follows <c>prep</c> on the command line.</param>
public sealed record HealingSpellInfo(HealingSpell Spell, string Name, string PrepName)
{
    /// <summary>The slot's abbreviation — the suffix of its script variables.</summary>
    public string Abbreviation => Spell.ToString();

    /// <summary>Genie 4 script variable holding this spell's mana. Kept exactly,
    /// so a <c>.cmd</c> that reads <c>$GCTextBoxManaHW</c> keeps working.</summary>
    public string ManaVariable => "GCTextBoxMana" + Abbreviation;

    /// <summary>Genie 4 script variable holding this spell's prep-to-cast wait.</summary>
    public string DelayVariable => "GCTextBoxDelay" + Abbreviation;
}

/// <summary>The spell catalog, in panel order.</summary>
public static class HealingSpells
{
    public static IReadOnlyList<HealingSpellInfo> All { get; } = new[]
    {
        new HealingSpellInfo(HealingSpell.HW,   "Heal Wounds",       "hw"),
        new HealingSpellInfo(HealingSpell.HS,   "Heal Scars",        "hs"),
        new HealingSpellInfo(HealingSpell.HEAL, "Heal",              "heal"),
        new HealingSpellInfo(HealingSpell.REGE, "Regenerate",        "regen"),
        new HealingSpellInfo(HealingSpell.VH,   "Vitality Healing",  "vh"),
        new HealingSpellInfo(HealingSpell.BS,   "Blood Staunching",  "bs"),
        new HealingSpellInfo(HealingSpell.FP,   "Flush Poisons",     "fp"),
        new HealingSpellInfo(HealingSpell.CD,   "Cure Disease",      "cd"),
        new HealingSpellInfo(HealingSpell.REFR, "Refresh",           "refresh"),
    };

    private static readonly Dictionary<HealingSpell, HealingSpellInfo> BySpell =
        All.ToDictionary(s => s.Spell);

    public static HealingSpellInfo Get(HealingSpell spell) => BySpell[spell];

    /// <summary>Resolve an abbreviation (<c>hw</c>, <c>REGE</c>, …). False for
    /// anything that is not one of the nine slots.</summary>
    public static bool TryParse(string? text, out HealingSpell spell)
    {
        spell = default;
        var t = text?.Trim();
        // Letters only: Enum.TryParse would otherwise accept "1" as a slot.
        return t is { Length: > 0 } && t.All(char.IsLetter) &&
               Enum.TryParse(t, ignoreCase: true, out spell);
    }
}
