namespace Genie.App.Views;

/// <summary>
/// Shared case-insensitive substring filter used by the config editor panels
/// (Aliases / Triggers / Highlights / Substitutes / Gags / Variables / Macros /
/// Names / Classes). After an import a panel can hold hundreds of rules, so each panel
/// has a "Find…" box that runs its rows through <see cref="Matches"/> against
/// the relevant text columns.
/// </summary>
internal static class PanelFilterHelpers
{
    /// <summary>
    /// True when <paramref name="filter"/> is blank, or when any of the supplied
    /// <paramref name="fields"/> contains it (case-insensitive). A blank filter
    /// matches everything so the list shows in full.
    /// </summary>
    public static bool Matches(string? filter, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        foreach (var f in fields)
            if (!string.IsNullOrEmpty(f) &&
                f.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
