using System.Text;
using System.Text.RegularExpressions;

namespace Genie.Core.Diagnostics;

/// <summary>
/// Shared helpers that make user-authored regexes safe to run inline on the live
/// game feed — the single biggest "the client froze" risk in any trigger-driven
/// MUD client.
///
/// <list type="bullet">
///   <item><b>Match-timeout.</b> When safety is on, a rule's <see cref="Regex"/>
///   is built with <see cref="MatchTimeout"/>, so a catastrophic-backtracking
///   pattern (e.g. <c>(.*)+\s</c>) can never hang the read thread — it throws
///   <see cref="RegexMatchTimeoutException"/>, which the engines catch, count
///   (via <see cref="TimeoutSink"/>), and skip.</item>
///   <item><b>Literal pre-filter.</b> <see cref="LiteralHint"/> extracts the
///   longest literal run that is <i>guaranteed present in every match</i>; the
///   engine does a cheap <see cref="string.Contains(string,StringComparison)"/>
///   gate with it before running the regex, rejecting most non-matching lines
///   in nanoseconds.</item>
/// </list>
///
/// <para>The per-engine <c>SafetyEnabled</c> toggles decide whether rules are
/// built with the timeout + pre-filter; flipping one rebuilds that engine's
/// rules. The pre-filter is deliberately conservative: a wrong hint would
/// silently break a user's trigger, so any pattern with optionality or
/// alternation (<c>| ? * {</c>) opts out of pre-filtering entirely (the
/// timeout still applies).</para>
/// </summary>
public static class RegexSafety
{
    /// <summary>Per-rule regex match-timeout applied when safety is enabled.</summary>
    public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Sink for caught match-timeouts — GenieCore wires this to its
    /// <see cref="PipelineMetrics.RecordTimeout"/>. Null when no metrics attached.</summary>
    public static Action<PipelineStage>? TimeoutSink { get; set; }

    /// <summary>Build a <see cref="Regex"/>, baking in <see cref="MatchTimeout"/>
    /// when <paramref name="safe"/> — otherwise legacy behaviour (no timeout).</summary>
    public static Regex Build(string pattern, RegexOptions opts, bool safe)
        => safe ? new Regex(pattern, opts, MatchTimeout)
                : new Regex(pattern, opts);

    /// <summary>Report a caught regex timeout for <paramref name="stage"/>.</summary>
    public static void ReportTimeout(PipelineStage stage) => TimeoutSink?.Invoke(stage);

    /// <summary>Alternation / optionality metacharacters: a pattern with any of them
    /// has no literal run guaranteed in every match. Built once (public #285).</summary>
    private static readonly System.Buffers.SearchValues<char> OptionalityChars =
        System.Buffers.SearchValues.Create("|?*{");

    /// <summary>
    /// Longest run of literal characters in <paramref name="pattern"/> that is
    /// guaranteed to appear in <i>every</i> string the pattern matches, usable as
    /// a <see cref="string.Contains(string,StringComparison)"/> pre-filter.
    /// Returns null when no safe hint exists (≥3 chars required, and any
    /// optionality/alternation — <c>| ? * {</c> — disqualifies the whole pattern,
    /// since then no single literal run is guaranteed present).
    /// </summary>
    public static string? LiteralHint(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;

        // Optionality / alternation ⇒ no literal run is guaranteed in every
        // match. Bail conservatively rather than risk a false-negative filter.
        if (pattern.AsSpan().IndexOfAny(OptionalityChars) >= 0) return null;

        var best = string.Empty;
        var cur  = new StringBuilder();
        bool escaped = false;
        bool inClass = false;   // inside a [...] character class

        foreach (var ch in pattern)
        {
            if (escaped)                     { escaped = false; Flush(); continue; }   // \x — drop conservatively
            if (ch == '\\')                  { escaped = true;  Flush(); continue; }
            if (inClass)                     { if (ch == ']') inClass = false; continue; } // class chars are alternatives — never literal
            if (ch == '[')                   { Flush(); inClass = true;        continue; }
            if (".^$+()]".IndexOf(ch) >= 0)  { Flush();                        continue; } // group/anchor/quantified — break the run
            cur.Append(ch);
        }
        Flush();
        return best.Length >= 3 ? best : null;

        void Flush()
        {
            if (cur.Length > best.Length) best = cur.ToString();
            cur.Clear();
        }
    }
}
