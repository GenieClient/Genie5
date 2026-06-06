namespace Genie.Core.Scripting;

public static class ScriptParser
{
    public static ScriptInstance Parse(string name, string scriptsDir, string source)
    {
        var inst = new ScriptInstance { Name = name };

        // 1. Recursive include expansion → flat raw line list.
        var raw = new List<(string Origin, int LineNo, string Raw)>();
        ExpandIncludes(name, source, scriptsDir, raw,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        // 2. Normalise inline-body conditionals to block form. Matches
        //    Genie4's parse-time behavior: `if X then stmt` becomes
        //    `if X then` + `{` + `stmt` + `}` so the unified block-form
        //    jump tables handle them correctly in all chain positions.
        //    Also translates `begin`/`end` → `{`/`}` aliases.
        raw = NormaliseInlineConditionals(raw);

        // 3. Build ScriptLine list with indent + label table.
        for (int i = 0; i < raw.Count; i++)
        {
            var (origin, lineNo, line) = raw[i];
            int indent = 0;
            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
                indent++;
            var trimmed = line.Trim();
            inst.Lines.Add(new ScriptLine(lineNo, origin, line, trimmed, indent));

            if (trimmed.Length > 1 && !trimmed.Contains(' '))
            {
                if (trimmed[0] == ':')      inst.Labels[trimmed[1..]]   = i;
                else if (trimmed[^1] == ':') inst.Labels[trimmed[..^1]] = i;
            }
        }

        // 4. Build if/else jump maps for block-form conditionals.
        BuildIfMaps(inst);
        return inst;
    }

    /// <summary>
    /// Rewrites inline-body conditionals (`if X then stmt`, `elseif X then stmt`,
    /// `else stmt`) into block form, and translates `begin`/`end` to `{`/`}`.
    /// Runs after include expansion and before line numbering so the unified
    /// block-form machinery in <see cref="BuildIfMaps"/> handles every form.
    /// </summary>
    private static List<(string, int, string)> NormaliseInlineConditionals(
        List<(string Origin, int LineNo, string Raw)> input)
    {
        var output = new List<(string, int, string)>(input.Count);
        foreach (var (origin, lineNo, raw) in input)
        {
            var trimmed = raw.Trim();

            // begin / end aliases. Only when the whole line is just that word.
            if (trimmed.Equals("begin", StringComparison.OrdinalIgnoreCase))
            { output.Add((origin, lineNo, LeadingIndent(raw) + "{")); continue; }
            if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            { output.Add((origin, lineNo, LeadingIndent(raw) + "}")); continue; }

            // Inline `else <stmt>` (stmt non-empty, stmt != "{"):
            //   else <stmt>
            //     ↓
            //   else
            //   {
            //     <stmt>
            //   }
            if (trimmed.StartsWith("else ", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("elseif", StringComparison.OrdinalIgnoreCase))
            {
                var body = trimmed[5..].Trim();
                if (body.Length > 0 && body != "{")
                {
                    var indent = LeadingIndent(raw);
                    output.Add((origin, lineNo, indent + "else"));
                    output.Add((origin, lineNo, indent + "{"));
                    output.Add((origin, lineNo, indent + body));
                    output.Add((origin, lineNo, indent + "}"));
                    continue;
                }
            }

            // Inline `if <cond> then <stmt>` / `elseif <cond> then <stmt>` /
            // `if_N <stmt>` (stmt non-empty, stmt != "{")
            int sp = trimmed.IndexOf(' ');
            var first = sp < 0 ? trimmed : trimmed[..sp];
            bool isIf     = first.Equals("if",     StringComparison.OrdinalIgnoreCase);
            bool isElseIf = first.Equals("elseif", StringComparison.OrdinalIgnoreCase);
            bool isIfN    = first.Length == 4
                         && first.StartsWith("if_", StringComparison.OrdinalIgnoreCase)
                         && char.IsDigit(first[3]);

            bool isWhile  = first.Equals("while",  StringComparison.OrdinalIgnoreCase);

            if ((isIf || isElseIf || isWhile) && sp > 0)
            {
                var rest = trimmed[(sp + 1)..];
                int thenIdx = FindThenKeyword(rest);
                if (thenIdx >= 0)
                {
                    var afterThen = rest[(thenIdx + 4)..].Trim();
                    if (afterThen.Length > 0 && afterThen != "{")
                    {
                        var indent = LeadingIndent(raw);
                        var header = trimmed[..sp] + " " + rest[..thenIdx].TrimEnd() + " then";
                        output.Add((origin, lineNo, indent + header));
                        output.Add((origin, lineNo, indent + "{"));
                        output.Add((origin, lineNo, indent + afterThen));
                        output.Add((origin, lineNo, indent + "}"));
                        continue;
                    }
                }
            }
            else if (isIfN && sp > 0)
            {
                // `if_N` accepts either `if_N <stmt>` or `if_N then <stmt>`.
                // Normalise both to block form with an explicit `then` so
                // BuildIfMaps can treat them like any other block if.
                var rest = trimmed[(sp + 1)..].Trim();
                int thenIdx = FindThenKeyword(rest);
                string stmt;
                if (thenIdx >= 0) stmt = rest[(thenIdx + 4)..].Trim();
                else              stmt = rest;
                if (stmt.Length > 0 && stmt != "{")
                {
                    var indent = LeadingIndent(raw);
                    output.Add((origin, lineNo, indent + first + " then"));
                    output.Add((origin, lineNo, indent + "{"));
                    output.Add((origin, lineNo, indent + stmt));
                    output.Add((origin, lineNo, indent + "}"));
                    continue;
                }
            }

            output.Add((origin, lineNo, raw));
        }
        return output;
    }

    private static string LeadingIndent(string raw)
    {
        int i = 0;
        while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
        return raw[..i];
    }

    private static void ExpandIncludes(
        string origin, string source, string scriptsDir,
        List<(string, int, string)> output, HashSet<string> visited)
    {
        if (!visited.Add(origin)) return;

        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t   = raw.Trim();

            if (t.StartsWith("include ", StringComparison.OrdinalIgnoreCase))
            {
                var incName = t[8..].Trim();
                var path    = ResolveIncludePath(scriptsDir, incName);
                if (path != null)
                {
                    var subOrigin = Path.GetFileNameWithoutExtension(path);
                    ExpandIncludes(subOrigin, File.ReadAllText(path), scriptsDir, output, visited);
                }
                else
                {
                    output.Add((origin, i + 1, $"echo [script] include not found: {incName}"));
                }
                continue;
            }

            output.Add((origin, i + 1, raw));
        }
    }

    private static string? ResolveIncludePath(string dir, string name)
    {
        foreach (var ext in new[] { "", ".inc", ".cmd" })
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void BuildIfMaps(ScriptInstance inst)
    {
        for (int i = 0; i < inst.Lines.Count; i++)
        {
            var line = inst.Lines[i];
            var t    = line.Trimmed;
            if (t.Length == 0) continue;

            // First token
            int sp = t.IndexOf(' ');
            var first = sp < 0 ? t : t[..sp];
            bool isPlainIf = first.Equals("if",     StringComparison.OrdinalIgnoreCase);
            bool isElseIf  = first.Equals("elseif", StringComparison.OrdinalIgnoreCase);
            bool isWhile   = first.Equals("while",  StringComparison.OrdinalIgnoreCase);
            bool isIfN     = first.Length == 4
                          && first.StartsWith("if_", StringComparison.OrdinalIgnoreCase)
                          && char.IsDigit(first[3]);
            if (!isPlainIf && !isElseIf && !isWhile && !isIfN) continue;

            var rest = sp < 0 ? "" : t[(sp + 1)..];
            int thenIdx = FindThenKeyword(rest);
            if (thenIdx < 0) continue;
            var afterThen = rest[(thenIdx + 4)..].Trim();
            // "then {" on the same line is a brace block whose '{' happens to
            // share the if's line. Anything else non-empty is an inline body.
            bool inlineOpenBrace = afterThen == "{";
            if (afterThen.Length > 0 && !inlineOpenBrace) continue;

            int bodyStart, bodyEnd, closeBraceIdx = -1;
            bool useBraces;
            if (inlineOpenBrace)
            {
                // FindMatchingBrace treats line i as the opener (depth starts
                // at 1) and scans forward for the matching '}'.
                useBraces = true;
                bodyStart = i + 1;
                bodyEnd   = FindMatchingBrace(inst, i);
                if (bodyEnd < 0) continue;
                closeBraceIdx = bodyEnd;
            }
            else
            {
                // Detect brace style: next non-empty line is "{" alone.
                int braceOpen = NextNonEmpty(inst, i + 1);
                useBraces = braceOpen >= 0 && inst.Lines[braceOpen].Trimmed == "{";
                if (useBraces)
                {
                    bodyStart = braceOpen + 1;
                    bodyEnd   = FindMatchingBrace(inst, braceOpen);
                    if (bodyEnd < 0) continue; // unmatched
                    closeBraceIdx = bodyEnd;
                }
                else
                {
                    // Indent-based block: lines indented further than the if.
                    bodyStart = i + 1;
                    bodyEnd   = inst.Lines.Count;
                    for (int j = bodyStart; j < inst.Lines.Count; j++)
                    {
                        if (IsSkippable(inst.Lines[j].Trimmed)) continue;
                        if (inst.Lines[j].Indent <= line.Indent) { bodyEnd = j; break; }
                    }
                }
            }

            int afterBody = useBraces ? bodyEnd + 1 : bodyEnd;

            // While loop: false → past body; close brace → back to header.
            // No else/elseif chain handling.
            if (isWhile)
            {
                inst.IfFalseJump[i] = afterBody;
                if (useBraces && closeBraceIdx >= 0)
                    inst.WhileBackJump[closeBraceIdx] = i;
                continue;
            }

            int nextIx = NextNonEmpty(inst, afterBody);

            bool nextIsElseIf = nextIx >= 0
                             && IsElseIfLine(inst.Lines[nextIx].Trimmed)
                             && (useBraces || inst.Lines[nextIx].Indent == line.Indent);
            bool nextIsElse   = nextIx >= 0
                             && !nextIsElseIf
                             && IsElseLine(inst.Lines[nextIx].Trimmed)
                             && (useBraces || inst.Lines[nextIx].Indent == line.Indent);

            // No chain — simple if with no follow-up branch.
            if (!nextIsElseIf && !nextIsElse)
            {
                inst.IfFalseJump[i] = afterBody;
                continue;
            }

            // Chain: false branch goes to the next conditional line. An
            // `elseif` line dispatches itself (same code path as `if`); an
            // `else` line is entered via ElseJump-less fall-through.
            int chainEnd = ResolveChainEnd(inst, nextIx, line.Indent, useBraces, out int lastElseLine, out int lastElseBraceEnd);

            inst.IfFalseJump[i] = nextIsElseIf
                ? nextIx             // evaluate elseif's condition at that line
                : FindElseBodyStart(inst, nextIx); // jump to else body start

            // True branch's closing brace must skip the rest of the chain.
            if (useBraces && closeBraceIdx >= 0)
                inst.BraceEndJump[closeBraceIdx] = chainEnd;

            // If the chain ends with a terminal `else`, record its ElseJump
            // so that if a true branch of the FIRST if falls through without
            // braces (indent form), it still skips the else body.
            if (lastElseLine >= 0)
                inst.ElseJump[lastElseLine] = chainEnd;
        }
    }

    /// <summary>
    /// Walk an elseif/else chain starting at <paramref name="startIx"/> and
    /// return the line index immediately past the whole chain. Also reports
    /// the terminal `else` line (if any) so the caller can wire ElseJump.
    /// </summary>
    private static int ResolveChainEnd(
        ScriptInstance inst, int startIx, int baseIndent, bool parentUsedBraces,
        out int terminalElseLine, out int terminalElseBraceEnd)
    {
        terminalElseLine     = -1;
        terminalElseBraceEnd = -1;

        int cursor = startIx;
        while (cursor >= 0 && cursor < inst.Lines.Count)
        {
            var tt = inst.Lines[cursor].Trimmed;
            bool elseIf = IsElseIfLine(tt);
            bool elseL  = !elseIf && IsElseLine(tt);
            if (!elseIf && !elseL) return cursor;

            if (elseIf)
            {
                // Find this elseif's body
                int sp = tt.IndexOf(' ');
                var rest = sp < 0 ? "" : tt[(sp + 1)..];
                int thenIdx = FindThenKeyword(rest);
                if (thenIdx < 0) return cursor;
                var afterThen = rest[(thenIdx + 4)..].Trim();
                bool inlineOpen = afterThen == "{";
                if (afterThen.Length > 0 && !inlineOpen) return cursor;

                int bodyEnd;
                bool useBraces;
                if (inlineOpen)
                {
                    useBraces = true;
                    bodyEnd = FindMatchingBrace(inst, cursor);
                    if (bodyEnd < 0) return cursor;
                }
                else
                {
                    int braceOpen = NextNonEmpty(inst, cursor + 1);
                    useBraces = braceOpen >= 0 && inst.Lines[braceOpen].Trimmed == "{";
                    if (useBraces)
                    {
                        bodyEnd = FindMatchingBrace(inst, braceOpen);
                        if (bodyEnd < 0) return cursor;
                    }
                    else
                    {
                        bodyEnd = inst.Lines.Count;
                        for (int j = cursor + 1; j < inst.Lines.Count; j++)
                        {
                            if (IsSkippable(inst.Lines[j].Trimmed)) continue;
                            if (inst.Lines[j].Indent <= inst.Lines[cursor].Indent) { bodyEnd = j; break; }
                        }
                    }
                }
                cursor = useBraces ? bodyEnd + 1 : bodyEnd;
                int next = NextNonEmpty(inst, cursor);
                if (next < 0) return cursor;
                cursor = next;
            }
            else // terminal else
            {
                terminalElseLine = cursor;
                int braceOpen = NextNonEmpty(inst, cursor + 1);
                bool useBraces = braceOpen >= 0 && inst.Lines[braceOpen].Trimmed == "{";
                int elseEnd;
                if (useBraces)
                {
                    elseEnd = FindMatchingBrace(inst, braceOpen);
                    if (elseEnd < 0) return cursor + 1;
                    terminalElseBraceEnd = elseEnd;
                    return elseEnd + 1;
                }
                else
                {
                    elseEnd = inst.Lines.Count;
                    for (int j = cursor + 1; j < inst.Lines.Count; j++)
                    {
                        if (IsSkippable(inst.Lines[j].Trimmed)) continue;
                        if (inst.Lines[j].Indent <= inst.Lines[cursor].Indent) { elseEnd = j; break; }
                    }
                    return elseEnd;
                }
            }
        }
        return cursor;
    }

    /// <summary>
    /// Given an `else` line idx, return the first line of its body (after
    /// any opening brace).
    /// </summary>
    private static int FindElseBodyStart(ScriptInstance inst, int elseIx)
    {
        int braceOpen = NextNonEmpty(inst, elseIx + 1);
        if (braceOpen >= 0 && inst.Lines[braceOpen].Trimmed == "{")
            return braceOpen + 1;
        return elseIx + 1;
    }

    private static bool IsElseIfLine(string t)
    {
        if (!t.StartsWith("elseif", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Length == 6) return true;
        return t[6] == ' ' || t[6] == '\t';
    }

    /// <summary>True for lines the block/jump mapper must look past: blank
    /// lines and `#` comments. A `#` comment is semantically "not there" (Genie
    /// 4), so it must not separate an `if … then` from its `{`, or an if-block
    /// from its `else`/`elseif`, when building jump maps.</summary>
    private static bool IsSkippable(string trimmed)
        => trimmed.Length == 0 || trimmed[0] == '#';

    private static int NextNonEmpty(ScriptInstance inst, int from)
    {
        for (int j = from; j < inst.Lines.Count; j++)
            if (!IsSkippable(inst.Lines[j].Trimmed)) return j;
        return -1;
    }

    /// <summary>Find the matching '}' for a '{' at <paramref name="openIdx"/>.</summary>
    private static int FindMatchingBrace(ScriptInstance inst, int openIdx)
    {
        int depth = 1;
        for (int j = openIdx + 1; j < inst.Lines.Count; j++)
        {
            var t = inst.Lines[j].Trimmed;
            if (t == "{") depth++;
            else if (t == "}")
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return -1;
    }

    private static bool IsElseLine(string t)
    {
        if (!t.StartsWith("else", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Length == 4) return true;
        return t[4] == ' ' || t[4] == '\t';
    }

    /// <summary>
    /// Locate the keyword "then" outside of double-quoted strings, with whitespace
    /// (or string boundary) on both sides. Returns -1 if not found.
    /// </summary>
    public static int FindThenKeyword(string s)
    {
        bool inStr = false;
        for (int i = 0; i + 4 <= s.Length; i++)
        {
            if (s[i] == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            bool leftOk  = i == 0 || char.IsWhiteSpace(s[i - 1]);
            if (!leftOk) continue;
            if (!string.Equals(s.Substring(i, 4), "then", StringComparison.OrdinalIgnoreCase)) continue;
            bool rightOk = i + 4 == s.Length || char.IsWhiteSpace(s[i + 4]);
            if (!rightOk) continue;
            return i;
        }
        return -1;
    }
}
