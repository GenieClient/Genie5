using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.Core.Layout;

namespace Genie.App.Views;

/// <summary>
/// Per-window display settings editor. Lets the user customise title, font,
/// fg/bg colour, timestamp toggle, and "redirect if closed" routing for each
/// registered dockable. Ported from dylb0t/Genie5 with our top/bottom layout
/// + persistence callback shape.
/// </summary>
public partial class LayoutPanel : UserControl
{
    private WindowSettingsStore? _store;
    private WindowSettings?      _current;
    private Action?              _onChanged;

    /// <summary>Sentinel labels used in the IfClosed dropdown — mirror
    /// Genie 4 / Wrayth UCWindows semantics.</summary>
    private const string IfClosedDefaultLabel  = "(default)";
    private const string IfClosedDisabledLabel = "(disabled)";

    public LayoutPanel()
    {
        InitializeComponent();
        PopulateFontFamilies();
    }

    /// <summary>
    /// Fill the Font family AutoCompleteBox with every system-installed font,
    /// sorted alphabetically. AutoCompleteBox lets the user pick from the
    /// suggestion list OR type a fallback chain
    /// (<c>"Cascadia Mono,Consolas,Courier New,monospace"</c>) — picking is the
    /// friendly path; typing remains the power-user path. (Avalonia's ComboBox
    /// is selection-only; AutoCompleteBox is the pick-or-type equivalent of
    /// WPF's editable ComboBox.)
    /// </summary>
    private void PopulateFontFamilies()
    {
        var names = FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
        FontFamilyCombo.ItemsSource = names;
    }

    public void Initialize(WindowSettingsStore store, Action? onChanged = null)
    {
        _store     = store;
        _onChanged = onChanged;
        Refresh();
    }

    private void Refresh()
    {
        if (_store is null) return;

        WindowList.ItemsSource = _store.All.Values
            .Select(s => s.DisplayTitle)
            .ToList();

        // IfClosed dropdown: (default), (disabled), then every other window's title.
        var items = new List<string> { IfClosedDefaultLabel, IfClosedDisabledLabel };
        items.AddRange(_store.All.Values.Select(s => s.DisplayTitle));
        IfClosedBox.ItemsSource = items;

        if (_store.All.Count > 0) WindowList.SelectedIndex = 0;
    }

