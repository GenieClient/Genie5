using System;
using System.IO;
using Genie.Core.Diagnostics;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #307 — when the browser handoff for an auto-drafted issue failed, the
/// launch exception was echoed verbatim into the game window. .NET builds that
/// message as "…trying to start process '&lt;url&gt;' with working directory
/// '&lt;cwd&gt;'", so the line carried both the entire drafted issue body and
/// the user's account name — and users paste those lines into public reports,
/// which is exactly how #279 was filed with a local path in it.
/// </summary>
public class BrowserLaunchPrivacyTests
{
    /// <summary>
    /// .NET builds a launch failure as "…trying to start process '&lt;url&gt;'
    /// with working directory '&lt;cwd&gt;'", so echoing it put the user's
    /// account name — and the entire drafted issue body — into a line users
    /// then paste into public reports. That is how #279 was filed.
    /// </summary>
    [Fact]
    public void A_launch_failure_reason_carries_no_path_and_no_url()
    {
        var ex = new System.ComponentModel.Win32Exception(unchecked((int)0xFFFFFFFF),
            "An error occurred trying to start process " +
            "'https://github.com/GenieClient/Genie5/issues/new?body=SECRET' " +
            @"with working directory 'C:\Users\jason\AppData\Local\Genie5\current'.");

        var reason = BrowserLaunch.SafeReason(ex);

        Assert.DoesNotContain("jason",   reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\",    reason, StringComparison.Ordinal);
        Assert.DoesNotContain("http",    reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET",  reason, StringComparison.Ordinal);
    }

    /// <summary>A prefill URL carries the whole encoded issue body, so it is
    /// neither printable nor retypable — the echo keeps the stem only.</summary>
    [Fact]
    public void A_long_prefill_url_is_shortened_to_its_stem()
    {
        var url = "https://github.com/GenieClient/Genie5/issues/new?title=x&body="
                + new string('A', 4000);

        var shown = BrowserLaunch.ShortUrl(url);

        Assert.True(shown.Length < 300);
        Assert.StartsWith("https://github.com/GenieClient/Genie5/issues/new", shown);
        Assert.DoesNotContain("AAAA", shown);
    }

    /// <summary>An ordinary help link is short enough to print unchanged.</summary>
    [Fact]
    public void A_short_url_is_echoed_verbatim()
    {
        const string url = "https://elanthipedia.play.net/Main_Page";
        Assert.Equal(url, BrowserLaunch.ShortUrl(url));
    }

    /// <summary>When the browser cannot be opened the draft still has to reach
    /// the user, so it is written out and the path is what gets echoed.</summary>
    [Fact]
    public void A_draft_is_written_to_disk_when_the_browser_cannot_be_opened()
    {
        var dir = NewTempDir();
        try
        {
            var draft = new XmlGapReport.Draft(
                "[XML coverage] Unhandled <type> element", "body text here", "xml-coverage", "https://x/");

            var path = XmlGapReport.SaveDraft(draft, dir);

            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path!);
            Assert.Contains("body text here", text);
            Assert.Contains("xml-coverage", text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewTempDir() =>
        Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "genie_draftfix_" + Guid.NewGuid().ToString("N"))).FullName;
}
