using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Docking;

public class GenieDockFactory : Factory
{
    private readonly MainWindowViewModel _vm;

    /// <summary>
    /// Map of dockable-id → (instance, its default parent dock id). Populated
    /// by <see cref="CreateLayout"/> so the Window menu can show/hide both
    /// Tools and Documents by id while remembering where to put them back.
    /// The parent is stored as an <b>id</b>, not a reference, so it survives a
    /// <see cref="BuildLayout"/> tree rebuild — the rebuilt docks keep their
    /// ids, so a live lookup always finds the right parent.
    /// </summary>
    private readonly Dictionary<string, (IDockable Dockable, string ParentId)> _tools =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How to recreate a structural ToolDock if it was pruned. Dock.Avalonia
    /// collapses (removes) an empty ToolDock when its last child is closed via
    /// the tab X — after which <see cref="SetToolVisibility"/> can no longer
    /// find the parent to re-open the tool into. This records each home dock's
    /// grandparent + placement so we can rebuild it in its original spot.
    /// Grandparents (left-col / center-col / root-layout) are IsCollapsable=false
    /// and therefore always present, so a single level of rebuild is enough.
    /// </summary>
    private readonly record struct DockHome(
        string GrandparentId, Alignment Alignment, double Proportion, bool AtFront);

    private readonly Dictionary<string, DockHome> _dockHomes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The root returned by <see cref="CreateLayout"/>. Stored so we can walk
    /// the whole layout tree (including floating windows) when answering
    /// visibility queries — reference-equality on stored Dockable instances is
    /// unreliable because Dock.Avalonia may reinstantiate during layout init.
    /// </summary>
    private IRootDock? _root;

    /// <summary>The live MDI <see cref="DocumentDock"/> while in windowed mode
    /// (null in tabbed mode). Its <c>VisibleDockables</c> is the set of windows
    /// currently OPEN — used to persist/restore which windows the user closed.</summary>
    private DocumentDock? _mdiDock;

    /// <summary>
    /// "Last known location" for every registered tool. Captures enough
    /// state to fully reconstruct the tool's home, even when its parent
    /// ToolDock was pruned by Dock.Avalonia after the panel was closed:
    ///
    /// <list type="bullet">
    ///   <item><c>ParentId</c> — the parent dock the tool lived in.</item>
    ///   <item><c>Index</c> — position within that parent's
    ///         <c>VisibleDockables</c>.</item>
    ///   <item><c>GrandparentId</c> — the parent's parent dock; needed when
    ///         the parent itself was pruned and must be rebuilt.</item>
    ///   <item><c>ParentAlignment</c> + <c>ParentProportion</c> — enough to
    ///         re-instantiate the pruned ToolDock with the right placement.
    ///         Null when the parent isn't an IToolDock.</item>
    ///   <item><c>IndexInGrandparent</c> — where to splice the rebuilt
    ///         parent back into the grandparent's children.</item>
    /// </list>
    /// </summary>
    private readonly record struct LastKnownLocation(
        string         ParentId,
        int            Index,
        string?        GrandparentId,
        Alignment?     ParentAlignment,
        double         ParentProportion,
        int            IndexInGrandparent,
        string?        AnchorSiblingId,        // a non-splitter sibling of the parent
                                               // dock — used as a survivor anchor
                                               // when the grandparent itself gets
                                               // collapsed by Dock.Avalonia
        Orientation?   GrandparentOrientation); // the orientation the dissolved
                                                // grandparent had (Vertical for
                                                // center-col) — used by the
                                                // wrap-restore path to recreate
                                                // it around the anchor at the
                                                // right axis

    /// <summary>
    /// "Last known location" for every registered tool. Used by
    /// <see cref="SetToolVisibility"/> as the preferred restore target — gives
    /// users back EXACTLY where they had the panel before close, including
    /// custom positions from drag-redocking.
    ///
    /// <para>Populated three ways:</para>
    /// <list type="number">
    ///   <item>On every layout build (default + snapshot) via
    ///         <see cref="CaptureAllPositions"/>.</item>
    ///   <item>Just before a menu-driven close in
    ///         <see cref="SetToolVisibility"/>'s hide branch.</item>
    ///   <item>On every <c>DockableAdded</c> event so a re-shown tool's
    ///         new position immediately replaces its stale entry.</item>
    /// </list>
    /// </summary>
    private readonly Dictionary<string, LastKnownLocation> _lastKnownPositions =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Plugin-created windows ───────────────────────────────────────────────
    // Plugins surface their own panels by writing to a named window
    // (IPluginHost.SetWindow / EchoToWindow). We key each by a canonical
    // "pluginwin:<lowercased-name>" id and keep the VM alive for the whole
    // session — even if the user closes the tab — so content keeps accumulating
    // and re-opening shows the latest. The tools dict mirrors it for re-adding
    // to the live tree after a layout rebuild.
    public const string PluginWindowPrefix = "pluginwin:";

    private readonly Dictionary<string, PluginWindowViewModel> _pluginWindowVms =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginWindowTool> _pluginWindowTools =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ToolDock plugin windows dock into (beside Backpack/Experience
    /// in the right column). Matches the <c>backpack-dock</c> id built in
    /// <see cref="CreateLayout"/>.</summary>
    private const string PluginWindowParentId = "backpack-dock";

    /// <summary>Canonical dock id for a plugin window of the given display name.</summary>
    public static string PluginWindowId(string name) =>
        PluginWindowPrefix + (name ?? "").Trim().ToLowerInvariant();

    /// <summary>True if an id is a plugin-window id (vs a built-in tool).</summary>
    public static bool IsPluginWindowId(string? id) =>
        id is not null && id.StartsWith(PluginWindowPrefix, StringComparison.OrdinalIgnoreCase);

    public GenieDockFactory(MainWindowViewModel vm)
    {
        _vm = vm;
        WireLastKnownPositionTracking();
    }

    /// <summary>
    /// Build the main Game document, picking the renderer once. With
    /// <c>#config useeditorgamewindow off</c> (the default) this is the ordinary
    /// <see cref="GameTextDocument"/> and nothing about the shipped path changes;
    /// with it on, the <see cref="EditorGameTextDocument"/> subtype selects the
    /// AvaloniaEdit template instead. Read once here rather than made
    /// live-switchable — swapping renderers under a populated scrollback isn't
    /// worth the complexity, and a restart is a fair price for an experiment.
    /// </summary>
    private GameTextDocument NewGameText(Genie.Core.Layout.WindowSettingsStore ws)
    {
        var settings = ws.Get("game-text");
        return _vm.UseEditorGameWindow
            ? new EditorGameTextDocument(_vm.GameText, settings)
            : new GameTextDocument(_vm.GameText, settings);
    }

    public override IRootDock CreateLayout()
    {
        // Wire the host-window locator. Dock.Avalonia's FloatDockable silently
        // no-ops when this is null, which is why "Pop out to window" didn't
        // produce a window in the first build — the factory was building a
        // dock-window stub but had no way to host it as an OS-level Window.
        // Keyed by nameof(IDockWindow) per the Dock 11.x convention.
        //
        // The Background + TransparencyLevelHint settings are required: by
        // default HostWindow has a transparent fill and an acrylic transparency
        // hint, so on Windows the floated panel sits on top of whatever's
        // behind it on the desktop instead of looking like a proper opaque
        // window. Forcing an opaque dark fill matches the main window.
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () =>
            {
                var w = new GenieHostWindow
                {
                    Background           = new SolidColorBrush(Color.FromRgb(0x1f, 0x1f, 0x1f)),
                    TransparencyLevelHint = new[] { WindowTransparencyLevel.None },
                };
                return w;
            }
        };

        // Hand each Tool its WindowSettings entry so Layout-tab edits repaint
        // live (font, foreground, background, title).
        var ws       = _vm.WindowSettings;
        var gameText = NewGameText(ws);
        var vitals   = new VitalsTool      (_vm.Vitals,           ws.Get("vitals"));
        var room     = new RoomTool        (_vm.Room,             ws.Get("room"));
        var backpack = new BackpackTool    (_vm.Inventory,        ws.Get("backpack"));
        var mapper   = new MapperTool      (_vm.Mapper,           ws.Get("mapper"));
        var logons   = new StreamTool      (_vm.StreamTabs.Logons,   ws.Get("logons"));
        var talk     = new StreamTool      (_vm.StreamTabs.Talk,     ws.Get("talk"));
        var whispers = new StreamTool      (_vm.StreamTabs.Whispers, ws.Get("whispers"));
        var thoughts = new StreamTool      (_vm.StreamTabs.Thoughts, ws.Get("thoughts"));
        var combat   = new StreamTool      (_vm.StreamTabs.Combat,   ws.Get("combat"));
        var familiar = new StreamTool      (_vm.StreamTabs.Familiar, ws.Get("familiar"));
        var death    = new StreamTool      (_vm.StreamTabs.Death,    ws.Get("death"));
        var assess   = new StreamTool      (_vm.StreamTabs.Assess,   ws.Get("assess"));
        var atmospherics = new StreamTool  (_vm.StreamTabs.Atmospherics, ws.Get("atmospherics"));
        var log      = new StreamTool      (_vm.StreamTabs.Log,      ws.Get("log"));
        var itemlog  = new StreamTool      (_vm.StreamTabs.ItemLog,  ws.Get("itemlog"));
        var experience = new ExperienceTool(_vm.Experience,          ws.Get("experience"));
        var analytics  = new AnalyticsTool (_vm.Analytics,           ws.Get("analytics"));
        var activeSpells = new ActiveSpellsTool(_vm.ActiveSpells,    ws.Get("active-spells"));
        var timeTracker = new TimeTrackerTool(_vm.TimeTracker,       ws.Get("time-tracker"));
        var inventoryView = new InventoryViewTool(_vm.InventoryView, ws.Get("inventory-view"));
        var scripts    = new ScriptsTool   (_vm.Scripts,            ws.Get("scripts"));
        var scene      = new SceneTool     (_vm.Scene,              ws.Get("scene"));
        var mobs       = new MobsTool      (_vm.Mobs,               ws.Get("mobs"));
        var players    = new PlayersTool   (_vm.Players,            ws.Get("players"));
        var rawXml     = new RawXmlTool    (_vm.RawXml,             ws.Get("raw-xml"));
        var injuries   = new InjuriesTool  (_vm.Injuries,           ws.Get("injuries"));

        // ── Default ship layout — three vertical columns ─────────────────
        //   ┌──────────┬─────────────────────┬──────────┐
        //   │ Room     │ Game                │ Backpack │
        //   │          ├─────────────────────┤          │
        //   │ Streams  │ Mapper              │          │
        //   └──────────┴─────────────────────┴──────────┘
        //   Vitals stays in the registry but is OUT of the default-visible
        //   set — duplicates the bottom Status Bar. Users can re-open via
        //   Window → Vitals. Same pattern for any other panel.

        var documentDock = new DocumentDock
        {
            Id               = "docs",
            IsCollapsable    = false,
            VisibleDockables = CreateList<IDockable>(gameText),
            ActiveDockable   = gameText
        };

        // ── Center column: Game (top) + Mapper (bottom) ─────────────────
        var mapperDock = new ToolDock
        {
            Id               = "mapper-dock",
            Alignment        = Alignment.Bottom,
            Proportion       = 0.40,
            VisibleDockables = CreateList<IDockable>(mapper),
            ActiveDockable   = mapper
        };

        var centerCol = new ProportionalDock
        {
            Id               = "center-col",
            Orientation      = Orientation.Vertical,
            Proportion       = 0.56,   // 1.0 - leftCol(0.22) - backpackDock(0.22).
                                       // Without this, Dock.Avalonia treats centerCol as
                                       // "fill the remainder" but the resulting Grid* on
                                       // the column lacks a concrete star-weight, which
                                       // (a) leaves the horizontal splitters between
                                       // leftCol↔centerCol and centerCol↔backpackDock
                                       // un-draggable (no redistribution math to run), and
                                       // (b) makes Maximize→Restore redistribute the
                                       // recovered pixels unevenly because there's nothing
                                       // anchoring the centre column's share.
            IsCollapsable    = false,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                mapperDock
            )
        };

