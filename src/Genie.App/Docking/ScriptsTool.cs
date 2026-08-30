using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Script Manager dock tool (id stays <c>"scripts"</c> so saved layouts keep
/// working): script library browser + running-script management + output log.
/// </summary>
public class ScriptsTool : ActivityTool, IWindowMenuHost
{
    public ScriptsViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public ScriptsTool(ScriptsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "scripts";
        Title     = "Script Manager";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: script output lines and scripts starting.
        WireActivity(vm.Output);
        WireActivity(vm.RunningScripts);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
