using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Genie.Core.Events;
using Genie.Core.Models;

namespace Genie.Core.Combat;

/// <summary>
/// Turns one flattened <c>assess</c>-stream line into a typed
/// <see cref="AssessRow"/> / <see cref="AssessSelf"/> (public #313).
///
/// Pure and static — it takes the text and link spans a <see cref="TextEvent"/>
/// already carries and returns records, so it is trivially testable and can be
/// reused by any consumer of the stream (public #318's analyze window is meant
/// to follow this same shape).
/// </summary>
public static partial class AssessLineParser
{
    /// <summary>The header that opens an assess block. Treated as a reset
    /// signal alongside <c>&lt;clearStream id="assess"/&gt;</c>, so a block
    /// still starts cleanly if a session ever omits the clear.</summary>
    public const string HeaderPrefix = "You assess your combat situation";

    // "A sleazy lout (2: solidly balanced) is behind you at pole weapon range."
    //  |- name --|  |n|  |-- balance --|  |--------- tail ------------------|
    // Name is lazy so it stops at the FIRST "(n: ...)" group — a creature whose
    // own name contains parentheses would otherwise swallow it.
    private static readonly Regex CreatureRegex = CreaturePattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<name>.+?)\s+\((?<num>\d+):\s*(?<bal>[^)]*)\)\s*(?<tail>.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CreaturePattern();

    // "You (solidly balanced) are facing a sleazy lout (1) at pole weapon range."
    private static readonly Regex SelfRegex = SelfPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^You\s+\((?<bal>[^)]*)\)\s*(?<tail>.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SelfPattern();

    // The target inside the self line, when it carried no link:
    // "... facing a sleazy lout (1) at ...".
    private static readonly Regex SelfTargetRegex = SelfTargetPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<name>.+?)\s+\((?<num>\d+)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SelfTargetPattern();

    // The target's number when the NAME came from a link: the link ends right
    // before it, so what follows is " (1) at ..." with no name text to anchor
    // on — SelfTargetRegex's "(?<name>.+?)\s+" would never match here.
    private static readonly Regex TargetNumberRegex = TargetNumberPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\((?<num>\d+)\)", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TargetNumberPattern();

    // "is behind you at pole weapon range."  ->  pos="behind you", range="pole
    // weapon range". Both the range clause and the full stop are optional: only
    // one capture of this stream exists, so an unknown tail shape must degrade
    // to empty fields, never to a dropped row.
    private static readonly Regex TailRegex = TailPattern();
    [System.Text.RegularExpressions.GeneratedRegex(@"^is\s+(?<pos>.*?)(?:\s+at\s+(?<range>.+?))?\.?\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex TailPattern();

    /// <summary>True when <paramref name="text"/> is the block header.</summary>
    public static bool IsHeader(string? text) =>
        text is not null && text.StartsWith(HeaderPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Parse a creature row. Returns false for the header, the self line,
    /// blanks, and anything without the "(n: balance)" signature — those are
    /// not rows and the caller should leave them to the stream window.
    /// </summary>
    public static bool TryParseCreature(
        string? text, IReadOnlyList<LinkSpan>? links, out AssessRow row)
    {
        row = null!;
        if (string.IsNullOrWhiteSpace(text) || IsHeader(text)) return false;

        var m = CreatureRegex.Match(text);
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups["num"].Value, out var number)) return false;

        var look = FindLink(links, "look");
        var face = FindLink(links, "face");

        // Prefer the link's own display text for the name: the server wraps
        // exactly the creature phrase in <d cmd='look #id'>, so it is more
        // reliable than the regex when a name contains punctuation. Only when
        // the link opens the line, though — on the self line the look link
        // sits mid-sentence and would not be the row subject.
        var name = look is { Start: 0 }
            ? Slice(text, look)
            : m.Groups["name"].Value.Trim();

        var existId = ExistIdFrom(look) is { Length: > 0 } fromLook
            ? fromLook
            : ExistIdFrom(face);

        var tail = m.Groups["tail"].Value;
        // Drop the trailing " | F" action column before reading the tail — the
        // "F" is the face link's display text, not part of the sentence.
        var pipe = tail.LastIndexOf(" | ", StringComparison.Ordinal);
        if (pipe >= 0) tail = tail[..pipe];

        var (position, range) = SplitTail(tail);

        row = new AssessRow(
            Number:      number,
            ExistId:     existId,
            Name:        name,
            Balance:     m.Groups["bal"].Value.Trim(),
            Position:    position,
            Range:       range,
            LookCommand: look?.Command ?? (existId.Length > 0 ? "look #" + existId : ""),
            FaceCommand: face?.Command ?? (existId.Length > 0 ? "face #" + existId : ""),
            RawText:     text);
        return true;
    }

    /// <summary>
    /// Parse the "You (...) are facing ..." opener. Returns false for every
    /// other line, including creature rows that merely start with "You".
    /// </summary>
    public static bool TryParseSelf(
        string? text, IReadOnlyList<LinkSpan>? links, out AssessSelf self)
    {
        self = null!;
        if (string.IsNullOrWhiteSpace(text) || IsHeader(text)) return false;

        var m = SelfRegex.Match(text);
        if (!m.Success) return false;

        var look = FindLink(links, "look");
        var name = look is not null ? Slice(text, look) : "";
        var number = 0;

        // The number follows the name in the self line — "a sleazy lout (1)".
        // Read it from the text after the link so a future wording change
        // (different verb, extra clause) doesn't cost us the id or the name.
        if (look is not null)
        {
            var after = text[Math.Min(look.Start + look.Length, text.Length)..];
            var t = TargetNumberRegex.Match(after);
            if (t.Success) int.TryParse(t.Groups["num"].Value, out number);
        }
        else
        {
            var t = SelfTargetRegex.Match(m.Groups["tail"].Value);
            if (t.Success)
            {
                name = t.Groups["name"].Value.Trim();
                int.TryParse(t.Groups["num"].Value, out number);
            }
        }

        self = new AssessSelf(
            Balance:       m.Groups["bal"].Value.Trim(),
            FacingExistId: ExistIdFrom(look),
            FacingName:    name,
            FacingNumber:  number,
            RawText:       text);
        return true;
    }

    private static (string Position, string Range) SplitTail(string tail)
    {
        var t = TailRegex.Match(tail.Trim());
        if (!t.Success) return ("", "");
        return (t.Groups["pos"].Value.Trim(), t.Groups["range"].Value.Trim());
    }

    /// <summary>First game link whose command starts with <paramref name="verb"/>
    /// (e.g. "look", "face"). URL links are never candidates.</summary>
    private static LinkSpan? FindLink(IReadOnlyList<LinkSpan>? links, string verb)
    {
        if (links is null) return null;
        foreach (var l in links)
            if (!l.IsUrl &&
                l.Command.StartsWith(verb, StringComparison.OrdinalIgnoreCase) &&
                l.Command.Length > verb.Length &&
                char.IsWhiteSpace(l.Command[verb.Length]))
                return l;
        return null;
    }

    /// <summary>Digits after the '#' in "look #45029702"; empty when absent.</summary>
    private static string ExistIdFrom(LinkSpan? link)
    {
        if (link is null) return "";
        var hash = link.Command.IndexOf('#');
        if (hash < 0) return "";
        var end = hash + 1;
        while (end < link.Command.Length && char.IsDigit(link.Command[end])) end++;
        return link.Command[(hash + 1)..end];
    }

    /// <summary>The visible text a link covers, clamped — span offsets come
    /// from the parser and should always be in range, but a malformed line
    /// must not throw inside the game loop.</summary>
    private static string Slice(string text, LinkSpan link)
    {
        if (link.Start < 0 || link.Start >= text.Length) return "";
        var len = Math.Min(link.Length, text.Length - link.Start);
        return len <= 0 ? "" : text.Substring(link.Start, len).Trim();
    }
}
