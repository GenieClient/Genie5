using System;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#config useeditorstreamwindow</c> — the flag that swaps every Stream
/// window (Logons, Talk, Whispers, ...) onto the experimental AvaloniaEdit
/// renderer at once. Same contract as <c>useeditorgamewindow</c> (see
/// EditorGameWindowConfigTests): parses, persists, and reports like every
/// other boolean, and is off unless the user turned it on. The renderer swap
/// itself lives in Genie.App and is not reachable from these tests.
/// </summary>
public class EditorStreamWindowConfigTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    [Fact]
    public void DefaultsOff()
    {
        Assert.False(NewConfig().UseEditorStreamWindow);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("on", true)]
    [InlineData("False", false)]
    [InlineData("off", false)]
    [InlineData("", false)]
    [InlineData("banana", false)]
    public void RoundTrips(string input, bool expected)
    {
        var cfg = NewConfig();
        cfg.SetSetting("useeditorstreamwindow", input, showException: false);
        Assert.Equal(expected, cfg.UseEditorStreamWindow);
        Assert.Equal(expected.ToString(), cfg.GetSetting("useeditorstreamwindow"));
    }

    [Fact]
    public void IsListedForConfigDisplay()
    {
        Assert.Contains(NewConfig().ToConfigPairs(), p => p.Key == "useeditorstreamwindow");
        Assert.Contains(GenieConfig.ConfigCategories.SelectMany(c => c.Keys), k => k == "useeditorstreamwindow");
    }
}
