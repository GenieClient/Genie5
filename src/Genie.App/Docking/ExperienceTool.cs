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
public class ExperienceTool : ActivityTool, IWindowMenuHost
{
    public ExperienceViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }
    private double     _toolFontSize = 12;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    // Word Wrap (#120 semantics, same per-window WindowSettings.WordWrap every
    // other text panel uses). The rows are column-aligned, so wrapping only
    // bites once the panel is narrower than a row: wrap ON folds the overflow
    // onto a second line (Genie 4 EXPTracker behaviour — nothing hides), wrap
    // OFF keeps the columns rigid and hands long rows an h-scrollbar.
    private TextWrapping _toolTextWrapping = TextWrapping.Wrap;
    public  TextWrapping ToolTextWrapping { get => _toolTextWrapping; private set => SetProperty(ref _toolTextWrapping, value); }

    // Paired with the wrap mode: an Auto h-scrollbar gives the ItemsControl
    // infinite width, which would stop wrapping from ever happening.
    private Avalonia.Controls.Primitives.ScrollBarVisibility _toolHScroll
        = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    public  Avalonia.Controls.Primitives.ScrollBarVisibility ToolHScroll
    { get => _toolHScroll; private set => SetProperty(ref _toolHScroll, value); }

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

        ActivitySettings = settings;

        // Unread-activity flash: every exp push rebuilds Lines (Clear + Adds),
        // so Add events mark real updates; the highlight-repaint path uses
        // Replace and stays silent.
        WireActivity(vm.Lines);
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
        ToolTextWrapping = s.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ToolHScroll      = s.WordWrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }
}
