using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genie.Core.Update.Sources;

/// <summary>
/// File-list source backed by the GitHub Git Trees API
/// (<c>https://api.github.com/repos/{owner}/{repo}/git/trees/HEAD?recursive=1</c>).
///
/// Unlike <see cref="GithubContentsSource"/> — which lists a single folder —
/// this source sees the ENTIRE repo tree in one API call, so it serves repos
/// that organize their content in subdirectories (script collections like
/// Tirost/DR-Genie-Scripts keep each script suite in its own folder). The
/// returned <see cref="FileEntry.Name"/> is the path relative to the
/// configured root, forward-slash separated (e.g. <c>GenieHunter/hunt.cmd</c>),
/// so updaters can mirror the repo layout on disk.
///
/// Downloads go through <c>raw.githubusercontent.com/{owner}/{repo}/HEAD/…</c>
/// (the trees listing carries blob shas but no download URLs). <c>HEAD</c>
/// resolves to the repo's default branch on both endpoints, so no extra
/// "what's the default branch" round-trip is needed.
///
/// The <see cref="FileEntry.Sha"/> values are git blob sha-1s — same hash
/// the Contents API reports — so <see cref="GithubContentsSource.GitBlobSha1"/>
/// works for the local skip-if-unchanged comparison.
/// </summary>
public sealed class GithubTreeSource : IFileListSource
{
    private readonly HttpClient _http;
    private readonly string     _owner;
    private readonly string     _repo;
    private readonly string     _pathPrefix;  // repo subdirectory to root at; empty = whole repo
    private readonly string[]   _extensions;  // lowercase ".ext" filters; empty = all files

    public string Description { get; }

    /// <summary>
    /// Construct a recursive tree source for the given GitHub repo.
    /// </summary>
    /// <param name="owner">Repository owner (e.g. <c>Tirost</c>).</param>
    /// <param name="repo">Repository name (e.g. <c>DR-Genie-Scripts</c>).</param>
    /// <param name="path">
    ///   Optional subdirectory to treat as the root. Entries outside it are
    ///   ignored and returned names are relative to it. Empty = whole repo.</param>
    /// <param name="extensions">
    ///   Comma-separated extension filter (e.g. <c>".cmd,.js,.inc"</c>),
    ///   case-insensitive; leading dots optional. Null/empty = all files.</param>
    /// <param name="http">
    ///   Optional shared <see cref="HttpClient"/>. When null, the source
    ///   creates and owns one configured like <see cref="GithubContentsSource"/>'s.</param>
    public GithubTreeSource(
        string      owner,
        string      repo,
        string      path       = "",
        string?     extensions = null,
        HttpClient? http       = null)
    {
        _owner      = owner;
        _repo       = repo;
        _pathPrefix = (path ?? "").Trim('/');
        _extensions = ParseExtensions(extensions);

        Description = string.IsNullOrEmpty(_pathPrefix)
            ? $"{_owner}/{_repo}"
            : $"{_owner}/{_repo}/{_pathPrefix}";

        _http = http ?? GithubHttp.CreateClient();
    }

    /// <summary>Split a <c>".cmd,.js"</c>-style filter string into normalized lowercase extensions.</summary>
    public static string[] ParseExtensions(string? extensions) =>
        (extensions ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
            .ToArray();

    public async Task<FileListInfo> GetFileListAsync(CancellationToken ct = default)
    {
        var url  = $"https://api.github.com/repos/{_owner}/{_repo}/git/trees/HEAD?recursive=1";
        var json = await GithubHttp.GetStringAsync(_http, url, ct);
        var tree = JsonSerializer.Deserialize<TreeResponse>(json) ?? new();

        var prefix = _pathPrefix.Length == 0 ? "" : _pathPrefix + "/";
        var files  = (tree.Tree ?? new())
            .Where(e => e.Type == "blob" && !string.IsNullOrEmpty(e.Path))
            .Where(e => prefix.Length == 0 ||
                        e.Path!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(e => _extensions.Length == 0 ||
                        _extensions.Any(x => e.Path!.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new FileEntry(
                Name:        prefix.Length == 0 ? e.Path! : e.Path![prefix.Length..],
                DownloadUrl: BuildRawUrl(e.Path!),
                Sha:         e.Sha,
                Size:        e.Size))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // tree.Truncated means the repo exceeded the API's tree-size limit
        // (~100k entries) — far beyond any script repo, but surface it in the
        // identity string rather than silently under-reporting.
        var identity = tree.Truncated ? $"{Description} (listing truncated!)" : Description;
        return new FileListInfo(identity, files);
    }

    public async Task<byte[]> DownloadFileAsync(FileEntry file, CancellationToken ct = default)
    {
        return await GithubHttp.GetBytesAsync(_http, file.DownloadUrl, ct);
    }

    /// <summary>Escape each path segment individually so '/' separators survive.</summary>
    private string BuildRawUrl(string repoPath)
    {
        var escaped = string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString));
        return $"https://raw.githubusercontent.com/{_owner}/{_repo}/HEAD/{escaped}";
    }

    /// <summary>JSON shape returned by the Git Trees API.</summary>
    private sealed class TreeResponse
    {
        [JsonPropertyName("tree")]
        public List<TreeEntry>? Tree { get; set; }

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }
    }

    private sealed class TreeEntry
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        // "blob" for files, "tree" for directories.
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }
    }
}
