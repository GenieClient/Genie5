using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.Core.Import;
using Genie.Core.Persistence;
using Genie.Core.Macros;

namespace Genie.App.Views;

/// <summary>
/// Macros editor — keyboard-shortcut → action mapping. Key strings use the
/// Genie 4 vocabulary emitted by <see cref="MacroKeyConverter"/>
/// (e.g. <c>f1</c>, <c>ctrl+f2</c>, <c>alt+num5</c>).
/// </summary>
public partial class MacrosPanel : UserControl
{
    public sealed record MacroRow(string Scope, string Key, string Action);

    private MacroEngine?         _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    public MacrosPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new macros default to This character (#257)
    }

    public void Initialize(MacroEngine engine, Action? onChanged = null,
                           ScopeEditingContext? scopeContext = null)
    {
        _engine    = engine;
        _onChanged = onChanged;
        _scopeCtx  = scopeContext;
        var twoLayers = scopeContext?.TwoLayers == true;
        ScopeGroup.IsVisible = twoLayers;
        ScopeEditing.SetColumnVisible(ItemsList, "Scope", twoLayers);
        Refresh();
    }

    private void Refresh()
    {
        if (_engine is null) return;
        var keep = (ItemsList.SelectedItem as MacroRow)?.Key;
        ItemsList.ItemsSource = _engine.Rules
            .Select(r => new MacroRow(ScopeEditing.RowLabel(r.Scope), r.Key, r.Action))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Key, r.Action))
            .ToList();
        if (keep is not null)
            ItemsList.SelectedItem = ((IEnumerable<MacroRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Key == keep);
    }

    private MacroRule? SelectedRule()
    {
        if (_engine is null || ItemsList.SelectedItem is not MacroRow row) return null;
        return _engine.Rules.FirstOrDefault(r => r.Key == row.Key);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var rule = SelectedRule();
        if (rule is null) return;
        KeyBox.Text            = rule.Key;
        ActionBox.Text         = rule.Action;
        ScopeBox.SelectedIndex = ScopeEditing.ToIndex(rule.Scope);
        StatusText.Text        = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var key    = KeyBox.Text?.Trim() ?? string.Empty;
        var action = ActionBox.Text ?? string.Empty;
        if (string.IsNullOrEmpty(key)) { StatusText.Text = "Key is required."; return; }

        var existing = _engine.Rules.FirstOrDefault(
            r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        _engine.Add(key, action);
        var added = _engine.Rules.FirstOrDefault(
            r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (added is not null)
            added.Scope = _scopeCtx?.TwoLayers == true
                ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
                : existing?.Scope ?? RuleScope.Character;
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Saved '{key}'.";
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var rule = SelectedRule();
        if (rule is null) { StatusText.Text = "Select a macro to delete."; return; }

        // Deleting a shared (Global) macro affects every character; macros
        // have no enabled flag, so there is no local opt-out — confirm the
        // for-all removal (#257).
        if (rule.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true)
        {
            if (this.GetVisualRoot() is not Window owner) return;
            var choice = await ScopeDeleteDialog.Show(owner, rule.Key, allowOptOut: false);
            if (choice != ScopeDeleteChoice.RemoveForAll) return;
            _scopeCtx.NoteGlobalDelete?.Invoke(rule.Key);
        }

        _engine.Remove(rule.Key);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Deleted '{rule.Key}'.";
    }

    private void OnAdd  (object? sender, RoutedEventArgs e) => ClearForm();
    private void OnClear(object? sender, RoutedEventArgs e) => ClearForm();

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text ?? string.Empty;
        Refresh();
    }

    /// <summary>
    /// Capture the pressed key combo into <see cref="KeyBox"/>. The field
    /// is marked read-only on the XAML side so users can't type — focusing
    /// it and pressing the desired combo is the canonical entry path. The
    /// event is always marked handled so the window-level macro firer does
    /// NOT execute whatever macro is currently bound to that key.
    /// </summary>
    private void OnKeyBoxKeyDown(object? sender, KeyEventArgs e)
    {
        var key = MacroKeyConverter.ToMacroKey(e.Key, e.KeyModifiers);
        if (key is not null)
        {
            KeyBox.Text = key;
            StatusText.Text = $"Captured: {key}";
        }
        // Even when ToMacroKey returns null (modifier-only press), we still
        // mark handled so a stray F-key never fires the existing macro while
        // the user is editing.
        e.Handled = true;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Macros",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Macro files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportMacros(path, _engine, ImportMode.Merge);
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} macro(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} macro(s).";
    }

    private void ClearForm()
    {
        ItemsList.SelectedItem = null;
        KeyBox.Text            = string.Empty;
        ActionBox.Text         = string.Empty;
        ScopeBox.SelectedIndex = 0;   // new macros default to This character
        StatusText.Text        = string.Empty;
    }
}
