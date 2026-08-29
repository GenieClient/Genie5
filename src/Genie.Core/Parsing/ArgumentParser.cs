namespace Genie.Core.Parsing;

/// <summary>
/// Tokenises a Genie 4 command line into argv-style parts.
///
/// Recognises three token shapes:
/// <list type="bullet">
/// <item><c>bare</c> — whitespace-delimited.</item>
/// <item><c>"quoted"</c> — double quotes group spaces; outer quotes are stripped.</item>
/// <item><c>{braced}</c> — Genie 4's canonical grouping; balanced nesting allowed
///   (<c>{outer {inner} more}</c> is one token whose value is
///   <c>outer {inner} more</c>); only the outermost pair is stripped.</item>
/// </list>
/// Brace grouping is what scripts and saved <c>*.cfg</c> files rely on, so it
/// must survive a save-then-load round-trip without splitting on the spaces
/// inside the braces.
/// </summary>
public static class ArgumentParser
{
    public static IReadOnlyList<string> ParseArgs(string text)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var current   = new System.Text.StringBuilder();
        var inQuotes  = false;
        var braceDepth = 0;
        // True once the current token opened a quote/brace group — an
        // explicitly grouped EMPTY token ("" or {}) must survive as an empty
        // arg (Genie 4 parity). mm_train passes "" as a placeholder gosub arg
        // (`gosub Menu.Build … "" "Moonmage Training Menu"`); dropping it
        // shifted every later $-arg left, so $5 (the window name) came up
        // empty and the menu redraw cleared the main Game window.
        var grouped   = false;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            // Inside a quoted run, only " and { tracking matter (and {…} are
            // literal characters that don't open groups inside quotes).
            if (inQuotes)
            {
                if (ch == '"') { inQuotes = false; continue; }
                current.Append(ch);
                continue;
            }

            // Inside a braced group, only nesting matters; spaces stay
            // intact and quotes are literal.
            if (braceDepth > 0)
            {
                if (ch == '{') { braceDepth++; current.Append(ch); continue; }
                if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0) continue;   // strip outermost
                    current.Append(ch);
                    continue;
                }
                current.Append(ch);
                continue;
            }

            // Outside any group: handle openers, separators, and literals.
            if (ch == '"') { inQuotes = true; grouped = true; continue; }
            if (ch == '{') { braceDepth = 1; grouped = true; continue; }   // strip outermost
            if (ch == ' ' || ch == '\t')
            {
                if (current.Length > 0 || grouped)
                {
                    results.Add(current.ToString());
                    current.Clear();
                    grouped = false;
                }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0 || grouped) results.Add(current.ToString());
        return results;
    }

    /// <summary>
    /// The RAW text of everything from the (<paramref name="skipTokens"/>+1)-th
    /// token onward — quoting and bracing intact (public #300).
    /// <see cref="ParseArgs"/> strips quote/brace grouping per token, so
    /// rebuilding a value with <c>string.Join(" ", parts.Skip(n))</c> destroys
    /// the quotes an embedded <c>#eval replacere("a b"," ","_")</c> needs to
    /// evaluate; value-position callers slice the original text instead.
    /// Tokenization mirrors <see cref="ParseArgs"/> exactly (quotes group,
    /// braces nest, whitespace separates). Returns "" when the text has no
    /// further tokens.
    /// </summary>
    public static string RawTail(string text, int skipTokens)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var inQuotes   = false;
        var braceDepth = 0;
        var inToken    = false;
        var tokens     = 0;

        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"') inQuotes = false;
                continue;
            }
            if (braceDepth > 0)
            {
                if      (ch == '{') braceDepth++;
                else if (ch == '}') braceDepth--;
                continue;
            }
            if (ch == ' ' || ch == '\t')
            {
                if (inToken) { tokens++; inToken = false; }
                continue;
            }
            if (!inToken)
            {
                if (tokens == skipTokens) return text[i..].TrimEnd();
                inToken = true;
            }
            if      (ch == '"') inQuotes   = true;
            else if (ch == '{') braceDepth = 1;
        }
        return string.Empty;
    }

    /// <summary>
    /// Split a command line on <paramref name="separator"/> (default <c>;</c>) the
    /// way Genie 4's <c>Utility.SafeSplit</c> does (#132): a separator delimits a
    /// command only when it is NOT escaped by a preceding backslash and NOT inside
    /// a double-quoted run or a <c>{…}</c> brace group.
    /// <para>
    /// The escaping backslash is <b>preserved</b> in the output — Genie 4 does not
    /// unescape here — so <c>#var t a\;b</c> yields the single segment <c>a\;b</c>
    /// (and <c>$t</c> reads back with the backslash intact, matching Genie 4).
    /// <c>\\</c> is a literal backslash and resets the escape state. Empty segments
    /// are returned as-is; callers trim / skip them.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SafeSplit(string input, char separator)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(input)) return results;

        var current    = new System.Text.StringBuilder(input.Length);
        var inQuotes   = false;
        var braceDepth = 0;
        var escaped    = false;   // previous char was an unconsumed backslash

        foreach (var ch in input)
        {
            if (inQuotes)
            {
                current.Append(ch);
                if (ch == '"') inQuotes = false;
            }
            else if (ch == '"' && !escaped) { current.Append(ch); inQuotes = true; }
            else if (ch == '{' && !escaped) { braceDepth++; current.Append(ch); }
            else if (ch == '}' && !escaped) { if (braceDepth > 0) braceDepth--; current.Append(ch); }
            else if (ch == separator && !escaped && braceDepth == 0)
            {
                results.Add(current.ToString());
                current.Clear();
            }
            else current.Append(ch);

            // A backslash toggles escape (so `\\` cancels itself); anything else
            // clears it. Matches Genie 4's bPreviousWasEscapeChar handling.
            escaped = ch == '\\' && !escaped;
        }

        results.Add(current.ToString());
        return results;
    }
}
