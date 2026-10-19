using Avalonia.ReactiveUI;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

public partial class LinkRoomsDialog : ReactiveWindow<LinkRoomsViewModel>
{
    public LinkRoomsDialog()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            d(ViewModel!.OkCommand    .Subscribe(ok => Close(ok)));
            d(ViewModel!.CancelCommand.Subscribe(_  => Close(false)));
        });
    }
}
