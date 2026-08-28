namespace Genie.Core.Variables;

/// <summary>
/// Connection-state variable names owned by the LIVE session globals
/// (<c>Scripts.Globals</c>) that must never enter the persisted <c>#var</c>
/// store (public #294).
///
/// <para>
/// Genie 4 had a single variable list, so a <c>#var connected …</c> simply
/// overwrote the live reserved variable — with the quirk that
/// <c>VariableList.Add</c> also flipped its type to SaveToFile, after which
/// the "reserved" value was written into <c>variables.cfg</c> forever. Genie 5
/// splits live globals from the persisted store; importing such a file (or
/// typing <c>#var connected 0</c> here) planted a stale <c>connected=1</c> row
/// in the store, which (a) the Configuration ▸ Variables panel displayed as if
/// it were the live flag, and (b) shadowed <c>$connected</c> for scripts run
/// before the first connect of the session, when the live global doesn't exist
/// yet and resolution falls through to the store.
/// </para>
///
/// <para>
/// Enforcement lives in <see cref="VariableStore.Set"/> (one choke point
/// covering every loader: the App's variables.json load, <c>#var load</c> in
/// both formats, rule-file live reload, the Genie 4 importer, and CfgReplay's
/// scratch-store merge) plus <c>CommandEngine.HandleVar</c>, which routes a
/// typed/scripted <c>#var</c> on these names to the live globals for Genie 4
/// behavior parity. Extend the list only with names whose live value is
/// written by the connection layer itself and where a stale persisted copy
/// changes script behavior.
/// </para>
/// </summary>
public static class ReservedConnectionVars
{
    /// <summary>True when <paramref name="name"/> is a reserved
    /// connection-state variable (currently: <c>connected</c>).</summary>
    public static bool Contains(string name) =>
        string.Equals(name, "connected", StringComparison.OrdinalIgnoreCase);
}
