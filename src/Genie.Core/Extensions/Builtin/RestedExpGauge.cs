using System.Globalization;
using System.Text.RegularExpressions;

namespace Genie.Core.Extensions.Builtin;

/// <summary>Traffic-light state of the rested-EXP gauge.</summary>
public enum RestedExpLevel { Red, Yellow, Green }

/// <summary>One reading of the <c>exp rexp</c> line: banked time, time still
/// spendable this cycle, and time until the cycle refreshes.</summary>
public sealed record RestedExpReading(TimeSpan Stored, TimeSpan Usable, TimeSpan Refresh)
{
    /// <summary>What actually limits the 3x bonus right now — the smaller of the
    /// bank and this cycle's allowance.</summary>
    public TimeSpan Spendable => Stored < Usable ? Stored : Usable;
}

/// <summary>
/// Parses the rested-EXP line DR pushes in <c>&lt;component id='exp rexp'&gt;</c>
/// and turns it into a gauge fill and traffic-light level.
///
/// <para>The gauge shows <see cref="RestedExpReading.Spendable"/> against the
/// cap, not the bank alone: a full bank is useless once the cycle's allowance is
/// spent, so the bar follows whichever of the two runs out first.</para>
/// </summary>
public static class RestedExpGauge
{
    /// <summary>Bank cap used for the fill fraction: Premium on Prime. The
    /// account tier is not detected yet — see the release notes.</summary>
    public const double DefaultCapHours = 6.0;

    /// <summary>Spendable time below which the gauge turns yellow.</summary>
    public static readonly TimeSpan YellowBelow = TimeSpan.FromHours(1);

    private static readonly Regex TagRe = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex LineRe = new(
        @"Rested EXP Stored:\s*(.+?)\s+Usable This Cycle:\s*(.+?)\s+Cycle Refreshes:\s*(.+)$",
        RegexOptions.Compiled);
    private static readonly Regex HmRe = new(@"^(\d+):(\d{1,2})$", RegexOptions.Compiled);

    /// <summary>Parse the component body (tags tolerated).</summary>
    public static bool TryParse(string? content, out RestedExpReading reading)
    {
        reading = null!;
        if (string.IsNullOrWhiteSpace(content)) return false;
        var m = LineRe.Match(TagRe.Replace(content, "").Trim());
        if (!m.Success) return false;
        reading = new RestedExpReading(
            ParseDuration(m.Groups[1].Value),
            ParseDuration(m.Groups[2].Value),
            ParseDuration(m.Groups[3].Value));
        return true;
    }

    /// <summary>"5:03 hours" / "6 hours" / "1:39 hour" / "none" / "less than a
    /// minute" → a duration. Anything unrecognised counts as zero.</summary>
    public static TimeSpan ParseDuration(string? text)
    {
        var v = (text ?? "").Trim();
        if (v.EndsWith(" hours", StringComparison.OrdinalIgnoreCase)) v = v[..^6].TrimEnd();
        else if (v.EndsWith(" hour", StringComparison.OrdinalIgnoreCase)) v = v[..^5].TrimEnd();

        var hm = HmRe.Match(v);
        if (hm.Success)
            return new TimeSpan(int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture),
                                int.Parse(hm.Groups[2].Value, CultureInfo.InvariantCulture), 0);
        if (int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
            return TimeSpan.FromHours(hours);
        return TimeSpan.Zero;   // "none", "less than a minute", garbage
    }

    /// <summary>Red when nothing is spendable, yellow under <see cref="YellowBelow"/>,
    /// green otherwise.</summary>
    public static RestedExpLevel LevelOf(RestedExpReading r) =>
        r.Spendable <= TimeSpan.Zero ? RestedExpLevel.Red
        : r.Spendable < YellowBelow  ? RestedExpLevel.Yellow
        : RestedExpLevel.Green;

    /// <summary>Gauge fill 0..1: spendable time over the cap.</summary>
    public static double FillOf(RestedExpReading r, double capHours = DefaultCapHours)
    {
        if (capHours <= 0) return 0;
        var f = r.Spendable.TotalHours / capHours;
        return f < 0 ? 0 : f > 1 ? 1 : f;
    }

    /// <summary>"1:46" style, "0:00" when empty.</summary>
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return $"{(int)t.TotalHours}:{t.Minutes:00}";
    }

    /// <summary>Plain-language tooltip explaining the three numbers.</summary>
    public static string Describe(RestedExpReading r, RestedExpLevel level)
    {
        var numbers = $"{Format(r.Stored)} banked, {Format(r.Usable)} spendable this cycle, " +
                      $"cycle refreshes in {Format(r.Refresh)}.";
        var verdict = level switch
        {
            RestedExpLevel.Green  => "Bonus learning is on: training counts triple.",
            RestedExpLevel.Yellow => "Running low: the 3x bonus ends soon.",
            _                     => r.Stored <= TimeSpan.Zero
                                       ? "Bank is empty: training is at normal speed. It refills while you're logged off or in deep sleep."
                                       : "This cycle's allowance is spent: training is at normal speed until the cycle refreshes.",
        };
        return verdict + "\n" + numbers + "\nThe bar shows whichever of banked and spendable runs out first.";
    }
}
