using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Two Genie 4 variable behaviours the engine did not have, both of which
/// failed quietly rather than loudly — the class of bug that survives because
/// nobody can see it.
///
/// <list type="bullet">
///   <item><c>%list.length</c> — Genie 4's built-in pipe-list count.</item>
///   <item><c>%name = value</c> — Genie 4's bare sigil assignment, which was
///     falling through to the game socket.</item>
/// </list>
/// </summary>
public class ScriptG4VarParityTests
{
    private static List<string> Run(string body, out List<string> sent)
    {
        var echoed = new List<string>();
        var outbound = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_g4var_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: c => outbound.Add(c),
                                          echo: l => echoed.Add(l));
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 300; i++) engine.Tick();
            sent = outbound;
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ── .length ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a|b|c",  "3")]
    [InlineData("a|b",    "2")]
    [InlineData("solo",   "1")]
    // Genie 4 computes count('|') + 1, so an empty list reads 1, not 0. The
    // corpus loops are written against that arithmetic.
    [InlineData("",       "1")]
    [InlineData("a||c",   "3")]
    public void Length_suffix_counts_pipe_separated_elements(string list, string expected)
    {
        var o = Run($"var list {list}\necho LEN=%list.length\n", out _);
        Assert.Contains($"LEN={expected}", o);
    }

    [Fact]
    public void Length_suffix_shadows_a_stored_variable_of_that_name()
    {
        // The cyclic.cmd shape: the script stores the pipe COUNT under the
        // dotted name, then reads it back expecting the built-in's count+1.
        // Genie 4 checks the built-in first, so the stored 2 is shadowed by 3.
        const string body =
            "var cyclics a|b|c\n" +
            "eval cyclics.length count(\"%cyclics\",\"|\")\n" +
            "echo LEN=%cyclics.length\n";

        Assert.Contains("LEN=3", Run(body, out _));
    }

    [Fact]
    public void Length_suffix_works_on_globals_too()
    {
        // Seeded directly: #var is deliberately forwarded to the host command
        // engine (so a scripted write behaves like a typed one, including
        // `#var save`), and the bare test engine has no host.
        var o = RunWithGlobal("glist", "a|b|c|d", "echo LEN=$glist.length\n");
        Assert.Contains("LEN=4", o);
    }

    /// <summary>Runs a script with one global pre-seeded.</summary>
    private static List<string> RunWithGlobal(string name, string value, string body)
    {
        var echoed = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "gc_g4glob_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "t.cmd"), body);
            var engine = new ScriptEngine(dir, new TypeAheadSession(),
                                          sendCommand: _ => { }, echo: l => echoed.Add(l));
            engine.Globals[name] = value;
            engine.TryStart("t", new List<string>());
            for (int i = 0; i < 300; i++) engine.Tick();
            return echoed;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void An_undefined_base_leaves_the_text_alone()
    {
        // No `nothere` variable, so there is no list to count — the name stays
        // literal, which is Genie 4's undefined-variable policy.
        var o = Run("echo LEN=[%nothere.length]\n", out _);
        Assert.Contains("LEN=[%nothere.length]", o);
    }

    [Fact]
    public void A_dotted_variable_that_is_not_dot_length_still_resolves_normally()
    {
        var o = Run("var this.array x|y\necho V=%this.array\n", out _);
        Assert.Contains("V=x|y", o);
    }

    // ── bare assignment ──────────────────────────────────────────────────

    [Fact]
    public void Bare_local_assignment_sets_the_variable()
    {
        var o = Run("%Failure = 0\necho V=%Failure\n", out var sent);

        Assert.Contains("V=0", o);
        Assert.Empty(sent);          // and it must NOT reach the game
    }

    [Fact]
    public void Bare_assignment_without_an_equals_sign_also_works()
    {
        // Genie 4 accepts `%name value` as well as `%name = value`.
        var o = Run("%item rope\necho V=%item\n", out _);
        Assert.Contains("V=rope", o);
    }

    [Fact]
    public void Reassigning_an_already_defined_variable_still_assigns()
    {
        // The case that forced this to be a LOAD-time rewrite: by dispatch the
        // line would read "0 = 1" with nothing left to recognise.
        const string body =
            "%Failure = 0\n" +
            "%Failure = 1\n" +
            "echo V=%Failure\n";

        var o = Run(body, out var sent);
        Assert.Contains("V=1", o);
        Assert.Empty(sent);
    }

    [Fact]
    public void Bare_assignment_rewrites_to_the_right_statement()
    {
        // Asserted at the parser, which is where the rewrite lives — and for
        // the global form it has to be, since applying it needs the host
        // command engine that `#var` is deliberately routed through.
        Assert.Equal("setvariable Failure 0", ParsedLine("%Failure = 0"));
        Assert.Equal("setvariable item rope", ParsedLine("%item rope"));
        Assert.Equal("#var gvar world",       ParsedLine("$gvar = world"));
    }

    /// <summary>The single parsed statement text for a one-line script.</summary>
    private static string ParsedLine(string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gc_g4parse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var inst = ScriptParser.Parse("t", dir, source + "\n");
            foreach (var l in inst.Lines)
                if (l.Trimmed.Length > 0) return l.Trimmed;
            return string.Empty;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void The_value_may_reference_another_variable()
    {
        var o = Run("var src rope\n%dst = %src\necho V=%dst\n", out _);
        Assert.Contains("V=rope", o);
    }

    [Fact]
    public void A_value_containing_an_equals_sign_survives()
    {
        // Genie 4 replaces every " = " in the line; we replace only the first,
        // so this lands intact. No corpus line depends on either behaviour.
        var o = Run("%expr = a = b\necho V=%expr\n", out _);
        Assert.Contains("V=a = b", o);
    }

    // ── what must NOT be rewritten ───────────────────────────────────────

    [Fact]
    public void A_line_that_merely_starts_with_a_variable_is_still_a_command()
    {
        // %cmd holds a game command; the line is the command, not an
        // assignment. Requires a name followed by space AND a value, so the
        // guard keys on the shape rather than the sigil alone.
        var o = Run("var cmd look\n%cmd\n", out var sent);

        Assert.Contains("look", sent);
    }

    [Fact]
    public void A_bare_sigil_on_its_own_is_left_alone()
    {
        var o = Run("echo START\n%\necho END\n", out _);

        Assert.Contains("START", o);
        Assert.Contains("END",   o);
    }
}
