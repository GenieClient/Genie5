using System.Text.RegularExpressions;
using Genie.Core.Events;

namespace Genie.Core.Dialogs;

/// <summary>What activating a dialog control should do.</summary>
public enum ServerDialogActionKind
{
    /// <summary>Nothing — the control carries no command.</summary>
    None,
    /// <summary>Send through the normal user-command path.</summary>
    GameCommand,
    /// <summary>Open in a browser (the <c>url:</c> scheme).</summary>
    WebLink,
}

/// <summary>
/// A resolved control activation. <see cref="UnresolvedTokens"/> lists
/// <c>%placeholder%</c> names that matched no sibling control — a renderer
/// should refuse to send while any remain (the Genie 4 plugin silently
/// declined a Pay button whose dropdown had no selection; naming them is
/// better than a button that does nothing).
/// </summary>
public sealed record ServerDialogAction(
    ServerDialogActionKind Kind,
    string Value,
    IReadOnlyList<string> UnresolvedTokens)
{
    public static readonly ServerDialogAction None =
        new(ServerDialogActionKind.None, "", Array.Empty<string>());

    public bool CanSend => Kind != ServerDialogActionKind.None && UnresolvedTokens.Count == 0;
}

/// <summary>
/// Turns a server dialog control's <c>cmd</c> into something the client can
/// act on (#156 Phase 1).
///
/// <para>A <c>cmd</c> may contain <c>%placeholder%</c> tokens naming SIBLING
/// controls, filled at click time from what the user has actually entered —
/// <c>bank debt %province1% %bank_amount%</c> on the live <c>bank_debt</c>
/// fixture. Resolution follows the Genie 4 plugin: a checkBox contributes its
/// <c>checked_value</c>/<c>unchecked_value</c>, a dropDownBox its selected
/// item's DATA value from the <c>content_value</c> list, a radio group the
/// checked member's <c>cmd</c>, and anything else its text.</para>
///
/// <para>The plugin's hardcoded per-dialog special cases (<c>province1</c>,
/// <c>bank1</c>, <c>bank2</c>, and a <c>category</c> branch that INJECTS
/// separators to fan one click into several commands) are not ported — the
/// general rule covers them, and dialogs that genuinely need more belong in
/// the Phase 2 bespoke registry.</para>
///
/// <para><b>The separator is escaped across the whole finished command</b>, so
/// a server-authored string cannot fan out into several client commands —
/// neither through a value the user typed nor through the template itself.
/// The escape is <c>\</c> before the configured separator, which is what
/// <c>ArgumentParser.SafeSplit</c> already understands (#132). Note the
/// separator is configurable, so this must never hardcode <c>;</c>.</para>
/// </summary>
public static class ServerDialogCommand
{
    /// <summary>Base for the server's root-relative <c>url:</c> paths
    /// (<c>url:/dr/info/</c>, <c>url:/bounce/redirect.asp?URL=…</c>).</summary>
    public const string PlayNetBaseUrl = "https://www.play.net";

    private const string UrlScheme = "url:";

    // A token is %name% with no whitespace inside, so ordinary prose percentages
    // ("50% of 100%") cannot be mistaken for one.
    private static readonly Regex TokenRe =
        new(@"%([A-Za-z0-9_][A-Za-z0-9_.\-]*)%", RegexOptions.Compiled);

    /// <summary>
    /// Resolve <paramref name="cmd"/> against its sibling controls.
    /// <paramref name="liveValues"/> holds what the user has actually entered or
    /// selected, keyed by control id, and takes precedence over the value the
    /// server last sent.
    /// </summary>
    public static ServerDialogAction Resolve(
        string? cmd,
        IReadOnlyList<DialogControl>? siblings = null,
        IReadOnlyDictionary<string, string>? liveValues = null,
        char separator = ';')
    {
        if (string.IsNullOrWhiteSpace(cmd)) return ServerDialogAction.None;

        var isWebLink = cmd.StartsWith(UrlScheme, StringComparison.OrdinalIgnoreCase);
        var body = isWebLink ? cmd[UrlScheme.Length..] : cmd;

        var tokens = BuildTokenMap(siblings, liveValues);
        var unresolved = new List<string>();

        var substituted = TokenRe.Replace(body, match =>
        {
            var name = match.Groups[1].Value;
            if (tokens.TryGetValue(name, out var value)) return value;
            unresolved.Add(name);
            return match.Value;          // leave it visible rather than silently blank
        });

        if (isWebLink)
            return new(ServerDialogActionKind.WebLink, ResolveUrl(substituted), unresolved);

        // Escaped LAST, over the finished string, so neither a typed value nor
        // the server's own template can split into multiple commands.
        return new(ServerDialogActionKind.GameCommand,
                   EscapeSeparator(substituted, separator), unresolved);
    }

