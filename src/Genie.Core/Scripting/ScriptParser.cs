namespace Genie.Core.Scripting;

public static class ScriptParser
{
    /// <param name="sourcePath">Path the script text was read from, when there is
    /// one. Seeds the include-once set so a script that includes ITSELF pulls the
    /// text in once (Genie 4 puts the running file in <c>m_oScriptFiles</c> too).
    /// Omitting it costs nothing but that one case — a self-include still
    /// terminates, because the path is recorded before the recursive call.</param>
    /// <param name="includeRoots">Directories besides <paramref name="scriptsDir"/>
    /// that an <c>include</c> may resolve into — the engine passes its script
    /// search dirs, so a script in a subfolder can still include a shared file
    /// from the scripts root.</param>
    public static ScriptInstance Parse(string name, string scriptsDir, string source,
                                       string? sourcePath = null,
                                       IReadOnlyList<string>? includeRoots = null)
    {
        var inst = new ScriptInstance { Name = name };

        // 1. Recursive include expansion → flat raw line list.
        var raw = new List<(string Origin, int LineNo, string Raw)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(sourcePath) && CanonicalPath(sourcePath) is { } rootPath)
            visited.Add(rootPath);
        var roots = IncludeRoots(scriptsDir, includeRoots);
        ExpandIncludes(name, source, scriptsDir, roots, raw, visited);

        // 2. Normalise inline-body conditionals to block form. Matches
        //    Genie4's parse-time behavior: `if X then stmt` becomes
        //    `if X then` + `{` + `stmt` + `}` so the unified block-form
        //    jump tables handle them correctly in all chain positions.
        //    Also translates `begin`/`end` → `{`/`}` aliases.
        // 2a. Collapse inline `<% … %>` JavaScript blocks BEFORE the conditional
        //     and label passes below. A JS body can contain a bare `default:` or
        //     an unspaced object key that the label scanner would register as a
        //     script label, and a `then` inside a string literal that
        //     FindThenKeyword would split on. Extracting first makes all of that
        //     unreachable. Runs after include expansion, so blocks inside an
        //     included file are handled with no extra work.
        raw = ExtractJsBlocks(raw, inst);

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
    /// Collapses each inline <c>&lt;% … %&gt;</c> block to a single
    /// <c>__jsblock N</c> line, stashing the body in
    /// <see cref="ScriptInstance.JsBlocks"/> (public #322).
    /// <para>Genie 4 parity (<c>Script/Script.cs:3686</c>): <c>&lt;%</c> opens a
    /// block only when it starts the line; any text after it on that line is kept
    /// as JS; the block closes on the first line ENDING with <c>%&gt;</c>. A block
    /// left unterminated at end of file is still emitted, so it runs rather than
    /// silently vanishing.</para>
    /// </summary>
    private static List<(string, int, string)> ExtractJsBlocks(
        List<(string Origin, int LineNo, string Raw)> input, ScriptInstance inst)
    {
        var output = new List<(string, int, string)>(input.Count);
        List<string>? body = null;
        string blockOrigin = string.Empty, blockIndent = string.Empty;
        int blockLineNo = 0;

        void Close()
        {
            inst.JsBlocks.Add(string.Join("\n", body!));
            output.Add((blockOrigin, blockLineNo,
                        blockIndent + "__jsblock " + (inst.JsBlocks.Count - 1)));
            body = null;
        }

        foreach (var (origin, lineNo, raw) in input)
        {
            if (body is null)
            {
                var trimmed = raw.TrimStart();
                if (!trimmed.StartsWith("<%", StringComparison.Ordinal))
                { output.Add((origin, lineNo, raw)); continue; }

                body        = new List<string>();
                blockOrigin = origin;
                blockLineNo = lineNo;
                blockIndent = LeadingIndent(raw);

                // Text trailing `<%` on the opening line is JS. A one-line
                // `<% … %>` opens and closes here.
                var head = trimmed[2..];
                if (head.TrimEnd().EndsWith("%>", StringComparison.Ordinal))
                {
                    var t = head.TrimEnd();
                    body.Add(t[..^2]);
                    Close();
                }
                else if (head.Length > 0) body.Add(head);
                continue;
            }

            var tail = raw.TrimEnd();
            if (tail.EndsWith("%>", StringComparison.Ordinal))
            {
                var inner = tail[..^2];
                if (inner.Trim().Length > 0) body.Add(inner);
                Close();
            }
            else body.Add(raw);
        }

        if (body is not null) Close();   // unterminated block: emit it anyway
        return output;
    }

