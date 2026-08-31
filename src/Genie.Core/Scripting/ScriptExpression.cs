using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Genie.Core.Scripting;

/// <summary>
/// Recursive-descent expression evaluator for Genie script <c>if</c> conditions
/// and <c>eval</c>/<c>evalmath</c> assignments.
///
/// Supports:
///   - literals: numbers, "strings", true/false
///   - operators: || && ! == != &lt;&gt; eq = &lt; &gt; &lt;= &gt;= + - * / %
///                 (Genie4 word op `eq` ≡ `=`; `&lt;&gt;` ≡ `!=`)
///   - functions: matchre(s,pat)*, contains, startswith, endswith,
///                tolower, toupper, len, count, abs, min, max
///   - bare identifiers: returned as the literal text (after %var/$var
///                       substitution by the engine)
///
/// (*) matchre populates $0..$9 capture-group vars on the instance.
/// </summary>
internal sealed class ScriptExpression
{
    private readonly string                       _src;
    private readonly ScriptInstance               _inst;
    private readonly IDictionary<string, string>? _globals;
    private readonly Func<string, string?>?       _userVars;
    private int _pos;

    private ScriptExpression(string src, ScriptInstance inst,
                              IDictionary<string, string>? globals,
                              Func<string, string?>? userVars)
    { _src = src; _inst = inst; _globals = globals; _userVars = userVars; }

    public static object Eval(string src, ScriptInstance inst,
                               IDictionary<string, string>? globals = null,
                               Func<string, string?>? userVars = null)
    {
        var p = new ScriptExpression(src, inst, globals, userVars);
        var v = p.ParseOr();
        p.SkipWs();
        if (p._pos < p._src.Length)
            throw new Exception($"unexpected token at: {p._src[p._pos..]}");
        return v;
    }

    public static bool   EvalBool(string src, ScriptInstance inst,
                                    IDictionary<string, string>? globals = null,
                                    Func<string, string?>? userVars = null)
        => ToBool(Eval(src, inst, globals, userVars));

    public static string EvalString(string src, ScriptInstance inst,
                                      IDictionary<string, string>? globals = null,
                                      Func<string, string?>? userVars = null)
        => ToStr(Eval(src, inst, globals, userVars));

    // ── Coercion ────────────────────────────────────────────────────────────

    public static bool ToBool(object? v) => v switch
    {
        bool b   => b,
        double d => d != 0,
        string s => !string.IsNullOrEmpty(s)
                    && !s.Equals("false", StringComparison.OrdinalIgnoreCase)
                    && s != "0",
        null     => false,
        _        => false,   // incl. VarRemnant — G4: bare string sections are never true
    };

    public static double ToNum(object? v) => v switch
    {
        double d => d,
        bool b   => b ? 1 : 0,
        string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0,
        _        => 0,
    };

    public static string ToStr(object? v) => v switch
    {
        string s => s,
        bool b   => b ? "true" : "false",
        double d => d == Math.Floor(d) && !double.IsInfinity(d)
                        ? ((long)d).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("0.################", CultureInfo.InvariantCulture),
        null     => "",
        _        => v.ToString() ?? "",
    };

    private static bool TryNum(object? v, out double n)
    {
        switch (v)
        {
            case double d: n = d; return true;
            case bool   b: n = b ? 1 : 0; return true;
            case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        }
        n = 0; return false;
    }

    // ── Lexer helpers ───────────────────────────────────────────────────────

    private void SkipWs()
    {
        while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
    }

    private bool Match(string op)
    {
        SkipWs();
        if (_pos + op.Length > _src.Length) return false;
        if (_src.Substring(_pos, op.Length) != op) return false;
        _pos += op.Length;
        return true;
    }

    /// <summary>Match a word-boundary keyword (case-insensitive). Used for
    /// Genie4 logical keywords <c>and</c>, <c>or</c>, <c>not</c> so they
    /// aren't parsed as bare identifiers.</summary>
    private bool MatchWord(string word)
    {
        SkipWs();
        if (_pos + word.Length > _src.Length) return false;
        if (string.Compare(_src, _pos, word, 0, word.Length,
                           StringComparison.OrdinalIgnoreCase) != 0) return false;
        int end = _pos + word.Length;
        if (end < _src.Length)
        {
            char n = _src[end];
            if (char.IsLetterOrDigit(n) || n == '_') return false;
        }
        _pos = end;
        return true;
    }

