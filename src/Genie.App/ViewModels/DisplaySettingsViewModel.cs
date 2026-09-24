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
    private readonly DisplaySettings _live;
    private readonly ThemeService?   _themes;

    // Captured at open for Cancel: applying a theme seeds the live
    // game/echo colours, so restoring the theme alone isn't enough.
    private readonly string _originalThemeName = "";
    private readonly string _originalGameHex   = "";
    private readonly string _originalEchoHex   = "";
    private bool _themeTouched;
    private bool _suppressThemeApply;

    /// <summary>
    /// All font families installed on the host OS, plus the currently-selected
    /// font even if it isn't installed (so the binding shows it as selected).
    /// </summary>
    public IReadOnlyList<string> SystemFonts { get; }

    [Reactive] public Color  GameColor  { get; set; }
    [Reactive] public Color  EchoColor  { get; set; }
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

    public DisplaySettingsViewModel(DisplaySettings live, ThemeService? themes = null)
    {
        _live   = live;
        _themes = themes;

        // The stored value may be a comma-separated fallback chain ("Consolas,
        // Courier New,monospace"). Picking from a dropdown only sets one name,
        // so take the head of the chain as the current selection.
        var currentFont = live.FontFamily.Split(',')[0].Trim();

        SystemFonts = LoadSystemFonts(currentFont);

        GameColor  = TryParseColor(live.GameColorHex, Avalonia.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));
        EchoColor  = TryParseColor(live.EchoColorHex, Avalonia.Media.Color.FromRgb(0x88, 0xBB, 0xCC));
        EchoItalic = live.EchoItalic;
        AlwaysShowScrollbars = live.AlwaysShowScrollbars;
        FontFamily = currentFont;
        FontSize   = live.FontSize;
        EditorPath = live.EditorPath;

        if (_themes is not null)
        {
            _originalThemeName = _themes.CurrentName;
            _originalGameHex   = live.GameColorHex;
            _originalEchoHex   = live.EchoColorHex;
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
        _live.GameColorHex = "#" + GameColor.ToString().Substring(3); // drop alpha → #RRGGBB
        _live.EchoColorHex = "#" + EchoColor.ToString().Substring(3);
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
        _live.EchoColorHex = _originalEchoHex;
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
        EchoColor = TryParseColor(_live.EchoColorHex, EchoColor);
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
        GameColor  = TryParseColor(d.GameColorHex, Colors.LightGray);
        EchoColor  = TryParseColor(d.EchoColorHex, Avalonia.Media.Color.FromRgb(0x88, 0xBB, 0xCC));
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
