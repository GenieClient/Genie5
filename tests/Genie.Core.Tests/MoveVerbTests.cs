using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #123 — Genie 4 map arcs carry non-DR pacing prefixes ("rt north",
/// "slow south") that DR rejects with "Please rephrase that command." when sent
/// verbatim. <see cref="MoveVerb.Normalize"/> strips the known prefix and sends
/// the bare movement; real DR verbs are left untouched.
/// </summary>
public class MoveVerbTests
{
    [Theory]
    // Pacing prefixes stripped → bare direction (the #123 fix + the pre-existing rt case).
    [InlineData("slow south", "south")]
    [InlineData("slow northwest", "northwest")]
    [InlineData("rt north", "north")]
    [InlineData("RT DOWN", "DOWN")]              // case-insensitive prefix match
    [InlineData("slow   out", "out")]            // collapses padding after the prefix
    // "room" = automapper.cmd MOVE.ROOM (wait for pending moves to settle) —
    // our one-move-per-room pacing already covers the intent, so strip it.
    [InlineData("room go trap door", "go trap door")]
    [InlineData("room sear;-3knock concealed door", "sear;-3knock concealed door")]  // Riverhaven thief door
    [InlineData("roomy go x", "roomy go x")]     // "room" must be its own token
    [InlineData("room", "room")]                 // bare token, nothing to strip
    // Real DR verbs left untouched.
    [InlineData("go small alleyway", "go small alleyway")]
    [InlineData("climb wall", "climb wall")]
    [InlineData("swim west", "swim west")]
    [InlineData("dive pool", "dive pool")]
    [InlineData("search bushes", "search bushes")]
    [InlineData("north", "north")]
    // Not a prefix match: no trailing space / no remainder.
    [InlineData("slower", "slower")]             // "slow" not followed by a space
    [InlineData("rt", "rt")]                     // bare token, nothing to strip
    [InlineData("slow", "slow")]
    public void Normalize_StripsPacingPrefixesOnly(string input, string expected)
    {
        Assert.Equal(expected, MoveVerb.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyOrBlank_ReturnsInput(string input)
    {
        Assert.Equal(input, MoveVerb.Normalize(input));
    }

    // ── Search directive (hidden exits) ─────────────────────────────────────
    // Genie 4 map idiom: move="search go trampled path" means the exit only
    // opens after an in-room `search` (automapper.cmd MOVE.SEARCH). The walker
    // needs the inner move to execute the go-first / search-retry loop.

    [Theory]
    [InlineData("search go trampled path", "go trampled path")]   // Map4 → Aberro Valley
    [InlineData("search go faint trail", "go faint trail")]       // Map11 et al.
    [InlineData("search climb footholds", "climb footholds")]     // Map108 M'Riss
    [InlineData("SEARCH GO HIDDEN DOOR", "GO HIDDEN DOOR")]       // case-insensitive
    [InlineData("  search  go bare spot", "go bare spot")]        // padding tolerated
    public void TryParseSearchDirective_DirectiveForms_ReturnInnerMove(string verb, string expected)
    {
        Assert.True(MoveVerb.TryParseSearchDirective(verb, out var inner));
        Assert.Equal(expected, inner);
    }

    [Theory]
    [InlineData("search bushes")]        // literal game command, not a directive
    [InlineData("search")]               // bare verb
    [InlineData("search go")]            // no target after the inner verb
    [InlineData("searchlight go x")]     // "search" must be its own token
    [InlineData("go trampled path")]     // plain move
    [InlineData("north")]
    [InlineData("")]
    public void TryParseSearchDirective_NonDirectiveForms_ReturnFalse(string verb)
    {
        Assert.False(MoveVerb.TryParseSearchDirective(verb, out _));
    }

    // ── Quick-send chain segments ───────────────────────────────────────────
    // Genie 4 rewrites a ';'-segment starting with '-' to "#send <rest>"
    // (QuickSendChar, Core/Command.cs:254); #send peels leading digits/'.' as
    // a wait-before-send even with no space ("-3knock"). ExpandQuickSends
    // reproduces that at map-dispatch time, emitting the delay with a trailing
    // space because Genie 5's ParseSendDelay requires the boundary.

    [Theory]
    // Real map arcs.
    [InlineData("pull sconce;-1 go door",                                       // Map127 Boar Clan
                "pull sconce;#send 1 go door")]
    [InlineData("push wall;-3 go wall",                                         // Map99 Aesry
                "push wall;#send 3 go wall")]
    [InlineData("sear;-3knock concealed door;-whisp door $haven.pw",            // Map30 Riverhaven (post-Normalize)
                "sear;#send 3 knock concealed door;#send whisp door $haven.pw")]
    [InlineData("pull branch;-.5 push rock;-.5 go hole",                        // Map6 Crossing N Gate
                "pull branch;#send .5 push rock;#send .5 go hole")]
    [InlineData("touch alt;-pray",                                              // Map31a Zaulfung
                "touch alt;#send pray")]
    [InlineData("-search shadow;-go opening",                                   // Map30 Riverhaven
                "#send search shadow;#send go opening")]
    // Untouched forms.
    [InlineData("go small alleyway", "go small alleyway")]
    [InlineData("kneel;go halfling-sized burrow", "kneel;go halfling-sized burrow")]  // mid-word hyphen
    [InlineData("pull lever; go door; open vault; look in vault",               // Map90 Ratha — no dash
                "pull lever; go door; open vault; look in vault")]
    [InlineData("north", "north")]
    public void ExpandQuickSends_RewritesDashSegments(string input, string expected)
    {
        Assert.Equal(expected, MoveVerb.ExpandQuickSends(input));
    }

    [Fact]
    public void ExpandQuickSends_HonoursConfiguredCommandChar()
    {
        Assert.Equal("touch alt;~send pray",
                     MoveVerb.ExpandQuickSends("touch alt;-pray", '~'));
    }

    [Theory]
    [InlineData("-3knock concealed door", "knock concealed door")]
    [InlineData("-1 go door", "go door")]
    [InlineData("-.5 push rock", "push rock")]
    [InlineData("-pray", "pray")]
    public void TryStripQuickSend_RecoversWireCommand(string segment, string expected)
    {
        Assert.True(MoveVerb.TryStripQuickSend(segment, out var cmd));
        Assert.Equal(expected, cmd);
    }

    [Theory]
    [InlineData("go door")]              // no dash
    [InlineData("-")]                    // dash alone
    [InlineData("-3")]                   // delay with no command — stays literal
    [InlineData("")]
    [InlineData(null)]
    public void TryStripQuickSend_NonQuickSendForms_ReturnFalse(string? segment)
    {
        Assert.False(MoveVerb.TryStripQuickSend(segment, out _));
    }
}
