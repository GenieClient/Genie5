using System;
using System.IO;
using Genie.Core;
using Genie.Core.Import;
using Genie.Core.Presets;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #406: Genie 4's stock ui.* values are its Windows system greys. Nobody
/// chose them, so Genie 5 reads them as Default (follow the theme): when a
/// Genie 4 presets.cfg is imported, and once on upgrade for presets.json.
/// </summary>
public class UiPresetsGenie4StockTests
{
    [Theory]
    [InlineData("ui.menu",           "Black",     "#EEEEEE")]
    [InlineData("ui.menu",           "#000000",   "#eeeeee")]
    [InlineData("ui.textbox",        "Black",     "White")]
    [InlineData("ui.textbox",        "black",     "#FFFFFF")]
    [InlineData("ui.button",         "Black",     "Silver")]
    [InlineData("ui.menu.highlight", "LightBlue", "")]
    [InlineData("ui.menu.checked",   "#ADD8E6",   "")]
    public void Genie_4s_stock_chrome_values_are_recognised(string id, string fg, string bg) =>
        Assert.True(UiPresets.IsGenie4Stock(id, fg, bg));

    [Theory]
    [InlineData("ui.menu",    "Black",   "#202020")]   // a colour someone chose
    [InlineData("ui.textbox", "Default", "")]
    [InlineData("health",     "Maroon",  "")]          // not a ui.* preset
    public void Other_values_are_not(string id, string fg, string bg) =>
        Assert.False(UiPresets.IsGenie4Stock(id, fg, bg));

    [Fact]
    public void Importing_Genie_4_presets_keeps_the_theme_chrome_but_takes_chosen_colours()
    {
        var path = Path.Combine(Path.GetTempPath(), "genie_g4presets_" + Guid.NewGuid().ToString("N") + ".cfg");
        File.WriteAllLines(path, new[]
        {
            "#preset {ui.menu} {Black, #EEEEEE}",     // Genie 4 stock
            "#preset {ui.textbox} {Black, White}",    // Genie 4 stock
            "#preset {ui.status} {Yellow, Navy}",     // chosen in Genie 4
        });
        try
        {
            var engine = new PresetEngine();
            Genie4Importer.ImportPresets(path, engine, ImportMode.Merge);

            Assert.Equal("Default", engine.GetForeground("ui.menu"));
            Assert.Equal("",        engine.GetBackground("ui.menu"));
            Assert.Equal("Default", engine.GetForeground("ui.textbox"));
            Assert.Equal("Yellow",  engine.GetForeground("ui.status"));
            Assert.Equal("Navy",    engine.GetBackground("ui.status"));
        }
        finally { File.Delete(path); }
    }
}
