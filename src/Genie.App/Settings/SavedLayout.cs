using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genie.App.Settings;

/// <summary>
/// A user-named snapshot of the App's layout-related state: which dock
/// tools are visible, where the hands strip sits, RT badge position,
/// per-tag visibility filters, main-window size, etc.
///
/// <para>
/// This is the "Workspace presets" feature — Genie 4 parity. Users
/// rearrange their windows for hunting vs crafting vs roleplay and
/// switch between named layouts via the Layout menu. Each saved
/// layout lives at <c>{AppData}/Genie5/Layouts/{Name}.json</c>.
/// </para>
///
/// <para>
/// We intentionally don't serialise the full Dock.Avalonia tree.
/// Tool identity comes from the factory (string IDs); we capture
/// visibility + a handful of cross-cutting display flags and let
/// the factory rebuild the tree on apply. The trade-off is that
/// fine-grained adjustments (splitter positions within a tab group)
/// don't round-trip — those need real dock serialisation, which
/// can land in a later iteration.
/// </para>
/// </summary>
public sealed class SavedLayout
{
    /// <summary>User-given name. Doubles as filename (sanitised on save).</summary>
    public string Name { get; set; } = "Untitled";

    /// <summary>Optional one-liner from the Save As dialog.</summary>
    public string Description { get; set; } = "";

    /// <summary>ISO timestamp of when this layout was saved.</summary>
    public string SavedAt { get; set; } = DateTimeOffset.Now.ToString("O");

    // ── Window geometry ────────────────────────────────────────────────

    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    /// <summary>Main-window position (DIPs). Only meaningful when
    /// <see cref="HasWindowGeometry"/> is true.</summary>
    public int  WindowX { get; set; }
    public int  WindowY { get; set; }

    /// <summary>Whether the main window was maximized when the layout was saved.
    /// Wins over size on restore (we just re-maximize).</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>True once a layout has actually captured the main-window
    /// geometry. Layouts saved before window geometry rode on the profile leave
    /// this false, so applying them leaves the current window size untouched
    /// instead of snapping to the 1280×800 defaults at (0,0).</summary>
    public bool HasWindowGeometry { get; set; }

    // ── Dock-tool visibility ───────────────────────────────────────────

    /// <summary>String IDs of every tool that should be visible in the
    /// dock. Matches <see cref="Docking.GenieDockFactory"/> tool IDs:
    /// "vitals", "room", "backpack", "mapper", "logons", "talk",
    /// "whispers", "thoughts", "combat", etc.
    /// <para>Retained for backward-compat + as the fallback when
    /// <see cref="DockTree"/> is absent (layouts saved before full-tree
    /// serialisation landed).</para></summary>
    public List<string> VisibleTools { get; set; } = new();

    /// <summary>Full dock-tree snapshot — container structure, proportions,
    /// alignments, and active tabs. When present, this is the authoritative
    /// source for restoring the layout (it round-trips the *arrangement*, not
    /// just which tools are visible). Null for layouts saved before this
    /// feature; those fall back to <see cref="VisibleTools"/>.</summary>
    public Docking.DockNodeSnapshot? DockTree { get; set; }

    /// <summary>Whether this layout was saved in windowed (MDI) document mode.
    /// On load, the app switches to that mode before rebuilding — so a layout
    /// saved in windowed mode reopens windowed, not tabbed.</summary>
    public bool WindowedMode { get; set; }

    /// <summary>Per-window MDI geometry (position/size/state), keyed by panel
    /// id. Only populated for <see cref="WindowedMode"/> layouts; restores each
    /// floating window where it was when the layout was saved.</summary>
    public Dictionary<string, MdiWindowBounds>? MdiBounds { get; set; }

    // ── Cross-cutting display flags ────────────────────────────────────

    public bool   HandsStripVisible    { get; set; } = true;
    public bool   HandsStripAtBottom   { get; set; } = true;
    public bool   ShowStatusBar        { get; set; } = true;
    public bool   RoundTimeOnHandsStrip{ get; set; } = false;

    /// <summary>Per-tag visibility filters (Window → Game Window).</summary>
    public bool   ShowGameText         { get; set; } = true;
    public bool   ShowEchoText         { get; set; } = true;
    public bool   ShowScriptText       { get; set; } = true;

    /// <summary>Map canvas background hex — kept here so themed layouts
    /// (light mode in town, dark for hunting) round-trip the mapper
    /// background too.</summary>
    public string MapBackgroundHex     { get; set; } = "#1A1A1A";

    // ── (De)serialisation helpers ──────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Dock leaves a ProportionalDock's Proportion at double.NaN to mean
        // "auto / equal share". The DockTree snapshot captures that verbatim,
        // and System.Text.Json rejects NaN/Infinity unless told otherwise —
        // which crashed "Save Layout" (the snapshot legitimately contains NaN).
        // Allow the named literals so NaN round-trips and auto-sized docks stay
        // auto-sized; used for both serialize and deserialize (shared options).
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static SavedLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<SavedLayout>(json, JsonOpts); }
        catch { return null; }
    }
}
