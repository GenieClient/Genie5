namespace Genie.Core.Variables;

public sealed class VariableStore
{
    private readonly Dictionary<string, VariableValue> _variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Add or update a variable. Returns false (and stores nothing) for a
    /// reserved connection-state name — those live in the session globals and
    /// a persisted copy is stale by definition (public #294). This is the one
    /// choke point every store writer funnels through (typed <c>#var</c>, the
    /// variables.json / <c>#var load</c> / live-reload loaders, the Genie 4
    /// importer, CfgReplay's merge), so a <c>connected=1</c> row carried in
    /// from a Genie 4 profile is dropped on load and disappears from the
    /// files at the next save — existing profiles self-heal.
    /// </summary>
    public bool Set(string name, string value, VariableScope scope = VariableScope.User)
    {
        if (ReservedConnectionVars.Contains(name)) return false;
        if (_variables.ContainsKey(name))
            _variables[name].Value = value;
        else
            _variables[name] = new VariableValue(name, value, scope);
        return true;
    }

    public string? Get(string name)
        => _variables.TryGetValue(name, out var v) ? v.Value : null;

    public bool Remove(string name) => _variables.Remove(name);

    public void ClearUserVariables()
    {
        var keys = _variables.Where(kv => kv.Value.Scope == VariableScope.User)
                             .Select(kv => kv.Key).ToList();
        foreach (var k in keys) _variables.Remove(k);
    }

    public IReadOnlyDictionary<string, VariableValue> GetAll() => _variables;

    public string Expand(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = input;
        foreach (var kvp in _variables)
            result = result.Replace("$" + kvp.Key, kvp.Value.Value);
        return result;
    }
}
