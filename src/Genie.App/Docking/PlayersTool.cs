using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Players tool — lists the other players in the room (issue #86). Hidden by
/// default; re-open via Window → Players. Only the title syncs from
/// <see cref="WindowSettings"/>; the list keeps its own colour coding.
/// </summary>
public class PlayersTool : ActivityTool, IWindowMenuHost
{
    public PlayersViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public PlayersTool(PlayersViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "players";
        Title     = "Players";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: a player arriving in the room.
        WireActivity(vm.Players);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
