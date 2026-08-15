using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Services;
using Genie.App.Settings;
using Genie.Core;
using Genie.Core.Commanding;
using Genie.Core.Mapper;
using Genie.Core.Update;
using Genie.Core.Update.Sources;
using Genie.Core.Update.Updaters;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the Mapper dockable tool. Subscribes to the live
/// <see cref="AutoMapperEngine"/> and surfaces current-room state for the UI.
/// Also exposes <see cref="AutoCreateEnabled"/> so the user can toggle the
/// engine between "just look up" and "auto-create new rooms as I explore",
/// <see cref="UpdateMapsCommand"/> for pulling the latest zone XMLs from
/// the official <a href="https://github.com/GenieClient/Maps">GenieClient/Maps</a>
/// repository, a zone-selector dropdown (<see cref="AvailableZones"/> +
/// <see cref="SelectedZoneFile"/>), and <see cref="GotoNodeCommand"/> which
/// walks the player to the clicked room using the engine's BFS pathfinder.
/// </summary>
public class MapperViewModel : ReactiveObject
{
    [Reactive] public string ZoneName        { get; private set; } = "(disconnected)";
    [Reactive] public string CurrentTitle    { get; private set; } = "(unknown)";
    [Reactive] public string CurrentServerId { get; private set; } = "";
    [Reactive] public int    RoomCount       { get; private set; }

    /// <summary>The mapper <c>$roomid</c> — the current map node's id, matching the
    /// script variable scripts/<c>#goto</c> compare against (NOT the server's
    /// <c>$gameroomid</c>, e.g. 1010002). Defaults to "0" off-map, like
    /// <c>$roomid</c> itself. Used by the status-bar location line (#66).</summary>
    [Reactive] public string CurrentRoomId { get; private set; } = "0";

    /// <summary>The mapper <c>$zoneid</c> — the active zone's Genie 4 numeric id
    /// (e.g. "33"). Defaults to "0" off-map, like <c>$zoneid</c>. Shown on the
    /// status-bar location line when the user picks "Zone Number" mode (#66).</summary>
    [Reactive] public string CurrentZoneId { get; private set; } = "0";

    private ObservableAsPropertyHelper<string>? _locationDisplay;
    /// <summary>Compact "Zone: … · Room: …" readout for the optional status-bar
    /// location line (#66). Zone name + DR's live server room id, live-updating.</summary>
    public string LocationDisplay => _locationDisplay?.Value ?? "";

    /// <summary>
    /// Compass exits from the current room — "north", "northeast", "down",
    /// "up", etc. Rendered as clickable buttons in the Mapper status strip
    /// alongside the Less Obvious Paths buttons. Clicking sends the direction
    /// word through <see cref="CommandEngine.ProcessInput"/> exactly like a
    /// typed move — aliases, triggers, pace settings, and the Mapper engine's
    /// own direction-tracking all apply.
    /// </summary>
    public ObservableCollection<string> CurrentObviousExits { get; } = new();

    // ── Map canvas background (user-customisable) ─────────────────────────
    /// <summary>
    /// User-chosen background colour for the map canvas. Bound to a
    /// ColorPickerButton in the Details expander. Setting it updates the
    /// derived <see cref="MapBackgroundBrush"/> and is persisted to
    /// <c>display.json</c> via the injected <see cref="DisplaySettings"/>.
    /// </summary>
    [Reactive] public Color  MapBackground      { get; set; } = Color.Parse("#EEE8AA");

    /// <summary>
    /// Derived brush bound to <see cref="Controls.MapCanvas.MapBackgroundBrush"/>.
    /// Recomputed whenever <see cref="MapBackground"/> changes so the canvas
    /// repaints live as the user drags through the colour picker.
    /// </summary>
    [Reactive] public IBrush MapBackgroundBrush { get; private set; } = new SolidColorBrush(Color.Parse("#EEE8AA"));

    /// <summary>
    /// User-chosen colour for the map's on-canvas label text. Bound to a
    /// ColorPickerButton in the Details expander; persisted to <c>display.json</c>.
    /// </summary>
    [Reactive] public Color  MapTextColor       { get; set; } = Color.Parse("#000000");

    /// <summary>
    /// Derived brush bound to <see cref="Controls.MapCanvas.LabelTextBrush"/>.
    /// Recomputed whenever <see cref="MapTextColor"/> changes so labels repaint
    /// live as the user drags the colour picker.
    /// </summary>
    [Reactive] public IBrush MapTextBrush       { get; private set; } = new SolidColorBrush(Color.Parse("#000000"));

    /// <summary>
    /// Non-compass arcs from the current room — "go small alleyway",
    /// "climb trellis", etc. Rendered as clickable buttons in the Mapper
    /// panel; clicking sends <see cref="LessObviousPath.MoveCommand"/>
    /// through the same pipeline as a typed command.
    /// </summary>
    public ObservableCollection<LessObviousPath> CurrentLessObviousPaths { get; } = new();

    // ── Graphical-canvas bindings ─────────────────────────────────────────
    /// <summary>Active zone reference, surfaced for the MapCanvas binding.</summary>
    [Reactive] public MapZone? ActiveZone  { get; private set; }

    /// <summary>Current node reference, surfaced for the MapCanvas binding.</summary>
    [Reactive] public MapNode? ActiveNode  { get; private set; }

    /// <summary>
    /// Editable copy of <see cref="ActiveNode"/>'s Notes string. Bound to
    /// the Details panel's Notes textbox; the user types here, then clicks
    /// "Save" to push the edit back into the live <see cref="MapNode"/> and
    /// persist the zone XML. We keep a separate field rather than two-way-
    /// binding directly to the node so unsaved keystrokes don't trigger a
    /// disk write on every character.
    /// </summary>
    [Reactive] public string CurrentNotes { get; set; } = "";

    /// <summary>
    /// When <c>true</c>, the auto-hiding Details flyout stays open even when
    /// the pointer leaves it — toggled by the pin button in the panel header.
    /// When <c>false</c> (the default) the flyout collapses to a thin
    /// "DETAILS" strip on the map's right edge and only slides out while
    /// hovered. Session-only; not persisted.
    /// </summary>
    [Reactive] public bool DetailsPinned { get; set; }

    /// <summary>
    /// File-system last-write time of the loaded zone XML. Surfaces in the
    /// Details panel as "Last updated: N days ago" so the user can spot
    /// stale data (a zone from 6 months ago might be missing recently-added
    /// rooms). Null when no zone is loaded or the file no longer exists.
    /// </summary>
    [Reactive] public DateTime? ZoneLastWriteTime { get; private set; }

    /// <summary>
    /// Friendly age string derived from <see cref="ZoneLastWriteTime"/>:
    /// "today", "yesterday", "3 days ago", "2 weeks ago", "5 months ago".
    /// Empty when no zone is loaded.
    /// </summary>
    [Reactive] public string ZoneAgeDisplay { get; private set; } = "";

    /// <summary>
    /// True when the loaded zone XML is older than ~30 days. Used to flash
    /// a "may be stale" hint in the Details panel — community maps update
    /// frequently and old local copies can miss new rooms / exits.
    /// </summary>
    [Reactive] public bool IsZoneStale { get; private set; }

    /// <summary>
    /// Push <see cref="CurrentNotes"/> back into <see cref="ActiveNode"/>
    /// and save the zone XML to disk. No-op when no node or no zone file
    /// is selected.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? SaveNotesCommand { get; private set; }

    /// <summary>Z-level the canvas should display. Editable by the UI.</summary>
    [Reactive] public int     Level        { get; set; }

    /// <summary>Opacity (0–255) of the ghost rooms drawn for the floors directly
    /// above/below the current level (Genie 4 <c>AutoMapperAlpha</c>). Read from
    /// <c>GenieConfig.AutoMapperAlpha</c> on <see cref="Attach"/>; bound to
    /// <c>MapCanvas.AutoMapperAlpha</c>. 0 = single-level view.</summary>
    [Reactive] public int     AutoMapperAlpha { get; private set; } = 255;

