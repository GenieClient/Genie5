using System.IO.Compression;
using System.Xml.Linq;

namespace Genie.Core.Extensions.Builtin.InventoryView;

/// <summary>One player-shop listing from DR Service Plaza.</summary>
public sealed record ShopListing(
    string Tap,
    string Look,
    string Worn,
    string Price,
    string Surface,
    string Shop,
    string Owner,
    string Room,
    string City);

/// <summary>
/// Searches DragonRealms player-shop inventories using DR Service Plaza's
/// public export (<c>drservice.info/Plaza/Export.asmx/Latest</c> — the same
/// "Download Current Items to Excel" the site offers). The export is one
/// xlsx (~650 KB, ~6k listings); it's cached on disk and refreshed at most
/// once per <see cref="CacheTtl"/> so Genie is a polite consumer of a
/// volunteer-run service. Parsing is plain ZipArchive + XLinq over the
/// sheet — no spreadsheet library involved.
/// </summary>
public sealed class PlazaShopService
{
    private const string ExportUrl = "https://drservice.info/Plaza/Export.asmx/Latest";
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private readonly string _cacheFile;
    private readonly System.Threading.Lock _sync = new();
    private List<ShopListing>? _listings;

    /// <summary>Overridable download seam for tests: → raw xlsx bytes (null on
    /// failure). Default GETs the Plaza export.</summary>
    internal Func<Task<byte[]?>> Download;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public PlazaShopService(string dataRoot)
    {
        _cacheFile = Path.Combine(dataRoot, "InventoryView.plazashops.xlsx");
        Download = DefaultDownload;
    }

    /// <summary>When the current in-memory/disk data was fetched (null = never).</summary>
    public DateTime? DataTimestampUtc
    {
        get
        {
            try { return File.Exists(_cacheFile) ? File.GetLastWriteTimeUtc(_cacheFile) : null; }
            catch { return null; }
        }
    }

    /// <summary>Case-insensitive substring search over tap + look. Ensures the
    /// dataset is loaded (disk cache first; network when stale/absent). Returns
    /// null when no data could be obtained at all (offline, first run).</summary>
    public async Task<IReadOnlyList<ShopListing>?> SearchAsync(string term, int cap = 100)
    {
        var listings = await EnsureDataAsync().ConfigureAwait(false);
        if (listings is null) return null;
        if (string.IsNullOrWhiteSpace(term)) return Array.Empty<ShopListing>();

        term = term.Trim();
        var results = new List<ShopListing>();
        foreach (var l in listings)
        {
            if (l.Tap.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                l.Look.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(l);
                if (results.Count >= cap) break;
            }
        }
        return results;
    }

    private async Task<List<ShopListing>?> EnsureDataAsync()
    {
        lock (_sync)
        {
            if (_listings is not null &&
                DataTimestampUtc is { } ts && DateTime.UtcNow - ts < CacheTtl)
                return _listings;
        }

        // Fresh-enough disk cache?
        if (DataTimestampUtc is { } fileTs && DateTime.UtcNow - fileTs < CacheTtl)
        {
            var fromDisk = TryParseFile(_cacheFile);
            if (fromDisk is not null)
                return SetListings(fromDisk);
        }

        var bytes = await Download().ConfigureAwait(false);
        if (bytes is not null)
        {
            try
            {
                var parsed = ParseXlsx(new MemoryStream(bytes));
                try
                {
                    var tmp = _cacheFile + ".tmp";
                    File.WriteAllBytes(tmp, bytes);
                    File.Move(tmp, _cacheFile, overwrite: true);
                }
                catch { /* cache write is best-effort */ }
                return SetListings(parsed);
            }
            catch { /* fall through to any stale disk copy */ }
        }

        // Offline / bad download: any disk copy (even stale) beats nothing.
        var stale = TryParseFile(_cacheFile);
        return stale is not null ? SetListings(stale) : null;
    }

    private List<ShopListing> SetListings(List<ShopListing> listings)
    {
        lock (_sync) _listings = listings;
        return listings;
    }

    private static List<ShopListing>? TryParseFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return ParseXlsx(fs);
        }
        catch { return null; }
    }

    // ── Minimal xlsx reader (shared strings + first worksheet) ──────────────────
    /// <summary>Parses the Plaza export workbook. Column layout (header row):
    /// Tap, Look, Worn, Read, Price, Plats, KValue, Surface, Shop Name, Owner,
    /// Open, Close, Room, City, ePediaName, ScanDate — resolved BY HEADER NAME,
    /// so column reordering upstream doesn't silently shift fields.</summary>
    internal static List<ShopListing> ParseXlsx(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var shared = new List<string>();
        var sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry is not null)
        {
            using var s = sstEntry.Open();
            var doc = XDocument.Load(s);
            XNamespace ns = doc.Root!.Name.Namespace;
            foreach (var si in doc.Root.Elements(ns + "si"))
                shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => (string)t)));
        }

        var sheetEntry = zip.Entries.FirstOrDefault(e =>
                             e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) &&
                             e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                         ?? throw new InvalidDataException("no worksheet in workbook");

        using var sheetStream = sheetEntry.Open();
        var sheet = XDocument.Load(sheetStream);
        XNamespace sn = sheet.Root!.Name.Namespace;

        var rows = sheet.Descendants(sn + "row").ToList();
        if (rows.Count < 2) return new List<ShopListing>();

        // Header → column-letter map.
        var colByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in rows[0].Elements(sn + "c"))
        {
            var col = ColumnLetters((string?)c.Attribute("r") ?? "");
            var val = CellValue(c, sn, shared);
            if (col.Length > 0 && val.Length > 0) colByName[val] = col;
        }

        string Cell(Dictionary<string, string> cells, string header) =>
            colByName.TryGetValue(header, out var col) && cells.TryGetValue(col, out var v) ? v : "";

        var listings = new List<ShopListing>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in row.Elements(sn + "c"))
                cells[ColumnLetters((string?)c.Attribute("r") ?? "")] = CellValue(c, sn, shared);

            var tap = Cell(cells, "Tap");
            if (tap.Length == 0) continue;
            listings.Add(new ShopListing(
                Tap:     tap,
                Look:    Cell(cells, "Look"),
                Worn:    Cell(cells, "Worn"),
                Price:   Cell(cells, "Price"),
                Surface: Cell(cells, "Surface"),
                Shop:    Cell(cells, "Shop Name"),
                Owner:   Cell(cells, "Owner"),
                Room:    Cell(cells, "Room"),
                City:    Cell(cells, "City")));
        }
        return listings;
    }

    private static string ColumnLetters(string cellRef)
    {
        int i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;
        return cellRef[..i];
    }

    private static string CellValue(XElement cell, XNamespace ns, List<string> shared)
    {
        var v = cell.Element(ns + "v");
        if (v is null)
            return cell.Element(ns + "is")?.Value ?? "";   // inline string
        var raw = (string)v;
        if ((string?)cell.Attribute("t") == "s" &&
            int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count)
            return shared[idx];
        return raw;
    }

    private static async Task<byte[]?> DefaultDownload()
    {
        try
        {
            return await Http.GetByteArrayAsync(ExportUrl).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
