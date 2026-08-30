using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.Core.Aliases;
using Genie.Core.Import;
using Genie.Core.Persistence;

namespace Genie.App.Views;

/// <summary>
/// Alias editor — code-behind + named controls (dylb0t pattern).
/// One alias = one Name → Expansion mapping. Typing the Name as a command
/// expands to Expansion before being sent to the game.
/// </summary>
public partial class AliasesPanel : UserControl
{
    public sealed record AliasRow(string EnabledGlyph, string Scope, string Name, string Expansion, bool IsEnabled);

    private AliasEngine?         _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    /// <summary>Name of the rule currently loaded in the editor form. When a
    /// Refresh restores the selection to this same rule (a Find… keystroke,
    /// a toggle), OnSelectionChanged skips the form rewrite so unsaved edits
    /// survive. Null when composing a new entry.</summary>
    private string?              _loadedName;

    public AliasesPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new rules default to This character (#257)
    }

    public void Initialize(AliasEngine engine, Action? onChanged = null,
                           ScopeEditingContext? scopeContext = null)
    {
        _engine    = engine;
        _onChanged = onChanged;
        _scopeCtx  = scopeContext;
        var twoLayers = scopeContext?.TwoLayers == true;
        ScopeGroup.IsVisible = twoLayers;
        ScopeEditing.SetColumnVisible(ItemsList, "Scope", twoLayers);
        // A re-Initialize (profile switch) must not carry the previous
        // profile's filter or form over — a stale filter renders the new
        // profile's list empty for no visible reason.
        ClearForm();
        ResetFilter();
        Refresh();
    }

    private void Refresh()
    {
        if (_engine is null) return;
        var keep = (ItemsList.SelectedItem as AliasRow)?.Name;
        ItemsList.ItemsSource = _engine.Aliases
            .Select(a => new AliasRow(a.IsEnabled ? "✓" : "✗", ScopeEditing.RowLabel(a.Scope), a.Name, a.Expansion, a.IsEnabled))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Name, r.Expansion))
            .ToList();
        if (keep is not null)
        {
            var restored = ((IEnumerable<AliasRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Name == keep);
            if (restored is not null) ItemsList.SelectedItem = restored;
            // The filter hid the selected rule — clear the editor pane so it
            // can't keep showing (and saving / deleting) an invisible rule.
            else ClearForm();
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || ItemsList.SelectedItem is not AliasRow row) return;
        var alias = _engine.Aliases.FirstOrDefault(a => a.Name == row.Name);
        if (alias is null) return;
        if (alias.Name == _loadedName) return;   // restored selection — keep unsaved edits
        _loadedName            = alias.Name;
        NameBox.Text           = alias.Name;
        ExpansionBox.Text      = alias.Expansion;
        EnabledCheck.IsChecked = alias.IsEnabled;
        ScopeBox.SelectedIndex = ScopeEditing.ToIndex(alias.Scope);
        StatusText.Text        = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var name      = NameBox.Text?.Trim() ?? string.Empty;
        var expansion = ExpansionBox.Text?.Trim() ?? string.Empty;
        var enabled   = EnabledCheck.IsChecked == true;

        if (string.IsNullOrEmpty(name)) { StatusText.Text = "Name is required."; return; }

        var existing = _engine.Aliases.FirstOrDefault(a => a.Name == name);
        _engine.RemoveAlias(name);
        var added = _engine.AddAlias(name, expansion, enabled);
        added.Scope = _scopeCtx?.TwoLayers == true
            ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
            : existing?.Scope ?? RuleScope.Character;
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Saved '{name}'.";
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not AliasRow row) { StatusText.Text = "Select an alias to delete."; return; }
        var alias = _engine.Aliases.FirstOrDefault(a => a.Name == row.Name);
        if (alias is null) return;

        // Deleting a shared (Global) alias affects every character (#257).
        if (alias.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true)
        {
            if (this.GetVisualRoot() is not Window owner) return;
            var choice = await ScopeDeleteDialog.Show(owner, alias.Name, allowOptOut: true);
            if (choice == ScopeDeleteChoice.Cancel) return;
            if (choice == ScopeDeleteChoice.LocalOptOut)
            {
                LocalOptOut(alias);
                StatusText.Text = "Disabled for this character (still active for everyone else).";
                return;
            }
            _scopeCtx.NoteGlobalDelete?.Invoke(alias.Name);
        }

        _engine.RemoveAlias(row.Name);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Deleted '{row.Name}'.";
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not AliasRow row) { StatusText.Text = "Select an alias to toggle."; return; }
        var alias = _engine.Aliases.FirstOrDefault(a => a.Name == row.Name);
        if (alias is null) return;

        // Toggling OFF a shared alias writes the reversible local opt-out (#257).
        if (alias.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true && alias.IsEnabled)
        {
            LocalOptOut(alias);
            StatusText.Text = "Disabled for this character (still active for everyone else).";
            return;
        }

        _engine.SetEnabled(alias.Name, !alias.IsEnabled);
        Refresh();
        // The restored selection skips the form rewrite (unsaved-edit guard),
        // so sync the checkbox to the new state explicitly.
        EnabledCheck.IsChecked = alias.IsEnabled;
        _onChanged?.Invoke();
        StatusText.Text = $"'{alias.Name}' {(alias.IsEnabled ? "enabled" : "disabled")}.";
    }

    /// <summary>Shadow a shared alias with a disabled this-character copy; the
    /// shared alias stays in the global file via the save merge (#257).</summary>
    private void LocalOptOut(AliasRule alias)
    {
        if (_engine is null) return;
        _engine.RemoveAlias(alias.Name);
        _engine.AddAlias(alias.Name, alias.Expansion, isEnabled: false,
                         alias.ClassName).Scope = RuleScope.Character;
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
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

    private void ClearForm()
    {
        _loadedName            = null;
        ItemsList.SelectedItem = null;
        NameBox.Text           = string.Empty;
        ExpansionBox.Text      = string.Empty;
        EnabledCheck.IsChecked = true;
        ScopeBox.SelectedIndex = 0;   // new rules default to This character
        StatusText.Text        = string.Empty;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Aliases",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Alias files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportAliases(path, _engine, ImportMode.Merge);
        // Show the full post-import list — a still-active filter makes the
        // status count look like a failed import; the merge may also have
        // rewritten the rule loaded in the form, so drop that too.
        ClearForm();
        ResetFilter();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Imported {result.Imported} alias(es).";
    }
}