    private void OnWindowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_store is null || WindowList.SelectedIndex < 0) return;
        var id = _store.All.Keys.ElementAtOrDefault(WindowList.SelectedIndex);
        if (id is null) return;

        _current = _store.Get(id);
        LoadForm(_current);
        StatusText.Text = string.Empty;
    }

    private void LoadForm(WindowSettings s)
    {
        TitleBox.Text       = s.DisplayTitle;

        // Font family / size: empty / 0 are sentinels meaning "use the
        // global default" — toggle the per-field "Use default" checkbox
        // on and clear the input so the user sees the sentinel state
        // explicitly. WindowSettingsResolver handles fallback at render
        // time.
        LoadFontFamily(s.FontFamily);
        WindowSettingsResolver.LoadDouble(FontSizeBox, FontSizeDefaultCheck, s.FontSize, 0d);

        ColorPickerHelpers.LoadColor(FgColorPicker, FgDefaultCheck, s.Foreground, "Default");
        ColorPickerHelpers.LoadColor(BgColorPicker, BgNoneCheck,    s.Background, "");
        TimestampCheck.IsChecked = s.Timestamp;
        EchoToMainCheck.IsChecked = s.EchoToMain;
        FlashCheck.IsChecked = s.FlashOnActivity;
        IfClosedBox.SelectedItem = IfClosedToLabel(s.IfClosed);
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_current is null) { StatusText.Text = "Select a window first."; return; }

        var title    = TitleBox.Text?.Trim() ?? string.Empty;

        // Font family / size: read via WindowSettingsResolver so sentinels
        // ("Use default" checked) are persisted as empty / 0 — the
        // resolution to a live FontFamily / size happens at render time
        // against the global DisplaySettings.
        var fontFamily = ReadFontFamily();
        var fontSize   = WindowSettingsResolver.ReadDouble(FontSizeBox, FontSizeDefaultCheck, 0d,
                                                           parseFallback: _current.FontSize);

        // Validate font size only when not using the default sentinel. The
        // 6–72 bounds match Genie 4's UCWindows sanity check.
        if (FontSizeDefaultCheck.IsChecked != true && (fontSize < 6 || fontSize > 72))
        {
            StatusText.Text = "Font size must be a number between 6 and 72, or check Use default.";
            return;
        }

        _current.DisplayTitle = string.IsNullOrEmpty(title) ? _current.DefaultTitle : title;
        _current.FontFamily   = fontFamily;     // empty = sentinel = use global
        _current.FontSize     = fontSize;       // 0 = sentinel = use global
        _current.Foreground   = ColorPickerHelpers.ReadColor(FgColorPicker, FgDefaultCheck, "Default");
        _current.Background   = ColorPickerHelpers.ReadColor(BgColorPicker, BgNoneCheck,    "");
        _current.Timestamp    = TimestampCheck.IsChecked == true;
        _current.EchoToMain   = EchoToMainCheck.IsChecked == true;
        _current.FlashOnActivity = FlashCheck.IsChecked == true;
        _current.IfClosed     = LabelToIfClosed(IfClosedBox.SelectedItem as string);
        _current.NotifyChanged();

        Refresh();   // Window list might reflect a renamed title
        _onChanged?.Invoke();
        StatusText.Text = "Applied.";
    }

    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (_current is null || _store is null) return;

        // Reset to the registration-time defaults. Register() returns a fresh
        // template; we copy its fields into the live instance so anyone subscribed
        // to `_current.Changed` sees the update.
        var fresh = _store.Register(_current.Id, _current.DefaultTitle);
        _current.DisplayTitle = _current.DefaultTitle;
        _current.FontFamily   = fresh.FontFamily;
        _current.FontSize     = fresh.FontSize;
        _current.Foreground   = fresh.Foreground;
        _current.Background   = fresh.Background;
        _current.Timestamp    = fresh.Timestamp;
        _current.EchoToMain   = fresh.EchoToMain;
        _current.FlashOnActivity = fresh.FlashOnActivity;
        _current.IfClosed     = fresh.IfClosed;
        _current.NotifyChanged();

        LoadForm(_current);
        Refresh();
        _onChanged?.Invoke();
        StatusText.Text = "Reset to defaults.";
    }

    // ── Font-family ComboBox load / read (uses ComboBox.SelectedItem when
    //    the user picked from the dropdown, otherwise SelectionBoxItem/text
    //    so a typed fallback chain like "Cascadia Mono,Consolas,..." is
    //    preserved verbatim). ─────────────────────────────────────────────
    private void LoadFontFamily(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            FontFamilyDefaultCheck.IsChecked = true;
            FontFamilyCombo.SelectedItem     = null;
            FontFamilyCombo.Text             = string.Empty;
            return;
        }

        FontFamilyDefaultCheck.IsChecked = false;
        // For a comma-separated fallback chain, no single suggestion will
        // match — leave SelectedItem null and let Text hold the chain
        // verbatim. For a single-font name that's in the system list, also
        // mark SelectedItem so the dropdown displays the highlight.
        FontFamilyCombo.Text = raw;
        FontFamilyCombo.SelectedItem = null;
        if (FontFamilyCombo.ItemsSource is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                if (string.Equals(item as string, raw, System.StringComparison.OrdinalIgnoreCase))
                {
                    FontFamilyCombo.SelectedItem = item;
                    return;
                }
            }
        }
    }

    private string ReadFontFamily()
    {
        if (FontFamilyDefaultCheck.IsChecked == true) return string.Empty;

        // If the user picked from the suggestion list, SelectedItem is the
        // canonical name. Otherwise honour whatever they typed (allows
        // comma-separated fallback chains).
        if (FontFamilyCombo.SelectedItem is string picked && !string.IsNullOrWhiteSpace(picked))
            return picked;

        return FontFamilyCombo.Text?.Trim() ?? string.Empty;
    }

    // ── IfClosed value / label mapping ───────────────────────────────────────

    private string IfClosedToLabel(string? raw)
    {
        if (raw == null) return IfClosedDefaultLabel;
        if (raw.Length == 0) return IfClosedDisabledLabel;
        if (_store != null && _store.All.TryGetValue(raw, out var matched))
            return matched.DisplayTitle;
        return raw;
    }

    private string? LabelToIfClosed(string? label)
    {
        if (label == null || label == IfClosedDefaultLabel) return null;
        if (label == IfClosedDisabledLabel) return string.Empty;
        if (_store != null)
        {
            var hit = _store.All.Values.FirstOrDefault(s => s.DisplayTitle == label);
            if (hit != null) return hit.Id;
        }
        return label;
    }
}
