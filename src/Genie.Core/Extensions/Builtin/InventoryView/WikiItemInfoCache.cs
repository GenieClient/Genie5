using System.Text.Json;

namespace Genie.Core.Extensions.Builtin.InventoryView;

/// <summary>Elanthipedia-sourced facts about one item tap. Dimensions are the
/// wiki's game units; <see cref="Weight"/> is stones. Null field = the wiki
/// doesn't record it; a cached entry with all-null fields = the item has no
/// wiki page (negative result, cached so we don't re-ask every session).</summary>
public sealed record WikiItemInfo(
    string Tap,
    double? Weight,
    int? Length,
    int? Width,
    int? Height,
    string? PageUrl)
{
    /// <summary>"19×1×1" with "?" for gaps; empty when nothing is known.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SizeText
    {
        get
        {
            if (Length is null && Width is null && Height is null) return "";
            static string P(int? v) => v?.ToString() ?? "?";
            return $"{P(Length)}×{P(Width)}×{P(Height)}";
        }
    }

    /// <summary>Whole stones without a decimal tail; empty when unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string WeightText =>
        Weight is null ? "" : Weight.Value == Math.Floor(Weight.Value)
            ? ((long)Weight.Value).ToString()
            : Weight.Value.ToString("0.#");
}

/// <summary>
/// Resolves item taps to Elanthipedia's structured item data (weight and
/// dimensions) and remembers every answer — including "no page" — in a JSON
/// cache on disk, so a catalog's worth of lookups only ever hits the network
/// once per item.
///
/// <para><b>Two-phase resolve.</b> The wiki caps Semantic MediaWiki query
/// complexity (a disjunction of even ~18 titles errors with "restrictions on
/// query size or depth"), so existence is checked first with the ordinary
/// MediaWiki <c>action=query</c> API (40 titles per request, redirects
/// followed, no SMW limit); the SMW <c>ask</c> for weight/dimensions then runs
/// only over the pages that exist, in ≤12-title chunks. An API error at any
/// step aborts the batch WITHOUT caching — only an authoritative "no such
/// page" from phase 1 may negative-cache a tap.</para>
/// </summary>
public sealed class WikiItemInfoCache
{
    private const string WikiBase = "https://elanthipedia.play.net";
    /// <summary>Taps per resolve batch. Each tap expands to 4 namespace
    /// candidates → 40 existence titles, the anonymous API ceiling's safe side.</summary>
    public const int BatchSize = 10;
    /// <summary>Max title disjuncts per SMW ask (the wiki rejects ~18).</summary>
    private const int AskChunk = 12;

    private readonly string _cacheFile;
    private readonly System.Threading.Lock _sync = new();
    private readonly Dictionary<string, WikiItemInfo> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <summary>Overridable network seam for tests: SMW ask query text → raw
    /// JSON response (null on failure). Default POSTs to api.php.</summary>
    internal Func<string, Task<string?>> AskApi;

    /// <summary>Overridable network seam for tests: page titles → raw
    /// <c>action=query</c> JSON (existence + redirects), null on failure.</summary>
    internal Func<IReadOnlyList<string>, Task<string?>> QueryApi;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public WikiItemInfoCache(string dataRoot)
    {
        _cacheFile = Path.Combine(dataRoot, "InventoryView.wikicache.json");
        AskApi   = DefaultAskApi;
        QueryApi = DefaultQueryApi;
    }

    /// <summary>Cache-only lookup (no network). Null = not resolved yet.</summary>
    public WikiItemInfo? Get(string tap)
    {
        EnsureLoaded();
        lock (_sync)
            return _cache.TryGetValue(InventoryViewExtension.CleanTap(tap), out var info) ? info : null;
    }

    /// <summary>The subset of <paramref name="taps"/> with no cache entry yet.</summary>
    public List<string> Unresolved(IEnumerable<string> taps)
    {
        EnsureLoaded();
        lock (_sync)
            return taps.Select(InventoryViewExtension.CleanTap)
                       .Where(t => t.Length > 0 && !_cache.ContainsKey(t))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();
    }

    /// <summary>Resolve one batch of cleaned taps against the wiki. Phase 1
    /// (existence) decides which taps have pages and which are authoritative
    /// misses; phase 2 (SMW ask) pulls weight/dimensions for the existing ones.
    /// Any API failure aborts WITHOUT caching, so the batch is retried on a
    /// later run — an error must never masquerade as "no page". Returns how
    /// many taps were settled (data or authoritative negative).</summary>
    public async Task<int> ResolveBatchAsync(IReadOnlyList<string> cleanedTaps)
    {
        if (cleanedTaps.Count == 0) return 0;

        // ── Phase 1: which candidate pages exist? ────────────────────────────
        var allCandidates = cleanedTaps
            .SelectMany(InventoryViewExtension.CandidateTitles).ToList();
        var existJson = await QueryApi(allCandidates).ConfigureAwait(false);
        if (existJson is null) return 0;

        (HashSet<string> Existing, Dictionary<string, string> Renames)? existence;
        try   { existence = ParseExistence(existJson); }
        catch { return 0; }
        if (existence is null) return 0;   // {"error":…} response
        var (existing, renames) = existence.Value;

        // Per tap: the first (most-preferred) candidate that exists, mapped
        // through normalization/redirects to the title ask will key results by.
        var tapByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing    = new List<string>();
        foreach (var tap in cleanedTaps)
        {
            string? hit = null;
            foreach (var candidate in InventoryViewExtension.CandidateTitles(tap))
            {
                var final = FollowRenames(candidate, renames);
                if (existing.Contains(final)) { hit = final; break; }
            }
            if (hit is not null) tapByTitle.TryAdd(hit, tap);
            else                 missing.Add(tap);
        }

        // ── Phase 2: SMW ask for the existing pages, in small chunks ─────────
        var infoByTitle = new Dictionary<string, WikiItemInfo>(StringComparer.OrdinalIgnoreCase);
        var titles = tapByTitle.Keys.ToList();
        for (int i = 0; i < titles.Count; i += AskChunk)
        {
            var chunk = titles.Skip(i).Take(AskChunk).ToList();
            var query = $"[[{string.Join("||", chunk)}]]" +
                        "|?Weight of|?Length is|?Width is|?Height is" +
                        $"|limit={chunk.Count}";
            var json = await AskApi(query).ConfigureAwait(false);
            if (json is null) return 0;

            Dictionary<string, WikiItemInfo>? parsed;
            try   { parsed = ParseAskByTitle(json); }
            catch { return 0; }
            if (parsed is null) return 0;   // {"error":…} response
            foreach (var kv in parsed) infoByTitle[kv.Key] = kv.Value;
        }

        // ── Commit: data for hits, negatives ONLY for authoritative misses ───
        int settled = 0;
        lock (_sync)
        {
            foreach (var (title, tap) in tapByTitle)
            {
                _cache[tap] = infoByTitle.TryGetValue(title, out var info)
                    ? info with { Tap = tap }
                    // Page exists but SMW returned nothing for it: keep the
                    // page link, no stats.
                    : new WikiItemInfo(tap, null, null, null, null,
                          $"{WikiBase}/index.php?title={Uri.EscapeDataString(title)}");
                settled++;
            }
            foreach (var tap in missing)
            {
                _cache[tap] = new WikiItemInfo(tap, null, null, null, null, null);
                settled++;
            }
        }
        Save();
        return settled;
    }

