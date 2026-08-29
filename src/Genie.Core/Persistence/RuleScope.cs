namespace Genie.Core.Persistence;

/// <summary>
/// Which config layer a rule belongs to (public #257). A rule loaded from the
/// shared global <c>Config/</c> dir is <see cref="Global"/>; one loaded from
/// (or created for) the connected character's <c>Profiles/{Char}-{Acct}/</c>
/// dir is <see cref="Character"/>. The active set layers Character rules
/// FIRST, then every Global rule whose key no Character rule shadows — order
/// is load-bearing because the pattern engines are first-match-wins and
/// <c>AddRule</c> appends. A Character rule with the same key and
/// <c>IsEnabled = false</c> is therefore a per-character opt-out of a Global
/// rule with no schema change. Saves split by this tag: Character rules write
/// to the profile file, Global rules to the shared file — which is what stops
/// the first panel edit from forking the whole global set into the profile
/// (the #257 bug). Deliberately NOT serialized: a rule's scope IS the file it
/// lives in.
/// </summary>
public enum RuleScope
{
    /// <summary>Shared across every character (the global Config dir).</summary>
    Global,

    /// <summary>This character only (the connected profile's dir). The default
    /// for new rules.</summary>
    Character,
}
