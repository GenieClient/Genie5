using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Genie.App.Views;

/// <summary>Code-behind for <c>RestedExpBarView.axaml</c> — the rested-EXP gauge
/// on the hands strip. All state lives on <c>VitalsViewModel</c>.</summary>
public partial class RestedExpBarView : UserControl
{
    public RestedExpBarView() => AvaloniaXamlLoader.Load(this);
}
