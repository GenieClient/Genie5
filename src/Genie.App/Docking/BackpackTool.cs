using Avalonia;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

public class BackpackTool : ActivityTool, IWindowMenuHost, IFindHost
{
    public InventoryViewModel ViewModel { get; }

    /// <summary>In-window Find bar state (#120) — "where did that scimitar
    /// go" over the inventory list.</summary>
    public FindInWindowModel Find { get; }

    /// <summary>Right-click window menu (Clear / Close), built by
    /// <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

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

    // Word Wrap (#120) — see GameTextDocument for the semantics. Especially
    // useful here: wrap OFF keeps long item names on one row so the two-column
    // inventory layout survives narrow panels.
    private TextWrapping _toolTextWrapping = TextWrapping.Wrap;
    public  TextWrapping ToolTextWrapping { get => _toolTextWrapping; private set => SetProperty(ref _toolTextWrapping, value); }

    private Avalonia.Controls.Primitives.ScrollBarVisibility _toolHScroll
        = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    public  Avalonia.Controls.Primitives.ScrollBarVisibility ToolHScroll
    { get => _toolHScroll; private set => SetProperty(ref _toolHScroll, value); }

    public BackpackTool(InventoryViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "backpack";   // persistence key — saved layouts, ws.Get, dock registry depend on it
        Title     = "Inventory";
        Find      = new FindInWindowModel(() => vm.Items.Select(l => l.Text).ToArray());

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }

        // Unread-activity flash: the inventory list rebuilds on inv pushes.
        WireActivity(vm.Items);
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
        ToolTextWrapping = s.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ToolHScroll      = s.WordWrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }
}
