using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Genie.App.Settings;
using Genie.App.Theming;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// View-model for the Display Settings dialog. Edits a local copy of the
/// live <see cref="DisplaySettings"/>; <see cref="OkCommand"/> commits the
/// changes (which the owner persists to disk).
///
/// <para>The Theme tab (#20) is different: picking a theme applies it
/// immediately (live repaint, like the Edit → Theme menu) so the user can
/// see it behind the dialog; Cancel re-applies the theme + game/echo
/// colours captured at open. Import/duplicate/delete are file operations
/// on <c>Config/Themes</c> and are not undone by Cancel.</para>
/// </summary>
public class DisplaySettingsViewModel : ReactiveObject
{
    private readonly DisplaySettings   _live;
    private readonly ThemeService?     _themes;
    private readonly PresetColorStore? _presets;

    // Captured at open for Cancel: applying a theme seeds the live game
    // colour and the echo presets, so restoring the theme alone isn't enough.
    private readonly string _originalThemeName = "";
    private readonly string _originalGameHex   = "";

    /// <summary>The echo presets this dialog edits (public #406), each with
    /// the raw preset value at open (may be "Default") and the colour shown
    /// for it, so OK only writes a preset whose colour was actually changed.</summary>
    private readonly Dictionary<string, (string Raw, Color Shown)> _echoAtOpen = new();
    /// <summary>The vitals presets at open (public #406). Picking a theme on the
    /// Theme tab writes the theme's bar colours into them; Cancel puts these back.</summary>
    private readonly Dictionary<string, (string Fill, string Track)> _vitalsAtOpen = new();
    /// <summary>The ui.* chrome presets at open (public #406). Picking a theme on
    /// the Theme tab sets them back to Default; Cancel puts these back.</summary>
    private readonly Dictionary<string, (string Fg, string Bg)> _uiAtOpen = new();
    private bool _themeTouched;
    private bool _suppressThemeApply;

    /// <summary>
    /// All font families installed on the host OS, plus the currently-selected
    /// font even if it isn't installed (so the binding shows it as selected).
    /// </summary>
    public IReadOnlyList<string> SystemFonts { get; }

    /// <summary>
    /// Hint under the colour pickers (public #304, #406). The theme editor shows
    /// the same colours; Game text is one setting, and the three echo colours ARE
    /// the Genie 4 presets, so the Presets tab edits them too.
    /// </summary>
    public const string GameColorsNote =
        "These are the same colours the theme editor shows (Edit → Theme → Edit Theme…). " +
        "The three echo colours are the inputuser, inputother and scriptecho presets, " +
        "also on Configuration → Presets. Picking a theme sets them all to that theme's " +
        "colours; Reset puts back the active theme's colours.";

    [Reactive] public Color  GameColor  { get; set; }

    /// <summary>Typed commands — the <c>inputuser</c> preset (#406).</summary>
    [Reactive] public Color  InputUserColor  { get; set; }
    /// <summary>Commands sent by scripts — the <c>inputother</c> preset (#406).</summary>
    [Reactive] public Color  InputOtherColor { get; set; }
    /// <summary>Script output and Genie messages — the <c>scriptecho</c> preset (#406).</summary>
    [Reactive] public Color  ScriptEchoColor { get; set; }
    [Reactive] public bool   EchoItalic { get; set; }
    [Reactive] public bool   AlwaysShowScrollbars { get; set; }
    [Reactive] public string FontFamily { get; set; }
    [Reactive] public double FontSize   { get; set; }

    /// <summary>
    /// Path to the user's external editor for "Edit Script" (the pencil
    /// icon on the Script Bar, plus <c>#edit foo</c>). Empty means "use
    /// OS default `.cmd` file handler" — typically Notepad on Windows,
    /// TextEdit on macOS, the desktop default on Linux.
    /// </summary>
    [Reactive] public string EditorPath { get; set; } = "";

    // ── Theme tab (#20) ────────────────────────────────────────────────────

