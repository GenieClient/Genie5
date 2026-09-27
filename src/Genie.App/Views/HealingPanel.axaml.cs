using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.App.ViewModels;
using Genie.Core.Health;

namespace Genie.App.Views;

/// <summary>
/// Healing panel view — see <see cref="HealingViewModel"/>. Each handler is a
/// button's Click, and each one forwards to exactly one of the view-model's
/// click entry points: the only way this panel sends anything.
/// </summary>
public partial class HealingPanel : UserControl
{
    public HealingPanel() => InitializeComponent();

    private HealingViewModel? Vm => DataContext as HealingViewModel;

    private void OnPerceive(object? sender, RoutedEventArgs e) => Vm?.Perceive();
    private void OnTouch(object? sender, RoutedEventArgs e)    => Vm?.Touch();
    private void OnForget(object? sender, RoutedEventArgs e)   => Vm?.Forget();
    private void OnTakeAll(object? sender, RoutedEventArgs e)  => Vm?.TakeAll();
    private void OnStop(object? sender, RoutedEventArgs e)     => Vm?.Stop();
    private void OnCast(object? sender, RoutedEventArgs e)     => Vm?.CastPending();

    private void OnRegionClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is HealingViewModel.RegionCell cell) Vm?.HealRegion(cell);
    }

    private void OnCondition(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag &&
            Enum.TryParse<HealingCondition>(tag, out var condition))
            Vm?.TakeCondition(condition);
    }

    private void OnSpell(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is HealingViewModel.SpellRow row) Vm?.CastSpell(row.Info.Spell);
    }
}
