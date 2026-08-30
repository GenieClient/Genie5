using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Portrait tool — shows DR room/scene artwork (the <c>&lt;resource picture&gt;</c>
/// image). This is Genie 4's Portrait window (issue #26 menu parity); it was
/// briefly named "Scene" in early alphas — the internal id stays <c>scene</c>
/// so saved layouts keep restoring it. Hidden by default; re-open via
/// Window → Portrait. Only the title syncs from <see cref="WindowSettings"/>;
/// the image fills the panel.
/// </summary>
public class SceneTool : ActivityTool, IWindowMenuHost
{
    public SceneViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public SceneTool(SceneViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "scene";     // stable id — saved layouts predate the rename
        Title     = "Portrait";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        // Unread-activity flash: a new scene picture arrived.
        WireActivity(vm, nameof(SceneViewModel.Image));
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
