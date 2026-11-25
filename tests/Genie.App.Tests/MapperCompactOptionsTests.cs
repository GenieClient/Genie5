using System;
using System.Globalization;
using System.IO;
using Genie.App.Controls;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The compact-map options (#450): the Mapper as a small docked minimap. Four
/// switches — Details by click instead of hover, hide the toolbar, hide the
/// status strip, right-click to walk — that must leave today's behaviour alone
/// until the user turns one, and be remembered once they do.
/// </summary>
public class MapperCompactOptionsTests
{
    private static string TempDisplayPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "display.json");
    }

    [Fact]
    public void Defaults_keep_the_mapper_as_it_was()
    {
        var display = new DisplaySettings();
        Assert.True(display.MapDetailsOnHover);
        Assert.True(display.ShowMapToolbar);
        Assert.True(display.ShowMapStatusStrip);
        Assert.False(display.MapRightClickWalks);

        // Before a DisplaySettings is attached the view model reports the same.
        var vm = new MapperViewModel();
        Assert.True(vm.MapDetailsOnHover);
        Assert.True(vm.ShowMapToolbar);
        Assert.True(vm.ShowMapStatusStrip);
        Assert.False(vm.MapRightClickWalks);
    }

    [Fact]
    public void A_display_file_written_before_these_options_existed_loads_with_the_defaults()
    {
        var path = TempDisplayPath();
        try
        {
            File.WriteAllText(path, "{ \"ShowMapLegend\": false }");
            var display = DisplaySettings.Load(path);

            Assert.False(display.ShowMapLegend);
            Assert.True(display.MapDetailsOnHover);
            Assert.True(display.ShowMapToolbar);
            Assert.True(display.ShowMapStatusStrip);
            Assert.False(display.MapRightClickWalks);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void Each_option_is_saved_when_changed_and_read_back_by_the_next_session()
    {
        var path = TempDisplayPath();
        try
        {
            var vm = new MapperViewModel();
            vm.AttachDisplay(new DisplaySettings(), path);

            vm.MapDetailsOnHover  = false;
            vm.ShowMapToolbar     = false;
            vm.ShowMapStatusStrip = false;
            vm.MapRightClickWalks = true;

            var next = new MapperViewModel();
            next.AttachDisplay(DisplaySettings.Load(path), path);

            Assert.False(next.MapDetailsOnHover);
            Assert.False(next.ShowMapToolbar);
            Assert.False(next.ShowMapStatusStrip);
            Assert.True(next.MapRightClickWalks);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public void Changing_an_option_notifies_the_bound_menu_item_and_panel()
    {
        var path = TempDisplayPath();
        try
        {
            var vm = new MapperViewModel();
            vm.AttachDisplay(new DisplaySettings(), path);
            var raised = new System.Collections.Generic.List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            vm.ShowMapToolbar     = false;
            vm.ShowMapStatusStrip = false;
            vm.MapDetailsOnHover  = false;
            vm.MapRightClickWalks = true;

            Assert.Contains(nameof(MapperViewModel.ShowMapToolbar), raised);
            Assert.Contains(nameof(MapperViewModel.ShowMapStatusStrip), raised);
            Assert.Contains(nameof(MapperViewModel.MapDetailsOnHover), raised);
            Assert.Contains(nameof(MapperViewModel.MapRightClickWalks), raised);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    // hovered, hoverEnabled, pinned → open
    [Theory]
    [InlineData(false, true,  false, false)]   // idle
    [InlineData(true,  true,  false, true)]    // hover opens it (the default)
    [InlineData(true,  false, false, false)]   // hover-to-open off: hovering does nothing
    [InlineData(false, false, true,  true)]    // …but a click (pin) still opens it
    [InlineData(true,  false, true,  true)]
    [InlineData(false, true,  true,  true)]
    public void Details_panel_opens_when_pinned_or_when_hovered_with_hover_enabled(
        bool hovered, bool hoverEnabled, bool pinned, bool expected)
    {
        var open = HoverOrPinnedConverter.Instance.Convert(
            new object?[] { hovered, hoverEnabled, pinned }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(expected, open);
    }

    [Fact]
    public void Details_panel_stays_closed_while_its_bindings_are_still_unset()
    {
        // Avalonia hands a MultiBinding UnsetValue until every source resolves.
        var open = HoverOrPinnedConverter.Instance.Convert(
            new object?[] { Avalonia.AvaloniaProperty.UnsetValue, Avalonia.AvaloniaProperty.UnsetValue, Avalonia.AvaloniaProperty.UnsetValue },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, open);
    }

    // optionOn, hitRoom, editMode, shiftHeld → walk
    [Theory]
    [InlineData(false, true,  false, false, false)]  // option off: always the menu
    [InlineData(true,  true,  false, false, true)]   // option on, on a room: walk
    [InlineData(true,  false, false, false, false)]  // empty map: the menu (Float / Close live there)
    [InlineData(true,  true,  true,  false, false)]  // edit mode: the menu (Edit Exit, Remove)
    [InlineData(true,  true,  false, true,  false)]  // Shift: the menu on demand
    public void Right_click_walks_only_with_the_option_on_a_room_outside_edit_mode_without_shift(
        bool optionOn, bool hitRoom, bool editMode, bool shiftHeld, bool expected)
        => Assert.Equal(expected, MapCanvas.ShouldWalkOnRightClick(optionOn, hitRoom, editMode, shiftHeld));
}
