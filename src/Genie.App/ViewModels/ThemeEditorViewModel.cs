using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Genie.App.Settings;
using Genie.App.Theming;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>One editable colour role in the theme editor.</summary>
public sealed class ThemeColorEntry : ReactiveObject
{
    public string Key   { get; }
    public string Label { get; }
    [Reactive] public Color Value { get; set; }

    public ThemeColorEntry(string key, string label, Color value)
    {
        Key   = key;
        Label = label;
        Value = value;
    }
}

/// <summary>
/// Backs the Edit → Theme → Edit Theme… dialog (#20). Opens on a copy of the
/// ACTIVE theme (game/echo seeded from the live DisplaySettings values, which
/// may be user-tweaked away from the theme) and live-previews every change
/// through <see cref="ThemeService.Preview"/> — the whole app repaints as you
/// drag a picker. Save persists a custom theme (built-in names are reserved,
/// so editing a built-in naturally forks it); Cancel calls
/// <see cref="RestoreOriginal"/> to repaint the pre-edit state.
/// </summary>
public sealed class ThemeEditorViewModel : ReactiveObject
{
    private readonly ThemeService    _service;
    private readonly DisplaySettings _display;
    private readonly Theme           _original;
    private readonly string          _originalGameHex;
    private readonly string          _originalEchoHex;

    [Reactive] public string ThemeName      { get; set; }
    [Reactive] public bool   IsLightVariant { get; set; }

    /// <summary>Shown as a hint when the editor was opened on a built-in —
    /// saving will create a custom copy under the new name.</summary>
    public bool IsEditingBuiltIn { get; }

    public ObservableCollection<ThemeColorEntry> Surfaces  { get; } = new();
    public ObservableCollection<ThemeColorEntry> TextRoles { get; } = new();
    public ObservableCollection<ThemeColorEntry> Accents   { get; } = new();
    public ObservableCollection<ThemeColorEntry> Vitals    { get; } = new();
    public ObservableCollection<ThemeColorEntry> GameRoles { get; } = new();

    private readonly ObservableAsPropertyHelper<bool> _canSave;
    /// <summary>Non-empty name that doesn't collide with a built-in.</summary>
    public bool CanSave => _canSave.Value;

