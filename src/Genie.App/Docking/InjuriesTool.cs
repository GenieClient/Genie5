using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Injuries tool — body-silhouette wound/scar display fed by the server's
/// injuries dialog (issue #18). Hidden by default; re-open via
/// Window → Injuries. Only the title syncs from <see cref="WindowSettings"/>;
/// the cells keep their own severity colour coding.
/// </summary>
public class InjuriesTool : ActivityTool, IWindowMenuHost
{
    public InjuriesViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public InjuriesTool(InjuriesViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "injuries";
        Title     = "Injuries";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: a body part newly wounded/scarred. (Severity
        // shifts on an already-listed part don't touch this collection and
        // stay silent — good enough for a first cut.)
        WireActivity(vm.Injured);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
