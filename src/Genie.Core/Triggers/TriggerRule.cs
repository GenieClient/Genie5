using System.Text.RegularExpressions;
using Genie.Core.Diagnostics;

namespace Genie.Core.Triggers;

public sealed class TriggerRule
{
    private string? _hint;
    private bool    _safe = true;
    private readonly StringComparison _cmp;

    public TriggerRule(string pattern, string action, bool caseSensitive = false,
                       bool isEnabled = true, string className = "", bool safe = true,
                       string soundFile = "", string speak = "", bool eval = false,
                       bool matchAll = false)
    {
        Pattern       = pattern;
        Action        = action;
        CaseSensitive = caseSensitive;
        IsEnabled     = isEnabled;
        ClassName     = className;
        SoundFile     = soundFile;
        Speak         = speak;
        Eval          = eval;
        MatchAll      = matchAll;
        _cmp          = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Rebuild(safe);
    }
    public string Pattern       { get; }
    public string Action        { get; }
    public bool   CaseSensitive { get; }
    public bool   IsEnabled     { get; set; }
    public string ClassName     { get; set; }
    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;
    /// <summary>Opt-in script-expression evaluation of the action (#150). When
    /// true, <c>{…}</c> expression blocks in the action are evaluated (math /
    /// functions / string ops, via the same evaluator as <c>#eval</c>) after
    /// <c>$0..$n</c> capture substitution, before the action is dispatched.
    /// Off = the action is dispatched as-is (current behaviour).</summary>
    public bool   Eval          { get; set; }
    /// <summary>Opt-in "match all" (#23). When true, the action fires once per
    /// match on the line (each with its own <c>$0..$n</c> captures) instead of
    /// once for the first match. Off = fire once per line (current behaviour).</summary>
    public bool   MatchAll      { get; set; }
    /// <summary>Optional sound played when this trigger fires (resolved against
    /// SoundDir). Empty = silent.</summary>
    public string SoundFile     { get; set; }
    /// <summary>Optional TTS when this trigger fires: empty = silent, <c>*</c> =
    /// speak the matched line, anything else = speak that text ($0..$n capture
    /// groups expand). Spoken urgent — it barges in over stream read-aloud.</summary>
    public string Speak         { get; set; }
    public Regex  Regex         { get; private set; } = null!;

    public bool IsMatch(string line) => IsEnabled && SafeMatch(line) is { Success: true };

    /// <summary>
    /// Match with the safety layer: a cheap literal pre-filter before the regex,
    /// and a match-timeout guard that returns null (no match) instead of hanging
    /// the read thread on catastrophic backtracking. Returns the <see cref="Match"/>
    /// so callers can expand <c>$0..$n</c> capture groups.
    /// </summary>
    public Match? SafeMatch(string line)
    {
        if (_safe && _hint is not null && !line.Contains(_hint, _cmp)) return null;
        try { var m = Regex.Match(line); return m.Success ? m : null; }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Triggers); return null; }
    }

    /// <summary>
    /// All non-overlapping matches on the line (for a <c>MatchAll</c> trigger),
    /// under the same literal pre-filter + match-timeout safety as <see cref="SafeMatch"/>.
    /// Zero-length matches are skipped so a zero-width pattern can't fire the
    /// action once per character. Empty if nothing matches.
    /// </summary>
    public IReadOnlyList<Match> SafeMatchAll(string line)
    {
        if (_safe && _hint is not null && !line.Contains(_hint, _cmp)) return Array.Empty<Match>();
        try
        {
            List<Match>? matches = null;
            foreach (Match m in Regex.Matches(line))
                if (m.Success && m.Length > 0) (matches ??= new()).Add(m);
            return (IReadOnlyList<Match>?)matches ?? Array.Empty<Match>();
        }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Triggers); return Array.Empty<Match>(); }
    }

    /// <summary>(Re)build the regex with or without the safety match-timeout.</summary>
    internal void Rebuild(bool safe)
    {
        _safe = safe;
        var opts = RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        Regex = RegexSafety.Build(Pattern, opts, safe);
        _hint = safe ? RegexSafety.LiteralHint(Pattern) : null;
    }
}
