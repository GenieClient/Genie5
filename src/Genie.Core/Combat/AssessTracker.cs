using System;
using System.Collections.Generic;
using Genie.Core.Events;
using Genie.Core.Models;

namespace Genie.Core.Combat;

/// <summary>
/// Accumulates <c>assess</c>-stream lines into the live
/// <see cref="AssessSnapshot"/> (public #313).
///
/// Owned by <c>GameStateEngine</c>, which feeds it the events it already sees —
/// this type holds the block state machine so the engine's dispatch switch
/// stays a dispatch switch.
///
/// <para><b>Block boundaries.</b> A new assess replaces the previous result
/// rather than appending. The server opens one with
/// <c>&lt;clearStream id="assess"/&gt;</c> followed by the "You assess your
/// combat situation..." header; either is accepted as the reset signal, so a
/// session that sends only one of them still starts a clean block. Rows then
/// append until the next reset — deliberately NOT "one row per block", because
/// the rows of a single assess arrive as separate stream lines with nothing
/// but their order to group them.</para>
/// </summary>
public sealed class AssessTracker
{
    /// <summary>The stream id this tracker consumes.</summary>
    public const string StreamId = "assess";

    private readonly AssessSnapshot      _snapshot;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<AssessRow>     _rows = new();

    /// <summary>True once a block has been opened by a reset signal (or by the
    /// first row seen after one). Guards against a row that arrives with no
    /// header — it opens a block instead of appending to a stale one — while
    /// still letting the remaining rows of that same assess append.</summary>
    private bool _blockOpen;

    public AssessTracker(AssessSnapshot snapshot, Func<DateTimeOffset>? now = null)
    {
        _snapshot = snapshot;
        _now      = now ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Drop everything — the snapshot is no longer about where we are.
    /// Called on room change, for the same reason <c>CreatureStatuses</c> is
    /// cleared there (engagement is room-local, public #202).</summary>
    public void Reset()
    {
        _rows.Clear();
        _blockOpen        = false;
        _snapshot.Rows    = [];
        _snapshot.Self    = null;
        _snapshot.CapturedAt = default;
    }

    /// <summary>Handle <c>&lt;clearStream id="assess"/&gt;</c>. Ignores every
    /// other stream.</summary>
    public void OnClearStream(string? streamId)
    {
        if (!string.Equals(streamId, StreamId, StringComparison.OrdinalIgnoreCase)) return;
        BeginBlock();
    }

    /// <summary>
    /// Handle one text line. Returns true when the line was consumed as assess
    /// structure (header, self line, or creature row) — the caller keeps
    /// publishing it to the stream window either way; the return value is for
    /// tests and diagnostics.
    /// </summary>
    public bool OnText(TextEvent te)
    {
        if (!string.Equals(te.Stream, StreamId, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(te.Text)) return false;

        if (AssessLineParser.IsHeader(te.Text))
        {
            BeginBlock();
            return true;
        }

        if (AssessLineParser.TryParseSelf(te.Text, te.Links, out var self))
        {
            if (!_blockOpen) BeginBlock();
            _snapshot.Self = self;
            return true;
        }

        if (AssessLineParser.TryParseCreature(te.Text, te.Links, out var row))
        {
            if (!_blockOpen) BeginBlock();
            _rows.Add(row);
            // Publish a fresh array rather than exposing the live list: a UI
            // thread may be enumerating Rows while the next row lands here.
            _snapshot.Rows = _rows.ToArray();
            return true;
        }

        return false;
    }

    private void BeginBlock()
    {
        _rows.Clear();
        _blockOpen           = true;
        _snapshot.Rows       = [];
        _snapshot.Self       = null;
        _snapshot.CapturedAt = _now();
    }
}
