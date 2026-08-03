using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Arc-vs-sent-command matching for the mapper's graph-walk tier
/// (<see cref="AutoMapperEngine.MoveCommandMatches"/>). Community maps author
/// arcs whose raw string never appears on the wire:
/// <list type="bullet">
///   <item>Search directive — <c>move="search go trampled path"</c> (hidden
///         exit): the client sends <c>search</c> and the go separately.</item>
///   <item>Semicolon chain — <c>move="'grek;go door"</c> (Map 10: say the
///         password, then go): the command pipeline splits on the separator
///         and each segment is sent individually.</item>
/// </list>
/// Without these relaxations, position tracking through such arcs always
/// missed tier (b) and had to be rescued by fingerprint tiers — or wasn't.
/// </summary>
public class ArcMoveMatchingTests
{
    [Theory]
    // Plain moves — exact, case-insensitive.
    [InlineData("go small alleyway", "go small alleyway", true)]
    [InlineData("go small alleyway", "GO SMALL ALLEYWAY", true)]
    [InlineData("go small alleyway", "go alleyway", false)]
    // Search-directive arcs match the inner move actually sent.
    [InlineData("search go trampled path", "go trampled path", true)]
    [InlineData("search go trampled path", "search go trampled path", true)]  // exact still works
    [InlineData("search climb footholds", "climb footholds", true)]
    [InlineData("search go trampled path", "go faint trail", false)]
    [InlineData("search go trampled path", "search", false)]   // the search itself isn't the move
    // Semicolon-chain arcs match any individual segment.
    [InlineData("'grek;go door", "go door", true)]
    [InlineData("'grek;go door", "'grek", true)]
    [InlineData("'grek;go door", "'grek;go door", true)]       // exact still works
    [InlineData("'grek; go door", "go door", true)]            // padding tolerated
    [InlineData("'grek;go door", "go gate", false)]
    // Quick-send segments (G4 '-' = "#send [delay] cmd") match the bare command
    // the queue ultimately puts on the wire.
    [InlineData("pull sconce;-1 go door", "go door", true)]                // Map127 Boar Clan
    [InlineData("pull sconce;-1 go door", "pull sconce", true)]
    [InlineData("touch alt;-pray", "pray", true)]                          // Map31a Zaulfung
    [InlineData("pull sconce;-1 go door", "go gate", false)]
    // Chain-leading pacing prefixes are stripped before segment matching:
    // "room sear;…" dispatches "sear" first; "rt lie; northeast" sends "lie".
    [InlineData("room sear;-3knock concealed door;-whisp door $haven.pw", "sear", true)]        // Map30 Riverhaven
    [InlineData("room sear;-3knock concealed door;-whisp door $haven.pw", "knock concealed door", true)]
    [InlineData("rt lie; northeast", "lie", true)]                         // Map98 Road to Aesry
    // Empty arc move never matches.
    [InlineData("", "go door", false)]
    [InlineData(null, "go door", false)]
    public void MoveCommandMatches_CoversCommunityArcIdioms(string? arcMove, string used, bool expected)
    {
        Assert.Equal(expected, AutoMapperEngine.MoveCommandMatches(arcMove, used));
    }
}
