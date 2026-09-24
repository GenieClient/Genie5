using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parsing;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #362 — Genie 4's <c>{display:command}</c> inline click markup, with the
/// Genie 4 pattern (<c>{([^{]*):([^{]*)}</c>) and its in-place collapse.
/// </summary>
public class InlineClickMarkupTests
{
    private static string Linked(InlineClickMarkup.Result r, LinkSpan l) => r.Text.Substring(l.Start, l.Length);

    [Fact]
    public void The_issue_example_gives_two_independent_links()
    {
        var r = InlineClickMarkup.Apply("Go {north:north} or {south:go south}");

        Assert.Equal("Go north or south", r.Text);
        Assert.Equal(2, r.Links!.Count);
        Assert.Equal(("north", "north"), (Linked(r, r.Links[0]), r.Links[0].Command));
        Assert.Equal(("south", "go south"), (Linked(r, r.Links[1]), r.Links[1].Command));
        Assert.All(r.Links, l => Assert.False(l.IsUrl));
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("a {brace} but no colon")]
    [InlineData("a colon: but no brace")]
    [InlineData("")]
    public void Lines_without_markup_come_back_untouched(string text)
    {
        var bolds = new[] { new BoldSpan(0, 1) };
        var r = InlineClickMarkup.Apply(text, bolds: text.Length > 0 ? bolds : null);

        Assert.Equal(text, r.Text);
        Assert.Null(r.Links);
        if (text.Length > 0) Assert.Same(bolds, r.Bolds);
    }

    [Fact]
    public void The_display_runs_to_the_last_colon_as_in_genie4()
    {
        // Both groups greedy: "{HP: 50:look}" shows "HP: 50", runs "look".
        var r = InlineClickMarkup.Apply("{HP: 50:look}");

        Assert.Equal("HP: 50", r.Text);
        Assert.Equal("look", r.Links!.Single().Command);
    }

    [Fact]
    public void An_opening_brace_can_not_be_inside_a_link()
    {
        var r = InlineClickMarkup.Apply("{{a:b}");

        Assert.Equal("{a", r.Text);
        Assert.Equal(new LinkSpan(1, 1, "b"), r.Links!.Single());
    }

    [Theory]
    [InlineData("{:look}")]
    [InlineData("{look:}")]
    [InlineData("{look:   }")]
    public void An_empty_display_or_command_is_left_as_text(string text)
    {
        var r = InlineClickMarkup.Apply(text);

        Assert.Equal(text, r.Text);
        Assert.Null(r.Links);
    }

    [Fact]
    public void Spans_after_a_collapse_shift_left_by_the_removed_characters()
    {
        //            0         1         2
        //            0123456789012345678901234
        const string text = "{Go:north} then the orc";
        var bold   = new BoldSpan(20, 3);           // "orc"
        var preset = new PresetSpan(11, 4, "p");   // "then"

        var r = InlineClickMarkup.Apply(text, bolds: new[] { bold }, presets: new[] { preset });

        Assert.Equal("Go then the orc", r.Text);
        Assert.Equal("orc",  r.Text.Substring(r.Bolds![0].Start, r.Bolds[0].Length));
        Assert.Equal("then", r.Text.Substring(r.Presets![0].Start, r.Presets[0].Length));
    }

    [Fact]
    public void A_span_covering_the_markup_is_clamped_to_the_collapsed_text()
    {
        var r = InlineClickMarkup.Apply("say {hi:wave} now", bolds: new[] { new BoldSpan(0, 17) });

        Assert.Equal("say hi now", r.Text);
        Assert.Equal(new BoldSpan(0, 10), r.Bolds!.Single());
    }

    [Fact]
    public void Existing_links_are_kept_and_shifted_unless_they_overlap_the_markup()
    {
        const string text = "{a:x} north {b:y}";
        var parserLinks = new List<LinkSpan>
        {
            new(6, 5, "go north"),    // "north" — outside the markup, kept
            new(12, 5, "overlap"),    // covers "{b:y}" — dropped
        };

        var r = InlineClickMarkup.Apply(text, links: parserLinks);

        Assert.Equal("a north b", r.Text);
        Assert.Equal(new[] { "x", "go north", "y" }, r.Links!.Select(l => l.Command));
        Assert.Equal("north", Linked(r, r.Links![1]));
        Assert.DoesNotContain(r.Links, l => l.Command == "overlap");
    }
}
