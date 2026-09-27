namespace Genie.Core.Variables;

public sealed class VariableValue
{
    public VariableValue(string name, string value, VariableScope scope = VariableScope.User)
    {
        Name  = name;
        Value = value;
        Scope = scope;
    }
    public string        Name  { get; }
    public string        Value { get; set; }
    public VariableScope Scope { get; }

    /// <summary>Which config file this variable saves back to (public
    /// #257/#315): the shared global <c>variables.json</c> or this
    /// character's. Unrelated to <see cref="Scope"/> (the runtime kind).
    /// Never serialized: the file a variable lives in IS its layer.</summary>
    public Persistence.RuleScope ConfigScope { get; set; } = Persistence.RuleScope.Character;
}

public enum VariableScope { User, Temporary, Reserved, Server }
