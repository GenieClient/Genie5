using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.App.ViewModels;

namespace Genie.App.Views;

/// <summary>Bespoke injuries-&lt;charnum&gt; view — see <see cref="OtherInjuriesViewModel"/>.</summary>
public partial class OtherInjuriesPanel : UserControl
{
    public OtherInjuriesPanel() => InitializeComponent();

    private void OnPartClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is OtherInjuriesViewModel vm
            && (sender as Control)?.Tag is OtherInjuriesViewModel.Part part)
            vm.Transfer(part);
    }
}
