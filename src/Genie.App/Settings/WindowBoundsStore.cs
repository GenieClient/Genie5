using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genie.App.Settings;

/// <summary>
/// Main-window geometry persisted across restarts. Position/size in DIPs;
/// <see cref="Maximized"/> wins over size on restore (we just re-maximize).
/// </summary>
public sealed record WindowBounds(double Width, double Height, int X, int Y, bool Maximized);

/// <summary>
/// JSON load/save for the main window's size/position (<c>window.json</c>).
/// Best-effort — a missing/corrupt file just means "use the default size".
/// </summary>
public static class WindowBoundsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static WindowBounds? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(path), Json); }
        catch { return null; }
    }

    public static void Save(string path, WindowBounds bounds)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(bounds, Json));
        }
        catch { /* best-effort */ }
    }
}
