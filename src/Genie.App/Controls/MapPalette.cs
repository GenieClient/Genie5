using Avalonia.Media;
using Genie.Core.Presets;

namespace Genie.App.Controls;

/// <summary>
/// The map's colours, read from Genie 4's <c>automapper.*</c> presets (public
/// #406). Genie 4's AutoMapper (MapForm.cs) draws everything on the current
/// floor in each preset's colour and everything on another floor in the same
/// preset's background:
/// <list type="table">
///   <item><c>automapper.panel</c>: label text / map background</item>
///   <item><c>automapper.line</c>: compass lines and room borders</item>
///   <item><c>automapper.linego</c>: go / up / down / out lines</item>
///   <item><c>automapper.lineclimb</c>: climb lines</item>
///   <item><c>automapper.linestump</c>: exit stubs (neighbour not drawn)</item>
///   <item><c>automapper.node</c>: rooms with no colour of their own</item>
///   <item><c>automapper.path</c>: walk destination / rooms on the walk path</item>
/// </list>
/// A colour that is empty, "Default" or unreadable falls back to the stock
/// (Genie 4) value for that slot, so a half-edited preset never blanks the map.
/// </summary>
public sealed record MapPalette(
    Color Background, Color Text,
    Color Line,       Color LineOtherFloor,
    Color LineGo,     Color LineGoOtherFloor,
    Color LineClimb,  Color LineClimbOtherFloor,
    Color LineStump,  Color LineStumpOtherFloor,
    Color Node,       Color NodeOtherFloor,
    Color Path,       Color PathOtherFloor)
{
    public const string PanelPreset     = "automapper.panel";
    public const string LinePreset      = "automapper.line";
    public const string LineGoPreset    = "automapper.linego";
    public const string LineClimbPreset = "automapper.lineclimb";
    public const string LineStumpPreset = "automapper.linestump";
    public const string NodePreset      = "automapper.node";
    public const string PathPreset      = "automapper.path";

    /// <summary>Every map preset id.</summary>
    public static readonly IReadOnlyList<string> All =
        [PanelPreset, LinePreset, LineGoPreset, LineClimbPreset, LineStumpPreset, NodePreset, PathPreset];

    private static readonly PresetEngine StockEngine = new();

    /// <summary>The stock presets' palette (Genie 4's AutoMapper colours).</summary>
    public static MapPalette Stock { get; } = From(StockEngine.GetForeground, StockEngine.GetBackground);

    /// <summary>Build the palette from preset colour lookups (foreground,
    /// background by preset id).</summary>
    public static MapPalette From(Func<string, string> foreground, Func<string, string> background)
    {
        Color Fg(string id) => Parse(foreground(id)) ?? Parse(StockEngine.GetForeground(id)) ?? Colors.Black;
        Color Bg(string id) => Parse(background(id)) ?? Parse(StockEngine.GetBackground(id)) ?? Colors.White;

        return new MapPalette(
            Background: Bg(PanelPreset),      Text: Fg(PanelPreset),
            Line:       Fg(LinePreset),       LineOtherFloor:      Bg(LinePreset),
            LineGo:     Fg(LineGoPreset),     LineGoOtherFloor:    Bg(LineGoPreset),
            LineClimb:  Fg(LineClimbPreset),  LineClimbOtherFloor: Bg(LineClimbPreset),
            LineStump:  Fg(LineStumpPreset),  LineStumpOtherFloor: Bg(LineStumpPreset),
            Node:       Fg(NodePreset),       NodeOtherFloor:      Bg(NodePreset),
            Path:       Fg(PathPreset),       PathOtherFloor:      Bg(PathPreset));
    }

    /// <summary>A preset colour string ("Black", "#EEE8AA") as a colour, or null
    /// for empty / "Default" / unreadable.</summary>
    public static Color? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Default", StringComparison.OrdinalIgnoreCase)) return null;
        return Color.TryParse(value.Trim(), out var c) ? c : null;
    }

    /// <summary>A colour as the "#RRGGBB" a preset stores.</summary>
    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
