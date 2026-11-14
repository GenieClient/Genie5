using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genie.Core.Update.Sources;

/// <summary>
/// File-list source backed by the GitHub Contents REST API
/// (<c>https://api.github.com/repos/{owner}/{repo}/contents/{path}</c>).
/// Returns one <see cref="FileEntry"/> per file in the listed folder,
/// optionally filtered by extension. The <see cref="FileEntry.Sha"/>
/// values are the raw git blob sha-1s returned by the API, computable
/// locally via <see cref="GitBlobSha1"/> so callers can skip downloads
/// for files that haven't changed.
///
/// Generalised from the original <c>MapRepoUpdater</c> — same HTTP shape,
/// same sha-skip optimisation, but the owner/repo/path/extension are now
/// configuration instead of constants so the same code serves any
/// loose-files-in-a-folder GitHub source (maps, scripts, art packs, …).
/// </summary>
public sealed class GithubContentsSource : IFileListSource
{
    private const string DefaultUserAgent = "Genie5-Updater";

    private readonly HttpClient _http;
    private readonly string     _owner;
    private readonly string     _repo;
    private readonly string     _path;       // url-encoded subdirectory; empty = root
    private readonly string?    _extension;  // case-insensitive extension filter (e.g. ".xml"); null = all files

    public string Description { get; }

    /// <summary>
    /// Construct a source for the given GitHub repo path.
    /// </summary>
    /// <param name="owner">Repository owner (e.g. <c>GenieClient</c>).</param>
    /// <param name="repo">Repository name (e.g. <c>Maps</c>).</param>
    /// <param name="path">
    ///   Optional subdirectory within the repo. Pass an empty string for the
    ///   repo root. Already-encoded paths (e.g. <c>Copy%20These%20to%20…</c>)
    ///   are accepted as-is; <see cref="Uri.EscapeDataString"/> any literal
    ///   path you pass in.</param>
    /// <param name="extension">
    ///   Filter listings to files ending with this extension (case-insensitive).
    ///   Pass null to accept all file entries.</param>
    /// <param name="http">
    ///   Optional shared <see cref="HttpClient"/>. When null, the source
    ///   creates and owns one configured with a 5-minute timeout and the
    ///   GitHub v3 Accept header.</param>
    /// <param name="description">
    ///   Optional override for <see cref="Description"/>. The default is
    ///   <c>owner/repo[/path]</c> with the path as given, which for an
    ///   encoded path reads as <c>Festivals%20Copy%20to…</c>; pass the
    ///   human form when the source will be named in status text.</param>
    public GithubContentsSource(
        string      owner,
        string      repo,
        string      path        = "",
        string?     extension   = null,
        HttpClient? http        = null,
        string?     description = null)
    {
        _owner     = owner;
        _repo      = repo;
        _path      = path ?? "";
        _extension = extension is null
            ? null
            : (extension.StartsWith('.') ? extension : "." + extension);

        Description = description
            ?? (string.IsNullOrEmpty(_path)
                    ? $"{_owner}/{_repo}"
                    : $"{_owner}/{_repo}/{_path}");

        if (http != null)
        {
            _http = http;
        }
        else
        {
            // Long timeout — the listing call is small but downloading every
            // file in series can run up against the default 100s on a slow link.
            _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        }
    }

    public async Task<FileListInfo> GetFileListAsync(CancellationToken ct = default)
    {
        var url         = BuildListingUrl();
        var listingJson = await _http.GetStringAsync(url, ct);
        var entries     = JsonSerializer.Deserialize<List<RepoEntry>>(listingJson) ?? new();

        var files = entries
            .Where(e =>
                e.Type == "file" &&
                !string.IsNullOrEmpty(e.DownloadUrl) &&
                (_extension is null || e.Name.EndsWith(_extension, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new FileEntry(e.Name, e.DownloadUrl!, e.Sha, e.Size))
            .ToList();

        return new FileListInfo(Description, files);
    }

    public async Task<byte[]> DownloadFileAsync(FileEntry file, CancellationToken ct = default)
    {
        return await _http.GetByteArrayAsync(file.DownloadUrl, ct);
    }

    /// <summary>The Contents API URL this source lists — exposed so a test can
    /// pin the folder a feed resolves to without a network call.</summary>
    public string ListingUrl => BuildListingUrl();

    private string BuildListingUrl() =>
        string.IsNullOrEmpty(_path)
            ? $"https://api.github.com/repos/{_owner}/{_repo}/contents/"
            : $"https://api.github.com/repos/{_owner}/{_repo}/contents/{_path}";

    /// <summary>
    /// Compute the git blob sha-1 for a local file. Git's blob hashing
    /// prepends a header — <c>"blob &lt;size&gt;\0"</c> — and hashes the
    /// concatenation. Matching what the GitHub Contents API returns as
    /// <c>sha</c> lets a caller cheaply skip writes for unchanged files.
    /// Exposed as a public static so updaters can reuse the same hash.
    /// </summary>
    public static string GitBlobSha1(string path)
    {
        var content = File.ReadAllBytes(path);
        var header  = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha = System.Security.Cryptography.SHA1.Create();
        sha.TransformBlock(header, 0, header.Length, null, 0);
        sha.TransformFinalBlock(content, 0, content.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>JSON shape returned by the GitHub Contents API.</summary>
    private sealed class RepoEntry
    {
        [JsonPropertyName("name")]
        public string Name         { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type         { get; set; } = "";

        [JsonPropertyName("download_url")]
        public string? DownloadUrl { get; set; }

        // Git blob sha returned by /contents/. Matches what `git hash-object`
        // produces for the same content; used by callers to skip writes for
        // unchanged files.
        [JsonPropertyName("sha")]
        public string? Sha         { get; set; }

        [JsonPropertyName("size")]
        public long? Size          { get; set; }
    }
}
