using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Controls.Primitives;
using Genie.App.Docking;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the tab-overflow fix: with more tabs than the strip is wide, the
/// extras were clipped with no visible way to reach them.
///
/// <para>
/// The fix is two app-level pieces in <c>App.axaml</c>, both of which a future
/// theme cleanup could silently drop without any compile error:
/// </para>
/// <list type="number">
/// <item>A style flipping the ToolTabStrip ScrollViewer to
/// <c>HorizontalScrollBarVisibility="Auto"</c> — Dock's overflow arrow buttons
/// are gated on it (their visibility converter returns false for the stock
/// theme's "Hidden").</item>
/// <item><see cref="TabStripAutoScroll"/> attached to both strip types so the
/// active tab is scrolled into view on selection change.</item>
/// </list>
/// </summary>
public class TabStripOverflowTests
{
    private static string AppXaml()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Genie.App", "App.axaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate src/Genie.App/App.axaml walking up from {dir}");
    }

    [Fact]
    public void ToolTabStrip_scrollviewer_visibility_is_overridden_to_Auto()
    {
        // The selector must target the strip's templated PART_ScrollViewer —
        // the property lives on the inner ScrollViewer, not the strip itself.
        Assert.Matches(
            new Regex(
                @"<Style\s+Selector=""ToolTabStrip\s*/template/\s*ScrollViewer#PART_ScrollViewer"">\s*" +
                @"<Setter\s+Property=""HorizontalScrollBarVisibility""\s+Value=""Auto""",
                RegexOptions.Singleline),
            AppXaml());
    }

    [Theory]
    [InlineData("ToolTabStrip")]
    [InlineData("DocumentTabStrip")]
    public void Both_strip_types_get_the_auto_scroll_behavior(string strip)
    {
        Assert.Matches(
            new Regex(
                @"<Style\s+Selector=""" + strip + @""">\s*" +
                @"<Setter\s+Property=""\(docking:TabStripAutoScroll\.Enabled\)""\s+Value=""True""",
                RegexOptions.Singleline),
            AppXaml());
    }

    [Fact]
    public void Behavior_property_is_registered_for_selecting_items_controls()
    {
        // The styles above set the property via reflection at runtime; a rename
        // or host-type change would ship as a silently dead style.
        Assert.Equal("Enabled", TabStripAutoScroll.EnabledProperty.Name);
        Assert.True(
            TabStripAutoScroll.EnabledProperty.OwnerType == typeof(TabStripAutoScroll));
        Assert.False(
            TabStripAutoScroll.GetEnabled(new TabStrip()),
            "Enabled must default to false so non-styled strips are untouched.");
    }
}
