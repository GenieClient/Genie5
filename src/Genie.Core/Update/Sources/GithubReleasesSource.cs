using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genie.Core.Update.Sources;

/// <summary>
/// Release source backed by the GitHub Releases REST API
/// (<c>https://api.github.com/repos/{owner}/{repo}/releases/…</c>).
///
/// Channel mapping:
///   - <c>"stable"</c> → hits <c>/releases/latest</c>, which excludes
///     drafts and prereleases. Returns null if no stable release exists.
///   - <c>"beta"</c>   → lists recent releases and returns the most recently
///     published one, prerelease or stable, so a beta-channel user is never
///     held on an old prerelease that a stable release has superseded.
/// </summary>
public sealed class GithubReleasesSource : IReleaseSource
{
    private const string DefaultUserAgent = "Genie5-Updater";

    private readonly HttpClient _http;
    private readonly string     _owner;
    private readonly string     _repo;

    public string Description { get; }

    public GithubReleasesSource(string owner, string repo, HttpClient? http = null)
    {
        _owner = owner;
        _repo  = repo;
        Description = $"{_owner}/{_repo}";

        if (http != null)
        {
            _http = http;
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(
        string channel = "stable",
        CancellationToken ct = default)
    {
        if (string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase))
        {
            return await GetLatestBetaAsync(ct);
        }
        return await GetLatestStableAsync(ct);
    }

    public async Task<byte[]> DownloadAssetAsync(ReleaseAsset asset, CancellationToken ct = default)
    {
        // GitHub release asset download URLs return 302 to S3 — HttpClient
        // follows by default. No special handling needed.
        return await _http.GetByteArrayAsync(asset.DownloadUrl, ct);
    }

    private async Task<ReleaseInfo?> GetLatestStableAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;     // repo has no releases (or all are drafts/prereleases)
            resp.EnsureSuccessStatusCode();

            var json    = await resp.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize<RawRelease>(json);
            return release is null ? null : Map(release);
        }
        catch (HttpRequestException)
        {
            // Network failure — caller treats null as "couldn't reach source".
            return null;
        }
    }

    private async Task<ReleaseInfo?> GetLatestBetaAsync(CancellationToken ct)
    {
        var url     = $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=30";
        var json    = await _http.GetStringAsync(url, ct);
        var raw     = JsonSerializer.Deserialize<List<RawRelease>>(json) ?? new();

        // Filter out drafts (they shouldn't be in the list per the API contract,
        // but be defensive) and pick the most recently published release,
        // prerelease or not. Preferring a prerelease would keep offering an old
        // beta after a newer stable release has superseded it.
        var picked = raw.Where(r => !r.Draft).OrderByDescending(r => r.PublishedAt).FirstOrDefault();
        return picked is null ? null : Map(picked);
    }

    private static ReleaseInfo Map(RawRelease r) => new(
        Version:      r.TagName ?? "",
        Name:         r.Name,
        Notes:        r.Body,
        PublishedAt:  r.PublishedAt ?? DateTimeOffset.UnixEpoch,
        IsPrerelease: r.Prerelease,
        Assets: (r.Assets ?? new()).Select(a => new ReleaseAsset(
            Name:        a.Name ?? "",
            DownloadUrl: a.BrowserDownloadUrl ?? "",
            Size:        a.Size,
            ContentType: a.ContentType)).ToList());

    // ── GitHub Releases JSON shapes ─────────────────────────────────────────

    private sealed class RawRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("assets")]
        public List<RawAsset>? Assets { get; set; }
    }

    private sealed class RawAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("content_type")]
        public string? ContentType { get; set; }
    }
}
