namespace Genie.Core.Extensions.Builtin.SpellInfo;

/// <summary>The four filters <c>/spellinfo query</c> understands, as typed. A null
/// filter is not applied.</summary>
internal sealed record SpellFilter(
    string? Guild = null, string? Mana = null, string? Difficulty = null, string? Skill = null)
{
    public bool IsEmpty => Guild is null && Mana is null && Difficulty is null && Skill is null;
}

/// <summary>The outcome of running a filter: the resolved value of each filter that
/// was applied (in the order the Genie 4 plugin reported them), the matching spells,
/// or an error explaining which value didn't resolve and what the choices are.</summary>
internal sealed record SpellQueryResult(
    IReadOnlyList<(string Filter, string Value)> Applied,
    IReadOnlyList<SpellRecord>                   Matches,
    string?                                      Error);

/// <summary>
/// The <c>/spellinfo query</c> filter language, from Etherian's Genie 4 plugin:
/// <c>/guild</c>, <c>/mana</c>, <c>/difficulty</c> and <c>/skill</c>, each followed by
/// a (possibly multi-word) value, stacked to narrow the result —
/// <c>/spellinfo query /mana elemental /difficulty intro /skill targeted</c>.
///
/// <para><b>Values resolve against the data, not a fixed list.</b> An exact
/// (case-insensitive) match wins; otherwise a value that is the start of exactly
/// one choice is taken as that choice. That keeps the Genie 4 spellings working
/// against the wiki's longer names (<c>/mana life</c> → <c>Life Magic</c>) and
/// allows the short forms players already use (<c>/guild moon</c>,
/// <c>/skill aug</c>, <c>/difficulty inter</c>). An ambiguous or unknown value is
/// an error listing the choices, as the plugin's was — never a silent empty list.</para>
/// </summary>
internal static class SpellQuery
{
    private static readonly string[] FilterNames = { "guild", "mana", "difficulty", "skill" };

    /// <summary>Skill spellings the Genie 4 plugin used in its annotations that are
    /// not prefixes of the skill name.</summary>
    private static readonly Dictionary<string, string> SkillAliases =
        new(StringComparer.OrdinalIgnoreCase) { ["tm"] = "targeted" };

    /// <summary>Parse the tokens after <c>query</c>. Returns null and sets
    /// <paramref name="error"/> on a malformed query.</summary>
    public static SpellFilter? Parse(IReadOnlyList<string> tokens, out string? error)
    {
        error = null;
        if (tokens.Count == 0)
        {
            error = "You must pass some parameters to the query. Try /spellinfo query help.";
            return null;
        }

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in tokens)
        {
            if (raw.StartsWith('/'))
            {
                var name = raw[1..].ToLowerInvariant();
                if (Array.IndexOf(FilterNames, name) < 0)
                {
                    error = $"Invalid filter type: {raw}. Use /guild, /mana, /difficulty or /skill.";
                    return null;
                }
                current = name;
                if (!values.ContainsKey(name)) values[name] = new List<string>();
                continue;
            }

            if (current is null)
            {
                error = $"'{raw}' comes before any filter. Start with /guild, /mana, /difficulty or /skill.";
                return null;
            }

            var word = raw.Replace("\"", "");
            if (word.Length > 0) values[current].Add(word);
        }

        foreach (var (name, words) in values)
            if (words.Count == 0)
            {
                error = $"/{name} needs a value.";
                return null;
            }

        string? Get(string name) => values.TryGetValue(name, out var w) ? string.Join(" ", w) : null;
        return new SpellFilter(Get("guild"), Get("mana"), Get("difficulty"), Get("skill"));
    }

    /// <summary>Apply a filter to the table. Filters stack (all must match).</summary>
    public static SpellQueryResult Run(SpellFilter filter, SpellTable table)
    {
        var applied = new List<(string, string)>();
        IEnumerable<SpellRecord> spells = table.Spells;

        bool Step(string label, string? typed, Func<SpellRecord, IEnumerable<string>> values,
                  IReadOnlyDictionary<string, string>? aliases, out string? error)
        {
            error = null;
            if (typed is null) return true;

            var choices = table.Spells.SelectMany(values)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var value = Resolve(typed, choices, aliases);
            if (value is null)
            {
                error = $"No {label} matches '{typed}'. Choose one of: {string.Join(", ", choices)}.";
                return false;
            }

            applied.Add((label, value));
            spells = spells.Where(s => values(s).Contains(value, StringComparer.OrdinalIgnoreCase));
            return true;
        }

        if (!Step("guild",      filter.Guild,      s => new[] { s.Guild },            null,         out var e) ||
            !Step("mana",       filter.Mana,       s => Opt(s.ManaType),              null,         out e) ||
            !Step("difficulty", filter.Difficulty, s => Opt(s.Difficulty),            null,         out e) ||
            !Step("skill",      filter.Skill,      s => s.Skills,                     SkillAliases, out e))
            return new SpellQueryResult(applied, Array.Empty<SpellRecord>(), e);

        return new SpellQueryResult(applied, spells.ToList(), null);
    }

    /// <summary>Exact match first, then an alias, then a unique prefix. Null when
    /// nothing, or more than one choice, fits.</summary>
    internal static string? Resolve(string typed, IReadOnlyList<string> choices,
                                    IReadOnlyDictionary<string, string>? aliases = null)
    {
        var t = string.Join(" ", typed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (t.Length == 0) return null;

        var exact = choices.FirstOrDefault(c => c.Equals(t, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        if (aliases is not null && aliases.TryGetValue(t, out var aliased))
        {
            var hit = choices.FirstOrDefault(c => c.Equals(aliased, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        var prefixed = choices.Where(c => c.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefixed.Count == 1 ? prefixed[0] : null;
    }

    private static IEnumerable<string> Opt(string? value) =>
        value is null ? Array.Empty<string>() : new[] { value };
}
