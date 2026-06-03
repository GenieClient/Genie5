using System.Text.Json;

namespace Genie.App.Settings;

/// <summary>
/// One windowed-mode (MDI) child window's saved geometry, keyed by the
/// dockable's Id. Coordinates are in Dock's MDI coordinate space (the same
/// values Dock writes to <c>IMdiDocument.MdiBounds</c>).
/// </summary>
public sealed record MdiWindowBounds(
    double X, double Y, double Width, double Height, string State);

/// <summary>
/// JSON load/save for per-window MDI geometry (<c>mdi-layout.json</c>). Mirrors
/// the rest of the Settings stores: best-effort, never throws into the caller —
/// a missing/corrupt file just yields an empty map so windowed mode falls back
/// to Dock's default cascade positions.
/// </summary>
public static class MdiLayoutStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Dictionary<string, MdiWindowBounds> Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, MdiWindowBounds>>(
                File.ReadAllText(path), Json) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(string path, IReadOnlyDictionary<string, MdiWindowBounds> bounds)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(bounds, Json));
        }
        catch { /* best-effort — geometry persistence is a convenience, not critical */ }
    }
}
