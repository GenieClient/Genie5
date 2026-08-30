using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for the built-in Inventory View catalog (the cross-character
/// item tree scanned by <c>/iv scan</c>). A first-class tool (like
/// <see cref="TimeTrackerTool"/>), not a dynamic plugin window — the old
/// external-DLL text panel is replaced by a real TreeView with search,
/// expand/collapse, wiki lookup, export, and per-character removal (the
/// feature set of the Genie 4 plugin's WinForms window).
/// </summary>
public class InventoryViewTool : ActivityTool, IWindowMenuHost
{
    public InventoryViewViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    public InventoryViewTool(InventoryViewViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "inventory-view";
        Title     = "Inventory View";

        if (settings is not null)
        {
            ApplyTitle(settings);
            settings.Changed += () => ApplyTitle(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash: catalog tree rebuilds (e.g. an `/iv scan`
        // finishing while another tab is in front).
        WireActivity(vm.Roots);
    }

    private void ApplyTitle(WindowSettings s) =>
        Title = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
}
