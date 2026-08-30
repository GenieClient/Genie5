using Genie.Core.Capture;
using Genie.Core.Diagnostics;
using Genie.Core.Events;

namespace Genie.Core.Dialogs;

/// <summary>
/// Drafts a redacted, human-reviewable GitHub issue for a server dialog the
/// renderer doesn't map yet (#156 Phase 0c) — <c>#dialogs report &lt;id&gt;</c>.
/// Same trust model as <see cref="XmlGapReport"/>: a new-issue PREFILL URL
/// only — no token, no API write, no auto-post; the user reads the body in
/// the browser and submits it themselves. Dialog bodies can carry the
/// player's OWN data (a bank dialog holds balances), so the sample is
/// redacted AND the body tells the user to double-check before submitting.
/// </summary>
public static class DialogGapReport
{
    private const int MaxSampleChars = 600;
    private const string DefaultRepo = "https://github.com/GenieClient/Genie5";

    public static XmlGapReport.Draft Build(
        DialogSessionTracker.Row row,
        XmlGapReport.ReportContext ctx,
        string repoUrl = DefaultRepo,
        CaptureRedactor? redactor = null)
    {
        redactor ??= new CaptureRedactor();
        var sample = redactor.RedactRawXml(row.LastRaw ?? string.Empty).Trim();
        if (sample.Length > MaxSampleChars) sample = sample[..MaxSampleChars] + "\n… (truncated)";

        var census = row.ControlCounts.Count == 0
            ? "(no controls captured yet)"
            : string.Join(", ", row.ControlCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} ×{kv.Value}"));

        var title  = $"[Dialog coverage] Server dialog '{row.Id}'";
        const string labels = "xml-coverage";

        var body =
$@"### Server-driven dialog without a renderer mapping

DR sent a `<dialogData id='{row.Id}'>` dialog{(string.IsNullOrEmpty(row.Title) ? "" : $" (window title: **{row.Title}**)")} that Genie's server-dialog renderer doesn't cover yet.

**Controls seen this session:** {census} ({row.Blocks} update block{(row.Blocks == 1 ? "" : "s")})

**Sample** (redacted automatically — this may still include YOUR OWN in-game data such as amounts or names; please double-check before submitting):

```xml
{sample}
```

The full raw block is in `Logs/dialog_journal.xml` and can be attached after review.

**Environment**
- Genie 5: {ctx.AppVersion}
- OS: {ctx.Os}
- Parser commit: {ctx.Commit}

<sub>Drafted by <code>#dialogs report</code>. Nothing is posted automatically — you are reviewing and submitting this issue yourself.</sub>";

        var url = $"{repoUrl}/issues/new?labels={Uri.EscapeDataString(labels)}" +
                  $"&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        return new XmlGapReport.Draft(title, body, labels, url);
    }
}
