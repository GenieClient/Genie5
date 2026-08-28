using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for the Experience plugin's named-window output. Monospaced so
/// the plugin's column-aligned skill rows line up.
/// </summary>
public class ExperienceTool : Tool, IWindowMenuHost
{
    public ExperienceViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }
    private double     _toolFontSize = 12;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public ExperienceTool(ExperienceViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "experience";
        Title     = "Experience";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }
    }

    // Public #233: these were get-only constants — the Layout tab saved the
    // per-window font correctly, the panel just never read it back. Same
    // resolver pattern as StreamTool; only the properties this panel's
    // DataTemplate actually binds (family + size) are applied.
    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}
