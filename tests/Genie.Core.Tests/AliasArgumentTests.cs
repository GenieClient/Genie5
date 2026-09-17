using Genie.Core.Aliases;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Genie 4 alias argument substitution (<c>Core/Command.cs ParseAlias</c>).
/// Only <c>$*</c> was implemented, so every numbered argument reached the game
/// as the literal text <c>$1</c> — 94 of the 356 aliases in the reference
/// settings tree use them.
/// </summary>
public class AliasArgumentTests
{
    private static string Expand(string expansion, string input)
    {
        var parts = input.Trim().Split(' ', 2);
        var args  = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        return AliasEngine.Expand(expansion, input.Trim(), args);
    }

    // ── $0 and $* — the whole argument string ────────────────────────────

    [Fact]
    public void Dollar_zero_is_the_whole_argument_string()
        => Assert.Equal("get rope from pack", Expand("get $0", "gr rope from pack"));

    [Fact]
    public void Dollar_star_still_works()
        => Assert.Equal("get rope from pack", Expand("get $*", "gr rope from pack"));

    [Fact]
    public void Dollar_zero_is_empty_when_no_arguments_are_given()
        => Assert.Equal("get", Expand("get $0", "gr").TrimEnd());

    // ── Positional arguments ─────────────────────────────────────────────

    [Fact]
    public void Numbered_arguments_substitute_positionally()
        => Assert.Equal("put rope in pack", Expand("put $1 in $2", "stow rope pack"));

    [Fact]
    public void Missing_arguments_substitute_as_empty()
        => Assert.Equal("put rope in ", Expand("put $1 in $2", "stow rope"));

    [Fact]
    public void Arguments_past_nine_resolve_to_their_own_index()
    {
        // Genie 4 looped ascending and replaced $1 inside $10 first, yielding
        // "<arg1>0". Resolving the real index is a deliberate divergence.
        var input = "a one two three four five six seven eight nine ten";
        Assert.Equal("ten", Expand("$10", input));
    }

    [Fact]
    public void Repeated_placeholders_all_substitute()
        => Assert.Equal("look rope; get rope", Expand("look $1; get $1", "x rope"));

    // ── Grouping ─────────────────────────────────────────────────────────

    [Fact]
    public void Braced_arguments_group_and_lose_their_wrapper()
        => Assert.Equal("say hello there friend",
                        Expand("say $1", "s {hello there friend}"));

    [Fact]
    public void Quoted_arguments_group_and_lose_their_quotes()
        => Assert.Equal("whisper Miss Fortune hello",
                        Expand("whisper $1 $2", "w \"Miss Fortune\" hello"));

    // ── The no-$ append rule ─────────────────────────────────────────────

    [Fact]
    public void Expansion_without_any_dollar_gets_the_arguments_appended()
        => Assert.Equal("get rope from pack", Expand("get rope", "gr from pack"));

    [Fact]
    public void Expansion_without_any_dollar_and_no_arguments_is_unchanged()
        => Assert.Equal("get rope", Expand("get rope", "gr"));

    [Fact]
    public void Expansion_with_a_dollar_does_not_also_append()
        => Assert.Equal("get rope", Expand("get $1", "gr rope"));

    // ── Things that must not be treated as arguments ─────────────────────

    [Fact]
    public void Script_and_global_variables_are_left_alone()
    {
        // $charactername is a global, not an alias argument; only $<digits>
        // is substituted here.
        Assert.Equal("say hello $charactername",
                     Expand("say hello $charactername", "greet"));
    }

    [Fact]
    public void A_dollar_inside_a_word_is_not_an_argument()
        => Assert.Equal("appraise my var2", Expand("appraise my var2", "app"));

    // ── End-to-end through the engine ────────────────────────────────────

    [Fact]
    public void Engine_matches_and_fires_the_alias()
    {
        // Null command engine — TryProcess's result is the signal, same as
        // MasterToggleTests. The expansion itself is covered above.
        var engine = new AliasEngine();
        engine.AddAlias("stow", "put $1 in my $2");

        Assert.True(engine.TryProcess("stow rope pack"));
    }

    [Fact]
    public void Engine_matches_the_alias_name_case_insensitively_and_ignores_padding()
    {
        var engine = new AliasEngine();
        engine.AddAlias("stow", "put $1 in my pack");

        Assert.True(engine.TryProcess("  STOW rope  "));
    }

    [Fact]
    public void Engine_returns_false_for_a_non_alias()
    {
        var engine = new AliasEngine();
        engine.AddAlias("stow", "put $1 in my pack");

        Assert.False(engine.TryProcess("look"));
    }

    [Fact]
    public void Disabled_alias_does_not_fire()
    {
        var engine = new AliasEngine();
        engine.AddAlias("stow", "put $1 in my pack", isEnabled: false);

        Assert.False(engine.TryProcess("stow rope"));
    }
}
