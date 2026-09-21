using System.Text.RegularExpressions;
using Genie.Core.Classes;
using Genie.Core.Diagnostics;

namespace Genie.Core.Substitutes;

public sealed class SubstituteRule
{
    private Regex?  _regex;
    private string? _hint;
    private bool    _safe = true;
    private readonly StringComparison _cmp;

    public SubstituteRule(string pattern, string replacement, bool caseSensitive = false, bool isEnabled = true, string className = "", bool safe = true, bool wholeWord = false)
    {
        Pattern = pattern; Replacement = replacement; CaseSensitive = caseSensitive; IsEnabled = isEnabled; ClassName = className;
        WholeWord = wholeWord;
        _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _mayHaveVars = replacement.Contains('$');
        Rebuild(safe);
    }
    public string Pattern       { get; }
    public string Replacement   { get; }
    public bool   CaseSensitive { get; }
    public bool   IsEnabled     { get; set; }
    public string ClassName     { get; }

    /// <summary>
    /// Match only at word boundaries (public #245). A substitute for
    /// <c>take</c> should not rewrite the inside of <c>mistake</c>, and without
    /// this the only workaround was for the user to write the boundaries into
    /// the pattern themselves — which means knowing that the pattern is a regex
    /// at all, and that <c>#</c>b is the thing to reach for.
    /// </summary>
    public bool WholeWord { get; }

    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;

    /// <summary>True when the replacement text contains a <c>$</c>, so it MAY
    /// carry a global variable. Computed once at construction: the apply path
    /// runs per rule per line, and the overwhelming majority of replacements
    /// are literal text that must not pay for an expansion check.</summary>
    private readonly bool _mayHaveVars;

    public string Apply(string line) => Apply(line, expandVariables: null);

    /// <summary>
    /// Apply this rule. <paramref name="expandVariables"/> resolves
    /// <c>$globals</c> in the replacement text at match time (public #246) —
    /// replacing a name with <c>$charactername</c>, or tagging a line with
    /// <c>$roomid</c>. Null (or a replacement with no <c>$</c>) keeps the
    /// replacement literal, which is what every existing rule gets.
    /// </summary>
    public string Apply(string line, Func<string, string>? expandVariables)
    {
        if (_regex is null || !IsEnabled) return line;
        if (_safe && _hint is not null && !line.Contains(_hint, _cmp)) return line;

        // Expanded at MATCH time, not at rule-add time: the whole point is that
        // $roomid reads the room you are in now, not the one you were in when
        // you wrote the rule. Only reached when the replacement actually holds a
        // '$' AND this rule is about to fire.
        var replacement = _mayHaveVars && expandVariables is not null
            ? expandVariables(Replacement)
            : Replacement;

        try { return _regex.Replace(line, replacement); }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Substitutes); return line; }
    }

    internal void Rebuild(bool safe)
    {
        _safe = safe;
        var opts = RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        // Whole-word wraps the WHOLE pattern in a non-capturing group before
        // anchoring it, so a pattern with alternation still means what it
        // looks like: "\b(?:cat|dog)\b", not "\bcat|dog\b" (which would anchor
        // only the first branch). The user's own capture groups keep their
        // numbers, because the wrapper does not capture.
        var pattern = WholeWord ? $@"\b(?:{Pattern})\b" : Pattern;
        try { _regex = RegexSafety.Build(pattern, opts, safe); _hint = safe ? RegexSafety.LiteralHint(Pattern) : null; }
        catch { _regex = null; _hint = null; }
    }
}

public sealed class SubstituteEngine
{
    private readonly List<SubstituteRule> _rules = new();
    // Copy-on-write iteration snapshot (#251): Apply runs per line on the game
    // loop (and, until the display workstream moves it, on the UI render path)
    // while rule mutations can come from the other thread (`#sub add` vs the
    // config panel / rule-file live reload). Iterating the List during a
    // mutation throws; the hot path iterates this stable array instead,
    // rebuilt after every mutation.
    private volatile SubstituteRule[] _snapshot = Array.Empty<SubstituteRule>();
    private void Resnap() => _snapshot = _rules.ToArray();
    public IReadOnlyList<SubstituteRule> Rules => _rules;
    public ClassEngine? Classes { get; set; }

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config substitutes</c>).
    /// When off, <see cref="Apply"/> returns lines untouched — rules stay loaded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Resolves <c>$globals</c> in replacement text at match time
    /// (public #246). Set by <c>GenieCore</c> to the same expansion typed input
    /// and trigger actions use, so <c>$charactername</c> means the same thing
    /// everywhere. Null in a bare engine (tests, .cfg replay), which keeps
    /// replacements literal.</summary>
    public Func<string, string>? ExpandVariables { get; set; }

    private bool _safetyEnabled = true;
    /// <summary>When true, substitute regexes run with a match-timeout + literal
    /// pre-filter. Toggling rebuilds every rule.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var r in _rules) r.Rebuild(value); }
    }

    public SubstituteRule AddRule(string pattern, string replacement, bool caseSensitive = false, bool isEnabled = true, string className = "", bool wholeWord = false)
    {
        var rule = new SubstituteRule(pattern, replacement, caseSensitive, isEnabled, className, _safetyEnabled, wholeWord);
        _rules.Add(rule);
        Resnap();
        if (!string.IsNullOrEmpty(className)) Classes?.Ensure(className);
        return rule;
    }

    public bool RemoveRule(string pattern)
    {
        var removed = _rules.RemoveAll(r => r.Pattern == pattern) > 0;
        if (removed) Resnap();
        return removed;
    }

    public void Clear() { _rules.Clear(); Resnap(); }

    public string Apply(string line)
    {
        if (!Enabled) return line;
        foreach (var rule in _snapshot)
        {
            if (Classes is not null && !Classes.IsActive(rule.ClassName)) continue;
            line = rule.Apply(line, ExpandVariables);
        }
        return line;
    }
}
