using Genie.Core.Layout;
using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;

namespace Genie.App.Docking;

/// <summary>
/// Vitals tool — the inner controls (bars, hand labels) keep their hard-coded
/// per-channel colours, so the per-window foreground/background isn't wired
/// here. Only the title syncs from <see cref="WindowSettings"/> for now.
/// </summary>
public class VitalsTool : Tool
{
    public VitalsViewModel ViewModel { get; }

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
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
