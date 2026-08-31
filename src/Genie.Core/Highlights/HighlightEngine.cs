using Genie.Core.Classes;

namespace Genie.Core.Highlights;

public sealed class HighlightEngine
{
    private readonly List<HighlightRule> _rules = new();
    // Copy-on-write iteration snapshot (#251): highlight matching runs on the
    // UI render path per line while rule mutations can arrive on the game loop
    // (`#highlight add` from a script). The hot paths iterate this stable
    // array, rebuilt after every mutation.
    private volatile HighlightRule[] _snapshot = Array.Empty<HighlightRule>();
    private void Resnap() => _snapshot = _rules.ToArray();
    /// <summary>Stable point-in-time rule array for lock-free iteration off the
    /// owning thread (the App tokenizer reads this, not <see cref="Rules"/>).</summary>
    public IReadOnlyList<HighlightRule> RuleSnapshot => _snapshot;
    public IReadOnlyList<HighlightRule> Rules => _rules;
    public ClassEngine? Classes { get; set; }

    /// <summary>Master enable (File ▸ Master Toggles / <c>#config highlights</c>).
    /// When off, no rule matches — rules stay loaded and editable.</summary>
    public bool Enabled { get; set; } = true;

    private bool _safetyEnabled = true;
    /// <summary>When true, regex-type highlight rules run with a match-timeout +
    /// literal pre-filter. Toggling rebuilds every rule.</summary>
    public bool SafetyEnabled
    {
        get => _safetyEnabled;
        set { if (_safetyEnabled == value) return; _safetyEnabled = value; foreach (var r in _rules) r.Rebuild(value); }
    }

    public HighlightRule AddRule(string pattern, string foregroundColor, string backgroundColor = "",
                                 HighlightMatchType matchType = HighlightMatchType.String,
                                 bool caseSensitive = false, bool isEnabled = true, string className = "",
                                 string soundFile = "", string speak = "", IEnumerable<string>? windows = null)
    {
        var rule = new HighlightRule(pattern, foregroundColor, backgroundColor, matchType, caseSensitive, isEnabled, className, _safetyEnabled, soundFile, speak, windows);
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

    public HighlightRule? Match(string plainText)
    {
        if (!Enabled) return null;
        foreach (var rule in _snapshot)
            if (rule.IsEnabled && (Classes?.IsActive(rule.ClassName) ?? true) && rule.Matches(plainText))
                return rule;
        return null;
    }
}
