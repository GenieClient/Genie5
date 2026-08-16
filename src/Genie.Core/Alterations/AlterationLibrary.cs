using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Genie.Core.Alterations;

/// <summary>
/// The player's saved alteration designs, and their on-disk store.
///
/// Alteration Buddy kept these in <c>alterations.csv</c> next to the plugin DLL:
/// headerless, tab-delimited, one design per line, no escaping of any kind. G5
/// stores <c>alterations.json</c> in the Config directory instead — the format
/// survives multi-line Look text and gains Title/Notes — while
/// <see cref="ImportGenie4File"/> / <see cref="ExportGenie4File"/> keep the old
/// file readable and writable for players moving either direction.
///
/// This type is deliberately UI-free and synchronous: the library is a handful of
/// short strings, saved on explicit user action, never on the hot path.
/// </summary>
public sealed class AlterationLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Same rationale as PersistenceService: these files are hand-edited and
        // shared, so keep apostrophes and punctuation literal rather than \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<AlterationDesign> _designs = new();

    public IReadOnlyList<AlterationDesign> Designs => _designs;

    public int Count => _designs.Count;

    public void Add(AlterationDesign design) => _designs.Add(design);

    public void Insert(int index, AlterationDesign design) =>
        _designs.Insert(Math.Clamp(index, 0, _designs.Count), design);

    public bool Remove(AlterationDesign design) => _designs.Remove(design);

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < _designs.Count) _designs.RemoveAt(index);
    }

    public void Replace(int index, AlterationDesign design)
    {
        if (index >= 0 && index < _designs.Count) _designs[index] = design;
    }

    public void Clear() => _designs.Clear();

    public void ReplaceAll(IEnumerable<AlterationDesign> designs)
    {
        _designs.Clear();
        _designs.AddRange(designs);
    }

    // ── JSON store (G5 native) ──────────────────────────────────────────────

    /// <summary>
    /// Load from <paramref name="path"/>, replacing the current contents. A
    /// missing file is not an error (first run); a corrupt one throws, so the
    /// caller can report it rather than silently presenting an empty library and
    /// then overwriting the user's designs on the next save.
    /// </summary>
    public void Load(string path)
    {
        if (!File.Exists(path)) { _designs.Clear(); return; }

        var parsed = JsonSerializer.Deserialize<List<AlterationDesign>>(File.ReadAllText(path))
                     ?? new List<AlterationDesign>();
        ReplaceAll(parsed);
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(_designs, JsonOptions));
    }

    // ── Genie 4 / Alteration Buddy interop ──────────────────────────────────

    /// <summary>
    /// Read an Alteration Buddy <c>alterations.csv</c>.
    ///
    /// The upstream writer was <c>StreamWriter.WriteLine(design.ToString())</c>
    /// over a tab-join with no quoting, so a design whose Look field contained a
    /// newline (the Look box was multiline, so this was easy to do) was written
    /// across several physical lines and could not be read back correctly by the
    /// plugin itself. We recover what is recoverable: a physical line with no tab
    /// at all is treated as a continuation of the previous design's Look field
    /// rather than as a new, mangled design. Lines that are entirely blank are
    /// skipped.
    /// </summary>
    public static List<AlterationDesign> ImportGenie4File(string path)
    {
        var designs = new List<AlterationDesign>();
        if (!File.Exists(path)) return designs;

        foreach (var raw in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (!raw.Contains('\t') && designs.Count > 0)
            {
                var previous = designs[^1];
                previous.Look = string.IsNullOrEmpty(previous.Look)
                    ? raw
                    : previous.Look + Environment.NewLine + raw;
                continue;
            }

            designs.Add(AlterationDesign.FromGenie4Line(raw));
        }

        return designs;
    }

    /// <summary>Append an imported set to this library. Returns how many were
    /// added, after dropping designs that are entirely blank.</summary>
    public int ImportGenie4Into(string path)
    {
        var imported = ImportGenie4File(path).Where(d => !d.IsEmpty).ToList();
        _designs.AddRange(imported);
        return imported.Count;
    }

    /// <summary>Write the library back out in Alteration Buddy's format. Title and
    /// Notes are not representable there and are dropped — see
    /// <see cref="AlterationDesign.ToGenie4Line"/>.</summary>
    public void ExportGenie4File(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var d in _designs) sb.AppendLine(d.ToGenie4Line());
        File.WriteAllText(path, sb.ToString());
    }
}
