using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #367 — script-created and plugin-created windows share one registry;
/// the Window menu now lists them in separate Plugin Windows / Script Windows groups.
/// </summary>
public sealed class WindowOriginMenuHeadlessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "genie_winorigin_ui_" + Guid.NewGuid().ToString("N"));

    public WindowOriginMenuHeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public void Windows_split_by_origin_and_a_plugin_write_claims_an_existing_script_window()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var f = (GenieDockFactory)vm.DockFactory!;

        f.GetOrCreatePluginWindow("ScriptMenu");
        f.MarkPluginOwned("Inventory View");
        f.GetOrCreatePluginWindow("Inventory View");

        Assert.Equal(new[] { "Inventory View" }, f.PluginWindows(pluginOwned: true).Select(w => w.Title));
        Assert.Equal(new[] { "ScriptMenu" },     f.PluginWindows(pluginOwned: false).Select(w => w.Title));

        // Names are case-insensitive, like the window registry itself.
        f.MarkPluginOwned("scriptmenu");
        Assert.Empty(f.PluginWindows(pluginOwned: false));
        Assert.Equal(2, f.PluginWindows(pluginOwned: true).Count);
    }
}