    /// <summary>
    /// True when a sigil-leading line is an assignment rather than a value
    /// being used in place — the name must be a plain variable name followed
    /// by whitespace or <c>=</c>.
    /// </summary>
    /// <remarks>
    /// Guards against eating a line that merely BEGINS with a variable, such
    /// as <c>%cmd extra args</c> where the intent is the substituted value as
    /// a game command. Genie 4 does not make that distinction and rewrites any
    /// sigil-leading line; the corpus has no such line, and being narrower
    /// here cannot turn a working script into a broken one — only the reverse.
    /// </remarks>
    private static bool IsAssignmentStart(string trimmed)
    {
        int i = 1;
        while (i < trimmed.Length &&
               (char.IsLetterOrDigit(trimmed[i]) || trimmed[i] is '_' or '.' or '-'))
            i++;
        if (i == 1) return false;                       // bare sigil, no name
        if (i >= trimmed.Length) return false;          // name with no value
        return trimmed[i] == ' ' || trimmed[i] == '\t' || trimmed[i] == '=';
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

            // Genie 4's bare sigil assignment (Script.cs:3787-3799). A line
            // that STARTS with a sigil is an assignment statement:
            //
            //   %Failure = 0     →  setvariable Failure 0
            //   %item $0         →  setvariable item $0
            //   $gvar = world    →  #var gvar world
            //
            // Rewritten here, at load, for the same reason Genie 4 does it
            // here: by dispatch time the line has been %var/$var-substituted,
            // so re-assigning an already-defined variable would arrive as
            // "0 = 1" with nothing left to recognise. Ten lines in
            // lumberjacking.cmd use this form; without the rewrite each one
            // fell through to the default case and was sent to the game.
            if (trimmed.Length > 1 && (trimmed[0] == '%' || trimmed[0] == '$') &&
                IsAssignmentStart(trimmed))
            {
                var body = trimmed[1..];
                // Genie 4 replaces EVERY " = " in the line; we replace only the
                // first, so a value that itself contains " = " survives intact.
                // No corpus line has one, so this is a deliberate, zero-impact
                // improvement rather than a parity break.
                int eq = body.IndexOf(" = ", StringComparison.Ordinal);
                if (eq >= 0) body = body[..eq] + " " + body[(eq + 3)..];

                var stmt = trimmed[0] == '%' ? "setvariable " + body : "#var " + body;
                output.Add((origin, lineNo, LeadingIndent(raw) + stmt));
                continue;
            }

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

    /// <summary>
    /// Splices every <c>include</c> into the raw line list, depth-first, at the
    /// point the directive appears (Genie 4 <c>AppendFile</c> parity).
    /// <para><paramref name="visited"/> is the include-once set and is keyed on
    /// the RESOLVED FILE PATH, never on the file's name or stem. Genie 4 keys its
    /// own guard on the include argument as written
    /// (<c>m_oScriptFiles.Contains(strArgument)</c>), so <c>foo.inc</c> and
    /// <c>foo.cmd</c> are two different entries there and both load. Keying on the
    /// stem instead — with the running script seeded into the same set — silently
    /// dropped two shapes players actually write (public #347): a script that
    /// splits its variables into a sibling of the same name (<c>foo.cmd</c> doing
    /// <c>include foo.inc</c>) expanded to NOTHING, and <c>include x.inc</c>
    /// followed by <c>include x.cmd</c> expanded only the first. Both failed with
    /// no diagnostic — the file resolved fine, it was the guard that dropped it —
    /// so the first symptom was a <c>gosub</c> into a label that no longer
    /// existed.</para>
    /// <para>Recording the path BEFORE recursing is what bounds a cycle: A → B → A
    /// stops when B's include of A finds A's path already in the set.</para>
    /// </summary>
    private static void ExpandIncludes(
        string origin, string source, string scriptsDir, IReadOnlyList<string> roots,
        List<(string, int, string)> output, HashSet<string> visited)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var t   = raw.Trim();

            if (t.StartsWith("include ", StringComparison.OrdinalIgnoreCase))
            {
                var incName = t[8..].Trim();

                // #104: a .js include pulls in a JavaScript function library so the
                // .cmd can call its functions with `js` / `jscall`. The JS context
                // is per-running-script, so this can't be a parse-time splice like a
                // .cmd/.inc include — emit a runtime `__jsinclude <path>` directive
                // the engine loads at runtime (BOM-stripped + .length()→.length
                // compatibility rewrite). The path resolves against the scripts dir.
                if (incName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                {
                    // Relative names — including sub-paths like lib\x.js, which used
                    // to resolve against the process working directory — resolve
                    // against the scripts dir, as .cmd/.inc includes always have.
                    // Path.Combine keeps a rooted name as-is.
                    var jsPath = Path.Combine(scriptsDir, incName);
                    output.Add((origin, i + 1,
                        !File.Exists(jsPath)          ? $"echo [script] include not found: {incName}"
                        : !IsUnderAnyRoot(jsPath, roots) ? RefusedInclude(incName)
                        :                                  $"__jsinclude {jsPath}"));
                    continue;
                }

                var path = ResolveIncludePath(scriptsDir, incName);
                if (path != null && !IsUnderAnyRoot(path, roots))
                {
                    output.Add((origin, i + 1, RefusedInclude(incName)));
                }
                else if (path != null)
                {
                    // Already pulled in (by any spelling that resolves here) —
                    // skip silently, exactly as Genie 4 does for a repeat include.
                    if (!visited.Add(CanonicalPath(path) ?? path)) continue;
                    var subOrigin = Path.GetFileNameWithoutExtension(path);
                    ExpandIncludes(subOrigin, File.ReadAllText(path), scriptsDir, roots, output, visited);
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

    /// <summary>
    /// One canonical spelling for a file so the include-once set compares two
    /// references to the same file as equal — <c>lib\a.inc</c> vs <c>lib/a.inc</c>,
    /// a relative include vs an absolute one, a <c>.</c> segment in the middle.
    /// Returns null for a path the runtime refuses to canonicalise, and the caller
    /// falls back to the raw string rather than losing the guard entirely.
    /// <para>The set itself is case-insensitive, which is right on Windows and
    /// macOS and slightly conservative on Linux: two include files in one
    /// directory differing only in case would collapse to one. That was already
    /// the comparer before #347 and the alternative — two files a player cannot
    /// tell apart in a listing — is the worse failure.</para>
    /// </summary>
    private static string? CanonicalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    // ── Include containment (2026-08-31 security review) ─────────────────────
    // An include name comes straight from script text, and a downloaded script
    // could name `..\..\..\somewhere\secret.txt` or an absolute path: the file
    // was read and its unrecognised lines sent verbatim to the game socket. An
    // include must now resolve inside the script's own folder or one of the
    // engine's script dirs. This is stricter than Genie 4, which used any path
    // containing a backslash verbatim; no script in the reference corpora
    // includes from outside its scripts folder.

    private static string[] IncludeRoots(string scriptsDir, IReadOnlyList<string>? extra)
    {
        var roots = new List<string>();
        foreach (var d in extra is null ? new[] { scriptsDir } : extra.Prepend(scriptsDir))
            if (!string.IsNullOrWhiteSpace(d) && CanonicalPath(d) is { } full)
                roots.Add(Path.EndsInDirectorySeparator(full) ? full : full + Path.DirectorySeparatorChar);
        return roots.ToArray();
    }

    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        if (CanonicalPath(path) is not { } full) return false;
        foreach (var root in roots)
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string RefusedInclude(string name) =>
        $"echo [script] include refused — outside the scripts folder: {name}";

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
            if (t == "{" || OpensInlineBrace(t)) depth++;
            else if (t == "}")
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return -1;
    }

    /// <summary>
    /// True when a line's own header opens a brace block — `if … then {`,
    /// `elseif … then {`, `while … then {`, `if_N … then {`, or `else {`.
    /// FindMatchingBrace must count these as openers; a bare-"{" check pairs
    /// a nested `if … then {` block's '}' with the OUTER opener, so the outer
    /// if's false-jump lands inside its own body (#228).
    /// </summary>
    private static bool OpensInlineBrace(string t)
    {
        if (t.Length < 2 || t[^1] != '{') return false;
        if (IsElseLine(t) && !IsElseIfLine(t))
            return t[4..].Trim() == "{";
        int sp = t.IndexOf(' ');
        if (sp < 0) return false;
        var first = t[..sp];
        bool header = first.Equals("if",     StringComparison.OrdinalIgnoreCase)
                   || first.Equals("elseif", StringComparison.OrdinalIgnoreCase)
                   || first.Equals("while",  StringComparison.OrdinalIgnoreCase)
                   || (first.Length == 4
                       && first.StartsWith("if_", StringComparison.OrdinalIgnoreCase)
                       && char.IsDigit(first[3]));
        if (!header) return false;
        int thenIdx = FindThenKeyword(t[(sp + 1)..]);
        if (thenIdx < 0) return false;
        return t[(sp + 1 + thenIdx + 4)..].Trim() == "{";
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
