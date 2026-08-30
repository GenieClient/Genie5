using Genie.Core.Layout;
using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;

namespace Genie.App.Docking;

/// <summary>
/// Vitals tool — the inner controls (bars, hand labels) keep their hard-coded
/// per-channel colours, so the per-window foreground/background isn't wired
/// here. Only the title syncs from <see cref="WindowSettings"/> for now.
/// </summary>
public class VitalsTool : ActivityTool, IWindowMenuHost
{
    public VitalsViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public VitalsTool(VitalsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "vitals";
        Title     = "Vitals";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: only the five vitals values. The RT /
        // cast-bar / type-ahead properties tick continuously and would blink
        // this tab permanently; a vitals CHANGE is the actual signal.
        WireActivity(vm,
            nameof(VitalsViewModel.Health),
            nameof(VitalsViewModel.Mana),
            nameof(VitalsViewModel.Spirit),
            nameof(VitalsViewModel.Stamina),
            nameof(VitalsViewModel.Concentration));
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
