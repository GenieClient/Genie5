using System;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

/// <summary>
/// Code-behind for <c>StatusBarView.axaml</c> — the extracted vitals / Health
/// strip (#349).
///
/// <para>Owns the Magic Panels column flip, which used to live in
/// <c>MainWindow.axaml.cs</c>. Genie 4 collapses the mana column so the other
/// four vitals stretch equally (<c>SetMagicPanels</c> flips
/// <c>TableLayoutPanelBars.ColumnCount</c> 5 ↔ 4). It has to be done in code
/// because <c>ColumnDefinition.Width</c> cannot be data-bound; the mana cell's
/// own IsVisible binding hides the content, and this reclaims the space.</para>
///
/// <para>It moved here because the strip is now declared once per dock slot. A
/// single by-name lookup from MainWindow would have found whichever instance
/// happened to be realized, so the flip would silently stop applying the first
/// time someone moved the bar to the top.</para>
/// </summary>
public partial class StatusBarView : UserControl
{
    private IDisposable? _magicPanels;

    public StatusBarView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Rewire();
    }

    private void Rewire()
    {
        _magicPanels?.Dispose();
        _magicPanels = null;

        if (DataContext is not MainWindowViewModel vm) return;
        var grid = this.FindControl<Grid>("StatusBarGrid");
        if (grid is null || grid.ColumnDefinitions.Count < 2) return;

        _magicPanels = vm.Display.WhenAnyValue(x => x.ShowMagicPanels)
            .Subscribe(show => grid.ColumnDefinitions[1].Width =
                show ? new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star)
                     : new Avalonia.Controls.GridLength(0));
    }
}
