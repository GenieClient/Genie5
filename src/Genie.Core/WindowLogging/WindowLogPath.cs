using System.Text;

namespace Genie.Core.WindowLogging;

/// <summary>What a window-log filename template is expanded against: the
/// session identity and the moment the line arrived.</summary>
public readonly record struct WindowLogContext(
    string CharacterName, string GameName, string Stream, DateTime Time);

/// <summary>
/// Filename templates for per-window logs (public #270, Genie 4 Window Logger
/// parity) and the rule that keeps every expanded path inside the Logs folder.
///
/// <para><b>Tokens.</b> <c>{charactername}</c> (alias <c>{character}</c>),
/// <c>{gamename}</c> (alias <c>{game}</c>; both the instance code such as
/// <c>DR</c>, the same value <c>$game</c>/<c>$gamename</c> carry), <c>{stream}</c>,
/// and the date parts <c>{yyyy}</c>, <c>{yy}</c>, <c>{MM}</c>, <c>{dd}</c>. The
/// named tokens ignore case; the date tokens are case-exact, since <c>MM</c>
/// (month) and <c>mm</c> (minute) mean different things in .NET date formats.
/// The date is taken per line, so a <c>{yyyy}</c> file rolls over at the new
/// year without a reconnect.</para>
///
/// <para><b>Paths.</b> Same convention as <c>#log &gt;file</c>: a name is
/// relative to the configured Logs folder (<c>GenieConfig.LogDir</c>), and it
/// may contain folders. A leading <c>\</c> or <c>/</c> — the form the Genie 4
/// plugin's shipped config used (<c>\GenieWindows\Thoughts\…</c>) — is read as
/// Logs-relative too. Unlike <c>#log</c>, which is typed per line and trusted,
/// a template is stored and fires on server text, so it is confined: an
/// absolute path, a drive, or any <c>..</c> segment is refused, and token
/// values are sanitized so a value cannot introduce a folder.</para>
/// </summary>
public static class WindowLogPath
{
    /// <summary>Template used by <c>#windowlog add &lt;stream&gt;</c> with no file.</summary>
    public const string DefaultTemplate = @"{stream}\{stream}-{charactername}-{yyyy}.txt";

    /// <summary>The Genie 4 Window Logger's timestamp format, the default here.</summary>
    public const string DefaultTimestampFormat = "yyyy-MM-dd HH:mm";

    private static readonly string[] DateTokens = ["yyyy", "yy", "MM", "dd"];

    /// <summary>Substitute every known token. Unknown <c>{…}</c> runs are left as
    /// literal text (see <see cref="UnknownTokens"/> to warn about them).</summary>
    public static string Expand(string template, WindowLogContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        var sb = new StringBuilder(template.Length + 32);
        int i = 0;
        while (i < template.Length)
        {
            var ch = template[i];
            if (ch == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i)
                {
                    var name = template.Substring(i + 1, close - i - 1);
                    var value = TokenValue(name, ctx);
                    if (value is not null)
                    {
                        sb.Append(value);
                        i = close + 1;
                        continue;
                    }
                }
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary><c>{…}</c> names in <paramref name="template"/> that are not tokens.</summary>
    public static IReadOnlyList<string> UnknownTokens(string template)
    {
        var sample = new WindowLogContext("x", "x", "x", DateTime.Now);
        var unknown = new List<string>();
        int i = 0;
        while ((i = template.IndexOf('{', i)) >= 0)
        {
            int close = template.IndexOf('}', i + 1);
            if (close < 0) break;
            var name = template.Substring(i + 1, close - i - 1);
            if (TokenValue(name, sample) is null) unknown.Add(name);
            i = close + 1;
        }
        return unknown;
    }

    private static string? TokenValue(string name, WindowLogContext ctx)
    {
        foreach (var d in DateTokens)
            if (string.Equals(name, d, StringComparison.Ordinal))
                return ctx.Time.ToString(d, System.Globalization.CultureInfo.InvariantCulture);

        switch (name.ToLowerInvariant())
        {
            case "charactername":
            case "character":     return SafeValue(ctx.CharacterName, "unknown");
            case "gamename":
            case "game":          return SafeValue(ctx.GameName, "");
            case "stream":        return SafeValue(ctx.Stream, "main");
            default:              return null;
        }
    }

    /// <summary>A token value made safe for one path segment: no separators, no
    /// invalid filename characters, never <c>.</c> or <c>..</c>.</summary>
    private static string SafeValue(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = SanitizeSegment(value.Trim());
        return v is "." or ".." ? v.Replace('.', '_') : v;
    }

    private static string SanitizeSegment(string segment)
    {
        var sb = new StringBuilder(segment.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in segment)
            sb.Append(c == '/' || c == '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>
    /// Place an expanded name under <paramref name="logDir"/>. Returns the full
    /// path, or null with <paramref name="error"/> set when the name is empty,
    /// absolute, or would leave the Logs folder.
    /// </summary>
    public static string? Resolve(string logDir, string expanded, out string? error)
    {
        error = null;
        var name = (expanded ?? string.Empty).Trim().TrimStart('\\', '/');
        if (name.Length == 0) { error = "the file name is empty"; return null; }
        if (Path.IsPathRooted(name) || name.Contains(':'))
        {
            error = "absolute paths and drives are not allowed; paths are relative to the Logs folder";
            return null;
        }

        var segments = name.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>(segments.Length);
        foreach (var raw in segments)
        {
            var seg = raw.Trim();
            if (seg is "." or "..")
            {
                error = "'..' and '.' are not allowed; the log must stay inside the Logs folder";
                return null;
            }
            if (seg.Length == 0) continue;
            clean.Add(SanitizeSegment(seg));
        }
        if (clean.Count == 0) { error = "the file name is empty"; return null; }

        string root, full;
        try
        {
            root = Path.GetFullPath(logDir);
            full = Path.GetFullPath(Path.Combine([root, .. clean]));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }

        // Belt and braces: whatever the segments said, the result must sit
        // strictly below the Logs folder.
        var rootWithSep = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSep, cmp))
        {
            error = "the path leaves the Logs folder";
            return null;
        }
        return full;
    }

    /// <summary>Format <paramref name="time"/> with a rule's timestamp format, or
    /// null when the format is empty (no timestamp) or unusable.</summary>
    public static string? FormatTimestamp(string? format, DateTime time)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        try { return time.ToString(format, System.Globalization.CultureInfo.InvariantCulture); }
        catch (FormatException) { return null; }
    }
}
