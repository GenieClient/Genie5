using System;
using System.IO;
using System.Runtime.InteropServices;
using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Remote-supplied filenames must resolve inside the folder that owns them
/// (2026-08-31 security review).
///
/// <para><c>Path.Combine</c> constrains nothing: <c>"../../evil"</c> climbs out of
/// the base, and an ABSOLUTE second argument discards the base entirely and returns
/// the absolute path unchanged. Every updater writes files whose names come from a
/// remote feed, so a hostile or compromised source could choose where its bytes
/// land. For the plugin feed that is remote code execution, since the routine that
/// writes the file then loads it.</para>
/// </summary>
public class FeedPathTests
{
    private static string Base => OperatingSystem.IsWindows() ? @"C:\Genie5\Maps" : "/genie5/maps";

    // ── the attack shapes ────────────────────────────────────────────────────

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("../../evil.xml")]
    [InlineData("../../../../Windows/System32/evil.dll")]
    [InlineData("..\\..\\evil.xml")]
    [InlineData("subdir/../../evil.xml")]
    [InlineData("..")]
    [InlineData(".")]
    public void Traversal_is_refused(string name)
    {
        Assert.False(FeedPath.TryResolveUnder(Base, name, allowSubdirectories: false, out var path));
        Assert.Equal("", path);
    }

    [Theory]
    [InlineData("../evil.cmd")]
    [InlineData("../../evil.cmd")]
    [InlineData("ok/../../../evil.cmd")]
    public void Traversal_is_refused_even_where_subfolders_are_allowed(string name)
    {
        // The scripts feed legitimately serves nested paths, so it cannot simply
        // reject separators — the containment check is what stops it there.
        Assert.False(FeedPath.TryResolveUnder(Base, name, allowSubdirectories: true, out _));
    }

    [Fact]
    public void An_absolute_path_is_refused()
    {
        // The one that surprises people: Path.Combine(base, absolute) returns the
        // absolute path, silently dropping the base.
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\System32\evil.dll" : "/etc/evil";
        Assert.False(FeedPath.TryResolveUnder(Base, absolute, allowSubdirectories: false, out _));
        Assert.False(FeedPath.TryResolveUnder(Base, absolute, allowSubdirectories: true, out _));
    }

    [Fact]
    public void A_drive_qualified_name_is_refused()
    {
        Assert.False(FeedPath.TryResolveUnder(Base, "C:evil.dll", allowSubdirectories: false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string? name)
    {
        Assert.False(FeedPath.TryResolveUnder(Base, name, allowSubdirectories: false, out _));
    }

    [Fact]
    public void A_sibling_folder_sharing_a_prefix_is_refused()
    {
        // The classic off-by-one in containment checks: without the separator on the
        // comparison, "Maps2" passes a StartsWith test against "Maps".
        var baseDir = OperatingSystem.IsWindows() ? @"C:\Genie5\Maps" : "/genie5/maps";
        Assert.False(FeedPath.TryResolveUnder(baseDir, "../Maps2/evil.xml", allowSubdirectories: true, out _));
    }

    // ── what must keep working ───────────────────────────────────────────────

    [Theory]
    [InlineData("Crossing.xml")]
    [InlineData("map_1.xml")]
    [InlineData("Plugin.EXPTracker.dll")]
    [InlineData("name with spaces.xml")]
    [InlineData("Vela'Tohr Valley.xml")]
    public void An_ordinary_filename_resolves(string name)
    {
        Assert.True(FeedPath.TryResolveUnder(Base, name, allowSubdirectories: false, out var path));
        Assert.Equal(Path.Combine(Path.GetFullPath(Base), name), path);
    }

    [Fact]
    public void A_flat_feed_refuses_subfolders()
    {
        // Maps and plugins are flat by design — the maps updater enumerates its
        // folder top-level-only, the plugin loader scans a single directory. A name
        // carrying structure means the feed is not what this updater expects.
        Assert.False(FeedPath.TryResolveUnder(Base, "zones/Crossing.xml", allowSubdirectories: false, out _));
    }

    [Fact]
    public void A_nested_feed_accepts_subfolders()
    {
        // Scripts do mirror their repo's folders.
        Assert.True(FeedPath.TryResolveUnder(Base, "hunting/uber/hunt.cmd", allowSubdirectories: true, out var path));
        Assert.StartsWith(Path.GetFullPath(Base), path);
        Assert.EndsWith("hunt.cmd", path);
    }

    [Fact]
    public void Forward_slashes_are_accepted_from_remote_listings()
    {
        // GitHub listings use '/' regardless of the client's platform.
        Assert.True(FeedPath.TryResolveUnder(Base, "sub/dir/file.cmd", allowSubdirectories: true, out var path));
        Assert.Contains("file.cmd", path);
        if (OperatingSystem.IsWindows())
            Assert.DoesNotContain('/', path);
    }

    [Fact]
    public void The_resolved_path_is_always_inside_the_base()
    {
        // The property every caller depends on, stated once.
        foreach (var name in new[] { "a.xml", "b/c.xml", "d/e/f.xml" })
            if (FeedPath.TryResolveUnder(Base, name, allowSubdirectories: true, out var path))
                Assert.StartsWith(Path.GetFullPath(Base) + Path.DirectorySeparatorChar, path);
    }
}
