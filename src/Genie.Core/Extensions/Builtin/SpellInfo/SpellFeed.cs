using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Genie.Core.Extensions.Builtin.SpellInfo;

/// <summary>One spell, as the Elanthipedia feed describes it. List-valued fields
/// stay lists because the wiki allows several values (a spell can train both
/// augmentation and utility, or cast as battle or cyclic); scalar fields are the
/// first value, or null when the wiki leaves them unset.</summary>
/// <param name="PageTitle">The wiki page title, which is the feed's key. Usually
/// the spell name, but a few carry a disambiguation suffix (<c>Burden (spell)</c>).</param>
/// <param name="Name">The spell name as the game says it: the page title with any
/// <c>(spell)</c> suffix dropped.</param>
/// <param name="Abbrev">Abbreviation, or null when the wiki records none (the feed
/// spells that as the literal <c>none</c>).</param>
public sealed record SpellRecord(
    string                PageTitle,
    string                Name,
    string?               Abbrev,
    string                Guild,
    string?               ManaType,
    string?               Spellbook,
    string?               Difficulty,
    bool?                 Signature,
    string?               Effect,
    IReadOnlyList<string> CastTypes,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> SpellTypes,
    string?               OffContest,
    string?               DefContest,
    int?                  MinPrep,
    int?                  Cap,
    int?                  MinSkill,
    int?                  MaxSkill,
    int?                  Slots,
    string?               WikiUrl);

/// <summary>A parsed feed: every spell, name-sorted, and the feed's own
/// <c>SyncedAt</c> stamp (when the nightly job last queried the wiki).</summary>
public sealed record SpellTable(IReadOnlyList<SpellRecord> Spells, DateTimeOffset? SyncedAt);

/// <summary>
/// Reads the nightly spell feed at <see cref="SpellFeedCache.FeedUrl"/>. It is a
/// Semantic MediaWiki <c>ask</c> result, saved as-is:
/// <c>{ "query": { "results": { "&lt;page&gt;": { "printouts": {…}, "fullurl": … } } }, "SyncedAt": … }</c>.
///
/// <para>Two shapes need care. <b>Every printout is an array</b>, empty when the
/// wiki leaves a property unset, so each field reads its first element or none.
/// And page-typed properties (<c>Guild</c>) arrive as objects carrying
/// <c>fulltext</c>/<c>fullurl</c> rather than as strings.</para>
/// </summary>
public static partial class SpellFeed
{
    /// <summary>Parse a feed document. Throws <see cref="FormatException"/> when the
    /// text isn't the feed's shape or carries no spells, so a caller can refuse to
    /// replace a good cache with a broken download.</summary>
    public static SpellTable Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("The spell list isn't valid JSON.", ex); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Object)
                throw new FormatException("The spell list isn't in the expected shape.");

            var spells = new List<SpellRecord>();
            foreach (var entry in results.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("printouts", out var p) ||
                    p.ValueKind != JsonValueKind.Object)
                    continue;

                var title = Text(entry.Value, "fulltext") ?? entry.Name;
                var abbrev = First(p, "Abbrev");
                if (string.Equals(abbrev, "none", StringComparison.OrdinalIgnoreCase)) abbrev = null;

                spells.Add(new SpellRecord(
                    PageTitle:  title,
                    Name:       SpellSuffix().Replace(title, ""),
                    Abbrev:     abbrev,
                    Guild:      First(p, "Guild") ?? "None",
                    ManaType:   First(p, "Mana Type"),
                    Spellbook:  First(p, "Spellbook"),
                    Difficulty: First(p, "Difficulty"),
                    Signature:  First(p, "Signature") switch
                                {
                                    "t" or "true"  => true,
                                    "f" or "false" => false,
                                    _              => null,
                                },
                    Effect:     CleanEffect(All(p, "Effect")),
                    CastTypes:  All(p, "Cast Type"),
                    Skills:     All(p, "Skill"),
                    SpellTypes: All(p, "Spell Type"),
                    OffContest: First(p, "Off Contest"),
                    DefContest: First(p, "Def Contest"),
                    MinPrep:    Int(p, "Min Prep"),
                    Cap:        Int(p, "Cap"),
                    MinSkill:   Int(p, "Min Skill"),
                    MaxSkill:   Int(p, "Max Skill"),
                    Slots:      Int(p, "Slots"),
                    WikiUrl:    Text(entry.Value, "fullurl")));
            }

            if (spells.Count == 0)
                throw new FormatException("The spell list is empty.");

            spells.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            DateTimeOffset? synced = null;
            if (Text(root, "SyncedAt") is { } s && DateTimeOffset.TryParse(s,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
                synced = at;

            return new SpellTable(spells, synced);
        }
    }

    /// <summary>The wiki writes effects as HTML-bearing text (<c>Fire damage,&amp;nbsp;Single
    /// target.</c>). Decode the entities, turn the resulting non-breaking spaces into
    /// ordinary ones, drop any stray markup, and tidy the spacing.</summary>
    internal static string? CleanEffect(IReadOnlyList<string> values)
    {
        if (values.Count == 0) return null;
        var text = WebUtility.HtmlDecode(string.Join("; ", values));
        text = Markup().Replace(text, " ").Replace(' ', ' ');
        text = Spaces().Replace(text, " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? First(JsonElement printouts, string name)
    {
        var all = All(printouts, name);
        return all.Count > 0 ? all[0] : null;
    }

    private static IReadOnlyList<string> All(JsonElement printouts, string name)
    {
        if (!printouts.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var v in arr.EnumerateArray())
        {
            var s = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                // Page-typed values ({ "fulltext": "Bard", "fullurl": … }).
                JsonValueKind.Object => Text(v, "fulltext"),
                _                    => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        }
        return list;
    }

    private static int? Int(JsonElement printouts, string name) =>
        First(printouts, name) is { } s && double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (int)d
            : null;

    [GeneratedRegex(@"\s*\(spell\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SpellSuffix();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
