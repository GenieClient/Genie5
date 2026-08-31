using System.Text.RegularExpressions;

namespace Genie.Core.Highlights;

public sealed class NameRule
{
    public NameRule(string name, string foregroundColor, string backgroundColor = "")
    { Name = name; ForegroundColor = foregroundColor; BackgroundColor = backgroundColor; }
    public string Name            { get; }
    public string ForegroundColor { get; }
    public string BackgroundColor { get; }

    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;
}

public sealed class NameHighlightEngine
{
    // Concurrent + volatile regex (#251): Match/MatchAll run on the UI render
    // path per line while `#names add` from a script mutates the engine on the
    // game thread. The combined regex is rebuilt as a fresh object and swapped
    // by reference, so a reader always sees a coherent index.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, NameRule> _rules = new(StringComparer.OrdinalIgnoreCase);
    private volatile Regex? _regex;

    public IReadOnlyCollection<NameRule> Rules => (IReadOnlyCollection<NameRule>)_rules.Values;

    public void Add(string name, string foregroundColor, string backgroundColor = "")
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _rules[name] = new NameRule(name, foregroundColor, backgroundColor);
        RebuildIndex();
    }

    public bool Remove(string name) { var r = _rules.TryRemove(name, out _); if (r) RebuildIndex(); return r; }
    public void Clear() { _rules.Clear(); _regex = null; }
    public NameRule? Get(string name) => _rules.TryGetValue(name, out var r) ? r : null;

    public (NameRule Rule, int Start, int Length)? Match(string line)
    {
        if (_regex is null) return null;
        var m = _regex.Match(line);
        if (!m.Success) return null;
        var rule = _rules.GetValueOrDefault(m.Value);
        return rule is null ? null : (rule, m.Index, m.Length);
    }

    public IEnumerable<(NameRule Rule, int Start, int Length)> MatchAll(string line)
    {
        if (_regex is null) yield break;
        int next = 0;
        foreach (Match m in _regex.Matches(line))
        {
            if (m.Index < next) continue;
            var rule = _rules.GetValueOrDefault(m.Value);
            if (rule is null) continue;
            yield return (rule, m.Index, m.Length);
            next = m.Index + m.Length;
        }
    }

    private void RebuildIndex()
    {
        if (_rules.Count == 0) { _regex = null; return; }
        var names = _rules.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape);
        _regex = new Regex(@"\b(" + string.Join("|", names) + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }
}
