using System;
using System.Linq;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Genie.App.ScriptEditing;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #243 — syntax highlighting in the built-in script editor. The
/// <c>.cmd</c> definition ships as an embedded .xshd; JavaScript reuses
/// AvaloniaEdit's own. Both are repainted per theme, and the .cmd rules follow
/// the script engine (a <c>#</c> comment only at the start of a line, labels
/// only as a lone word with a colon, commands only where a command can stand).
/// </summary>
public class ScriptSyntaxTests
{
    [Fact]
    public void Cmd_xshd_loads_with_the_expected_colours_and_rule_sets()
    {
        var xshd = ScriptSyntax.LoadCmdXshd();
        Assert.Equal("Genie Script", xshd.Name);
        Assert.Contains(".cmd", xshd.Extensions);
        Assert.Contains(".inc", xshd.Extensions);

        var colours = xshd.Elements.OfType<XshdColor>().Select(c => c.Name).ToHashSet();
        foreach (var name in new[] { "Comment", "String", "Label", "Variable", "Keyword", "MetaCommand", "Number" })
            Assert.Contains(name, colours);

        var ruleSets = xshd.Elements.OfType<XshdRuleSet>().ToList();
        Assert.Contains(ruleSets, r => r.Name is null);           // the main rule set
        Assert.Contains(ruleSets, r => r.Name == "StringBody");

        var def = ScriptSyntax.Get(js: false, dark: true);
        Assert.NotNull(def.MainRuleSet);
        Assert.NotNull(def.GetNamedRuleSet("StringBody"));
    }

    [Fact]
    public void JavaScript_reuses_the_shipped_definition()
    {
        var def = ScriptSyntax.Get(js: true, dark: false);
        Assert.Equal("JavaScript", def.Name);
        Assert.NotNull(def.GetNamedColor("JavaScriptKeyWords"));
    }

    [Theory]
    [InlineData("hunt.cmd", "Genie Script")]
    [InlineData("helpers.INC", "Genie Script")]
    [InlineData("loot.js", "JavaScript")]
    public void The_extension_picks_the_definition(string file, string expected)
    {
        Assert.Equal(expected, ScriptSyntax.ForFile(file, dark: true)!.Name);
        Assert.Null(ScriptSyntax.ForFile("notes.txt", dark: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_named_colour_is_repainted_for_the_theme(bool dark)
    {
        foreach (var js in new[] { false, true })
        {
            var def = ScriptSyntax.Get(js, dark);
            foreach (var colour in def.NamedHighlightingColors)
            {
                var expected = ScriptSyntax.ColorFor(ScriptSyntax.RoleFor(colour.Name), dark);
                Assert.Equal(expected, colour.Foreground!.GetColor(null));
            }
        }
        // The stock JavaScript keyword blue is gone on the dark palette.
        Assert.NotEqual(Colors.Blue, ScriptSyntax.Get(true, dark: true).GetNamedColor("JavaScriptKeyWords").Foreground!.GetColor(null));
    }

    // ── what actually gets coloured ─────────────────────────────────────────

    /// <summary>The colour names covering each piece of <paramref name="line"/>.</summary>
    private static (string Text, string Colour)[] Sections(string line)
    {
        var doc = new TextDocument(line);
        var hl  = new DocumentHighlighter(doc, ScriptSyntax.Get(js: false, dark: true));
        var hlLine = hl.HighlightLine(1);
        return hlLine.Sections
            .Select(s => (line.Substring(s.Offset, s.Length).Trim(), s.Color.Name ?? ""))
            .ToArray();
    }

    private static string? ColourOf(string line, string piece)
        => Sections(line).Where(s => s.Text == piece).Select(s => s.Colour).FirstOrDefault();

    [Theory]
    [InlineData("# a comment")]
    [InlineData("   #goto LABEL")]
    [InlineData("#debug 10")]
    public void A_hash_at_the_start_of_a_line_comments_out_the_whole_line(string line)
    {
        var s = Sections(line);
        Assert.Single(s);
        Assert.Equal("Comment", s[0].Colour);
        Assert.Equal(line.Trim(), s[0].Text);
    }

    [Fact]
    public void A_hash_command_later_in_the_line_is_live()
    {
        Assert.Equal("Keyword", ColourOf("put #echo done", "put"));
        Assert.Equal("MetaCommand", ColourOf("put #echo done", "#echo"));
    }

    [Theory]
    [InlineData("loop:")]
    [InlineData("  attack.start:")]
    public void A_lone_word_with_a_colon_is_a_label(string line)
        => Assert.Equal("Label", ColourOf(line, line.Trim()));

    [Fact]
    public void Text_with_a_colon_is_not_a_label()
        => Assert.DoesNotContain(Sections("echo Note: done"), s => s.Colour == "Label");

    [Fact]
    public void Commands_light_up_only_where_a_command_can_start()
    {
        Assert.Equal("Keyword", ColourOf("waitfor You stand", "waitfor"));
        Assert.Equal("Keyword", ColourOf("echo You wait for the ferry", "echo"));
        Assert.Null(ColourOf("echo You wait for the ferry", "wait"));
        Assert.Equal("Keyword", ColourOf("if $standing = 1 then goto done", "then"));
        Assert.Equal("Keyword", ColourOf("if $standing = 1 then goto done", "goto"));
        Assert.Equal("Keyword", ColourOf("action put #beep when You are stunned", "put"));
    }

    [Fact]
    public void Variables_are_coloured_inside_and_outside_strings()
    {
        Assert.Equal("Variable", ColourOf("put attack %target", "%target"));
        Assert.Equal("Variable", ColourOf("if \"$righthand\" = \"Empty\" then goto x", "$righthand"));
        Assert.Contains(Sections("if \"$righthand\" = \"Empty\" then goto x"), s => s.Colour == "String");
        Assert.Equal("Variable", ColourOf("echo %1", "%1"));
    }
}
