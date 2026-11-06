namespace Genie.Core.Scripting;

/// <summary>
/// Case-insensitive file lookup for script names (public #392).
///
/// <para>Genie 4 never had to think about case: <c>.script</c> found
/// <c>Script.cmd</c> because NTFS compared the name for it. Every Genie 5
/// lookup kept that shape — build the exact path from what the player typed
/// and test it with <see cref="File.Exists(string)"/> — which still works on
/// Windows and macOS but not on ext4, where <c>.script</c>, <c>#script</c>,
/// <c>include Lib.inc</c> and <c>include lib.js</c> all failed with
/// "not found" against a file that was plainly there.</para>
///
/// <para><see cref="Find"/> keeps the exact-case probe as the fast path (and
/// the only path that ever runs on a case-insensitive filesystem) and, when
/// it misses, walks the path one segment at a time, matching each missing
/// segment against the directory listing ordinal-ignore-case. Sub-paths
/// (<c>GenieHunter/hunt</c>) are covered because every segment is matched,
/// not just the file name. When two files differ only by case the exact
/// spelling wins; otherwise the ordinal-first candidate is taken so the
/// answer is deterministic.</para>
/// </summary>
public static class ScriptFileProbe
{
    /// <summary>
    /// The on-disk path of <paramref name="relative"/> under
    /// <paramref name="dir"/> (a rooted <paramref name="relative"/> is used
    /// as-is, exactly as <see cref="Path.Combine(string, string)"/> does),
    /// matched case-insensitively when the exact-case path does not exist.
    /// Null when no file matches.
    /// </summary>
    public static string? Find(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;

        string combined;
        try { combined = Path.Combine(dir, relative); }
        catch (ArgumentException) { return null; }

        // Exact-case fast path — the whole story on Windows/macOS.
        if (File.Exists(combined)) return combined;

        return FindIgnoreCase(combined);
    }

    private static string? FindIgnoreCase(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return null;

        var segments = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var current = root;
        for (int i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var exact  = Path.Combine(current, segments[i]);
            if (isLast ? File.Exists(exact) : Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            string? match;
            try
            {
                var entries = isLast ? Directory.EnumerateFiles(current)
                                     : Directory.EnumerateDirectories(current);
                match = entries
                    .Where(e => string.Equals(Path.GetFileName(e), segments[i], StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null) return null;
            current = match;
        }

        return current;
    }
}
