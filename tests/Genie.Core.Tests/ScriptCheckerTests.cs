using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #239 — report every problem in a script at once, without running it.
/// Each rule mirrors what the engine would do on reaching the line; the false
/// positives found by running the checker across the community corpus (a trailing
/// "then {", "} else", `gosub clear`, an empty match pattern) are pinned as clean.
/// </summary>
public sealed class ScriptCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_scriptcheck_" + Guid.NewGuid().ToString("N"));

    public ScriptCheckerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private IReadOnlyList<ScriptChecker.Issue> Check(string body, string name = "t")
    {
        var path = Path.Combine(_dir, name + ".cmd");
        File.WriteAllText(path, body);
        return ScriptChecker.Check(ScriptParser.Parse(name, _dir, body, path));
    }

    private static string Only(IReadOnlyList<ScriptChecker.Issue> issues) => Assert.Single(issues).Message;

    [Fact]
    public void A_clean_script_has_no_findings()
    {
        Assert.Empty(Check(
            "start:\n" +
            "  match done You arrive\n" +
            "  put look\n" +
            "  matchwait 5\n" +
            "  if (%x > 1) then goto done\n" +
            "  gosub helper\n" +
            "  action put stand when ^You fall\n" +
            "  exit\n" +
            "helper:\n" +
            "  return\n" +
            "done:\n" +
            "  exit\n"));
    }

    [Fact]
    public void Goto_and_gosub_to_a_missing_label()
    {
        var issues = Check("goto nowhere\ngosub alsonowhere with args\nhere:\n");

        Assert.Equal(2, issues.Count);
        Assert.Contains("'goto nowhere' — no such label", issues[0].Message);
        Assert.Contains("'gosub alsonowhere' — no such label", issues[1].Message);
        Assert.Equal(1, issues[0].Line);
    }

    [Fact]
    public void A_match_to_a_missing_label_can_never_fire()
    {
        Assert.Contains("can never fire", Only(Check("matchre gone ^foo\nput look\nmatchwait\n")));
    }

    [Theory]
    [InlineData("if (%x > 1) goto a\na:\n", "'if' missing 'then'")]
    [InlineData("if (\"$guild\" = Thief\") goto a\na:\n", "unbalanced \" quotes?")]
    [InlineData("while %x < 3\na:\n", "'while' missing 'then'")]
    [InlineData("if (toupper(\"%a\") = NO then goto a\na:\n", "unbalanced parentheses")]
    public void Conditions(string body, string expected)
    {
        Assert.Contains(expected, Only(Check(body)));
    }

    [Fact]
    public void An_action_without_when()
    {
        Assert.Contains("missing 'when'", Only(Check("action put stand\n")));
    }

    [Theory]
    [InlineData("action on\n")]
    [InlineData("action (combat) off\n")]
    [InlineData("action remove ^You fall\n")]
    [InlineData("action clear\n")]
    public void Action_control_forms_are_fine(string body)
    {
        Assert.Empty(Check(body));
    }

    [Fact]
    public void Block_balance_counts_the_forms_real_scripts_use()
    {
        // Trailing "then {", and "} else" on one line — both from working community scripts.
        Assert.Empty(Check(
            "if (%a = 1) then {\n" +
            "  echo one\n" +
            "} else\n" +
            "{\n" +
            "  echo two\n" +
            "}\n"));
        Assert.Contains("never closed", Only(Check("if (%a = 1) then {\n  echo one\n")));
        Assert.Contains("no matching '{'", Only(Check("echo one\n}\n")));
    }

    [Fact]
    public void Built_ins_and_dynamic_targets_are_not_flagged()
    {
        Assert.Empty(Check(
            "gosub clear\n" +          // built in: wipes the return stack
            "goto %next\n" +          // only known at runtime
            "gosub $target\n" +
            "match catchall\n" +      // empty pattern — matches any line
            "catchall:\n"));
    }

    [Fact]
    public void A_duplicate_label_is_reported_at_the_one_that_wins()
    {
        var i = Assert.Single(Check("top:\necho a\ntop:\necho b\n"));
        Assert.Equal(3, i.Line);
        Assert.Contains("defined 2 times", i.Message);
    }

    [Fact]
    public void Include_problems_and_findings_inside_an_include_are_marked()
    {
        File.WriteAllText(Path.Combine(_dir, "lib.inc"), "goto callers_label\n");
        var issues = Check("include lib.inc\ninclude missing.inc\n");

        Assert.Contains(issues, i => i.Message.Contains("include not found: missing.inc") && !i.InInclude);
        var inLib = Assert.Single(issues, i => i.InInclude);
        Assert.Equal("lib", inLib.Origin);
        Assert.EndsWith("[in an include]", inLib.ToString());
    }

    [Fact]
    public void The_engine_reports_through_scriptcheck()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.cmd"), "goto nowhere\n");
        File.WriteAllText(Path.Combine(_dir, "good.cmd"), "echo hi\n");
        var engine = new ScriptEngine(_dir, new TypeAheadSession(), _ => { }, _ => { });

        var bad = engine.CheckScript("bad");
        Assert.Equal("[scriptcheck] bad.cmd: 1 problem (1 line):", bad[0]);
        Assert.Contains("bad:1 'goto nowhere'", bad[1]);
        Assert.StartsWith("[scriptcheck] good.cmd: no problems found", Assert.Single(engine.CheckScript("good")));
        Assert.StartsWith("[scriptcheck] not found: nope", Assert.Single(engine.CheckScript("nope")));
        Assert.Empty(engine.Instances);   // nothing ran
    }
}