    /// <summary>On-map colour legend toggle (#157) — two-way. Persists to
    /// display.json and drives <c>MapCanvas.ShowLegend</c>. Defaults on until a
    /// DisplaySettings is attached.</summary>
    public bool ShowMapLegend
    {
        get => _display?.ShowMapLegend ?? true;
        set
        {
            if (_display is null || _display.ShowMapLegend == value) return;
            _display.ShowMapLegend = value;
            try { if (!string.IsNullOrEmpty(_displayPath)) _display.Save(_displayPath); } catch { /* best effort */ }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>
    /// Scale factor for the map canvas, bound to <c>MapCanvas.Zoom</c>.
    /// Coerced to [0.4, 4.0] inside the control; we let the user push freely
    /// and the control clamps. 1.0 = native size.
    /// </summary>
    [Reactive] public double  ZoomLevel    { get; set; } = 1.0;

    /// <summary>
    /// Bumped every time the engine signals MapChanged. The MapCanvas binds to
    /// this so it knows to repaint even when <see cref="ActiveZone"/> is the
    /// same reference (the engine mutates the Nodes dictionary in place).
    /// </summary>
    [Reactive] public int     RenderTick   { get; private set; }

    /// <summary>
    /// Two-way bound to a CheckBox in the UI. When true, the engine creates
    /// new <c>MapNode</c>s as the player explores; when false it operates in
    /// lookup-only mode and signals <c>RoomNotFoundInZone</c> instead.
    /// </summary>
    [Reactive] public bool   AutoCreateEnabled { get; set; }

    // ── Editor state (Genie 4 AutoMapper edit toolbar parity) ─────────────
    /// <summary>Master toggle: when on, the canvas selects/drags nodes and the
    /// edit toolbar + node-properties panel appear.</summary>
    [Reactive] public bool   EditMode      { get; set; }

    /// <summary>Snap dragged nodes to the grid (always effectively on — the
    /// Genie 4 format stores 20px multiples; see MapCanvas).</summary>
    [Reactive] public bool   SnapToGrid    { get; set; } = true;

    /// <summary>Lock node positions so a drag can't nudge a clean map.</summary>
    [Reactive] public bool   LockPositions { get; set; }

    /// <summary>Genie 4 "Allow Duplicate" — mirror to
    /// <see cref="AutoMapperEngine.AllowDuplicateRooms"/>.</summary>
    [Reactive] public bool   AllowDuplicate { get; set; }

    /// <summary>Show the map's <c>&lt;label&gt;</c> text (landmark names like
    /// "East Gate", "Guard House"). On by default; the toolbar "Labels" toggle
    /// hides them for a cleaner view. Bound to
    /// <see cref="Controls.MapCanvas.FullLabels"/>.</summary>
    [Reactive] public bool   FullLabels    { get; set; } = true;

    /// <summary>The node selected in the canvas (edit mode). Two-way bound.</summary>
    [Reactive] public MapNode? SelectedNode { get; set; }

    /// <summary>True when the active zone has unsaved edits. Drives the Save
    /// button's enabled state + a "● unsaved" hint.</summary>
    [Reactive] public bool   IsZoneDirty   { get; private set; }

    // Editable mirrors of the selected node's fields (Edit Panel). The user
    // types here, then Apply pushes them back into the live node.
    [Reactive] public string SelNodeTitle    { get; set; } = "";
    [Reactive] public string SelNodeNotes    { get; set; } = "";
    [Reactive] public string SelNodeColor    { get; set; } = "";
    [Reactive] public string SelNodeServerId { get; set; } = "";

    // ── Editor commands ───────────────────────────────────────────────────
    /// <summary>Create a fresh empty zone in the engine (Genie 4 "New").</summary>
    public ReactiveCommand<Unit, Unit> NewZoneCommand        { get; }
    /// <summary>Save the active zone XML (Genie 4 "Save"). Derives a filename
    /// from the zone name for brand-new zones.</summary>
    public ReactiveCommand<Unit, Unit> SaveMapCommand        { get; }
    /// <summary>Delete the selected node (Genie 4 "Remove Selected").</summary>
    public ReactiveCommand<Unit, Unit> RemoveSelectedCommand { get; }
    /// <summary>Renumber node ids to a dense 1..N (Genie 4 "Reset Map IDs").</summary>
    public ReactiveCommand<Unit, Unit> ResetMapIdsCommand    { get; }
    /// <summary>Push the Edit-Panel fields back into the selected node.</summary>
    public ReactiveCommand<Unit, Unit> ApplyNodePropsCommand { get; }
    /// <summary>Invoked by the canvas after a node drag completes — mark dirty
    /// and repaint.</summary>
    public ReactiveCommand<MapNode, Unit> NodeMovedCommand   { get; }
    /// <summary>Delete a specific node — invoked by the canvas (Remove Room
    /// context item / Delete key) with the target node.</summary>
    public ReactiveCommand<MapNode, Unit> RemoveNodeCommand  { get; }

    // ── Zone selection ────────────────────────────────────────────────────
    /// <summary>Zone filenames (no extension) found in <see cref="MapsDirectory"/>.</summary>
    public ObservableCollection<string> AvailableZones { get; } = new();

    /// <summary>Currently picked zone filename (no extension). Setting this loads it.</summary>
    [Reactive] public string? SelectedZoneFile { get; set; }

    /// <summary>Live status from the last load attempt — empty on success.</summary>
    [Reactive] public string  LoadStatus { get; private set; } = "";

    /// <summary>Re-scan <see cref="MapsDirectory"/> for *.json zone files.</summary>
    public ReactiveCommand<Unit, Unit> RefreshZonesCommand { get; }

    // ── Zone-list sort order ──────────────────────────────────────────────
    // Display labels + persisted keys, index-aligned. display.json stores the
    // KEY ("name"/"recent"/"number") so the UI wording can change freely.
    private static readonly string[] ZoneSortLabels = { "Name", "Recently Changed", "Map Number" };
    private static readonly string[] ZoneSortKeys   = { "name", "recent", "number" };

    /// <summary>Choices for the sort dropdown next to the zone selector.</summary>
    public IReadOnlyList<string> ZoneSortModes => ZoneSortLabels;

    /// <summary>Selected sort label (two-way from the dropdown). Changing it
    /// re-sorts <see cref="AvailableZones"/> and persists to display.json.</summary>
    [Reactive] public string ZoneSortMode { get; set; } = "Name";

    /// <summary>
    /// True when <paramref name="zoneFile"/> does NOT follow the standard
    /// <c>Map&lt;number&gt;…</c> naming scheme — i.e. it's a "special" map
    /// (event zones like Hollow_Eve or Droughtman's_Maze). Drives the SPECIAL
    /// badge in the zone dropdown and the bottom-group placement in Map Number
    /// sort mode.
    /// </summary>
    public static bool IsSpecialMapName(string zoneFile)
        => !MapNumberRx.IsMatch(zoneFile);

    // ── Map update (GenieClient/Maps repo) ────────────────────────────────
    /// <summary>True while <see cref="UpdateMapsCommand"/> is running.</summary>
    [Reactive] public bool   IsUpdating       { get; private set; }

    /// <summary>Live status line — current filename + step. Cleared once idle.</summary>
    [Reactive] public string UpdateStatus     { get; private set; } = "";

    /// <summary>Result summary shown after the last update completes (or fails).</summary>
    [Reactive] public string UpdateSummary    { get; private set; } = "";

    /// <summary>Absolute path to the Maps directory the next update will write into.</summary>
    [Reactive] public string MapsDirectory    { get; set; } = "";

    /// <summary>
    /// True when <see cref="MapsDirectory"/> contains a <c>.git</c> subfolder —
    /// signals the user is pointing Genie at a git working copy of the Maps
    /// repo. Purely informational; the app never runs git commands itself.
    /// Recomputed whenever MapsDirectory changes and after every Update Maps.
    /// </summary>
    [Reactive] public bool   IsGitManaged     { get; private set; }

    /// <summary>
    /// Pulls every zone XML from <c>github.com/GenieClient/Maps</c>, imports
    /// each into our JSON zone format, and merges with any existing zone of
    /// the same name (preserving locally-collected <c>ServerRoomId</c>s).
    /// </summary>
    public ReactiveCommand<Unit, Unit> UpdateMapsCommand { get; }

    /// <summary>
    /// Detach the Mapper into its own floating window. Wired by
    /// <c>MainWindowViewModel</c> to <c>GenieDockFactory.FloatTool("mapper")</c>
    /// so the VM doesn't need a direct reference to the factory.
    /// </summary>
    public ReactiveCommand<Unit, Unit> FloatCommand     { get; }

    /// <summary>Cycle the graphical canvas's Z-level filter up by one.</summary>
    public ReactiveCommand<Unit, Unit> LevelUpCommand   { get; }
    /// <summary>Cycle the graphical canvas's Z-level filter down by one.</summary>
    public ReactiveCommand<Unit, Unit> LevelDownCommand { get; }

    /// <summary>Multiply <see cref="ZoomLevel"/> by 1.2 (1 wheel notch in).</summary>
    public ReactiveCommand<Unit, Unit> ZoomInCommand    { get; }
    /// <summary>Divide <see cref="ZoomLevel"/> by 1.2 (1 wheel notch out).</summary>
    public ReactiveCommand<Unit, Unit> ZoomOutCommand   { get; }
    /// <summary>Reset zoom to 1.0 (native).</summary>
    public ReactiveCommand<Unit, Unit> ZoomResetCommand { get; }

    /// <summary>
    /// Walk the player from <see cref="ActiveNode"/> to the clicked target
    /// using <see cref="AutoMapperEngine.FindPath"/>. Each move is sent through
    /// <see cref="CommandEngine.ProcessInput"/> so aliases / triggers / paces
    /// still apply. No-op when there's no current node or no path.
    /// </summary>
    public ReactiveCommand<MapNode, Unit> GotoNodeCommand { get; }

    /// <summary>Left-click on a cross-zone (blue-border) room: switch the mapper
    /// to the connecting map named in the room's note, selecting the reciprocal
    /// border room there. Bound to <see cref="Controls.MapCanvas"/>'s
    /// CrossZoneClickedCommand.</summary>
    public ReactiveCommand<MapNode?, Unit> OpenCrossZoneCommand { get; }

    /// <summary>
    /// True while the user is BROWSING a zone their character isn't placed in
    /// (a manual dropdown pick or a cross-zone click that lands away from the
    /// character). While set, the auto-follow reload paths are suspended so
    /// live room events can't yank the view back — a running travel script
    /// generates a room change every few seconds, which made cross-zone clicks
    /// on Map998_Transports look completely dead (2026-08-04 live smoke: the
    /// click DID load the target map; auto-follow re-loaded Transports in the
    /// same beat). Cleared the moment the engine places the character in the
    /// browsed zone (walked into it, or the user re-picked the character's
    /// actual map and the room resolved).
    /// </summary>
    [Reactive] public bool BrowsingZone { get; private set; }

    // True while an ENGINE-driven zone switch (boundary-note follow /
    // room-search auto-load) is assigning SelectedZoneFile — distinguishes
    // those from USER picks so LoadSelectedZone knows which loads may enter
    // browse mode. Scoped set/finally around the assignment; the WhenAnyValue
    // subscription runs synchronously on the same thread.
    private bool _autoZoneSwitch;

    /// <summary>Toolbar "⌖ Return to Current Zone" — only visible while
    /// <see cref="BrowsingZone"/>. Releases the browse-hold and jumps the view
    /// back to the character's zone.</summary>
    public ReactiveCommand<Unit, Unit> ReturnToCurrentZoneCommand { get; }

    private void ReturnToCurrentZone()
    {
        if (_engine is null) return;
        BrowsingZone = false;
        LoadStatus = "Returning to your character's zone…";

        // Primary: jump straight back to the zone the character last MATCHED
        // in (tracked on every CurrentNodeChanged). Re-deriving the zone from
        // the live room can't handle exits-less rooms, and the auto-load
        // dedupe would swallow the retry anyway.
        if (!string.IsNullOrEmpty(_lastMatchedZoneFile) &&
            !string.Equals(_lastMatchedZoneFile, SelectedZoneFile, StringComparison.OrdinalIgnoreCase) &&
            AvailableZones.Contains(_lastMatchedZoneFile))
        {
            _autoZoneSwitch = true;   // returning-to-follow, not browsing
            try { SelectedZoneFile = _lastMatchedZoneFile; }
            finally { _autoZoneSwitch = false; }
            _engine.Recalculate();
            return;
        }

        // Fallback (character never matched this session, or we're already on
        // their zone): re-arm the auto-load dedupe and re-evaluate the live
        // room — a miss fires RoomNotFoundInZone whose auto-load (no longer
        // suspended) tries the server-id / fingerprint tiers, and reports
        // "No local zone contains …" when the room genuinely can't be placed.
        _lastAutoLoadAttempt = null;
        _engine.Recalculate();
    }

    // The zone file the character most recently MATCHED a room in — the
    // "home" that ⌖ Return to Current Zone restores. Distinct from
    // SelectedZoneFile, which follows whatever map is DISPLAYED (browsing
    // included) — conflating the two was the original return-button bug.
    private string? _lastMatchedZoneFile;

    // Server room id at the moment the browse-hold engaged; a room-change past
    // it releases the hold (browsing is stationary-only — see the
    // RoomNotFoundInZone handler).
    private string? _browseHoldRoomId;

    // True while LoadSelectedZone runs a USER-initiated load — mutes the
    // RoomNotFoundInZone artifact the load itself fires (see the handler).
    private bool _loadingZone;

    /// <summary>
    /// Send a non-compass move command (e.g. "go small alleyway", "climb
    /// trellis") via the command pipeline. Invoked when the user clicks a
    /// button in the Less Obvious Paths strip. CommandEngine handles aliases,
    /// scripts, and roundtime queueing exactly as if they had typed it.
    /// </summary>
    public ReactiveCommand<string, Unit> WalkLessObviousCommand { get; }

    /// <summary>
    /// Send a compass direction (e.g. "north", "northeast", "down") via the
    /// command pipeline. Invoked when the user clicks a button in the Obvious
    /// Exits strip — same code path as <see cref="WalkLessObviousCommand"/>;
    /// kept separate only so the two surfaces can use distinct command
    /// instances if we ever want different CanExecute gates (e.g. disable
    /// compass clicks during roundtime).
    /// </summary>
    public ReactiveCommand<string, Unit> WalkCompassCommand     { get; }

    /// <summary>
    /// Set by the main window VM at startup. Invoked when the user clicks
    /// "Pop out to window" / "Float Mapper Window". Left as a delegate (rather
    /// than an event) so wiring is a single assignment.
    /// </summary>
    public Action? FloatRequested { get; set; }

    private AutoMapperEngine?  _engine;
    private GenieCore?         _core;
    private Genie.Core.Diagnostics.LiveAudit? _audit;
    private MapZoneRepository? _zoneRepo;
    private ZoneRoomIndex?     _roomIndex;   // whole-Maps room index for cross-zone #goto (lazy)
    private IReadOnlyList<ZoneConnection>? _derivedConnections;  // cross-zone links derived from map notes (lazy)
    private CommandEngine?     _commands;
    private DisplaySettings?   _display;
    private bool               _suppressAutoLoad;
    private string?            _displayPath;

    /// <summary>
    /// Drives step-by-step auto-walk when the user picks "Go Here" on a
    /// map node. Null before <see cref="Attach"/> runs; non-null once
    /// the GenieCore is wired. Surface this on the panel so XAML can
    /// bind the "Walking to X — N rooms left" indicator to
    /// <c>AutoWalk.Current</c>. Reactive so those bindings re-resolve when
    /// Attach assigns it — a plain property left them stuck on the failed
    /// pre-attach resolution, which renders as a phantom walk strip (#165).
    /// </summary>
    [Reactive] public AutoWalkService? AutoWalk { get; private set; }

    /// <summary>Cached reference to the live SkillStore — used by
    /// <see cref="MaybeShowSkillsPrompt"/> to decide whether to surface
    /// the banner. Null before Attach.</summary>
    private Genie.Core.Skills.SkillStore? _skillStore;

    /// <summary>
    /// True when the "Fetch your skills?" banner should be visible above
    /// the map canvas. Becomes true when (1) we're connected, (2) a zone
    /// has loaded, (3) the live SkillStore has no rank data yet, and
    /// (4) the user hasn't ticked "Don't ask again." Auto-flips false
    /// when the SkillStore receives its first rank (skill `info` reply
    /// is arriving).
    /// </summary>
    [Reactive] public bool ShowSkillsPrompt { get; private set; }

    /// <summary>
    /// Sends <c>skills</c> through the command pipeline so DR returns
    /// the full skill component dump. The parser's
    /// <c>&lt;component id='exp X'&gt;</c> hook then fills the
    /// SkillStore. Surface this on the Mapper banner.
    /// </summary>
    public ReactiveCommand<Unit, Unit> FetchSkillsCommand { get; }

    /// <summary>
    /// Dismiss the prompt for this session only. Banner hides; will
    /// prompt again on next launch (unless DontAskAgain is also set).
    /// </summary>
    public ReactiveCommand<Unit, Unit> DismissSkillsPromptCommand { get; }

    /// <summary>
    /// Dismiss + persist "don't ask again" to DisplaySettings. Banner
    /// stays hidden permanently for this character.
    /// </summary>
    public ReactiveCommand<Unit, Unit> DontAskAboutSkillsCommand { get; }

    /// <summary>
    /// Raised when the user wants to open the Edit Exit dialog for a
    /// specific exit. The host (MainWindowViewModel) wires this to the
    /// <see cref="MainWindowViewModel.ShowEditExitDialog"/> Interaction.
    /// Done as an event rather than a Reactive command because the
    /// payload is (node, exit) tuples that don't compose cleanly with
    /// ReactiveCommand's single-T input.
    /// </summary>
    public event Action<MapNode, MapExit>? EditExitRequested;

    /// <summary>
    /// Public entry point used by the MapCanvas right-click handler —
    /// when the user picks "Edit Exit ▶ {verb}" the canvas calls this.
    /// We forward the event so the host can open the dialog; on save,
    /// the host persists the zone XML via SaveCurrentZone().
    /// </summary>
    public void RequestEditExit(MapNode node, MapExit exit)
        => EditExitRequested?.Invoke(node, exit);

    /// <summary>
    /// Re-evaluate whether the "Fetch your skills?" banner should be
    /// shown. Called on zone-load (we now have a useful map to walk)
    /// and whenever the user clicks Goto without skill data. Decision
    /// rule: show iff persistently-dismissed flag is false AND the
    /// SkillStore has no rank data yet AND we're actually connected
    /// (avoids prompting on dev-replay / pre-connect).
    /// </summary>
    private void MaybeShowSkillsPrompt(Genie.Core.GenieCore core)
    {
        if (_display?.SkillsPromptDismissed == true) return;
        if (_skillStore is null) return;
        if (_skillStore.Snapshot().Count > 0) return;
        // Don't prompt if not connected — no point sending `skills` if
        // we have no socket to send through.
        if (_commands is null) return;
        ShowSkillsPrompt = true;
    }

    /// <summary>
    /// Reactive command bound to <c>MapCanvas.EditExitCommand</c>. Takes
    /// a (MapNode, MapExit) tuple — the canvas builds the tuple when the
    /// user picks an exit from the right-click "Edit Exit ▶" submenu.
    /// Initialized in the constructor (not field init) so the closure
    /// can reference <see cref="RequestEditExit"/>.
    /// </summary>
    public ReactiveCommand<(MapNode node, MapExit exit), Unit> EditExitCommand { get; private set; } = null!;

    /// <summary>
    /// Persist the active zone to disk after an Edit Exit save. Called
    /// by the host's edit-exit dialog handler once the dialog returns
    /// "ok". Defensive guards so a failed edit doesn't clobber a zone
    /// the user didn't intend to write.
    /// </summary>
    public void SaveCurrentZone()
    {
        if (_engine is null || _zoneRepo is null) return;
        if (string.IsNullOrEmpty(SelectedZoneFile)) return;
        if (string.IsNullOrEmpty(MapsDirectory)) return;

        var path = Path.Combine(MapsDirectory, SelectedZoneFile + ".xml");
        try
        {
            _zoneRepo.Save(path, _engine.ActiveZone);
            ZoneLastWriteTime = File.GetLastWriteTime(path);
            RefreshZoneAge();
            LoadStatus = "Zone saved.";
            RenderTick++;
        }
        catch (Exception ex)
        {
            LoadStatus = $"Save failed: {ex.Message}";
        }
    }

    // ── #mapper subcommand helpers (#146) ────────────────────────────────────

    /// <summary>Reload the active zone from disk, discarding unsaved in-memory
    /// changes (<c>#mapper load</c>). Returns the zone display name, or null when
    /// nothing is loaded.</summary>
    public string? ReloadActiveZone()
    {
        if (string.IsNullOrEmpty(SelectedZoneFile)) return null;
        LoadSelectedZone(SelectedZoneFile);     // re-reads the file into the engine
        return ZoneName;
    }

    /// <summary>Clear the active zone's rooms + labels in memory, keeping its
    /// name/id (<c>#mapper clear</c>). Not written until <c>#mapper save</c>, so
    /// an accidental clear is recoverable with <c>#mapper load</c>.</summary>
    public void ClearActiveZone()
    {
        if (_engine is null) return;
        var cur = _engine.ActiveZone;
        _engine.LoadZone(new Genie.Core.Mapper.MapZone { Name = cur.Name, Genie4Id = cur.Genie4Id });
        LoadStatus = "Zone cleared (in memory — #mapper save to persist).";
        RenderTick++;
    }

    /// <summary>Switch the loaded zone to one whose file matches <paramref name="idOrName"/>
    /// (<c>#mapper zone &lt;id|name&gt;</c>) — exact filename first, then a
    /// case-insensitive contains. Returns false when nothing matches.</summary>
    public bool SwitchZone(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return false;
        var match = AvailableZones.FirstOrDefault(z => z.Equals(idOrName, StringComparison.OrdinalIgnoreCase))
                 ?? AvailableZones.FirstOrDefault(z => z.IndexOf(idOrName, StringComparison.OrdinalIgnoreCase) >= 0);
        if (match is null) return false;
        SelectedZoneFile = match;               // triggers LoadSelectedZone via WhenAnyValue
        return true;
    }

    /// <summary>
    /// server-room-id → zone-file (no extension). Populated in the background
    /// by <see cref="RebuildServerIdIndexAsync"/> after Attach and after every
    /// successful Update Maps run. Drives the auto-zone-detect behaviour that
    /// fires whenever the engine can't match the player's current room in the
    /// loaded zone (which is the default state at connect time).
    /// </summary>
    private volatile Dictionary<string, string> _serverIdToZoneFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// title+exits fingerprint → zone-file. Fallback index used when the
    /// server-id lookup misses — typically because the zone XMLs are imported
    /// from a Genie 4 install that predates the <c>server_id</c> attribute
    /// extension. Built from every node in every local zone, just like the
    /// engine's own internal fingerprint index but scoped across zones.
    /// </summary>
    private volatile Dictionary<string, string> _fingerprintToZoneFile =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last cache key we attempted an auto-load for. Stops the engine's
    /// RoomNotFoundInZone events (which can fire on every room change while
    /// the wrong zone is loaded) from repeatedly trying to reload the same
    /// missing room. Composed of "id|fingerprint" so a change in either
    /// re-arms the attempt.
    /// </summary>
    private string? _lastAutoLoadAttempt;

    public MapperViewModel()
    {
        // EditExitCommand fans out to the EditExitRequested event so the
        // host can open the dialog (App layer owns dialog show; VM stays
        // UI-free). Initialised here (not field-init) so the closure can
        // see `this.RequestEditExit`.
        EditExitCommand = ReactiveCommand.Create<(MapNode node, MapExit exit), Unit>(tuple =>
        {
            RequestEditExit(tuple.node, tuple.exit);
            return Unit.Default;
        });

        // Wire the command's CanExecute to "not currently running AND have a
        // maps dir AND have a repo". The dir + repo are set by Attach(core).
        var canRun = this.WhenAnyValue(
            x => x.IsUpdating,
            x => x.MapsDirectory,
            (busy, dir) => !busy && !string.IsNullOrWhiteSpace(dir) && _zoneRepo is not null);

        UpdateMapsCommand = ReactiveCommand.CreateFromTask(UpdateMapsAsync, canRun);

        // Surface failures as a user-visible status line instead of swallowing.
        UpdateMapsCommand.ThrownExceptions.Subscribe(ex =>
        {
            IsUpdating    = false;
            UpdateStatus  = "";
            UpdateSummary = $"Update failed: {ex.Message}";
        });

        FloatCommand = ReactiveCommand.Create(() => FloatRequested?.Invoke());
        FloatCommand.ThrownExceptions.Subscribe(ex =>
            UpdateSummary = $"Float failed: {ex.Message}");

        // Lambdas wrapped to return Unit so the inferred command type is
        // ReactiveCommand<Unit, Unit> rather than <Unit, int>.
        LevelUpCommand   = ReactiveCommand.Create(() => { Level++; });
        LevelDownCommand = ReactiveCommand.Create(() => { Level--; });

        ZoomInCommand    = ReactiveCommand.Create(() => { ZoomLevel *= 1.2; });
        ZoomOutCommand   = ReactiveCommand.Create(() => { ZoomLevel /= 1.2; });
        ZoomResetCommand = ReactiveCommand.Create(() => { ZoomLevel  = 1.0; });

        // ── Editor commands ───────────────────────────────────────────────
        NewZoneCommand        = ReactiveCommand.Create(NewZone);
        SaveMapCommand        = ReactiveCommand.Create(SaveMap);
        RemoveSelectedCommand = ReactiveCommand.Create(RemoveSelected);
        ResetMapIdsCommand    = ReactiveCommand.Create(ResetMapIds);
        ApplyNodePropsCommand = ReactiveCommand.Create(ApplyNodeProps);
        NodeMovedCommand      = ReactiveCommand.Create<MapNode>(_ => { IsZoneDirty = true; RenderTick++; });
        RemoveNodeCommand     = ReactiveCommand.Create<MapNode>(RemoveNodeById);

        foreach (var c in new IReactiveCommand[]
                 { NewZoneCommand, SaveMapCommand, RemoveSelectedCommand,
                   ResetMapIdsCommand, ApplyNodePropsCommand, NodeMovedCommand, RemoveNodeCommand })
            c.ThrownExceptions.Subscribe(ex => LoadStatus = $"Editor error: {ex.Message}");

        // Mirror the selected node's fields into the editable Edit-Panel
        // properties whenever the selection changes (canvas sets SelectedNode
        // via its two-way binding).
        this.WhenAnyValue(x => x.SelectedNode).Subscribe(_ => MirrorSelectedNode());

        RefreshZonesCommand = ReactiveCommand.Create(RefreshAvailableZones);
        RefreshZonesCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Refresh failed: {ex.Message}");

        GotoNodeCommand = ReactiveCommand.Create<MapNode>(GotoNode);
        GotoNodeCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Goto failed: {ex.Message}");

        OpenCrossZoneCommand = ReactiveCommand.Create<MapNode?>(OpenCrossZone);
        OpenCrossZoneCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Zone switch failed: {ex.Message}");

        ReturnToCurrentZoneCommand = ReactiveCommand.Create(ReturnToCurrentZone);
        ReturnToCurrentZoneCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Return failed: {ex.Message}");

        // Mirror the browse-hold into the engine so Core consumers freeze
        // character-scoped globals ($zoneid/$roomid/…) while the view shows a
        // map the character isn't in — browsing must never rewrite script
        // state (2026-08-06: `#echo $zoneid` read 998 from the browsed
        // Transports map while the character stood in Dirge).
        this.WhenAnyValue(x => x.BrowsingZone)
            .Subscribe(b => { if (_engine is not null) _engine.ViewIsBrowsing = b; });

        // CanExecute: only when there's a current room AND a loaded zone
        // file. The button stays disabled when the user can't possibly
        // have anything useful to save.
        var canSaveNotes = this.WhenAnyValue(
            x => x.ActiveNode, x => x.SelectedZoneFile,
            (node, file) => node is not null && !string.IsNullOrEmpty(file));
        SaveNotesCommand = ReactiveCommand.Create(SaveNotes, canSaveNotes);
        SaveNotesCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Save notes failed: {ex.Message}");

        WalkLessObviousCommand = ReactiveCommand.Create<string>(cmd =>
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            _commands?.ProcessInput(cmd);
        });
        WalkLessObviousCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Walk failed: {ex.Message}");

        WalkCompassCommand = ReactiveCommand.Create<string>(cmd =>
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            _commands?.ProcessInput(cmd);
        });
        WalkCompassCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Walk failed: {ex.Message}");

        // ── Skills-prompt commands ────────────────────────────────────────
        // Created HERE (constructor), not in Attach(core). These properties are
        // not [Reactive], so assigning them in Attach — which runs AFTER the
        // Mapper banner's Command bindings have already evaluated — left the
        // bindings stuck on null and the three buttons permanently greyed out
        // (a null Command disables an Avalonia Button). The lambdas read
        // _commands / _display / _displayPath at click time (null-safe), exactly
        // like WalkCompassCommand above, so nothing here needs Attach to have run.
        // The skill-weighted pathfinder — and the Edit Exit dialog's Guild /
        // skill gating — work best with the character's guild, circle, and full
        // skill ranks. DR surfaces guild + circle via `info` and the complete
        // skill-rank set via `exp all`; rather than auto-firing on connect
        // (verb-spam), the banner asks the user once. (Wiring that decides WHEN
        // to show the banner stays in Attach, where `core` is available.)
        FetchSkillsCommand = ReactiveCommand.Create(() =>
        {
            _commands?.ProcessInput("info");      // guild + circle → class / level gating
            _commands?.ProcessInput("exp all");   // all skill ranks → skill gating
            ShowSkillsPrompt = false;   // hide immediately; data will arrive shortly
        });
        FetchSkillsCommand.ThrownExceptions.Subscribe(ex =>
            LoadStatus = $"Fetch skills failed: {ex.Message}");

        DismissSkillsPromptCommand = ReactiveCommand.Create(() =>
        {
            ShowSkillsPrompt = false;
        });

        DontAskAboutSkillsCommand = ReactiveCommand.Create(() =>
        {
            ShowSkillsPrompt = false;
            if (_display is not null)
            {
                _display.SkillsPromptDismissed = true;
                if (!string.IsNullOrEmpty(_displayPath))
                    _display.Save(_displayPath);
            }
        });

        // Auto-load the zone when the user picks one from the dropdown. Skip(1)
        // ignores the initial null emission; the _suppressAutoLoad guard lets
        // RefreshAvailableZones re-select a value (after a directory rescan)
        // without triggering a redundant reload.
        this.WhenAnyValue(x => x.SelectedZoneFile)
            .Skip(1)
            .Subscribe(LoadSelectedZone);

        // Re-sort the zone list when the user changes the sort dropdown, and
        // persist the choice. Skip(1) ignores the construction-time emission —
        // Attach() does the first RefreshAvailableZones, and AttachDisplay may
        // set this again from the stored value. RefreshAvailableZones preserves
        // the current selection, so re-sorting never unloads the active zone.
        this.WhenAnyValue(x => x.ZoneSortMode)
            .Skip(1)
            .Subscribe(_ =>
            {
                PersistZoneSort();
                RefreshAvailableZones();
            });

        // The status-bar location line OAPH (#66) is wired in AttachDisplay,
        // where the DisplaySettings are available — its Zone-name-vs-number mode
        // is a user setting (Display.ZoneRoomShowNumber).

        // Whenever the Maps directory changes, recompute the "git-managed"
        // hint. This is purely informational — Genie never runs git itself,
        // but showing the user that they're pointed at a working copy is a
        // small reassurance that their commits go where they expect.
        this.WhenAnyValue(x => x.MapsDirectory)
            .Subscribe(_ => RecomputeIsGitManaged());

        // Derive MapBackgroundBrush from MapBackground. Persist the hex on
        // each change so the choice survives restart.
        this.WhenAnyValue(x => x.MapBackground)
            .Subscribe(c =>
            {
                MapBackgroundBrush = new SolidColorBrush(c);
                if (_display is not null)
                {
                    var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    if (!string.Equals(_display.MapBackgroundHex, hex, StringComparison.OrdinalIgnoreCase))
                    {
                        _display.MapBackgroundHex = hex;
                        if (!string.IsNullOrEmpty(_displayPath))
                        {
                            try   { _display.Save(_displayPath); }
                            catch { /* persistence failure isn't fatal */ }
                        }
                    }
                }
            });

        // Same pattern for the label text colour.
        this.WhenAnyValue(x => x.MapTextColor)
            .Subscribe(c =>
            {
                MapTextBrush = new SolidColorBrush(c);
                if (_display is not null)
                {
                    var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    if (!string.Equals(_display.MapTextHex, hex, StringComparison.OrdinalIgnoreCase))
                    {
                        _display.MapTextHex = hex;
                        if (!string.IsNullOrEmpty(_displayPath))
                        {
                            try   { _display.Save(_displayPath); }
                            catch { /* persistence failure isn't fatal */ }
                        }
                    }
                }
            });
    }

