using System;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#config useeditorgamewindow</c> — the flag that swaps the main Game window
/// onto the experimental AvaloniaEdit renderer. The setting is read once, when the
/// dock layout is built, so the only contract Core owes is that it parses, persists
/// and reports like every other boolean — and, above all, that it is <b>off</b>
/// unless the user turned it on. The renderer swap itself lives in Genie.App and
/// is not reachable from these tests.
/// </summary>
public class EditorGameWindowConfigTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    [Fact]
    public void DefaultsOff()
    {
        // The acceptance bar for the whole feature: an untouched install renders
        // through the legacy path.
        Assert.False(NewConfig().UseEditorGameWindow);
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
        cfg.SetSetting("useeditorgamewindow", input, showException: false);
        Assert.Equal(expected, cfg.UseEditorGameWindow);
        Assert.Equal(expected.ToString(), cfg.GetSetting("useeditorgamewindow"));
    }

    [Fact]
    public void IsListedForConfigDisplay()
    {
        // ToConfigPairs drives settings.cfg persistence; ConfigCategories drives the
        // `#config` listing. A key in one and not the other is how settings go
        // missing from the UI, so assert both.
        Assert.Contains(NewConfig().ToConfigPairs(), p => p.Key == "useeditorgamewindow");
        Assert.Contains(GenieConfig.ConfigCategories.SelectMany(c => c.Keys), k => k == "useeditorgamewindow");
    }
}
