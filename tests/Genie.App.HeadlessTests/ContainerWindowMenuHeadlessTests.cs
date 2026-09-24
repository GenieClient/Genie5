using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>Public #336 — container windows are created hidden and are listed with
/// the server-described windows, not as script windows.</summary>
public sealed class ContainerWindowMenuHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_containerwin_" + Guid.NewGuid().ToString("N"));

    public ContainerWindowMenuHeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public void A_container_window_is_created_hidden_and_listed_as_a_container()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var f = (GenieDockFactory)vm.DockFactory!;

        f.GetOrCreateContainerWindow("My Backpack").AppendLine("a coil of rope");
        var id = GenieDockFactory.PluginWindowId("My Backpack");

        Assert.False(f.IsToolVisible(id));
        Assert.Equal(new[] { "My Backpack" }, f.ContainerWindows().Select(w => w.Title));
        Assert.Empty(f.PluginWindows(pluginOwned: false));   // not a script window
        Assert.NotNull(f.TryGetPluginWindow("My Backpack"));
        Assert.Null(f.TryGetPluginWindow("My Quiver"));

        // Opening it from the menu works like any other panel.
        f.SetToolVisibility(id, true);
        Assert.True(f.IsToolVisible(id));
    }
}
