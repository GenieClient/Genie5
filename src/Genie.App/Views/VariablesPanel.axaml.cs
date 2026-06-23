using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.Core.Import;
using Genie.Core.Variables;

namespace Genie.App.Views;

/// <summary>
/// User-variable editor — wraps <see cref="VariableStore"/> directly.  Scripts
/// reference these as <c>$Name</c>; the store is the single source of truth.
/// </summary>
public partial class VariablesPanel : UserControl
{
    public sealed record VariableRow(string Name, string Value);

    private VariableStore? _store;
    private Action?        _onChanged;

    public VariablesPanel() => InitializeComponent();

    public void Initialize(VariableStore store, Action onChanged)
    {
        _store     = store;
        _onChanged = onChanged;
        Refresh();
    }

    private void Refresh()
    {
        if (_store is null) return;
        var keep = (ItemsList.SelectedItem as VariableRow)?.Name;
        ItemsList.ItemsSource = _store.GetAll().Values
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VariableRow(v.Name, v.Value))
            .ToList();
        if (keep is not null)
            ItemsList.SelectedItem = ((IEnumerable<VariableRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Name == keep);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_store is null || ItemsList.SelectedItem is not VariableRow row) return;
        NameBox.Text    = row.Name;
        ValueBox.Text   = row.Value;
        StatusText.Text = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        var name  = NameBox.Text?.Trim() ?? string.Empty;
        var value = ValueBox.Text ?? string.Empty;

        if (string.IsNullOrEmpty(name)) { StatusText.Text = "Name is required."; return; }

        _store.Set(name, value);
        _onChanged?.Invoke();
        Refresh();
        StatusText.Text = $"Saved '{name}'.";
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;
        if (ItemsList.SelectedItem is not VariableRow row)
        {
            StatusText.Text = "Select a variable to delete.";
            return;
        }
        _store.Remove(row.Name);
        _onChanged?.Invoke();
        ClearForm();
        Refresh();
        StatusText.Text = $"Deleted '{row.Name}'.";
    }

    private void OnAdd  (object? sender, RoutedEventArgs e) => ClearForm();
    private void OnClear(object? sender, RoutedEventArgs e) => ClearForm();

    private void OnSelectAll(object? sender, RoutedEventArgs e) => ItemsList.SelectAll();

    /// <summary>
    /// Copy every selected row (not just the focused one — #97) to the clipboard
    /// as tab-separated <c>Name\tValue</c> lines, in display order.
    /// </summary>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var rows = ItemsList.SelectedItems?.Cast<VariableRow>().ToList() ?? new List<VariableRow>();
        if (rows.Count == 0 && ItemsList.SelectedItem is VariableRow one) rows.Add(one);
        if (rows.Count == 0) return;

        var text = string.Join(
            Environment.NewLine,
            rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => $"{r.Name}\t{r.Value}"));

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private void ClearForm()
    {
        ItemsList.SelectedItem = null;
        NameBox.Text           = string.Empty;
        ValueBox.Text          = string.Empty;
        StatusText.Text        = string.Empty;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_store is null) return;

        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Variables",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Variable files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportVariables(path, _store, ImportMode.Merge);
        _onChanged?.Invoke();
        Refresh();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} variable(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} variable(s).";
    }
}
