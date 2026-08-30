using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.Core.Highlights;
using Genie.Core.Import;
using Genie.Core.Persistence;

namespace Genie.App.Views;

/// <summary>
/// Player-name highlights editor. Code-behind + named controls (dylb0t pattern).
/// Each rule colours a specific bareword name wherever it appears in the
/// game text, talk streams, etc.
/// </summary>
public partial class NamesPanel : UserControl
{
    public sealed record NameRow(string Scope, string Name, string ForegroundColor, string BackgroundColor)
    {
        public IBrush NameForeground => ColorPickerHelpers.ParseBrush(ForegroundColor) ?? Brushes.LightGray;
        public IBrush NameBackground => ColorPickerHelpers.ParseBrush(BackgroundColor) ?? Brushes.Transparent;
    }

    private NameHighlightEngine? _engine;
    private Action?              _onChanged;
    private ScopeEditingContext? _scopeCtx;
    private string               _filter = string.Empty;

    public NamesPanel()
    {
        InitializeComponent();
        FgColorPicker.Color    = Colors.Yellow;
        BgNoneCheck.IsChecked  = true;
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 0;   // new names default to This character (#257)
    }

    public void Initialize(NameHighlightEngine engine, Action? onChanged = null,
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
        var keep = (ItemsList.SelectedItem as NameRow)?.Name;
        ItemsList.ItemsSource = _engine.Rules
            .Select(r => new NameRow(ScopeEditing.RowLabel(r.Scope), r.Name, r.ForegroundColor, r.BackgroundColor))
            .Where(r => PanelFilterHelpers.Matches(_filter, r.Name))
            .ToList();
        if (keep is not null)
            ItemsList.SelectedItem = ((IEnumerable<NameRow>)ItemsList.ItemsSource)
                .FirstOrDefault(r => r.Name == keep);
    }

    private NameRule? SelectedRule()
    {
        if (_engine is null || ItemsList.SelectedItem is not NameRow row) return null;
        return _engine.Rules.FirstOrDefault(r => r.Name == row.Name);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var rule = SelectedRule();
        if (rule is null) return;
        NameBox.Text = rule.Name;
        ColorPickerHelpers.LoadColor(FgColorPicker, FgDefaultCheck, rule.ForegroundColor, "Default");
        ColorPickerHelpers.LoadColor(BgColorPicker, BgNoneCheck,    rule.BackgroundColor, "");
        ScopeBox.SelectedIndex = ScopeEditing.ToIndex(rule.Scope);
        StatusText.Text = string.Empty;
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        Refresh();
        StatusText.Text = "Refreshed.";
    }

    private void OnAdd(object? sender, RoutedEventArgs e) => ClearForm();

    private void OnSaveRule(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var name = NameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name)) { StatusText.Text = "Name is required."; return; }

        var fg = ColorPickerHelpers.ReadColor(FgColorPicker, FgDefaultCheck, "Default");
        var bg = ColorPickerHelpers.ReadColor(BgColorPicker, BgNoneCheck,    "");

        var existing = _engine.Rules.FirstOrDefault(
            r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _engine.Add(name, fg, bg);
        var added = _engine.Rules.FirstOrDefault(
            r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (added is not null)
            added.Scope = _scopeCtx?.TwoLayers == true
                ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
                : existing?.Scope ?? RuleScope.Character;
        Refresh();
        _onChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = $"Saved '{name}'.";
    }

    private async void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;
        var rule = SelectedRule();
        if (rule is null) { StatusText.Text = "Select a name to remove."; return; }

        // Removing a shared (Global) name affects every character; names have
        // no enabled flag, so confirm the for-all removal (#257).
        if (rule.Scope == RuleScope.Global && _scopeCtx?.TwoLayers == true)
        {
            if (this.GetVisualRoot() is not Window owner) return;
            var choice = await ScopeDeleteDialog.Show(owner, rule.Name, allowOptOut: false);
            if (choice != ScopeDeleteChoice.RemoveForAll) return;
            _scopeCtx.NoteGlobalDelete?.Invoke(rule.Name);
        }

        _engine.Remove(rule.Name);
        ClearForm();
        Refresh();
        _onChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = $"Removed '{rule.Name}'.";
    }

    private void OnClear(object? sender, RoutedEventArgs e) => ClearForm();

    private void OnFilterChanged(object? sender, TextChangedEventArgs e)
    {
        _filter = FilterBox.Text ?? string.Empty;
        Refresh();
    }

    private void ClearForm()
    {
        ItemsList.SelectedItem   = null;
        NameBox.Text             = string.Empty;
        FgColorPicker.Color      = Colors.Yellow;
        FgDefaultCheck.IsChecked = false;
        BgNoneCheck.IsChecked    = true;
        ScopeBox.SelectedIndex   = 0;   // new names default to This character
        StatusText.Text          = string.Empty;
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_engine is null) return;

        var parent = this.GetVisualRoot() as Window;
        if (parent?.StorageProvider is not { } sp) return;

        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Names",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Name files") { Patterns = ["*.cfg", "*.txt"] }
            }
        });
        if (files is null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var result = Genie4Importer.ImportNames(path, _engine, ImportMode.Merge);
        Refresh();
        _onChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = result.Skipped > 0
            ? $"Imported {result.Imported} name(s), skipped {result.Skipped}."
            : $"Imported {result.Imported} name(s).";
    }
}