        // ── Left column: Room (top) + stream tabs (bottom) ──────────────
        var roomDock = new ToolDock
        {
            Id               = "room-dock",
            Alignment        = Alignment.Left,
            Proportion       = 0.35,
            VisibleDockables = CreateList<IDockable>(room),
            ActiveDockable   = room
        };

        var streamDock = new ToolDock
        {
            Id               = "streams",
            Alignment        = Alignment.Bottom,
            Proportion       = 0.65,
            VisibleDockables = CreateList<IDockable>(logons, talk, whispers, thoughts, combat, familiar, death, assess, log, itemlog),
            ActiveDockable   = combat   // matches screenshot default — Combat tab active
        };

        var leftCol = new ProportionalDock
        {
            Id               = "left-col",
            Orientation      = Orientation.Vertical,
            Proportion       = 0.22,
            IsCollapsable    = false,
            VisibleDockables = CreateList<IDockable>(
                roomDock,
                new ProportionalDockSplitter(),
                streamDock
            )
        };

        // ── Right column: Backpack (full height) ────────────────────────
        var backpackDock = new ToolDock
        {
            Id               = "backpack-dock",
            Alignment        = Alignment.Right,
            Proportion       = 0.22,
            VisibleDockables = CreateList<IDockable>(backpack),
            ActiveDockable   = backpack
        };

        // ── Root: three columns side-by-side ────────────────────────────
        var rootLayout = new ProportionalDock
        {
            Id               = "root-layout",
            Orientation      = Orientation.Horizontal,
            IsCollapsable    = false,
            VisibleDockables = CreateList<IDockable>(
                leftCol,
                new ProportionalDockSplitter(),
                centerCol,
                new ProportionalDockSplitter(),
                backpackDock
            )
        };

        var root = CreateRootDock();
        root.Id               = "root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList<IDockable>(rootLayout);
        root.ActiveDockable   = rootLayout;
        root.DefaultDockable  = rootLayout;
        _root                 = root;

        // ── Dockable registry for Window-menu visibility toggles ──────────
        // Includes the Game document so the user can re-open it from the menu
        // after clicking the X on its tab.
        // Registry maps each tool to its canonical parent dock — used by
        // SetToolVisibility to re-open a hidden tool in its natural spot.
        // After the layout rebuild the parents differ from the pre-rebuild
        // single sideDock: Room → roomDock, Backpack → backpackDock, etc.
        _tools.Clear();
        _tools[gameText.Id] = (gameText, documentDock.Id);
        // Vitals + Experience are hidden by default and re-open as tabs in the
        // right column beside the Backpack. (They previously pointed at a
        // never-tree-attached "side" dock, so their toggles silently no-op'd.)
        _tools[vitals.Id]   = (vitals,   backpackDock.Id);
        _tools[room.Id]     = (room,     roomDock.Id);
        _tools[backpack.Id] = (backpack, backpackDock.Id);
        _tools[mapper.Id]   = (mapper,   mapperDock.Id);
        _tools[logons.Id]   = (logons,   streamDock.Id);
        _tools[talk.Id]     = (talk,     streamDock.Id);
        _tools[whispers.Id] = (whispers, streamDock.Id);
        _tools[thoughts.Id] = (thoughts, streamDock.Id);
        _tools[combat.Id]   = (combat,   streamDock.Id);
        _tools[familiar.Id] = (familiar, streamDock.Id);
        _tools[death.Id]    = (death,    streamDock.Id);
        _tools[assess.Id]   = (assess,   streamDock.Id);
        // Atmospherics: ambient/weather feed — hidden by default (opt-in, #85);
        // re-open via Window → Atmospherics. Homes in the stream dock.
        _tools[atmospherics.Id] = (atmospherics, streamDock.Id);
        _tools[log.Id]      = (log,      streamDock.Id);
        _tools[itemlog.Id]  = (itemlog,  streamDock.Id);
        // Experience: registered but hidden by default (like Vitals) — re-opens
        // beside the Backpack via Window → Experience. The plugin fills it.
        _tools[experience.Id] = (experience, backpackDock.Id);
        // Analytics: skill-history charts — hidden by default, re-opens beside
        // the Backpack via Window → Analytics (grouped with Experience).
        _tools[analytics.Id] = (analytics, backpackDock.Id);
        // Active Spells: registered but hidden by default (like Experience) —
        // re-opens beside the Backpack via Window → Active Spells. The Spell
        // Timer fills it. First-class tool (not a plugin window) so it keeps MDI
        // decorations and never re-opens itself on a server push (#112).
        _tools[activeSpells.Id] = (activeSpells, backpackDock.Id);
        // Time Tracker: registered but hidden by default — re-opens beside the
        // Backpack via Window → Time Tracker. The builtin tracker fills it.
        // First-class tool (not a plugin window) for the same reasons as
        // Active Spells: top-level menu entry, no self-re-open on repaint.
        _tools[timeTracker.Id] = (timeTracker, backpackDock.Id);
        // Inventory View: registered but hidden by default — re-opens beside the
        // Backpack via Window → Inventory View, /iv open, or a scan completing.
        // First-class tool for the same reasons as Active Spells / Time Tracker.
        _tools[inventoryView.Id] = (inventoryView, backpackDock.Id);
        // Scripts: registered but hidden by default (like Vitals/Experience) —
        // re-opens beside the Backpack via Window → Scripts.
        _tools[scripts.Id]    = (scripts,    backpackDock.Id);
        // Portrait (id "scene"): room/scene artwork — also hidden by default
        // (re-open via Window → Portrait). Lives beside the Backpack when shown.
        _tools[scene.Id]      = (scene,      backpackDock.Id);
        // Mobs / Players: room-occupant lists — hidden by default (re-open via
        // Window → Mobs / Players). Home beside the Room panel they complement.
        _tools[mobs.Id]       = (mobs,       roomDock.Id);
        _tools[players.Id]    = (players,    roomDock.Id);
        // Raw XML (#14): dev/debug protocol dump — hidden by default, re-opens
        // beside the Backpack via Window → Raw XML (grouped with Scripts/Scene).
        _tools[rawXml.Id]     = (rawXml,     backpackDock.Id);
        // Injuries (#18): body-silhouette wound/scar display — hidden by
        // default, re-opens beside the Backpack (with Vitals it complements).
        _tools[injuries.Id]   = (injuries,   backpackDock.Id);

        // ── Home-dock recreation map ─────────────────────────────────────
        // Mirrors the proportions/alignments set on the ToolDocks above so a
        // home dock that Dock pruned on close can be rebuilt where it lived.
        // Values MUST match the dock definitions above. (The Game DocumentDock
        // is IsCollapsable=false, so it never needs rebuilding.)
        _dockHomes.Clear();
        _dockHomes[roomDock.Id]     = new(leftCol.Id,    Alignment.Left,   0.35, AtFront: true);
        _dockHomes[streamDock.Id]   = new(leftCol.Id,    Alignment.Bottom, 0.65, AtFront: false);
        _dockHomes[mapperDock.Id]   = new(centerCol.Id,  Alignment.Bottom, 0.40, AtFront: false);
        _dockHomes[backpackDock.Id] = new(rootLayout.Id, Alignment.Right,  0.22, AtFront: false);

        // Re-register any plugin windows created earlier this session so their
        // Window-menu toggles still resolve after a default-layout rebuild
        // (Reset / legacy load). They're registered (not force-shown) — the user
        // re-opens via Window → Plugin Windows, same as Vitals/Experience.
        foreach (var (id, tool) in _pluginWindowTools)
            _tools[id] = (tool, backpackDock.Id);

