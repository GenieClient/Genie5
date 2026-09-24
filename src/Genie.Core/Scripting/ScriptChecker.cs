namespace Genie.Core.Scripting;

/// <summary>
/// Whole-script check (public #239): report every problem the engine would only hit
/// when execution reaches the line, all at once — so an old script can be vetted for
/// Genie 5 without walking every branch of it in game.
///
/// <para>The rules are the engine's own, applied to the fully parsed script (includes
/// expanded, inline <c>if … then stmt</c> already normalised to block form), so a
/// finding here is exactly what would bite at runtime:</para>
/// <list type="bullet">
/// <item><c>goto</c> / <c>gosub</c> to a label that doesn't exist — at runtime the
/// script stops with "unknown label".</item>
/// <item><c>match</c> / <c>matchre</c> naming a missing label — that match can never
/// fire, so its matchwait always times out.</item>
/// <item><c>if</c> / <c>else if</c> without <c>then</c> (with the odd-quote hint the
/// engine gives), and unbalanced parentheses in a condition.</item>
/// <item><c>action</c> without <c>when</c> / <c>whenre</c>.</item>
/// <item>A missing or refused <c>include</c>, unbalanced <c>{</c> / <c>}</c> blocks,
/// and a label defined more than once (the later one is the one jumped to).</item>
/// </list>
/// <para>Targets built from variables (<c>goto %next</c>, <c>gosub $target</c>) are
/// only known at runtime and are skipped rather than guessed.</para>
/// </summary>
public static class ScriptChecker
{
    /// <summary>One finding, located by the file it came from (an include keeps its
    /// own name) and that file's line number.</summary>
    public sealed record Issue(string Origin, int Line, string Message, bool InInclude = false)
    {
        /// <summary>A finding inside an included file is flagged as such: a shared
        /// library routinely jumps to labels each including script is expected to
        /// define, so it only matters if that code runs.</summary>
        public override string ToString() => $"{Origin}:{Line} {Message}{(InInclude ? "  [in an include]" : "")}";
    }

    public static IReadOnlyList<Issue> Check(ScriptInstance script)
    {
        var issues = new List<Issue>();
        void Add(ScriptLine l, string message) => issues.Add(new Issue(l.Origin, l.LineNumber, message,
            InInclude: !string.Equals(l.Origin, script.Name, StringComparison.OrdinalIgnoreCase)));

        int open = 0;
        ScriptLine? firstUnmatchedClose = null;
        var labelLines = new Dictionary<string, List<ScriptLine>>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in script.Lines)
        {
            var t = line.Trimmed;
            if (t.Length == 0 || t[0] == '#') continue;

            // Openers as the parser counts them: a bare "{" line, or a header that
            // opens its own block (`if … then {`, `else {`, `while … then {`).
            if (t == "{") { open++; continue; }
            if (ScriptParser.OpensInlineBrace(t)) open++;   // …and still check the header below
            if (t[0] == '}')
            {
                if (open == 0) firstUnmatchedClose ??= line;
                else open--;
                // `} else …` closes a block and carries on — check the rest.
                t = t[1..].Trim();
                if (t.Length == 0) continue;
            }

            // Same label rule as the parser: one token, leading or trailing colon.
            if (t.Length > 1 && !t.Contains(' ') && (t[0] == ':' || t[^1] == ':'))
            {
                var name = t[0] == ':' ? t[1..] : t[..^1];
                if (!labelLines.TryGetValue(name, out var at)) labelLines[name] = at = new();
                at.Add(line);
                continue;
            }

            // The parser reports include trouble as an echo line in place.
            if (t.StartsWith("echo [script] include ", StringComparison.OrdinalIgnoreCase))
            {
                Add(line, t["echo [script] ".Length..]);
                continue;
            }

            var (word, rest) = Split(t);
            switch (word)
            {
                case "goto":
                case "gosub":
                {
                    var (target, _) = Split(rest, lower: false);
                    if (target.Length == 0) Add(line, $"'{word}' with no label");
                    // `gosub clear` is built in (Genie 4 parity): it wipes the return stack.
                    else if (word == "gosub" && target.Equals("clear", StringComparison.OrdinalIgnoreCase)) break;
                    else if (!IsDynamic(target) && !script.Labels.ContainsKey(target))
                        Add(line, $"'{word} {target}' — no such label; the script would stop here");
                    break;
                }
                case "match":
                case "matchre":
                {
                    // An empty pattern is legal — it matches any line, a catch-all.
                    var (target, _) = Split(rest, lower: false);
                    if (target.Length == 0) Add(line, $"'{word}' with no label");
                    else if (!IsDynamic(target) && !script.Labels.ContainsKey(target))
                        Add(line, $"match label '{target}' not found — this match can never fire");
                    break;
                }
                case "if":
                case "elseif":
                case "while":
                    CheckCondition(line, word, rest, Add);
                    break;
                case "else":
                {
                    var (next, afterIf) = Split(rest);
                    if (next == "if") CheckCondition(line, "else if", afterIf, Add);
                    break;
                }
                case "action":
                    CheckAction(line, rest, Add);
                    break;
            }
        }