    // ── Grammar ─────────────────────────────────────────────────────────────

    private object ParseOr()
    {
        var l = ParseAnd();
        while (Match("||") || MatchWord("or"))
        {
            var r = ParseAnd();
            l = ToBool(l) || ToBool(r);
        }
        return l;
    }

    private object ParseAnd()
    {
        var l = ParseNot();
        while (Match("&&") || MatchWord("and"))
        {
            var r = ParseNot();
            l = ToBool(l) && ToBool(r);
        }
        return l;
    }

    private object ParseNot()
    {
        SkipWs();
        if (_pos < _src.Length && _src[_pos] == '!'
            && (_pos + 1 >= _src.Length || _src[_pos + 1] != '='))
        {
            _pos++;
            return !ToBool(ParseNot());
        }
        if (MatchWord("not")) return !ToBool(ParseNot());
        return ParseCmp();
    }

    private object ParseCmp()
    {
        var l = ParseAdd();
        // Order matters: 2-char ops before 1-char. "<>" (Genie4 not-equal) sits
        // with the 2-char ops, before "<", so "<" can't shadow it.
        foreach (var op in new[] { "==", "!=", "<>", "<=", ">=", "=", "<", ">" })
        {
            int save = _pos;
            SkipWs();
            if (Match(op))
            {
                var r = ParseAdd();
                return Compare(l, r, op == "<>" ? "!=" : op);   // map <> → !=
            }
            _pos = save;
        }
        // Genie4 word operator: `eq` ≡ `=` (equality). Word-boundary matched
        // (like and/or/not) so it can't fire inside an identifier such as
        // "equipment"; a quoted "eq" value is untouched.
        int eqSave = _pos;
        if (MatchWord("eq"))
        {
            var r = ParseAdd();
            return Compare(l, r, "=");
        }
        _pos = eqSave;
        return l;
    }

    private static object Compare(object l, object r, string op)
    {
        if (TryNum(l, out var a) && TryNum(r, out var b))
        {
            return op switch
            {
                "==" or "=" => a == b,
                "!="        => a != b,
                "<"         => a <  b,
                ">"         => a >  b,
                "<="        => a <= b,
                ">="        => a >= b,
                _           => false,
            };
        }
        var c = string.Compare(ToStr(l), ToStr(r), StringComparison.Ordinal);
        return op switch
        {
            "==" or "=" => c == 0,
            "!="        => c != 0,
            "<"         => c <  0,
            ">"         => c >  0,
            "<="        => c <= 0,
            ">="        => c >= 0,
            _           => false,
        };
    }

    private object ParseAdd()
    {
        var l = ParseMul();
        while (true)
        {
            int save = _pos;
            SkipWs();
            if (Match("+"))
            {
                var r = ParseMul();
                l = TryNum(l, out var a) && TryNum(r, out var b)
                        ? (object)(a + b)
                        : ToStr(l) + ToStr(r);
            }
            else if (Match("-"))
            {
                var r = ParseMul();
                l = ToNum(l) - ToNum(r);
            }
            else { _pos = save; break; }
        }
        return l;
    }

    private object ParseMul()
    {
        var l = ParseUnary();
        while (true)
        {
            int save = _pos;
            SkipWs();
            if (Match("*")) { var r = ParseUnary(); l = ToNum(l) * ToNum(r); }
            else if (Match("/"))
            {
                var r = ParseUnary();
                var d = ToNum(r);
                l = d == 0 ? 0 : ToNum(l) / d;
            }
            else if (Match("%"))
            {
                var r = ParseUnary();
                var d = ToNum(r);
                l = d == 0 ? 0 : ToNum(l) % d;
            }
            else { _pos = save; break; }
        }
        return l;
    }

    private object ParseUnary()
    {
        SkipWs();
        if (_pos < _src.Length && _src[_pos] == '-')
        {
            _pos++;
            return -ToNum(ParseUnary());
        }
        return ParseAtom();
    }

    private object ParseAtom()
    {
        SkipWs();
        // Genie4 parity (#133): a missing operand is the EMPTY STRING, the way
        // Genie 4's section evaluator sees an empty token. An unset %var
        // substitutes to nothing before parsing, so "(%kcast = 1)" arrives as
        // "( = 1)" — that must compare "" against "1" (false), NOT fail the
        // whole expression to false, which was eating the defined side of an
        // `||`. Applies wherever a value slot is empty: before a comparison
        // operator, before ')' ("(1 = )", "()"), or at end of input. Structural
        // errors (unbalanced parens, stray tokens) still throw and warn.
        if (_pos >= _src.Length) return string.Empty;
        char c = _src[_pos];
        if (c == ')' || c == '=' || c == '<' || c == '>' || c == '!')
            return string.Empty;

