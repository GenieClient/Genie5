namespace Genie.Core.Persistence;

/// <summary>
/// The single precedence rule for two-layer rule config (public #257):
/// Character (profile) entries first, then Global entries whose key isn't
/// shadowed by a Character entry. Pure functions — parsing stays at the call
/// site on purpose (connect tolerates a corrupt file; live-reload must throw
/// before clearing anything).
/// </summary>
public static class ScopedRuleLoader
{
    /// <summary>
    /// Layer two same-type rule lists into the order the engines must apply
    /// them: every <paramref name="character"/> item (tagged
    /// <see cref="RuleScope.Character"/>) followed by each
    /// <paramref name="global"/> item (tagged <see cref="RuleScope.Global"/>)
    /// whose <paramref name="key"/> no character item carries. Keys compare
    /// case-insensitively. Duplicate keys WITHIN a list are preserved (the
    /// engines allow them); shadowing is only across layers — one character
    /// entry hides every same-key global entry. For upsert-style engines the
    /// same order works unchanged: each key is applied exactly once.
    /// </summary>
    public static List<(T Item, RuleScope Scope)> Layer<T>(
        IEnumerable<T>       character,
        IEnumerable<T>       global,
        Func<T, string>      key)
    {
        var result = new List<(T, RuleScope)>();
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in character)
        {
            result.Add((item, RuleScope.Character));
            shadowed.Add(key(item) ?? string.Empty);
        }
        foreach (var item in global)
            if (!shadowed.Contains(key(item) ?? string.Empty))
                result.Add((item, RuleScope.Global));
        return result;
    }

    /// <summary>
    /// True when the profile dir IS the global dir (an ad-hoc / profile-less
    /// connection, or tests pointing both at one folder). There is only one
    /// layer then — callers load the single file and tag everything
    /// <see cref="RuleScope.Global"/> so saves keep writing the one file.
    /// </summary>
    public static bool SameDirectory(string profileDir, string globalDir)
    {
        if (string.IsNullOrWhiteSpace(profileDir) || string.IsNullOrWhiteSpace(globalDir))
            return true;
        try
        {
            return string.Equals(
                Path.GetFullPath(profileDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(globalDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(profileDir, globalDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Both candidate paths for a rule file, profile first.</summary>
    public static (string ProfilePath, string GlobalPath) Paths(
        string profileDir, string globalDir, string fileName)
        => (Path.Combine(profileDir, fileName), Path.Combine(globalDir, fileName));
}
