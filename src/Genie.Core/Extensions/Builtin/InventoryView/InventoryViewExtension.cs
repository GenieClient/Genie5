using System.Text.RegularExpressions;
using System.Xml.Linq;
using Genie.Core.Events;

namespace Genie.Core.Extensions.Builtin.InventoryView;

/// <summary>
/// The Inventory View catalog: <c>/iv scan</c> walks the character's possessions
/// (inventory → vault book → deed register → home → Trader storage) and stores
/// them as a per-character item tree in the Genie-4-compatible
/// <c>InventoryView.xml</c>, searchable across characters. Absorbed from the
/// external <c>Plugin_InventoryViewV5</c> DLL (itself a port of Etherian's
/// Genie 4 plugin, v1.8) — the DLL id <c>genie.inventoryview</c> is retired in
/// <see cref="Plugins.PluginManager"/>.
///
/// <para><b>UI seam:</b> unlike the text trackers this extension does not render
/// through the named-window seam. The App's first-class Inventory View window
/// binds a TreeView to <see cref="SnapshotCatalog"/> and refreshes on
/// <see cref="CatalogChanged"/>; <c>/iv open</c>/<c>/iv search</c> raise
/// <see cref="OpenRequested"/> so the window can pop and pre-fill its search
/// box. Console hosts (no subscriber) fall back to echoed text.</para>
///
/// <para><b>Merged-line format:</b> DR's current <c>INV LIST</c> concatenates the
/// whole list onto one raw line — items keep their old leading indent (2 spaces
/// = level 1, 5 spaces + dash = level 2, 8 = level 3) but the newlines are gone.
/// <see cref="SplitMergedItems"/> re-derives the item boundaries from the runs
/// of 2+ spaces; the classic one-item-per-line output degrades to the same
/// parse. Catalogs saved by the pre-fix plugin (one giant merged tap) are
/// repaired on load.</para>
/// </summary>
public sealed class InventoryViewExtension : IGameExtension
{
    public string Name        => "InventoryView";
    public string Version     => "2.1";
    public string Description => "Catalogs each character's items (person, vault, deeds, home, Trader storage) and searches them across characters.";
    public bool   Enabled     { get; set; } = true;

    private IExtensionHost _host = null!;
    private CancellationTokenSource _cts = new();

    /// <summary>Guards <see cref="_characterData"/> — the scan mutates on the
    /// parser thread while the App reads snapshots on the UI thread.</summary>
    private readonly object _sync = new();
    private List<CharacterData> _characterData = new();

    // ── Scan state (mirrors the Genie 4 plugin's FSM fields) ──────────────────
    /// <summary>null = not scanning; else the FSM phase. Written on the parser
    /// thread, read from the UI thread (<see cref="ScanInProgress"/>, the Scan
    /// button) and from the watchdog task — volatile so those reads can't see a
    /// stale phase.</summary>
    private volatile string? _scanMode;
    private int _level = 1;               // current container depth while scanning
    private CharacterData? _currentData;  // character+source being filled
    private ItemData? _lastItem;          // last node added (the parent-walk anchor)
    private bool _debug;

    /// <summary>The character whose rows the in-flight scan is filling, so an
    /// abandoned scan can drop exactly what it added.</summary>
    private string _scanCharacter = "";
    /// <summary>Has the <c>inventory list</c> response produced an item yet, and
    /// has anything else pushed in ahead of it? Together they tell the list's own
    /// closing roundtime apart from a combat roundtime landing mid-list.</summary>
    private bool _invItemsSeen, _invChatterSeen;
    /// <summary>UTC ticks after which an unadvanced scan is abandoned. Every phase
    /// change and every catalogued line pushes it out (<see cref="TouchScan"/>).</summary>
    private long _scanDeadlineTicks;

    /// <summary>Which scan the watchdog belongs to, so a watchdog left sleeping by
    /// a cancelled scan retires instead of guarding the next one.</summary>
    private int _scanGeneration;
    /// <summary>Serializes the claim on ending a scan — <c>/iv cancel</c>, the
    /// watchdog and a character switch can race for it.</summary>
    private readonly object _scanLifecycle = new();

    /// <summary>How long one scan step may go without the game advancing it before
    /// the scan gives up. Every FSM phase blocks on specific trigger text, so a
    /// stun, a full pair of hands, a disconnect, or simply a reworded server
    /// message would otherwise park the scan forever — and with it every later
    /// <c>/iv scan</c>. Settable for tests.</summary>
    internal TimeSpan ScanIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

    // ── UI-facing surface ──────────────────────────────────────────────────────
    /// <summary>The catalog changed (scan complete, reload, repair, or character
    /// removed). Raised on the game/parser thread — UI subscribers marshal.</summary>
    public event Action? CatalogChanged;

    /// <summary>The user asked for the window (<c>/iv open [character]</c> /
    /// <c>/iv search &lt;text&gt;</c>). Arg = text to pre-fill the window's search
    /// box ("" for a plain open). When nothing subscribes (console host), the
    /// catalog/matches are echoed instead.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>A background wiki batch landed — freshly-resolved weight/size
    /// data is readable via <see cref="ItemInfo"/>. Raised off-thread.</summary>
    public event Action? ItemInfoUpdated;

    /// <summary>A scan started or stopped (completed, cancelled, or timed out) —
    /// the App gates its Scan/Cancel/Reload buttons on
    /// <see cref="ScanInProgress"/>. Raised on the parser or watchdog thread; UI
    /// subscribers marshal.</summary>
    public event Action? ScanStateChanged;

    private WikiItemInfoCache? _itemInfo;
    private PlazaShopService? _plaza;
    private bool _infoResolveRunning;

