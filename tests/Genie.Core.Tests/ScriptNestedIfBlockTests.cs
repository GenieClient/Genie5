using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// #228 — nested `if … then {` blocks. FindMatchingBrace only counted a bare
/// "{" line as an opener, so a nested `if … then {` header's brace was missed
/// and the nested block's '}' was paired with the OUTER if. A false outer
/// condition then jumped INTO its own body (right past the nested block) and
/// executed the tail lines. These tests drive the real engine end-to-end,
/// mirroring the issue repro.
/// </summary>
public class ScriptNestedIfBlockTests
{
    private static List<string> RunFixture(string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_nestif_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 200; i++) engine.Tick();
            return echoed.FindAll(l => l.StartsWith("T:"));
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // The exact shape from the issue: mallet in hand ⇒ the WHOLE outer block
    // must be skipped, including the lines after the nested if.
    private const string IssueRepro =
        "if (!contains(\"%rh\", \"mallet\")) then {\n" +
        "    echo T:A\n" +
        "    if (\"%rh\" != \"Empty\") then {\n" +
        "        echo T:B\n" +
        "    }\n" +
        "    echo T:C\n" +
        "}\n" +
        "echo T:DONE\n";

    [Fact]
    public void False_outer_if_skips_entire_block_including_nested_if()
    {
        var o = RunFixture("var rh silversteel mallet\n" + IssueRepro);

        Assert.DoesNotContain("T:A", o);
        Assert.DoesNotContain("T:B", o);
        Assert.DoesNotContain("T:C", o);
        Assert.Contains("T:DONE", o);
    }

    [Fact]
    public void True_outer_if_with_true_nested_if_runs_everything()
    {
        var o = RunFixture("var rh longsword\n" + IssueRepro);

        Assert.Equal(new[] { "T:A", "T:B", "T:C", "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void True_outer_if_with_false_nested_if_skips_only_inner_block()
    {
        var o = RunFixture("var rh Empty\n" + IssueRepro);

        Assert.Equal(new[] { "T:A", "T:C", "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void Nested_next_line_brace_inside_inline_brace_block()
    {
        // Mixed styles: outer `then {`, inner brace on its own line.
        var o = RunFixture(
            "var x 1\n" +
            "if (%x = 2) then {\n" +
            "    echo T:A\n" +
            "    if (%x = 1) then\n" +
            "    {\n" +
            "        echo T:B\n" +
            "    }\n" +
            "    echo T:C\n" +
            "}\n" +
            "echo T:DONE\n");

        Assert.Equal(new[] { "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void Three_levels_of_nesting_skip_as_one_block()
    {
        var o = RunFixture(
            "var x 1\n" +
            "if (%x = 2) then {\n" +
            "    echo T:A\n" +
            "    if (%x = 1) then {\n" +
            "        echo T:B\n" +
            "        if (%x = 1) then {\n" +
            "            echo T:C\n" +
            "        }\n" +
            "        echo T:D\n" +
            "    }\n" +
            "    echo T:E\n" +
            "}\n" +
            "echo T:DONE\n");

        Assert.Equal(new[] { "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void False_outer_if_with_nested_block_still_reaches_its_else()
    {
        var o = RunFixture(
            "var x 1\n" +
            "if (%x = 2) then {\n" +
            "    echo T:A\n" +
            "    if (%x = 1) then {\n" +
            "        echo T:B\n" +
            "    }\n" +
            "    echo T:C\n" +
            "}\n" +
            "else\n" +
            "{\n" +
            "    echo T:E\n" +
            "}\n" +
            "echo T:DONE\n");

        Assert.Equal(new[] { "T:E", "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void True_branch_with_nested_block_skips_its_else()
    {
        var o = RunFixture(
            "var x 2\n" +
            "if (%x = 2) then {\n" +
            "    echo T:A\n" +
            "    if (%x = 1) then {\n" +
            "        echo T:B\n" +
            "    }\n" +
            "    echo T:C\n" +
            "}\n" +
            "else\n" +
            "{\n" +
            "    echo T:E\n" +
            "}\n" +
            "echo T:DONE\n");

        Assert.Equal(new[] { "T:A", "T:C", "T:DONE" }, o.ToArray());
    }

    [Fact]
    public void While_loop_containing_nested_if_terminates_at_its_own_brace()
    {
        var o = RunFixture(
            "var i 0\n" +
            "while (%i < 2) then {\n" +
            "    if (%i = 0) then {\n" +
            "        echo T:FIRST\n" +
            "    }\n" +
            "    math i add 1\n" +
            "}\n" +
            "echo T:DONE-%i\n");

        Assert.Equal(new[] { "T:FIRST", "T:DONE-2" }, o.ToArray());
    }
}