    /// <summary>Built-ins first, then customs — mirrors the Edit → Theme menu.</summary>
    public ObservableCollection<string> ThemeNames { get; } = new();

    [Reactive] public string? SelectedThemeName { get; set; }

    /// <summary>Delete only makes sense for customs; built-ins ship in code.</summary>
    [Reactive] public bool CanDeleteTheme { get; set; }

    /// <summary>One-line feedback under the buttons ("Imported 'Foo'." etc.).</summary>
    [Reactive] public string ThemeStatus { get; set; } = "";

    /// <summary>Custom theme names for the duplicate prompt's overwrite list.</summary>
    public IReadOnlyList<string> CustomThemeNames =>
        _themes?.All.Where(t => !t.IsBuiltIn).Select(t => t.Name).ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public ReactiveCommand<Unit, bool> OkCommand     { get; }
    public ReactiveCommand<Unit, bool> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand  { get; }

    public DisplaySettingsViewModel() : this(new DisplaySettings()) { }

    public DisplaySettingsViewModel(DisplaySettings live, ThemeService? themes = null,
                                    PresetColorStore? presets = null)
    {
        _live    = live;
        _themes  = themes;
        _presets = presets ?? themes?.Presets;

        // The stored value may be a comma-separated fallback chain ("Consolas,
        // Courier New,monospace"). Picking from a dropdown only sets one name,
        // so take the head of the chain as the current selection.
        var currentFont = live.FontFamily.Split(',')[0].Trim();

        SystemFonts = LoadSystemFonts(currentFont);

        GameColor  = TryParseColor(live.GameColorHex, Avalonia.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
        foreach (var (_, id) in ThemeKeys.EchoPresetKeys)
        {
            var raw = _presets?.GetForeground(id) ?? new Genie.Core.Presets.PresetEngine().GetForeground(id);
            _echoAtOpen[id] = (raw, TryParseColor(raw, GameColor));   // "Default" → the text colour
        }
        LoadEchoColours(id => _echoAtOpen[id].Shown);
        if (_presets is not null)
        {
            foreach (var id in Genie.Core.VitalsPresets.All)
                _vitalsAtOpen[id] = (_presets.GetForeground(id), _presets.GetBackground(id));
            foreach (var id in Genie.Core.UiPresets.All)
                _uiAtOpen[id] = (_presets.GetForeground(id), _presets.GetBackground(id));
        }
        EchoItalic = live.EchoItalic;
        AlwaysShowScrollbars = live.AlwaysShowScrollbars;
        FontFamily = currentFont;
        FontSize   = live.FontSize;
        EditorPath = live.EditorPath;

        if (_themes is not null)
        {
            _originalThemeName = _themes.CurrentName;
            _originalGameHex   = live.GameColorHex;
            RefreshThemeNames(select: _themes.CurrentName);

            // Skip(1): the initial selection above must not re-apply.
            this.WhenAnyValue(x => x.SelectedThemeName)
                .Skip(1)
                .Where(_ => !_suppressThemeApply)
                .Subscribe(ApplySelectedTheme);
        }

        OkCommand     = ReactiveCommand.Create(Commit);
        CancelCommand = ReactiveCommand.Create(() => false);
        ResetCommand  = ReactiveCommand.Create(ResetToDefaults);
    }

    private bool Commit()
    {
        _live.GameColorHex = Hex(GameColor);
        // Echo colours are presets (#406): write only the ones the user changed,
        // so a preset left at "Default" isn't silently turned into a fixed colour.
        if (_presets is not null)
            foreach (var (id, colour) in EchoColours())
                if (colour != _echoAtOpen[id].Shown)
                    _presets.SetForeground(id, Hex(colour));
        _live.EchoItalic   = EchoItalic;
        _live.AlwaysShowScrollbars = AlwaysShowScrollbars;
        _live.FontFamily   = FontFamily;
        _live.FontSize     = FontSize;
        _live.EditorPath   = EditorPath ?? "";
        return true;
    }

    /// <summary>
    /// Undo any theme applied from the Theme tab. The view calls this on
    /// every non-OK close (Cancel button AND the ✕), so a previewed theme
    /// never survives a dismissed dialog. File operations (import /
    /// duplicate / delete) are not undone — they're real files.
    /// </summary>
    public void RestoreOriginalTheme()
    {
        if (_themes is null || !_themeTouched) return;
        _themeTouched = false;

        if (_themes.Find(_originalThemeName) is { } orig)
            _themes.Apply(orig);
        // Apply re-seeded game/echo from the theme file; the user may
        // have tweaked them since that theme was last applied — put
        // back exactly what was live when the dialog opened.
        _live.GameColorHex = _originalGameHex;
        if (_presets is not null)
        {
            foreach (var (id, (raw, _)) in _echoAtOpen)
                _presets.SetForeground(id, raw);
            foreach (var (id, (fill, track)) in _vitalsAtOpen)
                if (_presets.GetForeground(id) != fill || _presets.GetBackground(id) != track)
                    _presets.Set(id, fill, track);
            foreach (var (id, (fg, bg)) in _uiAtOpen)
                if (_presets.GetForeground(id) != fg || _presets.GetBackground(id) != bg)
                    _presets.Set(id, fg, bg);
        }
    }

    private static string Hex(Color c) => "#" + c.ToString().Substring(3); // drop alpha → #RRGGBB

    private IEnumerable<(string Id, Color Colour)> EchoColours() =>
    [
        (Genie.Core.EchoKindPresets.InputUser,  InputUserColor),
        (Genie.Core.EchoKindPresets.InputOther, InputOtherColor),
        (Genie.Core.EchoKindPresets.ScriptEcho, ScriptEchoColor),
    ];

    private void LoadEchoColours(Func<string, Color> colourOf)
    {
        InputUserColor  = colourOf(Genie.Core.EchoKindPresets.InputUser);
        InputOtherColor = colourOf(Genie.Core.EchoKindPresets.InputOther);
        ScriptEchoColor = colourOf(Genie.Core.EchoKindPresets.ScriptEcho);
    }

    // ── Theme tab (#20) ────────────────────────────────────────────────────

    private void RefreshThemeNames(string? select)
    {
        if (_themes is null) return;
        _suppressThemeApply = true;
        try
        {
            ThemeNames.Clear();
            foreach (var t in _themes.All) ThemeNames.Add(t.Name);
            SelectedThemeName =
                select is not null && ThemeNames.Contains(select, StringComparer.OrdinalIgnoreCase)
                    ? ThemeNames.First(n => n.Equals(select, StringComparison.OrdinalIgnoreCase))
                    : ThemeNames.FirstOrDefault();
            CanDeleteTheme = _themes.Find(SelectedThemeName ?? "") is { IsBuiltIn: false };
        }
        finally { _suppressThemeApply = false; }
    }

    private void ApplySelectedTheme(string? name)
    {
        if (_themes is null || string.IsNullOrWhiteSpace(name)) return;
        if (_themes.Find(name) is not { } theme) return;

        _themes.Apply(theme);          // live repaint, like the Edit → Theme menu
        _themeTouched  = true;
        CanDeleteTheme = !theme.IsBuiltIn;

        // Apply seeded the LIVE game/echo hexes from the theme; sync the
        // dialog's local copies so OK doesn't commit stale pre-theme values.
        GameColor = TryParseColor(_live.GameColorHex, GameColor);
        if (_presets is not null)
        {
            LoadEchoColours(id => TryParseColor(_presets.GetForeground(id), GameColor));
            // The theme wrote the presets already: they are the new baseline,
            // so OK must not treat them as user edits.
            foreach (var (id, colour) in EchoColours())
                _echoAtOpen[id] = (_echoAtOpen[id].Raw, colour);
        }
    }

    /// <summary>Import a theme JSON file into Config/Themes and apply it.</summary>
    public void ImportTheme(string path)
    {
        if (_themes is null) return;
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) { ThemeStatus = $"Couldn't read file: {ex.Message}"; return; }

        var theme = _themes.Import(json);
        if (theme is null) { ThemeStatus = "Not a valid theme file."; return; }
        RefreshThemeNames(select: theme.Name);
        ApplySelectedTheme(theme.Name);
        ThemeStatus = $"Imported '{theme.Name}'.";
    }

