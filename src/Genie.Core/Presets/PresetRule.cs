namespace Genie.Core.Presets;

public sealed class PresetRule
{
    public string Id              { get; set; } = string.Empty;
    public string ForegroundColor { get; set; } = "Default";
    public string BackgroundColor { get; set; } = string.Empty;
    public bool   HighlightLine   { get; set; } = false;

    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;
}
