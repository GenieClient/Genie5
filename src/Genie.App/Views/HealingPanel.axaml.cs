using System;
using Avalonia.Controls;
using Avalonia.Input;
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

    /// <summary>A right-click on a region: transfer it. Button's own handler
    /// only acts on the left button, so the right release reaches here; no
    /// context menu is attached, so nothing else opens.</summary>
    /// <summary>
    /// Right-click on a tile = transfer. Claimed on ContextRequested, not on the
    /// pointer release: the dock hangs the window menu (Flash on Activity /
    /// Float / Close Window) on an ancestor, and that menu opens on the same
    /// context request, so a release handler lost the race and the menu
    /// opened instead (2026-10-01 walk). Marking it handled keeps the window
    /// menu shut over a tile; off the tiles it still opens as before.
    /// </summary>
    private void OnRegionContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((sender as Control)?.Tag is not HealingViewModel.RegionCell cell) return;
        e.Handled = true;
        Vm?.TransferRegion(cell);
    }

    private void OnDrControl(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string id) Vm?.ActivateDialogControl(id);
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
