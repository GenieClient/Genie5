namespace Genie.Core.Events;

/// <summary>
/// Decodes the <c>name</c> of an injuries-dialog <c>&lt;image&gt;</c> into a
/// reading. Shared by the parser's self-injuries path (#18) and the
/// other-character injuries window (#156 bespoke override), which carries the
/// same vocabulary.
/// </summary>
public static class InjuryImageName
{
    /// <summary>
    /// <c>name</c> == the region id → healthy; <c>Injury&lt;N&gt;</c> → wound;
    /// <c>Scar&lt;N&gt;</c> → scar; <c>Nsys&lt;N&gt;</c> → nerve damage (the nsys
    /// region never uses the Injury/Scar names — Lich 5 xmlparser.rb:618).
    /// Severity runs 1–3 for every kind; a 0 digit ("Nsys0") reads healthy.
    /// The healthy nsys echo is lowercase "nsys" (= the region id), which must
    /// hit the healthy branch, so the Nsys prefix requires a digit after it.
    /// </summary>
    public static (InjuryKind Kind, int Severity) Decode(string? name)
    {
        name ??= "";
        var kind     = InjuryKind.None;
        var severity = 0;
        if (name.StartsWith("Injury", StringComparison.OrdinalIgnoreCase))
        {
            kind = InjuryKind.Wound;
            int.TryParse(name.AsSpan("Injury".Length), out severity);
        }
        else if (name.StartsWith("Scar", StringComparison.OrdinalIgnoreCase))
        {
            kind = InjuryKind.Scar;
            int.TryParse(name.AsSpan("Scar".Length), out severity);
        }
        else if (name.Length > "Nsys".Length
                 && name.StartsWith("Nsys", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(name.AsSpan("Nsys".Length), out severity))
        {
            kind = severity > 0 ? InjuryKind.Damage : InjuryKind.None;
        }
        // anything else (name echoes the region id) → healthy
        if (kind == InjuryKind.None) severity = 0;
        return (kind, severity);
    }
}