    /// <summary>Write the selected theme's JSON to a user-chosen path.</summary>
    public void ExportTheme(string path)
    {
        if (_themes is null || SelectedThemeName is null) return;
        ThemeStatus = _themes.ExportTo(SelectedThemeName, path)
            ? $"Exported '{SelectedThemeName}'."
            : "Export failed — couldn't write the file.";
    }

    /// <summary>Copy the selected theme to a new custom name and apply the copy.</summary>
    public void DuplicateTheme(string newName)
    {
        if (_themes is null || SelectedThemeName is null) return;
        var copy = _themes.Duplicate(SelectedThemeName, newName);
        if (copy is null)
        {
            ThemeStatus = $"Can't duplicate as '{newName}' — that name is reserved by a built-in theme.";
            return;
        }
        RefreshThemeNames(select: copy.Name);
        ApplySelectedTheme(copy.Name);
        ThemeStatus = $"Duplicated as '{copy.Name}'.";
    }

    /// <summary>Delete the selected custom theme file (built-ins refuse).</summary>
    public void DeleteSelectedTheme()
    {
        if (_themes is null || SelectedThemeName is null) return;
        var name = SelectedThemeName;
        if (_themes.Find(name) is { IsBuiltIn: true })
        {
            ThemeStatus = "Built-in themes can't be deleted.";
            return;
        }

        var wasActive = _themes.CurrentName.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (!_themes.Delete(name)) { ThemeStatus = $"Couldn't delete '{name}'."; return; }

        if (wasActive)
        {
            // The active palette's file is gone — fall back to the default.
            RefreshThemeNames(select: BuiltInThemes.DefaultName);
            ApplySelectedTheme(BuiltInThemes.DefaultName);
        }
        else RefreshThemeNames(select: _themes.CurrentName);
        ThemeStatus = $"Deleted '{name}'.";
    }

