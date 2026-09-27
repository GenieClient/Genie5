using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Genie.Core.Persistence;
using Genie.Core.Shunts;

namespace Genie.App.Views;

/// <summary>
/// Shunts editor (public #248) — send matching main-window lines to a named
/// window, moving them out of Main or (with "Also keep in main window") copying
/// them. The same shape as the Gags panel plus a target and a mode; there is
/// no Genie 4 import because Genie 4 never had shunts.
/// </summary>
public partial class ShuntsPanel : UserControl
{
    public sealed record ShuntRow(string EnabledGlyph, string Scope, string Pattern,
                                  string Window, string Mode, string ClassName);

    private ShuntEngine?         _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    /// <summary>Pattern of the rule currently loaded in the editor form — see
    /// GagsPanel: a Refresh that restores the same selection must not wipe
    /// unsaved edits. Null when composing a new entry.</summary>
    private string?              _loadedPattern;

    public ShuntsPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new rules default to This character (#257)
    }

    public void Initialize(ShuntEngine engine, Action? onChanged = null,
                           ScopeEditingContext? scopeContext = null)
    {
        _engine    = engine;
        _onChanged = onChanged;
        _scopeCtx  = scopeContext;
        var twoLayers = scopeContext?.TwoLayers == true;
        ScopeGroup.IsVisible = twoLayers;
        ScopeEditing.SetColumnVisible(ItemsList, "Scope", twoLayers);
        ScopeEditing.InitFilter(ScopeFilterBox, twoLayers, Refresh);
        ClearForm();
        ResetFilter();
        Refresh();
    }

    private static string ModeLabel(ShuntRule r) => r.Copy ? "copy" : "move";

    private void Refresh()
    {
        if (_engine is null) return;
        var keep = (ItemsList.SelectedItem as ShuntRow)?.Pattern;
        ItemsList.ItemsSource = _engine.Rules
            .Where(r => ScopeEditing.PassesFilter(ScopeFilterBox.SelectedIndex, r.Scope))
            .Select(r => new ShuntRow(r.IsEnabled ? "✓" : "✗", ScopeEditing.RowLabel(r.Scope),
                                      r.Pattern, r.Window, ModeLabel(r), r.ClassName))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Pattern, r.Window, r.ClassName))
            .ToList();
        if (keep is not null)
        {
            var restored = ((IEnumerable<ShuntRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Pattern == keep);
            if (restored is not null) ItemsList.SelectedItem = restored;
            else ClearForm();   // the filter hid the selected rule
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || ItemsList.SelectedItem is not ShuntRow row) return;
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;
        if (rule.Pattern == _loadedPattern) return;   // restored selection — keep unsaved edits
        _loadedPattern               = rule.Pattern;
        PatternBox.Text              = rule.Pattern;
        WindowBox.Text               = rule.Window;
        ClassBox.Text                = rule.ClassName;
        CopyCheck.IsChecked          = rule.Copy;
        CaseSensitiveCheck.IsChecked = rule.CaseSensitive;
        EnabledCheck.IsChecked       = rule.IsEnabled;
        ScopeBox.SelectedIndex       = ScopeEditing.ToIndex(rule.Scope);
        StatusText.Text              = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var pattern       = PatternBox.Text?.Trim() ?? string.Empty;
        var window        = (WindowBox.Text?.Trim() ?? string.Empty).TrimStart('>');
        var className     = ClassBox.Text?.Trim() ?? string.Empty;
        var copy          = CopyCheck.IsChecked == true;
        var caseSensitive = CaseSensitiveCheck.IsChecked == true;
        var enabled       = EnabledCheck.IsChecked == true;

        if (string.IsNullOrEmpty(pattern)) { StatusText.Text = "Pattern is required."; return; }
        if (string.IsNullOrEmpty(window))  { StatusText.Text = "Window is required."; return; }
        if (window.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            window.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "That is the main window — name another window.";
            return;
        }

        try { _ = new Regex(pattern); }
        catch (RegexParseException ex) { StatusText.Text = $"Invalid regex: {ex.Message}"; return; }

        var existing = _engine.Rules.FirstOrDefault(r => r.Pattern == pattern);
        _engine.RemoveRule(pattern);
        var added = _engine.AddRule(pattern, window, copy, caseSensitive, enabled, className);
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
        if (ItemsList.SelectedItem is not ShuntRow row) { StatusText.Text = "Select a shunt to delete."; return; }
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
        // A deleted per-character override un-shadows its shared twin now (public #315).
        var restored = ScopeEditing.RestoreTwinAfterDelete(_scopeCtx, rule.Scope, row.Pattern);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = restored ? ScopeEditing.DeletedStatus(row.Pattern, true) : "Deleted.";
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not ShuntRow row) { StatusText.Text = "Select a shunt to toggle."; return; }
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
        EnabledCheck.IsChecked = rule.IsEnabled;
        _onChanged?.Invoke();
        StatusText.Text = $"Shunt {(rule.IsEnabled ? "enabled" : "disabled")}.";
    }

    /// <summary>Shadow a shared shunt with a disabled this-character copy; the
    /// shared rule stays in the global file via the save merge (#257).</summary>
    private void LocalOptOut(ShuntRule rule)
    {
        if (_engine is null) return;
        _engine.RemoveRule(rule.Pattern);
        _engine.AddRule(rule.Pattern, rule.Window, rule.Copy, rule.CaseSensitive, isEnabled: false,
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

    private void ResetFilter()
    {
        _filter        = string.Empty;
        FilterBox.Text = string.Empty;
    }

    private void ClearForm()
    {
        _loadedPattern               = null;
        ItemsList.SelectedItem       = null;
        PatternBox.Text              = string.Empty;
        WindowBox.Text               = string.Empty;
        ClassBox.Text                = string.Empty;
        CopyCheck.IsChecked          = false;
        CaseSensitiveCheck.IsChecked = false;
        EnabledCheck.IsChecked       = true;
        ScopeBox.SelectedIndex       = 0;   // new rules default to This character
        StatusText.Text              = string.Empty;
    }
}
