using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Healing tool — the Empath healing console (public #263): per-patient body
/// diagrams from <c>perceive health</c> / <c>touch</c> readings, with
/// click-to-heal. Hidden by default; re-open via Window → Healing. Layout lives
/// in <c>Views/HealingPanel.axaml</c>.
/// </summary>
public class HealingTool : ActivityTool, IWindowMenuHost
{
    public HealingViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public HealingTool(HealingViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "healing";
        Title     = "Healing";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: a new reading landed (any patient).
        WireActivity(vm, nameof(HealingViewModel.ReadingTitle));
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
