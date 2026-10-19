namespace Genie.Core.Mapper;

/// <summary>Where a return portal will put you back: a room in a zone file.</summary>
public sealed record PortalOrigin(string Zone, string Room);

/// <summary>
/// Remembers, per zone, the room you stepped through a portal FROM when you entered
/// that zone — the destination of a <see cref="ZoneConnection.ReturnsToOrigin"/>
/// connection. Fang Cove's EXIT portal is the model: it returns you to the Crossing,
/// Shard, … wherever you entered. Arriving by any other means (a boat, walking) leaves
/// no origin, and so no return portal; leaving the zone consumes it.
/// Session memory only: it is not persisted, so after a restart the origin is unknown
/// until you next enter through a portal.
/// </summary>
public sealed class PortalOriginTracker
{
    private readonly Dictionary<string, PortalOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Zone entered → the origin to return to.</summary>
    public IReadOnlyDictionary<string, PortalOrigin> Origins => _origins;

    /// <summary>
    /// The character moved from <paramref name="fromZone"/> (standing in
    /// <paramref name="fromRoom"/>) into <paramref name="toZone"/>.
    /// <paramref name="lastCommand"/> is the last command sent. Only a command that
    /// goes through a portal records an origin; anything else forgets any.
    /// </summary>
    public void OnZoneChanged(string? fromZone, string? fromRoom, string toZone, string? lastCommand)
    {
        if (string.IsNullOrEmpty(toZone) ||
            string.Equals(fromZone, toZone, StringComparison.OrdinalIgnoreCase)) return;

        if (!string.IsNullOrEmpty(fromZone)) _origins.Remove(fromZone);   // leaving consumes it

        if (!string.IsNullOrEmpty(fromZone) && !string.IsNullOrEmpty(fromRoom) && IsPortalCommand(lastCommand))
            _origins[toZone] = new PortalOrigin(fromZone, fromRoom);
        else
            _origins.Remove(toZone);                                      // arrived some other way
    }

    public void Clear() => _origins.Clear();

    /// <summary>True for a movement command that goes through a portal ("go meeting portal").</summary>
    public static bool IsPortalCommand(string? command)
        => !string.IsNullOrWhiteSpace(command) &&
           command.Contains("portal", StringComparison.OrdinalIgnoreCase);
}
