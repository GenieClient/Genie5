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
    [Reactive] public Color  MapBackground      { get; set; } = Color.Parse("#1A1A1A");

    /// <summary>
    /// Derived brush bound to <see cref="Controls.MapCanvas.MapBackgroundBrush"/>.
    /// Recomputed whenever <see cref="MapBackground"/> changes so the canvas
    /// repaints live as the user drags through the colour picker.
    /// </summary>
    [Reactive] public IBrush MapBackgroundBrush { get; private set; } = new SolidColorBrush(Color.Parse("#1A1A1A"));

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

    /// <summary>Show full <c>|</c>-separated room labels vs. only the primary
    /// name (default). Bound to <see cref="Controls.MapCanvas.FullLabels"/>.</summary>
    [Reactive] public bool   FullLabels    { get; set; }

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
    private MapZoneRepository? _zoneRepo;
    private CommandEngine?     _commands;
    private DisplaySettings?   _display;
    private bool               _suppressAutoLoad;
    private string?            _displayPath;

    /// <summary>
    /// Drives step-by-step auto-walk when the user picks "Go Here" on a
    /// map node. Null before <see cref="Attach"/> runs; non-null once
    /// the GenieCore is wired. Surface this on the panel so XAML can
    /// bind the "Walking to X — N rooms left" indicator to
    /// <c>AutoWalk.Current</c>.
    /// </summary>
    public AutoWalkService? AutoWalk { get; private set; }

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
    public ReactiveCommand<Unit, Unit>? FetchSkillsCommand { get; private set; }

    /// <summary>
    /// Dismiss the prompt for this session only. Banner hides; will
    /// prompt again on next launch (unless DontAskAgain is also set).
    /// </summary>
    public ReactiveCommand<Unit, Unit>? DismissSkillsPromptCommand { get; private set; }

    /// <summary>
    /// Dismiss + persist "don't ask again" to DisplaySettings. Banner
    /// stays hidden permanently for this character.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? DontAskAboutSkillsCommand { get; private set; }

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

        // Auto-load the zone when the user picks one from the dropdown. Skip(1)
        // ignores the initial null emission; the _suppressAutoLoad guard lets
        // RefreshAvailableZones re-select a value (after a directory rescan)
        // without triggering a redundant reload.
        this.WhenAnyValue(x => x.SelectedZoneFile)
            .Skip(1)
            .Subscribe(LoadSelectedZone);

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
        if (Color.TryParse(display.MapBackgroundHex, out var c))
            MapBackground = c;
    }

    private void RecomputeIsGitManaged()
    {
        IsGitManaged = !string.IsNullOrWhiteSpace(MapsDirectory) &&
                       Directory.Exists(Path.Combine(MapsDirectory, ".git"));
    }

    public void Attach(GenieCore core)
    {
        _engine   = core.AutoMapper;
        _zoneRepo = core.ZoneRepository;
        _commands = core.Commands;

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
        // The skill-weighted pathfinder works best when it knows the
        // character's rank in every relevant skill. DR only emits
        // `<component id='exp X'>` events when the user types `info`,
        // `skills`, or naturally during play (skill ticks). Rather than
        // silently auto-firing `skills` on connect (verb-spam), we
        // surface a one-time banner asking the user to run it.
        FetchSkillsCommand = ReactiveCommand.Create(() =>
        {
            _commands?.ProcessInput("skills");
            ShowSkillsPrompt = false;   // hide immediately; data will arrive shortly
        });
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
        _engine.CurrentNodeChanged += () => Dispatcher.UIThread.Post(Refresh);
        _engine.MapChanged         += () => Dispatcher.UIThread.Post(Refresh);
        _engine.RoomNotFoundInZone += (serverId, title, exits)
            => TryAutoLoadZoneFor(serverId, title, exits);

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
            SelectedZoneFile = pickedZone;
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
        var files = Directory.GetFiles(MapsDirectory, "*.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(s => !string.IsNullOrEmpty(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preserve the user's selection across rescans when possible.
        var prev = SelectedZoneFile;
        _suppressAutoLoad = true;
        try
        {
            AvailableZones.Clear();
            foreach (var f in files) AvailableZones.Add(f!);
            SelectedZoneFile = files.Contains(prev) ? prev : null;
        }
        finally
        {
            _suppressAutoLoad = false;
        }
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

        _engine.LoadZone(zone);
        LoadStatus = $"Loaded {zone.Name} ({zone.Nodes.Count} rooms).";
    }

    // ── Goto ──────────────────────────────────────────────────────────────
    private void GotoNode(MapNode target)
    {
        if (_engine is null || AutoWalk is null) return;
        if (_engine.CurrentNode is null)
        {
            LoadStatus = "No current room — can't path. Walk one step so the mapper can match you first.";
            return;
        }
        if (_engine.CurrentNode.Id == target.Id)
        {
            LoadStatus = "Already here.";
            return;
        }

        // Delegate to AutoWalkService. The service sends each move on a
        // CurrentNodeChanged tick (not all at once), respects RT via the
        // command queue, and stops on Esc / typed command / disconnect /
        // window-unfocus-over-60s. Earlier implementations sent the whole
        // sequence as a burst which was non-compliant under DR policy.
        var started = AutoWalk.Start(_engine.CurrentNode, target);
        if (!started)
        {
            // Service refused — surface the reason (set via FlashStatus).
            // If nothing's flashed, just say "no path" as a fallback.
            LoadStatus = AutoWalk.LastStatusFlash ?? $"No path to '{target.Title}'.";
        }
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
        if (arg.Length == 0) { LoadStatus = "Usage: #goto <room id | label | title>"; return; }

        var target = ResolveNode(arg);
        if (target is null)
        {
            LoadStatus = $"#goto: no room matching '{arg}' in zone '{_engine.ActiveZone.Name}'.";
            return;
        }
        GotoNode(target);
    }

    /// <summary>Resolve a #goto token to a node: id → label → title.</summary>
    private MapNode? ResolveNode(string arg)
    {
        var zone = _engine!.ActiveZone;

        // 1) Numeric map id (Genie 4 `#goto 232`).
        if (int.TryParse(arg, out var id) && zone.Nodes.TryGetValue(id, out var byId))
            return byId;

        // 2) Note label — notes hold '|'-separated labels.
        foreach (var n in zone.Nodes.Values)
        {
            if (string.IsNullOrEmpty(n.Notes)) continue;
            foreach (var label in n.Notes.Split('|'))
                if (label.Trim().Equals(arg, StringComparison.OrdinalIgnoreCase))
                    return n;
        }

        // 3) Title — exact match first, then a single unambiguous contains.
        var exact = zone.Nodes.Values.FirstOrDefault(
            n => n.Title.Equals(arg, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

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
