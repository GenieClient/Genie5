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

    public SubstituteRule(string pattern, string replacement, bool caseSensitive = false, bool isEnabled = true, string className = "", bool safe = true)
    {
        Pattern = pattern; Replacement = replacement; CaseSensitive = caseSensitive; IsEnabled = isEnabled; ClassName = className;
        _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Rebuild(safe);
    }
    public string Pattern       { get; }
    public string Replacement   { get; }
    public bool   CaseSensitive { get; }
    public bool   IsEnabled     { get; set; }
    public string ClassName     { get; }
    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;

    public string Apply(string line)
    {
        if (_regex is null || !IsEnabled) return line;
        if (_safe && _hint is not null && !line.Contains(_hint, _cmp)) return line;
        try { return _regex.Replace(line, Replacement); }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Substitutes); return line; }
    }

    internal void Rebuild(bool safe)
    {
        _safe = safe;
        var opts = RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { _regex = RegexSafety.Build(Pattern, opts, safe); _hint = safe ? RegexSafety.LiteralHint(Pattern) : null; }
        catch { _regex = null; _hint = null; }
    }
}

public sealed class SubstituteEngine
{
    private readonly List<SubstituteRule> _rules = new();
    public IReadOnlyList<SubstituteRule> Rules => _rules;
    public ClassEngine? Classes { get; set; }

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config substitutes</c>).
    /// When off, <see cref="Apply"/> returns lines untouched — rules stay loaded.</summary>
    public bool Enabled { get; set; } = true;

    private bool _safetyEnabled = true;
    /// <summary>When true, substitute regexes run with a match-timeout + literal
    /// pre-filter. Toggling rebuilds every rule.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var r in _rules) r.Rebuild(value); }
    }

    public SubstituteRule AddRule(string pattern, string replacement, bool caseSensitive = false, bool isEnabled = true, string className = "")
    {
        var rule = new SubstituteRule(pattern, replacement, caseSensitive, isEnabled, className, _safetyEnabled);
        _rules.Add(rule);
        if (!string.IsNullOrEmpty(className)) Classes?.Ensure(className);
        return rule;
    }

    public bool RemoveRule(string pattern) => _rules.RemoveAll(r => r.Pattern == pattern) > 0;
    public void Clear() => _rules.Clear();

    public string Apply(string line)
    {
        if (!Enabled) return line;
        foreach (var rule in _rules)
        {
            if (Classes is not null && !Classes.IsActive(rule.ClassName)) continue;
            line = rule.Apply(line);
        }
        return line;
    }
}
