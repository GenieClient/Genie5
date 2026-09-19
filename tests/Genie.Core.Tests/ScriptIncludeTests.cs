using System;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Coverage for <c>include</c> expansion in <see cref="ScriptParser"/> — the
/// Genie 4 <c>AppendFile</c> splice, and the include-once guard that keeps a
/// cycle bounded without dropping files a player meant to load.
///
/// <para>Public #347: the guard used to be keyed on each file's extension-stripped
/// STEM, with the running script seeded into the same set, so two shapes players
/// actually write vanished silently — a script including a sibling of its own name
/// (<c>foo.cmd</c> + <c>foo.inc</c>), and two includes sharing a stem
/// (<c>x.inc</c> + <c>x.cmd</c>). Nothing was reported: the file resolved, the
/// guard discarded it, and the first symptom was a <c>gosub</c> into a label that
/// no longer existed. The guard is keyed on the resolved path now, which is the
/// closer analogue of Genie 4's argument-keyed <c>m_oScriptFiles</c>.</para>
/// </summary>
public class ScriptIncludeTests : IDisposable
{
    private readonly string _dir;

    public ScriptIncludeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_include_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string Write(string relativeName, string body)
    {
        var path = Path.Combine(_dir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>Parse as the engine does — the root's own path seeds the
    /// include-once set (see <c>ScriptEngine.StartInstance</c>).</summary>
    private ScriptInstance ParseFile(string rootName, string body)
    {
        var path = Write(rootName, body);
        return ScriptParser.Parse(Path.GetFileNameWithoutExtension(rootName), _dir,
                                  File.ReadAllText(path), path);
    }

    private static int CountLines(ScriptInstance inst, string trimmed)
        => inst.Lines.Count(l => l.Trimmed == trimmed);

    // ── #347: the two shapes the stem-keyed guard dropped ───────────────────

    [Fact]
    public void Include_sharing_the_root_scripts_stem_still_expands()
    {
        Write("main.inc", "HELPER:\n  echo from the include\n  return\n");

        var inst = ParseFile("main.cmd", "gosub HELPER\nexit\ninclude main.inc\n");

        Assert.True(inst.Labels.ContainsKey("HELPER"),
            "label from main.inc is missing; labels = " + string.Join(",", inst.Labels.Keys));
        Assert.Equal(1, CountLines(inst, "echo from the include"));
    }

    [Fact]
    public void Two_includes_sharing_a_stem_both_expand()
    {
        Write("shared.inc", "FROM_INC:\n  return\n");
        Write("shared.cmd", "FROM_CMD:\n  return\n");

        var inst = ParseFile("main.cmd", "include shared.inc\ninclude shared.cmd\n");

        Assert.True(inst.Labels.ContainsKey("FROM_INC") && inst.Labels.ContainsKey("FROM_CMD"),
            "labels = " + string.Join(",", inst.Labels.Keys));
    }

    // ── the guard must still guard ──────────────────────────────────────────

    [Fact]
    public void The_same_file_included_twice_expands_once()
    {
        Write("once.inc", "ONCE:\n  echo only once\n  return\n");

        var inst = ParseFile("main.cmd", "include once.inc\ninclude once.inc\n");

        Assert.Equal(1, CountLines(inst, "echo only once"));
    }

    [Fact]
    public void The_same_file_reached_by_two_spellings_expands_once()
    {
        Write("lib/dup.inc", "DUP:\n  echo one copy\n  return\n");

        // Second spelling walks back out of a sibling directory to the same file.
        Write("other/placeholder.inc", "\n");
        var inst = ParseFile("main.cmd",
            "include lib" + Path.DirectorySeparatorChar + "dup.inc\n" +
            "include other" + Path.DirectorySeparatorChar + ".." +
                              Path.DirectorySeparatorChar + "lib" +
                              Path.DirectorySeparatorChar + "dup.inc\n");

        Assert.Equal(1, CountLines(inst, "echo one copy"));
    }

    [Fact]
    public void A_cycle_between_two_includes_terminates_and_expands_each_once()
    {
        Write("a.inc", "A_LABEL:\n  echo in a\n  return\ninclude b.inc\n");
        Write("b.inc", "B_LABEL:\n  echo in b\n  return\ninclude a.inc\n");

        var inst = ParseFile("main.cmd", "include a.inc\n");

        Assert.Equal(1, CountLines(inst, "echo in a"));
        Assert.Equal(1, CountLines(inst, "echo in b"));
        Assert.True(inst.Labels.ContainsKey("A_LABEL") && inst.Labels.ContainsKey("B_LABEL"),
            "labels = " + string.Join(",", inst.Labels.Keys));
    }

    [Fact]
    public void A_script_that_includes_itself_expands_its_body_once()
    {
        var inst = ParseFile("self.cmd", "echo body line\ninclude self.cmd\n");

        Assert.Equal(1, CountLines(inst, "echo body line"));
    }

    // ── splice shape ────────────────────────────────────────────────────────

    [Fact]
    public void Include_is_spliced_where_the_directive_sits_not_appended()
    {
        Write("mid.inc", "echo from include\n");

        var inst = ParseFile("main.cmd", "echo before\ninclude mid.inc\necho after\n");

        var order = inst.Lines.Select(l => l.Trimmed).Where(t => t.Length > 0).ToList();
        Assert.Equal(new[] { "echo before", "echo from include", "echo after" }, order);
    }

    [Fact]
    public void Included_lines_carry_the_included_files_origin_and_its_own_numbering()
    {
        Write("origin.inc", "\necho tagged\n");

        var inst = ParseFile("main.cmd", "echo root\ninclude origin.inc\n");

        var included = inst.Lines.Single(l => l.Trimmed == "echo tagged");
        Assert.Equal("origin", included.Origin);
        Assert.Equal(2, included.LineNumber);

        var root = inst.Lines.Single(l => l.Trimmed == "echo root");
        Assert.Equal("main", root.Origin);
    }

    [Fact]
    public void Nested_includes_expand_depth_first()
    {
        Write("outer.inc", "echo outer head\ninclude inner.inc\necho outer tail\n");
        Write("inner.inc", "echo inner\n");

        var inst = ParseFile("main.cmd", "include outer.inc\n");

        var order = inst.Lines.Select(l => l.Trimmed).Where(t => t.Length > 0).ToList();
        Assert.Equal(new[] { "echo outer head", "echo inner", "echo outer tail" }, order);
    }

    // ── resolution ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("noext.inc", "include noext")]      // bare name, .inc tried
    [InlineData("noext.cmd", "include noext")]      // bare name, .cmd tried
    [InlineData("exact.inc", "include exact.inc")]  // spelled out
    public void Include_resolves_with_and_without_an_extension(string file, string directive)
    {
        Write(file, "RESOLVED:\n  return\n");

        var inst = ParseFile("main.cmd", directive + "\n");

        Assert.True(inst.Labels.ContainsKey("RESOLVED"),
            "labels = " + string.Join(",", inst.Labels.Keys));
    }

    [Fact]
    public void Include_resolves_from_a_subdirectory()
    {
        Write("lib/sub.inc", "SUBLBL:\n  return\n");

        var inst = ParseFile("main.cmd",
            "include lib" + Path.DirectorySeparatorChar + "sub.inc\n");

        Assert.True(inst.Labels.ContainsKey("SUBLBL"),
            "labels = " + string.Join(",", inst.Labels.Keys));
    }

    [Fact]
    public void A_missing_include_reports_itself_rather_than_failing_silently()
    {
        var inst = ParseFile("main.cmd", "include nope.inc\n");

        Assert.Contains(inst.Lines, l => l.Trimmed.Contains("include not found: nope.inc"));
    }

    [Fact]
    public void Include_directive_is_recognised_when_indented()
    {
        Write("indented.inc", "INDENTED:\n  return\n");

        var inst = ParseFile("main.cmd", "INIT:\n\tinclude indented.inc\n");

        Assert.True(inst.Labels.ContainsKey("INDENTED"),
            "labels = " + string.Join(",", inst.Labels.Keys));
    }

    [Fact]
    public void Include_survives_a_utf8_bom_and_crlf_line_endings()
    {
        File.WriteAllText(Path.Combine(_dir, "enc.inc"),
                          "ENCODED:\r\n  echo encoded\r\n  return\r\n",
                          new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var inst = ParseFile("main.cmd", "include enc.inc\r\n");

        Assert.True(inst.Labels.ContainsKey("ENCODED"),
            "labels = " + string.Join(",", inst.Labels.Keys));
        Assert.Equal(1, CountLines(inst, "echo encoded"));
    }

    // ── the no-source-path overload still terminates ────────────────────────

    [Fact]
    public void Parsing_without_a_source_path_still_bounds_a_self_include()
    {
        // Callers that hand Parse a bare string (tooling, tests) give it no root
        // path to seed, so the root is not in the include-once set and a
        // self-include expands the body a second time. That duplicate is accepted
        // rather than guessed away: resolving the root by NAME instead would pick
        // `loop.inc` over `loop.cmd` when both exist, which is exactly the pair
        // #347 exists to keep separate. What must hold either way is that it
        // TERMINATES — the path goes into the set before the recursive call — so
        // that is what this pins. The engine always passes the path, so the
        // duplicate is unreachable in the app.
        Write("loop.cmd", "echo looped\ninclude loop.cmd\n");

        var inst = ScriptParser.Parse("loop", _dir, File.ReadAllText(Path.Combine(_dir, "loop.cmd")));

        Assert.True(CountLines(inst, "echo looped") >= 1,
            "the body should survive the self-include");
    }
}
