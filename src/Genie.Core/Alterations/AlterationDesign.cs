namespace Genie.Core.Alterations;

/// <summary>
/// One saved alteration design — the four pieces of text a player hands a GM or
/// an alteration merchant.
///
/// The four fields are a deliberate one-for-one match with Alteration Buddy
/// (Djordje, GPL-3.0, github.com/mj-colonel-panic/AlterationBuddy), whose file
/// format was a headerless tab-separated line per design:
/// <c>ShortTap\tTap\tLook\tRead</c>. Keeping the shape identical is what makes
/// <see cref="AlterationLibrary.ImportGenie4File"/> a straight read rather than a
/// mapping exercise.
///
/// <see cref="Title"/> is the one addition, and it is deliberately outside that
/// set: it is a G5-only label for the saved-designs menu, defaulted from
/// <see cref="Tap"/> so an imported library still reads sensibly, and dropped on
/// export back to the Genie 4 format.
/// </summary>
public sealed class AlterationDesign
{
    /// <summary>Display label for the saved-designs list. Not part of the Genie 4
    /// format; falls back to <see cref="Tap"/> (then Short Tap, then "Untitled")
    /// when blank, which is what Alteration Buddy showed in its list box.</summary>
    public string Title { get; set; } = "";

    /// <summary>The item's name as it appears in inventory — "a razor-edged
    /// scimitar". DR budgets this as article / adjective / noun, 15 characters
    /// each (see <see cref="AlterationLimits.ShortTapSegment"/>).</summary>
    public string ShortTap { get; set; } = "";

    /// <summary>The one-line description shown when the item is tapped.</summary>
    public string Tap { get; set; } = "";

    /// <summary>The long description shown on LOOK.</summary>
    public string Look { get; set; } = "";

    /// <summary>The inscription shown on READ. Budgeted in both words and
    /// characters.</summary>
    public string Read { get; set; } = "";

    /// <summary>Free-form player notes (which merchant, cost, festival, …).
    /// G5-only, like <see cref="Title"/>, and dropped on Genie 4 export.</summary>
    public string Notes { get; set; } = "";

    /// <summary>
    /// True once the alteration has actually been done, so finished work stops
    /// crowding the drafts you are still working on (requested by Bardolf).
    /// Completed designs are kept, not deleted — they are the record you reach
    /// for when an item needs replacing — they just sort and filter apart.
    ///
    /// G5-only and dropped on Genie 4 export. Absent from older
    /// <c>alterations.json</c> files, where it deserialises to false: everything
    /// that predates this is a draft, which is the right default.
    /// </summary>
    public bool IsCompleted { get; set; }

    /// <summary>Label for the saved-designs menu and list. Never empty.</summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title))    return Title.Trim();
            if (!string.IsNullOrWhiteSpace(Tap))      return Tap.Trim();
            if (!string.IsNullOrWhiteSpace(ShortTap)) return ShortTap.Trim();
            return "Untitled design";
        }
    }

    /// <summary>True when every one of the four text fields is blank — the state
    /// of a freshly-opened designer, which must not be saved as a design.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ShortTap) &&
        string.IsNullOrWhiteSpace(Tap)      &&
        string.IsNullOrWhiteSpace(Look)     &&
        string.IsNullOrWhiteSpace(Read);

    public AlterationDesign Clone() => new()
    {
        Title       = Title,
        ShortTap    = ShortTap,
        Tap         = Tap,
        Look        = Look,
        Read        = Read,
        Notes       = Notes,
        IsCompleted = IsCompleted
    };

    /// <summary>
    /// Serialise to Alteration Buddy's tab-separated line so a G5 library can be
    /// handed back to a Genie 4 user. <see cref="Title"/> and <see cref="Notes"/>
    /// are not representable and are dropped.
    ///
    /// Embedded tabs and newlines are flattened to spaces: the Genie 4 format has
    /// no escaping at all, so a raw tab or a multi-line Look would silently
    /// corrupt every following field (Alteration Buddy's own writer had exactly
    /// this bug — see <see cref="AlterationLibrary.ImportGenie4File"/>). Flattening
    /// loses formatting; not flattening loses the file.
    /// </summary>
    public string ToGenie4Line() =>
        string.Join('\t', Flatten(ShortTap), Flatten(Tap), Flatten(Look), Flatten(Read));

    private static string Flatten(string s) =>
        s.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// Parse one Alteration Buddy line. Short rows are tolerated (missing fields
    /// come back empty) rather than throwing, because the upstream writer could
    /// produce them; extra tabs beyond the fourth field are folded back into the
    /// Read field so no text is dropped.
    /// </summary>
    public static AlterationDesign FromGenie4Line(string line)
    {
        var parts = (line ?? "").Split('\t');
        string At(int i) => i < parts.Length ? parts[i] : "";

        return new AlterationDesign
        {
            ShortTap = At(0),
            Tap      = At(1),
            Look     = At(2),
            Read     = parts.Length > 4 ? string.Join('\t', parts.Skip(3)) : At(3)
        };
    }
}
