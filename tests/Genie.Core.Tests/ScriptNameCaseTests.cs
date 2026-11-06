using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #392 — script names are case-insensitive on every platform.
///
/// <para>Genie 4 got this for free from NTFS. Genie 5's lookups built the
/// exact path from the typed name and tested it with <c>File.Exists</c>, so
/// on ext4 <c>.script</c> could not start <c>Script.cmd</c>, and an
/// <c>include</c> with the wrong case failed the same way. The probe in
/// <see cref="ScriptFileProbe"/> falls back to an ordinal-ignore-case match
/// against the directory listing when the exact path misses.</para>
///
/// <para>On macOS the exact-case probe already hits, so these tests pass
/// trivially there. On Windows the fixture flips its temp folder to
/// case-sensitive (NTFS supports it per directory via
/// <c>fsutil file setCaseSensitiveInfo</c>, no elevation needed) so the
/// fallback branch is exercised for real; where that is unavailable the tests
/// degrade to the trivial pass. The Linux CI leg always proves the fix.</para>
/// </summary>
public class ScriptNameCaseTests : IDisposable
{
    private readonly string _dir;
    private readonly bool _caseSensitive;
    private readonly List<string> _echoed = new();

    public ScriptNameCaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gc_namecase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        TryMakeCaseSensitive(_dir);

        // Detect what the filesystem actually does, rather than assuming by OS.
        var probe = Path.Combine(_dir, "Probe.tmp");
        File.WriteAllText(probe, "");
        _caseSensitive = !File.Exists(Path.Combine(_dir, "probe.tmp"));
        File.Delete(probe);
    }

    /// <summary>Best effort: a fresh, empty NTFS directory can be made
    /// case-sensitive by its owner. Sub-directories created inside it inherit
    /// the flag. Any failure just leaves the folder case-insensitive.</summary>
    private static void TryMakeCaseSensitive(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "fsutil",
                Arguments              = $"file setCaseSensitiveInfo \"{dir}\" enable",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            p?.WaitForExit(5000);
        }
        catch { /* fsutil missing or refused — tests still run, just trivially */ }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relativeName, string body)
    {
        var path = Path.Combine(_dir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
        return path;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── the probe itself ────────────────────────────────────────────────────

    [Fact]
    public void Exact_case_is_found_without_any_listing()
    {
        var file = Write("Script.cmd", "echo hi\n");
        var found = ScriptFileProbe.Find(_dir, "Script.cmd");
        Assert.NotNull(found);
        Assert.True(SamePath(file, found!));
    }

    [Fact]
    public void Lowercased_name_finds_a_mixed_case_file()
    {
        var file = Write("Script.cmd", "echo hi\n");
        // On a case-sensitive folder the pre-#392 exact probe misses — this is
        // the branch the fix adds. (Trivially true elsewhere.)
        if (_caseSensitive) Assert.False(File.Exists(Path.Combine(_dir, "script.cmd")));
        var found = ScriptFileProbe.Find(_dir, "script.cmd");
        Assert.NotNull(found);
        Assert.True(SamePath(file, found!));
    }

    [Fact]
    public void Extension_case_is_ignored_too()
    {
        var file = Write("hunt.CMD", "echo hi\n");
        var found = ScriptFileProbe.Find(_dir, "hunt.cmd");
        Assert.NotNull(found);
        Assert.True(SamePath(file, found!));
    }

    [Fact]
    public void Every_segment_of_a_sub_path_is_matched_case_insensitively()
    {
        var file = Write(Path.Combine("GenieHunter", "Hunt.cmd"), "echo hi\n");
        var found = ScriptFileProbe.Find(_dir, "geniehunter/HUNT.cmd");
        Assert.NotNull(found);
        Assert.True(SamePath(file, found!));
    }

    [Fact]
    public void A_missing_file_is_still_null()
    {
        Write("Script.cmd", "echo hi\n");
        Assert.Null(ScriptFileProbe.Find(_dir, "other.cmd"));
        Assert.Null(ScriptFileProbe.Find(_dir, Path.Combine("nodir", "script.cmd")));
    }

    [Fact]
    public void Blank_and_unusable_names_are_null_not_exceptions()
    {
        Assert.Null(ScriptFileProbe.Find(_dir, ""));
        Assert.Null(ScriptFileProbe.Find(_dir, "   "));
        Assert.Null(ScriptFileProbe.Find(Path.Combine(_dir, "missing"), "x.cmd"));
    }

    // ── .script / #script ───────────────────────────────────────────────────

    [Fact]
    public void Script_starts_when_typed_in_the_wrong_case()
    {
        Write("Script.cmd", "echo started\n");
        var engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: l => { lock (_echoed) _echoed.Add(l); });
        try
        {
            Assert.True(engine.TryStart("script", Array.Empty<string>()));
            Assert.DoesNotContain(_echoed, l => l.Contains("not found", StringComparison.OrdinalIgnoreCase));
        }
        finally { engine.StopAll(); }
    }

    [Fact]
    public void Scriptcheck_resolves_the_wrong_case_as_well()
    {
        Write("Script.cmd", "echo ok\n");
        var engine = new ScriptEngine(_dir, new TypeAheadSession(),
                                      sendCommand: _ => { }, echo: l => { lock (_echoed) _echoed.Add(l); });
        try
        {
            var lines = engine.CheckScript("SCRIPT");
            Assert.DoesNotContain(lines, l => l.Contains("not found", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(lines, l => l.Contains("Script.cmd", StringComparison.OrdinalIgnoreCase));
        }
        finally { engine.StopAll(); }
    }

    // ── include ─────────────────────────────────────────────────────────────

    [Fact]
    public void Include_with_the_wrong_case_is_spliced_in()
    {
        Write("Lib.inc", "echo from_lib\n");
        var path = Write("main.cmd", "include lib.inc\necho from_main\n");
        var inst = ScriptParser.Parse("main", _dir, File.ReadAllText(path), path);

        Assert.Contains(inst.Lines, l => l.Trimmed == "echo from_lib");
        Assert.DoesNotContain(inst.Lines, l => l.Trimmed.Contains("include not found"));
    }

    [Fact]
    public void Include_without_extension_still_tries_inc_then_cmd_case_insensitively()
    {
        Write("Helpers.INC", "echo from_helpers\n");
        var path = Write("main.cmd", "include helpers\n");
        var inst = ScriptParser.Parse("main", _dir, File.ReadAllText(path), path);

        Assert.Contains(inst.Lines, l => l.Trimmed == "echo from_helpers");
    }

    [Fact]
    public void Js_include_with_the_wrong_case_emits_the_load_directive()
    {
        var js   = Write("Lib.js", "function f() { return 1; }\n");
        var path = Write("main.cmd", "include lib.js\n");
        var inst = ScriptParser.Parse("main", _dir, File.ReadAllText(path), path);

        var directive = Assert.Single(inst.Lines, l => l.Trimmed.StartsWith("__jsinclude ", StringComparison.Ordinal));
        Assert.True(SamePath(js, directive.Trimmed["__jsinclude ".Length..]));
        Assert.DoesNotContain(inst.Lines, l => l.Trimmed.Contains("include not found"));
    }

    [Fact]
    public void A_js_include_that_really_is_missing_names_what_the_script_asked_for()
    {
        var path = Write("main.cmd", "include nothere.js\n");
        var inst = ScriptParser.Parse("main", _dir, File.ReadAllText(path), path);

        Assert.Contains(inst.Lines, l => l.Trimmed == "echo [script] include not found: nothere.js");
    }
}
