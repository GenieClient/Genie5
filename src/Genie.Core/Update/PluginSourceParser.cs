using System.Text.RegularExpressions;

namespace Genie.Core.Update;

/// <summary>
/// Parses user-supplied plugin source URLs (or shorthand) into a
/// <see cref="FeedEntry"/> ready to add to <c>update-feeds.json</c>.
///
/// Accepted inputs:
///   - <c>https://github.com/Owner/Repo</c>
///   - <c>https://github.com/Owner/Repo/</c>
///   - <c>https://github.com/Owner/Repo.git</c>
///   - <c>Owner/Repo</c>                      (shorthand)
///   - <c>github.com/Owner/Repo</c>          (no scheme)
///
/// The asset pattern defaults to <c>{Repo}.dll</c> if the user doesn't
/// override it — works for the common case of one-DLL plugins like
/// <c>Plugin_EXPTrackerV5</c>. The Add Source dialog (Phase 3) lets the
/// user edit the pattern before saving.
///
/// Future kinds (HTTP manifest, GitLab releases, etc.) plug in here as
/// additional <see cref="TryParse"/> branches.
/// </summary>
public static partial class PluginSourceParser
{
    // Matches the (owner, repo) pair out of any of the accepted GitHub
    // forms above. Owner and repo follow GitHub's naming rules:
    // alphanumeric + dash/underscore/dot, no leading dash.
    private static readonly Regex GithubPattern = GithubPatternRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:https?://)?(?:www\.)?github\.com/(?<owner>[A-Za-z0-9][A-Za-z0-9\-_.]*)/(?<repo>[A-Za-z0-9][A-Za-z0-9\-_.]*?)(?:\.git)?/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GithubPatternRegex();

    private static readonly Regex ShorthandPattern = ShorthandPatternRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<owner>[A-Za-z0-9][A-Za-z0-9\-_.]*)/(?<repo>[A-Za-z0-9][A-Za-z0-9\-_.]*)$", System.Text.RegularExpressions.RegexOptions.None)]
    private static partial System.Text.RegularExpressions.Regex ShorthandPatternRegex();

    /// <summary>
    /// Attempt to parse <paramref name="input"/> into a new plugin
    /// <see cref="FeedEntry"/>. On success, <paramref name="entry"/> has
    /// kind <c>github-releases</c>, a default asset pattern of
    /// <c>{repo}.dll</c>, and <see cref="FeedEntry.Enabled"/> true.
    /// </summary>
    /// <returns>True if parsed; false if the input matches no known shape.</returns>
    public static bool TryParse(string input, out FeedEntry entry, out string? error)
    {
        entry = new FeedEntry();
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Source URL is empty.";
            return false;
        }

        var raw = input.Trim();

        var m = GithubPattern.Match(raw);
        if (!m.Success) m = ShorthandPattern.Match(raw);
        if (!m.Success)
        {
            error = "Not a recognised plugin source. Paste a GitHub repo URL (https://github.com/Owner/Repo) or shorthand (Owner/Repo).";
            return false;
        }

        var owner = m.Groups["owner"].Value;
        var repo  = m.Groups["repo"].Value;

        entry = new FeedEntry
        {
            Id           = MakeId(owner, repo),
            Name         = repo,
            Kind         = "github-releases",
            Owner        = owner,
            Repo         = repo,
            AssetPattern = $"{repo}.dll",
            Enabled      = true,
        };
        return true;
    }

    /// <summary>
    /// Build a stable id from owner+repo. Lowercased, dashes replace dots so
    /// the id is also a valid filename / config-key (we don't currently use
    /// it as either, but neighbours might).
    /// </summary>
    private static string MakeId(string owner, string repo) =>
        $"{owner}/{repo}".ToLowerInvariant().Replace('.', '-');
}
