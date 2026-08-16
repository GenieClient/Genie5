namespace Genie.Core.Alterations;

/// <summary>
/// DragonRealms' alteration length budgets, as encoded by Alteration Buddy
/// (Djordje, GPL-3.0) and documented on Elanthipedia's Alteration page.
/// </summary>
public static class AlterationLimits
{
    /// <summary>Per-segment cap on the short tap: article, adjective and noun get
    /// 15 characters each. Alteration Buddy showed this as the static hint
    /// "15/15/15 article,adjective,noun" but never checked it; G5 measures it.</summary>
    public const int ShortTapSegment = 15;

    /// <summary>How many segments the short tap budget covers (article, adjective,
    /// noun).</summary>
    public const int ShortTapSegments = 3;

    public const int Tap            = 80;
    public const int Look           = 500;
    public const int ReadWords      = 10;
    public const int ReadCharacters = 50;
}

/// <summary>How much of one budget a field has consumed. <see cref="Remaining"/>
/// may go negative, which is what <see cref="IsOver"/> reports.</summary>
/// <param name="Used">Units consumed.</param>
/// <param name="Limit">Units allowed.</param>
/// <param name="Unit">Singular unit noun, e.g. "character" or "word".</param>
public readonly record struct AlterationBudget(int Used, int Limit, string Unit)
{
    public int  Remaining => Limit - Used;
    public bool IsOver    => Used > Limit;

    /// <summary>"80 characters remaining." / "3 characters over." — the counter
    /// text under each field.</summary>
    public string Describe() => IsOver
        ? $"{-Remaining} {Plural(-Remaining)} over."
        : $"{Remaining} {Plural(Remaining)} remaining.";

    private string Plural(int n) => n == 1 ? Unit : Unit + "s";
}

/// <summary>
/// Measures a <see cref="AlterationDesign"/> against <see cref="AlterationLimits"/>.
/// Pure, UI-free, and the single source of truth for both the designer's live
/// counters and any future <c>#alteration</c> script command.
/// </summary>
public static class AlterationValidator
{
    public static AlterationBudget TapBudget(string? tap) =>
        new(Len(tap), AlterationLimits.Tap, "character");

    public static AlterationBudget LookBudget(string? look) =>
        new(Len(look), AlterationLimits.Look, "character");

    public static AlterationBudget ReadCharacterBudget(string? read) =>
        new(Len(read), AlterationLimits.ReadCharacters, "character");

    /// <summary>
    /// Word count for the read inscription.
    ///
    /// Alteration Buddy computed this as <c>MAX - text.Split(' ').Length</c>, which
    /// is off by one on empty input (<c>"".Split(' ')</c> yields one empty element,
    /// so a blank field already reported a word spent) and double-counted every
    /// run of consecutive spaces. Splitting on whitespace with empties removed
    /// fixes both.
    /// </summary>
    public static AlterationBudget ReadWordBudget(string? read) =>
        new(WordCount(read), AlterationLimits.ReadWords, "word");

    public static int WordCount(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Per-segment budgets for the short tap — one per whitespace-separated word,
    /// each capped at <see cref="AlterationLimits.ShortTapSegment"/>. A short tap
    /// with more than <see cref="AlterationLimits.ShortTapSegments"/> segments is
    /// still measured (every segment gets a budget) so the designer can show which
    /// specific word is too long; whether the extra segments are legal is the GM's
    /// call, not ours, so this reports rather than rejects.
    /// </summary>
    public static IReadOnlyList<AlterationBudget> ShortTapSegmentBudgets(string? shortTap)
    {
        if (string.IsNullOrWhiteSpace(shortTap)) return Array.Empty<AlterationBudget>();

        return shortTap
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(seg => new AlterationBudget(seg.Length, AlterationLimits.ShortTapSegment, "character"))
            .ToList();
    }

    /// <summary>
    /// Counter text for the read field, which is budgeted twice. Alteration
    /// Buddy showed both on one line ("N words and M characters remaining.");
    /// we keep that but let each half report its own overage independently, so
    /// a 3-word / 60-character inscription says so rather than reporting only
    /// whichever check happened to be phrased first.
    /// </summary>
    public static string DescribeRead(string? read)
    {
        var words = ReadWordBudget(read);
        var chars = ReadCharacterBudget(read);
        return $"{words.Describe().TrimEnd('.')}, {Decapitalise(chars.Describe())}";
    }

    private static string Decapitalise(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>"a razor-edged scimitar" → 15/15/15 hint text with the longest
    /// segment flagged. Returns an empty string for a blank short tap.</summary>
    public static string DescribeShortTap(string? shortTap)
    {
        var budgets = ShortTapSegmentBudgets(shortTap);
        if (budgets.Count == 0) return "";

        var lengths = string.Join('/', budgets.Select(b => b.Used));
        var over    = budgets.Count(b => b.IsOver);

        if (over == 0)
            return $"{lengths} — limit {AlterationLimits.ShortTapSegment} per word.";

        return over == 1
            ? $"{lengths} — one word is over the {AlterationLimits.ShortTapSegment}-character limit."
            : $"{lengths} — {over} words are over the {AlterationLimits.ShortTapSegment}-character limit.";
    }

    /// <summary>True when nothing in the design exceeds its budget.</summary>
    public static bool IsWithinLimits(AlterationDesign d) =>
        !TapBudget(d.Tap).IsOver           &&
        !LookBudget(d.Look).IsOver         &&
        !ReadCharacterBudget(d.Read).IsOver &&
        !ReadWordBudget(d.Read).IsOver     &&
        !ShortTapSegmentBudgets(d.ShortTap).Any(b => b.IsOver);

    /// <summary>Human-readable reasons the design is over budget — one line per
    /// offending field, empty when <see cref="IsWithinLimits"/> is true.</summary>
    public static IReadOnlyList<string> Problems(AlterationDesign d)
    {
        var problems = new List<string>();

        var segments = ShortTapSegmentBudgets(d.ShortTap);
        if (segments.Any(b => b.IsOver))
            problems.Add($"Short Tap: {segments.Count(b => b.IsOver)} word(s) over {AlterationLimits.ShortTapSegment} characters.");

        var tap = TapBudget(d.Tap);
        if (tap.IsOver) problems.Add($"Tap: {tap.Describe()}");

        var look = LookBudget(d.Look);
        if (look.IsOver) problems.Add($"Look: {look.Describe()}");

        var readChars = ReadCharacterBudget(d.Read);
        if (readChars.IsOver) problems.Add($"Read: {readChars.Describe()}");

        var readWords = ReadWordBudget(d.Read);
        if (readWords.IsOver) problems.Add($"Read: {readWords.Describe()}");

        return problems;
    }

    private static int Len(string? s) => s?.Length ?? 0;
}