    private static string FollowRenames(string title, Dictionary<string, string> renames)
    {
        // normalized + redirect hops; bounded in case of a redirect loop.
        for (int hops = 0; hops < 5 && renames.TryGetValue(title, out var next); hops++)
            title = next;
        return title;
    }

    /// <summary>action=query JSON → (existing final titles, rename map from
    /// "normalized" + "redirects"). Null when the API reported an error.</summary>
    internal static (HashSet<string> Existing, Dictionary<string, string> Renames)? ParseExistence(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out _)) return null;
        if (!doc.RootElement.TryGetProperty("query", out var query)) return null;

        // Ordinal keys: MediaWiki titles are case-sensitive past the first
        // letter, and a normalization hop ("Item:vault book" → "Item:Vault
        // book") must not collide with a redirect FROM the normalized title.
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in new[] { "normalized", "redirects" })
            if (query.TryGetProperty(section, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var entry in arr.EnumerateArray())
                    if (entry.TryGetProperty("from", out var f) && entry.TryGetProperty("to", out var t) &&
                        f.GetString() is { } from && t.GetString() is { } to)
                        renames[from] = to;

        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (query.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Object)
            foreach (var page in pages.EnumerateObject())
                if (!page.Value.TryGetProperty("missing", out _) &&
                    page.Value.TryGetProperty("title", out var title) &&
                    title.GetString() is { } t)
                    existing.Add(t);
        return (existing, renames);
    }

    /// <summary>SMW ask JSON → full page title → info (Tap left as the title;
    /// the caller rebinds it to the originating tap). Null when the API
    /// reported an error (query too large, etc.) — never treat that as empty.</summary>
    internal static Dictionary<string, WikiItemInfo>? ParseAskByTitle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out _)) return null;

        var found = new Dictionary<string, WikiItemInfo>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Object)
            return found;   // a valid response can legitimately have no results

        foreach (var page in results.EnumerateObject())
        {
            var printouts = page.Value.GetProperty("printouts");
            var url = page.Value.TryGetProperty("fullurl", out var u) ? u.GetString() : null;
            found[page.Name] = new WikiItemInfo(
                page.Name,
                Num(printouts, "Weight of"),
                (int?)Num(printouts, "Length is"),
                (int?)Num(printouts, "Width is"),
                (int?)Num(printouts, "Height is"),
                url);
        }
        return found;
    }

    private static double? Num(JsonElement printouts, string property)
    {
        if (!printouts.TryGetProperty(property, out var arr) ||
            arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;
        var first = arr[0];
        return first.ValueKind switch
        {
            JsonValueKind.Number => first.GetDouble(),
            JsonValueKind.String when double.TryParse(first.GetString(), out var d) => d,
            _ => null,
        };
    }

    private static async Task<string?> DefaultAskApi(string query) =>
        await PostApi(new Dictionary<string, string>
        {
            ["action"] = "ask",
            ["format"] = "json",
            ["query"]  = query,
        }).ConfigureAwait(false);

    private static async Task<string?> DefaultQueryApi(IReadOnlyList<string> titles) =>
        await PostApi(new Dictionary<string, string>
        {
            ["action"]    = "query",
            ["format"]    = "json",
            ["redirects"] = "1",
            ["titles"]    = string.Join("|", titles),
        }).ConfigureAwait(false);

    private static async Task<string?> PostApi(Dictionary<string, string> form)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var resp = await Http.PostAsync($"{WikiBase}/api.php", content).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ── Disk cache ─────────────────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(_cacheFile)) return;
                var entries = JsonSerializer.Deserialize<List<WikiItemInfo>>(File.ReadAllText(_cacheFile));
                foreach (var e in entries ?? new())
                    _cache[e.Tap] = e;
            }
            catch { /* corrupt cache = cold start; it rebuilds itself */ }
        }
    }

    private void Save()
    {
        try
        {
            List<WikiItemInfo> entries;
            lock (_sync) entries = _cache.Values.ToList();
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
            File.Move(tmp, _cacheFile, overwrite: true);
        }
        catch { /* cache persistence is best-effort */ }
    }
}
