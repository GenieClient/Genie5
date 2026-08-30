using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.Core.Highlights;
using Genie.Core.Import;
using Genie.Core.Persistence;

namespace Genie.App.Views;

/// <summary>
/// Highlights editor panel — code-behind + named controls, no MVVM.
/// Ported from dylb0t/Genie5's <c>HighlightStringsPanel</c>. The DataGrid uses
/// runtime bindings against an anonymous row record; the editor reads / writes
/// directly to the engine through named controls. This pattern sidesteps every
/// Avalonia binding pitfall we hit with the previous MVVM-based editor.
/// </summary>
public partial class HighlightStringsPanel : UserControl
{
    public sealed record HighlightRow(
        string EnabledGlyph, string Scope, string MatchType, string ForegroundColor, string BackgroundColor,
        string Pattern, string ClassName, string Windows)
    {
        /// <summary>Brush parsed from <see cref="ForegroundColor"/> so the
        /// Pattern cell can render in the actual highlight colour.</summary>
        public IBrush PatternForeground => ColorPickerHelpers.ParseBrush(ForegroundColor) ?? Brushes.LightGray;

        /// <summary>Brush parsed from <see cref="BackgroundColor"/> so the
        /// Pattern cell can render against the actual highlight background.</summary>
        public IBrush PatternBackground => ColorPickerHelpers.ParseBrush(BackgroundColor) ?? Brushes.Transparent;
    }

    private static readonly string[] MatchTypes =
        ["String", "Line", "BeginsWith", "Regex"];

    private HighlightEngine?     _engine;
    private Action?              _onRulesChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    /// <summary>Pattern of the rule currently loaded in the editor, captured at
    /// selection time. Null when composing a brand-new entry. Save keys the
    /// update on THIS (not the live textbox text) so editing the pattern
    /// rewrites the selected rule in place instead of leaving the old one
    /// behind and adding a duplicate (#142).</summary>
    private string?          _editingPattern;

    public HighlightStringsPanel()
    {
        InitializeComponent();
        MatchTypeBox.ItemsSource   = MatchTypes;
        MatchTypeBox.SelectedIndex = 0;
        FgColorPicker.Color        = Colors.Yellow;
        BgNoneCheck.IsChecked      = true;
        ScopeBox.ItemsSource       = ScopeEditing.Labels;
        ScopeBox.SelectedIndex     = 0;   // new rules default to This character (#257)
    }

