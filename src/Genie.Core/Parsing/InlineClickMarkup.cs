using System.Text;
using System.Text.RegularExpressions;
using Genie.Core.Events;

namespace Genie.Core.Parsing;

/// <summary>
/// Genie 4's inline click markup (public #362): any output text containing
/// <c>{display:command}</c> shows <c>display</c> as a clickable link that runs
/// <c>command</c>, anywhere on the line, as many times as it appears —
/// <c>#echo Go {north:north} or {south:south}</c> renders "Go north or south" with
/// two independent links. It is not tied to a command: Genie 4 runs it in the same
/// per-line pass as highlights (<c>ComponentRichTextBox.ParseHighlights</c>), so it
/// applies to game text, <c>#echo</c>, script output and named windows alike.
///
/// <para><b>The pattern is Genie 4's, unchanged</b> (<c>ComponentRichTextBox.cs:518</c>):
/// <c>{([^{]*):([^{]*)}</c>. Both groups are greedy, so the display is everything up
/// to the LAST colon — <c>{HP: 50:look}</c> shows "HP: 50" and runs "look" — and a
/// command can't contain a colon. Scripts are written against exactly this, so it
/// is kept, quirks included.</para>
///
/// <para><b>No escaping</b>, as in Genie 4: a literal <c>{a:b}</c> in text becomes a
/// link. The shape doesn't occur in DragonRealms' own output (none in 51 recorded
/// sessions), so that is a script author's choice, not a stream hazard.</para>
///
/// <para>Collapsing <c>{a:b}</c> to <c>a</c> shortens the line, so every span that
/// already rides on it (parser links, bold, presets) is re-mapped onto the new
/// text. An existing link that overlaps a markup link is dropped — the markup
/// link owns those characters.</para>
/// </summary>
public static class InlineClickMarkup
{
    private static readonly Regex ClickRegex = new(@"{([^{]*):([^{]*)}", RegexOptions.CultureInvariant);

    /// <summary>A line after the markup pass.</summary>
    public readonly record struct Result(
        string Text,
        IReadOnlyList<LinkSpan>? Links,
        IReadOnlyList<BoldSpan>? Bolds,
        IReadOnlyList<PresetSpan>? Presets);

    /// <summary>Cheap pre-check — the overwhelmingly common line has no brace.</summary>
    public static bool MightContain(string? text) =>
        text is not null && text.Length >= 3 && text.IndexOf('{') >= 0 && text.IndexOf(':') >= 0;

    /// <summary>Collapse every <c>{display:command}</c> in <paramref name="text"/>
    /// to its display text and add a link span for it; re-map the existing spans.
    /// A line without markup comes back as the same instances.</summary>
    public static Result Apply(string text,
                               IReadOnlyList<LinkSpan>? links = null,
                               IReadOnlyList<BoldSpan>? bolds = null,
                               IReadOnlyList<PresetSpan>? presets = null)
    {
        var unchanged = new Result(text, links, bolds, presets);
        if (!MightContain(text)) return unchanged;

        var matches = ClickRegex.Matches(text);
        if (matches.Count == 0) return unchanged;

        // Each collapse: original [Index, Index+Length) → display text at newStart.
        var edits = new List<(int OrigStart, int OrigEnd, int DisplayOrigStart, int DisplayLength, int NewStart, string Command)>();
        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        foreach (Match m in matches)
        {
            var display = m.Groups[1];
            var command = m.Groups[2].Value;
            if (display.Length == 0 || command.Trim().Length == 0) continue;   // nothing to click
            sb.Append(text, cursor, m.Index - cursor);
            int newStart = sb.Length;
            sb.Append(display.Value);
            edits.Add((m.Index, m.Index + m.Length, display.Index, display.Length, newStart, command));
            cursor = m.Index + m.Length;
        }
        if (edits.Count == 0) return unchanged;
        sb.Append(text, cursor, text.Length - cursor);

        // Map an original offset onto the collapsed text. Inside a collapsed
        // match, a position in the display maps with it; one in the braces or the
        // command clamps to the display's nearer edge.
        int Map(int pos)
        {
            int removedBefore = 0;
            foreach (var e in edits)
            {
                if (pos < e.OrigStart) break;
                if (pos < e.OrigEnd)
                {
                    int rel = pos - e.DisplayOrigStart;
                    return e.NewStart + Math.Clamp(rel, 0, e.DisplayLength);
                }
                removedBefore += (e.OrigEnd - e.OrigStart) - e.DisplayLength;
            }
            return pos - removedBefore;
        }

        (int Start, int Length) MapSpan(int start, int length)
        {
            int s = Map(start), end = Map(start + length);
            return (s, Math.Max(0, end - s));
        }

        bool OverlapsMarkup(int start, int length) =>
            edits.Any(e => start < e.OrigEnd && start + length > e.OrigStart);

        var newLinks = new List<LinkSpan>();
        if (links is not null)
            foreach (var l in links)
            {
                if (OverlapsMarkup(l.Start, l.Length)) continue;   // the markup link owns these characters
                var (s, n) = MapSpan(l.Start, l.Length);
                if (n > 0) newLinks.Add(l with { Start = s, Length = n });
            }
        foreach (var e in edits)
            newLinks.Add(new LinkSpan(e.NewStart, e.DisplayLength, e.Command));
        newLinks.Sort((a, b) => a.Start.CompareTo(b.Start));

        List<BoldSpan>? newBolds = bolds?.Select(b => { var (s, n) = MapSpan(b.Start, b.Length); return b with { Start = s, Length = n }; })
                                         .Where(b => b.Length > 0).ToList();
        List<PresetSpan>? newPresets = presets?.Select(p => { var (s, n) = MapSpan(p.Start, p.Length); return p with { Start = s, Length = n }; })
                                               .Where(p => p.Length > 0).ToList();

        return new Result(sb.ToString(), newLinks, newBolds, newPresets);
    }
}
