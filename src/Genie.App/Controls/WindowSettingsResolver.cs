using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Genie.App.Settings;

namespace Genie.App.Controls;

/// <summary>
/// Helpers for resolving per-window settings against the global
/// <see cref="DisplaySettings"/> fallbacks.
///
/// <para>
/// <b>Architecture (Option A — per-window overrides global only when set):</b>
/// </para>
/// <list type="bullet">
///   <item><b>Foreground</b>: <c>"Default"</c> sentinel → fall back to
///         <c>Application.Resources["GameBrush"]</c>.</item>
///   <item><b>Background</b>: <c>""</c> sentinel → transparent
///         (the historical "None" semantic).</item>
///   <item><b>Font family</b>: empty sentinel → fall back to
///         <c>Application.Resources["GameFontFamily"]</c>.</item>
///   <item><b>Font size</b>: zero (or non-positive) sentinel → fall back to
///         <c>Application.Resources["GameFontSize"]</c>.</item>
/// </list>
///
/// <para>
/// The global values are pushed into <see cref="Application.Resources"/> by
/// <see cref="DisplaySettings.Apply"/> at app startup (and on every change
/// to the underlying settings), so reading them at tool-construction time
/// is reliable. The current architecture doesn't auto-update an
/// already-constructed tool when the global changes — tools read their
/// settings once. Closing + reopening the window picks up the new global
/// value. Full reactive flow is a follow-up.
/// </para>
///
/// <para>
/// Also includes UI helpers (<see cref="LoadString"/> / <see cref="ReadString"/>
/// and the numeric pair) for binding a <see cref="TextBox"/> + "Use default"
/// <see cref="CheckBox"/> pair to a sentinel-keyed string value, mirroring
/// the existing <see cref="ColorPickerHelpers"/> pattern for colour pickers.
/// </para>
/// </summary>
internal static class WindowSettingsResolver
{
    // ── Resolvers (per-window value → resolved live value) ────────────────────

    /// <summary>
    /// Resolve a per-window font-family string to a live <see cref="FontFamily"/>.
    /// Empty or whitespace → falls back to the global <c>GameFontFamily</c>
    /// resource; the resource is itself seeded with the
    /// <see cref="DisplaySettings.FontFamily"/> default
    /// (<c>Cascadia Mono,Consolas,Courier New,monospace</c>) by
    /// <see cref="DisplaySettings.Apply"/>.
    /// </summary>
    public static FontFamily ResolveFontFamily(string raw)
    {
        if (!string.IsNullOrWhiteSpace(raw))
            return FontChain.Resolve(raw);   // first installed font in the list (#395)

        if (Application.Current?.Resources[DisplaySettings.GameFontFamilyKey]
                is FontFamily resourceFamily)
            return FontChain.Resolve(string.Join(",", resourceFamily.FamilyNames));   // #395

        // Hard fallback if resources haven't been pushed yet (shouldn't
        // happen in practice — Display.Apply() runs before tools build —
        // but defensive against init-order surprises).
        return FontChain.Resolve("Cascadia Mono,Consolas,Courier New,monospace");
    }

    /// <summary>
    /// Resolve a per-window font-size value to a live size. Zero or
    /// negative → falls back to the global <c>GameFontSize</c> resource
    /// (default <c>13</c>).
    /// </summary>
    public static double ResolveFontSize(double raw)
    {
        if (raw > 0) return raw;

        if (Application.Current?.Resources[DisplaySettings.GameFontSizeKey]
                is double resourceSize && resourceSize > 0)
            return resourceSize;

        return 13d;
    }

    /// <summary>
    /// Resolve a per-window foreground string to a live <see cref="IBrush"/>.
    /// Empty / "Default" / "(none)" sentinels fall back to the global
    /// <c>GameBrush</c> resource.
    /// </summary>
    public static IBrush ResolveForeground(string raw)
    {
        if (ColorPickerHelpers.ParseBrush(raw) is { } explicitBrush)
            return explicitBrush;

        if (Application.Current?.Resources[DisplaySettings.GameBrushKey]
                is IBrush resourceBrush)
            return resourceBrush;

        return Brushes.LightGray;
    }

    /// <summary>
    /// Resolve a per-window background string to a live brush. Empty /
    /// "(none)" → returns null (transparent), matching the historical
    /// "None" semantic in this codebase. No global fallback for background
    /// — null is the intended sentinel for "use the panel's natural
    /// transparent background."
    /// </summary>
    public static IBrush? ResolveBackground(string raw)
        => ColorPickerHelpers.ParseBrush(raw);

    // ── UI helpers for sentinel-string editing (TextBox + CheckBox pair) ────

    /// <summary>
    /// Seed a <see cref="TextBox"/> + "Use default" <see cref="CheckBox"/>
    /// pair from a stored string value. If the stored value is empty or
    /// equals <paramref name="sentinelKeyword"/>, checks the box and
    /// clears the text. Otherwise unchecks the box and populates the
    /// text. Matches the <see cref="ColorPickerHelpers.LoadColor"/> shape.
    /// </summary>
    public static void LoadString(TextBox box, CheckBox sentinel,
                                  string value, string sentinelKeyword = "")
    {
        if (string.IsNullOrEmpty(value)
            || (!string.IsNullOrEmpty(sentinelKeyword)
                && value.Equals(sentinelKeyword, System.StringComparison.OrdinalIgnoreCase)))
        {
            sentinel.IsChecked = true;
            box.Text = string.Empty;
            return;
        }

        sentinel.IsChecked = false;
        box.Text = value;
    }

    /// <summary>
    /// Returns the value to persist back. When the sentinel checkbox is
    /// set, returns the sentinel keyword (default <c>""</c>); otherwise
    /// returns the box's text trimmed.
    /// </summary>
    public static string ReadString(TextBox box, CheckBox sentinel,
                                    string sentinelKeyword = "")
    {
        if (sentinel.IsChecked == true) return sentinelKeyword;
        return box.Text?.Trim() ?? string.Empty;
    }

    /// <summary>Numeric variant of <see cref="LoadString"/>.</summary>
    public static void LoadDouble(TextBox box, CheckBox sentinel,
                                  double value, double sentinelValue = 0d)
    {
        if (value <= sentinelValue)
        {
            sentinel.IsChecked = true;
            box.Text = string.Empty;
            return;
        }

        sentinel.IsChecked = false;
        box.Text = value.ToString("G");
    }

    /// <summary>Numeric variant of <see cref="ReadString"/>.</summary>
    public static double ReadDouble(TextBox box, CheckBox sentinel,
                                    double sentinelValue = 0d, double parseFallback = 0d)
    {
        if (sentinel.IsChecked == true) return sentinelValue;
        return double.TryParse(box.Text?.Trim() ?? "", out var d) ? d : parseFallback;
    }
}