    /// <summary>
    /// Wire the panel up to a live engine. <paramref name="onRulesChanged"/> is
    /// fired after every mutation (Save / Delete / Toggle / Import) so the host
    /// can repaint already-rendered text against the new rule set.
    /// <paramref name="scopeContext"/> enables the #257 scope UI; null / single
    /// layer hides it.
    /// </summary>
    public void Initialize(HighlightEngine engine, Action? onRulesChanged = null,
                           ScopeEditingContext? scopeContext = null)
    {
        _engine         = engine;
        _onRulesChanged = onRulesChanged;
        _scopeCtx       = scopeContext;
        var twoLayers   = scopeContext?.TwoLayers == true;
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
        var keep = (ItemsList.SelectedItem as HighlightRow)?.Pattern;
        ItemsList.ItemsSource = _engine.Rules
            .Select(r => new HighlightRow(
                r.IsEnabled ? "✓" : "✗",
                ScopeEditing.RowLabel(r.Scope),
                r.MatchType.ToString(),
                r.ForegroundColor,
                r.BackgroundColor,
                r.Pattern,
                r.ClassName,
                FormatWindows(r.Windows)))
            .Where(r => PanelFilterHelpers.Matches(
                _filter, r.Pattern, r.ForegroundColor, r.BackgroundColor, r.MatchType, r.ClassName))
            .ToList();
        if (keep is not null)
        {
            var restored = ((IEnumerable<HighlightRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Pattern == keep);
            if (restored is not null) ItemsList.SelectedItem = restored;
            // The filter hid the selected rule — clear the editor pane so it
            // can't keep showing (and saving / deleting) an invisible rule.
            else ClearForm();
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || ItemsList.SelectedItem is not HighlightRow row) return;
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;
        // Selection restored to the rule already loaded (a Find… keystroke's
        // Refresh, a toggle) — keep the form as-is so unsaved edits survive.
        if (rule.Pattern == _editingPattern) return;
        _editingPattern              = rule.Pattern;
        PatternBox.Text              = rule.Pattern;
        ColorPickerHelpers.LoadColor(FgColorPicker, FgDefaultCheck, rule.ForegroundColor, "Default");
        ColorPickerHelpers.LoadColor(BgColorPicker, BgNoneCheck,    rule.BackgroundColor, "");
        MatchTypeBox.SelectedItem    = rule.MatchType.ToString();
        ClassBox.Text                = rule.ClassName;
        WindowsBox.Text              = rule.Windows.Count == 0
            ? string.Empty
            : string.Join(", ", rule.Windows.OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
        CaseSensitiveCheck.IsChecked = rule.CaseSensitive;
        EnabledCheck.IsChecked       = rule.IsEnabled;
        ScopeBox.SelectedIndex       = ScopeEditing.ToIndex(rule.Scope);
        StatusText.Text              = string.Empty;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var pattern       = PatternBox.Text?.Trim() ?? string.Empty;
        var color         = ColorPickerHelpers.ReadColor(FgColorPicker, FgDefaultCheck, "Default");
        var bgColor       = ColorPickerHelpers.ReadColor(BgColorPicker, BgNoneCheck,    "");
        var matchTypeStr  = MatchTypeBox.SelectedItem as string ?? "String";
        var matchType     = Enum.TryParse<HighlightMatchType>(matchTypeStr, out var mt) ? mt : HighlightMatchType.String;
        var className     = ClassBox.Text?.Trim() ?? string.Empty;
        var windows       = ParseWindows(WindowsBox.Text);
        var caseSensitive = CaseSensitiveCheck.IsChecked == true;
        var enabled       = EnabledCheck.IsChecked == true;

        if (string.IsNullOrEmpty(pattern)) { StatusText.Text = "Pattern is required."; return; }

        if (matchType == HighlightMatchType.Regex)
        {
            try { _ = new Regex(pattern); }
            catch (RegexParseException ex) { StatusText.Text = $"Invalid regex: {ex.Message}"; return; }
        }

        // Key the update on the ORIGINALLY-selected pattern, not the textbox
        // text, so a pattern edit rewrites that rule in place (#142). For a
        // brand-new entry (_editingPattern null) the key IS the new pattern.
        var editKey  = string.IsNullOrEmpty(_editingPattern) ? pattern : _editingPattern;

        // Carry the CLI-managed fields (per-rule sound + speak) through the
        // edit — the form doesn't surface them, and dropping them here would
        // silently strip a #highlight-added sound/speak on every dialog save.
        var existing = _engine.Rules.FirstOrDefault(r => r.Pattern == editKey);

        // Remove the rule being edited, plus any rule that already uses the new
        // pattern (dedup — a rename must not leave two rules sharing a pattern).
        // Renaming a Global rule must also drop the OLD key from the shared
        // file explicitly, or the shadowed-twin save merge resurrects it (#257).
        if (existing?.Scope == RuleScope.Global &&
            !string.Equals(editKey, pattern, StringComparison.Ordinal))
            _scopeCtx?.NoteGlobalDelete?.Invoke(editKey);
        _engine.RemoveRule(editKey);
        if (!string.Equals(editKey, pattern, StringComparison.Ordinal))
            _engine.RemoveRule(pattern);

        var added = _engine.AddRule(pattern, color, bgColor, matchType, caseSensitive, enabled, className,
                        existing?.SoundFile ?? "", existing?.Speak ?? "", windows);
        added.Scope = _scopeCtx?.TwoLayers == true
            ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
            : existing?.Scope ?? RuleScope.Character;
        _editingPattern = pattern;   // keep the editor pointed at the saved rule
        Refresh();
        // Re-select the saved row so the editor stays consistent after a rename
        // (Refresh keys selection off the old row, which no longer exists).
        ItemsList.SelectedItem = (ItemsList.ItemsSource as IEnumerable<HighlightRow>)?
            .FirstOrDefault(r => r.Pattern == pattern);
        _onRulesChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = $"Saved “{pattern}” → {color}.";
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not HighlightRow row) { StatusText.Text = "Select a highlight to delete."; return; }
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;

        // Deleting a shared (Global) rule affects every character — prompt for
        // a reversible local opt-out vs a real remove-for-all (#257).
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
        _onRulesChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = "Deleted.";
    }

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        if (ItemsList.SelectedItem is not HighlightRow row) { StatusText.Text = "Select a highlight to toggle."; return; }
        var rule = _engine.Rules.FirstOrDefault(r => r.Pattern == row.Pattern);
        if (rule is null) return;

        // Toggling OFF a shared rule silently writes the reversible local
        // opt-out — the shared rule keeps running for everyone else (#257).
        if (rule.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true && rule.IsEnabled)
        {
            LocalOptOut(rule);
            StatusText.Text = "Disabled for this character (still active for everyone else).";
            return;
        }

        rule.IsEnabled = !rule.IsEnabled;
        Refresh();
        // The restored selection skips the form rewrite (unsaved-edit guard),
        // so sync the checkbox to the new state explicitly.
        EnabledCheck.IsChecked = rule.IsEnabled;
        _onRulesChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = $"Highlight {(rule.IsEnabled ? "enabled" : "disabled")}.";
    }

