using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.Core.Config;
using Genie.Core.Persistence;
using Genie.Core.Presets;

namespace Genie.App.Views;

/// <summary>
/// Preset (token) editor. Each preset is a named colour pair (fg / bg) the
/// renderer uses for game-emitted tokens like <c>roomname</c>, <c>speech</c>,
/// <c>creatures</c>, AutoMapper colours, UI chrome colours, etc.
/// </summary>
public partial class PresetsPanel : UserControl
{
    public sealed record PresetRow(string Scope, string Id, string ForegroundColor, string BackgroundColor, string LineGlyph)
    {
        public IBrush IdForeground => ColorPickerHelpers.ParseBrush(ForegroundColor) ?? Brushes.LightGray;
        public IBrush IdBackground => ColorPickerHelpers.ParseBrush(BackgroundColor) ?? Brushes.Transparent;
    }

    private PresetEngine?        _engine;
    private Action?              _onChanged;
    private GenieConfig?         _config;
    private Action?              _onConfigChanged;
    private ScopeEditingContext? _scopeCtx;
    private bool                 _suppressMonsterBold;   // guard the initial IsChecked set

    public PresetsPanel()
    {
        InitializeComponent();
        ScopeBox.ItemsSource   = ScopeEditing.Labels;
        ScopeBox.SelectedIndex = 1;   // preset overrides default to All characters —
                                      // the palette is app-wide unless deliberately per-char
    }

    public void Initialize(PresetEngine engine, Action? onChanged = null,
                           GenieConfig? config = null, Action? onConfigChanged = null,
                           ScopeEditingContext? scopeContext = null)
    {
        _engine          = engine;
        _onChanged       = onChanged;
        _config          = config;
        _onConfigChanged = onConfigChanged;
        _scopeCtx        = scopeContext;
        var twoLayers    = scopeContext?.TwoLayers == true;
        ScopeGroup.IsVisible = twoLayers;
        ScopeEditing.SetColumnVisible(PresetList, "Scope", twoLayers);

        // #131 MonsterBold on/off. Reflect the persisted setting without firing
        // the change handler (which would re-persist + re-render on load). The
        // control disables itself pre-connect, when there's no config to bind.
        _suppressMonsterBold = true;
        MonsterBoldCheck.IsChecked   = config?.MonsterBold ?? true;
        MonsterBoldCheck.IsEnabled   = config is not null;
        _suppressMonsterBold = false;

        Refresh();
    }

    /// <summary>#131: flip MonsterBold live. Updates the persisted config, the
    /// renderer's live flag, saves settings.cfg, and repaints visible lines so
    /// the change is immediate in every window (not just on next connect).</summary>
    private void OnMonsterBoldToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppressMonsterBold || _config is null) return;
        var on = MonsterBoldCheck.IsChecked == true;
        _config.MonsterBold                 = on;
        DefaultHighlights.MonsterBoldEnabled = on;
        _onConfigChanged?.Invoke();          // persist settings.cfg
        UserHighlights.NotifyRulesChanged(); // repaint currently-visible lines
    }

    private void Refresh()
    {
        if (_engine is null) return;
        var keep = (PresetList.SelectedItem as PresetRow)?.Id;
        PresetList.ItemsSource = _engine.Presets
            .OrderBy(kv => kv.Key)
            .Select(kv => new PresetRow(
                ScopeEditing.RowLabel(kv.Value.Scope),
                kv.Key,
                kv.Value.ForegroundColor,
                kv.Value.BackgroundColor,
                kv.Value.HighlightLine ? "✓" : ""))
            .ToList();
        if (keep is not null)
            PresetList.SelectedItem = ((IEnumerable<PresetRow>)PresetList.ItemsSource)
                .FirstOrDefault(r => r.Id == keep);
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_engine is null || PresetList.SelectedItem is not PresetRow row) return;
        var rule = _engine.Get(row.Id);
        if (rule is null) return;

        IdLabel.Text = row.Id;
        ColorPickerHelpers.LoadColor(FgColorPicker, FgDefaultCheck, rule.ForegroundColor, "Default");
        ColorPickerHelpers.LoadColor(BgColorPicker, BgNoneCheck,    rule.BackgroundColor, "");
        HighlightLineCheck.IsChecked = rule.HighlightLine;
        ScopeBox.SelectedIndex       = ScopeEditing.ToIndex(rule.Scope);
        UpdatePreview(rule.ForegroundColor, rule.BackgroundColor);
        StatusText.Text = string.Empty;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || PresetList.SelectedItem is not PresetRow row) return;
        var id = row.Id;

        var fg = ColorPickerHelpers.ReadColor(FgColorPicker, FgDefaultCheck, "Default");
        var bg = ColorPickerHelpers.ReadColor(BgColorPicker, BgNoneCheck,    "");

        _engine.Apply(new PresetRule
        {
            Id              = id,
            ForegroundColor = fg,
            BackgroundColor = bg,
            HighlightLine   = HighlightLineCheck.IsChecked == true,
            Scope           = _scopeCtx?.TwoLayers == true
                                  ? ScopeEditing.FromIndex(ScopeBox.SelectedIndex)
                                  : RuleScope.Global,
        });

        UpdatePreview(fg, bg);
        Refresh();
        _onChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = "Applied.";
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || PresetList.SelectedItem is not PresetRow row) return;
        var id = row.Id;

        // Build a fresh engine to recover the default for this preset id, then
        // apply that defaults rule back into the live engine.
        var fresh = new PresetEngine();
        var rule  = fresh.Get(id);
        if (rule is null) { StatusText.Text = "No default for this preset."; return; }

        _engine.Apply(rule);
        ColorPickerHelpers.LoadColor(FgColorPicker, FgDefaultCheck, rule.ForegroundColor, "Default");
        ColorPickerHelpers.LoadColor(BgColorPicker, BgNoneCheck,    rule.BackgroundColor, "");
        HighlightLineCheck.IsChecked = rule.HighlightLine;
        UpdatePreview(rule.ForegroundColor, rule.BackgroundColor);
        Refresh();
        _onChanged?.Invoke();
        UserHighlights.NotifyRulesChanged();
        StatusText.Text = "Reset to default.";
    }

    private void UpdatePreview(string fg, string bg)
    {
        PreviewText.Foreground   = ToBrush(fg) ?? Brushes.LightGray;
        PreviewBorder.Background = string.IsNullOrEmpty(bg)
            ? Brushes.Transparent
            : (ToBrush(bg) ?? Brushes.Transparent);
    }

    private static IBrush? ToBrush(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("Default", StringComparison.OrdinalIgnoreCase)) return null;
        if (Color.TryParse(name, out var c)) return new SolidColorBrush(c);
        try { return Brush.Parse(name); }
        catch { return null; }
    }
}
