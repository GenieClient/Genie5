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
}