    /// <summary>
    /// Hand the VM its persistent visual settings + the on-disk path. Called
    /// from <c>MainWindowViewModel</c> once <see cref="DisplaySettings"/> has
    /// loaded. Pre-seeds <see cref="MapBackground"/> from the stored hex and
    /// keeps the two in sync from then on.
    /// </summary>
    public void AttachDisplay(DisplaySettings display, string displayPath)
    {
        _display     = display;
        _displayPath = displayPath;
        this.RaisePropertyChanged(nameof(ShowMapLegend));   // reflect the stored value (#157)

        // Restore the zone-list sort order. Setting the property (when it
        // differs from the default) triggers the ctor subscription, which
        // re-sorts the list; PersistZoneSort is a no-op since the stored key
        // already matches.
        var sortIdx = Array.IndexOf(ZoneSortKeys, display.MapZoneSort);
        if (sortIdx >= 0) ZoneSortMode = ZoneSortLabels[sortIdx];

        // One-time migration: the old default canvas was dark (#1A1A1A). Genie 4's
        // AutoMapper uses a PaleGoldenrod (tan) canvas, which the map palette
        // (black cardinal lines, blue cross-zone borders) is designed for. Treat
        // the exact old default as "unset" and move it to the tan default so
        // existing users get the Genie 4 look without losing a genuinely custom
        // colour they picked. Idempotent — re-running it changes nothing.
        if (string.Equals(display.MapBackgroundHex, "#1A1A1A", StringComparison.OrdinalIgnoreCase))
            display.MapBackgroundHex = "#EEE8AA";

        if (Color.TryParse(display.MapBackgroundHex, out var c))
            MapBackground = c;
        if (Color.TryParse(display.MapTextHex, out var tc))
            MapTextColor = tc;

        // Status-bar location line (#66). Zone field follows the user's
        // Zone-name-vs-number setting; Room is always $roomid (the mapper node
        // id), never the server $gameroomid. Wired here (not the constructor) so
        // it can react to the DisplaySettings toggle. Idempotent: the OAPH is
        // built once even if AttachDisplay is somehow called again.
        _locationDisplay ??= this
            .WhenAnyValue(x => x.ZoneName, x => x.CurrentZoneId, x => x.CurrentRoomId)
            .CombineLatest(
                display.WhenAnyValue(d => d.ZoneRoomShowNumber),
                (parts, showNumber) =>
                {
                    var (name, zoneId, roomId) = parts;
                    var zone = showNumber ? zoneId : name;
                    return $"Zone: {zone}   Room: {roomId}";
                })
            .ToProperty(this, x => x.LocationDisplay);
    }

