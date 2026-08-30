using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for the Spell Timer's "Active Spells" named-window output.
/// Monospaced so the tracker's column-aligned spell rows line up. A first-class
/// tool (like <see cref="ExperienceTool"/>), not a dynamic plugin window — that
/// is what gives it MDI decorations and stops it re-opening on every prompt
/// after the user closes it (public #112).
/// </summary>
public class ActiveSpellsTool : ActivityTool, IWindowMenuHost
{
    public ActiveSpellsViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }
    private double     _toolFontSize = 12;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    public ActiveSpellsTool(ActiveSpellsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "active-spells";
        Title     = "Active Spells";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash. [Reactive] raises only on a REAL content
        // change, so an unchanged re-push from the Spell Timer stays silent
        // (duration ticks do change the text, and do count).
        WireActivity(vm, nameof(ActiveSpellsViewModel.Content));
    }

    // Public #233 — see ExperienceTool.ApplySettings.
    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}
