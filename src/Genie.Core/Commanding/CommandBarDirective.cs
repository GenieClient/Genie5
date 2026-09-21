namespace Genie.Core.Commanding;

/// <summary>
/// Genie 4's two command-bar directives in SENT text: <c>@</c> (place the text
/// in the command input with the caret where the marker was) and <c>\x</c>
/// (clear the input first). Public #348.
///
/// <para><b>Why this lives at the send sink.</b> Genie 4 gates it in
/// <c>ClassCommand_SendText</c> — the one funnel every origin passes through —
/// so a macro, a trigger action, an alias expansion and a script <c>put</c> all
/// behave the same way. Putting it anywhere else would make <c>#macro {F1}
/// {look @}</c> work while the identical text from a trigger sent a literal
/// <c>look @</c> to DragonRealms.</para>
///
/// <para><b>Why it matters more than it looks.</b> The stock <c>macros.cfg</c>
/// in the reference settings tree ships <c>#macro {F1} {look @}</c>. Without
/// this, anyone importing a Genie 4 settings folder pressed F1 and got "Please
/// rephrase that command." on their very first keypress.</para>
///
/// <para><b>One deliberate divergence.</b> Genie 4 handles the escape as
/// <c>sText.Replace(@"\@", "x")</c> — it rewrites an escaped marker to a bare
/// letter <c>x</c>, which suppresses the diversion but also corrupts the text
/// that reaches the game (<c>look \@home</c> would arrive as <c>look xhome</c>).
/// We implement the documented INTENT instead: <c>\@</c> is an escaped literal
/// <c>@</c>, it does not trigger the diversion, and it reaches the game as a
/// plain <c>@</c>.</para>
/// </summary>
public static class CommandBarDirective
{
    /// <summary>What <see cref="TryParse"/> resolved a line to.</summary>
    public readonly record struct Result(
        /// <summary>Text to place in the command bar.</summary>
        string Text,
        /// <summary>Caret offset within <see cref="Text"/> — where the <c>@</c>
        /// was, or the end when there was no marker.</summary>
        int CaretIndex,
        /// <summary>True for <c>\x</c>: replace whatever the bar holds rather
        /// than inserting into it.</summary>
        bool ClearFirst);

    private const string ClearToken   = @"\x";
    private const string EscapedMark  = @"\@";
    private const char   Mark         = '@';

    /// <summary>
    /// Resolve a line that is on its way to the game. Returns false when it
    /// carries no directive — the overwhelmingly common case — and the caller
    /// sends it unchanged. Returns true with a <see cref="Result"/> when the
    /// line belongs in the command bar and must NOT reach the game.
    /// </summary>
    public static bool TryParse(string? text, out Result result)
    {
        result = default;
        if (string.IsNullOrEmpty(text)) return false;

        // Cheap reject first: no directive characters at all, no work. Every
        // ordinary command takes this path.
        if (text.IndexOf(Mark) < 0 && !text.Contains(ClearToken, StringComparison.Ordinal))
            return false;

        var clearFirst = text.Contains(ClearToken, StringComparison.Ordinal);

        // Walk once, resolving escapes as we go so a `\@` neither triggers the
        // diversion nor survives into the sent text as a backslash.
        var sb     = new System.Text.StringBuilder(text.Length);
        var caret  = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && text[i] == '\\')
            {
                if (text[i + 1] == Mark) { sb.Append(Mark); i++; continue; }   // \@ → literal @
                if (text[i + 1] == 'x')  { i++; continue; }                    // \x → consumed
            }
            if (text[i] == Mark && caret < 0) { caret = sb.Length; continue; } // the marker itself
            sb.Append(text[i]);
        }

        // `\x` alone is a directive; `\@` alone is not. If every marker was
        // escaped and there was no clear token, this is ordinary text — but it
        // still needed unescaping, which the caller cannot do without knowing
        // the grammar, so report it as "not a directive" only after checking.
        if (caret < 0 && !clearFirst) return false;

        var resolved = sb.ToString();
        result = new Result(resolved, caret < 0 ? resolved.Length : caret, clearFirst);
        return true;
    }

    /// <summary>
    /// Strip escapes from a line that is NOT a directive, so <c>look \@home</c>
    /// reaches the game as <c>look @home</c>. Returns the input unchanged when
    /// it carries no escape, which is the common path.
    /// </summary>
    public static string Unescape(string text) =>
        text.Contains(EscapedMark, StringComparison.Ordinal)
            ? text.Replace(EscapedMark, "@", StringComparison.Ordinal)
            : text;
}
