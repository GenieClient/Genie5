using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #403: in windowed (MDI) mode, right-clicking inside a window opens the
/// Genie 4 per-window menu (Clear, Time Stamp, Name List Only, Close Window and
/// Genie 5's extras), as it does in docked mode. Both window frames use one
/// shared menu (Themes/WindowContextMenu.axaml), which takes the DataContext of
/// the window it opens on.
/// </summary>
public class MdiWindowMenuHeadlessTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "genie_mdimenu_" + Guid.NewGuid().ToString("N"))).FullName;
    private readonly MainWindowViewModel _vm;
    private readonly Window _main;

    public MdiWindowMenuHeadlessTests()
    {
        _vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var dock = new DockControl { Layout = _vm.DockLayout, Factory = _vm.DockFactory, InitializeLayout = false };
        ((INotifyPropertyChanged)_vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DockLayout)) dock.Layout = _vm.DockLayout;
        };
        _main = new Window { Width = 1200, Height = 800, Content = dock };
        _main.Show();
        Pump();
    }

    public void Dispose()
    {
        _main.Close();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Pump()
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); _main.UpdateLayout(); }
    }

    private void Windowed()
    {
        if (!_vm.Display.WindowedMode) _vm.ToggleWindowedModeCommand.Execute().Subscribe();
        Pump();
        Assert.True(_vm.Display.WindowedMode);
    }

    /// <summary>The content area of the MDI child window showing <paramref name="id"/>.</summary>
    private ContentControl MdiContent(string id)
    {
        var window = _main.GetVisualDescendants().OfType<MdiDocumentWindow>()
            .Single(w => (w.DataContext as IDockable)?.Id == id);
        return window.GetVisualDescendants().OfType<ContentControl>().First(c => c.Name == "PART_ContentPresenter");
    }

    /// <summary>Open <paramref name="host"/>'s menu as a right-click would and
    /// return the headers of the items it shows.</summary>
    private string[] OpenMenu(Control host)
    {
        var menu = host.ContextMenu;
        Assert.NotNull(menu);
        menu!.Open(host);
        Pump();
        var shown = menu.Items.OfType<MenuItem>().Where(m => m.IsVisible).Select(m => m.Header?.ToString() ?? "").ToArray();
        menu.Close();
        Pump();
        return shown;
    }

    [AvaloniaFact]
    public void Right_click_inside_an_MDI_stream_window_opens_the_window_menu()
    {
        Windowed();
        var shown = OpenMenu(MdiContent("talk"));

        Assert.Contains("Clear",          shown);
        Assert.Contains("Time Stamp",     shown);
        Assert.Contains("Name List Only", shown);
        Assert.Contains("Copy All",       shown);
        Assert.Contains("Close Window",   shown);
    }

    [AvaloniaFact]
    public void The_shared_menu_follows_the_window_it_opens_on()
    {
        Windowed();
        var talk = MdiContent("talk");
        var room = MdiContent("room");
        Assert.Same(talk.ContextMenu, room.ContextMenu);     // one shared menu

        var roomItems = OpenMenu(room);
        var talkItems = OpenMenu(talk);

        // Each window shows its own items: the stream window has Name List Only,
        // the Room panel doesn't.
        Assert.Contains("Name List Only", talkItems);
        Assert.DoesNotContain("Name List Only", roomItems);
        Assert.Contains("Close Window", roomItems);
    }

    [AvaloniaFact]
    public void A_menu_item_acts_on_the_window_it_was_opened_on()
    {
        Windowed();
        var talk = MdiContent("talk");
        var host = (IWindowMenuHost)talk.DataContext!;
        var before = host.WindowMenu.IsTimestampOn;

        var menu = talk.ContextMenu!;
        menu.Open(talk);
        Pump();
        var stamp = menu.Items.OfType<MenuItem>().Single(m => (m.Header as string) == "Time Stamp");
        stamp.IsChecked = !stamp.IsChecked;
        menu.Close();
        Pump();

        Assert.NotEqual(before, host.WindowMenu.IsTimestampOn);
    }

    [AvaloniaFact]
    public void Text_in_an_MDI_window_has_no_competing_copy_flyout()
    {
        Windowed();
        var talk = MdiContent("talk");
        var probe = new SelectableTextBlock { Text = "line" };
        talk.Content = probe;    // any text inside the window's content area
        Pump();

        Assert.Null(probe.ContextFlyout);
    }

    [AvaloniaFact]
    public void Text_in_a_docked_window_has_no_competing_copy_flyout_either()
    {
        var docked = _main.GetVisualDescendants().OfType<ContentControl>()
            .First(c => c.Name == "PART_CachedContentPresenter" && c.ContextMenu is not null);
        var probe = new SelectableTextBlock { Text = "line" };
        docked.Content = probe;
        Pump();

        Assert.Null(probe.ContextFlyout);
    }

    [AvaloniaFact]
    public void Docked_mode_still_uses_the_same_menu()
    {
        Assert.False(_vm.Display.WindowedMode);
        var docked = _main.GetVisualDescendants().OfType<ContentControl>()
            .First(c => c.Name == "PART_CachedContentPresenter" && c.ContextMenu is not null);
        var dockedMenu = docked.ContextMenu;

        Windowed();
        Assert.Same(dockedMenu, MdiContent("talk").ContextMenu);
    }
}
