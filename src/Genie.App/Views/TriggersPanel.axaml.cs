using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.Core.Import;
using Genie.Core.Persistence;
using Genie.Core.Triggers;

namespace Genie.App.Views;

/// <summary>
/// Trigger editor — code-behind + named controls (dylb0t pattern).
/// A trigger matches a regex against incoming game text and fires its action
/// (typically a command or script invocation) when the pattern matches.
/// </summary>
public partial class TriggersPanel : UserControl
{
    public sealed record TriggerRow(string EnabledGlyph, string Scope, string Pattern, string Action, string ClassName);

    private TriggerEngineFinal?  _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    /// <summary>Pattern of the rule currently loaded in the editor form. When a
    /// Refresh restores the selection to this same rule (a Find… keystroke,
    /// a toggle), OnSelectionChanged skips the form rewrite so unsaved edits
    /// survive. Null when composing a new entry.</summary>
    private string?              _loadedPattern;

    public TriggersPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new rules default to This character (#257)
    }

    public void Initialize(TriggerEngineFinal engine, Action? onChanged = null,
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
        var keep = (ItemsList.SelectedItem as TriggerRow)?.Pattern;
        ItemsList.ItemsSource = _engine.Triggers
            .Select(t => new TriggerRow(t.IsEnabled ? "✓" : "✗", ScopeEditing.RowLabel(t.Scope), t.Pattern, t.Action, t.ClassName))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Pattern, r.Action, r.ClassName))
            .ToList();
        if (keep is not null)
        {
            var restored = ((IEnumerable<TriggerRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Pattern == keep);
            if (restored is not null) ItemsList.SelectedItem = restored;
            // The filter hid the selected rule — clear the editor pane so it
            // can't keep showing (and saving / deleting) an invisible rule.
            else ClearForm();
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || ItemsList.SelectedItem is not TriggerRow row) return;
        var trigger = _engine.Triggers.FirstOrDefault(t => t.Pattern == row.Pattern);
        if (trigger is null) return;
        if (trigger.Pattern == _loadedPattern) return;   // restored selection — keep unsaved edits
        _loadedPattern               = trigger.Pattern;
        PatternBox.Text              = trigger.Pattern;
        ActionBox.Text               = trigger.Action;
        ClassBox.Text                = trigger.ClassName;
        CaseSensitiveCheck.IsChecked = trigger.CaseSensitive;
        EnabledCheck.IsChecked       = trigger.IsEnabled;
        EvalCheck.IsChecked          = trigger.Eval;
        MatchAllCheck.IsChecked      = trigger.MatchAll;
        ScopeBox.SelectedIndex       = ScopeEditing.ToIndex(trigger.Scope);
        StatusText.Text              = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var pattern       = PatternBox.Text?.Trim() ?? string.Empty;
        var action        = ActionBox.Text?.Trim() ?? string.Empty;
        var className     = ClassBox.Text?.Trim() ?? string.Empty;
        var caseSensitive = CaseSensitiveCheck.IsChecked == true;
        var enabled       = EnabledCheck.IsChecked == true;
        var eval          = EvalCheck.IsChecked == true;
        var matchAll      = MatchAllCheck.IsChecked == true;

        if (string.IsNullOrEmpty(pattern)) { StatusText.Text = "Pattern is required."; return; }

        try { _ = new Regex(pattern); }
        catch (RegexParseException ex) { StatusText.Text = $"Invalid regex: {ex.Message}"; return; }

        // Carry the CLI-managed fields (per-rule sound + speak) through the
        // edit — the form doesn't surface them, and dropping them here would
        // silently strip a #trigger-added sound/speak on every dialog save.
        var existing = _engine.Triggers.FirstOrDefault(t => t.Pattern == pattern);
        _engine.RemoveTrigger(pattern);
        var added = _engine.AddTrigger(pattern, action, caseSensitive, enabled, className,
                           existing?.SoundFile ?? "", existing?.Speak ?? "", eval, matchAll);
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
        if (ItemsList.SelectedItem is not TriggerRow row) { StatusText.Text = "Select a trigger to delete."; return; }
        var rule = _engine.Triggers.FirstOrDefault(t => t.Pattern == row.Pattern);
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

        _engine.RemoveTrigger(row.Pattern);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = "Deleted.";
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not TriggerRow row) { StatusText.Text = "Select a trigger to toggle."; return; }
        var trigger = _engine.Triggers.FirstOrDefault(t => t.Pattern == row.Pattern);
        if (trigger is null) return;

        // Toggling OFF a shared rule writes the reversible local opt-out (#257).
        if (trigger.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true && trigger.IsEnabled)
        {
            LocalOptOut(trigger);
            StatusText.Text = "Disabled for this character (still active for everyone else).";
            return;
        }

        _engine.SetEnabled(trigger.Pattern, !trigger.IsEnabled);
        Refresh();
        // The restored selection skips the form rewrite (unsaved-edit guard),
        // so sync the checkbox to the new state explicitly.
        EnabledCheck.IsChecked = trigger.IsEnabled;
        _onChanged?.Invoke();
        StatusText.Text = $"Trigger {(trigger.IsEnabled ? "enabled" : "disabled")}.";
    }

    /// <summary>Shadow a shared trigger with a disabled this-character copy;
    /// the shared rule stays in the global file via the save merge (#257).</summary>
    private void LocalOptOut(TriggerRule rule)
    {
        if (_engine is null) return;
        _engine.RemoveTrigger(rule.Pattern);
        _engine.AddTrigger(rule.Pattern, rule.Action, rule.CaseSensitive, isEnabled: false,
                           rule.ClassName, rule.SoundFile, rule.Speak, rule.Eval,
                           rule.MatchAll).Scope = RuleScope.Character;
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

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Triggers",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Trigger files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportTriggers(path, _engine, ImportMode.Merge);
        // Show the full post-import list — a still-active filter makes the
        // status count look like a failed import; the merge may also have
        // rewritten the rule loaded in the form, so drop that too.
        ClearForm();
        ResetFilter();
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} trigger(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} trigger(s).";
    }

    private void ClearForm()
    {
        _loadedPattern               = null;
        ItemsList.SelectedItem       = null;
        PatternBox.Text              = string.Empty;
        ActionBox.Text               = string.Empty;
        ClassBox.Text                = string.Empty;
        CaseSensitiveCheck.IsChecked = false;
        EnabledCheck.IsChecked       = true;
        EvalCheck.IsChecked          = false;
        MatchAllCheck.IsChecked      = false;
        ScopeBox.SelectedIndex       = 0;   // new rules default to This character
        StatusText.Text              = string.Empty;
    }
}
