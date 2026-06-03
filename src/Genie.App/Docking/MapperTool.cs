using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// AutoMapper dock tool. Shows current zone, current room, exits, and a toggle
/// for auto-creating new rooms during exploration.
/// </summary>
public class MapperTool : Tool
{
    public MapperViewModel ViewModel { get; }

    public MapperTool(MapperViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "mapper";
        Title     = "Mapper";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