    /// <summary>Elanthipedia weight/size per tap (persistent-cached).</summary>
    public WikiItemInfoCache ItemInfo => _itemInfo ??= new WikiItemInfoCache(_host.DataRoot);
    /// <summary>DR Service Plaza player-shop listings (cached export).</summary>
    public PlazaShopService Plaza => _plaza ??= new PlazaShopService(_host.DataRoot);

    /// <summary>Kick off background wiki resolution for any of these taps that
    /// have no cached answer. Batched + throttled; <see cref="ItemInfoUpdated"/>
    /// fires after each batch. Re-entrant calls while a run is active are
    /// no-ops (the active run picks the taps up via the cache check).</summary>
    public void RequestItemInfo(IEnumerable<string> taps)
    {
        var pending = ItemInfo.Unresolved(taps);
        if (pending.Count == 0) return;
        lock (_sync)
        {
            if (_infoResolveRunning) return;
            _infoResolveRunning = true;
        }

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < pending.Count && !token.IsCancellationRequested;
                     i += WikiItemInfoCache.BatchSize)
                {
                    var batch = pending.Skip(i).Take(WikiItemInfoCache.BatchSize).ToList();
                    await ItemInfo.ResolveBatchAsync(batch).ConfigureAwait(false);
                    ItemInfoUpdated?.Invoke();
                    await Task.Delay(300, token).ConfigureAwait(false);   // politeness gap
                }
            }
            catch (TaskCanceledException) { }
            finally
            {
                lock (_sync) _infoResolveRunning = false;
            }
        });
    }

    /// <summary>Search player-shop listings (tap + look). Null = no data
    /// obtainable (offline with a cold cache).</summary>
    public Task<IReadOnlyList<ShopListing>?> ShopSearchAsync(string term) =>
        Plaza.SearchAsync(term);

    /// <summary>Deep copy of the current catalog, safe to walk on any thread.</summary>
    public List<CharacterData> SnapshotCatalog()
    {
        lock (_sync) return _characterData.Select(c => c.Clone()).ToList();
    }

    public bool ScanInProgress => _scanMode != null;

    public void Initialize(IExtensionHost host)
    {
        _host = host;
        _cts  = new CancellationTokenSource();
        LoadSettings(initial: true);
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    public void OnGameLine(string line)     { }   // stream-agnostic — the FSM feeds from OnGameEvent
    public void OnCommandSent(string command) { }

    /// <summary>The scan FSM only ever consumes main-stream text: feeding from the
    /// typed event (which carries the stream id) keeps thoughts/logons/combat
    /// stream-window lines from being catalogued as items mid-scan.</summary>
    public void OnGameEvent(GameEvent ev)
    {
        if (_scanMode != null && ev is TextEvent { Stream: "main" } te)
            HandleScanLine(te.Text);
    }

    /// <summary>End-of-home signal (Genie 4 keyed off a bare "&gt;" line).</summary>
    public void OnPrompt()
    {
        if (_scanMode == "Home")
            AfterHome();
    }

    /// <summary>A character SWITCH mid-session: abandon any in-flight scan (its
    /// trigger text will never arrive). The catalog itself is cross-character by
    /// design and survives.</summary>
    public void OnReset() =>
        AbandonScan("InventoryView: scan abandoned (character switch); the catalog is unchanged.");

    // ── /iv command handling ───────────────────────────────────────────────────
    public bool OnSlashCommand(string input)
    {
        var trimmed = input.Trim();
        var lower   = trimmed.ToLowerInvariant();
        if (lower != "/inventoryview" && lower != "/iv" &&
            !lower.StartsWith("/inventoryview ") && !lower.StartsWith("/iv "))
            return false;

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb  = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "help";
        var arg   = parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : "";

        switch (verb)
        {
            case "scan":   StartScan();                                   break;
            case "cancel":
            case "stop":   CancelScan();                                  break;
            case "open":
            case "list":   RequestOpen(arg, search: false);               break;
            case "search": RequestOpen(arg, search: true);                break;
            case "reload": ReloadFromDisk();                              break;
            case "wiki":   WikiLookup(arg);                               break;
            case "shops":  ShopSearchCommand(arg);                        break;
            case "export": ExportCsv(string.IsNullOrWhiteSpace(arg) ? null : arg); break;
            case "debug":
                _debug = !_debug;
                _host.Echo("InventoryView debug mode " + (_debug ? "ON" : "OFF"));
                break;
            case "help":
            default:       Help();                                        break;
        }
        return true;
    }

    private void Help()
    {
        _host.Echo("Inventory View options:");
        _host.Echo("/iv scan             -- scan the current character (person, vault, deeds, home, trader) and save.");
        _host.Echo("/iv cancel           -- abandon a scan that is stuck waiting on the game.");
        _host.Echo("/iv open [character] -- open the Inventory View window (Window menu), optionally filtered.");
        _host.Echo("/iv search <text>    -- open the window with <text> in the search box.");
        _host.Echo("/iv reload           -- reload InventoryView.xml from disk (after scanning on another instance).");
        _host.Echo("/iv wiki <item>      -- look <item> up on the wiki in your browser.");
        _host.Echo("/iv shops <text>     -- search player-shop listings (DR Service Plaza data).");
        _host.Echo("/iv export [path]    -- export the catalog to a CSV file.");
        _host.Echo("(\"/inventoryview\" works as the long form of \"/iv\".)");
    }

    // ── Scan ───────────────────────────────────────────────────────────────────
    public void StartScan()
    {
        if (_scanMode != null)
        {
            _host.Echo("InventoryView: a scan is already in progress.");
            return;
        }
        if (Global("connected") != "1")
        {
            _host.Echo("You must be connected to the server to do a scan.");
            return;
        }

        LoadSettings();                       // pick up other instances' data first
        var me = Global("charactername");
        lock (_sync) _characterData.RemoveAll(c => c.Name == me);
        _scanCharacter = me;
        SetPhase("Start");
        StartWatchdog(Interlocked.Increment(ref _scanGeneration));
        ScanStateChanged?.Invoke();
        _host.SendCommand("inventory list");
    }

    /// <summary>User-facing abandon (<c>/iv cancel</c> and the window's Cancel
    /// button). Reverts the catalog to what is on disk — a scan only persists at
    /// <see cref="CompleteScan"/>, so nothing committed is lost.</summary>
    public void CancelScan()
    {
        if (_scanMode is null)
        {
            _host.Echo("InventoryView: no scan is running.");
            return;
        }
        AbandonScan("InventoryView: scan cancelled; the catalog is unchanged.");
    }

    /// <summary>Drop an in-flight scan and restore the on-disk catalog, discarding
    /// the partial rows the scan had already added under
    /// <see cref="_scanCharacter"/>. Without this an interrupted scan left a
    /// truncated catalog behind that the next <see cref="SaveSettings"/> persisted
    /// as if it were real. No-op when no scan is running; may run on the watchdog
    /// thread.</summary>
    private void AbandonScan(string? message)
    {
        lock (_scanLifecycle)          // only one of cancel/watchdog/switch may end it
        {
            if (_scanMode is null) return;
            _scanMode = null;
        }
        lock (_sync)
        {
            _characterData.RemoveAll(c =>
                string.Equals(c.Name, _scanCharacter, StringComparison.OrdinalIgnoreCase));
            _currentData = null;
            _lastItem    = null;
            _level       = 1;
        }
        _scanCharacter = "";
        LoadSettings();          // the scan never saved, so disk is the pre-scan truth
        if (message is not null) _host.Echo(message);
        ScanStateChanged?.Invoke();
        CatalogChanged?.Invoke();
    }

    public void ReloadFromDisk()
    {
        // Reloading swaps _characterData wholesale, which would orphan the
        // CharacterData an in-flight scan is filling: the scan would go on writing
        // into a detached object and then "complete" having catalogued nothing.
        if (_scanMode is not null)
        {
            _host.Echo("InventoryView: a scan is in progress — reload skipped " +
                       "(wait for it to finish, or /iv cancel).");
            return;
        }
        LoadSettings();
        _host.Echo("InventoryView data reloaded.");
        CatalogChanged?.Invoke();
    }

    /// <summary>Drop one character's catalog (all sources) and persist.</summary>
    public bool RemoveCharacter(string name)
    {
        int removed;
        lock (_sync) removed = _characterData.RemoveAll(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return false;
        SaveSettings();
        _host.Echo($"InventoryView: removed {name} from the catalog.");
        CatalogChanged?.Invoke();
        return true;
    }

    public void WikiLookup(string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            _host.Echo("Usage: /iv wiki <item text>");
            return;
        }
        _ = WikiLookupAsync(item);
    }

    private const string WikiBase = "https://elanthipedia.play.net";

    /// <summary>Overridable page-existence probe for tests. Takes the candidate
    /// titles ("Item:Steel ingot", "Weapon:…", …), returns the first that exists
    /// on the wiki (redirects followed) or null. The default implementation asks
    /// the MediaWiki API; any network failure degrades to null (→ search page).</summary>
    internal Func<IReadOnlyList<string>, Task<string?>> ResolveWikiTitle = QueryWikiApi;

    /// <summary>Open the item's wiki page. drservice.info's Genie 4 tap→wiki
    /// service is dead (it redirects to the retired
    /// <c>elanthipedia.play.net/Mediawiki/index.php/…</c> URL scheme), so this
    /// asks Elanthipedia itself: exact <c>Item:</c>/<c>Weapon:</c>/<c>Armor:</c>/
    /// <c>Shield:</c> page when one exists, otherwise the wiki's search page.</summary>
    internal async Task WikiLookupAsync(string item)
    {
        var term  = CleanTap(item);
        var title = await ResolveWikiTitle(CandidateTitles(term)).ConfigureAwait(false);
        var url   = title is not null
            ? $"{WikiBase}/index.php?title={Uri.EscapeDataString(title)}"
            : $"{WikiBase}/index.php?title=Special%3ASearch&search={Uri.EscapeDataString(term)}";
        _host.RunHashCommand("#browser " + url);
    }

    /// <summary>Tap → search term: drop the leading article and the transient
    /// "(closed)" suffix DR appends to closed containers. Public — the App's
    /// window uses the same normalization for shop searches.</summary>
    public static string CleanTap(string tap)
    {
        var t = tap.Trim();
        if (t.EndsWith(" (closed)", StringComparison.OrdinalIgnoreCase))
            t = t[..^" (closed)".Length].TrimEnd();
        foreach (var article in new[] { "a ", "an ", "some ", "the " })
            if (t.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                t = t.Substring(article.Length);
                break;
            }
        return t;
    }

    /// <summary>The Elanthipedia namespaces that hold item pages, most-likely first.</summary>
    internal static IReadOnlyList<string> CandidateTitles(string term) =>
        new[] { $"Item:{term}", $"Weapon:{term}", $"Armor:{term}", $"Shield:{term}" };

    private static readonly HttpClient WikiHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<string?> QueryWikiApi(IReadOnlyList<string> titles)
    {
        try
        {
            var url = $"{WikiBase}/api.php?action=query&format=json&redirects=1&titles=" +
                      Uri.EscapeDataString(string.Join("|", titles));
            using var doc = System.Text.Json.JsonDocument.Parse(
                await WikiHttp.GetStringAsync(url).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages))
                return null;

            // "pages" is keyed by pageid (negative = missing). Collect the titles
            // that exist, then keep the candidates' preference order.
            var existing = new List<string>();
            foreach (var page in pages.EnumerateObject())
                if (!page.Value.TryGetProperty("missing", out _) &&
                    page.Value.TryGetProperty("title", out var t) &&
                    t.GetString() is { } pageTitle)
                    existing.Add(pageTitle);

            foreach (var candidate in titles)   // preference order, redirect-tolerant
            {
                var match = existing.FirstOrDefault(e =>
                    string.Equals(e, candidate, StringComparison.OrdinalIgnoreCase) ||
                    e.StartsWith(candidate.Split(':')[0] + ":", StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            return existing.Count > 0 ? existing[0] : null;
        }
        catch
        {
            return null;   // offline / API change → the search page still works
        }
    }

    /// <summary>Console form of the shop search: echo the top matches. (The
    /// window's "Find in Shops" button calls <see cref="ShopSearchAsync"/> and
    /// renders its own list.)</summary>
    private void ShopSearchCommand(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            _host.Echo("Usage: /iv shops <text>");
            return;
        }
        term = term.Trim();
        _host.Echo($"InventoryView: searching player shops for \"{term}\"…");
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            var results = await ShopSearchAsync(term).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (results is null)
            {
                _host.Echo("InventoryView: shop data unavailable (offline and no cached copy yet).");
                return;
            }
            _host.Echo($"InventoryView: {results.Count} shop listing(s) match \"{term}\"" +
                       (results.Count >= 100 ? " (capped at 100)" : "") + ".");
            const int cap = 25;
            foreach (var l in results.Take(cap))
                _host.Echo($"  {l.Tap} — {l.Price} — {l.Shop} ({l.City})");
            if (results.Count > cap)
                _host.Echo($"  … and {results.Count - cap} more (use the Inventory View window).");
            if (Plaza.DataTimestampUtc is { } ts)
                _host.Echo($"  [Plaza data fetched {ts:yyyy-MM-dd HH:mm} UTC — drservice.info]");
        });
    }

    private void RequestOpen(string arg, bool search)
    {
        if (search && string.IsNullOrWhiteSpace(arg))
        {
            _host.Echo("Usage: /iv search <text>");
            return;
        }

        if (OpenRequested is not null)
        {
            OpenRequested(arg.Trim());
            return;
        }

        // Console host — no window to open; echo instead.
        if (search) EchoSearch(arg.Trim());
        else        EchoSummary();
    }

    private string Global(string name) =>
        _host.Globals.TryGetValue(name, out var v) ? v : "";

    // ── The scan state machine (Genie 4 ParseText parity) ──────────────────────
    private void HandleScanLine(string text)
    {
        string trimtext = text.Trim('\n', '\r', ' ');
        if (string.IsNullOrEmpty(trimtext)) return;

        switch (_scanMode)
        {
            case "Start":
                if (trimtext == "You have:")    // start of "inventory list"
                {
                    _host.Echo("Scanning Inventory.");
                    SetPhase("Inventory");
                    _currentData = NewSource("Inventory");
                    _level       = 1;
                    _invItemsSeen = _invChatterSeen = false;
                }
                break;

            case "Inventory":
                if (text.StartsWith("[Use INVENTORY HELP")) { /* skip */ }
                else if (text.StartsWith("Roundtime:"))     // end of "inventory list"
                {
                    // Combat roundtimes land on the main stream too, and one
                    // arriving before the list did would cut the scan short with an
                    // empty Inventory. Accept a roundtime as the terminator once the
                    // list has actually produced items — or when nothing has pushed
                    // in ahead of it, so a genuinely empty inventory still ends.
                    if (_invItemsSeen || !_invChatterSeen)
                    {
                        // Inventory list applies an RT proportional to item count. Wait
                        // it out (non-blocking) before grabbing the vault book.
                        int seconds = ParseRoundtime(trimtext);
                        SetPhase("VaultStart", extraSeconds: seconds);
                        PauseForRtThenSend(seconds, "get my vault book");
                    }
                    else if (_debug)
                        _host.Echo("[IV dbg] ignored a roundtime that arrived before the list");
                }
                else if (AddItems(text, InventoryLevel, rootStorage: false))
                    _invItemsSeen = true;
                else
                    _invChatterSeen = true;
                break;

            case "VaultStart":
                if (Regex.IsMatch(trimtext, @"^You get a.*vault book.*from") ||
                    trimtext == "You are already holding that.")
                {
                    _host.Echo("Scanning Vault.");
                    _host.SendCommand("read my vault book");
                }
                else if (trimtext == "Vault Inventory:")
                {
                    SetPhase("Vault");
                    _currentData = NewSource("Vault");
                    _level       = 1;
                }
                else if (trimtext == "What were you referring to?" ||
                         trimtext == "The script that the vault book is written in is unfamiliar to you.  You are unable to read it." ||
                         trimtext == "The vault book is filled with blank pages pre-printed with branch office letterhead.  An advertisement touting the services of Rundmolen Bros. Storage Co. is pasted on the inside cover.")
                {
                    _host.Echo("Skipping Vault.");
                    SetPhase("DeedStart");
                    _host.SendCommand("get my deed register");
                }
                break;

            case "Vault":
                if (text.StartsWith("The last note in your book indicates that your vault contains"))
                {
                    SetPhase("DeedStart");
                    _host.SendCommand("stow my vault book");
                    _host.SendCommand("get my deed register");
                }
                else AddItems(text, VaultLevel, rootStorage: true);
                break;

            case "DeedStart":
                if (Regex.IsMatch(trimtext, @"^You get a.*deed register.*from") ||
                    trimtext == "You are already holding that.")
                {
                    _host.Echo("Scanning Deed Register.");
                    _host.SendCommand("turn my deed register to contents");
                    _host.SendCommand("read my deed register");
                }
                else if (trimtext == "Page -- Deed")
                {
                    SetPhase("Deed");
                    _currentData = NewSource("Deed");
                    _level       = 1;
                }
                else if (trimtext == "What were you referring to?" ||
                         trimtext.StartsWith("You haven't stored any deeds in this register."))
                {
                    _host.Echo("Skipping Deed Register.");
                    SetPhase("HomeStart");
                    _host.SendCommand("home recall");
                }
                break;

            case "Deed":
                if (trimtext.StartsWith("Currently stored"))
                {
                    _host.SendCommand("stow my deed register");
                    SetPhase("HomeStart");
                    _host.SendCommand("home recall");
                }
                else
                {
                    // Deed entries are "Page -- Deed name"; keep the text after "--".
                    int idx = trimtext.IndexOf("--", StringComparison.Ordinal);
                    string tap = idx >= 0 ? trimtext.Substring(idx + 3) : trimtext;
                    lock (_sync)
                        _lastItem = _currentData!.AddItem(new ItemData { Tap = tap, Storage = false });
                    TouchScan();
                }
                break;

            case "HomeStart":
                if (trimtext == "The home contains:")
                {
                    _host.Echo("Scanning Home.");
                    SetPhase("Home");
                    _currentData = NewSource("Home");
                    _level       = 1;
                }
                else if (trimtext.StartsWith("Your documentation filed with the Estate Holders"))
                {
                    _host.Echo("Skipping Home.");
                    AfterHome();
                }
                else if (trimtext == "You shouldn't do that while inside of a home.  Step outside if you need to check something.")
                {
                    _host.Echo("You cannot check the contents of your home while inside of a home. Step outside and try again.");
                    AfterHome();
                }
                break;

            case "Home":
                if (trimtext == ">")            // belt-and-braces; OnPrompt also catches this
                {
                    AfterHome();
                }
                else if (trimtext.StartsWith("Attached:"))   // attached to a piece of furniture
                {
                    string tap = trimtext.Replace("Attached: ", "");
                    lock (_sync)
                    {
                        var holder = _lastItem?.Parent ?? _lastItem;
                        _lastItem = holder != null
                            ? holder.AddItem(new ItemData { Tap = tap })
                            : _currentData!.AddItem(new ItemData { Tap = tap });
                    }
                    TouchScan();
                }
                else                                          // a piece of furniture
                {
                    int idx = trimtext.IndexOf(':');
                    string tap = idx >= 0 && idx + 2 <= trimtext.Length
                        ? trimtext.Substring(Math.Min(idx + 2, trimtext.Length)) : trimtext;
                    lock (_sync)
                        _lastItem = _currentData!.AddItem(new ItemData { Tap = tap, Storage = true });
                    TouchScan();
                }
                break;

            case "TraderStart":
                if (Regex.IsMatch(trimtext, @"^You get a.*storage book.*from") ||
                    trimtext == "You are already holding that.")
                {
                    _host.Echo("Scanning Trader Storage.");
                    _host.SendCommand("read my storage book");
                }
                else if (trimtext == "in the known realms since 402.")
                {
                    SetPhase("Trader");
                    _currentData = NewSource("TraderStorage");
                    _level       = 1;
                }
                else if (trimtext == "What were you referring to?" ||
                         trimtext == "The storage book is filled with complex lists of inventory that make little sense to you.")
                {
                    _host.Echo("Skipping Trader Storage.");
                    CompleteScan();
                }
                break;

            case "Trader":
                if (text.StartsWith("A notation at the bottom indicates"))
                    CompleteScan();
                else
                    AddItems(text, TraderLevel, rootStorage: true);
                break;
        }
    }

    /// <summary>After the Home phase (or a skip): Traders scan their storage book;
    /// everyone else finishes.</summary>
    private void AfterHome()
    {
        if (Global("guild") == "Trader")
        {
            SetPhase("TraderStart");
            _host.SendCommand("get my storage book");
        }
        else CompleteScan();
    }

    private void CompleteScan()
    {
        lock (_scanLifecycle) _scanMode = null;
        _scanCharacter = "";
        ScanStateChanged?.Invoke();
        _host.Echo("Scan Complete.");
        // Re-emit through the parse pipeline so scripts can
        // `waitforre ^InventoryView scan complete`.
        _host.InjectParsedLine("InventoryView scan complete");
        SaveSettings();
        CatalogChanged?.Invoke();
        if (OpenRequested is not null) OpenRequested("");
        else                           EchoSummary();
    }

    // ── Scan watchdog ──────────────────────────────────────────────────────────
    /// <summary>Move to <paramref name="phase"/> and restart the idle clock.
    /// <paramref name="extraSeconds"/> covers a phase that legitimately waits
    /// (the roundtime pause before the vault book).</summary>
    private void SetPhase(string? phase, int extraSeconds = 0)
    {
        _scanMode = phase;
        TouchScan(extraSeconds);
    }

    /// <summary>"The scan is still making progress" — push the abandon deadline
    /// out. Called on every phase change and every line actually catalogued, so a
    /// long vault list keeps the scan alive while pure chatter does not.</summary>
    private void TouchScan(int extraSeconds = 0) =>
        Interlocked.Exchange(ref _scanDeadlineTicks,
            (DateTime.UtcNow + ScanIdleTimeout + TimeSpan.FromSeconds(extraSeconds)).Ticks);

    /// <summary>One task per scan: sleep until the deadline, and if nothing has
    /// pushed it out by then, abandon the scan. Re-sleeps whenever
    /// <see cref="TouchScan"/> moved the deadline while it waited.</summary>
    private void StartWatchdog(int generation)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (_scanMode is { } phase &&
                       Volatile.Read(ref _scanGeneration) == generation)
                {
                    var remaining =
                        new DateTime(Interlocked.Read(ref _scanDeadlineTicks), DateTimeKind.Utc)
                        - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        AbandonScan(
                            $"InventoryView: scan gave up waiting for the game (stuck at \"{phase}\"). " +
                            "The catalog is unchanged — /iv scan to try again.");
                        return;
                    }
                    await Task.Delay(remaining, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }   // shutdown
        });
    }

    // ── Tree building ──────────────────────────────────────────────────────────
    private CharacterData NewSource(string source)
    {
        var data = new CharacterData { Name = Global("charactername"), Source = source };
        lock (_sync) _characterData.Add(data);
        return data;
    }

    private static string StripDash(string tap) => tap.StartsWith('-') ? tap.Substring(1) : tap;

    // ── Indent → level schemes (one per list format) ───────────────────────────
    /// <summary>"inventory list": 2 spaces = level 1, 5 spaces (+dash) = 2, 8 = 3, …</summary>
    internal static int InventoryLevel(int spaces) => Math.Max(1, (spaces + 1) / 3);
    /// <summary>Vault book: level 1 at ≤4 leading spaces, +1 per 2 spaces beyond.</summary>
    internal static int VaultLevel(int spaces) => 1 + (spaces > 4 ? (spaces - 4) / 2 : 0);
    /// <summary>Trader storage book: fixed-width 4/8/12 spaces = level 1/2/3.</summary>
    internal static int TraderLevel(int spaces) => spaces switch { 4 => 1, 8 => 2, 12 => 3, _ => 3 };

    /// <summary>Matches the indent run that precedes every list entry. Item taps
    /// are single-spaced, so a run of 2+ spaces can only be indentation.</summary>
    private static readonly Regex IndentRun = new(@" {2,}", RegexOptions.Compiled);

    /// <summary>Split one raw list line (merged or classic) into items and add
    /// each to the tree. Returns false when the line was not list output at all —
    /// the Inventory phase uses that to spot a roundtime it should not trust.</summary>
    private bool AddItems(string rawText, Func<int, int> levelFromSpaces, bool rootStorage)
    {
        // The item phases are otherwise "anything that isn't the terminator is an
        // item", so every main-stream line that arrives mid-scan — combat, arrivals,
        // room text — was being catalogued. Real list output always carries its
        // leading indent (that is what the level schemes read); game prose never
        // does. Anything unindented is somebody else's line, so drop it.
        if (!StartsIndented(rawText))
        {
            if (_debug) _host.Echo($"[IV dbg] skipped (not indented): {rawText.Trim()}");
            return false;
        }
        bool added = false;
        foreach (var (level, tap) in SplitMergedItems(rawText, levelFromSpaces))
        {
            if (_debug) _host.Echo($"[IV dbg] lvl={level}: {tap}");
            lock (_sync) AddAtLevel(level, tap, rootStorage);
            TouchScan();
            added = true;
        }
        return added;
    }

    /// <summary>Does this raw line open with the indent every list entry carries?
    /// Deliberately checked on the RAW text — <see cref="SplitMergedItems"/> keeps
    /// its trimmed-line fallback because <see cref="RepairMergedTaps"/> feeds it
    /// stored taps that have no indent left.</summary>
    private static bool StartsIndented(string rawText) =>
        rawText.Length >= 2 && rawText[0] == ' ' && rawText[1] == ' ';

    /// <summary>
    /// DR's current inventory output drops the newlines between items — the whole
    /// list arrives as ONE raw line — but every item still carries its old leading
    /// indent. Each run of 2+ spaces is therefore the indentation of the item that
    /// follows it; the text between runs is one item. A classic one-item-per-line
    /// stream contains a single leading run and degrades to exactly the old parse.
    /// The "[Use INVENTORY HELP for more options.]" hint DR appends to the same
    /// raw line is stripped. Dashes are indent decoration, not part of the tap.
    /// </summary>
    internal static List<(int Level, string Tap)> SplitMergedItems(string rawText, Func<int, int> levelFromSpaces)
    {
        var result = new List<(int, string)>();

        int hint = rawText.IndexOf("[Use INVENTORY HELP", StringComparison.Ordinal);
        if (hint >= 0) rawText = rawText.Substring(0, hint);

        var runs = IndentRun.Matches(rawText);
        if (runs.Count == 0)
        {
            // No indent at all (an upstream layer trimmed the line): flat item.
            var tap = StripDash(rawText.Trim());
            if (tap.Length > 0) result.Add((1, tap));
            return result;
        }

        // Anything before the first run had its indent trimmed away — level 1.
        if (runs[0].Index > 0)
        {
            var head = StripDash(rawText.Substring(0, runs[0].Index).Trim());
            if (head.Length > 0) result.Add((1, head));
        }

        for (int i = 0; i < runs.Count; i++)
        {
            int start = runs[i].Index + runs[i].Length;
            int end   = i + 1 < runs.Count ? runs[i + 1].Index : rawText.Length;
            var tap   = StripDash(rawText.Substring(start, end - start).Trim());
            if (tap.Length > 0)
                result.Add((levelFromSpaces(runs[i].Length), tap));
        }
        return result;
    }

    /// <summary>The shared tree-insert from the Genie 4 plugin: place a node
    /// relative to the previous one by comparing <paramref name="newlevel"/> to
    /// the current depth, walking up parents when we pop out of a container.
    /// Callers hold <see cref="_sync"/>.</summary>
    private void AddAtLevel(int newlevel, string tap, bool rootStorage)
    {
        if (_currentData is null) return;

        if (newlevel <= 1 || _lastItem is null)            // root item
        {
            _lastItem = _currentData.AddItem(new ItemData { Tap = tap, Storage = rootStorage });
            _level = 1;
            return;
        }

        if (newlevel == _level)                            // sibling of the previous item
        {
            _lastItem = _lastItem.Parent != null
                ? _lastItem.Parent.AddItem(new ItemData { Tap = tap })
                : _currentData.AddItem(new ItemData { Tap = tap, Storage = rootStorage });
        }
        else if (newlevel == _level + 1)                   // child of the previous item
        {
            _lastItem = _lastItem.AddItem(new ItemData { Tap = tap });
        }
        else                                               // popped up one or more levels
        {
            for (int i = newlevel; i <= _level && _lastItem?.Parent != null; i++)
                _lastItem = _lastItem.Parent;
            _lastItem = _lastItem != null
                ? _lastItem.AddItem(new ItemData { Tap = tap })
                : _currentData.AddItem(new ItemData { Tap = tap, Storage = rootStorage });
        }
        _level = newlevel;
    }

    private static int ParseRoundtime(string trimtext)
    {
        var m = Regex.Match(trimtext, @"^Roundtime:\s{1,3}(\d{1,3})\s{1,3}secs?\.$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var s) ? s : 1;
    }

    /// <summary>Non-blocking replacement for the Genie 4 <c>Thread.Sleep(rt*1000)</c>:
    /// schedules the follow-up command after the roundtime without stalling the
    /// game/parse loop. Cancelled on shutdown.</summary>
    private void PauseForRtThenSend(int seconds, string command)
    {
        _host.Echo($"Pausing {seconds} seconds for RT.");
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(Math.Max(0, seconds) * 1000, token); }
            catch (TaskCanceledException) { return; }
            if (!token.IsCancellationRequested) _host.SendCommand(command);
        });
    }

    // ── Console fallbacks (no window subscriber) ───────────────────────────────
    private void EchoSummary()
    {
        List<CharacterData> snap = SnapshotCatalog();
        if (snap.Count == 0)
        {
            _host.Echo("InventoryView: nothing scanned yet. Type /iv scan while connected.");
            return;
        }
        foreach (var character in snap.Select(c => c.Name).Distinct().OrderBy(n => n))
        {
            var sources = snap.Where(c => c.Name == character).ToList();
            _host.Echo($"{character}: {sources.Sum(s => CountItems(s.Items))} items (" +
                       string.Join(", ", sources.Select(s => $"{s.Source} {CountItems(s.Items)}")) + ")");
        }
        _host.Echo("InventoryView: open the Inventory View window (Window menu) to browse.");
    }

    private void EchoSearch(string term)
    {
        var matches = FindMatches(term);
        _host.Echo($"InventoryView: {matches.Count} match(es) for \"{term}\".");
        const int cap = 50;
        foreach (var path in matches.Take(cap))
            _host.Echo("  " + path);
        if (matches.Count > cap)
            _host.Echo($"  … and {matches.Count - cap} more (use the Inventory View window).");
    }

    /// <summary>Full "Character &gt; Source &gt; container &gt; … &gt; item" paths
    /// for every catalogued item containing <paramref name="term"/>.</summary>
    public List<string> FindMatches(string term)
    {
        var matches = new List<string>();
        foreach (var c in SnapshotCatalog())
            foreach (var item in c.Items)
                SearchItem(c.Name, c.Source, item, term, matches);
        return matches;
    }

    private static void SearchItem(string character, string source, ItemData item, string term, List<string> matches)
    {
        if (!string.IsNullOrEmpty(item.Tap) &&
            item.Tap.Contains(term, StringComparison.OrdinalIgnoreCase))
            matches.Add(FullPath(character, source, item));
        foreach (var child in item.Items)
            SearchItem(character, source, child, term, matches);
    }

    private static string FullPath(string character, string source, ItemData item)
    {
        var parts = new List<string> { item.Tap };
        for (var p = item.Parent; p != null; p = p.Parent)
            parts.Insert(0, p.Tap);
        parts.Insert(0, source);
        parts.Insert(0, character);
        return string.Join(" > ", parts);
    }

    private static int CountItems(List<ItemData> items)
    {
        int n = items.Count;
        foreach (var i in items) n += CountItems(i.Items);
        return n;
    }

    // ── CSV export ─────────────────────────────────────────────────────────────
    /// <summary>Export the whole catalog to CSV. Returns the file written, or
    /// null on failure (already echoed).</summary>
    public string? ExportCsv(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(DataDir(), "InventoryView_export.csv");
        try
        {
            using var sw = new StreamWriter(path, append: false);
            sw.WriteLine("Character,Source,Tap,Path");
            foreach (var c in SnapshotCatalog())
                foreach (var item in c.Items)
                    ExportItem(sw, c.Name, c.Source, item);
            _host.Echo($"InventoryView: export complete -> {path}");
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Echo("InventoryView export error: " + ex.Message);
            return null;
        }
    }

    private static void ExportItem(StreamWriter sw, string character, string source, ItemData item)
    {
        var pathParts = new List<string> { source };
        for (var p = item.Parent; p != null; p = p.Parent)
            pathParts.Insert(1, p.Tap);
        sw.WriteLine(string.Join(",",
            CleanCsv(character), CleanCsv(source), CleanCsv(item.Tap),
            CleanCsv(string.Join("\\", pathParts))));
        foreach (var child in item.Items)
            ExportItem(sw, character, source, child);
    }

    private static string CleanCsv(string data)
    {
        if (!data.Contains(',')) return data;
        if (!data.Contains('"')) return $"\"{data}\"";
        return $"\"{data.Replace("\"", "\"\"")}\"";
    }

    // ── Persistence (XLinq in the Genie 4 XmlSerializer shape) ─────────────────
    private string DataDir()  => _host.DataRoot;
    private string ConfigFile => Path.Combine(DataDir(), "InventoryView.xml");

    private void LoadSettings(bool initial = false)
    {
        var file = ConfigFile;
        if (!File.Exists(file)) return;
        try
        {
            // A zero-byte file is a torn write from an interrupted save — there is
            // no data to recover, so treat it exactly like a first run.
            if (new FileInfo(file).Length == 0) return;

            var root = XDocument.Load(file).Root;
            var data = (root?.Elements("CharacterData") ?? Enumerable.Empty<XElement>())
                .Select(ReadCharacter).ToList();
            lock (_sync) _characterData = data;

            // Scans saved before the merged-line fix stored whole lists as one
            // giant tap — re-split those into proper subtrees and persist.
            int repaired = RepairMergedTaps();
            if (repaired > 0)
            {
                SaveSettings();
                _host.Echo($"InventoryView: repaired {repaired} merged entr{(repaired == 1 ? "y" : "ies")} from an older scan.");
            }
            if (initial && (data.Count > 0 || repaired > 0))
                CatalogChanged?.Invoke();
        }
        catch (Exception ex)   // IOException + XML parse errors
        {
            // Corrupt but non-empty: keep the bytes for inspection, start fresh.
            try { File.Copy(file, file + ".bad", overwrite: true); } catch { /* best effort */ }
            _host.Echo("InventoryView: saved data was unreadable (" + ex.Message +
                       "); starting with an empty catalog. The old file was kept as InventoryView.xml.bad.");
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(DataDir());
            XElement root;
            lock (_sync)
            {
                root = new XElement("ArrayOfCharacterData",
                    _characterData.Select(c => new XElement("CharacterData",
                        new XElement("name", c.Name),
                        new XElement("source", c.Source),
                        new XElement("items", c.Items.Select(ItemToXml)))));
            }
            // Atomic save: write the whole document to a temp file first, then
            // move it over the real one — a crash mid-write can only lose the .tmp.
            var file = ConfigFile;
            var tmp  = file + ".tmp";
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(tmp);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            _host.Echo("Error writing to InventoryView file: " + ex.Message);
        }
    }

    private static XElement ItemToXml(ItemData item) =>
        new("ItemData",
            new XElement("storage", item.Storage ? "true" : "false"),
            new XElement("tap", item.Tap),
            new XElement("items", item.Items.Select(ItemToXml)));

    private static CharacterData ReadCharacter(XElement el)
    {
        var c = new CharacterData
        {
            Name   = (string?)el.Element("name")   ?? "",
            Source = (string?)el.Element("source") ?? "",
        };
        foreach (var item in el.Element("items")?.Elements("ItemData") ?? Enumerable.Empty<XElement>())
            c.Items.Add(ReadItem(item, null));
        return c;
    }

    private static ItemData ReadItem(XElement el, ItemData? parent)
    {
        var item = new ItemData
        {
            Storage = (bool?)el.Element("storage") ?? false,
            Tap     = (string?)el.Element("tap")   ?? "",
            Parent  = parent,
        };
        foreach (var child in el.Element("items")?.Elements("ItemData") ?? Enumerable.Empty<XElement>())
            item.Items.Add(ReadItem(child, item));
        return item;
    }

    // ── Repair of pre-fix saved data ───────────────────────────────────────────
    /// <summary>Scans saved before the merged-line fix stored an entire merged
    /// list line as one root item whose tap still contains the indent runs.
    /// Re-split each such root into the subtree the scan would produce today.
    /// Only the indent-scheme sources are eligible — Deed and Home entries never
    /// carried indentation, so a multi-space tap there is item text, not damage.</summary>
    private int RepairMergedTaps()
    {
        int repaired = 0;
        lock (_sync)
        {
            foreach (var c in _characterData)
            {
                Func<int, int>? levelFn = c.Source switch
                {
                    "Inventory"     => InventoryLevel,
                    "Vault"         => VaultLevel,
                    "TraderStorage" => TraderLevel,
                    _               => null,
                };
                if (levelFn is null) continue;

                for (int i = 0; i < c.Items.Count; i++)
                {
                    var item = c.Items[i];
                    if (item.Items.Count > 0 || !IndentRun.IsMatch(item.Tap)) continue;
                    var split = SplitMergedItems(item.Tap, levelFn);
                    if (split.Count <= 1) continue;

                    var subtree = BuildSubtree(split, item.Storage);
                    c.Items.RemoveAt(i);
                    c.Items.InsertRange(i, subtree);
                    i += subtree.Count - 1;
                    repaired++;
                }
            }
        }
        return repaired;
    }

    /// <summary>Runs the standard <see cref="AddAtLevel"/> walk against a detached
    /// root list (the live scan state is saved and restored), so repaired data is
    /// shaped by the exact same code path as a fresh scan. Caller holds
    /// <see cref="_sync"/>.</summary>
    private List<ItemData> BuildSubtree(List<(int Level, string Tap)> split, bool rootStorage)
    {
        var (savedData, savedLast, savedLevel) = (_currentData, _lastItem, _level);
        var temp = new CharacterData();
        (_currentData, _lastItem, _level) = (temp, null, 1);
        foreach (var (level, tap) in split)
            AddAtLevel(level, tap, rootStorage);
        (_currentData, _lastItem, _level) = (savedData, savedLast, savedLevel);
        return temp.Items;
    }
}
