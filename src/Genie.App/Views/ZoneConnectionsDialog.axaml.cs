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

        // Room suggestions match anywhere in "id · title · #server id", so typing
        // "teller" or "9792" both find a room; the stored value stays the short id.
        foreach (var (box, drop) in new[] { (FromRoomBox, FromRoomDrop), (ToRoomBox, ToRoomDrop) })
        {
            box.ItemFilter = (search, item) =>
                string.IsNullOrWhiteSpace(search) ||
                (item is RoomChoice r && r.Display.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
            var b = box;
            drop.Click += (_, _) => { b.Focus(); b.IsDropDownOpen = true; };
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
