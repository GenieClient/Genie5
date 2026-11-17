using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Genie.App.Settings;

namespace Genie.App.Theming;

/// <summary>
/// Applies themes (#20) and manages the user's custom theme files.
///
/// <para><b>Apply pipeline</b> — mirrors <see cref="DisplaySettings.Apply"/>:
/// set <c>Application.RequestedThemeVariant</c> (flips every Fluent-styled
/// control Dark↔Light), then push one <see cref="SolidColorBrush"/> per
/// <see cref="ThemeKeys"/> role into <c>Application.Resources</c>. All chrome
/// AXAML references those keys via <c>DynamicResource</c>, so the repaint is
/// live — no restart.</para>
///
/// <para><b>Game-text precedence</b> (#20 "per-stream overrides win"):
/// an explicit user Apply also seeds <see cref="DisplaySettings.GameColorHex"/>,
/// the echo presets <c>inputuser</c> / <c>inputother</c> / <c>scriptecho</c> and
/// the vitals presets <c>health</c> / <c>mana</c> / … (public #406, through
/// <see cref="Presets"/>) from the theme; the startup
/// re-apply does NOT, so Display Settings tweaks and preset edits persist
/// across launches on top of any theme.</para>
///
/// <para>Custom themes live as JSON files in <c>Config/Themes</c> — one per
/// theme, hand-editable, missing keys fall back to the built-in Dark
/// palette per-key.</para>
/// </summary>
public sealed class ThemeService
{
    private readonly string _themesDir;
    private readonly DisplaySettings _display;
    private readonly List<Theme> _custom = new();

    /// <summary>Raised after any Apply — hook for persistence + menu refresh.</summary>
    public event Action? ThemeApplied;

    /// <summary>Where a theme's echo and vitals colours go (public #406): the
    /// presets. Set by the main window once the core exists; null (tests,
    /// designer) skips them.</summary>
    public PresetColorStore? Presets { get; set; }

    public ThemeService(string themesDir, DisplaySettings display)
    {
        _themesDir = themesDir;
        _display   = display;
        ReloadCustomThemes();
    }

