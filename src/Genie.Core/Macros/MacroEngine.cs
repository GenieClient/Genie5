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
    /// Lookup index keyed by <see cref="MacroKeyNormalizer"/> output, so a
    /// macro stored under Genie 4's spelling (<c>NumPad0</c>, <c>F1, Shift</c>,
    /// <c>Escape</c>) is found by the canonical key the runtime builds from a
    /// keystroke (<c>num0</c>, <c>shift+f1</c>, <c>esc</c>).
    /// </summary>
    /// <remarks>
    /// Indexing at lookup rather than rewriting <see cref="MacroRule.Key"/> on
    /// the way in is deliberate: the key keeps the spelling the user's file
    /// uses, so the <c>macros.cfg</c> dual-write emits what it read and a
    /// settings folder shared with a Genie 4 install is not silently
    /// rewritten into a dialect Genie 4 cannot parse.
    /// </remarks>
    private readonly Dictionary<string, MacroRule> _byNormalizedKey = new(StringComparer.OrdinalIgnoreCase);

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
        if (string.IsNullOrWhiteSpace(key)) return;

        var rule = new MacroRule(key, action, className);
        _rules[key] = rule;

        var normalized = MacroKeyNormalizer.Normalize(key);
        if (!string.IsNullOrEmpty(normalized)) _byNormalizedKey[normalized] = rule;
    }

    public bool Remove(string key)
    {
        if (!_rules.Remove(key, out var removed)) return false;

        // Only drop the index entry if it still points at the rule we removed;
        // another spelling of the same key may have replaced it since.
        var normalized = MacroKeyNormalizer.Normalize(key);
        if (!string.IsNullOrEmpty(normalized) &&
            _byNormalizedKey.TryGetValue(normalized, out var indexed) &&
            ReferenceEquals(indexed, removed))
        {
            _byNormalizedKey.Remove(normalized);
        }
        return true;
    }

    public void Clear()
    {
        _rules.Clear();
        _byNormalizedKey.Clear();
    }

    /// <summary>
    /// Look up the macro bound to <paramref name="key"/>. Returns null
    /// when no binding exists OR when the macro's class is inactive —
    /// callers should treat both cases as "no macro fires for this key."
    /// </summary>
    public MacroRule? Get(string key)
    {
        // Exact spelling first, then the normalized index so a Genie 4-spelled
        // binding is reachable from the canonical runtime key.
        if (!_rules.TryGetValue(key, out var r) &&
            !_byNormalizedKey.TryGetValue(MacroKeyNormalizer.Normalize(key), out r))
            return null;

        if (Classes is not null && !Classes.IsActive(r.ClassName)) return null;
        return r;
    }
}