        foreach (var (name, at) in labelLines)
            if (at.Count > 1)
                Add(at[^1], $"label '{name}' is defined {at.Count} times (first at line {at[0].LineNumber}) — goto uses this later one");
        if (firstUnmatchedClose is not null) Add(firstUnmatchedClose, "'}' with no matching '{'");
        if (open > 0 && script.Lines.Count > 0) Add(script.Lines[^1], $"{open} block(s) opened with '{{' are never closed");

        return issues.OrderBy(i => i.Origin, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Line).ToList();
    }

    private static void CheckCondition(ScriptLine line, string keyword, string rest, Action<ScriptLine, string> add)
    {
        int thenIdx = ScriptParser.FindThenKeyword(rest);
        if (thenIdx < 0)
        {
            var quotes = rest.Count(c => c == '"');
            add(line, $"'{keyword}' missing 'then'{(quotes % 2 == 1 ? " (unbalanced \" quotes?)" : "")}");
            return;
        }
        int depth = 0;
        bool inQuote = false;
        foreach (var c in rest[..thenIdx])
        {
            if (c == '"') inQuote = !inQuote;
            else if (inQuote) continue;
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) break;
        }
        if (depth != 0) add(line, $"unbalanced parentheses in the '{keyword}' condition");
    }

    private static readonly string[] ActionControlWords = ["on", "off", "clear", "remove"];

    private static void CheckAction(ScriptLine line, string rest, Action<ScriptLine, string> add)
    {
        var body = rest.Trim();
        if (body.Length == 0) { add(line, "'action' with nothing after it"); return; }

        // Optional "(label)" prefix, as the engine parses it.
        if (body[0] == '(' && body.IndexOf(')') is > 0 and var close) body = body[(close + 1)..].Trim();

        if (ActionControlWords.Any(w => body.Equals(w, StringComparison.OrdinalIgnoreCase)) ||
            body.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
            return;
        if (ScriptEngine.FindKeywordOutsideQuotes(body, "when") < 0 &&
            ScriptEngine.FindKeywordOutsideQuotes(body, "whenre") < 0)
            add(line, "'action' missing 'when' / 'whenre'");
    }

    private static bool IsDynamic(string target) => target.IndexOfAny(['%', '$']) >= 0;

    /// <summary>First whitespace-delimited token (lower-cased for keyword matching
    /// unless <paramref name="lower"/> is false) and the trimmed remainder.</summary>
    private static (string Word, string Tail) Split(string s, bool lower = true)
    {
        s = s.Trim();
        int i = s.IndexOfAny([' ', '\t']);
        var word = i < 0 ? s : s[..i];
        var rest = i < 0 ? "" : s[(i + 1)..].Trim();
        return (lower ? word.ToLowerInvariant() : word, rest);
    }
}
