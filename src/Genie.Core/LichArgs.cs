using System.Linq;
using System.Text;

namespace Genie.Core.Connection;

/// <summary>
/// Tokenizer for the <c>#config lichargs</c> string — shared by
/// <see cref="LichLauncher"/> (building <c>ProcessStartInfo.ArgumentList</c>) and
/// <see cref="LichDebugLogTailer"/> (resolving <c>--temp</c>). Both must agree, or
/// Genie tails a temp directory Lich was never actually given.
/// </summary>
internal static class LichArgs
{
    /// <summary>
    /// Split a Lich argument string into argv entries on whitespace, honouring
    /// single and double quotes so a path with spaces survives as one token
    /// (<c>--temp "/Application Support/lich/temp"</c>, <c>--temp="/a b"</c>).
    /// Quotes are stripped and may open mid-token.
    /// </summary>
    /// <remarks>
    /// Backslash is deliberately <em>not</em> an escape character: Lich args carry
    /// Windows paths (<c>--temp=C:\lich\temp</c>) far more often than they carry
    /// escaped literals, so treating <c>\</c> as an escape would break the common
    /// case to serve the rare one.
    /// </remarks>
    /// <exception cref="FormatException">A quote is never closed. Callers surface
    /// this and abort — a half-parsed argument string would otherwise launch Lich
    /// with mangled argv, and the resulting failure (a missing temp dir, a login
    /// that silently used the wrong character) is far harder to trace back to a
    /// stray quote in <c>#config lichargs</c> than an outright refusal to start.
    /// </exception>
    internal static IReadOnlyList<string> Tokenize(string? arguments)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments)) return tokens;

        var current = new StringBuilder();
        var open = false;      // a token is in progress (may still be empty: `--temp ""`)
        var quote = '\0';
        var quoteAt = -1;

        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];

            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; quoteAt = -1; }
                else current.Append(c);
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                quoteAt = i;
                open = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (open)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    open = false;
                }
                continue;
            }

            current.Append(c);
            open = true;
        }

        if (quote != '\0')
            throw new FormatException(
                $"unterminated {quote} quote in the Lich arguments (opened at position {quoteAt}).");

        if (open) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Directory constants <c>lich.rbw</c> reads from ARGV before loading
    /// <c>constants.rb</c>, in its spelling. All are <c>=</c>-only.
    /// </summary>
    private static readonly string[] DirFlags =
        { "home", "temp", "scripts", "maps", "logs", "backup", "data", "lib" };

    /// <summary>
    /// Flags Lich's own <c>--help</c> advertises that nothing implements, mapped to
    /// the spelling that works. See <c>lib/main/help_text.rb</c> (paths topic) versus
    /// the argument loop in <c>lich.rbw</c>.
    /// </summary>
    private static readonly (string Documented, string Actual)[] UnimplementedAliases =
    {
        ("temp-dir",   "temp"),
        ("script-dir", "scripts"),
        ("data-dir",   "data"),
    };

    /// <summary>
    /// Value of <paramref name="flag"/> (given without <c>=</c>, e.g. <c>--temp</c>)
    /// in its only working form, <c>--flag=VALUE</c>, with trailing separators trimmed.
    /// </summary>
    internal static bool TryParseDirFlag(IReadOnlyList<string> tokens, string flag, out string value)
    {
        value = string.Empty;
        var prefix = flag + "=";

        foreach (var token in tokens)
        {
            if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            value = token[prefix.Length..]
                .Trim()
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (value.Length > 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Find a path argument Lich accepts on the command line but silently discards —
    /// either the space-separated form of a directory flag (its regexes require
    /// <c>=</c>) or one of the aliases only <c>--help</c> believes in.
    /// </summary>
    /// <remarks>
    /// Worth failing a launch over: Lich neither warns nor errors on these, it just
    /// uses default paths, so the user gets a working-looking Lich writing somewhere
    /// they didn't ask for. The <c>--help</c> aliases matter most — a user who reads
    /// the docs and types <c>--temp-dir=…</c> is doing everything right.
    /// </remarks>
    /// <param name="problem">The offending token.</param>
    /// <param name="fix">The spelling that works.</param>
    internal static bool TryFindIgnoredDirFlag(
        IReadOnlyList<string> tokens, out string problem, out string fix)
    {
        problem = string.Empty;
        fix = string.Empty;

        foreach (var token in tokens)
        {
            var name = token.TrimStart('-');
            var eq = name.IndexOf('=');
            var bare = eq < 0 ? name : name[..eq];

            foreach (var (documented, actual) in UnimplementedAliases)
            {
                if (!bare.Equals(documented, StringComparison.OrdinalIgnoreCase)) continue;
                problem = token;
                fix = $"--{actual}=PATH";
                return true;
            }

            // `--temp /path` — Lich's /^--temp=(.+)/ never matches, and it does not
            // look at the next argument either.
            if (eq < 0 && DirFlags.Contains(bare, StringComparer.OrdinalIgnoreCase))
            {
                problem = token;
                fix = $"--{bare.ToLowerInvariant()}=PATH";
                return true;
            }
        }

        return false;
    }
}
