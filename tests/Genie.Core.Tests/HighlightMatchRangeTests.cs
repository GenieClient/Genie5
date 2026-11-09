using System.Linq;
using Genie.Core.Highlights;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Which characters a highlight colours, per match type — Genie 4 semantics.
/// <para>#397: a Regex rule with capture groups colours only groups 1..n
/// (Genie 4 <c>ComponentRichTextBox.ParseRegExpHighlight</c>); without groups
/// it colours the whole match.</para>
/// <para>#400: a BeginsWith rule colours the whole line, not just the prefix
/// (Genie 4 <c>Game.cs</c> "Line begins with" pass).</para>
/// </summary>
public class HighlightMatchRangeTests
{
    private static string[] Painted(HighlightRule rule, string line) =>
        rule.GetMatchPositions(line).Where(s => s.Length > 0)
            .Select(s => line.Substring(s.Start, s.Length)).ToArray();

    private static HighlightRule Regex(string pattern, bool caseSensitive = true) =>
        new(pattern, "#FF0000", matchType: HighlightMatchType.Regex, caseSensitive: caseSensitive);

    // ── #397 Regex ──────────────────────────────────────────────────────────

    [Fact]
    public void Regex_with_groups_paints_only_the_groups()
    {
        // The issue's imported speech/thought rule.
        const string line = "[General] Your mind hears Taenara thinking, \"Crackshots showing up\"";
        Assert.Equal(new[] { "[General] Your mind hears Taenara thinking, \"", "\"" },
                     Painted(Regex("(^.*, \").*(\")"), line));
    }

    [Fact]
    public void Regex_group_picks_out_one_word()
    {
        const string line = "     Evasion:  305 30% learning     ";
        Assert.Equal(new[] { "learning" }, Painted(Regex(@"\% (learning|thoughtful|thinking)"), line));
    }

    [Fact]
    public void Regex_without_groups_paints_the_whole_match()
    {
        Assert.Equal(new[] { "north gate" }, Painted(Regex("(?:north|south) gate"), "You see the north gate."));
        Assert.Equal(new[] { "bleeding" },   Painted(Regex("bleed\\w+"),             "You are bleeding."));
    }

    [Fact]
    public void Regex_groups_paint_on_every_match()
    {
        Assert.Equal(new[] { "1", "2" }, Painted(Regex(@"x(\d)"), "x1 and x2"));
    }

    [Fact]
    public void Regex_optional_group_that_did_not_take_part_is_skipped()
    {
        Assert.Equal(new[] { "b" }, Painted(Regex("(a)?(b)"), "b"));
    }

    [Fact]
    public void Regex_match_with_empty_groups_still_reports_a_match_but_paints_nothing()
    {
        // Sound / TTS fire on "the rule matched", so a zero-length span is kept.
        var spans = Regex("(z)?bar").GetMatchPositions("a bar").ToArray();
        Assert.Single(spans);
        Assert.Equal(0, spans[0].Length);
    }

    [Fact]
    public void Regex_no_match_yields_nothing()
    {
        Assert.Empty(Regex("(foo)").GetMatchPositions("bar baz"));
    }

    // ── #400 BeginsWith ─────────────────────────────────────────────────────

    [Fact]
    public void BeginsWith_paints_the_whole_line()
    {
        const string line = "* Moving inexpertly, a void-black umbral moth swings at you.  You dodge.";
        var rule = new HighlightRule("*", "Yellow", matchType: HighlightMatchType.BeginsWith);
        Assert.Equal(new[] { line }, Painted(rule, line));
    }

    [Fact]
    public void BeginsWith_needs_the_line_to_start_with_the_text()
    {
        var rule = new HighlightRule("You've gained", "Yellow", matchType: HighlightMatchType.BeginsWith);
        Assert.Empty(rule.GetMatchPositions("Then You've gained a rank."));
    }

    [Fact]
    public void BeginsWith_honours_case_sensitivity()
    {
        var insensitive = new HighlightRule("send", "Yellow", matchType: HighlightMatchType.BeginsWith, caseSensitive: false);
        var sensitive   = new HighlightRule("send", "Yellow", matchType: HighlightMatchType.BeginsWith, caseSensitive: true);
        Assert.Equal(new[] { "SEND 3" }, Painted(insensitive, "SEND 3"));
        Assert.Empty(sensitive.GetMatchPositions("SEND 3"));
    }

    // ── Unchanged types ─────────────────────────────────────────────────────

    [Fact]
    public void String_and_Line_are_unchanged()
    {
        var str  = new HighlightRule("bleeding", "Red", matchType: HighlightMatchType.String);
        var line = new HighlightRule("bleeding", "Red", matchType: HighlightMatchType.Line);
        Assert.Equal(new[] { "bleeding" },          Painted(str,  "You are bleeding."));
        Assert.Equal(new[] { "You are bleeding." }, Painted(line, "You are bleeding."));
    }
}
