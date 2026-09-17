namespace Genie.Core.Dialogs;

/// <summary>
/// Normalises a server-supplied dialog window title (public #344).
/// </summary>
/// <remarks>
/// DragonRealms sends dialog titles in the Win32 convention Wrayth was built
/// against, where a single <c>&amp;</c> marks the next character as a keyboard
/// mnemonic and <c>&amp;&amp;</c> is the escape for one literal ampersand:
/// <code>&lt;openDialog id="befriend" title="Friends &amp;amp;&amp;amp; Enemies" …&gt;</code>
/// decodes to <c>Friends &amp;&amp; Enemies</c> on the wire. Avalonia gives no
/// meaning to <c>&amp;</c> in a window title, so passing that through renders the
/// doubling verbatim — and it is persisted, because the title is also stored as
/// the remembered name in <c>dialogmappings.json</c>.
/// <para>
/// Only the DOUBLED form is collapsed. A lone <c>&amp;</c> is left exactly as it
/// arrived: under the Win32 reading it would be a mnemonic marker, but we have
/// never seen DR send one, and treating it as such would silently eat a real
/// ampersand from any title that simply wasn't escaped.
/// </para>
/// <para>
/// Titles only. Control captions arrive in <c>value</c>/<c>text</c> and have not
/// been seen doubled in any recording or journal entry; leaving them alone keeps
/// this to the one case that is evidenced.
/// </para>
/// </remarks>
public static class DialogTitle
{
    /// <summary>Collapse Win32 mnemonic-escaped <c>&amp;&amp;</c> to a single
    /// <c>&amp;</c>. Null and empty pass through unchanged.</summary>
    public static string? Normalize(string? title) =>
        string.IsNullOrEmpty(title) || !title.Contains("&&", System.StringComparison.Ordinal)
            ? title
            : title.Replace("&&", "&", System.StringComparison.Ordinal);
}
