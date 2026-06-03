using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Scripts dock tool. Shows currently-running scripts with stop buttons,
/// a Start... command for the file picker, and a scrollable echo log.
/// </summary>
public class ScriptsTool : Tool
{
    public ScriptsViewModel ViewModel { get; }

    public ScriptsTool(ScriptsViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "scripts";
        Title     = "Scripts";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
