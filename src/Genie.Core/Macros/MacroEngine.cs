using Genie.Core.Classes;

namespace Genie.Core.Macros;

public sealed class MacroRule
{
    public MacroRule(string key, string action, string className = "default")
    { Key = key; Action = action; ClassName = className; }
    public string Key    { get; }
    public string Action { get; }

    /// <summary>
    /// Class this macro belongs to (Genie 4 parity). The macro only fires
    /// when <see cref="ClassEngine.IsActive"/> returns true for this name.
    /// Default <c>"default"</c> always fires.
    /// </summary>
    public string ClassName { get; set; } = "default";

    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;
}

public sealed class MacroEngine
{
    private readonly Dictionary<string, MacroRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional class-scope filter — set by <see cref="GenieCore"/> at startup.
    /// When non-null, <see cref="Get"/> only returns macros whose
    /// <see cref="MacroRule.ClassName"/> is active. Null (offline / draft)
    /// returns the macro regardless.
    /// </summary>
    public ClassEngine? Classes { get; set; }

    public IReadOnlyCollection<MacroRule> Rules => _rules.Values;
    public void Add(string key, string action, string className = "default")
    {
        if (!string.IsNullOrWhiteSpace(key))
            _rules[key] = new MacroRule(key, action, className);
    }
    public bool Remove(string key) => _rules.Remove(key);
    public void Clear() => _rules.Clear();

    /// <summary>
    /// Look up the macro bound to <paramref name="key"/>. Returns null
    /// when no binding exists OR when the macro's class is inactive —
    /// callers should treat both cases as "no macro fires for this key."
    /// </summary>
    public MacroRule? Get(string key)
    {
        if (!_rules.TryGetValue(key, out var r)) return null;
        if (Classes is not null && !Classes.IsActive(r.ClassName)) return null;
        return r;
    }
}
