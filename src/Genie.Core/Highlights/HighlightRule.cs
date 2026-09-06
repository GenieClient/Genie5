using System.Text.RegularExpressions;
using Genie.Core.Diagnostics;

namespace Genie.Core.Highlights;

public enum HighlightMatchType { String, Line, BeginsWith, Regex }

public sealed class HighlightRule
{
    private Regex?  _regex;
    private string? _hint;        // literal pre-filter for Regex match type
    private bool    _safe = true;

    public HighlightRule(string pattern, string foregroundColor, string backgroundColor = "",
                         HighlightMatchType matchType = HighlightMatchType.String,
                         bool caseSensitive = false, bool isEnabled = true, string className = "",
                         bool safe = true, string soundFile = "", string speak = "",
                         IEnumerable<string>? windows = null)
    {
        Pattern         = pattern;
        ForegroundColor = foregroundColor;
        BackgroundColor = backgroundColor;
        MatchType       = matchType;
        CaseSensitive   = caseSensitive;
        IsEnabled       = isEnabled;
        ClassName       = className;
        SoundFile       = soundFile;
        Speak           = speak;
        Windows         = NormalizeWindows(windows);
        Rebuild(safe);
    }

    /// <summary>The windows this rule paints in, by canonical id ("main",
    /// "room", "mobs", "players", "objects", a stream id like "thoughts", a plugin window
    /// name). <b>Empty = every window</b> (the default, so existing and
    /// Genie 4-imported rules apply everywhere). Case-insensitive.</summary>
    public IReadOnlySet<string> Windows { get; private set; }

    /// <summary>Config layer this rule lives in (public #257) — which file it
    /// saves back to. Not serialized: scope IS the file it came from.</summary>
    public Persistence.RuleScope Scope { get; set; } = Persistence.RuleScope.Character;

    /// <summary>Replace the window scope (from the config panel). Empty clears
    /// it back to "every window".</summary>
    public void SetWindows(IEnumerable<string>? windows) => Windows = NormalizeWindows(windows);

    private static IReadOnlySet<string> NormalizeWindows(IEnumerable<string>? windows)
    {
        if (windows is null) return EmptyWindows;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
            if (!string.IsNullOrWhiteSpace(w)) set.Add(w.Trim());
        return set.Count == 0 ? EmptyWindows : set;
    }

    private static readonly IReadOnlySet<string> EmptyWindows =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Does this rule paint in <paramref name="window"/>? True when the
    /// rule has no window restriction (empty = all) or explicitly lists it.</summary>
    public bool AppliesToWindow(string window) =>
        Windows.Count == 0 || Windows.Contains(window);

    public string             Pattern         { get; }
    public string             ForegroundColor { get; }
    public string             BackgroundColor { get; }
    public HighlightMatchType MatchType       { get; }
    public bool               CaseSensitive   { get; }
    public bool               IsEnabled       { get; set; }
    public string             ClassName       { get; set; }
    /// <summary>Optional sound played when this highlight matches a line
    /// (resolved against SoundDir). Empty = silent.</summary>
    public string             SoundFile       { get; set; }
    /// <summary>Optional TTS when this highlight matches a line: empty = silent,
    /// <c>*</c> = speak the whole matched line, anything else = speak that text.
    /// Spoken urgent — it barges in over stream read-aloud.</summary>
    public string             Speak           { get; set; }

    public bool Matches(string line)
    {
        var cmp = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return MatchType switch
        {
            HighlightMatchType.Regex      => RegexIsMatch(line, cmp),
            HighlightMatchType.BeginsWith => line.StartsWith(Pattern, cmp),
            _                             => line.Contains(Pattern, cmp),
        };
    }

    private bool RegexIsMatch(string line, StringComparison cmp)
    {
        if (_regex is null) return false;
        if (_safe && _hint is not null && !line.Contains(_hint, cmp)) return false;
        try { return _regex.IsMatch(line); }
        catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Highlights); return false; }
    }

    /// <summary>
    /// Returns the (start, length) of every span in <paramref name="line"/>
    /// this rule highlights. Empty if no match. The renderer uses this to
    /// paint only the matched portion of a line rather than the whole line.
    /// </summary>
    public IEnumerable<(int Start, int Length)> GetMatchPositions(string line)
    {
        if (string.IsNullOrEmpty(line) || string.IsNullOrEmpty(Pattern))
            yield break;

        var cmp = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        switch (MatchType)
        {
            case HighlightMatchType.Regex:
                if (_regex is null) yield break;
                if (_safe && _hint is not null && !line.Contains(_hint, cmp)) yield break;
                // Materialise under the timeout guard so a catastrophic pattern
                // can't hang the render path; yield outside the try (C# rule).
                List<(int, int)>? spans = null;
                try
                {
                    foreach (Match m in _regex.Matches(line))
                        if (m.Success && m.Length > 0)
                            (spans ??= new()).Add((m.Index, m.Length));
                }
                catch (RegexMatchTimeoutException) { RegexSafety.ReportTimeout(PipelineStage.Highlights); yield break; }
                if (spans is not null)
                    foreach (var s in spans) yield return s;
                yield break;

            case HighlightMatchType.Line:
                // Whole-line highlight if the line contains the pattern anywhere.
                if (line.Contains(Pattern, cmp))
                    yield return (0, line.Length);
                yield break;

            case HighlightMatchType.BeginsWith:
                if (line.StartsWith(Pattern, cmp))
                    yield return (0, Pattern.Length);
                yield break;

            case HighlightMatchType.String:
            default:
                // All non-overlapping occurrences of the substring.
                int i = 0;
                while (i <= line.Length - Pattern.Length)
                {
                    int hit = line.IndexOf(Pattern, i, cmp);
                    if (hit < 0) yield break;
                    yield return (hit, Pattern.Length);
                    i = hit + Pattern.Length;
                }
                yield break;
        }
    }

    /// <summary>(Re)build the regex (Regex match type only) with or without the
    /// safety match-timeout + literal pre-filter.</summary>
    internal void Rebuild(bool safe)
    {
        _safe = safe;
        if (MatchType != HighlightMatchType.Regex) { _regex = null; _hint = null; return; }
        var opts = RegexOptions.Compiled | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try { _regex = RegexSafety.Build(Pattern, opts, safe); _hint = safe ? RegexSafety.LiteralHint(Pattern) : null; }
        catch { _regex = null; _hint = null; }
    }
}
