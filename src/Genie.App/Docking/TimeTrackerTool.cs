using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for the built-in Time Tracker's "Time Tracker" named-window
/// output. Monospaced so the tracker's column-aligned moon rows line up. A
/// first-class tool (like <see cref="ActiveSpellsTool"/>), not a dynamic
/// plugin window — the tracker is builtin now, so its panel belongs in the
/// top-level Window menu, keeps MDI decorations, and never re-opens itself
/// on a heartbeat repaint after the user closes it.
/// </summary>
public class TimeTrackerTool : Tool, IWindowMenuHost
{
    public TimeTrackerViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }
    private double     _toolFontSize = 12;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public TimeTrackerTool(TimeTrackerViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "time-tracker";
        Title     = "Time Tracker";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }
    }

    // Public #233 — see ExperienceTool.ApplySettings.
    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}
