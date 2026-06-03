using System;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Docking;

/// <summary>
/// Generic dock panel for a plugin-created window. One instance per plugin
/// window name; the <see cref="PluginWindowViewModel"/> carries the content and
/// title. Monospaced so plugins that render column-aligned text (inventory
/// trees, tables) line up — same treatment as the Experience panel.
///
/// <para>The dock <see cref="Dock.Model.Mvvm.Controls.Tool.Id"/> is the
/// canonical <c>pluginwin:&lt;name&gt;</c> key (see
/// <see cref="GenieDockFactory.PluginWindowId"/>), so the panel round-trips
/// through saved layouts and the Window menu's visibility toggles just like a
/// built-in tool.</para>
/// </summary>
public class PluginWindowTool : Tool
{
    public PluginWindowViewModel ViewModel { get; }

    public FontFamily ToolFontFamily { get; } =
        new("Cascadia Mono,Consolas,Courier New,monospace");
    public double ToolFontSize { get; } = 12;

    public PluginWindowTool(PluginWindowViewModel vm, string id, string title)
    {
        ViewModel = vm;
        Id        = id;
        Title     = string.IsNullOrWhiteSpace(title) ? vm.Title : title;

        // Keep the dock tab caption in sync if the plugin renames its window.
        vm.WhenAnyValue(x => x.Title)
          .Subscribe(t => { if (!string.IsNullOrWhiteSpace(t)) Title = t; });
    }
}
