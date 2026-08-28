using Genie.App.Docking;
using Genie.App.ViewModels;
using Genie.Core.Layout;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #233: four dock panels (Experience, Active Spells, Time Tracker,
/// plugin windows) declared <c>ToolFontSize</c>/<c>ToolFontFamily</c> as
/// get-only constants — Configuration → Layout saved the per-window font
/// correctly, but the panels never read it back, so Apply did nothing on
/// screen. They now follow the same resolve-and-listen pattern the Stream /
/// Inventory / Game panels always used.
/// </summary>
public class PanelFontSettingsTests
{
    private static WindowSettings Settings(string id, double fontSize = 13,
                                           string family = "Cascadia Mono,Consolas,Courier New,monospace")
        => new()
        {
            Id = id, DefaultTitle = id, DisplayTitle = id,
            FontFamily = family, FontSize = fontSize,
            Foreground = "Default", Background = "",
        };

    [Fact]
    public void Experience_tool_applies_saved_font_size_and_live_changes()
    {
        var s    = Settings("experience", fontSize: 18);
        var tool = new ExperienceTool(new ExperienceViewModel(), s);
        Assert.Equal(18, tool.ToolFontSize);

        // The Layout tab's Apply mutates the settings object and raises
        // Changed — the panel must follow without a reconnect.
        s.FontSize = 22;
        s.NotifyChanged();
        Assert.Equal(22, tool.ToolFontSize);
    }

    [Fact]
    public void ActiveSpells_tool_applies_saved_font_size_and_live_changes()
    {
        var s    = Settings("active-spells", fontSize: 18);
        var tool = new ActiveSpellsTool(new ActiveSpellsViewModel(), s);
        Assert.Equal(18, tool.ToolFontSize);

        s.FontSize = 9;
        s.NotifyChanged();
        Assert.Equal(9, tool.ToolFontSize);
    }

    [Fact]
    public void TimeTracker_tool_applies_saved_font_size_and_live_changes()
    {
        var s    = Settings("time-tracker", fontSize: 18);
        var tool = new TimeTrackerTool(new TimeTrackerViewModel(), s);
        Assert.Equal(18, tool.ToolFontSize);

        s.FontSize = 15;
        s.NotifyChanged();
        Assert.Equal(15, tool.ToolFontSize);
    }

    [Fact]
    public void PluginWindow_tool_applies_saved_font_size_and_live_changes()
    {
        var s    = Settings("plugin:test", fontSize: 18);
        var tool = new PluginWindowTool(new PluginWindowViewModel("Test"), "plugin:test", "Test", s);
        Assert.Equal(18, tool.ToolFontSize);

        s.FontSize = 11;
        s.NotifyChanged();
        Assert.Equal(11, tool.ToolFontSize);
    }

    [Fact]
    public void Explicit_font_family_overrides_the_monospace_default()
    {
        var s    = Settings("experience", family: "Verdana");
        var tool = new ExperienceTool(new ExperienceViewModel(), s);
        Assert.Contains("Verdana", tool.ToolFontFamily.ToString());
    }

    [Fact]
    public void Plugin_window_title_stays_plugin_owned()
    {
        // DisplayTitle in settings must NOT override the plugin's own title —
        // the plugin renames its window through the VM, not the Layout tab.
        var s    = Settings("plugin:test");
        s.DisplayTitle = "Renamed In Layout";
        var tool = new PluginWindowTool(new PluginWindowViewModel("Test"), "plugin:test", "Test", s);
        Assert.Equal("Test", tool.Title);
    }
}
