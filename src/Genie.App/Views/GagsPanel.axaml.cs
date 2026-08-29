using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.Core.Gags;
using Genie.Core.Import;
using Genie.Core.Persistence;

namespace Genie.App.Views;

/// <summary>
/// Gags editor — silence matching lines so they never render to the display.
/// Pure suppression: any line whose pattern matches an enabled gag is dropped.
/// </summary>
public partial class GagsPanel : UserControl
{
    public sealed record GagRow(string EnabledGlyph, string Scope, string Pattern, string ClassName);

    private GagEngine?           _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    public GagsPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new rules default to This character (#257)
    }

    public void Initialize(GagEngine engine, Action? onChanged = null,
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
        var keep = (ItemsList.SelectedItem as GagRow)?.Pattern;
        ItemsList.ItemsSource = _engine.Rules
            .Select(r => new GagRow(r.IsEnabled ? "✓" : "✗", ScopeEditing.RowLabel(r.Scope), r.Pattern, r.ClassName))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Pattern, r.ClassName))
            .ToList();
        if (keep is not null)
            ItemsList.SelectedItem = ((IEnumerable<GagRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Pattern == keep);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || ItemsList.SelectedItem is not GagRow row) return;
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;
        PatternBox.Text              = rule.Pattern;
        ClassBox.Text                = rule.ClassName;
        CaseSensitiveCheck.IsChecked = rule.CaseSensitive;
        EnabledCheck.IsChecked       = rule.IsEnabled;
        ScopeBox.SelectedIndex       = ScopeEditing.ToIndex(rule.Scope);
        StatusText.Text              = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var pattern       = PatternBox.Text?.Trim() ?? string.Empty;
        var className     = ClassBox.Text?.Trim() ?? string.Empty;
        var caseSensitive = CaseSensitiveCheck.IsChecked == true;
        var enabled       = EnabledCheck.IsChecked == true;

        if (string.IsNullOrEmpty(pattern)) { StatusText.Text = "Pattern is required."; return; }

        try { _ = new Regex(pattern); }
        catch (RegexParseException ex) { StatusText.Text = $"Invalid regex: {ex.Message}"; return; }

        var existing = _engine.Rules.FirstOrDefault(r => r.Pattern == pattern);
        _engine.RemoveRule(pattern);
        var added = _engine.AddRule(pattern, caseSensitive, enabled, className);
        added.Scope = _scopeCtx?.TwoLayers == true
            ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
            : existing?.Scope ?? RuleScope.Character;
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = "Saved.";
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not GagRow row) { StatusText.Text = "Select a gag to delete."; return; }
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;

        // Deleting a shared (Global) rule affects every character (#257).
        if (rule.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true)
        {
            if (this.GetVisualRoot() is not Window owner) return;
            var choice = await ScopeDeleteDialog.Show(owner, rule.Pattern, allowOptOut: true);
            if (choice == ScopeDeleteChoice.Cancel) return;
            if (choice == ScopeDeleteChoice.LocalOptOut)
            {
                LocalOptOut(rule);
                StatusText.Text = "Disabled for this character (still active for everyone else).";
                return;
            }
            _scopeCtx.NoteGlobalDelete?.Invoke(rule.Pattern);
        }

        _engine.RemoveRule(row.Pattern);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = "Deleted.";
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not GagRow row) { StatusText.Text = "Select a gag to toggle."; return; }
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;

        // Toggling OFF a shared rule writes the reversible local opt-out (#257).
        if (rule.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true && rule.IsEnabled)
        {
            LocalOptOut(rule);
            StatusText.Text = "Disabled for this character (still active for everyone else).";
            return;
        }

        rule.IsEnabled = !rule.IsEnabled;
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = $"Gag {(rule.IsEnabled ? "enabled" : "disabled")}.";
    }

    /// <summary>Shadow a shared gag with a disabled this-character copy; the
    /// shared rule stays in the global file via the save merge (#257).</summary>
    private void LocalOptOut(GagRule rule)
    {
        if (_engine is null) return;
        _engine.RemoveRule(rule.Pattern);
        _engine.AddRule(rule.Pattern, rule.CaseSensitive, isEnabled: false,
                        rule.ClassName).Scope = RuleScope.Character;
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

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Gags",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Gag files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportGags(path, _engine, ImportMode.Merge);
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} gag(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} gag(s).";
    }

    private void ClearForm()
    {
        ItemsList.SelectedItem       = null;
        PatternBox.Text              = string.Empty;
        ClassBox.Text                = string.Empty;
        CaseSensitiveCheck.IsChecked = false;
        EnabledCheck.IsChecked       = true;
        ScopeBox.SelectedIndex       = 0;   // new rules default to This character
        StatusText.Text              = string.Empty;
    }
}
