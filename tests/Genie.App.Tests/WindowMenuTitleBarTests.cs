using System.Windows.Input;
using Genie.App.Docking;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The one "Hide / Show Title Bar" menu item has two targets (public #299):
/// a floating window's own title bar (#181, session-only) and, docked, the
/// frame header of a window alone in its frame (persisted per window). These
/// pin the routing and the visibility gate on the menu model alone.
/// </summary>
public class WindowMenuTitleBarTests
{
    private sealed class Probe
    {
        public bool Floating;
        public bool Alone = true;
        public bool FloatHidden;
        public int  FloatToggles;
        public bool? LastDocked;

        public WindowMenuModel Build(bool dockedInit = false) => new(
            onToggleFloat:           () => { },
            floatStateProbe:         () => Floating,
            onToggleTitleBar:        () => { FloatToggles++; FloatHidden = !FloatHidden; },
            titleBarHiddenProbe:     () => FloatHidden,
            dockedTitleBarHidden:    dockedInit,
            onDockedTitleBarToggled: on => LastDocked = on,
            aloneInFrameProbe:       () => Alone);
    }

    [Fact]
    public void Docked_alone_offers_the_item_and_flips_the_persisted_setting()
    {
        var p = new Probe();
        var menu = p.Build();
        menu.RefreshFloatState();

        Assert.True(menu.ShowHideTitleBar);
        Assert.Equal("Hide Title Bar", menu.TitleBarHeader);

        ((ICommand)menu.ToggleTitleBarCommand!).Execute(null);

        Assert.True(menu.IsDockedTitleBarHidden);
        Assert.True(p.LastDocked);
        Assert.Equal(0, p.FloatToggles);
        Assert.Equal("Show Title Bar", menu.TitleBarHeader);
    }

    [Fact]
    public void Docked_in_a_shared_frame_hides_the_item_unless_the_setting_is_on()
    {
        var p = new Probe { Alone = false };
        var off = p.Build();
        off.RefreshFloatState();
        Assert.False(off.ShowHideTitleBar);

        var on = p.Build(dockedInit: true);
        on.RefreshFloatState();
        Assert.True(on.ShowHideTitleBar);   // still reachable, to turn it off
        Assert.Equal("Show Title Bar", on.TitleBarHeader);
    }

    [Fact]
    public void Floating_routes_to_the_float_title_bar_not_the_docked_setting()
    {
        var p = new Probe { Floating = true, Alone = false };
        var menu = p.Build();
        menu.RefreshFloatState();

        Assert.True(menu.ShowHideTitleBar);
        ((ICommand)menu.ToggleTitleBarCommand!).Execute(null);

        Assert.Equal(1, p.FloatToggles);
        Assert.Null(p.LastDocked);
        Assert.False(menu.IsDockedTitleBarHidden);
        Assert.Equal("Show Title Bar", menu.TitleBarHeader);   // reads the float
    }

    [Fact]
    public void Sync_updates_the_verb_without_running_the_handler()
    {
        var p = new Probe();
        var menu = p.Build();
        menu.SyncDockedTitleBar(true);

        Assert.True(menu.IsDockedTitleBarHidden);
        Assert.Null(p.LastDocked);
        Assert.Equal("Show Title Bar", menu.TitleBarHeader);
    }
}
