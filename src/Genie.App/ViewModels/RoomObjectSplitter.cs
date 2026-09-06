using System;
using System.Collections.Generic;

namespace Genie.App.ViewModels;

/// <summary>One object's slice of the room-objects line: a range into the
/// original component text, so a caller can map the component's bold spans
/// (creatures) onto the rows it produces.</summary>
public readonly record struct RoomObjectSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Splits DR's room-objects line ("You also see a stone urn and a wide arch.")
/// into one entry per object, for the Objects panel (issue #329).
///
/// <b>Why this is heuristic.</b> Unlike the room's creatures — which arrive
/// wrapped in <c>&lt;pushBold/&gt;</c> and so have exact spans — plain objects
/// carry NO per-item markup at all. Verified against every recorded session:
/// the <c>room objs</c> component is prose plus bold markers, with no
/// <c>&lt;a&gt;</c> links to key off. So the only separators available are the
/// commas and the trailing conjunction of an English list, and item names are
/// free to contain the word "and" themselves. A real recorded line:
///
///   You also see the firewood peddler Mags, Rartan's Collegium of Inner
///   Juggling and Reflexes and a trodden dirt path.
///
/// Two rules keep that (and every other recorded line) correct:
///
///   1. <b>Split commas first, then only the LAST " and " in the final
///      segment.</b> English lists put the conjunction between the last two
///      items, so an earlier "and" — "Juggling and Reflexes" — is part of a
///      name, not a separator.
///   2. <b>Only split on that conjunction when the tail reads like a new
///      item</b>, i.e. it opens with a determiner ("a wide arch", "the Guard
///      House", "some stone stairs") or a proper noun. This is what keeps a
///      name like "a mop and pail set" in one piece — "pail set" opens like
///      neither, so it stays part of the item before it.
///
/// Both rules are best-effort by nature. An item whose name ends in
/// "and &lt;determiner&gt; …" will still split in the wrong place; that is
/// disclosed on the issue rather than papered over, and cannot be fixed
/// client-side without per-item markup from the game.
/// </summary>
public static class RoomObjectSplitter
{
    private const string LeadIn = "You also see";
    private const string Conjunction = " and ";

    /// <summary>Words that open a new list item. A tail starting with one of
    /// these (or with a capital letter — a proper noun like "Rartan's
    /// Collegium") is treated as a separate object.</summary>
    private static readonly HashSet<string> Determiners =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "some", "several", "many", "few", "one", "two",
            "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "his", "her", "its", "their",
        };

    /// <summary>
    /// Split <paramref name="content"/> — the raw <c>room objs</c> text — into
    /// one span per object. Returns an empty list for an empty or lead-in-only
    /// line. Spans index into <paramref name="content"/> unchanged, so bold
    /// (creature) spans from the same ComponentEvent map straight onto them.
    /// </summary>
    public static IReadOnlyList<RoomObjectSpan> Split(string? content)
    {
        var result = new List<RoomObjectSpan>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        // ── Strip the lead-in and the sentence period ────────────────────
        int start = 0, end = content.Length;
        while (start < end && char.IsWhiteSpace(content[start])) start++;

        if (string.Compare(content, start, LeadIn, 0, LeadIn.Length,
                           StringComparison.OrdinalIgnoreCase) == 0)
        {
            start += LeadIn.Length;
            if (start < end && content[start] == ':') start++;   // defensive: not a form DR sends
            while (start < end && char.IsWhiteSpace(content[start])) start++;
        }

        while (end > start && char.IsWhiteSpace(content[end - 1])) end--;
        if (end > start && content[end - 1] == '.') end--;
        while (end > start && char.IsWhiteSpace(content[end - 1])) end--;
        if (start >= end) return result;

        // ── Commas ───────────────────────────────────────────────────────
        var segments = new List<RoomObjectSpan>();
        int segStart = start;
        for (int i = start; i < end; i++)
        {
            if (content[i] != ',') continue;
            segments.Add(new RoomObjectSpan(segStart, i - segStart));
            segStart = i + 1;
        }
        segments.Add(new RoomObjectSpan(segStart, end - segStart));

        // ── The trailing conjunction, in the last segment only ───────────
        for (int s = 0; s < segments.Count; s++)
        {
            var seg = Trim(content, segments[s]);
            // An Oxford-comma list ("A, B, and C") leaves a segment opening
            // with "and " — strip it before anything else looks at the text.
            seg = StripLeadingAnd(content, seg);
            if (seg.Length <= 0) continue;

            if (s == segments.Count - 1 && TrySplitConjunction(content, seg, out var head, out var tail))
            {
                Add(result, content, head);
                Add(result, content, tail);
            }
            else
            {
                Add(result, content, seg);
            }
        }

        return result;
    }

    /// <summary>Convenience overload for callers that only want the text.</summary>
    public static IReadOnlyList<string> SplitText(string? content)
    {
        var spans = Split(content);
        var text  = new List<string>(spans.Count);
        foreach (var s in spans) text.Add(content!.Substring(s.Start, s.Length));
        return text;
    }

    /// <summary>Split <paramref name="seg"/> at its LAST " and " when the tail
    /// opens like a new item. False leaves the segment whole.</summary>
    private static bool TrySplitConjunction(string content, RoomObjectSpan seg,
                                            out RoomObjectSpan head, out RoomObjectSpan tail)
    {
        head = tail = default;

        int at = content.LastIndexOf(Conjunction, seg.End - 1, seg.Length, StringComparison.Ordinal);
        if (at < seg.Start) return false;

        var h = Trim(content, new RoomObjectSpan(seg.Start, at - seg.Start));
        var t = Trim(content, new RoomObjectSpan(at + Conjunction.Length, seg.End - at - Conjunction.Length));
        if (h.Length <= 0 || t.Length <= 0) return false;
        if (!OpensNewItem(content, t)) return false;

        head = h;
        tail = t;
        return true;
    }

    /// <summary>True when the span's first word is a determiner, or the span
    /// starts with a capital (a proper noun — "Rartan's Collegium").</summary>
    private static bool OpensNewItem(string content, RoomObjectSpan span)
    {
        if (span.Length <= 0) return false;
        if (char.IsUpper(content[span.Start])) return true;

        int i = span.Start;
        while (i < span.End && !char.IsWhiteSpace(content[i])) i++;
        return Determiners.Contains(content.Substring(span.Start, i - span.Start));
    }

    private static RoomObjectSpan StripLeadingAnd(string content, RoomObjectSpan span)
    {
        const string and = "and ";
        if (span.Length > and.Length &&
            string.Compare(content, span.Start, and, 0, and.Length, StringComparison.OrdinalIgnoreCase) == 0)
            return Trim(content, new RoomObjectSpan(span.Start + and.Length, span.Length - and.Length));
        return span;
    }

    private static RoomObjectSpan Trim(string content, RoomObjectSpan span)
    {
        int s = span.Start, e = span.End;
        while (s < e && char.IsWhiteSpace(content[s])) s++;
        while (e > s && char.IsWhiteSpace(content[e - 1])) e--;
        return new RoomObjectSpan(s, e - s);
    }

    private static void Add(List<RoomObjectSpan> into, string content, RoomObjectSpan span)
    {
        span = Trim(content, span);
        if (span.Length > 0) into.Add(span);
    }
}