    /// <summary>Prefix the server's root-relative paths with play.net; leave an
    /// already-absolute URL alone.</summary>
    public static string ResolveUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim();
        if (trimmed.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return trimmed.StartsWith('/')
            ? PlayNetBaseUrl + trimmed
            : PlayNetBaseUrl + "/" + trimmed;
    }

    /// <summary>Backslash-escape every separator so the command engine's
    /// escape-aware split (#132) treats them as literal text.</summary>
    public static string EscapeSeparator(string command, char separator) =>
        command.Replace(separator.ToString(), "\\" + separator);

    // ── Token resolution ─────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildTokenMap(
        IReadOnlyList<DialogControl>? siblings,
        IReadOnlyDictionary<string, string>? liveValues)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (siblings is null) return map;

        foreach (var c in siblings)
        {
            switch (c.Type)
            {
                case DialogControlType.Radio:
                {
                    // A radio contributes under its GROUP name, and only while
                    // checked — the group resolves to whichever member is on.
                    var group = Attr(c, "group");
                    if (string.IsNullOrEmpty(group) || !IsChecked(c, liveValues)) break;
                    map[group] = c.Cmd ?? c.Value ?? "";
                    break;
                }

                case DialogControlType.CheckBox:
                {
                    if (string.IsNullOrEmpty(c.Id)) break;
                    map[c.Id] = IsChecked(c, liveValues)
                        ? Attr(c, "checked_value")   ?? "1"
                        : Attr(c, "unchecked_value") ?? "0";
                    break;
                }

                case DialogControlType.DropDownBox:
                {
                    if (string.IsNullOrEmpty(c.Id)) break;
                    map[c.Id] = SelectedDataValue(c, liveValues);
                    break;
                }

                default:
                {
                    if (string.IsNullOrEmpty(c.Id)) break;
                    map[c.Id] = Live(c, liveValues) ?? c.Value ?? c.Text ?? "";
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// A dropDownBox pairs display text with data values —
    /// <c>content_text="Zoluren,Therengia,…"</c> against
    /// <c>content_value="1,2,3,4,5"</c> — and the command wants the DATA value.
    /// Falls back to the display text when the lists are absent or ragged.
    /// </summary>
    private static string SelectedDataValue(
        DialogControl c, IReadOnlyDictionary<string, string>? liveValues)
    {
        var selected = Live(c, liveValues) ?? c.Value ?? "";
        if (selected.Length == 0) return "";

        var texts  = SplitList(Attr(c, "content_text"));
        var values = SplitList(Attr(c, "content_value"));
        if (texts.Length == 0 || values.Length == 0) return selected;

        var at = Array.FindIndex(texts,
            t => string.Equals(t, selected, StringComparison.OrdinalIgnoreCase));

        // No match means the selection is already a data value (a renderer that
        // binds the value list directly) or the lists are ragged — either way,
        // pass it through rather than blanking the command.
        return at >= 0 && at < values.Length ? values[at] : selected;
    }

    private static string[] SplitList(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? Array.Empty<string>()
            : raw.Split(',').Select(s => s.Trim()).ToArray();

    private static bool IsChecked(
        DialogControl c, IReadOnlyDictionary<string, string>? liveValues) =>
        Truthy(Live(c, liveValues) ?? Attr(c, "checked"));

    private static string? Live(
        DialogControl c, IReadOnlyDictionary<string, string>? liveValues) =>
        !string.IsNullOrEmpty(c.Id) && liveValues is not null &&
        liveValues.TryGetValue(c.Id, out var v) ? v : null;

    private static string? Attr(DialogControl c, string name) =>
        c.Attributes.TryGetValue(name, out var v) ? v : null;

    /// <summary>DR spells truth several ways across its dialogs.</summary>
    private static bool Truthy(string? s) =>
        !string.IsNullOrEmpty(s) &&
        (s.Equals("t",    StringComparison.OrdinalIgnoreCase) ||
         s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         s.Equals("y",    StringComparison.OrdinalIgnoreCase) ||
         s.Equals("yes",  StringComparison.OrdinalIgnoreCase) ||
         s == "1");
}