        AttachWindowMenus();
        return root;
    }

    /// <summary>
    /// Windowed (MDI) layout — every panel is a free-floating child window
    /// inside a single MDI <see cref="DocumentDock"/> (Genie 4 "windowed
    /// mode"). Requires Dock 11.3.9+ (<see cref="DocumentLayoutMode.Mdi"/>).
    /// Panel instances and their DataTemplates are identical to
    /// <see cref="CreateLayout"/>; only the container differs, so the Window
    /// menu, per-window settings, and float/visibility plumbing all carry over.
    /// </summary>
    /// <param name="visibleIds">Panel ids to open as windows. Null = the
    /// default set. When restoring a saved layout this is the set that was open
    /// when it was saved, so windows the user had closed stay closed.</param>
    public IRootDock CreateMdiLayout(IReadOnlyCollection<string>? visibleIds = null)
    {
        // Host-window locator is harmless in MDI mode (children are in-window,
        // not OS windows) but a panel can still be floated out to a real
        // window, so keep it wired exactly as the tabbed path does.
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new GenieHostWindow
            {
                Background            = new SolidColorBrush(Color.FromRgb(0x1f, 0x1f, 0x1f)),
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None },
            }
        };

        var ws         = _vm.WindowSettings;
        var gameText   = NewGameText(ws);
        var vitals     = new VitalsTool      (_vm.Vitals,             ws.Get("vitals"));
        var room       = new RoomTool        (_vm.Room,               ws.Get("room"));
        var backpack   = new BackpackTool    (_vm.Inventory,          ws.Get("backpack"));
        var mapper     = new MapperTool      (_vm.Mapper,             ws.Get("mapper"));
        var logons     = new StreamTool      (_vm.StreamTabs.Logons,   ws.Get("logons"));
        var talk       = new StreamTool      (_vm.StreamTabs.Talk,     ws.Get("talk"));
        var whispers   = new StreamTool      (_vm.StreamTabs.Whispers, ws.Get("whispers"));
        var thoughts   = new StreamTool      (_vm.StreamTabs.Thoughts, ws.Get("thoughts"));
        var combat     = new StreamTool      (_vm.StreamTabs.Combat,   ws.Get("combat"));
        var familiar   = new StreamTool      (_vm.StreamTabs.Familiar, ws.Get("familiar"));
        var death      = new StreamTool      (_vm.StreamTabs.Death,    ws.Get("death"));
        var assess     = new StreamTool      (_vm.StreamTabs.Assess,   ws.Get("assess"));
        var atmospherics = new StreamTool    (_vm.StreamTabs.Atmospherics, ws.Get("atmospherics"));
        var log        = new StreamTool      (_vm.StreamTabs.Log,      ws.Get("log"));
        var itemlog    = new StreamTool      (_vm.StreamTabs.ItemLog,  ws.Get("itemlog"));
        var experience = new ExperienceTool  (_vm.Experience,          ws.Get("experience"));
        var analytics  = new AnalyticsTool   (_vm.Analytics,           ws.Get("analytics"));
        var activeSpells = new ActiveSpellsTool(_vm.ActiveSpells,       ws.Get("active-spells"));
        var timeTracker = new TimeTrackerTool (_vm.TimeTracker,        ws.Get("time-tracker"));
        var inventoryView = new InventoryViewTool(_vm.InventoryView,   ws.Get("inventory-view"));
        var scene      = new SceneTool        (_vm.Scene,              ws.Get("scene"));
        var mobs       = new MobsTool         (_vm.Mobs,               ws.Get("mobs"));
        var players    = new PlayersTool      (_vm.Players,            ws.Get("players"));
        var rawXml     = new RawXmlTool       (_vm.RawXml,             ws.Get("raw-xml"));
        var injuries   = new InjuriesTool     (_vm.Injuries,           ws.Get("injuries"));

        // Every MDI panel in canonical order, paired with its id.
        var panels = new (string Id, IDockable Dockable)[]
        {
            ("game-text", gameText), ("room", room), ("mapper", mapper), ("backpack", backpack),
            ("logons", logons), ("talk", talk), ("whispers", whispers), ("thoughts", thoughts),
            ("combat", combat), ("familiar", familiar), ("death", death), ("assess", assess),
            ("atmospherics", atmospherics), ("log", log), ("itemlog", itemlog),
            ("vitals", vitals), ("experience", experience), ("analytics", analytics),
            ("active-spells", activeSpells),
            ("time-tracker", timeTracker), ("inventory-view", inventoryView), ("scene", scene),
            ("mobs", mobs), ("players", players), ("raw-xml", rawXml),
            ("injuries", injuries),
        };

        // Which panels open as windows. Default mirrors the tabbed layout
        // (Vitals + Experience stay registered-but-hidden).
        var defaultVisible = new[]
        {
            "game-text", "room", "mapper", "backpack",
            "logons", "talk", "whispers", "thoughts", "combat",
            "familiar", "death", "assess", "log", "itemlog",
        };
        var show = new HashSet<string>(
            visibleIds is { Count: > 0 } ? visibleIds : defaultVisible,
            StringComparer.OrdinalIgnoreCase);

        var visibleDockables = panels.Where(p => show.Contains(p.Id))
                                     .Select(p => p.Dockable).ToArray();
        if (visibleDockables.Length == 0)        // never leave the MDI dock empty
            visibleDockables = new IDockable[] { gameText };
        var active = visibleDockables.FirstOrDefault(
                         d => string.Equals(d.Id, "game-text", StringComparison.OrdinalIgnoreCase))
                     ?? visibleDockables[0];

        var mdiDock = new DocumentDock
        {
            Id                = "mdi",
            Title             = "Windows",
            IsCollapsable     = false,
            CanCreateDocument = false,
            LayoutMode        = DocumentLayoutMode.Mdi,
            VisibleDockables  = CreateList<IDockable>(visibleDockables),
            ActiveDockable    = active,
        };
        _mdiDock = mdiDock;

        var rootLayout = new ProportionalDock
        {
            Id               = "root-layout",
            Orientation      = Orientation.Horizontal,
            IsCollapsable    = false,
            VisibleDockables = CreateList<IDockable>(mdiDock),
        };

        var root = CreateRootDock();
        root.Id               = "root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList<IDockable>(rootLayout);
        root.ActiveDockable   = rootLayout;
        root.DefaultDockable  = rootLayout;
        _root                 = root;

        // Registry for Window-menu visibility toggles — ALL panels are
        // registered (so a closed/hidden one can be re-opened from the menu),
        // even though only the `show` set starts visible. No nested home docks
        // in MDI mode.
        _tools.Clear();
        foreach (var p in panels)
            _tools[p.Id] = (p.Dockable, mdiDock.Id);
        _dockHomes.Clear();

        foreach (var (id, tool) in _pluginWindowTools)
            _tools[id] = (tool, mdiDock.Id);

        AttachWindowMenus();
        return root;
    }

    /// <summary>Build + initialise the MDI layout, ready to assign to the
    /// DockControl. MDI counterpart of <see cref="BuildDefaultLayout"/>.
    /// <paramref name="savedBounds"/> (if any) restores each child window's
    /// last position/size/state.</summary>
    public IRootDock BuildMdiLayout(
        IReadOnlyDictionary<string, Settings.MdiWindowBounds>? savedBounds = null)
    {
        // The saved geometry keys are exactly the windows that were open, so
        // restore opens only those (closed windows stay closed).
        var visibleIds = savedBounds is { Count: > 0 } ? savedBounds.Keys.ToList() : null;
        var root = CreateMdiLayout(visibleIds);
        InitLayout(root);
        if (savedBounds is { Count: > 0 }) ApplyMdiBounds(savedBounds);
        CaptureAllPositions();
        return root;
    }

    // ── Windowed-mode (MDI) per-window geometry ────────────────────────────
    // Every dockable in Dock.Model.Mvvm (Tool AND Document) implements
    // IMdiDocument, so each floating panel carries its own MdiBounds/MdiState.

    /// <summary>Read the current MDI geometry of every panel that has a
    /// non-empty rect. Called before leaving windowed mode and on app close
    /// so positions survive restarts.</summary>
    public Dictionary<string, Settings.MdiWindowBounds> CaptureMdiBounds()
    {
        var result = new Dictionary<string, Settings.MdiWindowBounds>();
        // Iterate the MDI dock's live VisibleDockables — i.e. only the windows
        // currently OPEN. A panel the user closed is no longer here, so it
        // isn't recorded and won't be re-opened on restore. (Iterating _tools
        // would also capture closed panels' stale bounds.)
        if (_mdiDock?.VisibleDockables is not { } open) return result;
        foreach (var dockable in open)
        {
            if (dockable is Dock.Model.Controls.IMdiDocument mdi &&
                dockable.Id is { Length: > 0 } id)
            {
                var r = mdi.MdiBounds;
                // Only capture real, finite rects — a window that was never
                // realised (or is minimised) can report NaN/Infinity bounds,
                // which would crash the layout's JSON write.
                if (double.IsFinite(r.X) && double.IsFinite(r.Y) &&
                    double.IsFinite(r.Width) && double.IsFinite(r.Height) &&
                    r.Width > 0 && r.Height > 0)
                    result[id] = new Settings.MdiWindowBounds(
                        r.X, r.Y, r.Width, r.Height, mdi.MdiState.ToString());
            }
        }
        return result;
    }

    /// <summary>Restore saved MDI geometry onto the freshly built panels.</summary>
    public void ApplyMdiBounds(IReadOnlyDictionary<string, Settings.MdiWindowBounds> bounds)
    {
        foreach (var (_, (dockable, _)) in _tools)
        {
            if (dockable is Dock.Model.Controls.IMdiDocument mdi &&
                dockable.Id is { Length: > 0 } id &&
                bounds.TryGetValue(id, out var b))
            {
                mdi.MdiBounds = new Dock.Model.Core.DockRect(b.X, b.Y, b.Width, b.Height);
                if (Enum.TryParse<Dock.Model.Core.MdiWindowState>(b.State, out var st))
                    mdi.MdiState = st;
            }
        }
    }

    // ── Layout snapshot (full-tree round-trip) ─────────────────────────────

    /// <summary>
    /// Capture the live dock tree (proportions, alignments, active tabs,
    /// container structure) into a serializable snapshot. Leaf tools/documents
    /// are recorded by id; their view-models stay attached to the live
    /// instances, which <see cref="BuildLayout"/> re-uses. Returns null if the
    /// layout hasn't been created yet.
    /// </summary>
    public DockNodeSnapshot? CaptureLayout()
    {
        // root.VisibleDockables[0] is the top-level "root-layout" ProportionalDock.
        if (_root?.VisibleDockables is not { Count: > 0 } top) return null;
        return Capture(top[0]);
    }

    private static DockNodeSnapshot? Capture(IDockable node) => node switch
    {
        ProportionalDockSplitter => new DockNodeSnapshot { Kind = "splitter" },

        ProportionalDock pd => new DockNodeSnapshot
        {
            Kind        = "proportional",
            Id          = pd.Id,
            Orientation = pd.Orientation.ToString(),
            Proportion  = pd.Proportion,
            Children    = CaptureChildren(pd.VisibleDockables),
        },

        DocumentDock dd => new DockNodeSnapshot
        {
            Kind       = "documentdock",
            Id         = dd.Id,
            Proportion = dd.Proportion,
            ActiveId   = dd.ActiveDockable?.Id,
            Children   = CaptureChildren(dd.VisibleDockables),
        },

        ToolDock td => new DockNodeSnapshot
        {
            Kind       = "tooldock",
            Id         = td.Id,
            Alignment  = td.Alignment.ToString(),
            Proportion = td.Proportion,
            ActiveId   = td.ActiveDockable?.Id,
            Children   = CaptureChildren(td.VisibleDockables),
        },

        // Any other dockable is a leaf tool/document — store by id. For plugin
        // windows also persist the Title so the panel restores with its caption
        // before the plugin runs again.
        { } leaf => new DockNodeSnapshot
        {
            Kind  = "leaf",
            Id    = leaf.Id,
            Title = IsPluginWindowId(leaf.Id) ? leaf.Title : null,
        },
    };

    private static List<DockNodeSnapshot> CaptureChildren(IList<IDockable>? children)
    {
        var list = new List<DockNodeSnapshot>();
        if (children is null) return list;
        foreach (var c in children)
            if (Capture(c) is { } snap) list.Add(snap);
        return list;
    }

    /// <summary>
    /// Rebuild a full dock tree from a snapshot, re-using the live leaf tool
    /// instances from the registry (so view-models stay wired). Returns a new
    /// fully-initialised <see cref="IRootDock"/> ready to assign to the
    /// DockControl. Updates <see cref="_root"/> so subsequent visibility
    /// queries operate on the new tree.
    /// </summary>
    public IRootDock BuildLayout(DockNodeSnapshot snapshot)
    {
        var rootLayout = BuildNode(snapshot);

        var root = CreateRootDock();
        root.Id               = "root";
        root.IsCollapsable    = false;
        root.VisibleDockables = CreateList(rootLayout!);
        root.ActiveDockable   = rootLayout;
        root.DefaultDockable  = rootLayout;
        _root                 = root;

        InitLayout(root);
        // Re-prime the "last-known position" cache against the snapshot's
        // structure — _dockHomes may not match a saved layout's grandparent
        // ids, but _lastKnownPositions just records whatever ended up where.
        CaptureAllPositions();
        return root;
    }

    /// <summary>
    /// Build the canonical default 3-column layout AND initialise it, ready to
    /// assign to the DockControl. Used at startup and as the base for the
    /// legacy (no-snapshot) load path: rebuilding from scratch guarantees every
    /// parent ToolDock exists again, so a tool whose dock was auto-removed when
    /// it was closed can be re-shown. The DockControl runs with
    /// InitializeLayout="False" so this is the single, deterministic init.
    /// </summary>
    public IRootDock BuildDefaultLayout()
    {
        var root = CreateLayout();
        InitLayout(root);
        CaptureAllPositions();
        return root;
    }

    /// <summary>
    /// Armed by the call sites that present the <em>default</em> layout to the
    /// user (first-run startup, Reset to Default Layout, leaving windowed mode).
    /// When set, <see cref="FloatMapperIfPending"/> pops the Mapper out into its
    /// own floating window — the out-of-box default is a floating Mapper, not
    /// one docked at the centre-bottom. Restoring a <em>saved</em> layout never
    /// arms this, so the user's persisted Mapper placement is preserved.
    /// </summary>
    public bool PendingMapperFloat { get; set; }

    /// <summary>
    /// If <see cref="PendingMapperFloat"/> is set, float the Mapper now that the
    /// dock tree is live and the owner window is shown (FloatDockable needs both,
    /// so this must run after the window opens — not at <c>BuildDefaultLayout</c>
    /// time). Idempotent: clears the flag so it fires once per default-layout
    /// presentation. The Mapper stays in the dockable registry with its centre-
    /// bottom home intact, so the user can re-dock it any time.
    /// </summary>
    public void FloatMapperIfPending()
    {
        if (!PendingMapperFloat) return;
        PendingMapperFloat = false;
        FloatTool("mapper");
    }

    private IDockable? BuildNode(DockNodeSnapshot n)
    {
        switch (n.Kind)
        {
            case "proportional":
            {
                var kids = BuildChildren(n.Children);
                return new ProportionalDock
                {
                    Id               = n.Id ?? "",
                    Orientation      = ParseOrientation(n.Orientation),
                    Proportion       = n.Proportion,
                    IsCollapsable    = false,
                    IsEmpty          = ComputeIsEmpty(kids),
                    VisibleDockables = CreateList(kids.ToArray()),
                };
            }
            case "documentdock":
            {
                var kids = BuildChildren(n.Children);
                return new DocumentDock
                {
                    Id               = n.Id ?? "docs",
                    IsCollapsable    = false,
                    Proportion       = n.Proportion,
                    IsEmpty          = ComputeIsEmpty(kids),
                    VisibleDockables = CreateList(kids.ToArray()),
                    ActiveDockable   = FindById(kids, n.ActiveId),
                };
            }
            case "tooldock":
            {
                var kids = BuildChildren(n.Children);
                return new ToolDock
                {
                    Id               = n.Id ?? "",
                    Alignment        = ParseAlignment(n.Alignment),
                    Proportion       = n.Proportion,
                    IsEmpty          = ComputeIsEmpty(kids),
                    VisibleDockables = CreateList(kids.ToArray()),
                    ActiveDockable   = FindById(kids, n.ActiveId),
                };
            }
            case "splitter":
                return new ProportionalDockSplitter();
            case "leaf":
                if (n.Id is null) return null;
                if (_tools.TryGetValue(n.Id, out var entry)) return entry.Dockable;
                // A plugin window from a saved layout whose plugin hasn't pushed
                // content yet this session — recreate the panel from the snapshot
                // (with its persisted title) so the arrangement restores; the
                // plugin repopulates it on its next SetWindow.
                if (IsPluginWindowId(n.Id))
                {
                    // Window names that were dynamic plugin panels in earlier
                    // builds but are first-class tools now (Time Tracker moved
                    // builtin): map the stale leaf onto the real tool in its
                    // saved spot instead of resurrecting a dead generic panel
                    // no extension will ever write to again.
                    var migrated = n.Id.Substring(PluginWindowPrefix.Length) switch
                    {
                        "time tracker"   => "time-tracker",
                        "inventory view" => "inventory-view",   // was the external DLL's panel
                        _                => null,
                    };
                    if (migrated is not null)
                        return _tools.TryGetValue(migrated, out var m) ? m.Dockable : null;
                    return CreatePluginWindowTool(n.Id, n.Title);
                }
                return null;   // unregistered id — skip
            default:
                return null;
        }
    }

    /// <summary>
    /// Mirror of FactoryBase.UpdateIsEmpty for the snapshot-restore path (#219):
    /// docks built here bypass the factory's add/remove bookkeeping, so IsEmpty
    /// (default false) would stay stale for a column that was saved empty — and
    /// the ProportionalDockCollapseSkin keys the visual collapse on IsEmpty.
    /// Children are built bottom-up, so nested docks' IsEmpty is already set.
    /// </summary>
    private static bool ComputeIsEmpty(List<IDockable> kids) =>
        kids.Count == 0 || kids.All(k =>
            k is ISplitter || k is IDock { IsEmpty: true, IsCollapsable: true });

    private List<IDockable> BuildChildren(List<DockNodeSnapshot> children)
    {
        var list = new List<IDockable>();
        foreach (var c in children)
            if (BuildNode(c) is { } node) list.Add(node);
        return list;
    }

    private static IDockable? FindById(List<IDockable> children, string? id)
        => id is null ? null
         : children.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static Orientation ParseOrientation(string? s)
        => Enum.TryParse<Orientation>(s, ignoreCase: true, out var o) ? o : Orientation.Horizontal;

    private static Alignment ParseAlignment(string? s)
        => Enum.TryParse<Alignment>(s, ignoreCase: true, out var a) ? a : Alignment.Unset;

    // ── Per-window right-click menu ────────────────────────────────────────

    /// <summary>
    /// Build the right-click window menu (Genie 4's Clear / Time Stamp / Name
    /// List Only / Close Window) for every registered dockable that doesn't yet
    /// have one. Idempotent — the dockables persist across layout rebuilds, so a
    /// menu (and its WindowSettings.Changed subscription) is built once.
    /// </summary>
    private void AttachWindowMenus()
    {
        foreach (var (id, entry) in _tools)
            if (entry.Dockable is IWindowMenuHost { WindowMenu: null } host)
                host.WindowMenu = BuildWindowMenu(id, entry.Dockable);
    }

    /// <summary>
    /// Construct the menu model for one window, enabling only the items that
    /// apply to its type: Close for every tool (not the primary game document);
    /// Clear for windows with a line buffer; Time Stamp + Name List Only for the
    /// per-line text feeds (streams + game window).
    /// </summary>
    private WindowMenuModel BuildWindowMenu(string id, IDockable dockable)
    {
        // Close — every dockable except the primary game document.
        ICommand? close = dockable is GameTextDocument
            ? null
            : ReactiveCommand.Create(() => SetToolVisibility(id, false));

        // Clear — windows backed by a clearable line buffer.
        ICommand? clear = dockable switch
        {
            StreamTool st       => ReactiveCommand.Create(() => st.Buffer.Lines.Clear()),
            GameTextDocument gd => ReactiveCommand.Create(() => gd.ViewModel.Lines.Clear()),
            BackpackTool bp     => ReactiveCommand.Create(() => bp.ViewModel.Items.Clear()),
            RawXmlTool rx       => ReactiveCommand.Create(() => rx.ViewModel.Lines.Clear()),
            PluginWindowTool pw => ReactiveCommand.Create(() => pw.ViewModel.Lines.Clear()),
            _                   => null
        };

        // Float / Re-dock — every tool except the primary game document (which,
        // like Close, can't be detached). One toggle; the verb reflects live
        // float state, re-read on menu-open via WindowMenuBehavior.
        (Action? toggleFloat, Func<bool>? floatProbe) = dockable is GameTextDocument
            ? (null, null)
            : ((Action?)(() => ToggleFloat(id)), (Func<bool>?)(() => IsToolFloating(id)));

        // #181: Hide / Show Title Bar — same set as Float (any detachable tool); the
        // menu item shows only while the window is actually floating (WindowMenuModel
        // gates it on live float state).
        (Action? toggleTitleBar, Func<bool>? titleBarHidden) = dockable is GameTextDocument
            ? (null, null)
            : ((Action?)(() => ToggleFloatTitleBar(id)), (Func<bool>?)(() => IsFloatTitleBarHidden(id)));

        // Copy All — windows with a copyable line buffer. Game window reuses its
        // existing clipboard command (also on Ctrl+Shift+C); streams + raw-XML
        // copy their buffer text.
        ICommand? copyAll = dockable switch
        {
            GameTextDocument gd => gd.ViewModel.CopyAllCommand,
            StreamTool st       => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(st.Buffer.Lines.Select(l => l.Text))),
            RawXmlTool rx       => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(rx.ViewModel.Lines)),
            PluginWindowTool pw => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(pw.ViewModel.Lines.Select(l => l.Text))),
            BackpackTool bp     => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(bp.ViewModel.Items.Select(l => l.Text))),
            // Field/content panels (#120 follow-up): not line buffers, but
            // their text is just as copy/export-worthy.
            RoomTool rm         => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(RoomLines(rm))),
            ExperienceTool ex   => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(ex.ViewModel.Lines.Select(l => l.Text))),
            ActiveSpellsTool sp => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(ContentLines(sp.ViewModel.Content))),
            TimeTrackerTool tt  => ReactiveCommand.CreateFromTask(
                () => WindowClipboard.CopyLinesAsync(ContentLines(tt.ViewModel.Content))),
            _                   => null
        };

        // Save As… (#120) — same windows that can Copy All, plus the
        // inventory list: export the buffer as plain text via the OS picker.
        ICommand? saveAs = dockable switch
        {
            GameTextDocument gd => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(gd.Title, gd.ViewModel.Lines.Select(l => l.Text))),
            StreamTool st       => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(st.Title, st.Buffer.Lines.Select(l => l.Text))),
            BackpackTool bp     => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(bp.Title, bp.ViewModel.Items.Select(l => l.Text))),
            RawXmlTool rx       => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(rx.Title, rx.ViewModel.Lines)),
            PluginWindowTool pw => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(pw.Title, pw.ViewModel.Lines.Select(l => l.Text))),
            RoomTool rm         => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(rm.Title, RoomLines(rm))),
            ExperienceTool ex   => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(ex.Title, ex.ViewModel.Lines.Select(l => l.Text))),
            ActiveSpellsTool sp => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(sp.Title, ContentLines(sp.ViewModel.Content))),
            TimeTrackerTool tt  => ReactiveCommand.CreateFromTask(
                () => WindowSaveAs.SaveLinesAsync(tt.Title, ContentLines(tt.ViewModel.Content))),
            _                   => null
        };

        // Find… (#120) — any window hosting an in-window find bar.
        ICommand? find = dockable is IFindHost findHost
            ? ReactiveCommand.Create(() => { findHost.Find.IsOpen = true; })
            : null;

        // Word Wrap (#120) — WindowSettings-backed per-window toggle for the
        // TextLine feeds. Plugin windows stay always-wrap (menu-script text
        // depends on it); Raw XML stays always-NoWrap (protocol dump).
        var  wrapInit   = true;
        Action<bool>? wrapToggle = null;
        if (dockable is GameTextDocument or StreamTool or BackpackTool)
        {
            var wrapSettings = _vm.WindowSettings.Get(id);
            wrapInit   = wrapSettings.WordWrap;
            wrapToggle = on =>
            {
                wrapSettings.WordWrap = on;
                wrapSettings.NotifyChanged();   // tool re-resolves ToolTextWrapping live
                _vm.SaveWindowSettings();
            };
        }

        // Show Config Bar — panels with a settings strip across the top; today
        // just the Experience window's Density / Track gain / G4 layout row.
        // The toggle drives the view-model, which persists experienceconfigbar.
        bool          configBarInit   = true;
        Action<bool>? configBarToggle = null;
        if (dockable is ExperienceTool expTool)
        {
            configBarInit   = expTool.ViewModel.ShowConfigBar;
            configBarToggle = on => expTool.ViewModel.ShowConfigBar = on;
        }

        // Time Stamp + Name List Only + Pause Scrolling only make sense for the
        // per-line text feeds; everything else gets Copy All / Save As (where
        // it has a buffer) + Find + Clear + Float + Close.
        if (dockable is not (StreamTool or GameTextDocument))
        {
            var toolMenu = new WindowMenuModel(
                clear:             clear,
                close:             close,
                copyAll:           copyAll,
                saveAs:            saveAs,
                find:              find,
                wordWrapOn:        wrapInit,
                onWordWrapToggled: wrapToggle,
                onToggleFloat:     toggleFloat,
                floatStateProbe:   floatProbe,
                onToggleTitleBar:  toggleTitleBar,
                titleBarHiddenProbe: titleBarHidden,
                configBarOn:        configBarInit,
                onConfigBarToggled: configBarToggle);

            // Keep the checkmark honest when the visibility changes outside the
            // menu — the value seeded from settings.cfg on connect (Attach).
            if (dockable is ExperienceTool exSync)
                exSync.ViewModel.WhenAnyValue(v => v.ShowConfigBar)
                    .Subscribe(v => toolMenu.SyncConfigBar(v));

            return toolMenu;
        }

        var settings = _vm.WindowSettings.Get(id);

        // Sync the buffer to a persisted "Name List Only=true" so it filters from
        // the first line, not only after the menu is first opened.
        ApplyNameListOnly(dockable, settings.NameListOnly);

        // "Show in Main Window" (EchoToMain) — stream windows only; the main
        // game window echoing into itself is meaningless, so GameTextDocument
        // gets no toggle (ShowEchoToMain stays false → the item hides). On by
        // default: every stream mirrors into Main until the user opts out here.
        bool          echoInit   = false;
        Action<bool>? echoToggle = null;
        if (dockable is StreamTool)
        {
            echoInit   = settings.EchoToMain;
            echoToggle = on =>
            {
                settings.EchoToMain = on;
                settings.NotifyChanged();   // StreamTabsViewModel reads this live
                _vm.SaveWindowSettings();
            };
        }

        // Pause Scrolling — toggles the dockable's reactive IsScrollPaused, which
        // the ScrollViewer binds via AutoScrollBehavior.Paused. Transient.
        (bool pausedInit, Action<bool>? pauseToggle) = dockable switch
        {
            GameTextDocument gd => (gd.IsScrollPaused, (Action<bool>?)(on => gd.IsScrollPaused = on)),
            StreamTool st       => (st.IsScrollPaused, (Action<bool>?)(on => st.IsScrollPaused = on)),
            _                   => (false, null)
        };

        var menu = new WindowMenuModel(
            clear:                 clear,
            close:                 close,
            copyAll:               copyAll,
            timestampOn:           settings.Timestamp,
            onTimestampToggled:    on =>
            {
                settings.Timestamp = on;
                settings.NotifyChanged();   // repaint future lines with/without stamp
                _vm.SaveWindowSettings();
            },
            nameListOnlyOn:        settings.NameListOnly,
            onNameListOnlyToggled: on =>
            {
                settings.NameListOnly = on;
                ApplyNameListOnly(dockable, on);
                _vm.SaveWindowSettings();
            },
            scrollPausedOn:        pausedInit,
            onScrollPauseToggled:  pauseToggle,
            onToggleFloat:         toggleFloat,
            floatStateProbe:       floatProbe,
            saveAs:                saveAs,
            find:                  find,
            wordWrapOn:            wrapInit,
            onWordWrapToggled:     wrapToggle,
            echoToMainOn:          echoInit,
            onEchoToMainToggled:   echoToggle,
            onToggleTitleBar:      toggleTitleBar,
            titleBarHiddenProbe:   titleBarHidden);

        // Keep the checkmarks in sync if the same settings are edited elsewhere
        // (e.g. the Configuration → Layout tab's Time Stamp checkbox).
        settings.Changed += () =>
        {
            menu.SyncTimestamp(settings.Timestamp);
            menu.SyncNameListOnly(settings.NameListOnly);
            menu.SyncWordWrap(settings.WordWrap);
            menu.SyncEchoToMain(settings.EchoToMain);
        };

        return menu;
    }

    /// <summary>Flatten the Room panel's fields (title, description, exits,
    /// players, objects) to text lines for Copy All / Save As — the panel is
    /// field-based, not a line buffer, so it needs its own flattening. Empty
    /// fields are skipped, mirroring their collapsed rows in the panel.</summary>
    private static IEnumerable<string> RoomLines(RoomTool room)
    {
        var vm = room.ViewModel;
        return new[] { vm.Title, vm.Description, vm.Exits, vm.Players, vm.Objects }
            .Where(f => !string.IsNullOrWhiteSpace(f));
    }

    /// <summary>Split a single-string panel body (Experience, Active Spells,
    /// Time Tracker) into lines for Copy All / Save As.</summary>
    private static IEnumerable<string> ContentLines(string content)
        => string.IsNullOrEmpty(content)
            ? Array.Empty<string>()
            : content.Replace("\r\n", "\n").Split('\n');

    /// <summary>Push the Name List Only flag onto the window's line buffer so its
    /// Add path starts (or stops) filtering.</summary>
    private static void ApplyNameListOnly(IDockable dockable, bool on)
    {
        switch (dockable)
        {
            case StreamTool st:       st.Buffer.NameListOnly      = on; break;
            case GameTextDocument gd: gd.ViewModel.NameListOnly   = on; break;
        }
    }

    // ── Window-management API ──────────────────────────────────────────────

    /// <summary>
    /// True if any dockable with this id exists anywhere in the live layout
    /// tree (including floating windows). Id-based lookup so we're not tripped
    /// up by Dock.Avalonia replacing instances during layout init.
    /// </summary>
    public bool IsToolVisible(string id)
    {
        if (_root is null) return false;
        return FindByIdInTree(_root, id) is not null;
    }

    /// <summary>
    /// Show or hide a registered dockable. Showing re-adds it to its original
    /// parent dock and activates the tab. Hiding closes whatever instance is
    /// currently in the tree (in case Dock.Avalonia replaced our stored ref).
    /// </summary>
    public void SetToolVisibility(string id, bool visible)
    {
        if (!_tools.TryGetValue(id, out var entry)) return;
        var (dockable, parentId) = entry;

        var current = _root is null ? null : FindByIdInTree(_root, id);
        var currentlyVisible = current is not null;
        if (currentlyVisible == visible)
        {
            // "Show" on a tool that's already visible but floating in a
            // MINIMIZED host window must restore that window — floats have no
            // taskbar button or Alt-Tab entry (#170), so the Window menu is
            // the only way back after the chrome's minimize button.
            if (visible && FindWindowHosting(id)?.Host is Avalonia.Controls.Window host
                && host.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                host.WindowState = Avalonia.Controls.WindowState.Normal;
                host.Activate();
            }
            return;
        }

        if (visible)
        {
            if (_root is null) return;

            // Stage 0: last-known-location. If the user dragged this panel
            // somewhere custom and then closed it, restore it to exactly
            // that spot — better UX than always going back to the default
            // home. _lastKnownPositions is populated continuously (initial
            // layout build, every DockableAdded, and just before close).
            if (_lastKnownPositions.TryGetValue(id, out var lastPos))
            {
                // Stage 0a: parent dock still in the tree — simplest restore.
                if (FindByIdInTree(_root, lastPos.ParentId) is IDock lastParent)
                {
                    InitDockable(dockable, lastParent);
                    var count = lastParent.VisibleDockables?.Count ?? 0;
                    if (lastPos.Index >= 0 && lastPos.Index <= count)
                        InsertDockable(lastParent, dockable, lastPos.Index);
                    else
                        AddDockable(lastParent, dockable);
                    SetActiveDockable(dockable);
                    return;
                }

                // Stage 0b: parent got pruned (the single-child ToolDock
                // collapsed when its last tool was closed), but the
                // grandparent is still around — rebuild the parent in its
                // recorded position. This is what the user's "previously
                // closed location" requirement needs: when Mapper was alone
                // in mapper-dock and got closed, mapper-dock vanished;
                // recreating it inside center-col with the captured
                // alignment + proportion brings Mapper back to its real
                // home.
                if (!string.IsNullOrEmpty(lastPos.GrandparentId)
                    && lastPos.ParentAlignment.HasValue
                    && FindByIdInTree(_root, lastPos.GrandparentId) is IDock grand)
                {
                    var rebuiltParent = new ToolDock
                    {
                        Id               = lastPos.ParentId,
                        Alignment        = lastPos.ParentAlignment.Value,
                        Proportion       = lastPos.ParentProportion,
                        VisibleDockables = CreateList<IDockable>(),
                    };
                    InitDockable(rebuiltParent, grand);
                    var grandCount = grand.VisibleDockables?.Count ?? 0;
                    if (lastPos.IndexInGrandparent >= 0 && lastPos.IndexInGrandparent <= grandCount)
                        InsertDockable(grand, rebuiltParent, lastPos.IndexInGrandparent);
                    else
                        AddDockable(grand, rebuiltParent);

                    InitDockable(dockable, rebuiltParent);
                    AddDockable(rebuiltParent, dockable);
                    SetActiveDockable(dockable);
                    return;
                }

                // Stage 0c/0d: grandparent ALSO got dissolved (the classic
                // Dock.Avalonia "single-non-splitter-child → unwrap" trap).
                // Look for an anchor sibling — a dock that used to share
                // the grandparent with our parent. The anchor survived the
                // unwrap; we use it as a coordinate to find where the
                // grandparent USED to be in the tree, then either:
                //   • 0d (wrap-restore): if the anchor's current parent
                //     has a DIFFERENT orientation than the dissolved
                //     grandparent (e.g. the dissolved center-col was
                //     Vertical, but the anchor now lives in the
                //     Horizontal root-layout), rebuild the missing
                //     grandparent ProportionalDock and move the anchor
                //     into it together with the new ToolDock. Result:
                //     original "Game on top, Mapper below" stacking is
                //     restored.
                //   • 0c (sibling-splice): if orientations match (or we
                //     have no orientation info), just splice the rebuilt
                //     ToolDock as a sibling of the anchor.
                if (!string.IsNullOrEmpty(lastPos.AnchorSiblingId)
                    && lastPos.ParentAlignment.HasValue
                    && FindByIdInTree(_root, lastPos.AnchorSiblingId) is IDockable anchor
                    && FindParentInTree(_root, anchor) is IDock anchorParent)
                {
                    var rebuiltParent = new ToolDock
                    {
                        Id               = lastPos.ParentId,
                        Alignment        = lastPos.ParentAlignment.Value,
                        Proportion       = lastPos.ParentProportion,
                        VisibleDockables = CreateList<IDockable>(),
                    };
                    InitDockable(rebuiltParent, anchorParent);

                    var anchorIdx = anchorParent.VisibleDockables?.IndexOf(anchor) ?? -1;

                    // Stage 0d: orientation-aware wrap-restore. If the
                    // anchor's current parent is a ProportionalDock with a
                    // different orientation than our captured grandparent
                    // (e.g. anchor moved Vertical→Horizontal during the
                    // unwrap), we wrap the anchor in a fresh
                    // ProportionalDock of the original orientation so the
                    // restored stacking matches what the user had.
                    if (lastPos.GrandparentOrientation.HasValue
                        && anchorParent is ProportionalDock anchorProp
                        && anchorProp.Orientation != lastPos.GrandparentOrientation.Value
                        && anchorIdx >= 0
                        && anchorProp.VisibleDockables is { } anchorSiblings)
                    {
                        // Build a new ProportionalDock that will host both
                        // the anchor and the rebuilt mapper-dock. Inherit
                        // the captured grandparent's id so future captures
                        // see consistent ids.
                        var rebuiltGrand = new ProportionalDock
                        {
                            Id               = lastPos.GrandparentId ?? string.Empty,
                            Orientation      = lastPos.GrandparentOrientation.Value,
                            IsCollapsable    = false,
                            VisibleDockables = CreateList<IDockable>(),
                        };

                        // Move the anchor out of its current parent — manual
                        // collection edit since Dock.Avalonia's FactoryBase
                        // doesn't expose a cross-parent move primitive at
                        // this version. Removing also drops any adjacent
                        // ProportionalDockSplitter (the one separating the
                        // anchor from its old neighbour) — without that
                        // cleanup the new wrapper inherits a phantom
                        // splitter.
                        anchorSiblings.RemoveAt(anchorIdx);
                        if (anchorIdx < anchorSiblings.Count
                            && anchorSiblings[anchorIdx] is ProportionalDockSplitter trailingSplitter)
                            anchorSiblings.RemoveAt(anchorIdx);
                        else if (anchorIdx > 0
                            && anchorSiblings[anchorIdx - 1] is ProportionalDockSplitter leadingSplitter)
                            anchorSiblings.RemoveAt(anchorIdx - 1);

                        // Splice the new wrapper at the anchor's old
                        // position in the great-grandparent. Compute the
                        // insertion index AFTER the splitter cleanup above.
                        var insertIdx = Math.Min(anchorIdx, anchorSiblings.Count);

                        // Populate the wrapper: anchor → splitter →
                        // rebuilt mapper-dock. This restores the original
                        // stacking (Game on top, Mapper below in the
                        // Vertical case).
                        InitDockable(anchor,        rebuiltGrand);
                        AddDockable (rebuiltGrand,  anchor);
                        AddDockable (rebuiltGrand,  new ProportionalDockSplitter());
                        InitDockable(rebuiltParent, rebuiltGrand);
                        AddDockable (rebuiltGrand,  rebuiltParent);

                        // Add the tool itself to the new ToolDock.
                        InitDockable(dockable,      rebuiltParent);
                        AddDockable (rebuiltParent, dockable);
                        SetActiveDockable(dockable);

                        // Finally drop the wrapper back into the great-
                        // grandparent at the anchor's old slot.
                        InitDockable(rebuiltGrand, anchorParent);
                        if (insertIdx < anchorSiblings.Count)
                            anchorSiblings.Insert(insertIdx, rebuiltGrand);
                        else
                            anchorSiblings.Add(rebuiltGrand);

                        return;
                    }

                    // Stage 0c (fallback): orientations match (or no
                    // orientation info) — simple sibling splice. The panel
                    // comes back next to the anchor on the same axis as
                    // the survivor; the user can drag it elsewhere if
                    // they want.
                    if (anchorIdx >= 0 && anchorParent is ProportionalDock)
                    {
                        InsertDockable(anchorParent, new ProportionalDockSplitter(), anchorIdx + 1);
                        InsertDockable(anchorParent, rebuiltParent, anchorIdx + 2);
                    }
                    else if (anchorIdx >= 0)
                    {
                        InsertDockable(anchorParent, rebuiltParent, anchorIdx + 1);
                    }
                    else
                    {
                        AddDockable(anchorParent, rebuiltParent);
                    }

                    InitDockable(dockable, rebuiltParent);
                    AddDockable(rebuiltParent, dockable);
                    SetActiveDockable(dockable);
                    return;
                }
            }

            // Resolve the parent dock live by id — the stored reference would
            // be stale after a BuildLayout rebuild.
            if (FindByIdInTree(_root, parentId) is IDock parent)
            {
                // Re-establish parent / owner wiring before adding. Without
                // this, a dockable that was X-closed re-enters
                // VisibleDockables but the ContentControl never re-binds
                // to it — the tab shows in the strip but the panel beneath
                // stays blank, OR the visual doesn't appear at all. This
                // is the difference between "menu checkbox flipped on" and
                // "user actually sees their panel back."
                InitDockable(dockable, parent);
                AddDockable(parent, dockable);
                SetActiveDockable(dockable);
            }
            // The parent ToolDock was pruned when its last child was closed
            // (the tab-X bug). Rebuild it in its home position so the tool
            // returns to the dock it lived in before, instead of silently
            // failing to re-open.
            else if (TryRestoreHomeDock(parentId, dockable))
            {
                // home rebuild succeeded — nothing more to do.
            }
            // Both the original parent and the home-rebuild failed. This
            // typically happens when a saved layout was loaded that doesn't
            // include the original grandparent (e.g. "center-col" was renamed
            // or the snapshot pre-dates the _dockHomes registration code).
            // Rather than silently leaving the menu checkbox flipped on with
            // nothing on screen, fall back through:
            //   1. Any other ToolDock anywhere in the live tree (so the
            //      panel comes back in a reasonable location).
            //   2. As a last resort, float it in its own OS window so the
            //      user at least sees their panel — they can drag it back
            //      to redock wherever they want.
            else if (FindFirstToolDockInTree(_root) is IDock fallbackParent)
            {
                InitDockable(dockable, fallbackParent);
                AddDockable(fallbackParent, dockable);
                SetActiveDockable(dockable);
            }
            else
            {
                InitDockable(dockable, _root);
                FloatDockable(dockable);
            }
        }
        else
        {
            // Capture last-known location before closing so a future re-open
            // can put the panel back exactly where it was — including any
            // drag-redocked custom position. The matching capture for X-button
            // close happens via the DockableAdded hook (the panel's position
            // was recorded the last time it entered the tree).
            CapturePosition(current);

            // Close the instance actually in the tree, not our stored reference
            // (which Dock.Avalonia may have replaced during init).
            CloseDockable(current!);
        }
    }

    /// <summary>
    /// Recreate a pruned home ToolDock (per <see cref="_dockHomes"/>) inside its
    /// always-present grandparent, drop <paramref name="dockable"/> into it, and
    /// activate it. Returns false if there's no recorded home or the grandparent
    /// can't be found. Splitters are inserted only where one isn't already
    /// adjacent, so re-opening doesn't accumulate duplicate separators.
    /// </summary>
    private bool TryRestoreHomeDock(string parentId, IDockable dockable)
    {
        if (_root is null) return false;
        if (!_dockHomes.TryGetValue(parentId, out var home)) return false;
        if (FindByIdInTree(_root, home.GrandparentId) is not IDock grand) return false;

        var dock = new ToolDock
        {
            Id               = parentId,
            Alignment        = home.Alignment,
            Proportion       = home.Proportion,
            VisibleDockables = CreateList<IDockable>(),
        };

        var kids = grand.VisibleDockables;
        var hadChildren = kids is { Count: > 0 };

        if (home.AtFront)
        {
            InsertDockable(grand, dock, 0);
            // grand is now [dock, <old first child>, …]; separate them unless
            // the old first child is already a splitter.
            if (hadChildren && grand.VisibleDockables is { Count: > 1 } k
                && k[1] is not ProportionalDockSplitter)
                InsertDockable(grand, new ProportionalDockSplitter(), 1);
        }
        else
        {
            if (hadChildren && kids![kids.Count - 1] is not ProportionalDockSplitter)
                AddDockable(grand, new ProportionalDockSplitter());
            AddDockable(grand, dock);
        }

        // Same re-init as the live-parent path in SetToolVisibility — without
        // this, the freshly-rebuilt ToolDock holds a phantom dockable whose
        // visual isn't bound to its content.
        InitDockable(dockable, dock);
        AddDockable(dock, dockable);
        SetActiveDockable(dockable);
        return true;
    }

    /// <summary>
    /// Detach a tool into its own top-level floating window. Idempotent:
    /// if the tool is missing from the tree it's a no-op; if it's already
    /// in a floating window Dock.Avalonia handles re-floating gracefully
    /// (the tool's CanFloat must be true, which is the default on Tool).
    /// </summary>
    public void FloatTool(string id)
    {
        if (_root is null) return;
        var current = FindByIdInTree(_root, id);
        if (current is null) return;

        // Base Factory.FloatDockable lifts the dockable into a new HostWindow
        // anchored to the root's Windows collection. Dragging that window's
        // title bar back over the main app shows dock indicators so the user
        // can re-dock it wherever they want.
        FloatDockable(current);
    }

    /// <summary>
    /// Snapshot every floating window attached to the root — the tool ids each
    /// hosts plus its screen geometry — for layout persistence. Floats live in
    /// <see cref="IRootDock.Windows"/>, outside the docked tree that
    /// <see cref="CaptureLayout"/> walks, so without this saved layouts dropped
    /// every floated panel.
    /// </summary>
    public List<FloatingWindowSnapshot> CaptureFloatingWindows()
    {
        var list = new List<FloatingWindowSnapshot>();
        if (_root?.Windows is not { } windows) return list;
        foreach (var w in windows)
        {
            if (w.Layout is not { } layout) continue;
            var ids = new List<string>();
            CollectLeafToolIds(layout, ids);
            if (ids.Count == 0) continue;

            // Read geometry from the REALIZED host window, not the IDockWindow
            // model — Dock doesn't reliably sync X/Y/Width/Height back from the
            // OS window (a moved/resized float can leave the model at 0/NaN,
            // which restored as a giant default-sized window). Same convention
            // on both ends: Position is physical pixels, Width/Height DIPs.
            double x = w.X, y = w.Y, width = w.Width, height = w.Height;
            if (w.Host is Avalonia.Controls.Window host)
            {
                x = host.Position.X;
                y = host.Position.Y;
                if (host.Bounds.Width  > 0) width  = host.Bounds.Width;
                if (host.Bounds.Height > 0) height = host.Bounds.Height;
            }

            list.Add(new FloatingWindowSnapshot
            {
                ToolIds = ids,
                X       = x,
                Y       = y,
                Width   = width,
                Height  = height,
            });
        }
        return list;
    }

    /// <summary>Collect the registry ids of leaf tools/documents under a
    /// floating window's layout tree (skips the wrapper docks).</summary>
    private static void CollectLeafToolIds(IDockable node, List<string> ids)
    {
        if (node is IDock dock)
        {
            if (dock.VisibleDockables is { } kids)
                foreach (var k in kids) CollectLeafToolIds(k, ids);
            return;
        }
        if (node is not ProportionalDockSplitter && node.Id is { Length: > 0 } id)
            ids.Add(id);
    }

    /// <summary>
    /// Re-float the tools recorded by <see cref="CaptureFloatingWindows"/> at
    /// their saved geometry. Must run AFTER the rebuilt tree is live on the
    /// DockControl and the owner window is shown (FloatDockable needs both —
    /// same constraint as <see cref="FloatMapperIfPending"/>). A snapshot that
    /// held several tools restores one float per tool, cascaded from the saved
    /// origin — a deliberate simplification over rebuilding multi-tab float
    /// containers. Unknown/stale ids are skipped.
    /// </summary>
    public void RestoreFloatingWindows(IEnumerable<FloatingWindowSnapshot> floats)
    {
        if (_root is null) return;
        foreach (var f in floats)
        {
            int cascade = 0;
            foreach (var id in f.ToolIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (IsToolFloating(id)) { cascade++; continue; }   // already floating

                if (FindByIdInTree(_root, id) is { } docked)
                    FloatDockable(docked);                          // float the docked instance
                else
                    ShowToolFloating(id);                           // open-closed-tool-floated path

                if (FindWindowHosting(id) is { } w)
                {
                    // Apply only sane, finite geometry — a snapshot captured
                    // from a degenerate model must not blow the float up to a
                    // giant default or park it off-screen. Position and size
                    // are validated independently.
                    bool sizeOk = double.IsFinite(f.Width)  && f.Width  >= 100
                               && double.IsFinite(f.Height) && f.Height >= 80;
                    bool posOk  = double.IsFinite(f.X) && double.IsFinite(f.Y);

                    if (sizeOk) { w.Width = f.Width; w.Height = f.Height; }
                    if (posOk)  { w.X = f.X + cascade * 28; w.Y = f.Y + cascade * 28; }

                    // The HostWindow is already presented; push the geometry
                    // through to the realized OS window as well, then clamp it
                    // onto a visible screen (the Opened-time clamp already ran
                    // with the pre-geometry defaults).
                    if (w.Host is Avalonia.Controls.Window host)
                    {
                        if (sizeOk) { host.Width = f.Width; host.Height = f.Height; }
                        if (posOk)
                            host.Position = new Avalonia.PixelPoint(
                                (int)(f.X + cascade * 28), (int)(f.Y + cascade * 28));
                        if (host is GenieHostWindow g) g.ClampToVisibleScreen();
                    }
                }
                cascade++;
            }
        }
    }

    /// <summary>The floating <see cref="IDockWindow"/> whose layout currently
    /// hosts the given tool id, or null if the tool isn't floating.</summary>
    private IDockWindow? FindWindowHosting(string id)
    {
        if (_root?.Windows is not { } windows) return null;
        foreach (var w in windows)
            if (w.Layout is { } layout && FindByIdInTree(layout, id) is not null)
                return w;
        return null;
    }

    /// <summary>
    /// Close every floating <see cref="IDockWindow"/> currently attached to the
    /// root. Swapping a freshly-built tree onto the DockControl does NOT tear
    /// down the old root's HostWindows — Dock.Avalonia only owns windows via the
    /// live root's <see cref="IRootDock.Windows"/> collection, so any tool that
    /// was floated (the out-of-box default floats the Mapper) survives the swap
    /// as an orphaned OS window while the rebuilt tree floats its own copy —
    /// the duplicate-Mapper bug on Reset to Default Layout. Callers invoke this
    /// against the CURRENT root immediately before building the replacement.
    /// </summary>
    public void CloseFloatingWindows()
    {
        if (_root?.Windows is not { } windows) return;
        // Exit() removes the window from the collection as it closes, so iterate
        // a snapshot to avoid mutating-while-enumerating.
        foreach (var w in windows.ToList())
            w.Exit();
    }

    // Base titles captured the first time a #comment is applied to a window,
    // so the annotation can be cleared back to the original (#179).
    private readonly Dictionary<string, string> _windowBaseTitles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Genie 4 <c>#comment &lt;window&gt; &lt;text&gt;</c>: annotate a dock
    /// panel's title bar as "<c>Base (text)</c>", or restore the base title
    /// when <paramref name="comment"/> is blank. The window name matches a tool
    /// id (e.g. "room", "mapper") or a plugin-window name, case-insensitively.
    /// </summary>
    public void SetWindowComment(string window, string comment)
    {
        if (string.IsNullOrWhiteSpace(window) || _root is null) return;

        // Resolve the target dockable: a built-in tool id (e.g. "room"), else a
        // plugin window by name. Match against what's actually in the tree.
        var id = _tools.Keys.FirstOrDefault(k => k.Equals(window, StringComparison.OrdinalIgnoreCase));
        if (id is null && FindByIdInTree(_root, PluginWindowId(window)) is not null)
            id = PluginWindowId(window);
        if (id is null || FindByIdInTree(_root, id) is not { } dockable) return;

        if (!_windowBaseTitles.TryGetValue(id, out var baseTitle))
        {
            baseTitle = dockable.Title ?? window;
            _windowBaseTitles[id] = baseTitle;
        }
        dockable.Title = string.IsNullOrWhiteSpace(comment)
            ? baseTitle
            : $"{baseTitle} ({comment.Trim()})";
    }

    /// <summary>
    /// True if the tool is currently in one of the root's floating
    /// <see cref="IRootDock.Windows"/> (as opposed to docked in the main
    /// layout). Drives the Window menu's "Float" ⇄ "Re-dock" verb and routes
    /// <see cref="ToggleFloat"/>.
    /// </summary>
    public bool IsToolFloating(string id)
    {
        if (_root?.Windows is not { } windows) return false;
        foreach (var w in windows)
            if (w.Layout is { } layout && FindByIdInTree(layout, id) is not null)
                return true;
        return false;
    }

    /// <summary>Window-menu "Float / Re-dock": float a docked tool, or bring a
    /// floating one back to its docked home.</summary>
    public void ToggleFloat(string id)
    {
        if (IsToolFloating(id)) RedockTool(id);
        else                    FloatTool(id);
    }

    /// <summary>#181: whether the tool's floating window currently has its title bar
    /// (Dock chrome) collapsed. False when the tool is docked or its host isn't a
    /// <see cref="GenieHostWindow"/>.</summary>
    public bool IsFloatTitleBarHidden(string id) =>
        FindWindowHosting(id)?.Host is GenieHostWindow g && g.IsTitleBarHidden;

    /// <summary>#181: collapse / restore a floating tool's title bar to reclaim the
    /// chrome's vertical space. No-op unless the tool is floating in a
    /// <see cref="GenieHostWindow"/> (a docked panel has no float title bar).</summary>
    public void ToggleFloatTitleBar(string id)
    {
        if (FindWindowHosting(id)?.Host is GenieHostWindow g)
            g.SetTitleBarHidden(!g.IsTitleBarHidden);
    }

    /// <summary>
    /// Bring a floating tool back into the docked layout at its last docked
    /// location. Closes the floating instance (the now-empty HostWindow is
    /// torn down by Dock.Avalonia) and reuses <see cref="SetToolVisibility"/>'s
    /// home-restore path — the same Stage-0 last-known-location logic that
    /// reopening a closed panel uses. <see cref="CapturePosition"/> deliberately
    /// ignores floating positions, so <c>_lastKnownPositions[id]</c> still
    /// points at the spot the tool was docked before it floated.
    /// </summary>
    public void RedockTool(string id)
    {
        if (_root is null) return;
        var current = FindByIdInTree(_root, id);
        if (current is null || !IsToolFloating(id)) return;

        // Drop the floating instance, then restore to home. After the close the
        // id is gone from the tree, so SetToolVisibility sees it as hidden and
        // runs its restore branch instead of early-returning.
        CloseDockable(current);
        SetToolVisibility(id, true);
    }

    /// <summary>
    /// Show a registered tool as a floating window when it isn't already in the
    /// tree. Unlike <see cref="FloatTool"/> (which floats a tool that's already
    /// docked), this is the "open it floated" path for a closed tool — it inits
    /// the registered dockable against the root and floats it. Idempotent: if
    /// the tool is already visible anywhere (docked or floating) it's a no-op,
    /// which also avoids spawning a duplicate instance.
    /// </summary>
    public void ShowToolFloating(string id)
    {
        if (_root is null) return;
        if (FindByIdInTree(_root, id) is not null) return;   // already shown
        if (!_tools.TryGetValue(id, out var entry)) return;

        var dockable = entry.Dockable;
        InitDockable(dockable, _root);
        FloatDockable(dockable);
    }

    /// <summary>
    /// Record the dockable's current parent + index in
    /// <see cref="_lastKnownPositions"/>, along with the grandparent + parent
    /// alignment / proportion so we can rebuild the parent ToolDock if it
    /// gets pruned later. Walks the live tree to discover the parent rather
    /// than reading <c>Dockable.Owner</c> — the latter is only reliably set
    /// when a dockable enters the tree via <c>AddDockable</c>; tools wired
    /// at <c>CreateLayout</c> time (the initial <c>VisibleDockables</c>
    /// list-init path) have <c>Owner == null</c> until later, which would
    /// make CapturePosition silently fail on the initial cache priming.
    /// </summary>
    private void CapturePosition(IDockable? dockable)
    {
        if (dockable?.Id is not { Length: > 0 } id) return;
        if (_root is null) return;

        // Don't overwrite the last DOCKED location with a floating one. While a
        // tool sits in a HostWindow, its parent/grandparent are the ephemeral
        // float containers, which vanish when that window closes — caching them
        // sends a later reopen down the home-fallback path instead of the user's
        // custom docked spot (#35). Keep the prior docked position instead.
        if (_root.Windows?.Any(w => w.Layout is { } l && FindByIdInTree(l, id) is not null) ?? false)
        {
            return;
        }

        // Walk the tree to find this dockable's parent + grandparent rather
        // than relying on Dockable.Owner being set.
        if (FindParentInTree(_root, dockable) is not IDock parent) return;

        // Drag-redock creates fresh ToolDock / ProportionalDock containers
        // with empty Id. Earlier this branch returned silently — which left
        // _lastKnownPositions stuck on the LAST recognised parent (the
        // original home), so a "drag Mapper to a new column → close it →
        // reopen via Window menu" cycle restored to the original home
        // instead of the new spot. Stamping a stable synthetic Id keeps
        // both Stage 0a (parent in tree) and Stage 0b (rebuild from
        // grandparent) viable across the drag-close-reopen cycle.
        EnsureStableId(parent);
        var idx = parent.VisibleDockables?.IndexOf(dockable) ?? -1;

        string?      grandparentId          = null;
        Alignment?   parentAlignment        = null;
        double       parentProportion       = 0;
        int          idxInGrandparent       = -1;
        string?      anchorSiblingId        = null;
        Orientation? grandparentOrientation = null;
        if (FindParentInTree(_root, parent) is IDock grand)
        {
            // Same treatment for the grandparent — when a drag creates a new
            // ProportionalDock to host the new column, its Id is also empty.
            // Without this Stage 0b ("rebuild parent inside grandparent")
            // misses on lookup at restore time even though the grandparent is
            // still live in the tree.
            EnsureStableId(grand);
            grandparentId    = grand.Id;
            idxInGrandparent = grand.VisibleDockables?.IndexOf(parent) ?? -1;
            if (parent is IToolDock toolDock)
            {
                parentAlignment  = toolDock.Alignment;
                parentProportion = toolDock.Proportion;
            }
            if (grand is ProportionalDock propGrand)
                grandparentOrientation = propGrand.Orientation;

            // Find an "anchor sibling": a non-splitter dockable in the same
            // grandparent that we can use as a survivor reference if the
            // grandparent itself collapses. Dock.Avalonia unwraps a
            // ProportionalDock when it ends up with only one non-splitter
            // child — so when we're closing the last toolDock in
            // center-col, both center-col AND mapper-dock disappear and
            // the lone surviving sibling (e.g. the DocumentDock for the
            // Game window) gets promoted directly into the great-
            // grandparent (root-layout). Capturing that survivor lets us
            // walk back to a real container at restore time.
            if (grand.VisibleDockables is { } siblings)
            {
                foreach (var sib in siblings)
                {
                    if (ReferenceEquals(sib, parent)) continue;
                    if (sib is ProportionalDockSplitter) continue;
                    if (string.IsNullOrEmpty(sib.Id))    continue;
                    anchorSiblingId = sib.Id;
                    break;
                }
            }
        }

        _lastKnownPositions[id] = new LastKnownLocation(
            parent.Id, idx, grandparentId, parentAlignment, parentProportion,
            idxInGrandparent, anchorSiblingId, grandparentOrientation);
    }

    /// <summary>
    /// Stamp a synthetic stable Id on a dock that Dock.Avalonia spawned with
    /// an empty Id (drag-redock containers, occasionally a fresh
    /// ProportionalDock wrapper from a split). Keeps the Id we already
    /// assigned in <c>CreateLayout</c> intact — the synthetic prefix
    /// <c>auto-</c> makes it obvious in diagnostics that this container
    /// wasn't part of the canonical layout. The Id survives for the
    /// lifetime of the container instance, which is exactly the lifetime we
    /// need for <see cref="_lastKnownPositions"/> to round-trip a
    /// drag-close-reopen sequence.
    /// </summary>
    private static void EnsureStableId(IDockable dock)
    {
        if (string.IsNullOrEmpty(dock.Id))
            dock.Id = "auto-" + Guid.NewGuid().ToString("N").Substring(0, 12);
    }

    /// <summary>
    /// Walk the dock tree (recursively, including floating windows) and
    /// return the immediate parent IDock of <paramref name="target"/>, or
    /// null if the target isn't in the tree. Used by
    /// <see cref="CapturePosition"/> because <c>Dockable.Owner</c> isn't
    /// reliably set on tools wired via the initial <c>VisibleDockables</c>
    /// list-init path in <c>CreateLayout</c>.
    /// </summary>
    private static IDock? FindParentInTree(IDockable? node, IDockable target)
    {
        if (node is not IDock dock) return null;
        if (dock.VisibleDockables is { } children)
        {
            foreach (var child in children)
            {
                if (ReferenceEquals(child, target)) return dock;
                if (FindParentInTree(child, target) is { } hit) return hit;
            }
        }
        if (node is IRootDock root && root.Windows is { } windows)
        {
            foreach (var w in windows)
                if (w.Layout is { } layout && FindParentInTree(layout, target) is { } hit)
                    return hit;
        }
        return null;
    }

    /// <summary>
    /// Walk the current dock tree and capture the position of every
    /// registered tool into <see cref="_lastKnownPositions"/>. Called at the
    /// end of every layout build (default + snapshot) so the cache starts
    /// out matching whatever Dock.Avalonia just instantiated. Subsequent
    /// drag-redocking refreshes individual entries via the
    /// <c>DockableAdded</c> hook in <see cref="WireLastKnownPositionTracking"/>.
    /// </summary>
    private void CaptureAllPositions()
    {
        if (_root is null) return;
        foreach (var toolId in _tools.Keys.ToArray())
        {
            if (FindByIdInTree(_root, toolId) is { } d)
                CapturePosition(d);
        }
    }

    /// <summary>
    /// Subscribe to <c>FactoryBase.DockableAdded</c> so we refresh the
    /// <see cref="_lastKnownPositions"/> entry whenever a dockable enters
    /// the tree (initial layout build + drag-redock + menu-driven reopen).
    /// Hooked once at factory construction. The hide branch of
    /// <see cref="SetToolVisibility"/> additionally captures position
    /// immediately before <c>CloseDockable</c>; together the two paths give
    /// us a fresh-enough cache to restore even after drag-then-X-close.
    /// </summary>
    private void WireLastKnownPositionTracking()
    {
        // Capture on any event that can move a dockable. A drag-redock does NOT
        // reliably raise DockableAdded (or raises it before the new parent is
        // wired), so the custom location was never cached and a later reopen
        // restored to the stale home (#35). Also listen for DockableDocked /
        // DockableMoved, and DEFER the capture to the next dispatcher cycle so
        // the tree has settled (the parent linkage is live) before we walk it.
        void Recapture(IDockable? d)
        {
            if (d?.Id is not { Length: > 0 }) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CapturePosition(d));
        }
        DockableAdded  += (_, e) => Recapture(e.Dockable);
        DockableDocked += (_, e) => Recapture(e.Dockable);
        DockableMoved  += (_, e) => Recapture(e.Dockable);
    }

    /// <summary>
    /// Walk the dock tree (recursively, including floating windows) and
    /// return the first <see cref="IToolDock"/> encountered, or null if
    /// none exist. Used by <see cref="SetToolVisibility"/> as the
    /// last-resort fallback when a tool's registered parent has been
    /// pruned AND the home-rebuild path also failed (typically a saved
    /// layout that drops the original grandparent ids).
    /// </summary>
    private static IDock? FindFirstToolDockInTree(IDockable? node)
    {
        if (node is null) return null;
        if (node is IToolDock td) return td;
        if (node is IDock dock)
        {
            if (dock.VisibleDockables is { } children)
                foreach (var child in children)
                    if (FindFirstToolDockInTree(child) is { } hit)
                        return hit;
            if (node is IRootDock root && root.Windows is { } windows)
                foreach (var w in windows)
                    if (w.Layout is { } layout && FindFirstToolDockInTree(layout) is { } hit)
                        return hit;
        }
        return null;
    }

    /// <summary>
    /// Walk the dock tree (including floating windows under any RootDock) and
    /// return the first dockable whose Id matches, or null.
    /// </summary>
    private static IDockable? FindByIdInTree(IDockable node, string id)
    {
        if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase))
            return node;

        if (node is IDock dock)
        {
            if (dock.VisibleDockables is { } children)
                foreach (var child in children)
                    if (FindByIdInTree(child, id) is { } hit)
                        return hit;

            if (node is IRootDock root && root.Windows is { } windows)
                foreach (var w in windows)
                    if (w.Layout is { } layout && FindByIdInTree(layout, id) is { } hit)
                        return hit;
        }
        return null;
    }

    /// <summary>Dockable ids known to the factory — exposed so the VM can iterate the registry.</summary>
    public IReadOnlyCollection<string> ToolIds => _tools.Keys;

    // ── Plugin-window API ────────────────────────────────────────────────────

    /// <summary>
    /// Get the view-model backing a plugin window by its display name, creating
    /// the dock panel the first time. This is the single entry point the host
    /// uses to honour <c>IPluginHost.SetWindow</c> / <c>EchoToWindow</c> for
    /// non-built-in window names.
    ///
    /// <para><paramref name="show"/> controls visibility on <i>this</i> call.
    /// A newly-created window is always shown (you can't see a window that was
    /// never opened). For an existing window: <c>SetWindow</c> passes
    /// <c>show: true</c> — a deliberate full render should re-surface the panel
    /// if the user had closed it (e.g. <c>/iv open</c> after closing the tab).
    /// <c>EchoToWindow</c> passes <c>show: false</c> — a passive appended line
    /// must not yank a closed log panel back open on every line.</para>
    /// </summary>
    public PluginWindowViewModel GetOrCreatePluginWindow(string name, bool show = true)
    {
        var id = PluginWindowId(name);
        if (!_pluginWindowVms.TryGetValue(id, out var vm))
        {
            var tool = CreatePluginWindowTool(id, name);
            vm = _pluginWindowVms[tool.Id];
            show = true;   // first sight: always open so it's actually visible
        }

        // SetToolVisibility resolves the parent dock live by id and no-ops if
        // the panel is already in the tree.
        if (show && !IsToolVisible(id))
            SetToolVisibility(id, true);

        return vm;
    }

    /// <summary>Create + register a plugin-window VM/Tool for an id (no show).
    /// Shared by the runtime path and snapshot restore.</summary>
    private PluginWindowTool CreatePluginWindowTool(string id, string? name)
    {
        var title = string.IsNullOrWhiteSpace(name)
            ? id.Substring(PluginWindowPrefix.Length)
            : name.Trim();

        var vm   = new PluginWindowViewModel(title);
        var tool = new PluginWindowTool(vm, id, title);

        _pluginWindowVms[id]   = vm;
        _pluginWindowTools[id] = tool;
        _tools[id]             = (tool, PluginWindowParentId);
        tool.WindowMenu        = BuildWindowMenu(id, tool);
        return tool;
    }

    /// <summary>All plugin windows created this session, as (id, title, visible)
    /// — drives the Window → Plugin Windows submenu.</summary>
    public IReadOnlyList<(string Id, string Title, bool Visible)> PluginWindows()
    {
        var list = new List<(string, string, bool)>();
        foreach (var (id, tool) in _pluginWindowTools)
            list.Add((id, tool.Title, IsToolVisible(id)));
        return list;
    }

    // FactoryBase already exposes DockableClosed / DockableAdded events; the
    // VM subscribes to those directly in its constructor so the Window-menu
    // check marks stay aligned with the dock's actual state.
}
