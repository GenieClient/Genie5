using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Genie.Core.Events;

namespace Genie.Core.Health;

/// <summary>
/// Reads one body region's wound or scar out of the `health` verb's summary
/// line ("You have minor abrasions to the tail, …").
///
/// <para>The phrase table is Lich 5's <c>WOUND_SEVERITY_REGEX_MAP</c>
/// (dragonrealms/commons/common-healing-data.rb), keeping only the phrases that
/// take a generic body-part slot — every one of them reads the same for any
/// part, so <c>{P}</c> is filled in with the region asked for. Region-specific
/// phrases (head, eyes, chest, abdomen, skin) are left out; the nerve readings
/// live in the parser's nerve table.</para>
///
/// <para>The summary is a SECONDARY source: it only supplies a reading for a
/// region the injuries dialog never reports (the tail). See
/// <c>DrXmlParser</c>'s dialog-primary rules.</para>
/// </summary>
public static class HealthWoundText
{
    private sealed record Entry(string Template, int Severity, bool Scar);

    // Severity is Lich's 1–8 scale for the health verb. Duplicate phrases in
    // Lich's map resolve to the last value (Ruby hash literal semantics).
    private static readonly Entry[] Entries =
    {
        new(@"minor abrasions to the {P}", 1, false),
        new(@"a few nearly invisible scars along the {P}", 1, true),
        new(@"some tiny scars (?:across|along) the {P}", 2, true),
        new(@"(?:light|tiny) scratches to the {P}", 2, false),
        new(@"cuts and bruises about the {P}", 3, false),
        new(@"minor scar\w+ (?:about|along|across) the {P}", 3, true),
        new(@"minor swelling and bruising (?:around|in) the {P}", 3, false),
        new(@"occasional twitch\w* (?:on|in) the {P}", 3, true),
        new(@"a black and blue {P}", 3, false),
        new(@"deep cuts (?:about|across) the {P}", 4, false),
        new(@"severe scarring (?:across|along|about) the {P}", 4, true),
        new(@"a severely swollen and\s?(?:deeply)? bruised {P}", 4, false),
        new(@"(?:occasional|constant) twitch\w* (?:on|in) the {P}", 4, true),
        new(@"a bruised,* swollen and bleeding {P}", 5, false),
        new(@"deeply scarred gashes across the {P}", 5, true),
        new(@"a severely swollen, bruised and crossed {P}", 5, false),
        new(@"a constant twitching in the {P}", 5, true),
        new(@"a constant twitching in the {P} and difficulty moving in general", 5, true),
        new(@"deep slashes across the {P}", 5, false),
        new(@"a severely swollen and deeply bruised {P}", 5, false),
        new(@"severely swollen and bruised {P}", 5, false),
        new(@"a bruised, swollen and slashed {P}", 6, false),
        new(@"a punctured and shriveled {P}", 6, true),
        new(@"a severely swollen,* bruised and cloudy {P}", 6, false),
        new(@"a clouded {P}", 6, true),
        new(@"gaping holes in the {P}", 6, false),
        new(@"a broken {P} with gaping holes", 6, false),
        new(@"severe scarring and ugly gashes about the {P}", 6, true),
        new(@"severe scarring and chunks of flesh missing from the {P}", 6, true),
        new(@"a severely swollen and deeply bruised {P} with odd protrusions under the skin", 6, false),
        new(@"a partially paralyzed {P}", 6, true),
        new(@"a painful {P} and difficulty moving without pain", 6, true),
        new(@"a bruised,* swollen and shattered {P}", 7, false),
        new(@"severely scarred,* mangled and malformed {P}", 7, true),
        new(@"a completely clouded {P}", 7, true),
        new(@"a shattered {P} with gaping wounds", 7, false),
        new(@"a severely swollen and deeply bruised {P} with bones protruding out from the skin", 7, false),
        new(@"a severely swollen and deeply bruised {P} with ribs or vertebrae protruding out from the skin", 7, false),
        new(@"a severely paralyzed {P}", 7, true),
        new(@"a severely painful {P} with significant problems moving", 7, true),
        new(@"{P} deeply gouged with gaping wounds", 7, false),
        new(@"a severely bloated and discolored {P} with strange round lumps under the skin", 7, false),
        new(@"an ugly stump for a {P}", 8, false),
        new(@"a pulpy cavity for a {P}", 8, false),
        new(@"a completely useless {P} with nearly all flesh and bone torn away", 8, false),
        new(@"a completely destroyed {P} with nearly all flesh and bone torn away revealing a gaping hole", 8, false),
        new(@"an ugly flesh stump for a {P}", 8, true),
        new(@"an ugly flesh stump for a {P} with little left to support the head", 8, true),
        new(@"a severely swollen and shattered {P} which appears completely useless", 8, false),
        new(@"a severely swollen and shattered {P} which appears useless to hold up the head", 8, false),
        new(@"a completely paralyzed {P}", 8, true),
        new(@"a mostly non-existent {P} filled with ugly chunks of scarred flesh", 8, true),
        new(@"an extremely painful {P} while gasping for breath in short shallow bursts", 8, true),
        new(@"a severely bloated and discolored {P} which appears oddly rearranged", 8, false),
        new(@"a severely swollen {P} with a shattered spinal cord", 8, false),
        new(@"an extremely painful and bizarrely twisted {P} making it nearly impossible to move", 8, true),
    };

    private static readonly ConcurrentDictionary<string, Regex[]> _byPart = new(StringComparer.Ordinal);

    /// <summary>Fold Lich's 1–8 health-text scale onto the panel's three sprite
    /// ranks: 1–4 → 1, 5–6 → 2, 7–8 → 3. The mapping is an estimate — it is not
    /// confirmed against what DR's own dialog reports for the same wound.</summary>
    public static int RankOf(int severity) => severity <= 4 ? 1 : severity <= 6 ? 2 : 3;

    /// <summary>
    /// The reading the line carries for <paramref name="part"/> ("tail"), or
    /// null when no known phrase names it. A fresh wound beats a scar, then the
    /// highest severity wins; the severity is returned already folded to the
    /// panel's 1–3 rank.
    /// </summary>
    public static (InjuryKind Kind, int Rank)? Read(string line, string part)
    {
        var regexes = _byPart.GetOrAdd(part, static p =>
        {
            var slot = @"(?:inside\s)?" + Regex.Escape(p) + @"\b";
            var built = new Regex[Entries.Length];
            for (int i = 0; i < Entries.Length; i++)
                built[i] = new Regex(Entries[i].Template.Replace("{P}", slot), RegexOptions.CultureInvariant);
            return built;
        });

        InjuryKind bestKind = InjuryKind.None;
        int bestSeverity = 0;
        for (int i = 0; i < Entries.Length; i++)
        {
            if (!regexes[i].IsMatch(line)) continue;
            var kind = Entries[i].Scar ? InjuryKind.Scar : InjuryKind.Wound;
            if (bestKind == InjuryKind.None
                || (kind == InjuryKind.Wound && bestKind == InjuryKind.Scar)
                || (kind == bestKind && Entries[i].Severity > bestSeverity))
            {
                bestKind = kind;
                bestSeverity = Entries[i].Severity;
            }
        }
        return bestKind == InjuryKind.None ? null : (bestKind, RankOf(bestSeverity));
    }
}
