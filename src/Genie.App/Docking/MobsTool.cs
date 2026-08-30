using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Mobs tool — lists the creatures in the room (issue #86). Hidden by default;
/// re-open via Window → Mobs. Only the title syncs from
/// <see cref="WindowSettings"/>; the list keeps its own colour coding.
/// </summary>
public class MobsTool : ActivityTool, IWindowMenuHost
{
    public MobsViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public MobsTool(MobsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "mobs";
        Title     = "Mobs";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        // Unread-activity flash: a creature arriving in the room.
        WireActivity(vm.Mobs);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
