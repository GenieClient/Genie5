using System;
using System.Linq;
using Genie.Core.Capture;
using Genie.Core.Parser;

namespace Genie.Core.Diagnostics;

/// <summary>
/// Drafts a redacted, human-reviewable GitHub issue for an XML element the
/// parser doesn't consume — the "Report XML Gap" flow that pairs with
/// <c>#audit xmlhunting</c>.
///
/// <para><b>Human-in-the-loop by design.</b> This produces a GitHub new-issue
/// <i>prefill URL</i> — no token, no API write, no auto-post. The app opens the
/// URL in the user's browser; the user reads the (already redacted) body and
/// clicks Submit themselves. That keeps three things true:</para>
/// <list type="bullet">
///   <item><b>Privacy (policy gate G2):</b> other players' speech is stripped by
///   <see cref="CaptureRedactor"/> before the sample ever reaches the draft.</item>
///   <item><b>No spam:</b> one issue per <i>tag type</i>, and a person — not a
///   bot — decides to file it.</item>
///   <item><b>No shipped credentials:</b> the prefill URL needs no auth.</item>
/// </list>
/// </summary>
public static class XmlGapReport
{
    /// <summary>Environment/context lines that go in the issue body.</summary>
    public sealed record ReportContext(string AppVersion, string Os, string Commit, string? Trigger);

    /// <summary>A ready-to-review issue: the <see cref="Url"/> opens GitHub's
    /// new-issue form prefilled with <see cref="Title"/>/<see cref="Body"/>/
    /// <see cref="Labels"/>.</summary>
    public sealed record Draft(string Title, string Body, string Labels, string Url);

    // GitHub prefill URLs are length-bounded; keep the redacted sample small so
    // the encoded URL stays well under the limit.
    private const int MaxSampleChars = 600;
    private const string DefaultRepo = "https://github.com/GenieClient/Genie5";

    /// <summary>
    /// Build a review-ready issue draft for an unconsumed element. <paramref
    /// name="rawSample"/> is a short raw-XML block around the element; it is
    /// redacted before inclusion.
    /// </summary>
    public static Draft Build(
        string tag,
        DrXmlParser.TagFate fate,
        string rawSample,
        ReportContext ctx,
        string repoUrl = DefaultRepo,
        CaptureRedactor? redactor = null)
    {
        redactor ??= new CaptureRedactor();
        var sample = Truncate(redactor.RedactRawXml(rawSample ?? string.Empty).Trim(), MaxSampleChars);

        var title  = $"[XML coverage] Unhandled <{tag}> element";
        const string labels = "xml-coverage";

        var body =
$@"### Unhandled XML element

Genie's parser received a `<{tag}>` element it does not currently consume — classified **{FateLabel(fate)}**.

**Sample** (other players' speech is removed automatically — please double-check before submitting):

```xml
{sample}
```

**Environment**
- Genie 5: {ctx.AppVersion}
- OS: {ctx.Os}
- Parser commit: {ctx.Commit}
- Triggered by: {ctx.Trigger ?? "(not specified)"}

<sub>Auto-drafted by Genie's <code>#audit xmlhunting</code> coverage reporter. Nothing is posted automatically — you are reviewing and submitting this issue yourself.</sub>";

        var url = $"{repoUrl}/issues/new?labels={Enc(labels)}&title={Enc(title)}&body={Enc(body)}";
        return new Draft(title, body, labels, url);
    }

    private static string FateLabel(DrXmlParser.TagFate f) => f switch
    {
        DrXmlParser.TagFate.DroppedData => "game data we currently discard (DROP-DATA)",
        DrXmlParser.TagFate.Unknown     => "unknown / unhandled",
        DrXmlParser.TagFate.Consumed    => "already consumed (no gap — informational)",
        _                               => "settings noise (drop-set)",
    };

    /// <summary>
    /// Write a draft to <paramref name="dir"/> as markdown and return the full
    /// path, or null if it cannot be written. The fallback when the browser
    /// handoff fails (#307): the prefill URL is far too long to echo or retype,
    /// so the reviewable body goes to a file the user can open and paste.
    /// </summary>
    public static string? SaveDraft(Draft draft, string dir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            var slug = new string(draft.Title
                .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
                .ToArray())
                .Trim('-');
            while (slug.Contains("--", StringComparison.Ordinal))
                slug = slug.Replace("--", "-", StringComparison.Ordinal);
            if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
            if (slug.Length == 0) slug = "draft";

            var path = System.IO.Path.Combine(dir, $"issue-draft-{slug}.md");
            System.IO.File.WriteAllText(path,
                $"<!-- Labels: {draft.Labels} -->{Environment.NewLine}" +
                $"# {draft.Title}{Environment.NewLine}{Environment.NewLine}" +
                draft.Body + Environment.NewLine);
            return path;
        }
        catch
        {
            // A failed browser launch must not become a failed write on top of
            // it — the caller falls back to the short URL alone.
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "\n… (truncated)";

    private static string Enc(string s) => Uri.EscapeDataString(s);
}