    private void RecomputeIsGitManaged()
    {
        IsGitManaged = !string.IsNullOrWhiteSpace(MapsDirectory) &&
                       Directory.Exists(Path.Combine(MapsDirectory, ".git"));
    }

    public void Attach(GenieCore core)
    {
        _core     = core;
        _engine   = core.AutoMapper;
        _zoneRepo = core.ZoneRepository;
        _commands = core.Commands;

        // Tee the auto-load diagnostic (LoadStatus) into the Live Audit log so a
        // zone-edge stall shows WHY ("No local zone contains…" / "Engine can't
        // match…") inline with the raw stream. Note() is a no-op when off.
        _audit = core.Audit;
        this.WhenAnyValue(x => x.LoadStatus)
            .Subscribe(s => { if (!string.IsNullOrEmpty(s)) _audit?.Note("MAP", s); });

        // Ghost-floor opacity for the multi-level map view (Genie 4 parity).
        AutoMapperAlpha = core.Config.AutoMapperAlpha;

        // Auto-walk runs through the same command pipeline as user input
        // (alias expansion / RT gating). The service owns the session
        // state machine + cancellation surfaces; we just hand it the
        // engine + the GenieCore for command dispatch.
        AutoWalk = new Services.AutoWalkService(core, _engine);

        // Cancel any in-flight walk on disconnect — per the compliance
        // review, we never auto-resume across sessions.
        core.ConnectionState
            .Where(s => s.Kind == Genie.Core.Events.ConnectionEventKind.Disconnected
                     || s.Kind == Genie.Core.Events.ConnectionEventKind.Error)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => AutoWalk.Cancel("connection lost"));

        // ── Skills prompt wiring ──────────────────────────────────────
        // The Fetch / Skip / Don't-ask commands themselves are created in the
        // constructor (so the banner's bindings resolve non-null at view-load
        // time — see the comment there). Here we only wire WHEN the banner
        // shows/hides, which needs `core`.

        // Show the prompt when a zone is first loaded AND we have no
        // skill data yet AND the user hasn't permanently dismissed it.
        // Re-evaluates whenever ActiveZone changes.
        this.WhenAnyValue(x => x.ActiveZone)
            .Where(z => z is not null)
            .Subscribe(_ => MaybeShowSkillsPrompt(core));

        // Hide the prompt the moment skill data starts arriving.
        core.State.LiveSkills.Changed += () =>
            Dispatcher.UIThread.Post(() => ShowSkillsPrompt = false);

        _skillStore = core.State.LiveSkills;

        Refresh();

        // Dispatch to UI thread — engine events can fire from the parser
        // observable's thread.
        _engine.CurrentNodeChanged += () => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            // Live Audit: log the mapper's resolved room/zone on EVERY room
            // change — including "(**)" rooms that carry no server id and so
            // produce no NAV line. This is what reveals a cross-zone stall
            // (the mapper staying on the old zone past a boundary).
            var n = _engine?.CurrentNode;
            _audit?.Note("ROOM",
                $"node={(n is null ? "LOST" : n.Id.ToString())} zone='{_engine?.ActiveZone?.Name}' title='{n?.Title}'");
            // While BROWSING, a node match usually means nothing about the
            // character: boundary stubs duplicate their room into the browsed
            // map, so the ANCHOR room "matching" must not release the hold,
            // must not overwrite the remembered home zone, and must not let
            // the engine follow the stub's note and drag the view around.
            // Release paths: the character MOVING (RoomNotFoundInZone sees a
            // new server room id), the ⌖ Return button, the user re-selecting
            // the home zone (LoadSelectedZone's identity check) — and, below,
            // a match on a DIFFERENT room than the hold anchor.
            if (BrowsingZone)
            {
                // A match on a NEW server room means the character is moving
                // AND the browsed zone resolves their rooms — it IS their
                // zone; adopt it. Without this, browsing the character's own
                // zone latched forever (rooms kept matching, so the
                // RoomNotFoundInZone release never fired) and the hold-gated
                // SyncMapperGlobals starved: $roomid/$zoneid read 0 all
                // session while this handler's ungated Refresh() kept the
                // status line tracking (Shroom's #226 live verify; travel.cmd
                // reads $roomid=0 and starts MOVERANDOM).
                if (Services.BrowseHoldPolicy.ShouldReleaseOnMatch(
                        n is not null, _engine?.CurrentServerRoomId, _browseHoldRoomId))
                {
                    BrowsingZone = false;   // reactive mirror clears engine.ViewIsBrowsing
                    _audit?.Note("ROOM", "browse-hold released — new room matched in the browsed zone; adopting it as the character's zone");
                    _lastMatchedZoneFile = SelectedZoneFile;
                    // The match that proved residency ran under the hold, so
                    // the globals sync skipped it. Re-resolve with the hold
                    // off; the recalc re-enters this handler (posted) with
                    // BrowsingZone false and syncs $roomid/$zoneid normally.
                    _engine?.Recalculate();
                }
                return;
            }

