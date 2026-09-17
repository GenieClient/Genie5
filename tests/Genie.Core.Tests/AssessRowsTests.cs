using System;
using System.Linq;
using Genie.Core.Combat;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #313 — the <c>assess</c> stream parsed into structured per-creature
/// rows, keyed by the exist ids <c>&lt;crtrStatus&gt;</c> also uses so the two
/// sources join.
///
/// The capture below is simtel's live session from the issue, fed through the
/// real <see cref="DrXmlParser"/> rather than hand-built events — these tests
/// pin the whole path (stream routing, link spans, block reset) the way the
/// panel consumes it.
/// </summary>
public class AssessRowsTests
{
    /// <summary>simtel12's capture (see <see cref="AssessCapture"/>) — fed
    /// through the real parser, not hand-built events, so these tests pin the
    /// whole path the panel consumes.</summary>
    private const string Capture = AssessCapture.Block;

    private static (DrXmlParser Parser, Genie.Core.Models.GameState State, IDisposable Engine) Wire()
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var state  = new Genie.Core.Models.GameState();
        var engine = new Genie.Core.GameState.GameStateEngine(
            parser.GameEvents, state,
            NullLogger<Genie.Core.GameState.GameStateEngine>.Instance);
        return (parser, state, engine);
    }

    // ── End-to-end: the capture becomes rows ─────────────────────────────────

    [Fact]
    public void Capture_yields_one_row_per_creature_in_assess_order()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);

        var rows = state.Combat.Assess.Rows;
        Assert.Equal(4, rows.Count);
        // Assess order is the game's own ordering — the panel preserves it.
        Assert.Equal([1, 2, 3, 4], rows.Select(r => r.Number).ToArray());
        Assert.Equal(
            ["45029699", "45029702", "45029705", "45029711"],
            rows.Select(r => r.ExistId).ToArray());
    }

    [Fact]
    public void Row_fields_come_apart_into_name_balance_position_range()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);

        var fourth = state.Combat.Assess.Rows[3];
        Assert.Equal("A sleazy lout",      fourth.Name);
        Assert.Equal("badly balanced",     fourth.Balance);
        Assert.Equal("moving to flank you", fourth.Position);
        Assert.Equal("pole weapon range",  fourth.Range);
        // The raw line is always kept so a display can fall back to it.
        Assert.StartsWith("A sleazy lout (4: badly balanced)", fourth.RawText);

        var first = state.Combat.Assess.Rows[0];
        Assert.Equal("nimbly balanced", first.Balance);
        Assert.Equal("facing you",      first.Position);
    }

    [Fact]
    public void Rows_carry_the_look_and_face_commands_as_typed_targets()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);

        var row = state.Combat.Assess.Rows[2];
        // These are the server's own commands — they work as typed input, so
        // the interaction layer needs no new server support.
        Assert.Equal("look #45029705", row.LookCommand);
        Assert.Equal("face #45029705", row.FaceCommand);
    }

    [Fact]
    public void Self_line_gives_own_balance_and_current_target()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);

        var self = state.Combat.Assess.Self;
        Assert.NotNull(self);
        Assert.Equal("solidly balanced", self!.Balance);
        Assert.Equal("a sleazy lout",    self.FacingName);
        Assert.Equal("45029699",         self.FacingExistId);
        Assert.Equal(1,                  self.FacingNumber);
    }

    [Fact]
    public void Self_line_is_not_also_counted_as_a_creature_row()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);

        // "You (solidly balanced) are facing a sleazy lout (1) at pole weapon
        // range." carries a (1) but no "(n: balance)" — 4 creatures, not 5.
        Assert.Equal(4, state.Combat.Assess.Rows.Count);
        Assert.DoesNotContain(state.Combat.Assess.Rows, r => r.Name.StartsWith("You"));
    }

    [Fact]
    public void Header_and_blank_lines_produce_no_rows()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed("<pushStream id=\"assess\"/><clearStream id=\"assess\"/>" +
                    "You assess your combat situation...\r\n\r\n<popStream/>\r\n");

        Assert.Empty(state.Combat.Assess.Rows);
        Assert.Null(state.Combat.Assess.Self);
        // The block is still marked as started, so CapturedAt is set.
        Assert.NotEqual(default, state.Combat.Assess.CapturedAt);
    }

    // ── Snapshot semantics ───────────────────────────────────────────────────

    [Fact]
    public void A_second_assess_replaces_the_first_rather_than_appending()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        Assert.Equal(4, state.Combat.Assess.Rows.Count);

        parser.Feed(
            "<pushStream id=\"assess\"/><clearStream id=\"assess\"/>You assess your combat situation...\r\n" +
            "<popStream/><pushStream id=\"assess\"/><d cmd='look #999'>A hunting lynx</d> " +
            "(1: solidly balanced) is facing you at melee range. | <d cmd='face #999'>F</d>\r\n" +
            "<popStream/>\r\n");

        var rows = Assert.Single(state.Combat.Assess.Rows);
        Assert.Equal("A hunting lynx", rows.Name);
        Assert.Equal("999",            rows.ExistId);
        Assert.Equal("melee range",    rows.Range);
        // The previous block's self line goes with it.
        Assert.Null(state.Combat.Assess.Self);
    }

    [Fact]
    public void Room_change_clears_the_snapshot()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        Assert.NotEmpty(state.Combat.Assess.Rows);

        // Engagement — and therefore the assess reading and its exist ids — is
        // room-local, the same rule CreatureStatuses follows (public #202).
        parser.Feed("<nav rm='12345'/>");

        Assert.Empty(state.Combat.Assess.Rows);
        Assert.Null(state.Combat.Assess.Self);
        Assert.True(state.Combat.Assess.IsEmpty);
    }

    [Fact]
    public void Rows_are_republished_as_a_new_list_not_mutated_in_place()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        var captured = state.Combat.Assess.Rows;   // a UI thread's snapshot

        parser.Feed(
            "<pushStream id=\"assess\"/><clearStream id=\"assess\"/>You assess your combat situation...\r\n" +
            "<popStream/><pushStream id=\"assess\"/><d cmd='look #7'>A lout</d> " +
            "(1: nimbly balanced) is facing you at melee range.\r\n<popStream/>\r\n");

        // The list the consumer is holding still has its 4 rows — enumerating
        // it while the engine ingests the next assess cannot throw.
        Assert.Equal(4, captured.Count);
        Assert.Single(state.Combat.Assess.Rows);
    }

    // ── The join with crtrStatus ─────────────────────────────────────────────

    [Fact]
    public void Rows_join_to_crtrStatus_flags_by_exist_id()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        // crtrStatus keeps arriving after the assess — this is the live half of
        // the row, which is why the join happens at read time.
        parser.Feed("<crtrStatus exist=\"45029705\" hostile=\"1\" disengaged=\"1\" flying=\"0\"/>");

        var row = state.Combat.Assess.Rows.Single(r => r.ExistId == "45029705");
        Assert.True(state.Combat.TryGetCreatureStatus(row.ExistId, out var status));
        Assert.True(status.Hostile);
        Assert.True(status.Disengaged);

        // A creature that has sent no status this room simply has none.
        var quiet = state.Combat.Assess.Rows.Single(r => r.ExistId == "45029699");
        Assert.False(state.Combat.TryGetCreatureStatus(quiet.ExistId, out _));
    }

    [Fact]
    public void TryGetCreatureStatus_is_safe_for_a_row_without_an_exist_id()
    {
        var state = new Genie.Core.Models.GameState();
        Assert.False(state.Combat.TryGetCreatureStatus("", out _));
        Assert.False(state.Combat.TryGetCreatureStatus(null, out _));
    }

    // ── Robustness: one capture exists, so unknown shapes must degrade ───────

    [Fact]
    public void An_unrecognised_tail_keeps_the_row_with_empty_position_and_range()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(
            "<pushStream id=\"assess\"/><clearStream id=\"assess\"/>You assess your combat situation...\r\n" +
            "<popStream/><pushStream id=\"assess\"/><d cmd='look #42'>A sleazy lout</d> " +
            "(1: nimbly balanced) has you pinned in some new way we have never seen.\r\n" +
            "<popStream/>\r\n");

        var row = Assert.Single(state.Combat.Assess.Rows);
        Assert.Equal("A sleazy lout",   row.Name);
        Assert.Equal("nimbly balanced", row.Balance);
        Assert.Equal("42",              row.ExistId);
        // Unknown tail → no guesses, but the row (and its targets) survive.
        Assert.Equal("", row.Position);
        Assert.Equal("", row.Range);
        Assert.Equal("face #42", row.FaceCommand);
        Assert.Contains("pinned in some new way", row.RawText);
    }

    [Fact]
    public void A_row_without_links_still_parses_from_text_alone()
    {
        Assert.True(AssessLineParser.TryParseCreature(
            "A sleazy lout (2: solidly balanced) is behind you at pole weapon range.",
            links: null, out var row));

        Assert.Equal(2,                 row.Number);
        Assert.Equal("A sleazy lout",   row.Name);
        Assert.Equal("behind you",      row.Position);
        // No link, no exist id — and therefore no synthesised commands.
        Assert.Equal("", row.ExistId);
        Assert.Equal("", row.FaceCommand);
    }

    [Fact]
    public void A_range_less_row_keeps_its_position()
    {
        Assert.True(AssessLineParser.TryParseCreature(
            "A sleazy lout (3: badly balanced) is behind you.",
            links: null, out var row));

        Assert.Equal("behind you", row.Position);
        Assert.Equal("",           row.Range);
    }

    [Fact]
    public void Other_streams_never_produce_assess_rows()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        // Main-stream text of the same shape is not an assess row.
        parser.Feed("A sleazy lout (1: nimbly balanced) is facing you at pole weapon range.\r\n");
        parser.Feed("<pushStream id=\"combat\"/>A sleazy lout (2: solidly balanced) " +
                    "is behind you at pole weapon range.\r\n<popStream/>\r\n");

        Assert.Empty(state.Combat.Assess.Rows);
    }

    // ── Block-boundary edge cases ────────────────────────────────────────────

    [Fact]
    public void The_header_alone_opens_a_block_when_no_clearStream_arrives()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        Assert.Equal(4, state.Combat.Assess.Rows.Count);

        // Same assess, but this session sent no <clearStream/> — the header
        // still resets, so rows don't pile onto the previous reading.
        parser.Feed(
            "<pushStream id=\"assess\"/>You assess your combat situation...\r\n" +
            "<popStream/><pushStream id=\"assess\"/><d cmd='look #8'>A lout</d> " +
            "(1: nimbly balanced) is facing you at melee range.\r\n<popStream/>\r\n");

        Assert.Single(state.Combat.Assess.Rows);
    }

    [Fact]
    public void A_clearStream_for_another_window_does_not_reset_assess()
    {
        var (parser, state, engine) = Wire();
        using var scope = engine;

        parser.Feed(Capture);
        parser.Feed("<clearStream id=\"inv\"/>");

        Assert.Equal(4, state.Combat.Assess.Rows.Count);
    }
}
