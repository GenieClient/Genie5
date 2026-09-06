using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Objects tool — lists what's on the ground in the room, one entry per line
/// (issue #329; the third leg of the #86 Mobs/Players pair). Hidden by
/// default; re-open via Window → Objects. Only the title syncs from
/// <see cref="WindowSettings"/>; the rows keep their own colour coding.
/// </summary>
public class ObjectsTool : ActivityTool, IWindowMenuHost
{
    public ObjectsViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public ObjectsTool(ObjectsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "objects";
        Title     = "Objects";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: something new on the ground.
        WireActivity(vm.Objects);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
