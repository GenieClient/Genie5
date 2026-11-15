using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Genie.App.Views;

/// <summary>Code-behind for <c>CastBarView.axaml</c> — the spell-preparation
/// countdown bar on the hands strip. All state lives on <c>VitalsViewModel</c>.</summary>
public partial class CastBarView : UserControl
{
    public CastBarView() => AvaloniaXamlLoader.Load(this);
}
