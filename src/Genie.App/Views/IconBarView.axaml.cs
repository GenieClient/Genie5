using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Genie.App.Views;

/// <summary>Code-behind for <c>IconBarView.axaml</c> — the extracted Icon Bar
/// (#349). No logic: placement, visibility and data all come from the host.</summary>
public partial class IconBarView : UserControl
{
    public IconBarView() => AvaloniaXamlLoader.Load(this);
}
