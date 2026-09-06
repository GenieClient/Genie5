using System;
using System.IO;

namespace Genie.Core.Update;

/// <summary>
/// Resolves a remote-supplied filename to a local path, or refuses it.
///
/// <para>Every updater writes files whose names come from a remote feed — a GitHub
/// tree listing, a release asset. <c>Path.Combine(baseDir, name)</c> does not
/// constrain the result to <c>baseDir</c>: <c>"../../evil"</c> climbs out of it, and
/// an ABSOLUTE second argument discards the base entirely and returns the absolute
/// path unchanged. A hostile or compromised source could therefore choose where its
/// bytes land. For the plugin feed that is remote code execution, since the file it
/// writes is a DLL the same routine then loads.</para>
///
/// <para>Two shapes are supported, because the feeds genuinely differ:</para>
/// <list type="bullet">
///   <item><b>Flat</b> (maps, plugins) — the name must be a bare filename. These
///     directories are flat by design; the maps updater enumerates its folder
///     top-level-only and the plugin loader scans a single directory.</item>
///   <item><b>Nested</b> (scripts) — relative subfolders are allowed, because a
///     pulled script repo mirrors its own folder structure.</item>
/// </list>
///
/// <para>Refusal, not repair. Silently rewriting <c>../../evil.xml</c> to
/// <c>evil.xml</c> would keep a hostile feed working while writing a file nobody
/// asked for; returning false lets the caller report it, which is what a feed
/// serving traversal paths deserves.</para>
/// </summary>
public static class FeedPath
{
    /// <summary>Resolve <paramref name="name"/> under <paramref name="baseDir"/>,
    /// or return false if it would land anywhere else.</summary>
    /// <param name="allowSubdirectories">Whether relative subfolders are legitimate
    /// for this feed. False requires a bare filename.</param>
    public static bool TryResolveUnder(string baseDir, string? name,
                                       bool allowSubdirectories, out string localPath)
    {
        localPath = "";
        if (string.IsNullOrWhiteSpace(name)) return false;

        // A drive-qualified or rooted name would win the Combine outright.
        if (Path.IsPathRooted(name) || name.Contains(':')) return false;

        // Normalize BOTH separators before anything looks at the shape. On Linux a
        // backslash is an ordinary filename character, so a name like "..\..\evil.xml" arrives
        // as one long legal name and neither GetFileName nor the containment check
        // sees a traversal — it would be refused on Windows and quietly accepted on
        // Linux. No legitimate feed name contains a backslash, so treating it as a
        // separator everywhere costs nothing and makes the two platforms agree.
        var relative = name.Replace('\\', Path.DirectorySeparatorChar)
                           .Replace('/',  Path.DirectorySeparatorChar);

        if (!allowSubdirectories)
        {
            // Bare filename only: these feeds are flat, so any directory structure
            // in the name means the feed is not what this updater expects.
            if (relative.IndexOf(Path.DirectorySeparatorChar) >= 0) return false;
            if (relative is "." or "..") return false;
        }

        // Segment check, ahead of the containment test. A ".." that cancels out
        // ("sub/../file") still tells us the feed is composing paths it has no
        // business composing, and refusing it keeps the accepted set small.
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            if (segment is ".." or ".") return false;

        string root, full;
        try
        {
            root = Path.GetFullPath(baseDir);
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch
        {
            // Malformed enough that the framework won't even normalize it.
            return false;
        }

        // The containment check proper. Compared with the separator appended so a
        // sibling directory sharing a prefix ("Maps2" against "Maps") cannot pass.
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        localPath = full;
        return true;
    }
}
