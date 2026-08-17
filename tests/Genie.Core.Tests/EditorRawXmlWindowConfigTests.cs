using System;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#config useeditorrawxmlwindow</c> — the flag that swaps the Raw XML
/// window onto the experimental AvaloniaEdit renderer. Same contract as
/// <c>useeditorgamewindow</c> (see EditorGameWindowConfigTests): parses,
/// persists, and reports like every other boolean, and is off unless the
/// user turned it on. The renderer swap itself lives in Genie.App and is
/// not reachable from these tests.
/// </summary>
public class EditorRawXmlWindowConfigTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    [Fact]
    public void DefaultsOff()
    {
        Assert.False(NewConfig().UseEditorRawXmlWindow);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("on", true)]
    [InlineData("False", false)]
    [InlineData("off", false)]
    [InlineData("", false)]        // unset / unparseable stays on the shipped renderer
    [InlineData("banana", false)]
    public void RoundTrips(string input, bool expected)
    {
        var cfg = NewConfig();
        cfg.SetSetting("useeditorrawxmlwindow", input, showException: false);
        Assert.Equal(expected, cfg.UseEditorRawXmlWindow);
        Assert.Equal(expected.ToString(), cfg.GetSetting("useeditorrawxmlwindow"));
    }

    [Fact]
    public void IsListedForConfigDisplay()
    {
        Assert.Contains(NewConfig().ToConfigPairs(), p => p.Key == "useeditorrawxmlwindow");
        Assert.Contains(GenieConfig.ConfigCategories.SelectMany(c => c.Keys), k => k == "useeditorrawxmlwindow");
    }
}