    /// <summary>Replace a shared rule's live presence with a disabled
    /// this-character copy: the copy shadows the global twin (which stays in
    /// the shared file untouched, via the save merge) — delete the copy later
    /// and the shared rule shows through again at the next connect/reload.</summary>
    private void LocalOptOut(HighlightRule rule)
    {
        if (_engine is null) return;
        _engine.RemoveRule(rule.Pattern);
        _engine.AddRule(rule.Pattern, rule.ForegroundColor, rule.BackgroundColor, rule.MatchType,
                        rule.CaseSensitive, isEnabled: false, rule.ClassName,
                        rule.SoundFile, rule.Speak, rule.Windows).Scope = RuleScope.Character;
        ClearForm();
        Refresh();
        _onRulesChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
    }

    private void OnAdd(object? sender, RoutedEventArgs e) => ClearForm();

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

        // Avalonia 11's modern file-picker API. The dylb0t source used the
        // deprecated OpenFileDialog; this is the supported replacement.
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Highlights",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Highlight files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportHighlights(path, _engine, ImportMode.Merge);
        // Show the full post-import list — a still-active filter makes the
        // status count look like a failed import; the merge may also have
        // rewritten the rule loaded in the form, so drop that too.
        ClearForm();
        ResetFilter();
        Refresh();
        _onRulesChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} highlight(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} highlight(s).";
    }

    private void ClearForm()
    {
        _editingPattern              = null;
        ItemsList.SelectedItem       = null;
        PatternBox.Text              = string.Empty;
        FgColorPicker.Color          = Colors.Yellow;
        FgDefaultCheck.IsChecked     = false;
        BgNoneCheck.IsChecked        = true;
        MatchTypeBox.SelectedIndex   = 0;
        ClassBox.Text                = string.Empty;
        WindowsBox.Text              = string.Empty;
        CaseSensitiveCheck.IsChecked = false;
        EnabledCheck.IsChecked       = true;
        ScopeBox.SelectedIndex       = 0;   // new rules default to This character
        StatusText.Text              = string.Empty;
    }

    /// <summary>Editor text ("main, room") → the window-id set for a rule.
    /// Blank / whitespace = empty set = "every window".</summary>
    private static IEnumerable<string> ParseWindows(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant());

    /// <summary>A rule's window set → editor/column text. Empty set (every
    /// window) shows as "All" in the list and blank in the editor.</summary>
    private static string FormatWindows(IReadOnlySet<string> windows) =>
        windows.Count == 0 ? "All" : string.Join(", ", windows.OrderBy(w => w, StringComparer.OrdinalIgnoreCase));
}
