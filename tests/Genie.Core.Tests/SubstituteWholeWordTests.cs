using System;
using System.IO;
using System.Linq;
using Genie.Core.Persistence;
using Genie.Core.Substitutes;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #245 — a per-rule "whole words only" toggle, so a substitute for
/// <c>take</c> does not also rewrite the inside of <c>mistake</c>.
///
/// <para>Carried over from Genie 4 #123. Without it the only workaround was for
/// the user to write the boundaries into the pattern themselves, which means
/// knowing the pattern is a regex at all and that <c>\b</c> is the thing to
/// reach for — not a reasonable ask of someone replacing a word with another
/// word.</para>
/// </summary>
public class SubstituteWholeWordTests
{
    private static SubstituteEngine Engine() => new();

    // ── The behaviour ────────────────────────────────────────────────────────

    /// <summary>The reported case, verbatim.</summary>
    [Fact]
    public void A_whole_word_rule_does_not_rewrite_the_inside_of_a_longer_word()
    {
        var e = Engine();
        e.AddRule("take", "grab", wholeWord: true);

        Assert.Equal("grab it", e.Apply("take it"));
        Assert.Equal("a mistake", e.Apply("a mistake"));   // untouched
    }

    /// <summary>…and the default is unchanged, so no existing rule shifts
    /// behaviour under anyone.</summary>
    [Fact]
    public void Without_the_flag_the_old_substring_behaviour_is_kept()
    {
        var e = Engine();
        e.AddRule("take", "grab");

        Assert.Equal("a misgrab", e.Apply("a mistake"));
    }

    /// <summary>Boundaries are about word characters, not spaces — punctuation
    /// on either side still counts as a boundary.</summary>
    [Fact]
    public void Punctuation_counts_as_a_word_boundary()
    {
        var e = Engine();
        e.AddRule("take", "grab", wholeWord: true);

        Assert.Equal("(grab), grab!", e.Apply("(take), take!"));
    }

    /// <summary>
    /// The whole pattern is wrapped before anchoring, so alternation means what
    /// it looks like. Anchoring naively would produce <c>\bcat|dog\b</c>, which
    /// binds as "(\bcat) or (dog\b)" — the second branch would still match
    /// inside a longer word, and the bug would look fixed on the first branch
    /// only.
    /// </summary>
    [Fact]
    public void Alternation_is_anchored_as_a_whole_not_just_its_first_branch()
    {
        var e = Engine();
        e.AddRule("cat|dog", "pet", wholeWord: true);

        Assert.Equal("pet and pet", e.Apply("cat and dog"));
        Assert.Equal("concatenate hotdog", e.Apply("concatenate hotdog"));
    }

    /// <summary>The wrapper is non-capturing, so the user's own group numbers
    /// are unchanged and a replacement using $1 keeps working.</summary>
    [Fact]
    public void Capture_group_numbers_are_not_shifted_by_the_wrapper()
    {
        var e = Engine();
        e.AddRule(@"(\w+)berry", "BERRY:$1", wholeWord: true);

        Assert.Equal("BERRY:blue", e.Apply("blueberry"));
    }

    // ── Persistence: a flag that does not round-trip is worse than none ──────

    /// <summary>A whole-word rule saved and reloaded is still whole-word;
    /// otherwise the toggle silently reverts at the next connect.</summary>
    [Fact]
    public void The_flag_survives_a_json_round_trip()
    {
        var dir  = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie_ww_" + Guid.NewGuid().ToString("N"))).FullName;
        var path = Path.Combine(dir, "substitutes.json");
        try
        {
            var saved = Engine();
            saved.AddRule("take", "grab", wholeWord: true);
            saved.AddRule("kobold", "friend");
            new PersistenceService().SaveSubstitutes(path, saved.Rules);

            var models = new PersistenceService().LoadSubstitutes(path);
            var loaded = Engine();
            foreach (var m in models)
                loaded.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName, m.WholeWord);

            Assert.Equal("a mistake", loaded.Apply("a mistake"));
            Assert.Equal("grab it",   loaded.Apply("take it"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The .cfg twin carries it as a trailing keyword, the same shape
    /// #trigger add uses for eval/matchall — so it cannot shift the positional
    /// class slot.</summary>
    [Fact]
    public void The_cfg_line_carries_a_trailing_wholeword_keyword()
    {
        var e = Engine();
        e.AddRule("take", "grab", className: "hunting", wholeWord: true);
        e.AddRule("kobold", "friend", className: "hunting");

        var lines = CfgFormat.SubstituteLines(e.Rules).ToList();

        Assert.EndsWith(" wholeword", lines[0]);
        Assert.DoesNotContain("wholeword", lines[1]);
    }
}
