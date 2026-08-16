using System;
using System.IO;
using System.Linq;
using Genie.Core.Alterations;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The Alterations subsystem — a port of Alteration Buddy (Djordje, GPL-3.0,
/// github.com/mj-colonel-panic/AlterationBuddy) into Genie 5 as a first-class
/// feature behind its own top-level menu.
///
/// These tests pin the two things a port must not get wrong: the composed
/// request string (players paste it verbatim to a merchant, so it is a
/// user-visible contract) and interop with the old <c>alterations.csv</c>. They
/// also pin the two upstream counter bugs we deliberately fixed, so a later
/// "restore G4 parity" pass cannot silently reintroduce them.
/// </summary>
public class AlterationDesignerTests : IDisposable
{
    private readonly string _root;

    public AlterationDesignerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie-alterations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── Result composition (Alteration Buddy UpdateResult parity) ────────────

    [Fact]
    public void Format_matches_alteration_buddy_output_exactly()
    {
        var design = new AlterationDesign
        {
            ShortTap = "a razor-edged scimitar",
            Tap      = "a wickedly curved scimitar with a razor edge",
            Look     = "The blade curves back on itself in a single unbroken arc.",
            Read     = "For she who does not yield"
        };

        Assert.Equal(
            "Short Tap: a razor-edged scimitar" +
            " \\ Tap: a wickedly curved scimitar with a razor edge" +
            " \\ Look: The blade curves back on itself in a single unbroken arc." +
            " \\ Read: \"For she who does not yield\"",
            AlterationFormatter.Format(design));
    }

    [Fact]
    public void Format_skips_blank_fields_and_their_separators()
    {
        var design = new AlterationDesign { ShortTap = "a plain ring", Read = "hold fast" };

        Assert.Equal("Short Tap: a plain ring \\ Read: \"hold fast\"",
                     AlterationFormatter.Format(design));
    }

    [Fact]
    public void Format_of_an_empty_design_is_empty()
    {
        Assert.Equal("", AlterationFormatter.Format(new AlterationDesign()));
    }

    [Fact]
    public void MultiLine_format_labels_one_field_per_line()
    {
        var design = new AlterationDesign { Tap = "a tap", Read = "a read" };

        Assert.Equal(
            "Tap: a tap" + Environment.NewLine + "Read: \"a read\"",
            AlterationFormatter.Format(design, AlterationResultFormat.MultiLine));
    }

    // ── Budgets ─────────────────────────────────────────────────────────────

    [Fact]
    public void Tap_and_look_budgets_use_the_documented_limits()
    {
        Assert.Equal(80,  AlterationValidator.TapBudget("").Limit);
        Assert.Equal(500, AlterationValidator.LookBudget("").Limit);
        Assert.Equal(50,  AlterationValidator.ReadCharacterBudget("").Limit);
        Assert.Equal(10,  AlterationValidator.ReadWordBudget("").Limit);
    }

    [Fact]
    public void An_empty_read_field_has_spent_no_words()
    {
        // Alteration Buddy computed 10 - "".Split(' ').Length, so a blank field
        // already reported nine words remaining. Blank must mean zero spent.
        var budget = AlterationValidator.ReadWordBudget("");

        Assert.Equal(0,  budget.Used);
        Assert.Equal(10, budget.Remaining);
        Assert.False(budget.IsOver);
    }

    [Fact]
    public void Runs_of_whitespace_count_as_one_word_break()
    {
        // Split(' ') counted "a  b" as three words. Collapsing empties fixes it.
        Assert.Equal(2, AlterationValidator.WordCount("a  b"));
        Assert.Equal(2, AlterationValidator.WordCount("  a\tb  "));
    }

    [Fact]
    public void Going_over_a_budget_reports_the_overage_not_a_negative_remainder()
    {
        var budget = AlterationValidator.TapBudget(new string('x', 83));

        Assert.True(budget.IsOver);
        Assert.Equal(-3, budget.Remaining);
        Assert.Equal("3 characters over.", budget.Describe());
    }

    [Fact]
    public void Budget_descriptions_are_singular_at_one()
    {
        Assert.Equal("1 character remaining.", new AlterationBudget(79, 80, "character").Describe());
        Assert.Equal("1 word over.",           new AlterationBudget(11, 10, "word").Describe());
    }

    [Fact]
    public void Short_tap_is_measured_per_word_against_the_fifteen_character_limit()
    {
        var budgets = AlterationValidator.ShortTapSegmentBudgets("a razor-edged scimitar");

        Assert.Equal(3, budgets.Count);
        Assert.Equal(new[] { 1, 11, 8 }, budgets.Select(b => b.Used));
        Assert.All(budgets, b => Assert.False(b.IsOver));
    }

    [Fact]
    public void Short_tap_flags_the_word_that_breaks_the_limit()
    {
        var budgets = AlterationValidator.ShortTapSegmentBudgets("an indistinguishable ring");

        Assert.False(budgets[0].IsOver);                      // "an"                 —  2
        Assert.True(budgets[1].IsOver);                       // "indistinguishable"  — 17
        Assert.False(budgets[2].IsOver);                      // "ring"               —  4
        Assert.Contains("one word is over", AlterationValidator.DescribeShortTap("an indistinguishable ring"));
        Assert.Contains("1/11/8", AlterationValidator.DescribeShortTap("a razor-edged scimitar"));
    }

    [Fact]
    public void Blank_short_tap_has_no_segments_and_no_hint()
    {
        Assert.Empty(AlterationValidator.ShortTapSegmentBudgets("   "));
        Assert.Equal("", AlterationValidator.DescribeShortTap(null));
    }

    [Fact]
    public void Problems_lists_every_field_that_is_over_budget()
    {
        var design = new AlterationDesign
        {
            Tap  = new string('x', 90),
            Look = new string('y', 510),
            Read = "one two three four five six seven eight nine ten eleven"
        };

        var problems = AlterationValidator.Problems(design);

        Assert.False(AlterationValidator.IsWithinLimits(design));
        Assert.Contains(problems, p => p.StartsWith("Tap:"));
        Assert.Contains(problems, p => p.StartsWith("Look:"));
        Assert.Contains(problems, p => p.StartsWith("Read:"));
    }

    [Fact]
    public void A_design_inside_every_budget_reports_no_problems()
    {
        var design = new AlterationDesign
        {
            ShortTap = "a silver ring",
            Tap      = "a slender silver ring",
            Look     = "Light catches the band.",
            Read     = "always"
        };

        Assert.True(AlterationValidator.IsWithinLimits(design));
        Assert.Empty(AlterationValidator.Problems(design));
    }

    // ── Display name ────────────────────────────────────────────────────────

    [Fact]
    public void DisplayName_falls_back_through_title_tap_shorttap()
    {
        Assert.Equal("My design", new AlterationDesign { Title = "My design", Tap = "t" }.DisplayName);
        Assert.Equal("t",         new AlterationDesign { Tap = "t", ShortTap = "s" }.DisplayName);
        Assert.Equal("s",         new AlterationDesign { ShortTap = "s" }.DisplayName);
        Assert.Equal("Untitled design", new AlterationDesign().DisplayName);
    }

    // ── Genie 4 interop ─────────────────────────────────────────────────────

    [Fact]
    public void Genie4_line_round_trips_all_four_fields()
    {
        var design = new AlterationDesign
        {
            ShortTap = "a ring", Tap = "a plain ring", Look = "It is plain.", Read = "hold fast"
        };

        var back = AlterationDesign.FromGenie4Line(design.ToGenie4Line());

        Assert.Equal(design.ShortTap, back.ShortTap);
        Assert.Equal(design.Tap,      back.Tap);
        Assert.Equal(design.Look,     back.Look);
        Assert.Equal(design.Read,     back.Read);
    }

    [Fact]
    public void Genie4_export_flattens_embedded_tabs_and_newlines()
    {
        // The format has no escaping, so a raw tab or newline would shift every
        // following field. Flattening loses formatting; not flattening loses data.
        var design = new AlterationDesign { Tap = "a\tring", Look = "line one\r\nline two" };

        var line = design.ToGenie4Line();

        Assert.Equal(4, line.Split('\t').Length);
        Assert.DoesNotContain('\n', line);
        Assert.Equal("line one line two", AlterationDesign.FromGenie4Line(line).Look);
    }

    [Fact]
    public void Short_genie4_rows_are_tolerated_rather_than_throwing()
    {
        var design = AlterationDesign.FromGenie4Line("a ring\ta plain ring");

        Assert.Equal("a ring",       design.ShortTap);
        Assert.Equal("a plain ring", design.Tap);
        Assert.Equal("",             design.Look);
        Assert.Equal("",             design.Read);
    }

    [Fact]
    public void Extra_tabs_fold_back_into_the_read_field_instead_of_being_dropped()
    {
        var design = AlterationDesign.FromGenie4Line("s\tt\tl\tr1\tr2");

        Assert.Equal("r1\tr2", design.Read);
    }

    [Fact]
    public void Importing_recovers_designs_that_alteration_buddy_wrote_across_lines()
    {
        // Upstream wrote a multiline Look field verbatim, producing physical lines
        // with no tab that its own reader turned into junk designs. Treat a
        // tab-less line as a continuation of the previous Look.
        var path = Path.Combine(_root, "alterations.csv");
        File.WriteAllLines(path, new[]
        {
            "a ring\ta plain ring\tThe band is worn smooth\thold fast",
            "and faintly warm to the touch.",
            "a cloak\ta dark cloak\tIt drinks the light.\t"
        });

        var imported = AlterationLibrary.ImportGenie4File(path);

        Assert.Equal(2, imported.Count);
        Assert.Equal("The band is worn smooth" + Environment.NewLine + "and faintly warm to the touch.",
                     imported[0].Look);
        Assert.Equal("a dark cloak", imported[1].Tap);
    }

    [Fact]
    public void Importing_a_missing_file_yields_an_empty_set()
    {
        Assert.Empty(AlterationLibrary.ImportGenie4File(Path.Combine(_root, "nope.csv")));
    }

    [Fact]
    public void ImportGenie4Into_appends_and_drops_blank_designs()
    {
        var path = Path.Combine(_root, "alterations.csv");
        File.WriteAllLines(path, new[] { "\t\t\t", "a ring\ta plain ring\t\t" });

        var library = new AlterationLibrary();
        library.Add(new AlterationDesign { Tap = "existing" });

        var added = library.ImportGenie4Into(path);

        Assert.Equal(1, added);
        Assert.Equal(2, library.Count);
        Assert.Equal("a plain ring", library.Designs[1].Tap);
    }

    // ── Completed / draft separation (Bardolf's request) ────────────────────

    private static AlterationLibrary LibraryOf(params (string tap, bool done)[] designs)
    {
        var library = new AlterationLibrary();
        foreach (var (tap, done) in designs)
            library.Add(new AlterationDesign { Tap = tap, IsCompleted = done });
        return library;
    }

    [Fact]
    public void Designs_default_to_draft()
    {
        // Libraries written before the completed flag existed have no such field;
        // everything in them must read as a draft, not as finished work.
        Assert.False(new AlterationDesign().IsCompleted);
    }

    [Fact]
    public void Display_order_puts_drafts_above_completed_work()
    {
        var library = LibraryOf(("done-1", true), ("draft-1", false), ("done-2", true), ("draft-2", false));

        var order = library.InDisplayOrder().Select(e => e.Design.Tap).ToList();

        Assert.Equal(new[] { "draft-1", "draft-2", "done-1", "done-2" }, order);
    }

    [Fact]
    public void Display_order_keeps_file_order_within_each_group()
    {
        // A stable sort matters: marking one design done should move that design
        // and shuffle nothing else.
        var library = LibraryOf(("a", false), ("b", false), ("c", false));
        library.SetCompleted(1, true);

        Assert.Equal(new[] { "a", "c", "b" }, library.InDisplayOrder().Select(e => e.Design.Tap));
    }

    [Fact]
    public void Entries_carry_their_library_index_not_their_row_position()
    {
        // This is the load-bearing property: display order != storage order, so a
        // row's position is not a valid index. Deleting or editing by row would
        // hit the wrong design.
        var library = LibraryOf(("done", true), ("draft", false));

        var rows = library.InDisplayOrder();

        Assert.Equal("draft", rows[0].Design.Tap);
        Assert.Equal(1,       rows[0].Index);
        Assert.Equal("done",  rows[1].Design.Tap);
        Assert.Equal(0,       rows[1].Index);
    }

    // NB: these are three Facts rather than one Theory over AlterationFilter.
    // An InlineData argument typed as a Genie.Core enum breaks xUnit DISCOVERY in
    // this project — attribute argument types are resolved before the
    // ModuleInitializer resolver in ModuleInit.cs can locate Genie.Core.dll
    // (referenced by HintPath with Private=false, since Core is a self-contained
    // exe and can't be a ProjectReference). Keep Core types out of attributes.
    [Fact]
    public void The_drafts_filter_selects_only_unfinished_designs()
    {
        var library = LibraryOf(("draft-1", false), ("done-1", true), ("draft-2", false));

        Assert.Equal(new[] { "draft-1", "draft-2" },
                     library.InDisplayOrder(AlterationFilter.Drafts).Select(e => e.Design.Tap));
    }

    [Fact]
    public void The_completed_filter_selects_only_finished_designs()
    {
        var library = LibraryOf(("draft-1", false), ("done-1", true), ("draft-2", false));

        Assert.Equal(new[] { "done-1" },
                     library.InDisplayOrder(AlterationFilter.Completed).Select(e => e.Design.Tap));
    }

    [Fact]
    public void The_all_filter_keeps_everything_in_display_order()
    {
        var library = LibraryOf(("draft-1", false), ("done-1", true), ("draft-2", false));

        Assert.Equal(new[] { "draft-1", "draft-2", "done-1" },
                     library.InDisplayOrder(AlterationFilter.All).Select(e => e.Design.Tap));
    }

    [Fact]
    public void Filtered_entries_still_carry_true_library_indexes()
    {
        var library = LibraryOf(("draft-1", false), ("done-1", true), ("draft-2", false));

        Assert.Equal(new[] { 1 }, library.InDisplayOrder(AlterationFilter.Completed).Select(e => e.Index));
        Assert.Equal(new[] { 0, 2 }, library.InDisplayOrder(AlterationFilter.Drafts).Select(e => e.Index));
    }

    [Fact]
    public void SetCompleted_flips_a_design_both_ways_and_reports_out_of_range()
    {
        var library = LibraryOf(("a", false));

        Assert.True(library.SetCompleted(0, true));
        Assert.True(library.Designs[0].IsCompleted);

        Assert.True(library.SetCompleted(0, false));
        Assert.False(library.Designs[0].IsCompleted);

        Assert.False(library.SetCompleted(5, true));
        Assert.False(library.SetCompleted(-1, true));
    }

    [Fact]
    public void Counts_split_drafts_from_completed()
    {
        var library = LibraryOf(("a", false), ("b", true), ("c", true));

        Assert.Equal(1, library.DraftCount);
        Assert.Equal(2, library.CompletedCount);
        Assert.Equal(3, library.Count);
    }

    [Fact]
    public void Completed_state_survives_a_save_and_reload()
    {
        var path = Path.Combine(_root, "alterations.json");
        LibraryOf(("draft", false), ("done", true)).Save(path);

        var reloaded = new AlterationLibrary();
        reloaded.Load(path);

        Assert.False(reloaded.Designs[0].IsCompleted);
        Assert.True(reloaded.Designs[1].IsCompleted);
    }

    [Fact]
    public void A_library_file_predating_the_completed_flag_loads_as_all_drafts()
    {
        var path = Path.Combine(_root, "alterations.json");
        File.WriteAllText(path, """[{"Title":"","ShortTap":"","Tap":"old","Look":"","Read":"","Notes":""}]""");

        var library = new AlterationLibrary();
        library.Load(path);

        Assert.Equal(1, library.DraftCount);
        Assert.Equal(0, library.CompletedCount);
    }

    [Fact]
    public void Completed_state_is_not_representable_in_the_genie4_format()
    {
        // Four fields, no more -- so a round-trip through the old format loses it.
        // Documented rather than worked around; the same is true of Title/Notes.
        var design = new AlterationDesign { Tap = "a plain ring", IsCompleted = true };

        Assert.False(AlterationDesign.FromGenie4Line(design.ToGenie4Line()).IsCompleted);
    }

    [Fact]
    public void Clone_carries_the_completed_flag()
    {
        Assert.True(new AlterationDesign { IsCompleted = true }.Clone().IsCompleted);
    }

    // ── JSON store ──────────────────────────────────────────────────────────

    [Fact]
    public void Library_round_trips_through_json_including_g5_only_fields()
    {
        var path = Path.Combine(_root, "alterations.json");
        var library = new AlterationLibrary();
        library.Add(new AlterationDesign
        {
            Title = "Festival ring", ShortTap = "a ring", Tap = "a plain ring",
            Look  = "line one" + Environment.NewLine + "line two",
            Read  = "hold fast", Notes = "Ilithi, 470"
        });
        library.Save(path);

        var reloaded = new AlterationLibrary();
        reloaded.Load(path);

        Assert.Equal(1, reloaded.Count);
        var d = reloaded.Designs[0];
        Assert.Equal("Festival ring", d.Title);
        Assert.Equal("Ilithi, 470",   d.Notes);
        Assert.Contains(Environment.NewLine, d.Look);
    }

    [Fact]
    public void Loading_a_missing_library_clears_rather_than_throwing()
    {
        var library = new AlterationLibrary();
        library.Add(new AlterationDesign { Tap = "stale" });

        library.Load(Path.Combine(_root, "absent.json"));

        Assert.Equal(0, library.Count);
    }

    [Fact]
    public void Loading_a_corrupt_library_throws_rather_than_silently_emptying_it()
    {
        // A swallowed parse error here would present an empty designer, and the
        // next Save would overwrite the user's real designs with nothing.
        var path = Path.Combine(_root, "alterations.json");
        File.WriteAllText(path, "{ this is not json");

        Assert.ThrowsAny<Exception>(() => new AlterationLibrary().Load(path));
    }

    [Fact]
    public void Save_creates_the_config_directory_if_it_is_missing()
    {
        var path = Path.Combine(_root, "nested", "dir", "alterations.json");

        new AlterationLibrary().Save(path);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Export_writes_one_tab_delimited_line_per_design()
    {
        var path = Path.Combine(_root, "out.csv");
        var library = new AlterationLibrary();
        library.Add(new AlterationDesign { Title = "dropped", ShortTap = "a ring", Tap = "a plain ring" });
        library.Add(new AlterationDesign { ShortTap = "a cloak", Tap = "a dark cloak" });

        library.ExportGenie4File(path);
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(4, l.Split('\t').Length));
        Assert.DoesNotContain("dropped", File.ReadAllText(path));
    }
}
