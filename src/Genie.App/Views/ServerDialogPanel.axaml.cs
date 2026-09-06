using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Genie.App.ViewModels;

namespace Genie.App.Views;

/// <summary>
/// Code-behind for the server-driven dialog panel (#156 Phase 1).
///
/// <para>The body grid is rebuilt here rather than bound: row and column counts
/// change with every <c>dialogData</c> delta, and Avalonia will not convert a
/// bound string into <c>RowDefinitions</c>/<c>ColumnDefinitions</c>. Each child
/// is a <see cref="ContentControl"/> holding the control's view-model, so the
/// type-matched DataTemplates in the .axaml still do the rendering.</para>
///
/// <para>Interaction handlers route by control ID (carried on
/// <see cref="Control.Tag"/>) back into the view-model, which owns command
/// resolution. The view never builds or sends a command itself.</para>
/// </summary>
public partial class ServerDialogPanel : UserControl
{
    private ServerDialogViewModel? _vm;

    public ServerDialogPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.Controls.CollectionChanged -= OnControlsChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as ServerDialogViewModel;
        if (_vm is null) return;

        _vm.Controls.CollectionChanged += OnControlsChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        RebuildGrid();
    }

    private void OnControlsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildGrid();

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Row/column counts move independently of the control collection when a
        // delta only repositions what is already there.
        if (e.PropertyName is nameof(ServerDialogViewModel.Columns)
                           or nameof(ServerDialogViewModel.Rows))
            RebuildGrid();
    }

    private void RebuildGrid()
    {
        var body = this.FindControl<Grid>("Body");
        if (body is null || _vm is null) return;

        body.Children.Clear();
        body.RowDefinitions.Clear();
        body.ColumnDefinitions.Clear();

        // Every track sizes to content — the point of inferring a grid instead
        // of honouring the server's pixel coordinates.
        for (int c = 0; c < Math.Max(_vm.Columns, 1); c++)
            body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < Math.Max(_vm.Rows, 1); r++)
            body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        foreach (var control in _vm.Controls)
        {
            var host = new ContentControl
            {
                Content           = control,
                Margin            = new Avalonia.Thickness(0, 0, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = control.CentreAligned ? HorizontalAlignment.Center
                                    : control.RightAlignedOrStretch(),
            };

            Grid.SetRow(host, Math.Clamp(control.Row, 0, body.RowDefinitions.Count - 1));
            Grid.SetColumn(host, Math.Clamp(control.Column, 0, body.ColumnDefinitions.Count - 1));
            Grid.SetColumnSpan(host, Math.Clamp(control.ColumnSpan, 1, body.ColumnDefinitions.Count));
            body.Children.Add(host);
        }
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private void OnControlActivated(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is { } id) _vm?.Activate(id);
    }

    private void OnRadioSelected(object? sender, RoutedEventArgs e)
    {
        if (IdOf(sender) is not { } id) return;
        _vm?.SelectRadio(id);
        _vm?.Activate(id);
    }

    private void OnComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        // A dropDownBox fires its own command on selection change — but only if
        // it HAS one. Otherwise it is just an input feeding a button's
        // %placeholder%, and firing here would send a bare command on every
        // scroll through the list.
        if (sender is not ComboBox { Tag: string id } ||
            (sender as ComboBox)?.DataContext is not DialogComboViewModel { HasOwnCommand: true })
            return;
        _vm?.Activate(id);
    }

    private void OnEditBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter commits an editBox, matching how the game's own input works.
        if (e.Key != Key.Enter) return;
        if (IdOf(sender) is not { } id) return;
        _vm?.Activate(id);
        e.Handled = true;
    }

    private static string? IdOf(object? sender) =>
        sender is Control { Tag: string id } && id.Length > 0 ? id : null;
}

internal static class DialogAlignmentExtensions
{
    /// <summary>A right-aligned control (<c>align="ne"</c>) hugs the right edge
    /// of its cell; everything else starts at the left.</summary>
    public static HorizontalAlignment RightAlignedOrStretch(this DialogControlViewModel vm) =>
        vm.RightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left;
}
