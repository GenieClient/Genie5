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
/// <c>monospace</c> counts only if what comes back is fixed-pitch. If nothing in
/// the list resolves, the setting is used as written, which is what Genie did
/// before. Results are cached per setting string.</para>
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

        return ResolvedName(setting) is { } family
            ? new FontFamily(family)
            : new FontFamily(setting);
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
