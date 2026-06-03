using Avalonia;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

public class GameTextDocument : Document
{
    public GameTextViewModel ViewModel { get; }

    // Per-window appearance overrides. SetProperty-backed (Dock.Model.Mvvm base
    // is a CommunityToolkit ObservableObject) instead of Fody [Reactive].

    /// <summary>Per-window foreground brush. Null falls through to the global GameBrush.</summary>
    private IBrush?    _toolForeground;
    public  IBrush?    ToolForeground { get => _toolForeground; private set => SetProperty(ref _toolForeground, value); }

    /// <summary>Per-window background brush. Null = transparent (default).</summary>
    private IBrush?    _toolBackground;
    public  IBrush?    ToolBackground { get => _toolBackground; private set => SetProperty(ref _toolBackground, value); }

    /// <summary>Per-window font family override.</summary>
    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }

    /// <summary>Per-window font size override.</summary>
    private double     _toolFontSize = 13;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public GameTextDocument(GameTextViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "game-text";
        Title     = "Game";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }
    }

    /// <summary>
    /// Copy values from <see cref="WindowSettings"/> into the reactive UI
    /// properties. Called once at construction and re-called on every
    /// <c>Changed</c> notification so Layout-tab edits repaint live.
    /// </summary>
    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        // Resolve per-window sentinels (empty FontFamily / non-positive
        // FontSize / "Default" Foreground) against the global DisplaySettings
        // values pushed to Application.Resources. Option A: per-window
        // overrides global only when explicitly set. See
        // WindowSettingsResolver for the full sentinel table.
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
        ToolForeground = WindowSettingsResolver.ResolveForeground(s.Foreground);
        ToolBackground = WindowSettingsResolver.ResolveBackground(s.Background); // null = transparent
    }
}
