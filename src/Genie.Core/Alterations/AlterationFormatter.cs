using System.Text;

namespace Genie.Core.Alterations;

/// <summary>Layout for the composed alteration request.</summary>
public enum AlterationResultFormat
{
    /// <summary>Alteration Buddy's exact output: one line, fields joined by
    /// <c> \ </c>, read inscription wrapped in double quotes. This is the form
    /// players paste to a merchant or GM, so it is the default and must not
    /// drift.</summary>
    Genie4,

    /// <summary>One labelled field per line. Easier to proofread in the designer;
    /// not what you hand to a GM.</summary>
    MultiLine
}

/// <summary>
/// Composes the four design fields into the request string.
/// Port of Alteration Buddy's <c>UpdateResult()</c> (Djordje, GPL-3.0).
/// </summary>
public static class AlterationFormatter
{
    private const string Separator = " \\ ";

    public static string Format(AlterationDesign design, AlterationResultFormat format = AlterationResultFormat.Genie4) =>
        Format(design.ShortTap, design.Tap, design.Look, design.Read, format);

    public static string Format(
        string? shortTap, string? tap, string? look, string? read,
        AlterationResultFormat format = AlterationResultFormat.Genie4)
    {
        // Fields are emitted in fixed order and blank ones are skipped entirely —
        // including their separator, which is why the separator is appended only
        // when something has already been written.
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(shortTap)) parts.Add("Short Tap: " + shortTap);
        if (!string.IsNullOrEmpty(tap))      parts.Add("Tap: "       + tap);
        if (!string.IsNullOrEmpty(look))     parts.Add("Look: "      + look);
        if (!string.IsNullOrEmpty(read))     parts.Add("Read: \""    + read + "\"");

        if (parts.Count == 0) return "";

        if (format == AlterationResultFormat.MultiLine)
        {
            var sb = new StringBuilder();
            foreach (var p in parts) sb.AppendLine(p);
            return sb.ToString().TrimEnd();
        }

        return string.Join(Separator, parts);
    }
}
