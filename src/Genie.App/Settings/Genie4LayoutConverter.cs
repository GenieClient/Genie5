using System.Globalization;
using System.Xml.Linq;

namespace Genie.App.Settings;

/// <summary>
/// Genie 4 <c>Config/Layout/*.layout</c> → Genie 5 <see cref="SavedLayout"/> (public #319).
///
/// <para>Genie 4 has no docking — every window floats at its own position inside the
/// main form — so the faithful target is a <b>windowed-mode (MDI)</b> layout, the same
/// shape as the Heirloom built-in: each open window becomes an entry in
/// <see cref="SavedLayout.MdiBounds"/> at its Genie 4 position and size. Users can dock
/// and re-save afterwards.</para>
///
/// <para>Converted: main-window geometry (or maximized), each recognised window's
/// position, size and open/closed state, the script bar's top/bottom placement and the
/// status bar. Not converted: per-window fonts, colours, timestamps and <c>IfClosed</c>
/// routing — those live in Genie 5's per-window settings, not in a layout. A window id
/// Genie 5 has no panel for (a custom <c>#echo &gt;name</c> window, or one of the
/// Genie 4 windows with no Genie 5 counterpart) is skipped and reported.</para>
/// </summary>
public static class Genie4LayoutConverter
{
    /// <summary>Genie 4 window <c>ID</c> → Genie 5 dock tool id.</summary>
    private static readonly Dictionary<string, string> ToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main"]         = "game-text",
        ["game"]         = "game-text",
        ["log"]          = "log",
        ["talk"]         = "talk",
        ["whispers"]     = "whispers",
        ["thoughts"]     = "thoughts",
        ["ooc"]          = "ooc",
        ["familiar"]     = "familiar",
        ["experience"]   = "experience",
        ["percwindow"]   = "active-spells",
        ["logons"]       = "logons",
        ["death"]        = "death",
        ["room"]         = "room",
        ["objects"]      = "objects",
        ["mobs"]         = "mobs",
        ["players"]      = "players",
        ["inv"]          = "backpack",
        ["assess"]       = "assess",
        ["atmospherics"] = "atmospherics",
        ["combat"]       = "combat",
        ["raw"]          = "raw-xml",
        ["itemlog"]      = "itemlog",
    };

    /// <summary>Every Genie 5 tool id the converter can produce (tests pin these
    /// against the dock factory's panel table).</summary>
    public static IReadOnlyCollection<string> MappedToolIds =>
        ToolIds.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What a conversion kept and dropped, for the import summary.</summary>
    public sealed record Report(int Converted, int Closed, IReadOnlyList<string> Skipped);

    /// <summary>Convert one <c>.layout</c> file's XML. Returns null (with an empty
    /// report) when the text is not a Genie 4 layout at all.</summary>
    public static (SavedLayout? Layout, Report Report) Convert(string xml, string name)
    {
        XElement root;
        try { root = XDocument.Parse(xml).Root!; }
        catch { return (null, new Report(0, 0, Array.Empty<string>())); }

        var windows = root.Element("Windows");
        if (root.Name.LocalName != "Genie" || windows is null)
            return (null, new Report(0, 0, Array.Empty<string>()));

        var layout = new SavedLayout
        {
            Name         = name,
            Description  = "Imported from a Genie 4 layout. Windowed (MDI) mode, as Genie 4 had no docking — dock the panels and re-save to make it a docked layout.",
            WindowedMode = true,
            DockTree     = null,
            MdiBounds    = new Dictionary<string, MdiWindowBounds>(StringComparer.OrdinalIgnoreCase),
        };

        if (windows.Element("Main") is { } main)
        {
            layout.WindowMaximized = Bool(main, "Maximized") ?? false;
            if (Num(main, "Width") is > 0 and var w && Num(main, "Height") is > 0 and var h)
            {
                layout.WindowWidth  = w;
                layout.WindowHeight = h;
                layout.WindowX      = (int)(Num(main, "Left") ?? 0);
                layout.WindowY      = (int)(Num(main, "Top")  ?? 0);
                layout.HasWindowGeometry = true;
            }
            else if (layout.WindowMaximized)
                layout.HasWindowGeometry = true;   // maximized carries no rect — that's fine
        }

        int converted = 0, closed = 0;
        var skipped = new List<string>();
        foreach (var el in windows.Elements())
        {
            var id = (string?)el.Attribute("ID");
            if (string.IsNullOrWhiteSpace(id)) continue;   // Main, fonts
            if (!ToolIds.TryGetValue(id, out var toolId))
            {
                skipped.Add((string?)el.Attribute("Name") is { Length: > 0 } n ? n : id);
                continue;
            }
            // The Game window carries no Visible attribute: it is always open.
            if (!(Bool(el, "Visible") ?? true)) { closed++; continue; }
            if (layout.MdiBounds.ContainsKey(toolId)) continue;   // first wins (main/game alias)

            double width  = Num(el, "Width")  is > 0 and var ww ? ww : 300;
            double height = Num(el, "Height") is > 0 and var hh ? hh : 200;
            layout.MdiBounds[toolId] = new MdiWindowBounds(
                Math.Max(0, Num(el, "Left") ?? 0), Math.Max(0, Num(el, "Top") ?? 0),
                width, height, "Normal");
            layout.VisibleTools.Add(toolId);
            converted++;
        }

        // Bars sit beside <Windows> in the file.
        if (root.Element("ScriptBar") is { } bar && (string?)bar.Attribute("Dock") is { } dock)
            layout.ScriptBarAtBottom = !dock.Equals("Top", StringComparison.OrdinalIgnoreCase);
        if (root.Element("StatusBar") is { } status && Bool(status, "Visible") is { } showStatus)
            layout.ShowStatusBar = showStatus;

        return (layout, new Report(converted, closed, skipped));
    }

    /// <summary>The <c>.layout</c> files a Genie 4 Config folder holds: its
    /// <c>Layout/</c> subfolder (where Genie 4 saves them) plus any at the top
    /// level (the shipped <c>default.layout</c> sits there).</summary>
    public static IReadOnlyList<string> FindLayoutFiles(string configDir)
    {
        var found = new List<string>();
        foreach (var dir in new[] { configDir, Path.Combine(configDir, "Layout") })
        {
            try
            {
                if (Directory.Exists(dir))
                    found.AddRange(Directory.GetFiles(dir, "*.layout").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            }
            catch { /* unreadable folder — nothing to import from it */ }
        }
        return found;
    }

    /// <summary>The Genie 5 name an imported file gets — prefixed so it can't
    /// collide with (or silently replace) a built-in or a native layout.</summary>
    public static string ImportedName(string path) => "G4 " + Path.GetFileNameWithoutExtension(path);

    private static double? Num(XElement el, string attr) =>
        double.TryParse((string?)el.Attribute(attr), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
        && double.IsFinite(v) ? v : null;

    private static bool? Bool(XElement el, string attr) =>
        bool.TryParse((string?)el.Attribute(attr), out var b) ? b : null;
}
