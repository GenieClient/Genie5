using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Genie.App.Settings;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the layouts we ship in <c>src/Genie.App/Layouts/</c> (Strongbox,
/// Shadowveil, Heirloom, …). They are hand-authored JSON, not round-tripped
/// through "Save Layout", so nothing else catches a typo in them: a malformed
/// file makes <see cref="SavedLayout.FromJson"/> return null and the built-in
/// silently vanishes from the Layout menu, and a panel id that no longer exists
/// is simply skipped by <c>ApplyMdiBounds</c>, leaving a window missing.
///
/// <para>The known-id list is read out of <c>GenieDockFactory.CreateMdiLayout</c>'s
/// panel table rather than from a live factory — instantiating one needs the
/// whole view-model graph, and the source table is the same thing the applied
/// layout is matched against.</para>
/// </summary>
public class ShippedLayoutTests
{
    private static DirectoryInfo RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "Genie.App", "Layouts")))
                return d;
        throw new DirectoryNotFoundException(
            $"Could not locate src/Genie.App/Layouts walking up from {AppContext.BaseDirectory}");
    }

    public static TheoryData<string> ShippedLayoutFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(
                     Path.Combine(RepoRoot().FullName, "src", "Genie.App", "Layouts"), "*.json"))
            data.Add(Path.GetFileName(f));
        return data;
    }

    private static SavedLayout Load(string fileName)
    {
        var path = Path.Combine(RepoRoot().FullName, "src", "Genie.App", "Layouts", fileName);
        var layout = SavedLayout.FromJson(File.ReadAllText(path));
        Assert.True(layout is not null, $"{fileName} did not deserialise into a SavedLayout.");
        return layout!;
    }

    /// <summary>Panel ids from the MDI panel table in GenieDockFactory.</summary>
    private static HashSet<string> KnownPanelIds()
    {
        var src = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src", "Genie.App", "Docking", "GenieDockFactory.cs"));
        var table = Regex.Match(
            src, @"var panels = new \(string Id, IDockable Dockable\)\[\]\s*\{(?<body>.*?)\};",
            RegexOptions.Singleline);
        Assert.True(table.Success, "Could not find the MDI panel table in GenieDockFactory.cs.");

        var ids = Regex.Matches(table.Groups["body"].Value, @"\(""(?<id>[a-z0-9\-]+)"",")
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(ids);
        return ids;
    }

    [Theory]
    [MemberData(nameof(ShippedLayoutFiles))]
    public void Shipped_layout_parses_and_is_named(string fileName)
    {
        var layout = Load(fileName);
        Assert.Equal(Path.GetFileNameWithoutExtension(fileName), layout.Name);
        Assert.False(string.IsNullOrWhiteSpace(layout.Description));
    }

    [Theory]
    [MemberData(nameof(ShippedLayoutFiles))]
    public void Shipped_layout_only_names_panels_that_exist(string fileName)
    {
        var known  = KnownPanelIds();
        var layout = Load(fileName);

        foreach (var id in layout.VisibleTools)
        {
            // Plugin windows register their own ids at runtime ("pluginwin:<key>").
            if (id.StartsWith("pluginwin:", StringComparison.OrdinalIgnoreCase)) continue;
            Assert.True(known.Contains(id), $"{fileName}: VisibleTools names unknown panel '{id}'.");
        }

        foreach (var id in layout.MdiBounds?.Keys ?? Enumerable.Empty<string>())
            Assert.True(known.Contains(id), $"{fileName}: MdiBounds names unknown panel '{id}'.");
    }

    [Theory]
    [MemberData(nameof(ShippedLayoutFiles))]
    public void Windowed_layout_carries_usable_geometry_for_every_open_window(string fileName)
    {
        var layout = Load(fileName);
        if (!layout.WindowedMode) return;

        Assert.NotNull(layout.MdiBounds);
        Assert.NotEmpty(layout.MdiBounds!);

        foreach (var (id, b) in layout.MdiBounds!)
        {
            Assert.True(double.IsFinite(b.X) && double.IsFinite(b.Y), $"{fileName}/{id}: non-finite position.");
            Assert.True(b.X >= 0 && b.Y >= 0, $"{fileName}/{id}: negative position would open off-canvas.");
            Assert.True(b.Width > 0 && b.Height > 0, $"{fileName}/{id}: non-positive size.");
            Assert.True(Enum.TryParse<Dock.Model.Core.MdiWindowState>(b.State, out _),
                $"{fileName}/{id}: '{b.State}' is not an MdiWindowState.");
        }

        // BuildMdiLayout opens exactly the MdiBounds keys, so VisibleTools has
        // to agree or the Window menu's check marks disagree with the screen.
        Assert.Equal(
            layout.VisibleTools.OrderBy(x => x, StringComparer.OrdinalIgnoreCase),
            layout.MdiBounds!.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }
}
