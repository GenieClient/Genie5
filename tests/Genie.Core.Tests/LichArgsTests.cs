using System;
using System.Linq;
using Genie.Core.Connection;
using Xunit;

namespace Genie.Core.Tests;

public class LichArgsTests
{
    [Fact]
    public void Tokenize_splits_plain_flags_on_whitespace()
    {
        Assert.Equal(
            new[] { "--login", "Char", "--without-frontend", "--detachable-client=8000" },
            LichArgs.Tokenize("--login Char  --without-frontend\t--detachable-client=8000").ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Tokenize_yields_nothing_for_blank_input(string? input) =>
        Assert.Empty(LichArgs.Tokenize(input));

    [Fact]
    public void Tokenize_keeps_a_quoted_path_with_spaces_as_one_token()
    {
        Assert.Equal(
            new[] { "--temp", "/Users/me/Application Support/lich/temp" },
            LichArgs.Tokenize("--temp \"/Users/me/Application Support/lich/temp\"").ToArray());

        Assert.Equal(
            new[] { "--temp", "/Users/me/Application Support/lich/temp" },
            LichArgs.Tokenize("--temp '/Users/me/Application Support/lich/temp'").ToArray());
    }

    [Fact]
    public void Tokenize_allows_a_quote_to_open_mid_token()
    {
        Assert.Equal(
            new[] { "--temp=/a b/temp", "--login", "Char" },
            LichArgs.Tokenize("--temp=\"/a b/temp\" --login Char").ToArray());

        // Whole argument quoted resolves to the same single token.
        Assert.Equal(
            new[] { "--temp=/a b/temp" },
            LichArgs.Tokenize("\"--temp=/a b/temp\"").ToArray());
    }

    [Fact]
    public void Tokenize_does_not_treat_backslash_as_an_escape()
    {
        // Windows paths must survive verbatim — this is why escaping is unsupported.
        Assert.Equal(
            new[] { "--temp=C:\\lich\\temp", "--login", "Char" },
            LichArgs.Tokenize("--temp=C:\\lich\\temp --login Char").ToArray());

        Assert.Equal(
            new[] { "--temp", "C:\\Program Files\\lich\\temp" },
            LichArgs.Tokenize("--temp \"C:\\Program Files\\lich\\temp\"").ToArray());
    }

    [Theory]
    [InlineData("--login Char --temp \"/a b/temp", '"')]
    [InlineData("--login Char --temp '/a b/temp", '\'')]
    [InlineData("--temp \"", '"')]
    public void Tokenize_throws_on_an_unterminated_quote(string input, char quote)
    {
        // Fail fast: a half-parsed argument string would launch Lich with mangled
        // argv and fail later in ways that don't point back at the stray quote.
        var ex = Assert.Throws<FormatException>(() => LichArgs.Tokenize(input));
        Assert.Contains($"unterminated {quote} quote", ex.Message);
        Assert.Contains(input.IndexOf(quote).ToString(), ex.Message);
    }

    [Fact]
    public void Tokenize_accepts_a_quote_char_reopened_after_a_closed_pair()
    {
        // Closing resets the tracker — the second pair must not report the first.
        Assert.Equal(
            new[] { "--temp", "/a b", "--login", "My Char" },
            LichArgs.Tokenize("--temp \"/a b\" --login \"My Char\"").ToArray());
    }

    [Fact]
    public void Tokenize_preserves_an_explicitly_empty_argument()
    {
        Assert.Equal(new[] { "--temp", "" }, LichArgs.Tokenize("--temp \"\"").ToArray());
    }

    [Theory]
    // Space form — lich.rbw's regexes all require '='.
    [InlineData("--login Char --temp /a/b", "--temp", "--temp=PATH")]
    [InlineData("--home /a/b", "--home", "--home=PATH")]
    // Documented by `lich --help`, implemented nowhere.
    [InlineData("--temp-dir=/a/b", "--temp-dir=/a/b", "--temp=PATH")]
    [InlineData("--script-dir=/a/b", "--script-dir=/a/b", "--scripts=PATH")]
    [InlineData("--data-dir=/a/b", "--data-dir=/a/b", "--data=PATH")]
    public void TryFindIgnoredDirFlag_catches_what_lich_silently_drops(
        string args, string expectedProblem, string expectedFix)
    {
        Assert.True(LichArgs.TryFindIgnoredDirFlag(LichArgs.Tokenize(args), out var problem, out var fix));
        Assert.Equal(expectedProblem, problem);
        Assert.Equal(expectedFix, fix);
    }

    [Theory]
    [InlineData("--temp=/a/b --login Char --without-frontend")]
    [InlineData("--home=/a/b --scripts=/c/d --maps=/e/f")]
    [InlineData("--login Char --dragonrealms --genie --headless 8000")]
    // --hosts-dir takes a space-separated value and IS handled (argv_options.rb).
    [InlineData("--hosts-dir /a/b")]
    public void TryFindIgnoredDirFlag_passes_arguments_lich_honours(string args) =>
        Assert.False(LichArgs.TryFindIgnoredDirFlag(LichArgs.Tokenize(args), out _, out _));

    [Fact]
    public void TryParseDirFlag_reads_only_the_equals_form()
    {
        Assert.True(LichArgs.TryParseDirFlag(LichArgs.Tokenize("--temp=/a/b"), "--temp", out var value));
        Assert.Equal("/a/b", value);

        Assert.False(LichArgs.TryParseDirFlag(LichArgs.Tokenize("--temp /a/b"), "--temp", out _));
        Assert.False(LichArgs.TryParseDirFlag(LichArgs.Tokenize("--temp="), "--temp", out _));
    }
}
