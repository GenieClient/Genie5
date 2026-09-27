namespace Genie.Core.Extensions.Builtin.SpellInfo;

/// <summary>How one download attempt ended: the parsed table on success, or a
/// short reason on failure. Never both.</summary>
internal sealed record SpellFetchResult(SpellTable? Table, string? Error);

/// <summary>
/// The on-disk copy of the spell feed, and the one place that downloads it.
///
/// <para><b>The feed is a third-party file</b> on someone else's storage account, so
/// it is treated gently: it is only ever fetched because a SpellInfo command needed
/// it (first use, or <c>/spellinfo refresh</c>), never while game text is being
/// processed, and never more than once a day — the feed itself is only regenerated
/// nightly, so asking more often buys nothing. The cache file's write time is the
/// clock for that.</para>
///
/// <para><b>A bad download never replaces a good cache.</b> The response is parsed
/// before anything is written, and the write goes through a temp file, so a torn
/// or truncated download leaves the previous copy in place.</para>
/// </summary>
internal sealed class SpellFeedCache
{
    /// <summary>The nightly Elanthipedia-derived feed, maintained by Etherian.</summary>
    public const string FeedUrl = "https://drservice.blob.core.windows.net/public-content/wiki/spells.json";

    /// <summary>How old the cache must be before it is fetched again.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan            _timeout;
    private readonly Func<DateTime>      _utcNow;

    /// <param name="path">Where the cache lives.</param>
    /// <param name="handler">Injected by tests; null uses a real handler.</param>
    /// <param name="utcNow">Injected by tests; null uses the system clock.</param>
    public SpellFeedCache(string path, HttpMessageHandler? handler = null,
                          Func<DateTime>? utcNow = null, TimeSpan? timeout = null)
    {
        CachePath = path;
        _handler  = handler;
        _utcNow   = utcNow ?? (static () => DateTime.UtcNow);
        _timeout  = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string CachePath { get; }

    /// <summary>When the cache was last written by a successful download, or null
    /// when there is no cache.</summary>
    public DateTime? CachedAtUtc => File.Exists(CachePath) ? File.GetLastWriteTimeUtc(CachePath) : null;

    /// <summary>True when there is no cache or it is at least <see cref="MaxAge"/> old.</summary>
    public bool IsStale => CachedAtUtc is not { } at || _utcNow() - at >= MaxAge;

    /// <summary>Read the cached copy. Null when there is none or it doesn't parse —
    /// a corrupt cache counts as no cache, so the next command downloads afresh.</summary>
    public SpellTable? LoadCached(Action<string>? log = null)
    {
        try
        {
            return File.Exists(CachePath) ? SpellFeed.Parse(File.ReadAllText(CachePath)) : null;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"SpellInfo: ignoring unreadable cache {CachePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Download, validate, and cache the feed.</summary>
    public async Task<SpellFetchResult> FetchAsync(CancellationToken ct = default)
    {
        string body;
        try
        {
            using var http = _handler is null
                ? new HttpClient()
                : new HttpClient(_handler, disposeHandler: false);
            http.Timeout = _timeout;

            using var resp = await http.GetAsync(FeedUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new SpellFetchResult(null, $"the feed answered {(int)resp.StatusCode}");
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new SpellFetchResult(null, $"couldn't reach the feed ({ex.Message})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SpellFetchResult(null, "the feed didn't answer in time");
        }

        SpellTable table;
        try { table = SpellFeed.Parse(body); }
        catch (FormatException ex) { return new SpellFetchResult(null, ex.Message); }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var tmp = CachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, body, ct).ConfigureAwait(false);
            File.Move(tmp, CachePath, overwrite: true);
            File.SetLastWriteTimeUtc(CachePath, _utcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The download is good; it just won't survive a restart. Use it anyway.
            return new SpellFetchResult(table, null);
        }

        return new SpellFetchResult(table, null);
    }
}
