using System.Text.RegularExpressions;
using Genie.Core.Classes;
using Genie.Core.Diagnostics;

namespace Genie.Core.Shunts;

/// <summary>
/// One <c>#shunt</c> rule (public #248): game lines matching
/// <see cref="Pattern"/> are routed to the named window <see cref="Window"/> —
/// moved out of the main game window, or, with <see cref="Copy"/>, shown in
/// both. Shaped like a gag rule plus a target, because that is what it is: a
/// gag that says where the line went instead of throwing it away.
/// </summary>
public sealed class ShuntRule
{
    private Regex?  _regex;
    private string? _hint;          // literal pre-filter (null = none)
    private bool    _safe = true;
    private readonly StringComparison _cmp;

    public ShuntRule(string pattern, string window, bool copy = false, bool caseSensitive = false,
                     bool isEnabled = true, string className = "", bool safe = true)
    {
        Pattern = pattern; Window = window.Trim(); Copy = copy;
        CaseSensitive = caseSensitive; IsEnabled = isEnabled; ClassName = className;
        _cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Rebuild(safe);
    }

    public string Pattern       { get; }
    /// <summary>The target window's name, as typed — the same names
    /// <c>#echo &gt;Window</c> accepts (a stream window like <c>Atmospherics</c>,
    /// or any other name, which becomes its own window on first use).</summary>
    public string Window        { get; }
    /// <summary>True = the line shows in the target AND stays in the main
    /// window. False (the default) = the line moves to the target.</summary>
    public bool   Copy          { get; }
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
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Shunts); return false; }
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

/// <summary>
/// The <c>#shunt</c> rule set (public #248). Consulted by the display layer for
/// each main-window game line AFTER substitutes and gags: the pattern sees the
/// substituted text (the text you would have read), and a gagged line is gone
/// before a shunt can move it anywhere. First matching rule wins — one line
/// goes to one window.
/// </summary>
public sealed class ShuntEngine
{
    private readonly List<ShuntRule> _rules = new();
    // Copy-on-write iteration snapshot (#251), same reason as GagEngine: Match
    // runs per line on the UI render path while rule mutations can come from
    // the game thread (#shunt from a script) or a rule-file live reload.
    private volatile ShuntRule[] _snapshot = Array.Empty<ShuntRule>();
    private void Resnap() => _snapshot = _rules.ToArray();
    public IReadOnlyList<ShuntRule> Rules => _rules;
    public ClassEngine? Classes { get; set; }

    /// <summary>Engine-wide enable. When off, <see cref="Match"/> never
    /// matches — rules stay loaded.</summary>
    public bool Enabled { get; set; } = true;

    private bool _safetyEnabled = true;
    /// <summary>When true, shunt regexes run with a match-timeout + literal
    /// pre-filter. Toggling rebuilds every rule.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var r in _rules) r.Rebuild(value); }
    }

    public ShuntRule AddRule(string pattern, string window, bool copy = false, bool caseSensitive = false,
                             bool isEnabled = true, string className = "")
    {
        var rule = new ShuntRule(pattern, window, copy, caseSensitive, isEnabled, className, _safetyEnabled);
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

    /// <summary>The first enabled, class-active rule matching
    /// <paramref name="line"/>, or null.</summary>
    public ShuntRule? Match(string line)
    {
        if (!Enabled) return null;
        foreach (var rule in _snapshot)
        {
            if (Classes is not null && !Classes.IsActive(rule.ClassName)) continue;
            if (rule.Matches(line)) return rule;
        }
        return null;
    }
}
