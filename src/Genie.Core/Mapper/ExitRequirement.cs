using System.Text.RegularExpressions;
using Genie.Core.Skills;

namespace Genie.Core.Mapper;

/// <summary>
/// Structured representation of a <see cref="MapExit.Requires"/> string.
/// Parsed once at zone-load (or whenever the user edits the exit) and held
/// alongside the raw <see cref="MapExit.Requires"/> for round-trip XML.
///
/// <para>
/// Syntax accepted by <see cref="Parse"/>:
/// <list type="bullet">
///   <item><c>"climbing 50"</c> — legacy free-form, min rank 50</item>
///   <item><c>"climbing>=50"</c> — explicit min, identical meaning</item>
///   <item><c>"climbing>=50, athletics>=30"</c> — multiple skill ANDs</item>
///   <item><c>"class=Thief"</c> — guild/class restriction</item>
///   <item><c>"level>=25"</c> — character level (circle in DR) gate</item>
/// </list>
/// Unrecognised pieces are kept in <see cref="RawText"/> so we don't lose
/// information; they pass as "non-blocking" requirements (best-effort).
/// </para>
/// </summary>
public sealed partial record ExitRequirement
{
    /// <summary>Skill-name → minimum rank. AND-ed against the character.</summary>
    public Dictionary<string, int> MinRanks { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional class restriction (Thief, Empath, etc.). Null = any class.</summary>
    public string? RequiredClass { get; init; }

    /// <summary>Optional minimum level / circle. Null = any.</summary>
    public int? MinLevel { get; init; }

    /// <summary>Original string for round-trip; non-empty if the parser
    /// couldn't fully tokenise the input.</summary>
    public string RawText { get; init; } = "";

    /// <summary>Empty requirement — always passes. Useful as a default
    /// for exits with no <c>requires</c> attribute.</summary>
    public static readonly ExitRequirement Empty = new();

    public bool IsEmpty =>
        MinRanks.Count == 0 && RequiredClass is null && MinLevel is null;

    /// <summary>
    /// Test whether the character satisfies the requirement. Unknown
    /// skills (we haven't seen rank data yet) pass — the alternative is
    /// silently excluding every skill-gated edge before the player has
    /// loaded their stats panel.
    /// </summary>
    public bool IsMet(SkillStore? skills, string? characterClass, int characterLevel)
    {
        // Unknown class (null/empty) → pass: we haven't read the character's
        // guild/class yet, so assume reachable rather than silently excluding
        // every class-gated edge. Matches the skill-rank "unknown → pass" rule
        // below, AutoMapperEngine.CharacterClass's documented contract ("null
        // means no class info yet — passes all class checks"), and the mapper's
        // "every gated exit is assumed reachable" banner. Only block when we
        // KNOW the class and it doesn't match.
        if (RequiredClass is not null &&
            !string.IsNullOrEmpty(characterClass) &&
            !string.Equals(characterClass, RequiredClass, StringComparison.OrdinalIgnoreCase))
            return false;

        // Unknown level/circle (<= 0) → pass, same rationale: 0 means "no data
        // yet" (circle is frequently unpopulated), not "level zero". Without
        // this guard every `level>=N` gate fails before stats are read and the
        // pathfinder returns "No path".
        if (MinLevel.HasValue && characterLevel > 0 && characterLevel < MinLevel.Value)
            return false;

        if (skills is not null)
        {
            foreach (var (skill, min) in MinRanks)
            {
                if (!skills.IsKnown(skill)) continue;     // unknown → pass
                if (skills.Rank(skill) < min) return false;
            }
        }

        return true;
    }

    /// <summary>Returns the missing prerequisites as human-readable lines.
    /// Empty list when satisfied. Used by the pathfinder's "why no path"
    /// diagnostic.</summary>
    public IReadOnlyList<string> WhyUnmet(SkillStore? skills, string? characterClass, int characterLevel)
    {
        var reasons = new List<string>();
        if (RequiredClass is not null &&
            !string.IsNullOrEmpty(characterClass) &&
            !string.Equals(characterClass, RequiredClass, StringComparison.OrdinalIgnoreCase))
            reasons.Add($"requires class {RequiredClass} (you are {characterClass})");

        if (MinLevel.HasValue && characterLevel > 0 && characterLevel < MinLevel.Value)
            reasons.Add($"requires level {MinLevel} (you are {characterLevel})");

        if (skills is not null)
        {
            foreach (var (skill, min) in MinRanks)
            {
                if (!skills.IsKnown(skill)) continue;
                if (skills.Rank(skill) < min)
                    reasons.Add($"requires {skill} {min} (you have {skills.Rank(skill)})");
            }
        }

        return reasons;
    }

    // ── Parser ─────────────────────────────────────────────────────────────

    private static readonly Regex SkillOpRegex = SkillOpPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*(?<skill>[a-zA-Z][a-zA-Z ]*?)\s*(?<op>>=|>|=)?\s*(?<value>\d+)\s*$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex SkillOpPattern();

    private static readonly Regex ClassRegex = ClassPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*class\s*=\s*(?<class>\w+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ClassPattern();

    private static readonly Regex LevelRegex = LevelPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*level\s*(?:>=|>)?\s*(?<value>\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LevelPattern();

    /// <summary>
    /// Parse the free-form Requires string into structured fields.
    /// Splits on commas; each comma-separated piece is tried against
    /// the patterns above. Unparseable pieces accumulate in RawText.
    /// </summary>
    public static ExitRequirement Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var minRanks    = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? cls     = null;
        int?    minLvl  = null;
        var     rawLeft = new List<string>();

        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = piece.Trim();
            if (p.Length == 0) continue;

            // Class restriction first — has the most distinct prefix.
            var cm = ClassRegex.Match(p);
            if (cm.Success)
            {
                cls = cm.Groups["class"].Value;
                continue;
            }

            // Level gate.
            var lm = LevelRegex.Match(p);
            if (lm.Success && int.TryParse(lm.Groups["value"].Value, out var lvl))
            {
                minLvl = lvl;
                continue;
            }

            // Skill rank — last because it's the loosest pattern.
            var sm = SkillOpRegex.Match(p);
            if (sm.Success && int.TryParse(sm.Groups["value"].Value, out var rank))
            {
                var skill = sm.Groups["skill"].Value.Trim();
                if (skill.Length > 0)
                {
                    minRanks[skill] = rank;
                    continue;
                }
            }

            rawLeft.Add(p);
        }

        return new ExitRequirement
        {
            MinRanks      = minRanks,
            RequiredClass = cls,
            MinLevel      = minLvl,
            RawText       = string.Join(", ", rawLeft),
        };
    }
}
