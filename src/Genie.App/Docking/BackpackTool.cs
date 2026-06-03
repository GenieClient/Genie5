using Avalonia;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

public class BackpackTool : Tool
{
    public InventoryViewModel ViewModel { get; }

    // Per-window appearance overrides. Backed by SetProperty (the Dock.Model.Mvvm
    // base derives from CommunityToolkit's ObservableObject) rather than Fody
    // [Reactive], since this Tool no longer derives from a ReactiveObject.
    private IBrush?    _toolForeground;
    public  IBrush?    ToolForeground { get => _toolForeground; private set => SetProperty(ref _toolForeground, value); }

    private IBrush?    _toolBackground;
    public  IBrush?    ToolBackground { get => _toolBackground; private set => SetProperty(ref _toolBackground, value); }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }

    private double     _toolFontSize = 11;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public BackpackTool(InventoryViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "backpack";
        Title     = "Backpack";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }
    }

    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        // Resolve per-window sentinels against global DisplaySettings — see
        // WindowSettingsResolver for the full table. Option A: per-window
        // overrides global only when explicitly set.
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
        ToolForeground = WindowSettingsResolver.ResolveForeground(s.Foreground);
        ToolBackground = WindowSettingsResolver.ResolveBackground(s.Background);
    }
}
