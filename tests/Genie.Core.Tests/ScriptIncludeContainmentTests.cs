using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// 2026-08-31 security review — an <c>include</c> name comes straight from script
/// text, so a downloaded script could walk out of the scripts folder with
/// <c>..</c> segments or an absolute path; the file was read and its unrecognised
/// lines sent verbatim to the game. Includes must now resolve inside the script's
/// folder or one of the engine's script dirs, and a refusal reports itself.
/// </summary>
public class ScriptIncludeContainmentTests : IDisposable
{
    private readonly string _root;
    private readonly string _scripts;
    private readonly string _outside;
    private static readonly char Sep = Path.DirectorySeparatorChar;

    public ScriptIncludeContainmentTests()
    {
        _root    = Path.Combine(Path.GetTempPath(), "genie_inccontain_" + Guid.NewGuid().ToString("N"));
        _scripts = Path.Combine(_root, "scripts");
        _outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(_scripts);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.inc"), "SECRET_LINE\n");
        File.WriteAllText(Path.Combine(_outside, "lib.js"), "function f(){ return 1; }\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ScriptInstance Parse(string body, string? baseDir = null, IReadOnlyList<string>? roots = null)
    {
        var dir  = baseDir ?? _scripts;
        var path = Path.Combine(dir, "main.cmd");
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, body);
        return ScriptParser.Parse("main", dir, body, path, roots);
    }

    private static bool Has(ScriptInstance inst, string text) =>
        inst.Lines.Any(l => l.Trimmed.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Dot_dot_traversal_out_of_the_scripts_folder_is_refused()
    {
        var inst = Parse($"include ..{Sep}outside{Sep}secret.inc\n");

        Assert.False(Has(inst, "SECRET_LINE"));
        Assert.True(Has(inst, "include refused"));
    }

    [Fact]
    public void Absolute_path_outside_the_scripts_folder_is_refused()
    {
        var inst = Parse($"include {Path.Combine(_outside, "secret.inc")}\n");

        Assert.False(Has(inst, "SECRET_LINE"));
        Assert.True(Has(inst, "include refused"));
    }

    [Fact]
    public void Js_include_outside_the_scripts_folder_is_refused()
    {
        var inst = Parse($"include ..{Sep}outside{Sep}lib.js\n");

        Assert.False(Has(inst, "__jsinclude"));
        Assert.True(Has(inst, "include refused"));
    }

    [Fact]
    public void Absolute_path_inside_the_scripts_folder_still_works()
    {
        File.WriteAllText(Path.Combine(_scripts, "shared.inc"), "SHARED_LINE\n");

        var inst = Parse($"include {Path.Combine(_scripts, "shared.inc")}\n");

        Assert.True(Has(inst, "SHARED_LINE"));
    }

    [Fact]
    public void A_subfolder_script_may_include_from_an_engine_script_root()
    {
        // A script in scripts\hunting\ including ..\shared.inc: outside its own
        // folder but inside the scripts root the engine passes as an include root.
        File.WriteAllText(Path.Combine(_scripts, "shared.inc"), "SHARED_LINE\n");
        var sub = Path.Combine(_scripts, "hunting");

        var inst = Parse($"include ..{Sep}shared.inc\n", baseDir: sub, roots: new[] { _scripts });

        Assert.True(Has(inst, "SHARED_LINE"));
        Assert.False(Has(inst, "include refused"));
    }

    [Fact]
    public void A_sibling_folder_that_merely_shares_the_prefix_is_not_inside()
    {
        // "scripts-evil" starts with "scripts" — containment compares whole
        // directory segments, not a raw string prefix.
        var evil = _scripts + "-evil";
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "x.inc"), "EVIL_LINE\n");

        var inst = Parse($"include ..{Sep}scripts-evil{Sep}x.inc\n");

        Assert.False(Has(inst, "EVIL_LINE"));
        Assert.True(Has(inst, "include refused"));
    }

    [Fact]
    public void Engine_refuses_the_traversal_end_to_end()
    {
        File.WriteAllText(Path.Combine(_scripts, "t.cmd"),
            $"include ..{Sep}outside{Sep}secret.inc\necho done\n");
        var sent   = new List<string>();
        var echoed = new List<string>();
        var engine = new ScriptEngine(_scripts, new TypeAheadSession(),
                                      sendCommand: sent.Add, echo: echoed.Add);
        engine.TryStart("t", new List<string>());
        for (int i = 0; i < 50; i++) engine.Tick();

        Assert.DoesNotContain("SECRET_LINE", sent);
        Assert.Contains(echoed, l => l.Contains("include refused"));
    }
}
