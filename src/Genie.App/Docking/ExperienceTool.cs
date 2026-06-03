using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for the Experience plugin's named-window output. Monospaced so
/// the plugin's column-aligned skill rows line up.
/// </summary>
public class ExperienceTool : Tool
{
    public ExperienceViewModel ViewModel { get; }

    public FontFamily ToolFontFamily { get; } =
        new("Cascadia Mono,Consolas,Courier New,monospace");
    public double ToolFontSize { get; } = 12;

    public ExperienceTool(ExperienceViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "experience";
        Title     = "Experience";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
