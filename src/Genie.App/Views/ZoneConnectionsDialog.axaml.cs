using Avalonia.Controls;
using Avalonia.ReactiveUI;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

public partial class ZoneConnectionsDialog : ReactiveWindow<ZoneConnectionsViewModel>
{
    public ZoneConnectionsDialog()
    {
        InitializeComponent();

        // Zone and room boxes work the same way: type anywhere in the text to
        // filter, or press ▾ to see everything. Rooms match "id · title · #server
        // id"; the stored value stays the short id. The "Add zone…" entry is
        // always offered.
        foreach (var (box, drop) in new[]
                 { (FromZoneBox, FromZoneDrop), (ToZoneBox, ToZoneDrop), (FromRoomBox, FromRoomDrop), (ToRoomBox, ToRoomDrop) })
        {
            box.ItemFilter = (search, item) =>
            {
                if (item is string zone && zone == ZoneConnectionsViewModel.AddZoneItem) return true;
                if (string.IsNullOrWhiteSpace(search)) return true;
                var hay = item is RoomChoice r ? r.Display : item?.ToString() ?? "";
                return hay.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
            };
            var b = box;
            drop.Click += (_, _) =>
            {
                b.Focus();
                // A box that already holds a value filters to it; ▾ should show the whole list.
                b.IsDropDownOpen = true;
            };
        }

        this.WhenActivated(d =>
        {
            // Save doesn't close — the user often saves multiple times
            // while editing. Close button explicitly dismisses the
            // dialog with output=false.
            d(ViewModel!.CloseCommand.Subscribe(_ => Close(false)));
        });
    }
}