        if (c == '(')
        {
            _pos++;
            var v = ParseOr();
            SkipWs();
            if (_pos >= _src.Length || _src[_pos] != ')')
                throw new Exception("expression: missing ')'");
            _pos++;
            return v;
        }
        if (c == '"')                              return ParseString();
        if (char.IsDigit(c) || c == '.')           return ParseNumber();
        if (char.IsLetter(c) || c == '_')          return ParseIdentOrCall();
        // Literal %/$ remnants: an UNDEFINED variable survives substitution as
        // literal text (Genie 4 parity — see ScriptEngine.ResolveTokenAt), so
        // `($Outdoorsmanship.Ranks >= 1750)` can reach the evaluator with the
        // sigil token intact. G4's Eval tokenizes it as a plain string, making
        // ordering comparisons silently false (Eval.cs:744) and equality a
        // string compare — read it as a string atom so we land in the same
        // Compare paths instead of throwing "bad condition". Operand position
        // only: modulo's '%' binds in ParseMul before ParseAtom ever sees it.
        if (c == '$' || c == '%')                  return ParseSigilLiteral();
        throw new Exception($"expression: unexpected '{c}'");
    }

    /// <summary>Consume a literal variable remnant: a run of leading sigils
    /// (`$$name` survives substitution whole) plus the same name charset the
    /// engine's substituter scans (letters, digits, '_', '.', '-'). A bare
    /// sigil with no name yields just the sigil — G4 treats a lone '%'/'$' as
    /// ordinary string text.</summary>
    private object ParseSigilLiteral()
    {
        int s = _pos;
        while (_pos < _src.Length && (_src[_pos] == '$' || _src[_pos] == '%')) _pos++;
        while (_pos < _src.Length &&
               (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' ||
                _src[_pos] == '.' || _src[_pos] == '-'))
            _pos++;
        return new VarRemnant(_src[s.._pos]);
    }

    /// <summary>
    /// The literal remnant of an UNDEFINED variable. Comparisons and string
    /// functions see the literal text (via <see cref="ToStr"/>), but as a bare
    /// truth value it is FALSE: G4's evaluator only counts NumberType sections
    /// in GetBooleanResult (Eval.cs:423) — a lone string section contributes
    /// nothing — so `if (%unsetvar)` is false in Genie 4 even though the
    /// remnant is non-empty text. A plain .NET string atom keeps this
    /// engine's existing truthiness; only remnants get the G4 rule.
    /// </summary>
    private sealed class VarRemnant
    {
        public readonly string Text;
        public VarRemnant(string text) => Text = text;
        public override string ToString() => Text;
    }

    private string ParseString()
    {
        _pos++; // opening "
        var sb = new StringBuilder();
        while (_pos < _src.Length && _src[_pos] != '"')
        {
            if (_src[_pos] == '\\' && _pos + 1 < _src.Length)
            {
                _pos++;
                char esc = _src[_pos];
                switch (esc)
                {
                    case 'n':  sb.Append('\n'); break;
                    case 't':  sb.Append('\t'); break;
                    case 'r':  sb.Append('\r'); break;
                    case '\\': sb.Append('\\'); break;
                    case '"':  sb.Append('"');  break;
                    default:
                        // Preserve the backslash for unrecognised sequences
                        // so regex escapes like \[ \] \b \d pass through intact.
                        sb.Append('\\');
                        sb.Append(esc);
                        break;
                }
                _pos++;
            }
            else
            {
                sb.Append(_src[_pos++]);
            }
        }
        if (_pos < _src.Length) _pos++; // closing "
        return sb.ToString();
    }

    private double ParseNumber()
    {
        int s = _pos;
        while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
        return double.Parse(_src[s.._pos], CultureInfo.InvariantCulture);
    }

