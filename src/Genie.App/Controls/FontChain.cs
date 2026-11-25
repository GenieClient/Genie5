using System;
using System.Collections.Concurrent;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Genie.App.Controls;

/// <summary>
/// Turns a font setting such as <c>Cascadia Mono,Consolas,Courier New,monospace</c>
/// into the first font in the list that is actually installed (public #395).
///
/// <para>Avalonia's own handling of a comma list only matches names against the
/// installed families, so a generic name like <c>monospace</c> at the end never
/// resolves: on Linux, where neither Consolas nor Courier New is installed, the
/// whole list fell back to the proportional system font (Noto Sans on Ubuntu)
/// and DR's <c>&lt;output class="mono"&gt;</c> tables (EXP, maps, appraisals) lost
/// their columns. A single generic name, on the other hand, is passed to the
/// platform, which on Linux asks fontconfig and gets the real monospace font.</para>
///
/// <para>So each name is tried on its own, in order: a named family counts only
/// if that family is what comes back (not the default it falls back to), and
/// <c>monospace</c> counts only if what comes back is fixed-pitch. The result is
/// the first installed font followed by the other installed fonts in the list,
/// so characters the first one lacks still fall back along the user's list. If
/// nothing in the list resolves, the result is the platform's default font by
/// name (see <see cref="RendererList"/>, public #393). Results are cached per
/// setting string.</para>
///
/// <para>A family must be written the way the system names it: a localized or
/// aliased name the font manager doesn't report is treated as not installed.</para>
/// </summary>
public static class FontChain
{
    private static readonly ConcurrentDictionary<string, FontFamily> Cache = new(StringComparer.Ordinal);

    /// <summary>Binding converter for font pickers: a font name to its
    /// <see cref="FontFamily"/>, so each entry previews in its own font. A plain
    /// <c>FontFamily="{Binding}"</c> on a string logs "Could not convert" and
    /// previews nothing.</summary>
    public static readonly IValueConverter NameToFamily =
        new FuncValueConverter<string?, FontFamily?>(n => string.IsNullOrWhiteSpace(n) ? null : new FontFamily(n));

    /// <summary>The installed font for <paramref name="setting"/>; see the class remarks.</summary>
    public static FontFamily Resolve(string setting)
    {
        var key = setting.Trim();
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var family = Build(key);
        // Only remember an answer the platform font manager gave: one worked out
        // before it is up (a static initialiser at start-up) must not stick.
        if (FontManagerReady()) Cache[key] = family;
        return family;
    }

    private static bool FontManagerReady()
    {
        try { _ = FontManager.Current; return true; }
        catch (Exception) { return false; }
    }

    /// <summary>The family name <paramref name="setting"/> resolves to, or null when
    /// nothing in it is installed. For tests and diagnostics.</summary>
    public static string? ResolvedName(string setting)
    {
        foreach (var name in Names(setting.Trim()))
            if (TryInstalled(name, out var family)) return family;
        return null;
    }

    private static FontFamily Build(string setting)
    {
        // An embedded/resource font ("avares://…#Name", "fonts:…") is Avalonia's
        // own syntax, not a list of system names: leave it alone.
        if (setting.Contains('#') || setting.Contains(':'))
            return new FontFamily(setting);

        return new FontFamily(RendererList(
            setting,
            name => TryInstalled(name, out var family) ? family : null,
            DefaultFamilyName));
    }

    /// <summary>What the renderer is handed for <paramref name="setting"/>: its
    /// installed families, or — when none of them is installed — the platform's
    /// default family by name (public #393). The setting is passed through as
    /// written only when even that is unknown (no font manager yet).
    ///
    /// <para>The default family is the font such a setting was already drawn in,
    /// so nothing looks different. What changes is that the renderer is given a
    /// name it can find: handed a list with no installed family (a lone
    /// <c>Cascadia Mono</c> in settings synced from Windows), Avalonia on Linux
    /// loaded the fallback font's data again on every lookup and never freed it,
    /// about 25 KB per line of game text.</para></summary>
    internal static string RendererList(string setting, Func<string, string?> lookup, Func<string?> defaultFamily)
    {
        var installed = InstalledNames(setting, lookup);
        if (installed.Count > 0) return string.Join(", ", installed);
        return defaultFamily() is { Length: > 0 } fallback ? fallback : setting;
    }

    /// <summary>The concrete family the platform falls back to (Noto Sans on
    /// Ubuntu), or null when it cannot be asked.</summary>
    private static string? DefaultFamilyName()
    {
        try
        {
            var manager = FontManager.Current;
            if (manager.TryGetGlyphTypeface(Typeface.Default, out var face) &&
                !string.IsNullOrWhiteSpace(face?.FamilyName))
                return face.FamilyName;

            // "$Default" is Avalonia's placeholder, not a family the platform knows.
            var name = manager.DefaultFontFamily.Name;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith('$') ? null : name;
        }
        catch (Exception) { return null; }   // no platform font manager (early start-up)
    }

    /// <summary>The installed families in <paramref name="setting"/>, in order,
    /// starting at the first one that resolves. Handing the renderer the later
    /// installed names too keeps per-character fallback to them for glyphs the
    /// first font lacks (#422 review). Names that aren't installed are left out:
    /// a missing family in the list is what made Avalonia reload fallback font
    /// data on every lookup on Linux.</summary>
    internal static IReadOnlyList<string> InstalledNames(string setting)
        => InstalledNames(setting, name => TryInstalled(name, out var family) ? family : null);

    /// <summary><see cref="InstalledNames(string)"/> with the font lookup supplied
    /// (the installed family a name resolves to, or null) — for tests.</summary>
    internal static IReadOnlyList<string> InstalledNames(string setting, Func<string, string?> lookup)
    {
        var result = new List<string>();
        foreach (var name in Names(setting.Trim()))
            if (lookup(name) is { } family &&
                !result.Contains(family, StringComparer.OrdinalIgnoreCase))
                result.Add(family);
        return result;
    }

    private static string[] Names(string setting)
        => setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool TryInstalled(string name, out string family)
    {
        family = "";
        IGlyphTypeface? face;
        try
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(name), out face) || face is null)
                return false;
        }
        catch (Exception) { return false; }   // no platform font manager (early start-up)

        if (string.Equals(name, "monospace", StringComparison.OrdinalIgnoreCase))
        {
            if (!face.Metrics.IsFixedPitch) return false;   // macOS answers with Helvetica
        }
        else if (!string.Equals(face.FamilyName, name, StringComparison.OrdinalIgnoreCase))
        {
            return false;                                     // not installed: the default came back
        }

        family = face.FamilyName;
        return true;
    }
}
