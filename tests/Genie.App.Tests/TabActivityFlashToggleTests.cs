using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Genie.Core.Layout;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Unread-tab flash (ActivityTool) and its per-window "Flash on Activity"
/// toggle (<see cref="WindowSettings.FlashOnActivity"/>). Pure view-model +
/// Dock.Model — no Avalonia platform needed; mirrors the dock shape the app
/// builds (two stream tools stacked in one ToolDock).
/// </summary>
public class TabActivityFlashToggleTests
{
    private static (StreamBuffer talk, StreamTool talkTool,
                    StreamBuffer combat, StreamTool combatTool, ToolDock dock)
        BuildDock(WindowSettings? talkSettings = null)
    {
        var factory    = new Factory();
        var talk       = new StreamBuffer("Talk");
        var talkTool   = new StreamTool(talk, talkSettings);
        var combat     = new StreamBuffer("Combat");
        var combatTool = new StreamTool(combat);

        var dock = new ToolDock
        {
            Id = "dock",
            VisibleDockables = factory.CreateList<IDockable>(talkTool, combatTool),
            Factory = factory,
        };
        talkTool.Owner   = dock;
        combatTool.Owner = dock;
        dock.ActiveDockable = combatTool;   // Talk is backgrounded
        return (talk, talkTool, combat, combatTool, dock);
    }

    [Fact]
    public void Line_into_backgrounded_tab_sets_flag_and_activation_clears_it()
    {
        var (talk, talkTool, _, _, dock) = BuildDock();

        talk.Add("someone says hello");
        Assert.True(talkTool.IsModified);

        dock.ActiveDockable = talkTool;     // the tab-click path
        Assert.False(talkTool.IsModified);
    }

    [Fact]
    public void Line_into_active_tab_does_not_flag()
    {
        var (_, _, combat, combatTool, _) = BuildDock();

        combat.Add("a hit lands");
        Assert.False(combatTool.IsModified);
    }

    [Fact]
    public void Flash_toggle_off_suppresses_the_flag()
    {
        var settings = new WindowSettings { Id = "talk", FlashOnActivity = false };
        var (talk, talkTool, _, _, _) = BuildDock(settings);

        talk.Add("quiet line");
        Assert.False(talkTool.IsModified);
    }

    [Fact]
    public void Turning_the_toggle_off_clears_an_active_flash()
    {
        var settings = new WindowSettings { Id = "talk" };   // FlashOnActivity default true
        var (talk, talkTool, _, _, _) = BuildDock(settings);

        talk.Add("someone says hello");
        Assert.True(talkTool.IsModified);

        settings.FlashOnActivity = false;
        settings.NotifyChanged();           // the menu / Layout-tab apply path
        Assert.False(talkTool.IsModified);
    }

    [Fact]
    public void Toggle_applies_live_without_resubscribe()
    {
        var settings = new WindowSettings { Id = "talk", FlashOnActivity = false };
        var (talk, talkTool, _, _, _) = BuildDock(settings);

        talk.Add("suppressed");
        Assert.False(talkTool.IsModified);

        settings.FlashOnActivity = true;    // flip back on, same instance
        talk.Add("now it flashes");
        Assert.True(talkTool.IsModified);
    }
}
