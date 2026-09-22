using System.Text.Json;
using System.Text.Json.Serialization;
using Genie.App.Docking;

namespace Genie.App.Settings;

/// <summary>
/// Disk store for the dock's float memory — which panels the user last had
/// floating, and the screen geometry of each float. One JSON file at
/// <c>{Config}/float-memory.json</c>.
///
/// <para>Why this isn't part of <see cref="SavedLayout"/>: a saved layout is a
/// deliberate, named arrangement the user asks for by name. This is the
/// opposite — an implicit "put it back where I had it" cache, updated by
/// ordinary use and never surfaced in the Layout menu. Keeping them apart also
/// keeps the precedence rule intact: a layout's
/// <see cref="FloatingWindowSnapshot"/> is applied after this cache and so
/// still wins (see <c>GenieDockFactory.ImportFloatMemory</c>).</para>
///
/// <para>Note this deliberately does NOT re-open panels at launch. It only
/// answers "if the user opens this panel, should it come back as a window, and
/// where?" — which panels are open still comes from the layout, as before.</para>
/// </summary>
public static class FloatMemoryStore
{
    /// <summary>Filename under the Config dir.</summary>
    public const string FileName = "float-memory.json";

    /// <summary>What actually goes in the file. A wrapper object rather than a
    /// bare dictionary so the format has room to grow a version field or a
    /// second map without breaking older readers.</summary>
    private sealed class Document
    {
        public Dictionary<string, FloatMemoryEntry> Tools { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Geometry is NaN when a tool is floated-last with no usable bounds
        // (every close happened while maximized). Same reason SavedLayout needs
        // this: System.Text.Json rejects NaN unless told otherwise.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Read the float memory. Returns an empty map for a missing, unreadable or
    /// malformed file — this is a convenience cache, so a bad file costs the
    /// user their remembered positions and nothing else.
    /// </summary>
    public static IReadOnlyDictionary<string, FloatMemoryEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, FloatMemoryEntry>();
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOpts);
            return doc?.Tools is { Count: > 0 } tools
                ? new Dictionary<string, FloatMemoryEntry>(tools, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, FloatMemoryEntry>();
        }
        catch
        {
            return new Dictionary<string, FloatMemoryEntry>();
        }
    }

    /// <summary>
    /// Write the float memory, creating the Config dir if needed. Swallows IO
    /// failures: this runs off a debounce timer and on app close, and neither is
    /// a place to surface a dialog over a cache file.
    /// </summary>
    public static void Save(string path, IReadOnlyDictionary<string, FloatMemoryEntry> memory)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var doc = new Document
            {
                Tools = new Dictionary<string, FloatMemoryEntry>(memory, StringComparer.OrdinalIgnoreCase),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch
        {
            // Best effort.
        }
    }
}
