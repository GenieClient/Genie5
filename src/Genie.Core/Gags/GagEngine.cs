using System.Text.RegularExpressions;
using Genie.Core.Classes;
using Genie.Core.Diagnostics;

namespace Genie.Core.Gags;

public sealed class GagRule
{
    private Regex?  _regex;
    private string? _hint;          // literal pre-filter (null = none)
    private bool    _safe = true;
    private readonly StringComparison _cmp;

    public GagRule(string pattern, bool caseSensitive = false, bool isEnabled = true, string className = "", bool safe = true)
    {
        Pattern = pattern; CaseSensitive = caseSensitive; IsEnabled = isEnabled; ClassName = className;
        _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Rebuild(safe);
    }
    public string Pattern       { get; }
    public bool   CaseSensitive { get; }
    public bool   IsEnabled     { get; set; }
    public string ClassName     { get; }
    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;

    public bool Matches(string line)
    {
        if (_regex is null || !IsEnabled) return false;
        // Cheap literal gate before the regex engine runs.
        if (_safe && _hint is not null && !line.Contains(_hint, _cmp)) return false;
        try { return _regex.IsMatch(line); }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Gags); return false; }
    }

    /// <summary>(Re)build the regex with or without the safety match-timeout.</summary>
    internal void Rebuild(bool safe)
    {
        _safe = safe;
        var opts = RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { _regex = RegexSafety.Build(Pattern, opts, safe); _hint = safe ? RegexSafety.LiteralHint(Pattern) : null; }
        catch { _regex = null; _hint = null; }
    }
}

public sealed class GagEngine
{
    private readonly List<GagRule> _rules = new();
    // Copy-on-write iteration snapshot (#251): ShouldGag runs per line on the
    // game loop AND on the UI render path while rule mutations can come from
    // the other thread. The hot path iterates this stable array, rebuilt after
    // every mutation.
    private volatile GagRule[] _snapshot = Array.Empty<GagRule>();
    private void Resnap() => _snapshot = _rules.ToArray();
    public IReadOnlyList<GagRule> Rules => _rules;
    public ClassEngine? Classes { get; set; }

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config gags</c>).
    /// When off, <see cref="ShouldGag"/> never gags — rules stay loaded.</summary>
    public bool Enabled { get; set; } = true;

    private bool _safetyEnabled = true;
    /// <summary>When true, gag regexes run with a match-timeout + literal
    /// pre-filter. Toggling rebuilds every rule.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var r in _rules) r.Rebuild(value); }
    }

    public GagRule AddRule(string pattern, bool caseSensitive = false, bool isEnabled = true, string className = "")
    {
        var rule = new GagRule(pattern, caseSensitive, isEnabled, className, _safetyEnabled);
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

    public bool ShouldGag(string line)
    {
        if (!Enabled) return false;
        foreach (var rule in _snapshot)
        {
            if (Classes is not null && !Classes.IsActive(rule.ClassName)) continue;
            if (rule.Matches(line)) return true;
        }
        return false;
    }
}
