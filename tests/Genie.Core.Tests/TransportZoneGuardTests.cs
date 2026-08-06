using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// The automapper must not auto-jump into the community "Transports" map (zone
/// id 998 — ferries/gondolas/barges) on a global room-search miss: those rooms
/// exist in no real zone, and Genie 4 kept <c>$zoneid</c> on the bank zone while
/// aboard, which <c>$zoneid</c>-driven travel scripts depend on. This locks the
/// predicate that drives that guard (see <c>MapperViewModel.TryAutoLoadZoneFor</c>).
/// </summary>
public class TransportZoneGuardTests
{
    [Theory]
    [InlineData("Map998_Transports", true)]     // the canonical DR transports map
    [InlineData("Map998_transports", true)]     // case-insensitive
    [InlineData("Transports", true)]            // bare name variant
    [InlineData("Map60_Southern_Trade_Route_Part_1", false)]  // a real bank zone
    [InlineData("Map1_Crossing", false)]
    [InlineData("Map7_Northern_Trade_Road", false)]           // "Trade_Road" ≠ Transports
    [InlineData("Map30_Riverhaven", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Identifies_only_the_transports_map(string? zoneFile, bool expected)
        => Assert.Equal(expected, AutoMapperEngine.IsTransientTransportZone(zoneFile));
}
