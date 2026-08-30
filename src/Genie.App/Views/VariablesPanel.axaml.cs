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
    private string         _filter = string.Empty;

    /// <summary>Name of the variable currently loaded in the editor form. When
    /// a Refresh restores the selection to this same variable (a Find…
    /// keystroke), OnSelectionChanged skips the form rewrite so unsaved edits
    /// survive. Null when composing a new entry.</summary>
    private string?        _loadedName;

    public VariablesPanel() => InitializeComponent();

    public void Initialize(VariableStore store, Action onChanged)
    {
        _store     = store;
        _onChanged = onChanged;
        // A re-Initialize (profile switch) must not carry the previous
        // profile's filter or form over — a stale filter renders the new
        // profile's list empty for no visible reason.
        ClearForm();
        ResetFilter();
        Refresh();
    }

    private void Refresh()
    {
        if (_store is null) return;
        var keep = ItemsList.SelectedItems?.Cast<VariableRow>()
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        ItemsList.ItemsSource = _store.GetAll().Values
            .Select(v => new VariableRow(v.Name, v.Value))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Name, r.Value))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Restore the whole multi-selection, not just the focused row — this
        // grid is Extended-mode with a multi-row Copy (#97), and Refresh now
        // also runs on every Find keystroke.
        if (ItemsList.SelectedItems is { } selection)
            foreach (var row in (IEnumerable<VariableRow>)ItemsList.ItemsSource)
                if (keep.Contains(row.Name))
                    selection.Add(row);
        // The filter hid every selected variable — clear the editor pane so
        // it can't keep showing (and saving / deleting) an invisible one.
        if (keep.Count > 0 && (ItemsList.SelectedItems?.Count ?? 0) == 0)
            ClearForm();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_store is null || ItemsList.SelectedItem is not VariableRow row) return;
        if (row.Name == _loadedName) return;   // restored selection — keep unsaved edits
        _loadedName     = row.Name;
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

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text ?? string.Empty;
        Refresh();
    }

    /// <summary>Drop any active Find… filter (profile switch / import) so the
    /// list renders in full and status counts match what's visible.</summary>
    private void ResetFilter()
    {
        _filter        = string.Empty;
        FilterBox.Text = string.Empty;
    }

    private void OnSelectAll(object? sender, RoutedEventArgs e)
    {
        ItemsList.SelectAll();
        // Select All only reaches the filtered rows — say so, or the #97 Copy
        // silently exports a subset the user thinks is the whole list.
        var visible = (ItemsList.ItemsSource as IEnumerable<VariableRow>)?.Count() ?? 0;
        var total   = _store?.GetAll().Count ?? visible;
        StatusText.Text = visible < total
            ? $"Selected {visible} of {total} variables — the Find… filter hides the rest."
            : string.Empty;
    }

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
        _loadedName            = null;
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
        // Show the full post-import list — a still-active filter makes the
        // status count look like a failed import; the merge may also have
        // rewritten the variable loaded in the form, so drop that too.
        ClearForm();
        ResetFilter();
        Refresh();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} variable(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} variable(s).";
    }
}