    private object ParseIdentOrCall()
    {
        int s = _pos;
        while (_pos < _src.Length &&
               (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' || _src[_pos] == '.'))
            _pos++;
        var name = _src[s.._pos];

        SkipWs();
        if (_pos < _src.Length && _src[_pos] == '(')
        {
            _pos++;
            var args = new List<object>();
            SkipWs();
            if (_pos < _src.Length && _src[_pos] != ')')
            {
                args.Add(ParseOr());
                while (true)
                {
                    SkipWs();
                    if (_pos < _src.Length && _src[_pos] == ',') { _pos++; args.Add(ParseOr()); }
                    else break;
                }
            }
            SkipWs();
            if (_pos >= _src.Length || _src[_pos] != ')')
                throw new Exception($"expression: missing ')' after {name}(");
            _pos++;
            return CallFunc(name, args);
        }

        if (name.Equals("true",  StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

        // Genie 4 parity: a bare string operand may contain SPACES — Genie 4's
        // evaluator splits the expression on operators, so
        // `(%guild = Moon Mage)` and `$selection = DIVINATION TOOL` compare
        // multi-word text (mm_train does both, and its Menu.Build tests
        // `!($5 = "")` where $5 substitutes to "Moonmage Training Menu").
        // Absorb following bare words into one literal; the logical word
        // operators (and/or/not/eq) and any symbol end the operand.
        while (true)
        {
            int save = _pos;
            SkipWs();
            if (_pos >= _src.Length || (!char.IsLetter(_src[_pos]) && _src[_pos] != '_'))
            { _pos = save; break; }
            int ws = _pos;
            // Apostrophes are common in absorbed DR text ("Peri'el",
            // "'visions'.") and are not an operator anywhere in the grammar.
            while (_pos < _src.Length &&
                   (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_' ||
                    _src[_pos] == '.' || _src[_pos] == '\''))
                _pos++;
            var word = _src[ws.._pos];
            if (word.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("or",  StringComparison.OrdinalIgnoreCase) ||
                word.Equals("not", StringComparison.OrdinalIgnoreCase) ||
                word.Equals("eq",  StringComparison.OrdinalIgnoreCase))
            { _pos = save; break; }
            name += " " + word;
        }
        return name; // bare identifier / multi-word text → literal string
    }

    private object CallFunc(string name, List<object> args)
    {
        string A(int i) => i < args.Count ? ToStr(args[i]) : "";
        double N(int i) => i < args.Count ? ToNum(args[i]) : 0;

        switch (name.ToLowerInvariant())
        {
            case "matchre":
            {
                var s   = A(0);
                var pat = A(1);
                Match m;
                // RegexSafety match-timeout (deep-dive Phase 0): the pattern is
                // user script text — a catastrophic one times out as a no-match
                // instead of wedging the pipeline inside an eval.
                try { m = Regex.Match(s, pat, RegexOptions.None, Diagnostics.RegexSafety.MatchTimeout); }
                catch (RegexMatchTimeoutException)
                {
                    Diagnostics.RegexSafety.ReportTimeout(Diagnostics.PipelineStage.Scripts);
                    return false;
                }
                catch (Exception ex) { throw new Exception($"matchre: bad regex: {ex.Message}"); }
                if (!m.Success) return false;
                // Captures land in $0..$9 on the current DollarStack frame —
                // script args (%N) must remain untouched.
                var frame = _inst.DollarStack.Count > 0
                    ? _inst.DollarStack.Peek()
                    : null;
                if (frame is null)
                {
                    frame = new string[10];
                    _inst.DollarStack.Push(frame);
                }
                for (int i = 0; i < 10; i++) frame[i] = string.Empty;
                frame[0] = m.Value;
                for (int i = 1; i < m.Groups.Count && i <= 9; i++)
                    frame[i] = m.Groups[i].Value;
                return true;
            }
            // Genie4 parity: the string predicates are case-SENSITIVE (Eval.cs
            // uses no comparer). Ordinal — matching Compare()'s ==/= — keeps the
            // game's ASCII text deterministic. instr/instring are boolean
            // "contains" aliases (NOT a position).
            case "instr":
            case "instring":
            case "contains":   return A(0).IndexOf(A(1), StringComparison.Ordinal) >= 0;
            case "startswith": return A(0).StartsWith(A(1), StringComparison.Ordinal);
            case "endswith":   return A(0).EndsWith(A(1),   StringComparison.Ordinal);
            case "tolower":    return A(0).ToLowerInvariant();
            case "toupper":    return A(0).ToUpperInvariant();
            case "len":
            case "length":     return (double)A(0).Length;
            case "count":
            {
                // Genie4 parity (Eval.cs Count): the number of OCCURRENCES of
                // the substring — NOT the element count. "a|b|c" with "|" → 2,
                // count("barbar","foo") → 0. Community scripts depend on this:
                // they iterate 0..count INCLUSIVE to walk a pipe list (e.g.
                // cyclic.cmd), so returning elements would overrun by one (#134).
                // Genie 4 loops forever on an empty match; return 0 instead.
                var s = A(0); var sep = A(1);
                if (s.Length == 0 || sep.Length == 0) return 0.0;
                int n = 0, idx = 0;
                while ((idx = s.IndexOf(sep, idx, StringComparison.Ordinal)) >= 0)
                { n++; idx += sep.Length; }
                return (double)n;
            }
            case "defined":   // Genie4 alias of def
            case "def":
            {
                // def(Name) — true if Name EXISTS as a variable, regardless of
                // value. Genie 4 (Eval.cs) checks m_oGlobals.VariableList
                // .ContainsKey(name), so a var set to "" still counts — this is
                // exactly how scripts tell "set empty" from "never set". Match
                // that existence semantics across all three stores Genie 5 keeps
                // a name in: script locals (%vars), live-state globals, and the
                // persistent #var store (the one `#var testvar foo` writes to,
                // reached via UserVarLookup — #129: it was omitted here, so
                // def(testvar) returned false even though $testvar resolved).
                var n = A(0);
                if (string.IsNullOrEmpty(n)) return false;
                if (_inst.Vars.ContainsKey(n)) return true;
                if (_globals != null && _globals.ContainsKey(n)) return true;
                if (_userVars?.Invoke(n) is not null) return true;
                return false;
            }
            case "abs": return Math.Abs(N(0));
            case "min": return Math.Min(N(0), N(1));
            case "max": return Math.Max(N(0), N(1));
            case "replace":
                return A(0).Replace(A(1), A(2));
            case "replacere":
                // Timeout-bounded like matchre; any failure (bad regex OR a
                // catastrophic pattern timing out) returns the input unchanged.
                try { return Regex.Replace(A(0), A(1), A(2), RegexOptions.None, Diagnostics.RegexSafety.MatchTimeout); }
                catch { return A(0); }
            case "match":
                // Genie4 parity: exact equality (Eval.cs:941 string.Equals with
                // the default Ordinal comparer), NOT substring. Case-sensitive to
                // stay consistent with `==`/`=`, which Compare() already does
                // Ordinal. Was a substring IndexOf — silently behaved like contains.
                return string.Equals(A(0), A(1), StringComparison.Ordinal);
            case "substring":   // Genie4 alias of substr
            case "substr":
            {
                var s = A(0);
                int st = (int)N(1);
                if (st < 0) st = 0;
                if (st >= s.Length) return "";
                if (args.Count < 3) return s.Substring(st);
                int ln = (int)N(2);
                if (ln < 0) ln = 0;
                if (st + ln > s.Length) ln = s.Length - st;
                return s.Substring(st, ln);
            }
            case "trim":   return A(0).Trim();
            // Genie4: case-sensitive AND 1-based (IndexOf + 1), so a miss is 0
            // (falsy). Community scripts rely on `if !indexof(hay, needle)`
            // meaning "needle absent" (e.g. GenieHunter/hunt.cmd). Was 0-based /
            // OrdinalIgnoreCase, which made the not-found case (-1) truthy.
            case "indexof":
                return (double)(A(0).IndexOf(A(1), StringComparison.Ordinal) + 1);
            case "lastindexof":
                return (double)(A(0).LastIndexOf(A(1), StringComparison.Ordinal) + 1);
            case "element":
            {
                // element(list, index [, sep])  — default separator is '|'
                var list = A(0);
                int ix = (int)N(1);
                var sep = args.Count >= 3 ? A(2) : "|";
                var parts = list.Split(new[] { sep }, StringSplitOptions.None);
                return ix >= 0 && ix < parts.Length ? parts[ix] : "";
            }
            case "floor":   return Math.Floor(N(0));
            case "ceiling":
            case "ceil":    return Math.Ceiling(N(0));
            case "round":   return args.Count >= 2
                                ? Math.Round(N(0), (int)N(1))
                                : Math.Round(N(0));
            case "sqrt":    return Math.Sqrt(N(0));
            case "log":
            case "ln":      return Math.Log(N(0));
            case "log10":   return Math.Log10(N(0));
            case "neg":     return -Math.Abs(N(0));
            case "pos":     return Math.Abs(N(0));
        }
        throw new Exception($"unknown function: {name}");
    }
}
