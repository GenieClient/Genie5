using System;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
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
public class PluginWindowTool : Tool, IWindowMenuHost, IFindHost
{
    public PluginWindowViewModel ViewModel { get; }

    /// <summary>In-window Find bar state (#120).</summary>
    public FindInWindowModel Find { get; }

    /// <summary>Right-click window menu (Clear / Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }
    private double     _toolFontSize = 12;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public PluginWindowTool(PluginWindowViewModel vm, string id, string title,
                            Genie.Core.Layout.WindowSettings? settings = null)
    {
        ViewModel = vm;
        Find      = new FindInWindowModel(() => vm.Lines.Select(l => l.Text).ToArray());
        Id        = id;
        Title     = string.IsNullOrWhiteSpace(title) ? vm.Title : title;

        // Keep the dock tab caption in sync if the plugin renames its window.
        vm.WhenAnyValue(x => x.Title)
          .Subscribe(t => { if (!string.IsNullOrWhiteSpace(t)) Title = t; });

        // Public #233: honour per-window font settings. Plugin windows are
        // registered into the WindowSettingsStore at creation (they are
        // dynamic, so they only appear in Configuration → Layout once the
        // plugin has opened them; settings are session-scoped for now —
        // persisted rows for ids registered after load are dropped by
        // WindowSettingsStore.Apply). DisplayTitle is deliberately NOT
        // applied here: the plugin owns its own window title.
        if (settings is not null)
        {
            ApplyFonts(settings);
            settings.Changed += () => ApplyFonts(settings);
        }
    }

    private void ApplyFonts(Genie.Core.Layout.WindowSettings s)
    {
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}
