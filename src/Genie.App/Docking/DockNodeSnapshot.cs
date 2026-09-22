namespace Genie.App.Docking;

/// <summary>
/// Serializable snapshot of one node in the Dock.Avalonia layout tree.
/// Captures the container structure (proportional splits, tool/document
/// docks, splitters) plus the proportions and active-tab selections, so a
/// saved layout can round-trip the *arrangement* — not just which tools are
/// visible.
///
/// <para>Leaf tools/documents are stored by <see cref="Id"/> only (Kind
/// "leaf"). On rebuild the live instance is pulled from the factory's tool
/// registry, so each panel keeps its already-wired view-model rather than
/// being reconstructed. This is why we don't need the heavier
/// <c>Dock.Serializer</c> package + context relocation.</para>
/// </summary>
public sealed class DockNodeSnapshot
{
    /// <summary>"proportional" | "tooldock" | "documentdock" | "splitter" | "leaf".</summary>
    public string Kind { get; set; } = "";

    /// <summary>Dock/dockable id. For leaves this is the registry key used to
    /// resolve the live instance.</summary>
    public string? Id { get; set; }

    /// <summary>"Horizontal" | "Vertical" — proportional docks only.</summary>
    public string? Orientation { get; set; }

    /// <summary>"Left" | "Right" | "Top" | "Bottom" | "Unset" — tool/document docks only.</summary>
    public string? Alignment { get; set; }

    /// <summary>Split proportion (0..1) or NaN for auto.</summary>
    public double Proportion { get; set; } = double.NaN;

    /// <summary>Id of the active child dockable (selected tab), if any.</summary>
    public string? ActiveId { get; set; }

    /// <summary>Display title — only persisted for plugin-window leaves
    /// (<see cref="Id"/> starting <c>pluginwin:</c>). Lets a saved layout
    /// recreate the panel with its caption before the plugin has run again,
    /// so the tab isn't blank on restore. Null for built-in tools (their title
    /// comes from the factory / WindowSettings).</summary>
    public string? Title { get; set; }

    public List<DockNodeSnapshot> Children { get; set; } = new();
}

/// <summary>
/// Serializable snapshot of one floating window attached to the dock root —
/// the tool ids it hosts plus its screen geometry. Floats live in
/// <c>IRootDock.Windows</c>, OUTSIDE the docked tree that
/// <see cref="DockNodeSnapshot"/> captures, so without this a saved layout
/// silently dropped every floated panel (field report, 2026-07-16).
/// Restore refloats each id at the saved geometry; a multi-tab float is
/// restored as one window per tool (cascaded), a deliberate simplification.
/// </summary>
public sealed class FloatingWindowSnapshot
{
    /// <summary>Registry ids of the tools hosted in this floating window.</summary>
    public List<string> ToolIds { get; set; } = new();

    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// One tool's remembered float placement — where its window sat the last time
/// it floated, and whether floating was that tool's most recent placement.
///
/// <para>This is the persistable shape of <c>GenieDockFactory</c>'s in-memory
/// float caches (public #359). Geometry uses the same units the rest of the
/// docking code does: position in physical pixels, size in DIPs. NaN means
/// "no geometry recorded" — a tool can be floated-last without usable bounds
/// if its window was maximized at every close.</para>
/// </summary>
public sealed record FloatMemoryEntry(
    double X,
    double Y,
    double Width,
    double Height,
    bool   FloatedLast);
