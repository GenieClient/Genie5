using System.Collections.Generic;

namespace Genie.Core.Models;

/// <summary>
/// One creature row from the <c>assess</c> stream (public #313).
///
/// The server sends each row as its own <c>&lt;pushStream id="assess"&gt;</c>
/// line, e.g.
/// <code>
/// &lt;d cmd='look #45029702'&gt;A sleazy lout&lt;/d&gt; (2: solidly balanced) is behind you at pole weapon range. | &lt;d cmd='face #45029702'&gt;F&lt;/d&gt;
/// </code>
/// which the parser flattens to the text in <see cref="RawText"/> plus two
/// <c>LinkSpan</c>s. The <c>#id</c> in those commands is the creature's
/// <b>exist id</b> — the same key <c>&lt;crtrStatus&gt;</c> uses, which is what
/// lets the two sources join (see <c>CombatState.TryGetCreatureStatus</c>).
///
/// Every field but <see cref="RawText"/> is best-effort: only one live capture
/// of this stream exists, so a row whose tail doesn't match the known shape is
/// still kept (with empty <see cref="Position"/>/<see cref="Range"/>) rather
/// than dropped. <see cref="RawText"/> is always the full line, so a display
/// can fall back to it.
/// </summary>
/// <param name="Number">The game's own ordinal — the <c>2</c> in "(2: solidly
/// balanced)". This is what the player types in commands that take a number,
/// and it is the assess ordering the panel preserves.</param>
/// <param name="ExistId">Creature exist id, digits only ("45029702"). Empty
/// when the row carried no <c>&lt;d cmd&gt;</c> link to take it from.</param>
/// <param name="Name">Display name ("A sleazy lout").</param>
/// <param name="Balance">Balance phrase ("solidly balanced").</param>
/// <param name="Position">Position phrase relative to you ("facing you",
/// "behind you", "moving to flank you"). Empty when unrecognised.</param>
/// <param name="Range">Range phrase ("pole weapon range"). Empty when the row
/// carried none.</param>
/// <param name="LookCommand">Command the name links to ("look #45029702").</param>
/// <param name="FaceCommand">Command the "F" links to ("face #45029702").
/// Synthesised from <see cref="ExistId"/> when the row had no face link.</param>
/// <param name="RawText">The whole flattened line, links stripped.</param>
public sealed record AssessRow(
    int    Number,
    string ExistId,
    string Name,
    string Balance,
    string Position,
    string Range,
    string LookCommand,
    string FaceCommand,
    string RawText);

/// <summary>
/// The "You (solidly balanced) are facing a sleazy lout (1) at pole weapon
/// range." line that opens an assess block — your own balance, and which
/// creature you are currently facing.
/// </summary>
/// <param name="Balance">Your balance phrase ("solidly balanced").</param>
/// <param name="FacingExistId">Exist id of the creature you face; empty when
/// the line named none.</param>
/// <param name="FacingName">Display name of the creature you face.</param>
/// <param name="FacingNumber">That creature's assess number, or 0.</param>
/// <param name="RawText">The whole flattened line.</param>
public sealed record AssessSelf(
    string Balance,
    string FacingExistId,
    string FacingName,
    int    FacingNumber,
    string RawText);

/// <summary>
/// The latest <c>assess</c> result — a snapshot, not a running log: each new
/// assess replaces the previous one (the server prefixes one with
/// <c>&lt;clearStream id="assess"/&gt;</c>, and the "You assess your combat
/// situation..." header is treated as a second, equivalent reset signal).
///
/// <para><b>Freshness.</b> Assess is a point-in-time reading; nothing in the
/// stream retracts a row when a creature dies or flees. The snapshot is
/// cleared on room change (engagement is room-local — the same rule
/// <c>CreatureStatuses</c> follows, public #202), and <see cref="CapturedAt"/>
/// lets a display age it. Live-changing flags should be read from
/// <c>CombatState.CreatureStatuses</c> by exist id rather than assumed from
/// the row.</para>
///
/// <para><b>Threading.</b> <see cref="Rows"/> is replaced wholesale on each
/// append, never mutated in place, so a UI thread enumerating it is safe while
/// the engine ingests the next row.</para>
/// </summary>
public sealed class AssessSnapshot
{
    /// <summary>Creature rows in the order the server sent them (assess
    /// order). Empty until the first assess of the session/room.</summary>
    public IReadOnlyList<AssessRow> Rows { get; internal set; } = [];

    /// <summary>The opening "You (…) are facing …" line; null when the block
    /// had none.</summary>
    public AssessSelf? Self { get; internal set; }

    /// <summary>LOCAL clock instant the current block started (its reset
    /// signal). Default when no assess has been seen.</summary>
    public DateTimeOffset CapturedAt { get; internal set; }

    /// <summary>True when the snapshot holds nothing to show.</summary>
    public bool IsEmpty => Rows.Count == 0 && Self is null;
}