    /// <summary>Built-ins first (fixed order), then customs alphabetically.</summary>
    public IReadOnlyList<Theme> All =>
        BuiltInThemes.All.Concat(_custom.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Name of the active theme (persisted in display.json).</summary>
    public string CurrentName => string.IsNullOrWhiteSpace(_display.ThemeName)
        ? BuiltInThemes.DefaultName
        : _display.ThemeName;

    public Theme Current => Find(CurrentName) ?? BuiltInThemes.Dark;

    public Theme? Find(string name) =>
        All.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ── Apply ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Re-apply the persisted theme at startup. Chrome + variant only —
    /// game-text colours already live in display.json (seeded at the last
    /// explicit apply, possibly user-tweaked since), so re-seeding here
    /// would clobber the user's Display Settings edits every launch.
    /// </summary>
    public void ApplyStartup() => Apply(Current, applyGameText: false);

    /// <summary>Explicit user apply: chrome + variant + game-text defaults.</summary>
    public void Apply(Theme theme, bool applyGameText = true)
    {
        PushPalette(theme, applyGameText, savePresets: true);
        _display.ThemeName = theme.Name;
        ThemeApplied?.Invoke();
    }

    /// <summary>
    /// Live-preview a (possibly unsaved) theme — used by the theme editor
    /// while the user drags colour pickers. Paints everything exactly like
    /// <see cref="Apply"/> but records nothing: no <c>ThemeName</c>, no
    /// <see cref="ThemeApplied"/>. Cancel = re-<see cref="Apply"/> the
    /// original (the editor also restores the pre-edit game/echo hexes,
    /// which may have been user-tweaked away from the theme's own values).
    /// </summary>
    public void Preview(Theme theme) => PushPalette(theme, applyGameText: true, savePresets: false);

    private void PushPalette(Theme theme, bool applyGameText, bool savePresets)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant =
            theme.BaseVariant.Equals("Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

        var res = app.Resources;
        foreach (var key in ThemeKeys.BrushKeys)
        {
            var hex = theme.Get(key) ?? BuiltInThemes.Dark.Get(key)!;
            res[key] = new SolidColorBrush(
                Color.TryParse(hex, out var c) ? c : Colors.Magenta);
        }

        if (applyGameText)
        {
            // Routed through DisplaySettings so its existing subscription
            // pushes GameBrush/EchoBrush AND re-tokenises rendered lines.
            if (theme.Get(ThemeKeys.GameText) is { } game) _display.GameColorHex = game;

            if (Presets is { } presets)
            {
                foreach (var (presetId, colour) in EchoColours(theme))
                {
                    if (savePresets) presets.SetForeground(presetId, colour);
                    else             presets.Preview(presetId, colour);
                }
                foreach (var (presetId, fill, track) in VitalsColours(theme))
                {
                    if (savePresets) presets.Set(presetId, fill, track);
                    else             presets.Preview(presetId, fill, track);
                }
                // The ui.* chrome presets (public #406) go back to Default, so the
                // theme just picked colours the menu, input, status strip and
                // AutoMapper Settings again, as picking a theme resets the echo
                // and vitals colours. A preview leaves them alone.
                if (savePresets)
                    foreach (var presetId in Genie.Core.UiPresets.All)
                        if (!IsDefault(presets.GetForeground(presetId)) || presets.GetBackground(presetId).Length > 0)
                            presets.Set(presetId, "Default", "");
            }
        }
    }

    private static bool IsDefault(string colour) =>
        colour.Length == 0 || colour.Equals("Default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The vitals preset colours <paramref name="theme"/> sets (public
    /// #406): each bar's fill (the preset's foreground) and track (its
    /// background). A missing key is null and leaves that colour alone, so a
    /// theme file from before #406, which has fills but no tracks, keeps the
    /// presets' tracks. An empty track means none.</summary>
    public static IEnumerable<(string PresetId, string? Fill, string? Track)> VitalsColours(Theme theme)
    {
        foreach (var (fillKey, trackKey, presetId) in ThemeKeys.VitalsPresetKeys)
        {
            var fill  = theme.Get(fillKey);
            var track = theme.Get(trackKey);
            if (fill is not null || track is not null)
                yield return (presetId, fill, track);
        }
    }

    /// <summary>The echo preset colours <paramref name="theme"/> sets (public #406).
    /// A theme file from before #406 has only the single legacy
    /// <see cref="ThemeKeys.GameEcho"/>; it is applied to all three presets so
    /// the theme keeps its look. A theme with neither sets none.</summary>
    public static IEnumerable<(string PresetId, string Colour)> EchoColours(Theme theme)
    {
        var hasEchoKeys = ThemeKeys.EchoPresetKeys.Any(k => theme.Get(k.Key) is not null);
        var legacy      = hasEchoKeys ? null : theme.Get(ThemeKeys.GameEcho);
        foreach (var (key, presetId) in ThemeKeys.EchoPresetKeys)
            if ((theme.Get(key) ?? legacy) is { } colour)
                yield return (presetId, colour);
    }

    // ── Custom themes ─────────────────────────────────────────────────────

    public void ReloadCustomThemes()
    {
        _custom.Clear();
        if (!Directory.Exists(_themesDir)) return;
        foreach (var file in Directory.EnumerateFiles(_themesDir, "*.json"))
        {
            try
            {
                var theme = Theme.FromJson(File.ReadAllText(file));
                if (theme is null) continue;
                if (string.IsNullOrWhiteSpace(theme.Name))
                    theme.Name = Path.GetFileNameWithoutExtension(file);
                // A custom may not shadow a built-in name — skip it.
                if (BuiltInThemes.All.Any(b =>
                        b.Name.Equals(theme.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _custom.Add(theme);
            }
            catch { /* unreadable file — ignore, don't break the menu */ }
        }
    }

    /// <summary>
    /// Snapshot the active palette (plus the CURRENT game/echo colours, which
    /// may be user-tweaked — that's the point of "Save Current As") into a
    /// named custom theme, persist it, and make it the active theme.
    /// Returns null when the name collides with a built-in.
    /// </summary>
    public Theme? SaveCurrentAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;

        var snapshot = Current.Clone(name);
        snapshot.Colors[ThemeKeys.GameText] = _display.GameColorHex;
        snapshot.Colors.Remove(ThemeKeys.GameEcho);   // superseded by the three echo keys (#406)
        if (Presets is { } presets)
        {
            foreach (var (key, presetId) in ThemeKeys.EchoPresetKeys)
                snapshot.Colors[key] = presets.GetForeground(presetId);
            foreach (var (fillKey, trackKey, presetId) in ThemeKeys.VitalsPresetKeys)
            {
                snapshot.Colors[fillKey]  = presets.GetForeground(presetId);
                snapshot.Colors[trackKey] = presets.GetBackground(presetId);
            }
        }

        return SaveOrUpdateCustom(snapshot) ? snapshot : null;
    }

    /// <summary>
    /// Persist a fully-built custom theme (theme editor path): write/overwrite
    /// its <c>Config/Themes</c> file, make it the active theme. Returns false
    /// when the name is empty or reserved by a built-in.
    /// </summary>
    public bool SaveOrUpdateCustom(Theme theme)
    {
        var name = theme.Name.Trim();
        if (name.Length == 0) return false;
        if (BuiltInThemes.All.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return false;
        theme.Name = name;

        WriteTheme(theme);

        _display.ThemeName = name;
        ThemeApplied?.Invoke();
        return true;
    }

    /// <summary>
    /// Copy an existing theme (built-in or custom) to a new custom theme
    /// file WITHOUT activating it — the Display Settings "Duplicate" button.
    /// Overwrites an existing custom of the same name (the name prompt shows
    /// existing names as click-to-overwrite, same as Save Current As…).
    /// Returns null when the source is missing or the name is empty /
    /// reserved by a built-in.
    /// </summary>
    public Theme? Duplicate(string sourceName, string newName)
    {
        var source = Find(sourceName);
        newName = newName.Trim();
        if (source is null || newName.Length == 0) return null;
        if (BuiltInThemes.All.Any(b => b.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var copy = source.Clone(newName);
        WriteTheme(copy);
        return copy;
    }

    /// <summary>
    /// Import theme JSON (a shared/hand-written file) as a custom theme,
    /// WITHOUT activating it. The name is uniquified against every existing
    /// theme — an import can never overwrite anything. Null = unparseable.
    /// </summary>
    public Theme? Import(string json)
    {
        var theme = Theme.FromJson(json);
        if (theme is null) return null;
        if (string.IsNullOrWhiteSpace(theme.Name)) theme.Name = "Imported Theme";
        theme.Name = UniqueName(theme.Name.Trim());
        WriteTheme(theme);
        return theme;
    }

    /// <summary>Write a theme (built-in or custom) to an arbitrary path for sharing.</summary>
    public bool ExportTo(string name, string destPath)
    {
        var theme = Find(name);
        if (theme is null) return false;
        try { File.WriteAllText(destPath, theme.ToJson()); return true; }
        catch { return false; }
    }

    private string UniqueName(string baseName)
    {
        if (Find(baseName) is null) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (Find(candidate) is null) return candidate;
        }
    }

    private void WriteTheme(Theme theme)
    {
        Directory.CreateDirectory(_themesDir);
        File.WriteAllText(PathFor(theme.Name), theme.ToJson());
        ReloadCustomThemes();
    }

    /// <summary>Delete a custom theme file. Built-ins are refused.</summary>
    public bool Delete(string name)
    {
        var theme = Find(name);
        if (theme is null || theme.IsBuiltIn) return false;
        try { File.Delete(PathFor(theme.Name)); } catch { return false; }
        ReloadCustomThemes();
        return true;
    }

    private string PathFor(string name)
    {
        // Keep file names filesystem-safe while preserving the display name
        // inside the JSON payload.
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_themesDir, safe + ".json");
    }
}
