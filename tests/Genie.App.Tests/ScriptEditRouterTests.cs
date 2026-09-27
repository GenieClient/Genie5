using System;
using System.Collections.Generic;
using System.IO;
using Genie.App.ScriptEditing;
using Genie.Core.Scripting;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #243 — where <c>#edit</c> / Script Manager Edit / the Script Bar pencil
/// open a script: the built-in editor by default, the external-editor ladder with
/// <c>#config externaleditor on</c>. And which files <c>#edit</c> may open at all:
/// only what a script start of that name would run, inside the scripts folders.
/// </summary>
public class ScriptEditRouterTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_editroute_" + Guid.NewGuid().ToString("N"));

    public ScriptEditRouterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private (ScriptEditRouter Router, List<string> BuiltIn, List<string> External) Make(Func<bool> useExternal)
    {
        var builtIn  = new List<string>();
        var external = new List<string>();
        return (new ScriptEditRouter(useExternal, builtIn.Add, external.Add), builtIn, external);
    }

    [Fact]
    public void By_default_edits_open_in_the_built_in_editor()
    {
        var (router, builtIn, external) = Make(() => false);

        Assert.Equal(ScriptEditRoute.BuiltIn, router.Open("hunt.cmd"));

        Assert.Equal(new[] { "hunt.cmd" }, builtIn);
        Assert.Empty(external);
    }

    [Fact]
    public void The_opt_out_sends_edits_to_the_external_editor()
    {
        var (router, builtIn, external) = Make(() => true);

        Assert.Equal(ScriptEditRoute.External, router.Open("hunt.cmd"));

        Assert.Empty(builtIn);
        Assert.Equal(new[] { "hunt.cmd" }, external);
    }

    [Fact]
    public void The_setting_is_read_per_request()
    {
        var useExternal = false;
        var (router, builtIn, external) = Make(() => useExternal);

        router.Open("a.cmd");
        useExternal = true;          // #config externaleditor on, mid-session
        router.Open("b.cmd");

        Assert.Equal(new[] { "a.cmd" }, builtIn);
        Assert.Equal(new[] { "b.cmd" }, external);
    }

    [Theory]
    [InlineData("hunt", true)]
    [InlineData("hunt.js", true)]
    [InlineData("../settings", false)]
    [InlineData("sub/hunt", false)]
    [InlineData("sub\\hunt", false)]
    [InlineData("", false)]
    public void Names_must_be_bare(string name, bool ok)
        => Assert.Equal(ok, ScriptEditRouter.IsValidName(name));

    [Fact]
    public void Rooted_names_are_refused()
        => Assert.False(ScriptEditRouter.IsValidName(Path.Combine(Path.GetTempPath(), "x.cmd")));

    private ScriptEngine Engine() => new(_dir, new TypeAheadSession(), _ => { }, _ => { });

    [Fact]
    public void ResolveExisting_uses_the_script_start_lookup()
    {
        var engine = Engine();
        File.WriteAllText(Path.Combine(_dir, "hunt.js"), "");
        File.WriteAllText(Path.Combine(_dir, "hunt.cmd"), "");

        Assert.Equal(Path.Combine(_dir, "hunt.cmd"), ScriptEditRouter.ResolveExisting(engine, "hunt"));
        Assert.Equal(Path.Combine(_dir, "hunt.js"),  ScriptEditRouter.ResolveExisting(engine, " hunt.js "));
        Assert.Null(ScriptEditRouter.ResolveExisting(engine, "missing"));
    }

    [Fact]
    public void ResolveExisting_never_leaves_the_scripts_folder()
    {
        var engine = Engine();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + "-outside.cmd"), "");
        try
        {
            Assert.Null(ScriptEditRouter.ResolveExisting(engine, "../" + Path.GetFileName(_dir) + "-outside"));
            Assert.Null(ScriptEditRouter.ResolveExisting(engine, Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + "-outside.cmd")));
        }
        finally
        {
            File.Delete(Path.Combine(Path.GetDirectoryName(_dir)!, Path.GetFileName(_dir) + "-outside.cmd"));
        }
    }
}
