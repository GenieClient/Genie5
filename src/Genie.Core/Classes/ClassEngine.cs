using Genie.Core.Persistence;

namespace Genie.Core.Classes;

public sealed class ClassEngine
{
    private readonly Dictionary<string, bool> _classes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Config layer of each class (public #257/#315): only Global entries are
    /// recorded — anything absent is <see cref="RuleScope.Character"/>, the
    /// default for a new class. Kept beside the name→state map rather than in
    /// it so every existing reader of <see cref="GetAll"/> stays unchanged.
    /// </summary>
    private readonly HashSet<string> _global = new(StringComparer.OrdinalIgnoreCase);

    public ClassEngine() { _classes["default"] = true; }

    public IReadOnlyCollection<string> Names => _classes.Keys;

    /// <summary>
    /// Fires after any state-changing operation (Set / Remove / Clear /
    /// ActivateAll / DeactivateAll). Genie 4 raises an <c>EventClassChange</c>
    /// after #class commands so the UI and rule engines can refresh their
    /// class-gated state. We surface the same hook here.
    /// </summary>
    public event Action? Changed;

    public bool IsActive(string? className)
    {
        if (string.IsNullOrEmpty(className)) return true;
        if (className.Equals("default", StringComparison.OrdinalIgnoreCase)) return true;
        return _classes.TryGetValue(className, out var v) && v;
    }

    public bool Ensure(string className, bool defaultActive = true)
    {
        if (string.IsNullOrEmpty(className)) return true;
        if (className.Equals("default", StringComparison.OrdinalIgnoreCase)) return true;
        if (!_classes.TryGetValue(className, out var v))
        {
            _classes[className] = defaultActive;
            Changed?.Invoke();
            return defaultActive;
        }
        return v;
    }

    public void Set(string className, bool active)
    {
        if (string.IsNullOrEmpty(className)) return;
        if (className.Equals("default", StringComparison.OrdinalIgnoreCase)) { _classes["default"] = true; return; }
        // A runtime change to a shared class's state (#class, a script, the
        // panel) becomes this character's override — the same outcome the
        // pre-split save produced, and it keeps one character's toggles out
        // of every other character's shared file. Loaders re-tag explicitly.
        if (_classes.TryGetValue(className, out var old) && old != active) _global.Remove(className);
        _classes[className] = active;
        Changed?.Invoke();
    }

    /// <summary>Which config file <paramref name="className"/> saves back to
    /// (public #257/#315). Unknown names and <c>default</c> report Character.</summary>
    public RuleScope ScopeOf(string className) =>
        _global.Contains(className) ? RuleScope.Global : RuleScope.Character;

    /// <summary>Tag an existing class with its config layer. No-op for an
    /// unknown name or the built-in <c>default</c> class (never saved).</summary>
    public void SetScope(string className, RuleScope scope)
    {
        if (string.IsNullOrEmpty(className) || !_classes.ContainsKey(className)) return;
        if (className.Equals("default", StringComparison.OrdinalIgnoreCase)) return;
        if (scope == RuleScope.Global) _global.Add(className);
        else                           _global.Remove(className);
    }

    public bool Remove(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        if (className.Equals("default", StringComparison.OrdinalIgnoreCase)) return false;
        var removed = _classes.Remove(className);
        _global.Remove(className);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        _classes.Clear();
        _global.Clear();
        _classes["default"] = true;
        Changed?.Invoke();
    }

    public void ActivateAll()
    {
        foreach (var k in _classes.Keys.ToList())
        {
            if (!_classes[k]) _global.Remove(k);   // changed → this character's override (see Set)
            _classes[k] = true;
        }
        Changed?.Invoke();
    }

    public void DeactivateAll()
    {
        foreach (var k in _classes.Keys.ToList())
            if (!k.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                if (_classes[k]) _global.Remove(k);
                _classes[k] = false;
            }
        Changed?.Invoke();
    }

    public IReadOnlyDictionary<string, bool> GetAll() => _classes;
}