    public ThemeEditorViewModel(ThemeService service, DisplaySettings display)
    {
        _service  = service;
        _display  = display;
        _original = service.Current;
        _originalGameHex = display.GameColorHex;
        _originalEchoHex = display.EchoColorHex;

        IsEditingBuiltIn = _original.IsBuiltIn;
        ThemeName        = _original.IsBuiltIn ? $"My {_original.Name}" : _original.Name;
        IsLightVariant   = _original.BaseVariant.Equals("Light", StringComparison.OrdinalIgnoreCase);

        Color FromTheme(string key) =>
            Color.TryParse(_original.Get(key) ?? BuiltInThemes.Dark.Get(key)!, out var c)
                ? c : Colors.Magenta;
        Color FromHex(string hex) => Color.TryParse(hex, out var c) ? c : Colors.Magenta;

        void Add(ObservableCollection<ThemeColorEntry> group, string key, string label, Color value)
            => group.Add(new ThemeColorEntry(key, label, value));

        Add(Surfaces, ThemeKeys.WindowBg,     "Window background",                 FromTheme(ThemeKeys.WindowBg));
        Add(Surfaces, ThemeKeys.PanelBg,      "Panel background",                  FromTheme(ThemeKeys.PanelBg));
        Add(Surfaces, ThemeKeys.PanelBgDeep,  "Log / output wells",                FromTheme(ThemeKeys.PanelBgDeep));
        Add(Surfaces, ThemeKeys.ToolbarBg,    "Toolbars",                          FromTheme(ThemeKeys.ToolbarBg));
        Add(Surfaces, ThemeKeys.StripBg,      "Strips (hands / status bars)",      FromTheme(ThemeKeys.StripBg));
        Add(Surfaces, ThemeKeys.Border,       "Borders & separators",              FromTheme(ThemeKeys.Border));
        Add(Surfaces, ThemeKeys.Selection,    "Text selection",                    FromTheme(ThemeKeys.Selection));

        Add(TextRoles, ThemeKeys.TextPrimary,   "Text — primary",                  FromTheme(ThemeKeys.TextPrimary));
        Add(TextRoles, ThemeKeys.TextSecondary, "Text — secondary",                FromTheme(ThemeKeys.TextSecondary));
        Add(TextRoles, ThemeKeys.TextMuted,     "Text — muted / hints",            FromTheme(ThemeKeys.TextMuted));
        Add(TextRoles, ThemeKeys.SectionHeader, "Section headers",                 FromTheme(ThemeKeys.SectionHeader));

        Add(Accents, ThemeKeys.Accent,       "Accent (links, exits, compass)",     FromTheme(ThemeKeys.Accent));
        Add(Accents, ThemeKeys.AccentBg,     "Accent background",                  FromTheme(ThemeKeys.AccentBg));
        Add(Accents, ThemeKeys.AccentBorder, "Accent border",                      FromTheme(ThemeKeys.AccentBorder));
        Add(Accents, ThemeKeys.Success,      "Success (players, ok badges)",       FromTheme(ThemeKeys.Success));
        Add(Accents, ThemeKeys.SuccessBg,    "Success background",                 FromTheme(ThemeKeys.SuccessBg));
        // "creatures" dropped from the label (#236): the Mobs window's creature
        // colour now follows the `creatures` preset, not this token.
        Add(Accents, ThemeKeys.Warning,      "Warning (stale)",                    FromTheme(ThemeKeys.Warning));
        Add(Accents, ThemeKeys.WarningBg,    "Warning background",                 FromTheme(ThemeKeys.WarningBg));
        Add(Accents, ThemeKeys.Danger,       "Danger (errors, destructive)",       FromTheme(ThemeKeys.Danger));
        Add(Accents, ThemeKeys.DangerBg,     "Danger background",                  FromTheme(ThemeKeys.DangerBg));

        Add(Vitals, ThemeKeys.HealthBar,  "Health bar",        FromTheme(ThemeKeys.HealthBar));
        Add(Vitals, ThemeKeys.ManaBar,    "Mana bar",          FromTheme(ThemeKeys.ManaBar));
        Add(Vitals, ThemeKeys.SpiritBar,  "Spirit bar",        FromTheme(ThemeKeys.SpiritBar));
        Add(Vitals, ThemeKeys.StaminaBar, "Stamina bar",       FromTheme(ThemeKeys.StaminaBar));
        Add(Vitals, ThemeKeys.ConcBar,    "Concentration bar", FromTheme(ThemeKeys.ConcBar));

        // Seed from the LIVE display values, not the theme file — "edit what
        // I'm looking at", including any Display Settings tweaks.
        Add(GameRoles, ThemeKeys.GameText, "Game text (default)", FromHex(_originalGameHex));
        Add(GameRoles, ThemeKeys.GameEcho, "Echo (typed commands)", FromHex(_originalEchoHex));

        _canSave = this.WhenAnyValue(x => x.ThemeName,
                name => !string.IsNullOrWhiteSpace(name) &&
                        !BuiltInThemes.All.Any(b =>
                            b.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToProperty(this, x => x.CanSave);

        // ── Live preview ──────────────────────────────────────────────────
        // Merge every entry's colour stream + the variant toggle; throttle
        // because a picker drag fires continuously and the game-text entries
        // trigger a full line re-tokenise per push.
        var changes = AllEntries()
            .Select(e => e.WhenAnyValue(x => x.Value).Skip(1).Select(_ => Unit.Default))
            .Append(this.WhenAnyValue(x => x.IsLightVariant).Skip(1).Select(_ => Unit.Default));
        Observable.Merge(changes)
            .Throttle(TimeSpan.FromMilliseconds(120), RxApp.MainThreadScheduler)
            .Subscribe(_ => _service.Preview(BuildTheme()));
    }

    private IEnumerable<ThemeColorEntry> AllEntries()
        => Surfaces.Concat(TextRoles).Concat(Accents).Concat(Vitals).Concat(GameRoles);

    /// <summary>The edited palette as a saveable theme.</summary>
    public Theme BuildTheme()
    {
        var theme = new Theme
        {
            Name        = ThemeName.Trim(),
            BaseVariant = IsLightVariant ? "Light" : "Dark",
        };
        foreach (var e in AllEntries())
            theme.Colors[e.Key] = ToHex(e.Value);
        return theme;
    }

    /// <summary>Cancel path: repaint the pre-edit theme and put back the
    /// pre-edit game/echo colours (they may have been user-tweaked, so the
    /// theme's own values are not authoritative).</summary>
    public void RestoreOriginal()
    {
        _service.Apply(_original, applyGameText: false);
        _display.GameColorHex = _originalGameHex;
        _display.EchoColorHex = _originalEchoHex;
    }

    private static string ToHex(Color c)
        => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                      : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