    private void ResetToDefaults()
    {
        var d = new DisplaySettings();
        // Game text / Echoes reset to the ACTIVE theme's colours (public #304) — the
        // built-in DisplaySettings defaults are the Dark palette's, which on a
        // light theme meant Reset painted light-grey text on a white window.
        var theme = _themes?.Current;
        GameColor  = TryParseColor(theme?.Get(ThemeKeys.GameText) ?? d.GameColorHex, Colors.LightGray);
        // Echo colours: the active theme's, else the stock preset defaults (#406).
        var themeEcho = theme is null ? new() : ThemeService.EchoColours(theme).ToDictionary(x => x.PresetId, x => x.Colour);
        var stock     = new Genie.Core.Presets.PresetEngine();
        LoadEchoColours(id => TryParseColor(themeEcho.GetValueOrDefault(id) ?? stock.GetForeground(id), GameColor));
        EchoItalic = d.EchoItalic;
        AlwaysShowScrollbars = d.AlwaysShowScrollbars;
        FontFamily = d.FontFamily.Split(',')[0].Trim();
        FontSize   = d.FontSize;
        EditorPath = d.EditorPath;
    }

    // ── System font enumeration (cross-platform via FontManager) ──────────────

    private static IReadOnlyList<string> LoadSystemFonts(string includeIfMissing)
    {
        // FontManager.Current.SystemFonts uses the platform's font enumerator:
        //   Windows → DirectWrite, macOS → CoreText, Linux → FontConfig.
        // No OS-specific code needed here.
        var fonts = FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(includeIfMissing) &&
            !fonts.Contains(includeIfMissing, StringComparer.OrdinalIgnoreCase))
        {
            fonts.Add(includeIfMissing);
        }

        fonts.Sort(StringComparer.OrdinalIgnoreCase);
        return fonts;
    }

    private static Color TryParseColor(string hex, Color fallback)
        => Color.TryParse(hex, out var c) ? c : fallback;
}