            // Remember the zone the character last MATCHED in — this is what
            // "⌖ Return to Current Zone" jumps back to. Derivation-by-rematch
            // alone can't do it: an exits-less room ("Obvious exits: none",
            // e.g. Dirge's Temple Courtyard) has a fingerprint too weak for
            // the auto-load index (2026-08-06 smoke).
            if (n is not null) _lastMatchedZoneFile = SelectedZoneFile;
            MaybeFollowZoneNote(n);
        });
        _engine.MapChanged         += () => Dispatcher.UIThread.Post(Refresh);
        _engine.RoomNotFoundInZone += (serverId, title, exits) =>
        {
            // A user-initiated zone load is IN PROGRESS: this miss is an
            // artifact of loading a map the character isn't in, not of the
            // character moving. Acting on it here (the by-title boundary-stub
            // follow especially) snapped a fresh browse straight back to the
            // character's map before the browse-hold even engaged.
            if (_loadingZone) return;

            // Browse-hold: the user is deliberately looking at a different map.
            // Every live room event fires this handler while the loaded zone
            // doesn't contain the character (a moving ferry fires one every few
            // seconds), and the auto-load below would instantly yank the view
            // back — which made cross-zone clicks look dead during travel
            // (2026-08-04 smoke). Suspend following until the character shows
            // up in the browsed zone or the user returns to their map.
            if (BrowsingZone)
            {
                // Release the hold the moment the CHARACTER MOVES: the engine
                // can only match rooms in the loaded zone, so keeping the
                // suppression up while the character travels starves tracking
                // completely — no auto-load, no match, $zoneid frozen stale,
                // and travel.cmd's $roomid=0 branch starts MOVERANDOM
                // (2026-08-06 ferry ping-pong). Browsing is a STATIONARY
                // inspection mode; a new server room id means the character is
                // going places, so follow them again.
                if (!string.Equals(serverId, _browseHoldRoomId, StringComparison.OrdinalIgnoreCase))
                {
                    BrowsingZone = false;
                    _audit?.Note("MISS", "browse-hold released — character moved; resuming auto-follow");
                    // fall through to the normal auto-load below
                }
                else
                {
                    _audit?.Note("MISS", $"\"{title}\" not in browsed zone '{_engine?.ActiveZone?.Name}' — auto-load suspended (browsing)");
                    return;
                }
            }
            _audit?.Note("MISS", $"engine can't place \"{title}\" in '{_engine?.ActiveZone?.Name}' → trying auto-load");
            // First: a boundary stub in THIS zone with this title may name the
            // destination zone (the map's own cross-zone link) — definitive, no
            // fingerprint ambiguity. Fall back to the server-id/fingerprint
            // auto-detect only if there's no such note.
            if (TryFollowZoneNoteByTitle(title)) return;
            TryAutoLoadZoneFor(serverId, title, exits);
        };

        // Toggle binding → engine.IsEnabled. WhenAnyValue emits initial value
        // on subscribe; the engine starts disabled so we mirror that here.
        AutoCreateEnabled = _engine.IsEnabled;
        this.WhenAnyValue(x => x.AutoCreateEnabled)
            .Skip(1)   // ignore the initial emission; engine already matches
            .Subscribe(v => { if (_engine is not null) _engine.IsEnabled = v; });

        // Allow-duplicate mirror (Genie 4 parity) — same pattern.
        AllowDuplicate = _engine.AllowDuplicateRooms;
        this.WhenAnyValue(x => x.AllowDuplicate)
            .Skip(1)
            .Subscribe(v => { if (_engine is not null) _engine.AllowDuplicateRooms = v; });

        RefreshAvailableZones();

        // Build the server-id → zone-file index in the background. Reading
        // and JSON-parsing every zone file is non-trivial, so don't block
        // the UI thread — auto-detect will simply skip until the index lands.
        _ = RebuildServerIdIndexAsync();
    }

    /// <summary>
    /// Cross-zone transition via a boundary room's map note. Genie 4 maps
    /// annotate a shared boundary room with the adjacent zone file it belongs
    /// to, e.g. <c>note="Map7_Northern_Trade_Road.xml|NE Gate|NTR"</c>. When the
    /// mapper lands on such a node, switch to that zone — the same room is a
    /// full node there (with the real forward exits), so <c>$zoneid</c> advances
    /// and a <c>$zoneid</c>-driven script (travel.cmd) can continue across the
    /// boundary. This is the disambiguation a room shared by several maps needs:
    /// the map data itself names the destination zone, removing the ambiguity
    /// that title/fingerprint matching can't resolve.
    /// </summary>
    private void MaybeFollowZoneNote(MapNode? node)
    {
        // Engine-driven follow (the character walked onto a boundary node):
        // flag it so FollowZoneNote's zone switch doesn't read as browsing.
        _followingEngine = true;
        try { FollowZoneNote(node); }
        finally { _followingEngine = false; }
    }

    // True while FollowZoneNote runs on behalf of the ENGINE (character
    // movement) rather than a user action — see BrowsingZone.
    private bool _followingEngine;

    /// <summary>Switch zones if <paramref name="node"/>'s note names an adjacent
    /// zone file (and we're not already on it). Returns true when the note was
    /// handled (switched, or already on the noted zone) so callers can stop.</summary>
    private bool FollowZoneNote(MapNode? node)
    {
        if (node is null || string.IsNullOrEmpty(node.Notes)) return false;

        foreach (var token in node.Notes.Split('|'))
        {
            var t = token.Trim();
            if (!t.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

            var basename = Path.GetFileNameWithoutExtension(t);
            // Already on it — handled (also stops re-switch flapping).
            if (string.Equals(basename, SelectedZoneFile, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!AvailableZones.Contains(basename))
            {
                _audit?.Note("XZONE", $"boundary note → '{basename}' but that zone isn't available");
                return false;
            }

            _audit?.Note("XZONE", $"boundary note: switching '{SelectedZoneFile}' → '{basename}'");
            // Setting this triggers WhenAnyValue → LoadSelectedZone → engine
            // LoadZone + Recalculate, which re-resolves the current room in the
            // new zone. The re-resolved node is the room's home node and won't
            // carry a .xml note, so this fires once, not in a loop.
            // NOTE: the cross-zone CLICK reuses this path, so the auto flag is
            // only held when the ENGINE drove the switch (see MaybeFollowZoneNote
            // caller); OpenCrossZone is a user action and must be allowed to
            // enter browse mode via LoadSelectedZone's detection.
            _autoZoneSwitch = _followingEngine;
            try { SelectedZoneFile = basename; }
            finally { _autoZoneSwitch = false; }
            return true;
        }
        return false;
    }

    /// <summary>
    /// Left-click on a cross-zone room (blue border) in the map view: open the
    /// connecting map (smoke 2026-08-03, task #2). Reuses
    /// <see cref="FollowZoneNote"/>'s note-parse/availability/switch, then
    /// selects the reciprocal border room in the freshly-loaded zone — the node
    /// whose note points back at the zone we came from — so the eye lands on
    /// the connection point instead of an unanchored map. No reciprocal note
    /// (one-way annotation) just leaves the selection cleared by the load.
    /// </summary>
    private void OpenCrossZone(MapNode? node)
    {
        if (node is null) return;
        var from = SelectedZoneFile;
        if (!FollowZoneNote(node))
        {
            // Say WHY nothing happened — the availability miss inside
            // FollowZoneNote is audit-only, which made a failed click read as
            // a dead feature (2026-08-04 live smoke on Map998_Transports).
            var target = node.Notes?.Split('|')
                .Select(t => t.Trim())
                .FirstOrDefault(t => t.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            LoadStatus = target is null
                ? $"Room {node.Id} carries no linked-map note."
                : $"Linked map '{Path.GetFileNameWithoutExtension(target)}' isn't in the zone list.";
            return;
        }
        if (string.IsNullOrEmpty(from) || _engine?.ActiveZone?.Nodes is null)
            return;
        var back = from + ".xml";
        foreach (var n in _engine.ActiveZone.Nodes.Values)
            if (n.Notes.Contains(back, StringComparison.OrdinalIgnoreCase))
            {
                SelectedNode = n;
                break;
            }
    }

    /// <summary>MISS path: the engine couldn't place the live room in the active
    /// zone, but the active zone may still hold a boundary STUB node with this
    /// title whose note names the destination zone — follow it. Covers the case
    /// where you walk through a gate normally (no graph-walk match) rather than
    /// arriving at the boundary stub via an in-zone arc.</summary>
    private bool TryFollowZoneNoteByTitle(string title)
    {
        if (_engine?.ActiveZone?.Nodes is null || string.IsNullOrWhiteSpace(title)) return false;
        _followingEngine = true;
        try
        {
            foreach (var node in _engine.ActiveZone.Nodes.Values)
                if (string.Equals(node.Title, title, StringComparison.OrdinalIgnoreCase) && FollowZoneNote(node))
                    return true;
        }
        finally { _followingEngine = false; }
        return false;
    }

    /// <summary>
    /// Engine fired RoomNotFoundInZone — try to figure out which zone file
    /// contains that room and load it automatically. Tries in order:
    /// <list type="number">
    ///   <item><b>Server room id</b> — definitive but only works when the
    ///         local zones carry <c>server_id</c> attributes. Empty for any
    ///         zone freshly imported from a Genie 4 install.</item>
    ///   <item><b>Title + exits fingerprint</b> — fallback that works on
    ///         every zone since it just uses fields the engine already has.
    ///         Strong enough to disambiguate most rooms; rare collisions
    ///         (e.g. two zones with a "Town Square North" pointing the same
    ///         compass directions) will pick whichever zone the indexer
    ///         encountered first.</item>
    /// </list>
    /// Runs on the parser thread; the actual zone load is dispatched to the
    /// UI thread so the reactive properties update safely.
    /// </summary>
    private void TryAutoLoadZoneFor(string serverRoomId, string title, IReadOnlyCollection<string> exits)
    {
        var fingerprint = MapFingerprint.Compute(title, exits);
        var exitList    = exits.Count == 0 ? "(none)" : string.Join(", ", exits);

        if (_serverIdToZoneFile.Count == 0 && _fingerprintToZoneFile.Count == 0)
        {
            // Indexes haven't built yet — surface that so the user knows
            // the lookup wasn't silently skipped; subsequent room changes
            // will retry once the background scan completes.
            Dispatcher.UIThread.Post(() =>
                LoadStatus = $"Waiting for zone index (room: '{title}', exits: {exitList}).");
            return;
        }

        // Compose a dedupe key from BOTH inputs — we want a new id OR a
        // new fingerprint to re-arm the attempt, but the same room (same
        // id+fingerprint) firing every state-change should be silenced.
        var attemptKey  = $"{serverRoomId}|{fingerprint}";
        if (string.Equals(_lastAutoLoadAttempt, attemptKey, StringComparison.OrdinalIgnoreCase))
            return;
        _lastAutoLoadAttempt = attemptKey;

        // (1) Definitive: server room id from <nav rm="..."/>
        string? zoneFile = null;
        string  reason   = "";
        if (!string.IsNullOrEmpty(serverRoomId) &&
            _serverIdToZoneFile.TryGetValue(serverRoomId, out var idHit))
        {
            zoneFile = idHit;
            reason   = $"server room {serverRoomId}";
        }
        // (2) Fallback: title + exits fingerprint
        else if (_fingerprintToZoneFile.TryGetValue(fingerprint, out var fpHit))
        {
            zoneFile = fpHit;
            reason   = $"room title \"{title}\"";
        }

        if (zoneFile is null)
        {
            // Index didn't have this room. Always surface so the user (and
            // we while debugging) can see what fingerprint failed — gating
            // this on "zone ever loaded" hid the most common failure case
            // (first connect, no zone yet).
            var idHint = string.IsNullOrEmpty(serverRoomId) ? "" : $" [server {serverRoomId}]";
            var diag   = $"No local zone contains \"{title}\" with exits {exitList}{idHint}.";
            Dispatcher.UIThread.Post(() => LoadStatus = diag);
            return;
        }

        // Genie 4 parity: keep the GLOBAL room search (above) from selecting the
        // transient-transport map (id 998, the community "Transports" —
        // ferries/gondolas/barges). Correction (verified vs G4 source, 2026-08-06):
        // G4 DOES enter Transports aboard a ferry — a bank-zone deck room carries an
        // authored .xml boundary note and G4 follows it (IsLabelFile =
        // Note.Contains(".xml")), sitting on $zoneid=998 for the crossing; G5's
        // FollowZoneNote matches that and is intentionally NOT guarded. What G4
        // lacks is THIS global cross-map search, which on a match miss could snap
        // the active zone to Map998 (or a wrong 998 room) in cases G4 never would.
        // Skipping it here keeps G5's 998-entry authored-note-driven, like G4. It
        // does NOT keep $zoneid on the bank zone (the earlier belief that it did was
        // wrong); whether this guard is what travel.cmd actually needs is still open
        // pending a `#script debug travel` trace.
        if (AutoMapperEngine.IsTransientTransportZone(zoneFile))
        {
            Dispatcher.UIThread.Post(() =>
                LoadStatus = $"Aboard a transport ('{zoneFile}') — keeping zone '{SelectedZoneFile}' (Genie 4 parity).");
            return;
        }

        // Already loaded the right zone but the engine still doesn't match? Don't
        // re-trigger LoadZone (would wipe CurrentNode and loop). Surface this as
        // a diagnostic instead — the room is supposedly in this zone but the
        // engine can't match it, so title/exits parsing or fingerprint encoding
        // has drifted.
        if (string.Equals(SelectedZoneFile, zoneFile, StringComparison.OrdinalIgnoreCase))
        {
            var diag = $"Engine can't match \"{title}\" (exits: {exitList}) in '{zoneFile}'.";
            Dispatcher.UIThread.Post(() => LoadStatus = diag);
            return;
        }

        var pickedZone   = zoneFile;
        var pickedReason = reason;
        Dispatcher.UIThread.Post(() =>
        {
            LoadStatus = $"Auto-detected zone '{pickedZone}' from {pickedReason}.";
            // Setting this triggers WhenAnyValue → LoadSelectedZone → engine.LoadZone,
            // which calls Recalculate() so the player's current room matches.
            // Auto flag: this is the ENGINE following the character, never a
            // browse (see BrowsingZone).
            _autoZoneSwitch = true;
            try { SelectedZoneFile = pickedZone; }
            finally { _autoZoneSwitch = false; }
        });
    }

    /// <summary>
    /// Called by the main window VM after the user picks a new Maps directory.
    /// Re-scans the dropdown list and rebuilds the auto-detect server-id index
    /// in the background — both are needed to keep "Auto-detect zone from
    /// server room id" responsive against the new location.
    /// </summary>
    public void OnMapsDirectoryChanged()
    {
        RefreshAvailableZones();
        _lastAutoLoadAttempt = null;
        _ = RebuildServerIdIndexAsync();
    }

    private async Task RebuildServerIdIndexAsync()
    {
        if (_zoneRepo is null || string.IsNullOrWhiteSpace(MapsDirectory) || !Directory.Exists(MapsDirectory))
            return;

        var repo = _zoneRepo;
        var dir  = MapsDirectory;

        var (idIndex, fpIndex) = await Task.Run(() =>
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Genie 4 XML is the on-disk format — see MapZoneRepository.
            foreach (var file in Directory.GetFiles(dir, "*.xml"))
            {
                MapZone? zone;
                try { zone = repo.Load(file); }
                catch { continue; }
                if (zone is null) continue;

                var fname = Path.GetFileNameWithoutExtension(file);
                foreach (var node in zone.Nodes.Values)
                {
                    if (!string.IsNullOrEmpty(node.ServerRoomId))
                        ids.TryAdd(node.ServerRoomId, fname);
                    // Title+exits fingerprint — fallback for old maps that
                    // don't yet have server_id attributes. Skips nodes with
                    // no title (rare; would collide as the empty fingerprint).
                    if (!string.IsNullOrWhiteSpace(node.Title))
                    {
                        var fp = MapFingerprint.Compute(node.Title, node.Exits);
                        fps.TryAdd(fp, fname);
                    }
                }
            }
            return (ids, fps);
        });

        _serverIdToZoneFile    = idIndex;
        _fingerprintToZoneFile = fpIndex;
        _lastAutoLoadAttempt   = null;   // allow re-evaluation against the new indexes

        // Surface a one-liner so the user knows auto-detect is armed.
        // Useful for debugging when the index unexpectedly has 0 entries
        // (Maps dir empty, all XMLs malformed, etc.).
        Dispatcher.UIThread.Post(() =>
            LoadStatus = $"Indexed for auto-detect: {fpIndex.Count} fingerprints, {idIndex.Count} server ids.");
    }

    private void Refresh()
    {
        if (_engine is null) return;

        ZoneName  = string.IsNullOrEmpty(_engine.ActiveZone.Name)
            ? "(unsaved)" : _engine.ActiveZone.Name;
        RoomCount = _engine.ActiveZone.Nodes.Count;

        // Mapper $roomid (#66) — the current node's id (what scripts/#goto use),
        // "0" off-map. Matches the $roomid script variable exactly, NOT the
        // server's $gameroomid.
        CurrentRoomId = _engine.CurrentNode?.Id.ToString() ?? "0";
        // $zoneid (#66) — the active zone's Genie 4 numeric id, "0" off-map.
        CurrentZoneId = string.IsNullOrEmpty(_engine.ActiveZone.Genie4Id) ? "0" : _engine.ActiveZone.Genie4Id;

        // Surface the live references for the canvas. Reference may not have
        // changed since last call (the engine mutates Nodes in place), so we
        // also bump RenderTick to force the canvas to repaint.
        ActiveZone = _engine.ActiveZone;
        ActiveNode = _engine.CurrentNode;
        RenderTick++;

        var node = _engine.CurrentNode;
        CurrentObviousExits.Clear();
        CurrentLessObviousPaths.Clear();
        if (node is not null)
        {
            CurrentTitle    = node.Title;
            CurrentServerId = node.ServerRoomId ?? "";
            // Mirror the node's stored Notes into the editable buffer so the
            // Details panel's TextBox shows whatever is on file for this room.
            // Users edit here then hit Save → SaveNotes() pushes back.
            CurrentNotes    = node.Notes ?? "";

            // Obvious paths = compass-only. Filtering Direction.None keeps
            // "go ...", "climb ..." etc. out of the compass list — they get
            // their own clickable strip below as Less Obvious Paths.
            foreach (var exit in node.Exits)
            {
                if (exit.Direction != Direction.None)
                    CurrentObviousExits.Add(exit.Direction.ToString().ToLowerInvariant());
            }

            // Less Obvious = anything that isn't a compass primitive. Surfacing
            // them as buttons makes "hidden" arcs (go-doors, climb-walls,
            // swim-rivers) actually discoverable instead of buried in the map
            // graph the player can't see.
            foreach (var exit in node.Exits)
            {
                if (exit.Direction == Direction.None && !string.IsNullOrEmpty(exit.MoveCommand))
                    CurrentLessObviousPaths.Add(new LessObviousPath(exit.MoveCommand, exit.Requires));
            }

            // Follow the player to whichever Z-level they're on, so the canvas
            // doesn't get stranded on level 0 when they go up/down stairs.
            if (Level != node.Z) Level = node.Z;
        }
        else
        {
            CurrentTitle    = "(not yet matched)";
            CurrentServerId = "";
            CurrentNotes    = "";
        }
    }

    /// <summary>
    /// Compute "X ago" + stale-flag from a last-write timestamp. Called
    /// after a zone load or successful save so the Details panel's
    /// freshness indicator reflects the latest disk state.
    /// </summary>
    private void RefreshZoneAge()
    {
        if (ZoneLastWriteTime is null)
        {
            ZoneAgeDisplay = "";
            IsZoneStale    = false;
            return;
        }

        var age = DateTime.Now - ZoneLastWriteTime.Value;
        ZoneAgeDisplay = age.TotalDays switch
        {
            < 1   => "today",
            < 2   => "yesterday",
            < 14  => $"{(int)age.TotalDays} days ago",
            < 60  => $"{(int)(age.TotalDays / 7)} weeks ago",
            < 730 => $"{(int)(age.TotalDays / 30)} months ago",
            _     => $"{(int)(age.TotalDays / 365)} years ago",
        };
        IsZoneStale = age.TotalDays > 30;
    }

    /// <summary>
    /// Push <see cref="CurrentNotes"/> back into the active node and save
    /// the zone XML to disk. Triggered by the Save button next to the
    /// Notes textbox. No-op if there's no active node or no zone file
    /// selected — the button is disabled in that state, but we re-check
    /// defensively here in case the command fires from a stale binding.
    /// </summary>
    private void SaveNotes()
    {
        if (_engine is null || _zoneRepo is null) return;
        if (ActiveNode is null) return;
        if (string.IsNullOrEmpty(SelectedZoneFile)) return;
        if (string.IsNullOrEmpty(MapsDirectory)) return;

        ActiveNode.Notes = CurrentNotes ?? "";

        var path = Path.Combine(MapsDirectory, SelectedZoneFile + ".xml");
        try
        {
            _zoneRepo.Save(path, _engine.ActiveZone);
            // Updating the file refreshes its last-write time — reflect that
            // in the UI so "today" shows immediately after a save.
            ZoneLastWriteTime = File.GetLastWriteTime(path);
            RefreshZoneAge();
            LoadStatus = $"Saved notes for {ActiveNode.Title}.";
            // Bump the render tick so the canvas re-paints — the room label
            // for this node may have changed (room labels come from Notes).
            RenderTick++;
        }
        catch (Exception ex)
        {
            LoadStatus = $"Save failed: {ex.Message}";
        }
    }

    // ── Editor operations (Genie 4 AutoMapper toolbar) ────────────────────
    private void NewZone()
    {
        if (_engine is null) { LoadStatus = "Mapper not ready."; return; }
        _engine.NewZone("New Zone");
        // Don't trigger LoadSelectedZone — there's no file yet.
        _suppressAutoLoad = true;
        try { SelectedZoneFile = null; } finally { _suppressAutoLoad = false; }
        SelectedNode      = null;
        ZoneLastWriteTime = null;
        ZoneAgeDisplay    = "";
        IsZoneStale       = false;
        IsZoneDirty       = true;     // unsaved
        EditMode          = true;     // drop straight into editing a blank map
        Refresh();
        LoadStatus = "New zone created — turn on Record (or add rooms), then Save.";
    }

    private void SaveMap()
    {
        if (_engine is null || _zoneRepo is null) { LoadStatus = "Mapper not ready."; return; }
        if (string.IsNullOrWhiteSpace(MapsDirectory)) { LoadStatus = "No Maps directory set."; return; }

        var file = SelectedZoneFile;
        if (string.IsNullOrEmpty(file))
        {
            // Brand-new zone with no backing file — derive a filename from the
            // zone name (sanitised) so New → Save works without a Save-As dialog.
            var zname = _engine.ActiveZone.Name;
            file = SanitizeFileName(string.IsNullOrWhiteSpace(zname) ? "new_zone" : zname);
        }

        var path = Path.Combine(MapsDirectory, file + ".xml");
        try
        {
            _zoneRepo.Save(path, _engine.ActiveZone);
            ZoneLastWriteTime = File.GetLastWriteTime(path);
            RefreshZoneAge();
            IsZoneDirty = false;
            RenderTick++;

            // Make sure the dropdown reflects a newly-created file and keep it
            // selected WITHOUT re-loading (which would reset CurrentNode).
            if (!AvailableZones.Contains(file))
            {
                _suppressAutoLoad = true;
                try { AvailableZones.Add(file); SelectedZoneFile = file; }
                finally { _suppressAutoLoad = false; }
            }
            LoadStatus = $"Saved {file}.xml ({_engine.ActiveZone.Nodes.Count} rooms).";
        }
        catch (Exception ex)
        {
            LoadStatus = $"Save failed: {ex.Message}";
        }
    }

    private void RemoveSelected()
    {
        if (SelectedNode is null) { LoadStatus = "No room selected."; return; }
        RemoveNodeById(SelectedNode);
    }

    private void RemoveNodeById(MapNode node)
    {
        if (_engine is null || node is null) return;
        var id = node.Id;
        if (_engine.RemoveNode(id))
        {
            if (SelectedNode?.Id == id) SelectedNode = null;
            IsZoneDirty = true;
            Refresh();
            LoadStatus = $"Removed room {id}. Save to persist.";
        }
    }

    private void ResetMapIds()
    {
        if (_engine is null) { LoadStatus = "Mapper not ready."; return; }
        _engine.ResetMapIds();
        SelectedNode = null;   // ids changed; clear selection to avoid a stale ref
        IsZoneDirty  = true;
        Refresh();
        LoadStatus = "Renumbered room IDs to 1..N. Save to persist.";
    }

    private void ApplyNodeProps()
    {
        if (_engine is null || SelectedNode is null) { LoadStatus = "No room selected."; return; }
        SelectedNode.Title        = SelNodeTitle    ?? "";
        SelectedNode.Notes        = SelNodeNotes    ?? "";
        SelectedNode.Color        = SelNodeColor    ?? "";
        SelectedNode.ServerRoomId = SelNodeServerId ?? "";
        // Title + ServerRoomId feed the lookup indexes — rebuild them.
        _engine.NotifyStructureChanged();
        IsZoneDirty = true;
        RenderTick++;
        LoadStatus = $"Updated room {SelectedNode.Id}. Save to persist.";
    }

    private void MirrorSelectedNode()
    {
        var n = SelectedNode;
        SelNodeTitle    = n?.Title        ?? "";
        SelNodeNotes    = n?.Notes        ?? "";
        SelNodeColor    = n?.Color        ?? "";
        SelNodeServerId = n?.ServerRoomId ?? "";
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean   = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "new_zone" : clean;
    }

    // ── Zone selector ─────────────────────────────────────────────────────
    private void RefreshAvailableZones()
    {
        if (string.IsNullOrWhiteSpace(MapsDirectory) || !Directory.Exists(MapsDirectory))
        {
            AvailableZones.Clear();
            return;
        }

        // XML is the canonical format — matches the upstream GenieClient/Maps
        // repo so users can manage their Maps directory as a git clone.
        var files = SortZoneFiles(Directory.GetFiles(MapsDirectory, "*.xml"), ZoneSortMode);

        // Preserve the user's selection across rescans when possible.
        var prev = SelectedZoneFile;
        _suppressAutoLoad = true;
        try
        {
            AvailableZones.Clear();
            foreach (var f in files) AvailableZones.Add(f);
            SelectedZoneFile = prev is not null && files.Contains(prev) ? prev : null;
        }
        finally
        {
            _suppressAutoLoad = false;
        }
    }

    /// <summary>Matches the standard zone naming scheme: <c>Map</c> + number +
    /// optional letter suffix (Map10, Map107a, Map118e). Case-insensitive.</summary>
    private static readonly System.Text.RegularExpressions.Regex MapNumberRx =
        new(@"^map(\d+)([a-z]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                                  System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Order the zone file paths per the user's sort mode and return bare
    /// filenames (no extension) for the dropdown.
    /// <list type="bullet">
    /// <item>Name — plain A–Z (the historic order). Note this is a STRING sort,
    ///   so Map10 lands between Map108 and Map112.</item>
    /// <item>Recently Changed — file last-write time, newest first; handy for
    ///   "which zone was I just editing".</item>
    /// <item>Map Number — numeric MapNN order (Map10 before Map105), letter
    ///   variants after their base number (Map107 &lt; Map107a); special maps
    ///   (no MapNN prefix) sink to the bottom, A–Z.</item>
    /// </list>
    /// </summary>
    private static List<string> SortZoneFiles(string[] paths, string sortMode)
    {
        var key = ZoneSortKeyFor(sortMode);

        IEnumerable<string> ordered = key switch
        {
            "recent" => paths
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!),

            "number" => paths
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s =>
                {
                    var m = MapNumberRx.Match(s!);
                    // Special maps (no MapNN prefix) group AFTER all numbered
                    // maps. long-parse is defensive against absurd digit runs.
                    return m.Success && long.TryParse(m.Groups[1].Value, out var n)
                        ? (Name: s!, Special: 0, Number: n, Suffix: m.Groups[2].Value)
                        : (Name: s!, Special: 1, Number: 0L, Suffix: "");
                })
                .OrderBy(t => t.Special)
                .ThenBy(t => t.Number)
                .ThenBy(t => t.Suffix, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Name,   StringComparer.OrdinalIgnoreCase)
                .Select(t => t.Name),

            _ => paths
                .Select(Path.GetFileNameWithoutExtension)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ToList();
    }

    private static string ZoneSortKeyFor(string label)
    {
        var i = Array.IndexOf(ZoneSortLabels, label);
        return i >= 0 ? ZoneSortKeys[i] : "name";
    }

    /// <summary>Write the current sort choice to display.json (best-effort,
    /// same pattern as <see cref="ShowMapLegend"/>). No-op before AttachDisplay
    /// or when the stored key already matches.</summary>
    private void PersistZoneSort()
    {
        if (_display is null) return;
        var key = ZoneSortKeyFor(ZoneSortMode);
        if (_display.MapZoneSort == key) return;
        _display.MapZoneSort = key;
        try { if (!string.IsNullOrEmpty(_displayPath)) _display.Save(_displayPath); } catch { /* best effort */ }
    }

    private void LoadSelectedZone(string? filename)
    {
        if (_suppressAutoLoad)         return;
        if (_engine is null || _zoneRepo is null) return;
        if (string.IsNullOrEmpty(filename))       return;

        var path = Path.Combine(MapsDirectory, filename + ".xml");
        var zone = _zoneRepo.Load(path);
        if (zone is null)
        {
            LoadStatus = $"Could not load {filename}.xml";
            return;
        }

        // USER-initiated load with a live character: engage the browse intent
        // BEFORE the engine loads. LoadZone fires CurrentNodeChanged
        // synchronously, and GenieCore's SyncMapperGlobals runs on it — setting
        // the freeze only AFTER the load let that sync clobber $zoneid/$roomid
        // with the browsed map's values and 0 (`#echo $zoneid $roomid` read
        // "8 0" instead of the character's "1 236", 2026-08-06). Likewise the
        // load's own artifact room-miss fired RoomNotFoundInZone before the
        // hold existed, and the by-title boundary-stub path snapped the view
        // straight back (clicking Hodierna's Grace on Map1 loaded Map998 and
        // bounced back to Map1 in the same beat, via 998's own "Alfren's
        // Ferry" stub). _loadingZone mutes that artifact miss; the provisional
        // ViewIsBrowsing freeze is finalized (or lifted) right after the load.
        var userLoad = !_autoZoneSwitch &&
                       !string.IsNullOrEmpty(_engine.CurrentServerRoomId);
        if (userLoad) _engine.ViewIsBrowsing = true;   // provisional freeze
        _loadingZone = userLoad;

        // Capture the file's last-write time so the Details panel can show
        // "Last updated: X ago" and flag stale zones. Wrapped in try/catch
        // because the file may have been deleted between Load() and now.
        try
        {
            ZoneLastWriteTime = File.GetLastWriteTime(path);
            RefreshZoneAge();
        }
        catch
        {
            ZoneLastWriteTime = null;
            ZoneAgeDisplay    = "";
            IsZoneStale       = false;
        }

        try { _engine.LoadZone(zone); }
        finally { _loadingZone = false; }

        // Browse-hold detection: suspend auto-follow while the user
        // deliberately looks at a map that isn't the character's, so room
        // events can't re-load their zone in the same beat (2026-08-04 smoke:
        // cross-zone clicks during a ferry ride looked dead). The rule — and
        // the history behind it, including why it compares zone IDENTITY and
        // never probes for a match — lives on BrowseHoldPolicy.ShouldLatch.
        // Call-site specifics: _autoZoneSwitch drives userLoad: false, and
        // LoadZone → Recalculate ran synchronously above, so CurrentNode is
        // already resolved here.
        BrowsingZone = Services.BrowseHoldPolicy.ShouldLatch(
                           userLoad, _lastMatchedZoneFile, filename);
        // Anchor the hold to the room the character occupied when browsing
        // began — the RoomNotFoundInZone handler releases the hold as soon as
        // a DIFFERENT server room fires (character moved; tracking must win).
        _browseHoldRoomId = BrowsingZone ? _engine.CurrentServerRoomId : null;
        if (!BrowsingZone)
        {
            // Not browsing after all (engine-driven load, or the user picked
            // the character's own zone and the room matched). Lift the
            // provisional freeze and re-resolve so SyncMapperGlobals rewrites
            // the globals with the character's actual values — the sync that
            // fired during LoadZone ran under the freeze and was skipped.
            _engine.ViewIsBrowsing = false;
            if (userLoad) _engine.Recalculate();
        }
        LoadStatus = BrowsingZone
            ? $"Loaded {zone.Name} ({zone.Nodes.Count} rooms) — browsing (tracking paused; returns when your character enters this map or you re-select theirs)."
            : $"Loaded {zone.Name} ({zone.Nodes.Count} rooms).";
    }

    // ── Goto ──────────────────────────────────────────────────────────────
    private void GotoNode(MapNode target)
    {
        if (_engine is null || AutoWalk is null) return;

        // In-zone: the mapper has placed the player in the currently displayed
        // zone, so origin + target share a graph — use the well-tested single-zone
        // walk. The service sends each move on a CurrentNodeChanged tick (not all
        // at once), respects RT via the command queue, and stops on Esc / typed
        // command / disconnect / window-unfocus-over-60s.
        if (_engine.CurrentNode is not null)
        {
            if (_engine.CurrentNode.Id == target.Id) { LoadStatus = "Already here."; return; }
            if (TryHandOffToAutomapperScript(_engine.CurrentNode, target)) return;
            if (!AutoWalk.Start(_engine.CurrentNode, target))
                LoadStatus = AutoWalk.LastStatusFlash ?? $"No path to '{target.Title}'.";
            return;
        }

        // The player isn't placed in the displayed zone — you've switched the map
        // to look at another zone. Treat the click as a CROSS-ZONE goto from the
        // player's real room (resolved via the whole-Maps index) to the clicked
        // room in the shown zone.
        if (!TryStartCrossZoneWalk(SelectedZoneFile ?? "", target.Id, target.Title))
            LoadStatus = "Can't path yet — the mapper hasn't placed your character. " +
                         "Connect and walk a step (or the current room isn't mapped), then try again.";
    }

    /// <summary>
    /// Genie 4-parity hand-off (#226): Genie 4's <c>#goto</c> never walked —
    /// it sent <c>.automapper &lt;moves&gt;</c> and the community
    /// <c>automapper.cmd</c> did the walking, which is where special-move
    /// directives (<c>script ggbypass</c>, <c>ice nw</c>, <c>swim …</c>) and
    /// the pacing globals (<c>$caravan</c>/<c>$powerwalk</c>/…) live. When
    /// that script is present (and <c>#config automapperscript</c> is on,
    /// the default), start it with the path as its arguments instead of the
    /// built-in walker. Moves are passed as discrete script args — the
    /// quote-safe equivalent of Genie 4's quoted PathText (NodeList.PathText).
    /// Restart-while-running follows <c>abortdupescript</c> (default on):
    /// a second <c>#goto</c> aborts the running instance and relaunches —
    /// Genie 4's supersede semantics. Returns false (→ built-in walker) when
    /// the hand-off doesn't apply; cross-zone gotos never come here.
    /// </summary>
    private bool TryHandOffToAutomapperScript(MapNode origin, MapNode target)
    {
        if (_core is null || _engine is null || !_core.Config.AutoMapperScript) return false;
        if (!_core.Scripts.ScriptFileExists("automapper")) return false;

        var moves = _engine.FindPath(origin, target);
        if (moves is null || moves.Count == 0) return false;   // let the walker report "no path"

        AutoWalk?.Cancel("handed to automapper.cmd");
        if (!_core.Scripts.TryStart("automapper", moves))
        {
            // Only fails when an instance is running and abortdupescript is off —
            // don't double-drive with the built-in walker on top of it.
            LoadStatus = "automapper.cmd is already running (abortdupescript is off).";
            return true;
        }
        LoadStatus = $"Handed {moves.Count}-move path to automapper.cmd.";
        return true;
    }

    /// <summary>
    /// Resolve a <c>#goto</c> argument to a room in the active zone and start
    /// an attended walk — the typed/scripted equivalent of clicking a room.
    /// Accepts a numeric map id (Genie 4 <c>#goto 232</c>), a note label
    /// (notes are <c>|</c>-separated, Genie 4 parity), or room-title text
    /// (exact match preferred, else a single unambiguous substring match).
    /// </summary>
    public void GotoByName(string arg)
    {
        if (_engine is null) { LoadStatus = "Mapper not ready — load a zone first."; return; }
        arg = arg?.Trim() ?? "";
        if (arg.Length == 0) { LoadStatus = "Usage: #goto <room id | label | title | @tag>"; return; }

        // '@tag' → walk to the NEAREST room carrying that tag (Lich
        // find_nearest_by_tag). Needs the current room as the search origin.
        if (arg.StartsWith('@'))
        {
            var tag = arg[1..].Trim();
            if (_engine.CurrentNode is null)
            {
                LoadStatus = "No current room — walk one step so the mapper can match you before #goto @tag.";
                return;
            }
            var nearest = _engine.FindNearestByTag(_engine.CurrentNode, tag);
            if (nearest is null)
            {
                var known = string.Join(", ", _engine.KnownTags.OrderBy(t => t));
                LoadStatus = known.Length == 0
                    ? $"#goto: no rooms are tagged in '{_engine.ActiveZone.Name}'."
                    : $"#goto @{tag}: no reachable room tagged '{tag}'. Known tags: {known}.";
                return;
            }
            GotoNode(nearest);
            return;
        }

        var target = ResolveNode(arg);
        if (target is null)
        {
            // Not in the loaded zone — try a CROSS-ZONE walk: resolve the target
            // through the whole-Maps index and route there with the multi-zone
            // pathfinder. (Common when a travel script fires a room in another zone.)
            if (TryStartCrossZoneGoto(arg)) return;

            LoadStatus = $"#goto: no room matching '{arg}' in zone '{_engine.ActiveZone.Name}'.";
            // Signal automapper-driven scripts (travel.cmd, …) that this #goto
            // can't be resolved — they matchwait on "DESTINATION NOT FOUND" and
            // would otherwise hang.
            AutoWalk?.EmitAutomapperSignal(AutomapperSignals.DestinationNotFound);
            return;
        }
        GotoNode(target);
    }

    /// <summary>Load every zone once and build BOTH the cross-zone room index and
    /// the note-derived cross-zone connections (cached). A single scan of the Maps
    /// folder feeds both, so we don't read 121 zone files twice.</summary>
    private void EnsureMapsScan()
    {
        if (_roomIndex is not null && _derivedConnections is not null) return;
        var loaded = new List<(string ZoneFile, MapZone Zone)>();
        if (_zoneRepo is not null && !string.IsNullOrWhiteSpace(MapsDirectory))
            foreach (var path in _zoneRepo.ListZoneFiles(MapsDirectory))
            {
                var z = _zoneRepo.Load(path);
                if (z is not null) loaded.Add((Path.GetFileNameWithoutExtension(path), z));
            }
        _roomIndex          = ZoneRoomIndex.Build(loaded);
        _derivedConnections = ZoneConnectionDeriver.Derive(loaded);
    }

    /// <summary>Whole-Maps room index for cross-zone resolution (server-room-id /
    /// title → zone + node).</summary>
    private ZoneRoomIndex RoomIndex() { EnsureMapsScan(); return _roomIndex!; }

    /// <summary>Cross-zone connections for the multi-zone pathfinder: the links
    /// derived from the maps' border-room notes MERGED with any hand-authored
    /// ZoneConnections.xml entries. Authored entries augment (and override on an
    /// exact endpoint match) the derived graph rather than replacing it wholesale,
    /// so the placeholder baseline Genie seeds on first launch can't shadow the
    /// derived links. See <see cref="ZoneConnectionMerge"/>.</summary>
    private IReadOnlyList<ZoneConnection> Connections()
    {
        var authored = new ZoneConnectionsRepository(
            Path.Combine(MapsDirectory, "ZoneConnections.xml")).Load();
        EnsureMapsScan();
        return ZoneConnectionMerge.Merge(_derivedConnections!, authored);
    }

    /// <summary>
    /// Attempt a CROSS-ZONE <c>#goto</c>: the target isn't in the loaded zone, so
    /// resolve it through the whole-Maps <see cref="ZoneRoomIndex"/> and, when it
    /// lives in another zone, plan + walk there with <see cref="MultiZonePathfinder"/>.
    /// The walker already executes cross-zone plans (wait countdown + destination-
    /// zone fingerprint arrival). Returns true when it took over the goto (started,
    /// or failed with a surfaced reason); false to let the caller report "not found".
    /// </summary>
    private bool TryStartCrossZoneGoto(string arg)
    {
        if (_zoneRepo is null || string.IsNullOrWhiteSpace(MapsDirectory)) return false;

        var index = RoomIndex();
        // Resolve the target: server-room-id first (exact, game-wide), else a title.
        if (!index.TryResolveServerRoom(arg, out var dest))
        {
            var titleHits = index.ByTitle(arg);
            if (titleHits.Count == 0) return false;   // not a known cross-zone room
            dest = titleHits[0];
        }
        return TryStartCrossZoneWalk(dest.Zone, dest.NodeId, arg);
    }

    /// <summary>
    /// Shared cross-zone walk: origin is the player's <b>actual</b> current room —
    /// resolved from its server-room-id via the whole-Maps index, so it works even
    /// when the displayed zone isn't the one they're standing in. Plans with
    /// <see cref="MultiZonePathfinder"/> and hands the plan to the walker (which
    /// executes cross-zone steps via wait-countdown + destination-zone fingerprint
    /// arrival). Returns false when the origin can't be determined (offline / the
    /// current room isn't mapped) or the destination is actually in the same zone.
    /// </summary>
    private bool TryStartCrossZoneWalk(string destZone, int destNodeId, string destTitle)
    {
        if (_engine is null || _zoneRepo is null || AutoWalk is null ||
            string.IsNullOrWhiteSpace(MapsDirectory))
            return false;

        var srv = _engine.CurrentServerRoomId;
        if (string.IsNullOrWhiteSpace(srv) || !RoomIndex().TryResolveServerRoom(srv, out var origin))
            return false;   // don't know where the player physically is

        if (string.Equals(origin.Zone, destZone, StringComparison.OrdinalIgnoreCase))
            return false;   // same zone after all — let the in-zone path handle it

        var pathfinder = new MultiZonePathfinder(
            _zoneRepo, MapsDirectory, Connections(),
            _skillStore, _engine.CharacterClass, _engine.CharacterLevel);

        var plan = pathfinder.FindPath(
            origin.Zone, origin.NodeId.ToString(), destZone, destNodeId.ToString());

        // Display-only nodes (arrival is confirmed by zone fingerprint, not ids).
        var originNode = new MapNode { Id = origin.NodeId, Title = "current location" };
        var destNode   = new MapNode { Id = destNodeId,   Title = destTitle };
        if (!AutoWalk.StartCrossZone(originNode, destNode, plan))
            LoadStatus = AutoWalk.LastStatusFlash ?? $"No cross-zone path to '{destTitle}'.";
        return true;
    }

    /// <summary>
    /// Resolve a #goto token to a node, in Genie 4 priority order: numeric id,
    /// then note label (exact, then prefix), then title (exact, then prefix),
    /// then a single unambiguous title substring.
    /// <para>
    /// The prefix steps are Genie 4's shorthand handling (#115): typing
    /// <c>#goto gem</c> reaches a room labelled <c>gems</c>, and <c>#goto
    /// brickwell</c> reaches one labelled/titled <c>brickwell tower</c>. Prefix
    /// matches take the first hit (Genie 4 + the Kzin prototype's resolver
    /// behaviour); the unambiguous-substring fallback only fires when exactly
    /// one title contains the token.
    /// </para>
    /// </summary>
    private MapNode? ResolveNode(string arg)
    {
        var zone = _engine!.ActiveZone;

        // 1) Numeric map id (Genie 4 `#goto 232`).
        if (int.TryParse(arg, out var id) && zone.Nodes.TryGetValue(id, out var byId))
            return byId;

        // Notes hold '|'-separated labels; match the predicate against any one.
        bool Label(MapNode n, Func<string, bool> pred)
            => !string.IsNullOrEmpty(n.Notes) &&
               n.Notes.Split('|').Any(label => pred(label.Trim()));

        // 2) Note label exact → 3) note label prefix (shorthand, #115) →
        //    4) title exact → 5) title prefix (shorthand).
        var hit =
               zone.Nodes.Values.FirstOrDefault(n => Label(n, l => l.Equals(arg, StringComparison.OrdinalIgnoreCase)))
            ?? zone.Nodes.Values.FirstOrDefault(n => Label(n, l => l.StartsWith(arg, StringComparison.OrdinalIgnoreCase)))
            ?? zone.Nodes.Values.FirstOrDefault(n => n.Title.Equals(arg, StringComparison.OrdinalIgnoreCase))
            ?? zone.Nodes.Values.FirstOrDefault(n => n.Title.StartsWith(arg, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) return hit;

        // 6) Final fallback: a single unambiguous title substring.
        var partial = zone.Nodes.Values
            .Where(n => n.Title.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    // ── UpdateMaps implementation ─────────────────────────────────────────
    private async Task UpdateMapsAsync()
    {
        if (_zoneRepo is null || string.IsNullOrWhiteSpace(MapsDirectory))
            return;

        IsUpdating    = true;
        UpdateStatus  = "Contacting github.com/GenieClient/Maps...";
        UpdateSummary = "";

        try
        {
            // Phase 1 of the update system: hardwire the default GenieClient/Maps
            // source here so this menu entry keeps working unchanged. The Updates
            // dialog (Phase 3) will load enabled feeds from update-feeds.json and
            // pass them all in; at that point this method becomes a thin shortcut
            // to the same dialog's Maps tab.
            var source  = new GithubContentsSource(
                owner:     "GenieClient",
                repo:      "Maps",
                extension: ".xml");
            var updater = new MapsUpdater(_zoneRepo, MapsDirectory, new[] { source });

            // Progress reports fire on the HTTP worker thread; marshal text
            // updates back to the UI thread so the binding update is safe.
            var progress = new Progress<UpdateProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                    UpdateStatus = $"[{p.Current}/{p.Total}] {p.Item} — {p.Status}"));

            var result = await updater.ApplyAsync(progress);
            UpdateSummary = result.Summary;

            // Bump room-count display in case the active zone's JSON was
            // refreshed on disk — the engine will pick up the new data the
            // next time the user loads/reloads a zone.
            Refresh();

            // Repopulate the zone dropdown — new files may have appeared.
            RefreshAvailableZones();

            // Rebuild the auto-detect index — new zones contain new server
            // room ids the player might walk into. Background task.
            _ = RebuildServerIdIndexAsync();
        }
        finally
        {
            IsUpdating   = false;
            UpdateStatus = "";
        }
    }
}

/// <summary>
/// XAML-static converters for the Mapper's zone dropdown. Items stay plain
/// filename strings (the SelectedItem ↔ SelectedZoneFile binding depends on
/// that), so the "special map" distinction is derived per-item at render time
/// instead of being baked into a wrapper object.
/// </summary>
public static class ZoneNameConverters
{
    /// <summary>String zone filename → true when it's a special (non-MapNN)
    /// map, e.g. Hollow_Eve. Shows the SPECIAL badge in the dropdown.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter IsSpecial =
        new Avalonia.Data.Converters.FuncValueConverter<string?, bool>(
            s => !string.IsNullOrEmpty(s) && MapperViewModel.IsSpecialMapName(s));
}
