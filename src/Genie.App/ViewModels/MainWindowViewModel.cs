using System.Reactive;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Dock.Model.Controls;
using Dock.Model.Core;
using Genie.App.Diagnostics;
using Genie.App.Docking;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.Core;
using Genie.Core.Connection;
using Genie.Core.Events;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Genie.Core.Profiles;
using Genie.Core.Runtime;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

public class MainWindowViewModel : ReactiveObject, IActivatableViewModel
{
    public ViewModelActivator Activator { get; } = new();

    // ── Sub-ViewModels ────────────────────────────────────────────────────────

    public GameTextViewModel   GameText   { get; } = new();
    public VitalsViewModel     Vitals     { get; } = new();
    public RoomViewModel       Room       { get; } = new();
    public InventoryViewModel  Inventory  { get; } = new();
    public MapperViewModel     Mapper     { get; } = new();
    public CommandViewModel    Command    { get; }
    public StreamTabsViewModel StreamTabs { get; } = new();
    public ExperienceViewModel Experience { get; } = new();
    public ScriptBarViewModel  ScriptBar  { get; } = new();

    /// <summary>
    /// Global layout presets, shared across every character —
    /// <c>{AppData}/Genie5/Layouts/</c>. Always present.
    /// </summary>
    private Settings.LayoutStore _globalLayouts = null!;

    /// <summary>
    /// Per-profile layout presets for the connected saved profile —
    /// <c>{Config}/Profiles/{guid}/Layouts/</c>. Null when not connected, or
    /// connected via bare credentials (no saved profile to scope to).
    /// </summary>
    private Settings.LayoutStore? _profileLayouts;


    /// <summary>Current set of saved layouts, refreshed for the Layout
    /// menu each time it opens. ObservableCollection so the menu's
    /// ItemsControl picks up additions / deletions live. Each item wraps
    /// the layout name together with a pre-bound <see cref="LoadLayoutCommand"/>
    /// reference so the menu DataTemplate can bind without an ancestor
    /// cast — see <see cref="LayoutMenuItem"/>.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LayoutMenuItem> SavedLayouts { get; }
        = new();

    // ── Docking ───────────────────────────────────────────────────────────────

    [Reactive] public IRootDock? DockLayout  { get; private set; }
    public IFactory?  DockFactory { get; private set; }

    // ── Connection state ──────────────────────────────────────────────────────

    [Reactive] public string             ConnectionStatus { get; private set; } = "Disconnected";
    [Reactive] public bool               IsConnected      { get; private set; }

    /// <summary>
    /// Profile that backs the current (or most recent) connection. Set by
    /// <see cref="ConnectAsync"/> when a saved profile was picked in the Connect
    /// dialog. Drives per-profile config storage paths and the title bar.
    /// </summary>
    [Reactive] public ConnectionProfile? ConnectedProfile { get; private set; }

    /// <summary>
    /// The <see cref="ConnectionConfig"/> used by the most recent successful
    /// connect (or attempt). Survives disconnect so reopening the Connect
    /// dialog can pre-fill the just-used credentials — important for the
    /// bare-credential / no-saved-profile path where, without this, the
    /// dialog would fall back to auto-selecting some saved profile instead
    /// of showing what the user just typed.
    /// </summary>
    public ConnectionConfig? LastConnectionConfig { get; private set; }

    // ── Commands & interactions ───────────────────────────────────────────────

    public Interaction<Unit, ConnectResult?>              ShowConnectDialog        { get; } = new();
    public Interaction<DisplaySettings, bool>             ShowDisplaySettingsDialog{ get; } = new();
    public Interaction<ConfigurationViewModel, Unit>      ShowConfigurationDialog  { get; } = new();
    public Interaction<Genie4ImportViewModel, Unit>       ShowGenie4ImportDialog   { get; } = new();
    public Interaction<EditExitViewModel, bool>           ShowEditExitDialog       { get; } = new();
    public Interaction<ZoneConnectionsViewModel, Unit>    ShowZoneConnectionsDialog{ get; } = new();
    public ReactiveCommand<Unit, Unit>                    ConnectCommand           { get; }
    public ReactiveCommand<Unit, Unit>                    DisconnectCommand        { get; }
    public ReactiveCommand<Unit, Unit>                    DisplaySettingsCommand   { get; }
    public ReactiveCommand<Unit, Unit>                    ConfigurationCommand     { get; }
    public ReactiveCommand<Unit, Unit>                    Genie4ImportCommand      { get; }
    public ReactiveCommand<Unit, Unit>                    ZoneConnectionsCommand   { get; }
    public ReactiveCommand<Unit, Unit>                    SaveLayoutAsCommand      { get; }
    public ReactiveCommand<LayoutMenuItem, Unit>          LoadLayoutCommand        { get; }
    public ReactiveCommand<Unit, Unit>                    RefreshLayoutListCommand { get; }
    public ReactiveCommand<Unit, Unit>                    ManageLayoutsCommand     { get; }
    public Interaction<ManageLayoutsViewModel, Unit>      ShowManageLayoutsDialog  { get; } = new();

    // ── Help menu (Updates) ─────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit>                    ShowUpdatesCommand       { get; }
    public Interaction<UpdatesDialogViewModel, Unit>      ShowUpdatesDialog        { get; } = new();

    /// <summary>Opens the community Discord invite in the user's default browser.
    /// Cross-platform via <c>UseShellExecute = true</c>; same pattern the parser
    /// uses for <c>&lt;a href&gt;</c> links from the game stream.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenDiscordCommand       { get; }

    // ── Scripts menu (Genie 4 parity) ───────────────────────────────────────
    // Routes through ICommandHost methods on the live core. None of these
    // commands take parameters; the parameterised one (TraceAllScripts)
    // takes an integer-as-string from the menu CommandParameter.
    public ReactiveCommand<Unit, Unit>                    ListRunningScriptsCommand { get; }
    public ReactiveCommand<Unit, Unit>                    PauseAllScriptsCommand    { get; }
    public ReactiveCommand<Unit, Unit>                    ResumeAllScriptsCommand   { get; }
    public ReactiveCommand<Unit, Unit>                    AbortAllScriptsCommand    { get; }
    public ReactiveCommand<string, Unit>                  TraceAllScriptsCommand    { get; }
    public ReactiveCommand<Unit, Unit>                    OpenScriptsFolderCommand  { get; }

    // ── Help menu (external links) ──────────────────────────────────────────
    // Ported from the Genie 4 Help menu (Forms/FormMain). Each opens a URL in
    // the user's default browser via OpenUrl(). GitHub / Wiki / Latest Release
    // point at the Genie 5 repo (GenieClient/Genie5); the Community Links are
    // game/community resources carried over unchanged from Genie 4.
    /// <summary>Opens the Genie 5 GitHub releases page (latest signed builds).</summary>
    public ReactiveCommand<Unit, Unit>                    OpenLatestReleaseCommand { get; }
    /// <summary>Opens the Genie 5 source repository on GitHub.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenGitHubCommand        { get; }
    /// <summary>Opens the Genie 5 wiki.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenWikiCommand          { get; }
    /// <summary>Community Links → Play.net (Simutronics account/billing portal).</summary>
    public ReactiveCommand<Unit, Unit>                    OpenPlayNetCommand       { get; }
    /// <summary>Community Links → Elanthipedia (the DragonRealms community wiki).</summary>
    public ReactiveCommand<Unit, Unit>                    OpenElanthipediaCommand  { get; }
    /// <summary>Community Links → DR Service (drservice.info).</summary>
    public ReactiveCommand<Unit, Unit>                    OpenDrServiceCommand     { get; }
    /// <summary>Community Links → Lich 5 community Discord.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenLichDiscordCommand   { get; }
    /// <summary>Community Links → Isharon's Genie Settings (elanthia.org/GenieSettings).</summary>
    public ReactiveCommand<Unit, Unit>                    OpenIsharonSettingsCommand { get; }

    /// <summary>True when a background check found at least one enabled feed with an update available.
    /// Drives the Help-menu badge ("Help ●") so users see availability without opening the dialog.</summary>
    [Reactive] public bool                                UpdatesAvailable         { get; private set; }
    /// <summary>Menu header bound by MainWindow.axaml — appends a bullet when <see cref="UpdatesAvailable"/>.</summary>
    public string HelpMenuHeader => UpdatesAvailable ? "_Help ●" : "_Help";

    // ── Plugins menu ────────────────────────────────────────────────────────
    /// <summary>Loaded plugins shown in the Plugins menu (enable/disable).
    /// Rebuilt when the menu opens.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PluginMenuItem> PluginMenuItems { get; } = new();
    /// <summary>Unloaded plugin DLLs in the Plugins folder (Plugins → Load).</summary>
    public System.Collections.ObjectModel.ObservableCollection<PluginFileItem> AvailablePluginFiles { get; } = new();
    /// <summary>Plugin-created dock windows (Window → Plugin Windows). A plugin
    /// surfaces a panel by writing to a named window via the host's SetWindow /
    /// EchoToWindow seam; this list lets the user show/hide each. Rebuilt when
    /// the Window menu opens.</summary>
    public System.Collections.ObjectModel.ObservableCollection<PluginWindowMenuItem> PluginWindowMenuItems { get; } = new();
    public ReactiveCommand<Unit, Unit> OpenPluginsFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadPluginsCommand     { get; }
    public ReactiveCommand<Unit, Unit> RefreshPluginListCommand { get; }
    // ResetLayoutCommand is declared further down (existing); we re-assign
    // it in the layout-feature block to use the new ApplyLayout helper
    // so the dock + display flags both come back to factory defaults.

    public Interaction<LayoutSavePrompt, LayoutSaveResult?> ShowLayoutSavePrompt   { get; } = new();
    public ReactiveCommand<Unit, Unit>                    ToggleStatusBarCommand   { get; }
    public ReactiveCommand<Unit, Unit>                    ToggleWindowedModeCommand{ get; }
    public ReactiveCommand<Unit, Unit>                    ToggleGuildInTitleCommand{ get; }
    public ReactiveCommand<Unit, Unit>                    ToggleHandsBarCommand    { get; }
    /// <summary>Window → Hands Strip Position → Top. Snaps the strip to the top of the window.</summary>
    public ReactiveCommand<Unit, Unit>                    HandsBarToTopCommand     { get; }
    /// <summary>Window → Hands Strip Position → Bottom. Snaps it back to the Genie 4 position (above vitals).</summary>
    public ReactiveCommand<Unit, Unit>                    HandsBarToBottomCommand  { get; }
    /// <summary>Window → Enhanced Hands Strip. Toggles the dylb0t-derived icon widgets (compass / body / status icons) into / out of the strip.</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleEnhancedHandsStripCommand { get; }
    /// <summary>Window → Roundtime Position → Command Bar. Keeps the "⏱ N.Ns" badge inline with the input row.</summary>
    public ReactiveCommand<Unit, Unit>                    RoundTimeToCommandBarCommand { get; }
    /// <summary>Window → Roundtime Position → Hands Strip. Moves the RT badge onto the L/R/S row.</summary>
    public ReactiveCommand<Unit, Unit>                    RoundTimeToHandsStripCommand { get; }

    /// <summary>File → Record Session — toggle raw-XML capture on/off.</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleRecordingCommand   { get; }

    /// <summary>True iff the RT badge should render in its command-bar slot —
    /// i.e. the character is in RT AND the user has chosen the command-bar
    /// position. Computed from <see cref="VitalsViewModel.InRoundTime"/> and
    /// <see cref="DisplaySettings.RoundTimeOnHandsStrip"/>.</summary>
    [Reactive] public bool ShowRtInCommandBar  { get; private set; }

    /// <summary>True iff the RT badge should render inline on the hands strip
    /// (in RT AND user chose hands-strip position).</summary>
    [Reactive] public bool ShowRtOnHandsStrip  { get; private set; }

    /// <summary>True while <see cref="SessionRecorder"/> is actively writing the
    /// raw XML stream to disk. Drives the File-menu checkbox + the title-bar
    /// "● REC" suffix.</summary>
    [Reactive] public bool IsRecording        { get; private set; }

    /// <summary>Full window title with optional " ● REC" suffix when recording.
    /// Composed reactively from <see cref="ConnectionStatus"/>, <see cref="CharacterGuild"/>,
    /// and <see cref="IsRecording"/>.</summary>
    [Reactive] public string WindowTitle      { get; private set; } = "Genie 5";

    /// <summary>Character guild for the title bar, parsed from the <c>info</c>
    /// verb (empty until the player runs <c>info</c>). Appended to the title
    /// as " — {Guild}" only when non-empty.</summary>
    [Reactive] public string CharacterGuild   { get; private set; } = "";

    /// <summary>Raw-XML session capture. One file per Start invocation under
    /// <c>{AppData}/Genie5/Logs/</c>. Always present (constructed once at startup);
    /// only active when toggled via <see cref="ToggleRecordingCommand"/>.</summary>
    public SessionRecorder Recorder { get; }
    public ReactiveCommand<Unit, Unit>                    ResetLayoutCommand       { get; }
    public ReactiveCommand<Unit, Unit>                    ExitCommand              { get; }
    /// <summary>
    /// File → Maps Directory... — opens a folder picker so the user can point
    /// Genie at a different Maps location, e.g. their own <c>git clone</c> of
    /// GenieClient/Maps. Persists to <c>paths.json</c>, then re-scans the new
    /// directory for zones and rebuilds the auto-detect server-id index.
    /// </summary>
    public ReactiveCommand<Unit, Unit>                    SetMapsDirectoryCommand  { get; }

    /// <summary>
    /// File → Open Maps Folder — opens the CURRENT Maps directory in the OS
    /// file browser so the user can see their XML zone files directly.
    /// Complements <see cref="SetMapsDirectoryCommand"/> (which is for
    /// *changing* the directory, not viewing it — a folder picker hides
    /// files which surprised users who clicked it expecting to see maps).
    /// </summary>
    public ReactiveCommand<Unit, Unit>                    OpenMapsFolderCommand    { get; }

    /// <summary>
    /// Opens the recordings directory (`{AppData}/Genie5/Logs/`) in the OS
    /// file browser. Helps users find raw-XML recordings they captured via
    /// File → Record Session — without it they'd have to hand-navigate the
    /// AppData path, which most users can't easily find.
    /// </summary>
    public ReactiveCommand<Unit, Unit>                    OpenRecordingsFolderCommand { get; }

    // ── Tool visibility (display-only; private setters guarantee these only
    // change via the Toggle commands, never via stray binding pushes) ────────
    [Reactive] public bool GameVisible     { get; private set; } = true;
    [Reactive] public bool VitalsVisible   { get; private set; } = true;
    [Reactive] public bool RoomVisible     { get; private set; } = true;
    [Reactive] public bool BackpackVisible { get; private set; } = true;
    [Reactive] public bool MapperVisible   { get; private set; } = true;
    [Reactive] public bool ExperienceVisible { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool LogonsVisible   { get; private set; } = true;
    [Reactive] public bool TalkVisible     { get; private set; } = true;
    [Reactive] public bool WhispersVisible { get; private set; } = true;
    [Reactive] public bool ThoughtsVisible { get; private set; } = true;
    [Reactive] public bool CombatVisible   { get; private set; } = true;
    [Reactive] public bool LogVisible      { get; private set; } = true;
    [Reactive] public bool ItemLogVisible  { get; private set; } = true;

    // ── Toggle commands (one per dockable) ───────────────────────────────────
    public ReactiveCommand<Unit, Unit> ToggleGameCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleVitalsCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleRoomCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleBackpackCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMapperCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleExperienceCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLogonsCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleTalkCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleWhispersCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThoughtsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCombatCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleLogCommand      { get; }
    public ReactiveCommand<Unit, Unit> ToggleItemLogCommand  { get; }

    // ── Core ──────────────────────────────────────────────────────────────────

    private GenieCore? _core;

    /// <summary>
    /// Read-only access to the live <see cref="GenieCore"/>. Null before the
    /// first connect. Exposed for code-behind hooks (macro key handler,
    /// Ctrl+Right paste, etc.) that need to dispatch input through
    /// <see cref="GenieCore.ProcessInput"/>.
    /// </summary>
    public GenieCore? Core => _core;

    // ── Profile store ─────────────────────────────────────────────────────────

    /// <summary>
    /// Saved connection profiles. Loaded at startup from <c>Config/profiles.json</c>
    /// under the platform-appropriate user data directory (or alongside the app
    /// in portable mode).
    /// </summary>
    public ProfileStore Profiles { get; } = new();

    /// <summary>
    /// Live display settings (fonts, colors). Edited via Edit → Display Settings.
    /// Changes push directly into <see cref="Avalonia.Application.Resources"/>,
    /// so the UI repaints without per-line property-change notifications.
    /// </summary>
    public DisplaySettings Display { get; }

    /// <summary>
    /// Per-window display settings (title, font, fg/bg, timestamp, redirect).
    /// Edited via Edit → Configuration → Layout. Registered with the known
    /// dock-tool ids at app start; pre-populated with Genie 4 default routing.
    /// </summary>
    public WindowSettingsStore WindowSettings { get; } = new();

    private readonly string _profilesPath;
    private readonly string _displayPath;
    private readonly string _pathsPath;
    private readonly string _configDir;
    private readonly string _defaultMapsDir;

    /// <summary>
    /// In-memory MDI geometry held across a Window-menu mode toggle within a
    /// single session. Captured when leaving windowed mode so toggling back
    /// restores the floating windows where they were. Deliberately NOT
    /// persisted to disk — windowed geometry only survives a restart via a
    /// saved layout (<see cref="SavedLayout.MdiBounds"/>).
    /// </summary>
    private Dictionary<string, Settings.MdiWindowBounds>? _mdiBoundsCache;

    // ── Main-window geometry bridge (set by the View) ───────────────────────
    /// <summary>Set by <c>MainWindow</c>: returns the live window geometry so a
    /// layout save can capture it. Null until the view wires it up.</summary>
    public Func<(double Width, double Height, int X, int Y, bool Maximized)>? CaptureWindowGeometry { get; set; }

    /// <summary>Set by <c>MainWindow</c>: applies geometry from a loaded layout
    /// to the main window. Null until the view wires it up.</summary>
    public Action<double, double, int, int, bool>? ApplyWindowGeometry { get; set; }

    /// <summary>
    /// Directory the <see cref="SessionRecorder"/> writes raw-XML captures to
    /// (`{AppData}/Genie5/Logs/`). Exposed via <see cref="OpenRecordingsFolderCommand"/>
    /// so users can browse captured `.xml` files from the File menu without
    /// hunting through AppData paths.
    /// </summary>
    private readonly string _logsDir;
    private string _pluginsDir = "";

    /// <summary>
    /// Session-scoped map of DR's <c>#NNNN</c> container-item-IDs to their
    /// human <c>title</c> (e.g. <c>#37666728 → "My Backpack"</c>). Populated
    /// from <see cref="ContainerEvent"/>s the parser emits at session start
    /// and whenever containers move. Consumed by <see cref="BuildLinkEcho"/>
    /// to render click-echoes like <c>get a tapered cutlass in My Backpack</c>
    /// instead of leaking the raw <c>in #37666728</c> ID.
    /// <para>
    /// ConcurrentDictionary because writes come from the parser's RX
    /// subscription (any thread) and reads happen on the UI thread when
    /// echoing the link click — the contention is trivial but the type's
    /// thread-safety is free and avoids races.
    /// </para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _containerNouns = new();

    /// <summary>
    /// Loaded path overrides. Currently just <see cref="PathSettings.MapsDirectory"/>;
    /// expanded over time. Persisted to <c>Config\paths.json</c>.
    /// </summary>
    public PathSettings Paths { get; private set; } = new();

    /// <summary>Launch flags parsed from the command line (null in the
    /// design-time / parameterless path). Consumed once by
    /// <see cref="RunStartupConnectAsync"/> after the window is shown.</summary>
    public StartupOptions? Startup { get; }

    public MainWindowViewModel() : this(null) { }

    public MainWindowViewModel(StartupOptions? startup)
    {
        Startup = startup;

        // ── Storage locations ─────────────────────────────────────────────
        // LocalDirectoryService honors portable mode (Config\ next to the exe)
        // and XDG / AppSupport paths on Linux / macOS.
        var dir       = new LocalDirectoryService("Genie5", AppContext.BaseDirectory);
        _configDir    = dir.Current.ValidateDirectory("Config");
        _profilesPath = Path.Combine(_configDir, "profiles.json");
        _displayPath  = Path.Combine(_configDir, "display.json");
        _pathsPath    = Path.Combine(_configDir, "paths.json");
        // Recordings live as a sibling to Config (not under it) — same parent
        // dir, so {AppData}/Genie5/{Config, Logs, Maps, Profiles}/ is the layout.
        _logsDir      = Path.Combine(Path.GetDirectoryName(_configDir)!, "Logs");
        _pluginsDir   = Path.Combine(Path.GetDirectoryName(_configDir)!, "Plugins");

        // Global layout presets — one JSON per layout, at {AppData}/Genie5/Layouts/.
        // Per-profile presets attach on connect (see SetProfileLayoutScope).
        var layoutsDir = Path.Combine(Path.GetDirectoryName(_configDir)!, "Layouts");
        _globalLayouts = new Settings.LayoutStore(layoutsDir);

        ErrorLog.Initialize(_configDir);

        // Maps live at the TOP LEVEL of the data tree, parallel to Config/
        // and Scripts/ — matching the Genie 4 layout users already know
        // (Genie 4 ships Art/, Config/, Help/, Icons/, Logs/, Maps/,
        // Plugins/, Scripts/, Sounds/ all at one level). An earlier rev
        // had Maps inside Config/, which (a) didn't match Genie 4 muscle
        // memory and (b) made the folder hard to point a git-clone at.
        // Users can still override via File → Change Maps Directory... to
        // point at a clone of GenieClient/Maps for git workflow.
        _defaultMapsDir     = dir.Current.ResolvePath("Maps");
        Paths               = PathSettings.Load(_pathsPath);

        // ── One-time migration: Config\Maps → Maps ─────────────────────────
        // Earlier builds defaulted to Config\Maps. If that folder exists with
        // files AND the new top-level Maps is empty/missing, move the files
        // up. Move (not copy) so we don't leave stale duplicates that drift
        // out of sync. If the user has explicitly pointed Paths.MapsDirectory
        // elsewhere, do nothing — their override wins.
        if (string.IsNullOrWhiteSpace(Paths.MapsDirectory))
        {
            var legacyMapsDir = Path.Combine(_configDir, "Maps");
            var newHasFiles   = Directory.Exists(_defaultMapsDir) &&
                                Directory.GetFiles(_defaultMapsDir, "*.xml").Length > 0;
            var legacyHasFiles = Directory.Exists(legacyMapsDir) &&
                                 Directory.GetFiles(legacyMapsDir, "*.xml").Length > 0;
            if (legacyHasFiles && !newHasFiles)
            {
                try
                {
                    Directory.CreateDirectory(_defaultMapsDir);
                    foreach (var src in Directory.GetFiles(legacyMapsDir, "*.xml"))
                    {
                        var dst = Path.Combine(_defaultMapsDir, Path.GetFileName(src));
                        if (!File.Exists(dst))
                            File.Move(src, dst);
                    }
                    // Try to remove the now-empty legacy folder. Quietly skip
                    // if it still has non-XML files — better to leave the
                    // empty husk than to lose data we didn't expect.
                    if (Directory.Exists(legacyMapsDir) &&
                        Directory.GetFileSystemEntries(legacyMapsDir).Length == 0)
                        Directory.Delete(legacyMapsDir);
                }
                catch (Exception ex) { ErrorLog.Log("MigrateMapsToTopLevel", ex); }
            }
        }

        // One-time import from a Genie 4 install. If the user has no explicit
        // override and the new location is empty, look for an existing Genie 4
        // Maps directory at %APPDATA%\Genie Client 4\Maps\ and *copy* the
        // XMLs over. Copy (not move) because many users still run Genie 4
        // alongside Genie 5 — taking their data away would break that.
        //
        // Path is Windows-specific; on macOS/Linux the resolved directory
        // simply won't exist, and the whole block is a quiet no-op.
        if (string.IsNullOrWhiteSpace(Paths.MapsDirectory) &&
            (!Directory.Exists(_defaultMapsDir) ||
             Directory.GetFiles(_defaultMapsDir, "*.xml").Length == 0))
        {
            var genie4MapsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Genie Client 4",
                "Maps");

            if (Directory.Exists(genie4MapsDir) &&
                Directory.GetFiles(genie4MapsDir, "*.xml").Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(_defaultMapsDir);
                    foreach (var src in Directory.GetFiles(genie4MapsDir, "*.xml"))
                    {
                        var dst = Path.Combine(_defaultMapsDir, Path.GetFileName(src));
                        if (!File.Exists(dst)) File.Copy(src, dst);
                    }
                }
                catch (Exception ex) { ErrorLog.Log("ImportGenie4Maps", ex); }
            }
        }

        Mapper.MapsDirectory = string.IsNullOrWhiteSpace(Paths.MapsDirectory)
            ? _defaultMapsDir
            : Paths.MapsDirectory;

        Profiles.Load(_profilesPath);

        Display = DisplaySettings.Load(_displayPath);
        Display.Apply();  // push values into Application.Resources
        Mapper.AttachDisplay(Display, _displayPath);

        // Register every known dockable with the window-settings store so the
        // Layout tab in Configuration sees the full list. Genie 4 standard ids
        // (talk, whispers, …) inherit IfClosed defaults from the store's
        // built-in table; ours (game-text, vitals, …) just take generic defaults.
        WindowSettings.Register("game-text", "Game");
        WindowSettings.Register("vitals",    "Vitals");
        WindowSettings.Register("room",      "Room");
        WindowSettings.Register("backpack",  "Backpack");
        WindowSettings.Register("logons",    "Logons");
        WindowSettings.Register("talk",      "Talk");
        WindowSettings.Register("whispers",  "Whispers");
        WindowSettings.Register("thoughts",  "Thoughts");
        WindowSettings.Register("combat",    "Combat");
        WindowSettings.Register("log",       "Log");
        WindowSettings.Register("itemlog",   "ItemLog");
        WindowSettings.Register("mapper",    "Mapper");
        WindowSettings.Register("experience", "Experience");

        // ── Global → per-window propagation ─────────────────────────────
        // When the user changes DisplaySettings (color, font, etc.), the
        // updated values get pushed into Application.Resources by
        // Display.Apply()'s internal subscription. But each tool's
        // ApplySettings() resolves its per-window foreground/font ONCE at
        // construct + on per-window WindowSettings.Changed — it doesn't
        // see global resource changes on its own.
        //
        // For windows using the "Use default" sentinel
        // (Foreground="Default" / FontFamily=""/ FontSize=0), we want a
        // global change to ripple through. Solution: fire NotifyChanged()
        // on every registered WindowSettings whenever the relevant Display
        // property changes — that re-runs ApplySettings, which re-runs
        // WindowSettingsResolver.* against the freshly-pushed resources.
        //
        // Skip(1) suppresses the initial-value fire (Display.Apply() already
        // pushed the seeded values; nothing to repaint yet at construct).
        Display.WhenAnyValue(
                x => x.GameColorHex,
                x => x.EchoColorHex,
                x => x.FontFamily,
                x => x.FontSize)
            .Skip(1)
            .Subscribe(_ =>
            {
                foreach (var s in WindowSettings.All.Values)
                    s.NotifyChanged();
            });

        Command = new CommandViewModel(SendCommand);

        var factory = new GenieDockFactory(this);

        // Keep the Window-menu check marks aligned with the dock's actual state:
        // when the user closes a tab via its X, the factory raises DockableClosed
        // and we flip the matching bool to false. Same for DockableAdded on re-open.
        factory.DockableClosed += (_, e) =>
        {
            if (e.Dockable?.Id is { Length: > 0 } id)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SetVisibilityBool(id, false));
        };
        factory.DockableAdded += (_, e) =>
        {
            if (e.Dockable?.Id is { Length: > 0 } id)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => SetVisibilityBool(id, true));
        };

        DockFactory = factory;
        // Always start in the built-in tabbed layout. The document mode,
        // window geometry, and MDI window positions are NOT auto-restored from
        // the last session — they only load when a layout profile is applied
        // (the Layout menu, or the per-profile / global default layout on
        // connect via ApplyDefaultLayoutForConnect).
        DockLayout  = factory.BuildDefaultLayout();

        // Wire the Mapper's "pop out" button. Done here (after factory exists)
        // because the VM doesn't carry a factory reference itself.
        Mapper.FloatRequested = () => factory.FloatTool("mapper");

        ConnectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var result = await ShowConnectDialog.Handle(Unit.Default);
            if (result is not null) await ConnectAsync(result.Config, result.Profile);
        });

        DisplaySettingsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var ok = await ShowDisplaySettingsDialog.Handle(Display);
            if (ok) Display.Save(_displayPath);
        });

        ConfigurationCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            // The dialog scopes every config file to the selected profile.
            // When the selected profile matches the connected one, edits go
            // straight to the live engines; otherwise edits act on draft
            // engines loaded from that profile's directory.
            var cfgVm = new ConfigurationViewModel(_core, _configDir, Profiles, ConnectedProfile, WindowSettings);
            await ShowConfigurationDialog.Handle(cfgVm);
        });

        // File -> Import from Genie 4...
        // Opens the Genie4ImportDialog with auto-detected source path +
        // probe + per-type checkboxes + Global/Profile routing. Disabled
        // when no GenieCore is wired (pre-connect) — the dialog needs the
        // live engines to apply imports to.
        Genie4ImportCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_core is null)
            {
                GameText.AddSystemLine("[import] Import is available once the app has finished initialising.");
                return;
            }
            var profileDir   = ConnectedProfile is not null ? GetProfileConfigDir(ConnectedProfile) : null;
            var profileChar  = ConnectedProfile?.CharacterName;
            var importVm = new Genie4ImportViewModel(_core, _configDir, profileDir, profileChar);
            await ShowGenie4ImportDialog.Handle(importVm);
        });

        // File -> Cross-Zone Connections...
        // Opens the meta-graph editor where users curate transit links
        // between zones (boats, climb-walls, etc.). The file lives at
        // {MapsDirectory}/ZoneConnections.xml — same dir as the zone
        // XMLs, so the community Maps repo absorbs it on `git pull`.
        ZoneConnectionsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var mapsDir = Mapper.MapsDirectory;
            if (string.IsNullOrWhiteSpace(mapsDir))
            {
                GameText.AddSystemLine("[mapper] No Maps directory set — pick one via File → Change Maps Directory first.");
                return;
            }
            var repo = new Genie.Core.Mapper.ZoneConnectionsRepository(
                System.IO.Path.Combine(mapsDir, "ZoneConnections.xml"));
            var vm = new ZoneConnectionsViewModel(repo);
            await ShowZoneConnectionsDialog.Handle(vm);
        });

        // ── Layout save/load (Workspace presets) ──────────────────────────
        // Captures current panel arrangement + display flags into a named
        // JSON file; loading restores all of them. Matches the muscle
        // memory of Genie 4's Layout menu: "I have a hunt layout and a
        // crafting layout, switch with one click."

        // Save As: prompt for a name via the Interaction, then capture and
        // persist to the ACTIVE store (connected profile, else global). Empty
        // name = user cancelled the prompt.
        SaveLayoutAsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var prompt = new LayoutSavePrompt(
                DefaultName:      $"Layout {DateTime.Now:yyyy-MM-dd}",
                ProfileAvailable: _profileLayouts is not null,
                ProfileNames:     _profileLayouts?.List() ?? Array.Empty<string>(),
                GlobalNames:      _globalLayouts.List());

            var result = await ShowLayoutSavePrompt.Handle(prompt);
            if (result is null || string.IsNullOrWhiteSpace(result.Name)) return;

            var store = result.Scope == LayoutScope.Profile && _profileLayouts is not null
                ? _profileLayouts
                : _globalLayouts;

            var layout  = CaptureCurrentLayout();
            layout.Name = result.Name.Trim();
            store.Save(layout);
            RefreshSavedLayoutList();
            GameText.AddSystemLine(
                $"[layout] saved '{layout.Name}' ({(ReferenceEquals(store, _profileLayouts) ? "profile" : "global")})");
        });

        // Load: parameter is the menu item, which carries the layout's scope so
        // we read from the right store. Applies the saved state to the live VM.
        LoadLayoutCommand = ReactiveCommand.Create<LayoutMenuItem>(item =>
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name)) return;
            var store  = item.Scope == LayoutScope.Profile ? _profileLayouts : _globalLayouts;
            var loaded = store?.Load(item.Name);
            if (loaded is null)
            {
                GameText.AddSystemLine($"[layout] could not load '{item.Name}'");
                return;
            }
            ApplyLayout(loaded);
            GameText.AddSystemLine(
                $"[layout] loaded '{item.Name}'{(item.Scope == LayoutScope.Global ? " (global)" : "")}");
        });

        // Reset: bin the current state and let the dock factory rebuild
        // from the canonical 3-column default. Useful as an "I broke
        // something" escape hatch.
        ResetLayoutCommand = ReactiveCommand.Create(() =>
        {
            ApplyLayout(new Settings.SavedLayout
            {
                Name              = "Default",
                VisibleTools      = new List<string> {
                    "game-text", "room", "backpack", "mapper",
                    "logons", "talk", "whispers", "thoughts", "combat",
                },
                HandsStripVisible    = true,
                HandsStripAtBottom   = true,
                ShowStatusBar        = true,
                RoundTimeOnHandsStrip= false,
                ShowGameText         = true,
                ShowEchoText         = true,
                ShowScriptText       = true,
            });
            GameText.AddSystemLine("[layout] reset to default");
        });

        // Refresh the menu's "Load ▶" list — called on SubmenuOpened
        // for the Layout menu so we re-read disk if new files landed.
        RefreshLayoutListCommand = ReactiveCommand.Create(RefreshSavedLayoutList);

        // Manage Layouts… — copy between scopes/profiles, set defaults,
        // rename, delete. Build a VM over the current stores + all profiles,
        // show the dialog, then refresh the Load menu since the set may change.
        ManageLayoutsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var vm = new ManageLayoutsViewModel(
                _globalLayouts,
                _profileLayouts,
                ConnectedProfile,
                Profiles.Profiles,
                p => new Settings.LayoutStore(Path.Combine(GetProfileConfigDir(p), "Layouts")),
                Display,
                () => Display.Save(_displayPath),
                SaveProfiles);
            await ShowManageLayoutsDialog.Handle(vm);
            RefreshSavedLayoutList();
        });

        // ── Help → Check for Updates ────────────────────────────────────────
        // Builds a fresh UpdatesDialogViewModel each open so it picks up
        // any feed-config edits made via the #plugin add command bar verb.
        // Maps directory + zone repo + plugin manager come from the live core
        // when connected; pre-connect, the Maps tab works against the
        // configured default directory but plugin install/update needs a
        // running PluginManager to hot-swap (otherwise it just drops the DLL).
        ShowUpdatesCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var store      = new Genie.Core.Update.FeedConfigStore(_configDir);
            var mapsDir    = Paths?.MapsDirectory is { Length: > 0 } md
                                 ? md
                                 : Path.Combine(Path.GetDirectoryName(_configDir)!, "Maps");
            var zoneRepo   = _core?.ZoneRepository ?? new Genie.Core.Mapper.MapZoneRepository();
            var pluginMgr  = _core?.Plugins;
            var vm = new UpdatesDialogViewModel(store, mapsDir, _pluginsDir, zoneRepo, pluginMgr);
            await ShowUpdatesDialog.Handle(vm);
            // Re-run a background check so the badge reflects post-dialog state
            // (a successful update during the session should clear the dot).
            _ = CheckForUpdatesInBackgroundAsync();
        });

        // All Help-menu external links funnel through OpenUrl() (defined below),
        // which hands the URL to the OS shell. Ported from the Genie 4 Help menu;
        // the repo links target GenieClient/Genie5, the community links are the
        // same game/community resources Genie 4 shipped.
        OpenDiscordCommand         = ReactiveCommand.Create(() => OpenUrl("https://discord.gg/MtmzE2w",                     "Discord"));
        OpenLatestReleaseCommand   = ReactiveCommand.Create(() => OpenUrl("https://github.com/GenieClient/Genie5/releases/latest", "the releases page"));
        OpenGitHubCommand          = ReactiveCommand.Create(() => OpenUrl("https://github.com/GenieClient/Genie5",          "GitHub"));
        OpenWikiCommand            = ReactiveCommand.Create(() => OpenUrl("https://github.com/GenieClient/Genie5/wiki",     "the wiki"));
        OpenPlayNetCommand         = ReactiveCommand.Create(() => OpenUrl("https://www.play.net/dr",                        "Play.net"));
        OpenElanthipediaCommand    = ReactiveCommand.Create(() => OpenUrl("https://elanthipedia.play.net",                 "Elanthipedia"));
        OpenDrServiceCommand       = ReactiveCommand.Create(() => OpenUrl("https://drservice.info",                        "DR Service"));
        OpenLichDiscordCommand     = ReactiveCommand.Create(() => OpenUrl("https://discord.gg/uxZWxuX",                     "the Lich Discord"));
        OpenIsharonSettingsCommand = ReactiveCommand.Create(() => OpenUrl("https://www.elanthia.org/GenieSettings/",       "Isharon's Genie Settings"));

        // ── Scripts menu (Genie 4 parity) ───────────────────────────────────
        // All routes through #-verbs the CommandEngine already handles via
        // ICommandHost. Going through ProcessInput keeps the audit trail
        // identical to a typed command, which matters for the alias / trigger
        // pipeline and the Game-window echo.
        ListRunningScriptsCommand = ReactiveCommand.Create(() => _core?.Commands.ProcessInput("#scripts"));
        PauseAllScriptsCommand    = ReactiveCommand.Create(() => _core?.Commands.ProcessInput("#pauseall"));
        ResumeAllScriptsCommand   = ReactiveCommand.Create(() => _core?.Commands.ProcessInput("#resumeall"));
        AbortAllScriptsCommand    = ReactiveCommand.Create(() => _core?.Commands.ProcessInput("#stopall"));
        TraceAllScriptsCommand    = ReactiveCommand.Create<string>(level => _core?.Commands.ProcessInput($"#traceall {level ?? "0"}"));

        OpenScriptsFolderCommand = ReactiveCommand.Create(() =>
        {
            // Open the SAME folder the script engine actually loads from —
            // Core's resolved ScriptDir — so the menu and the engine never
            // disagree (issue #37). This also honors a per-profile data root
            // and portable mode automatically, since Core's ScriptDir tracks
            // the active data root. Pre-connect, fall back to the shared root
            // next to the config dir.
            var scriptsDir = _core is not null
                                 ? _core.Config.ScriptDir
                                 : Path.Combine(Path.GetDirectoryName(_configDir)!, "Scripts");
            try
            {
                if (!Directory.Exists(scriptsDir)) Directory.CreateDirectory(scriptsDir);
                // Cross-platform folder open: ShellExecute on Windows, `open`
                // on macOS, `xdg-open` on Linux. Same approach as the Maps
                // folder open under File → Maps.
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(scriptsDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                GameText.AddSystemLine($"[scripts] could not open {scriptsDir} ({ex.Message})");
            }
        });

        // Fire-and-forget startup check so the Help-menu badge surfaces
        // any pending updates without the user having to open the dialog.
        // Errors are swallowed — badge stays clear on network failure.
        _ = CheckForUpdatesInBackgroundAsync();

        this.WhenAnyValue(x => x.UpdatesAvailable)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HelpMenuHeader)));

        // ── Plugins menu ────────────────────────────────────────────────────
        RefreshPluginListCommand = ReactiveCommand.Create(RefreshPluginList);
        OpenPluginsFolderCommand = ReactiveCommand.Create(OpenPluginsFolder);
        ReloadPluginsCommand     = ReactiveCommand.Create(() =>
        {
            if (_core is null) { GameText.AddSystemLine("[plugin] connect first to (re)load plugins."); return; }
            _core.Plugins.DiscoverAndLoad(_pluginsDir);
            RefreshPluginList();
            GameText.AddSystemLine($"[plugin] {_core.Plugins.Plugins.Count} plugin(s) loaded.");
        });

        ToggleStatusBarCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowStatusBar = !Display.ShowStatusBar;
            Display.Save(_displayPath);
        });

        // Switch between tabbed/docked and windowed (MDI) document modes.
        // Rebuilds the dock layout from scratch — the panel view-models are
        // reused, only the container tree changes — then re-syncs the
        // Window-menu check marks against the freshly built tree.
        ToggleWindowedModeCommand = ReactiveCommand.Create(() =>
        {
            if (DockFactory is not GenieDockFactory factory) return;
            // Leaving windowed mode — capture the current window geometry into
            // the in-memory cache so toggling back restores positions within
            // this session. (Not written to disk; restart-persistence is via a
            // saved layout only.)
            if (Display.WindowedMode)
                _mdiBoundsCache = factory.CaptureMdiBounds();
            Display.WindowedMode = !Display.WindowedMode;
            DockLayout = Display.WindowedMode
                ? factory.BuildMdiLayout(_mdiBoundsCache)
                : factory.BuildDefaultLayout();
            RefreshVisibilityBools();
            GameText.AddSystemLine($"[layout] {(Display.WindowedMode ? "windowed (MDI)" : "tabbed")} mode");
        });

        ToggleGuildInTitleCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowGuildInTitle = !Display.ShowGuildInTitle;
            Display.Save(_displayPath);
        });

        ToggleHandsBarCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowHandsBar = !Display.ShowHandsBar;
            Display.Save(_displayPath);
        });

        HandsBarToTopCommand = ReactiveCommand.Create(() =>
        {
            Display.HandsAtBottom = false;
            Display.Save(_displayPath);
        });

        HandsBarToBottomCommand = ReactiveCommand.Create(() =>
        {
            Display.HandsAtBottom = true;
            Display.Save(_displayPath);
        });

        ToggleEnhancedHandsStripCommand = ReactiveCommand.Create(() =>
        {
            Display.UseEnhancedHandsStrip = !Display.UseEnhancedHandsStrip;
            Display.Save(_displayPath);
        });

        RoundTimeToCommandBarCommand = ReactiveCommand.Create(() =>
        {
            Display.RoundTimeOnHandsStrip = false;
            Display.Save(_displayPath);
        });

        RoundTimeToHandsStripCommand = ReactiveCommand.Create(() =>
        {
            Display.RoundTimeOnHandsStrip = true;
            Display.Save(_displayPath);
        });

        // ── Session recorder ──────────────────────────────────────────────
        // Logs sit beside Config under {AppData}/Genie5/ to mirror the layout
        // TestHarness writes to. Always allocate the recorder (cheap — just
        // ensures the dir exists); IsRecording stays false until the user
        // toggles via the File menu.
        // Wire a diagnostic-log callback so subscribe/stop events surface in
        // the main Game window. Critical for debugging recorder issues —
        // without this, a silent zero-chunks subscription is invisible.
        Recorder = new SessionRecorder(_logsDir, msg =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GameText.AddSystemLine(msg));
        });
        Recorder.CurrentFileChanged += file =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsRecording = file is not null);

        ToggleRecordingCommand = ReactiveCommand.Create(() =>
        {
            if (Recorder.IsRecording)
            {
                Recorder.Stop();
            }
            else if (_core is null)
            {
                // Don't silently no-op — Avalonia's auto-toggle CheckBox on the
                // MenuItem has already flipped the visual checkmark to ✓ at this
                // point, and the user has no way to tell that recording didn't
                // actually start. Tell them in the Game window, and then force
                // IsRecording back to false (the line below) so the menu's
                // OneWay binding repaints it as unchecked.
                GameText.AddSystemLine("[recorder] cannot start — not connected. Connect first, then enable Record Session.");
            }
            else
            {
                var name = !string.IsNullOrWhiteSpace(_core.State.CharacterName)
                    ? _core.State.CharacterName
                    : ConnectedProfile?.CharacterName ?? "unknown";
                Recorder.Start(_core, name);
            }

            // CRITICAL: ALWAYS reconcile the bool to the recorder's actual
            // state at the end of every toggle. Avalonia's `ToggleType=CheckBox`
            // MenuItem auto-toggles its IsChecked on click BEFORE the command
            // runs; if the command then early-returns (e.g. _core was null),
            // the visual ✓ stays on but the recorder never started. Forcing
            // IsRecording = Recorder.IsRecording here pushes the truth back
            // through the OneWay binding so the menu repaints correctly.
            IsRecording = Recorder.IsRecording;
        });

        // Title-bar composition — show "🔴 REC" suffix while recording so the
        // user can't forget the capture is running (matches the compliance
        // review's recommendation that recording always be visibly indicated).
        // The red-circle emoji (U+1F534) is rendered with its intrinsic red
        // glyph by every modern color-emoji font; OS window-title chrome
        // strings are otherwise plain text with no markup for per-char color,
        // so this is the simplest reliable way to make the indicator red.
        this.WhenAnyValue(x => x.ConnectionStatus, x => x.CharacterGuild,
                          x => x.IsRecording, x => x.Display.ShowGuildInTitle)
            .Subscribe(_ =>
            {
                var guild = (Display.ShowGuildInTitle && !string.IsNullOrWhiteSpace(CharacterGuild))
                    ? $" — {CharacterGuild}" : "";
                var rec   = IsRecording ? "  🔴 REC" : "";
                WindowTitle = $"Genie 5 — {ConnectionStatus}{guild}{rec}";
            });

        // Compound visibility: ShowRtInCommandBar is true only when the
        // character is in RT AND the user chose the command-bar position.
        // ShowRtOnHandsStrip is its mirror. Bind both XAML badges to these
        // so toggling the position re-routes the visible badge live without
        // needing a converter or MultiBinding in the XAML.
        this.WhenAnyValue(
                x => x.Vitals.InRoundTime,
                x => x.Display.RoundTimeOnHandsStrip)
            .Subscribe(_ =>
            {
                ShowRtInCommandBar = Vitals.InRoundTime && !Display.RoundTimeOnHandsStrip;
                ShowRtOnHandsStrip = Vitals.InRoundTime &&  Display.RoundTimeOnHandsStrip;
            });

        // ── Per-dockable toggle commands ───────────────────────────────────
        // Each command derives the new desired state from the dock factory's
        // actual state (not from the bool), so the menu and dock can never
        // get out of sync even if something was closed via the dock's own X.
        ToggleGameCommand     = MakeToggleCommand("game-text", v => GameVisible     = v);
        ToggleVitalsCommand   = MakeToggleCommand("vitals",    v => VitalsVisible   = v);
        ToggleRoomCommand     = MakeToggleCommand("room",      v => RoomVisible     = v);
        ToggleBackpackCommand = MakeToggleCommand("backpack",  v => BackpackVisible = v);
        ToggleMapperCommand   = MakeToggleCommand("mapper",    v => MapperVisible   = v);
        ToggleExperienceCommand = MakeToggleCommand("experience", v => ExperienceVisible = v);
        ToggleLogonsCommand   = MakeToggleCommand("logons",    v => LogonsVisible   = v);
        ToggleTalkCommand     = MakeToggleCommand("talk",      v => TalkVisible     = v);
        ToggleWhispersCommand = MakeToggleCommand("whispers",  v => WhispersVisible = v);
        ToggleThoughtsCommand = MakeToggleCommand("thoughts",  v => ThoughtsVisible = v);
        ToggleCombatCommand   = MakeToggleCommand("combat",    v => CombatVisible   = v);
        ToggleLogCommand      = MakeToggleCommand("log",       v => LogVisible      = v);
        ToggleItemLogCommand  = MakeToggleCommand("itemlog",   v => ItemLogVisible  = v);

        ResetLayoutCommand = ReactiveCommand.Create(() =>
        {
            if (DockFactory is not GenieDockFactory factory) return;
            foreach (var id in new[] { "game-text", "vitals", "room", "backpack", "mapper", "logons", "talk", "whispers", "thoughts", "combat", "log", "itemlog" })
                factory.SetToolVisibility(id, true);

            GameVisible   = VitalsVisible   = RoomVisible    = BackpackVisible = MapperVisible = true;
            LogonsVisible = TalkVisible     = WhispersVisible = ThoughtsVisible = CombatVisible = true;
            LogVisible    = ItemLogVisible  = true;
        });

        DisconnectCommand = ReactiveCommand.CreateFromTask(
            DisconnectAsync,
            this.WhenAnyValue(x => x.IsConnected));

        ExitCommand = ReactiveCommand.Create(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });

        SetMapsDirectoryCommand     = ReactiveCommand.CreateFromTask(SetMapsDirectoryAsync);
        OpenMapsFolderCommand       = ReactiveCommand.Create(OpenMapsFolder);
        OpenRecordingsFolderCommand = ReactiveCommand.Create(OpenRecordingsFolder);

        // ── Exception logging for every async ReactiveCommand ────────────────
        // Without a subscriber on ThrownExceptions, ReactiveUI considers the
        // exception unobserved and tears down the process. Route everything
        // to ErrorLog so causes are recorded instead of silent exits.
        // (This block lives at the END of the constructor on purpose — every
        // command above must already be assigned before we deref it.)
        ConfigurationCommand  .ThrownExceptions.Subscribe(ex => ErrorLog.Log("ConfigurationCommand",   ex));
        ConnectCommand        .ThrownExceptions.Subscribe(ex => ErrorLog.Log("ConnectCommand",         ex));
        DisconnectCommand     .ThrownExceptions.Subscribe(ex => ErrorLog.Log("DisconnectCommand",      ex));
        DisplaySettingsCommand.ThrownExceptions.Subscribe(ex => ErrorLog.Log("DisplaySettingsCommand", ex));
    }

    /// <summary>
    /// Persist the current <see cref="Profiles"/> list to disk. Called by the
    /// connect dialog after a Save / Delete action.
    /// </summary>
    public void SaveProfiles() => Profiles.Save(_profilesPath);

    /// <summary>
    /// File → Maps Directory... Opens a native folder picker, persists the
    /// choice, and re-points the Mapper VM at the new directory. Designed to
    /// be aimed at a <c>git clone</c> of GenieClient/Maps so users can edit
    /// zones in Genie and contribute back via standard git workflow.
    /// </summary>
    /// <summary>
    /// Open the current Maps directory in the host OS's file browser
    /// (Explorer on Windows, Finder on macOS, xdg-open on Linux).
    /// Uses an OS-specific launcher because passing the directory path as
    /// the FileName of <see cref="System.Diagnostics.ProcessStartInfo"/>
    /// fails on Windows ("Location is not available") — Explorer needs the
    /// path as an ARGUMENT, not as the executable.
    /// </summary>
    // ── #plugin command bar handler ─────────────────────────────────────────
    //
    //   #plugin                       list loaded + available
    //   #plugin list
    //   #plugin enable  <id|name>     enable a loaded plugin
    //   #plugin disable <id|name>     disable a loaded plugin
    //   #plugin unload  <id|name>     unload a loaded plugin (releases the .dll)
    //   #plugin load    <file>        load a .dll from the Plugins folder
    //   #plugin reload                re-scan the folder, load all new
    //   #plugin folder                open the Plugins folder
    //   #plugin sources               list configured plugin update feeds
    //   #plugin add     <url>         add a plugin source (paste GitHub URL or owner/repo)
    //   #plugin update                check + apply every enabled plugin feed
    //   #plugin update  <id|name>     check + apply one plugin feed
    private void HandlePluginCommand(string args)
    {
        if (_core is null) { GameText.AddSystemLine("[plugin] connect first."); return; }

        var trimmed = (args ?? string.Empty).Trim();
        var split   = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub     = split.Length > 0 ? split[0].ToLowerInvariant() : "list";
        var rest    = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (sub)
        {
            case "":
            case "list":    PluginCmdList();                 break;
            case "enable":  PluginCmdSetEnabled(rest, true); break;
            case "disable": PluginCmdSetEnabled(rest, false);break;
            case "unload":  PluginCmdUnload(rest);           break;
            case "load":    PluginCmdLoad(rest);             break;
            case "reload":  ReloadPluginsCommand.Execute().Subscribe(); RefreshPluginList(); break;
            case "folder":  OpenPluginsFolder();             break;
            case "sources": PluginCmdSources();              break;
            case "add":     PluginCmdAddSource(rest);        break;
            case "update":  _ = PluginCmdUpdateAsync(rest);  break;
            default:
                GameText.AddSystemLine(
                    "[plugin] usage: #plugin [list | enable <id> | disable <id> | unload <id> | load <file> | reload | folder | sources | add <url> | update [<id>]]");
                break;
        }
    }

    private void PluginCmdList()
    {
        GameText.AddSystemLine("[plugin] loaded:");
        var any = false;
        foreach (var p in _core!.Plugins.Plugins)
        {
            GameText.AddSystemLine($"  {p.Name} v{p.Version}  [{p.Id}]  {(p.Enabled ? "enabled" : "disabled")}");
            any = true;
        }
        if (!any) GameText.AddSystemLine("  (none loaded)");

        if (Directory.Exists(_pluginsDir))
        {
            var unloaded = Directory.EnumerateFiles(_pluginsDir, "*.dll")
                .Where(f => !_core.Plugins.IsFileLoaded(f)).ToList();
            if (unloaded.Count > 0)
            {
                GameText.AddSystemLine("[plugin] available to load:");
                foreach (var f in unloaded) GameText.AddSystemLine($"  {Path.GetFileName(f)}");
            }
        }
    }

    /// <summary>Find a loaded plugin by exact id or name (case-insensitive).</summary>
    private Genie.Plugins.IGeniePlugin? FindPlugin(string idOrName)
        => _core!.Plugins.Plugins.FirstOrDefault(p =>
               p.Id.Equals(idOrName,   StringComparison.OrdinalIgnoreCase) ||
               p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    private void PluginCmdSetEnabled(string idOrName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) { GameText.AddSystemLine($"[plugin] usage: #plugin {(enabled ? "enable" : "disable")} <id|name>"); return; }
        if (FindPlugin(idOrName) is not { } p) { GameText.AddSystemLine($"[plugin] not loaded: '{idOrName}'"); return; }
        p.Enabled = enabled;
        RefreshPluginList();
        GameText.AddSystemLine($"[plugin] '{p.Name}' {(enabled ? "enabled" : "disabled")}.");
    }

    private void PluginCmdUnload(string idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) { GameText.AddSystemLine("[plugin] usage: #plugin unload <id|name>"); return; }
        if (FindPlugin(idOrName) is not { } p) { GameText.AddSystemLine($"[plugin] not loaded: '{idOrName}'"); return; }
        _core!.Plugins.Unload(p.Id);
        RefreshPluginList();
        GameText.AddSystemLine($"[plugin] unloaded '{p.Id}'.");
    }

    private void PluginCmdLoad(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) { GameText.AddSystemLine("[plugin] usage: #plugin load <file.dll>"); return; }
        if (!Directory.Exists(_pluginsDir)) { GameText.AddSystemLine("[plugin] no Plugins folder."); return; }

        // Match by filename, with or without the .dll extension (case-insensitive).
        var match = Directory.EnumerateFiles(_pluginsDir, "*.dll").FirstOrDefault(f =>
        {
            var n = Path.GetFileName(f);
            return n.Equals(file, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(n).Equals(file, StringComparison.OrdinalIgnoreCase);
        });

        if (match is null) { GameText.AddSystemLine($"[plugin] no such DLL in Plugins folder: '{file}'"); return; }
        if (_core!.Plugins.IsFileLoaded(match)) { GameText.AddSystemLine($"[plugin] already loaded: {Path.GetFileName(match)}"); return; }

        if (_core.Plugins.LoadFile(match)) GameText.AddSystemLine($"[plugin] loaded from {Path.GetFileName(match)}.");
        else                                GameText.AddSystemLine($"[plugin] no plugin found in {Path.GetFileName(match)}.");
        RefreshPluginList();
    }

    /// <summary>Rebuild the Plugins-menu list from the live session's loaded
    /// plugins. Empty when not connected (plugins load on connect).</summary>
    private void RefreshPluginList()
    {
        PluginMenuItems.Clear();
        AvailablePluginFiles.Clear();
        if (_core is null) return;

        // Loaded plugins (enable/disable + unload).
        foreach (var p in _core.Plugins.Plugins)
        {
            var id = p.Id;
            PluginMenuItems.Add(new PluginMenuItem(p, onUnload: () =>
            {
                _core?.Plugins.Unload(id);
                RefreshPluginList();
                GameText.AddSystemLine($"[plugin] unloaded '{id}'.");
            }));
        }

        // Unloaded DLLs in the folder (load each individually).
        if (Directory.Exists(_pluginsDir))
            foreach (var dll in Directory.EnumerateFiles(_pluginsDir, "*.dll"))
            {
                if (_core.Plugins.IsFileLoaded(dll)) continue;
                var path = dll;
                AvailablePluginFiles.Add(new PluginFileItem(Path.GetFileName(dll), onLoad: () =>
                {
                    if (_core?.Plugins.LoadFile(path) == true)
                        GameText.AddSystemLine($"[plugin] loaded from {Path.GetFileName(path)}.");
                    else
                        GameText.AddSystemLine($"[plugin] no plugin found in {Path.GetFileName(path)}.");
                    RefreshPluginList();
                }));
            }
    }

    private void OpenPluginsFolder() => OpenFolder(_pluginsDir, "OpenPluginsFolder");

    // ── Help → Check for Updates: background check ─────────────────────────
    //
    // Runs cheap CheckAsync on every enabled feed in the background and sets
    // UpdatesAvailable. Drives the Help-menu badge so the user sees there's
    // something to update without having to open the dialog. Failures are
    // silent — we'd rather a flaky network not produce a misleading badge.
    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            var store   = new Genie.Core.Update.FeedConfigStore(_configDir);
            var cfg     = store.Load();
            var mapsDir = Paths?.MapsDirectory is { Length: > 0 } md
                              ? md
                              : Path.Combine(Path.GetDirectoryName(_configDir)!, "Maps");
            var zoneRepo = _core?.ZoneRepository ?? new Genie.Core.Mapper.MapZoneRepository();
            var pluginMgr = _core?.Plugins;
            var channel   = string.IsNullOrWhiteSpace(cfg.Core.Channel) ? "stable" : cfg.Core.Channel;
            var any       = false;

            foreach (var feed in cfg.Maps.Where(f => f.Enabled))
            {
                try
                {
                    var src = new Genie.Core.Update.Sources.GithubContentsSource(
                        feed.Owner, feed.Repo, feed.Path, feed.Extension);
                    var u = new Genie.Core.Update.Updaters.MapsUpdater(
                        zoneRepo, mapsDir, new[] { (Genie.Core.Update.Sources.IFileListSource)src });
                    if ((await u.CheckAsync()).UpdateAvailable) any = true;
                }
                catch { /* silent — see method header */ }
            }

            foreach (var feed in cfg.Plugins.Where(f => f.Enabled))
            {
                try
                {
                    var src = new Genie.Core.Update.Sources.GithubReleasesSource(feed.Owner, feed.Repo);
                    var u = new Genie.Core.Update.Updaters.PluginUpdater(
                        feed, src, _pluginsDir, pluginMgr, channel);
                    if ((await u.CheckAsync()).UpdateAvailable) any = true;
                }
                catch { /* silent — see method header */ }
            }

            // Core app — only meaningful when running from a Velopack install
            // (CoreAppUpdater short-circuits with "(dev build)" otherwise, so
            // there's no risk of spurious badges from dev runs).
            try
            {
                var coreUrl = $"https://github.com/{cfg.Core.Owner}/{cfg.Core.Repo}";
                var core    = new Services.CoreAppUpdater(coreUrl, channel);
                if ((await core.CheckAsync()).UpdateAvailable) any = true;
            }
            catch { /* silent — see method header */ }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdatesAvailable = any);
        }
        catch { /* belt-and-braces */ }
    }

    // ── #plugin sources / add / update ─────────────────────────────────────
    //
    // Backend lives in Genie.Core.Update (see PluginUpdater + GithubReleasesSource +
    // PluginSourceParser + FeedConfigStore). Phase 3 will surface the same operations
    // through a proper Updates dialog; until then these command-line entry points let
    // the user manage and exercise plugin feeds.

    private Genie.Core.Update.FeedConfigStore PluginFeedStore()
        => new(_configDir);

    private void PluginCmdSources()
    {
        var cfg = PluginFeedStore().Load();
        GameText.AddSystemLine("[plugin] sources:");
        if (cfg.Plugins.Count == 0)
        {
            GameText.AddSystemLine("  (none configured) — try `#plugin add https://github.com/Owner/Repo`");
            return;
        }
        foreach (var f in cfg.Plugins)
        {
            var enabled = f.Enabled ? "on " : "off";
            var src     = string.IsNullOrEmpty(f.Owner) ? f.ManifestUrl : $"{f.Owner}/{f.Repo}";
            GameText.AddSystemLine($"  [{enabled}] {f.Name}  ({f.Kind} · {src})  asset={f.AssetPattern}");
        }
    }

    private void PluginCmdAddSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            GameText.AddSystemLine("[plugin] usage: #plugin add <github-url-or-owner/repo>");
            return;
        }

        if (!Genie.Core.Update.PluginSourceParser.TryParse(url, out var entry, out var err))
        {
            GameText.AddSystemLine($"[plugin] {err}");
            return;
        }

        var store = PluginFeedStore();
        var cfg   = store.Load();
        // Dedupe by id — re-adding overwrites the prior entry rather than duplicating.
        var existing = cfg.Plugins.FirstOrDefault(p =>
            p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            cfg.Plugins.Remove(existing);
            GameText.AddSystemLine($"[plugin] (replacing existing entry for {entry.Id})");
        }
        cfg.Plugins.Add(entry);

        if (store.Save(cfg))
            GameText.AddSystemLine($"[plugin] added '{entry.Name}' ({entry.Owner}/{entry.Repo}, asset {entry.AssetPattern}). Run `#plugin update {entry.Name}` to install.");
        else
            GameText.AddSystemLine($"[plugin] failed to save {store.FilePath}");
    }

    private async Task PluginCmdUpdateAsync(string idOrName)
    {
        var cfg     = PluginFeedStore().Load();
        var channel = cfg.Core.Channel;
        var targets = string.IsNullOrWhiteSpace(idOrName)
            ? cfg.Plugins.Where(p => p.Enabled).ToList()
            : cfg.Plugins.Where(p =>
                  p.Enabled &&
                  (p.Id.Equals(idOrName,   StringComparison.OrdinalIgnoreCase) ||
                   p.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase))).ToList();

        if (targets.Count == 0)
        {
            GameText.AddSystemLine(string.IsNullOrWhiteSpace(idOrName)
                ? "[plugin] no enabled plugin sources to update."
                : $"[plugin] no enabled source matches '{idOrName}'. Try `#plugin sources`.");
            return;
        }

        foreach (var feed in targets)
        {
            await PluginCmdUpdateOneAsync(feed, channel);
        }
    }

    private async Task PluginCmdUpdateOneAsync(Genie.Core.Update.FeedEntry feed, string channel)
    {
        Genie.Core.Update.Sources.IReleaseSource source;
        switch (feed.Kind.ToLowerInvariant())
        {
            case "github-releases":
                source = new Genie.Core.Update.Sources.GithubReleasesSource(feed.Owner, feed.Repo);
                break;
            default:
                GameText.AddSystemLine($"[plugin] source kind '{feed.Kind}' not supported yet (entry: {feed.Name}).");
                return;
        }

        var updater = new Genie.Core.Update.Updaters.PluginUpdater(
            feed:       feed,
            source:     source,
            pluginsDir: _pluginsDir,
            manager:    _core!.Plugins,
            channel:    channel);

        var progress = new Progress<Genie.Core.Update.UpdateProgress>(p =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                GameText.AddSystemLine($"[plugin:{feed.Name}] {p.Item} — {p.Status}")));

        try
        {
            var check = await updater.CheckAsync();
            if (!check.UpdateAvailable)
            {
                GameText.AddSystemLine($"[plugin:{feed.Name}] up to date ({check.LatestVersion}).");
                return;
            }

            GameText.AddSystemLine($"[plugin:{feed.Name}] update available: {updater.CurrentVersion} → {check.LatestVersion}");
            var result = await updater.ApplyAsync(progress);
            GameText.AddSystemLine($"[plugin:{feed.Name}] {result.Summary}");
            foreach (var e in result.Errors) GameText.AddSystemLine($"[plugin:{feed.Name}] ERROR: {e}");
            RefreshPluginList();
        }
        catch (Exception ex)
        {
            GameText.AddSystemLine($"[plugin:{feed.Name}] FAILED: {ex.Message}");
        }
    }

    /// <summary>Open a folder in the OS file browser, creating it if missing.
    /// Cross-platform (explorer / open / xdg-open).</summary>
    private static void OpenFolder(string dir, string logTag)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var nativePath = Path.GetFullPath(dir);
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start("explorer.exe", nativePath);
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                         System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", nativePath);
            else
                System.Diagnostics.Process.Start("xdg-open", nativePath);
        }
        catch (Exception ex) { ErrorLog.Log(logTag, ex); }
    }

    /// <summary>
    /// Opens <paramref name="url"/> in the user's default browser. Hand off to
    /// the OS shell via <c>UseShellExecute = true</c> — required by .NET for URL
    /// strings (the runtime won't launch them as raw filenames) and the same
    /// cross-platform pattern the parser uses for in-game <c>&lt;a href&gt;</c>
    /// link clicks. On failure we surface the URL in the game window so the user
    /// can open it manually rather than failing silently. <paramref name="label"/>
    /// is the human-readable name used in that fallback message.
    /// </summary>
    private void OpenUrl(string url, string label)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            GameText.AddSystemLine($"[help] could not open {label} ({ex.Message}). Visit {url} manually.");
        }
    }

    private void OpenMapsFolder()
    {
        var dir = Mapper.MapsDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            ErrorLog.Log("OpenMapsFolder", new InvalidOperationException(
                "Maps directory is not configured."));
            return;
        }

        try
        {
            // First-run users typically haven't pulled the Maps repo yet, so
            // the configured directory may not exist on disk. Create it so
            // the menu item actually opens a folder instead of silently
            // failing — matches OpenRecordingsFolder's behavior.
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            // Canonical Windows pattern: Process.Start(filename, argument).
            // Quoting / escaping handled internally. The ProcessStartInfo
            // route with explicit Arguments was failing here with
            // "Location is not available" — likely because the quoted arg
            // was being passed to explorer.exe in a form it interpreted
            // as a literal filename to OPEN (not navigate to). The simpler
            // overload works reliably.
            var nativePath = Path.GetFullPath(dir);
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start("explorer.exe", nativePath);
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                         System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", nativePath);
            else
                System.Diagnostics.Process.Start("xdg-open", nativePath);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("OpenMapsFolder", ex);
        }
    }

    /// <summary>
    /// Opens the recordings folder in the OS file browser. Mirrors
    /// <see cref="OpenMapsFolder"/>. Creates the directory if it doesn't
    /// exist yet (user may have installed but never recorded).
    /// </summary>
    private void OpenRecordingsFolder()
    {
        var dir = _logsDir;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var nativePath = Path.GetFullPath(dir);
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start("explorer.exe", nativePath);
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                         System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", nativePath);
            else
                System.Diagnostics.Process.Start("xdg-open", nativePath);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("OpenRecordingsFolder", ex);
        }
    }

    private async Task SetMapsDirectoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow?.StorageProvider is not { } sp) return;

        IStorageFolder? startLocation = null;
        if (Directory.Exists(Mapper.MapsDirectory))
        {
            try   { startLocation = await sp.TryGetFolderFromPathAsync(Mapper.MapsDirectory); }
            catch { /* picker just falls back to its default location */ }
        }

        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title              = "Choose Maps directory",
            AllowMultiple      = false,
            SuggestedStartLocation = startLocation,
        });

        var folder = picked?.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        Paths.MapsDirectory = path;
        try   { Paths.Save(_pathsPath); }
        catch (Exception ex) { ErrorLog.Log("SaveMapsDirectory", ex); }

        // Re-point the Mapper VM and re-scan. RefreshAvailableZones runs on the
        // current (UI) thread and is cheap; RebuildServerIdIndexAsync kicks
        // off a background scan. Both fire off the MapsDirectory change so
        // the auto-zone-detect index stays in sync with the new location.
        Mapper.MapsDirectory = path;
        Mapper.OnMapsDirectoryChanged();
    }

    /// <summary>
    /// Re-sync every <c>XxxVisible</c> bool to the dock factory's actual state.
    /// Called from the Window menu's SubmenuOpened handler so the check marks
    /// always reflect truth at the moment the user looks at them — robust
    /// against any close/move path that doesn't raise an event we caught.
    /// </summary>
    public void RefreshVisibilityBools()
    {
        if (DockFactory is not GenieDockFactory factory) return;

        RefreshPluginWindowList();   // Window menu just opened — refresh dynamic list too

        SetVisibilityBool("game-text", factory.IsToolVisible("game-text"));
        SetVisibilityBool("vitals",    factory.IsToolVisible("vitals"));
        SetVisibilityBool("room",      factory.IsToolVisible("room"));
        SetVisibilityBool("backpack",  factory.IsToolVisible("backpack"));
        SetVisibilityBool("mapper",    factory.IsToolVisible("mapper"));
        SetVisibilityBool("experience", factory.IsToolVisible("experience"));
        SetVisibilityBool("logons",    factory.IsToolVisible("logons"));
        SetVisibilityBool("talk",      factory.IsToolVisible("talk"));
        SetVisibilityBool("whispers",  factory.IsToolVisible("whispers"));
        SetVisibilityBool("thoughts",  factory.IsToolVisible("thoughts"));
        SetVisibilityBool("combat",    factory.IsToolVisible("combat"));
        SetVisibilityBool("log",       factory.IsToolVisible("log"));
        SetVisibilityBool("itemlog",   factory.IsToolVisible("itemlog"));
    }

    // ── Plugin-created windows ───────────────────────────────────────────────
    //
    // Plugins surface their own dock panels purely through the named-window seam
    // — IPluginHost.SetWindow(name, content) (replace) and EchoToWindow(name,
    // text) (append). The App owns no per-plugin UI; the dock factory spins up a
    // generic PluginWindowTool the first time a plugin writes to a new name, and
    // the panel then behaves like any built-in tool (dockable, floatable,
    // closable, layout-persisted). "Experience" stays special-cased to its own
    // bespoke panel; everything else is dynamic.

    /// <summary>Window names that must NOT be turned into generic plugin panels:
    /// the Experience panel (its own VM) plus the built-in dock tools / streams,
    /// so a stray <c>#echo &gt;combat</c> or a plugin reusing a built-in name
    /// doesn't spawn a confusing duplicate.</summary>
    private static readonly HashSet<string> ReservedWindowNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "experience", "main", "game", "game-text", "room", "vitals",
            "backpack", "mapper", "scripts",
            "logons", "talk", "whispers", "thoughts", "combat",
            "log", "itemlog",
        };

    private static bool IsReservedWindow(string? name)
        => string.IsNullOrWhiteSpace(name) || ReservedWindowNames.Contains(name.Trim());

    /// <summary>Wire the host's plugin-window seam to the dock factory. Both
    /// callbacks marshal to the UI thread — they fire from parser/plugin threads
    /// and mutate the dock tree.</summary>
    private void AttachPluginWindows(GenieCore core)
    {
        // SetWindow(name, content) → replace the panel's contents (snapshot
        // style — how the Experience/Inventory plugins re-render).
        core.SetPluginWindow += (window, content) =>
        {
            if (IsReservedWindow(window)) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DockFactory is GenieDockFactory f)
                    f.GetOrCreatePluginWindow(window).SetContent(content);
            });
        };

        // EchoToWindow(text, name, color) → append a line (log style). Also
        // covers script `#echo >name …`, which previously had no subscriber.
        // show:false — appended lines create the panel on first sight but must
        // not re-open it on every line if the user closed it.
        core.EchoToWindow += (text, window, _) =>
        {
            // First-class log windows: route #echo >log / >itemlog to the
            // built-in stream panels instead of auto-creating a plugin window.
            var w = window?.Trim().ToLowerInvariant();
            if (w is "log" or "itemlog")
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    (w == "log" ? StreamTabs.Log : StreamTabs.ItemLog).Add(text));
                return;
            }
            if (IsReservedWindow(window)) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DockFactory is GenieDockFactory f)
                    f.GetOrCreatePluginWindow(window!, show: false).AppendLine(text);
            });
        };
    }

    /// <summary>Rebuild the Window → Plugin Windows submenu from the factory's
    /// live set of plugin panels. Called when the Window menu opens.</summary>
    private void RefreshPluginWindowList()
    {
        PluginWindowMenuItems.Clear();
        if (DockFactory is not GenieDockFactory factory) return;

        foreach (var (id, title, visible) in factory.PluginWindows())
        {
            var wid = id;   // capture per-iteration for the toggle closure
            PluginWindowMenuItems.Add(new PluginWindowMenuItem(title, visible, () =>
            {
                if (DockFactory is GenieDockFactory f)
                    f.SetToolVisibility(wid, !f.IsToolVisible(wid));
            }));
        }
    }

    /// <summary>
    /// Resolve the per-profile config directory. When <paramref name="profile"/>
    /// is null we use the legacy global <c>Config/</c> directory — preserving
    /// behaviour for users who never picked a profile and for fresh installs.
    /// </summary>
    public string GetProfileConfigDir(ConnectionProfile? profile)
    {
        if (profile is null) return _configDir;

        // Honor a per-profile data root: when the profile points at its own
        // folder, its config (rules, layouts) lives under {DataDirectory}/Config
        // so it stays consistent with Core, which also repoints there. Empty =
        // the default global Config dir.
        var baseConfigDir = profile.DataDirectory is { Length: > 0 } dd
            ? Path.Combine(Path.GetFullPath(dd), "Config")
            : _configDir;

        var dir = Path.Combine(baseConfigDir, "Profiles", profile.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Point the per-profile layout store at the connected profile's dir, or
    /// clear it (global-only) when there's no saved profile. Only real saved
    /// profiles get a scope — a bare-credential connection stays global so we
    /// don't strand presets under a throwaway dir.
    /// </summary>
    private void SetProfileLayoutScope(ConnectionProfile? profile)
    {
        _profileLayouts = profile is null
            ? null
            : new Settings.LayoutStore(Path.Combine(GetProfileConfigDir(profile), "Layouts"));
    }

    /// <summary>
    /// Replay every saved rule file from the connected profile's config dir
    /// (or the global dir for an ad-hoc connection) into the live engines so
    /// anything configured offline via the Configuration dialog is active
    /// from the moment the connection comes up.
    /// </summary>
    private void LoadSavedConfiguration(GenieCore core)
    {
        var p   = new PersistenceService();
        var dir = GetProfileConfigDir(ConnectedProfile);

        SafeLoad(dir, "highlights.json", path =>
        {
            foreach (var m in p.LoadHighlights(path))
            {
                core.Highlights.RemoveRule(m.Pattern);
                core.Highlights.AddRule(
                    m.Pattern, m.ForegroundColor, m.BackgroundColor,
                    Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                    m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(dir, "triggers.json", path =>
        {
            foreach (var m in p.LoadTriggers(path))
            {
                core.Triggers.RemoveTrigger(m.Pattern);
                core.Triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(dir, "substitutes.json", path =>
        {
            foreach (var m in p.LoadSubstitutes(path))
            {
                core.Substitutes.RemoveRule(m.Pattern);
                core.Substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(dir, "gags.json", path =>
        {
            foreach (var m in p.LoadGags(path))
            {
                core.Gags.RemoveRule(m.Pattern);
                core.Gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(dir, "aliases.json", path =>
        {
            foreach (var m in p.LoadAliases(path))
            {
                core.Aliases.RemoveAlias(m.Name);
                core.Aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
            }
        });

        var macrosPath = Path.Combine(dir, "macros.json");
        if (File.Exists(macrosPath))
        {
            SafeLoad(dir, "macros.json", path =>
            {
                foreach (var m in p.LoadMacros(path))
                    core.Macros.Add(m.Key, m.Action);
            });
        }
        else
        {
            // First run for this profile: seed Genie 4's classic numpad
            // movement pad so 10-key travel works out of the box. Persisted
            // so it appears in the Macros panel and the user can edit/remove
            // any of it freely.
            SeedDefaultMovementMacros(core.Macros);
            try { p.SaveMacros(macrosPath, core.Macros.Rules); } catch { /* best-effort seed */ }
        }

        SafeLoad(dir, "variables.json", path =>
        {
            foreach (var m in p.LoadVariables(path))
                core.Variables.Store.Set(m.Name, m.Value);
        });

        SafeLoad(dir, "windows.json", path =>
        {
            foreach (var m in p.LoadWindowSettings(path))
                WindowSettings.Apply(m);
        });
    }

    /// <summary>Run a load callback against <c>{dir}/{name}</c> if it exists, swallowing exceptions.</summary>
    private static void SafeLoad(string dir, string fileName, Action<string> load)
    {
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return;
        try { load(path); } catch { /* corrupt JSON shouldn't block connect */ }
    }

    /// <summary>
    /// Genie 4's classic numpad ("10-key") movement pad. Seeded on first run
    /// for a profile so directional travel works out of the box; the user can
    /// rebind or delete any of these from the Macros panel. Requires NumLock
    /// on (so the OS reports NumPadN rather than the navigation keys).
    /// <code>
    ///   7 nw   8 n    9 ne
    ///   4 w    5 out  6 e
    ///   1 sw   2 s    3 se
    ///   0 down
    /// </code>
    /// </summary>
    private static void SeedDefaultMovementMacros(Genie.Core.Macros.MacroEngine macros)
    {
        // Only fill keys that aren't already bound, so this never clobbers a
        // binding loaded earlier in the connect sequence.
        void Bind(string key, string action)
        {
            if (macros.Get(key) is null) macros.Add(key, action);
        }

        Bind("num8", "n");  Bind("num2", "s");
        Bind("num6", "e");  Bind("num4", "w");
        Bind("num9", "ne"); Bind("num7", "nw");
        Bind("num3", "se"); Bind("num1", "sw");
        Bind("num5", "out"); Bind("num0", "down");
    }

    /// <summary>
    /// Build a toggle command for the named tool. The command always asks the
    /// dock factory for the actual current state and flips it — this keeps the
    /// menu's check mark and the dock's true state aligned even if the user
    /// closed a tool by some other means (e.g. the X on its tab).
    /// </summary>
    private ReactiveCommand<Unit, Unit> MakeToggleCommand(string toolId, Action<bool> updateBool)
        => ReactiveCommand.Create(() =>
        {
            if (DockFactory is not GenieDockFactory factory) return;
            var newVisible = !factory.IsToolVisible(toolId);
            factory.SetToolVisibility(toolId, newVisible);
            // The Opened/Closed events also push this; explicitly setting it
            // here covers cases where the event already fired with the same
            // value (no PropertyChanged would otherwise refresh the binding).
            updateBool(newVisible);
        });

    /// <summary>
    /// Sync the matching <c>XxxVisible</c> bool to <paramref name="visible"/>
    /// for a dockable id reported by the factory's open/close events. We force
    /// a property change even if the value didn't move so the OneWay binding
    /// always re-pushes to <c>MenuItem.IsChecked</c> (otherwise the auto-toggle
    /// from the menu click can leave the visible check mark stuck inverted).
    /// </summary>
    private void SetVisibilityBool(string id, bool visible)
    {
        // Force the property-changed notification by flipping twice when needed.
        switch (id)
        {
            case "game-text": ForceSet(visible, v => GameVisible     = v, () => GameVisible);     break;
            case "vitals":    ForceSet(visible, v => VitalsVisible   = v, () => VitalsVisible);   break;
            case "room":      ForceSet(visible, v => RoomVisible     = v, () => RoomVisible);     break;
            case "backpack":  ForceSet(visible, v => BackpackVisible = v, () => BackpackVisible); break;
            case "mapper":    ForceSet(visible, v => MapperVisible   = v, () => MapperVisible);   break;
            case "experience": ForceSet(visible, v => ExperienceVisible = v, () => ExperienceVisible); break;
            case "logons":    ForceSet(visible, v => LogonsVisible   = v, () => LogonsVisible);   break;
            case "talk":      ForceSet(visible, v => TalkVisible     = v, () => TalkVisible);     break;
            case "whispers":  ForceSet(visible, v => WhispersVisible = v, () => WhispersVisible); break;
            case "thoughts":  ForceSet(visible, v => ThoughtsVisible = v, () => ThoughtsVisible); break;
            case "combat":    ForceSet(visible, v => CombatVisible   = v, () => CombatVisible);   break;
            case "log":       ForceSet(visible, v => LogVisible      = v, () => LogVisible);      break;
            case "itemlog":   ForceSet(visible, v => ItemLogVisible  = v, () => ItemLogVisible);  break;
        }

        static void ForceSet(bool target, Action<bool> set, Func<bool> get)
        {
            if (get() == target)
            {
                // Same value — flip then restore to force a PropertyChanged
                // notification so any OneWay-bound MenuItem.IsChecked refreshes
                // (cures the "checkmark stuck inverted after X-close" bug).
                set(!target);
            }
            set(target);
        }
    }

    private async Task ConnectAsync(ConnectionConfig cfg, ConnectionProfile? profile)
    {
        if (_core is not null)
            await _core.DisposeAsync();

        // Per-profile data root: if the chosen profile points at its own folder,
        // carry that into the core so its data resolves there instead of the
        // default (AppData / portable) root.
        if (profile is { DataDirectory.Length: > 0 })
            cfg = cfg with { DataDirectoryOverride = profile.DataDirectory };

        _core                = new GenieCore(cfg, loggerFactory: null);
        ConnectedProfile     = profile;   // null if user typed credentials without picking a saved profile
        LastConnectionConfig = cfg;       // remembered so reopening Connect after disconnect pre-fills
        CharacterGuild       = "";        // cleared until this session's `info` reveals the guild

        // Guild for the title bar — fires when the player runs `info`. DR has
        // no structured guild tag, so we parse it from the info output.
        _core.GameEvents.OfType<GuildEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => CharacterGuild = e.Guild);

        // Scope the per-profile layout store to this profile (global-only for
        // bare-credential connects), refresh the Load menu, and auto-apply the
        // profile's default layout (falling back to the global default, then
        // the built-in layout if neither resolves).
        SetProfileLayoutScope(profile);
        RefreshSavedLayoutList();
        ApplyDefaultLayoutForConnect(profile);

        _core.ConnectionState.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                IsConnected      = e.Kind == ConnectionEventKind.Connected;
                ConnectionStatus = e.Kind == ConnectionEventKind.Connected
                    ? $"Connected — {cfg.CharacterName}"
                    : e.Kind.ToString();
            });

        // Auto-load every saved rule file into the live engines so anything
        // the user configured offline (via the Configuration dialog) is
        // immediately active. Then expose Highlights to the renderer.
        LoadSavedConfiguration(_core);
        UserHighlights.Engine = _core.Highlights;

        // Wire <d cmd="..."> link clicks to the command pipeline so they
        // behave like the user typed the command. Mirror the ShowLinks
        // config gate so users can opt out of clickable styling entirely.
        // Pass the display text as echoOverride so the Game window shows
        // "get a tapered cutlass" (readable) instead of the raw cmd
        // "get #49489411 in #49489410" (item exist-IDs).
        Highlighting.DefaultHighlights.OnLinkClicked = (cmd, displayText) =>
            _core?.ProcessInput(cmd, echoOverride: BuildLinkEcho(cmd, displayText));
        Highlighting.DefaultHighlights.LinksEnabled  = _core.Config.ShowLinks;

        // External URL hyperlinks (<a href='URL'>) — DR emits these in the
        // news/login resources block (Simucoin Store, Elanthipedia, etc.).
        // Hand them off to the OS shell so the user's default browser opens
        // the URL. UseShellExecute=true is required by .NET for URL strings
        // (the runtime won't launch them as raw filenames).
        Highlighting.DefaultHighlights.OnUrlClicked = url =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorLog.Log("OnUrlClicked", ex);
            }
        };

        // GameText filters lines based on the user's per-tag visibility
        // toggles (Window → Game Window) — supply Display so it can read
        // ShowGameText / ShowEchoText / ShowScriptText at subscription time.
        GameText.DisplaySettings = Display;
        GameText.Attach(_core);
        Vitals.Attach(_core);
        Room.Attach(_core);
        Inventory.Attach(_core);
        Mapper.Attach(_core);
        StreamTabs.Attach(_core);
        Experience.Attach(_core);
        AttachPluginWindows(_core);

        // Load external plugin DLLs from {AppData}/Genie5/Plugins (the builtin
        // Experience plugin is already registered in GenieCore's ctor), then
        // populate the Plugins menu.
        _core.Plugins.DiscoverAndLoad(_pluginsDir);
        RefreshPluginList();
        ScriptBar.Attach(_core);

        // Edit-in-editor requests come from two places, both routed to the
        // same handler: the Script Bar's pencil button (per-running-script),
        // and the `#edit <name>` meta-command (from the command bar). Both
        // ultimately want to open the script file in the user-configured
        // editor (or OS default `.cmd` handler) — so the App owns the
        // launch logic and both sources fan in here.
        ScriptBar.EditScript          += OpenScriptInEditor;
        _core.EditScriptRequested     += name =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenScriptInEditor(name));

        // #layout … from the command bar — dock + store work happens on the
        // UI thread.
        _core.LayoutCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleLayoutCommand(args));

        // #plugin … — load/unload/enable/disable from the command bar.
        _core.PluginCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandlePluginCommand(args));

        // #config / #set / #setting / #settings — open Configuration dialog,
        // or save / load / edit display.json. UI-thread-bound because the
        // dialog handler executes via ReactiveCommand.
        _core.ConfigCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleConfigCommand(args));

        // #goto / #go2 … from the command bar or a script — resolve the room
        // against the active zone and start an attended walk. UI-thread-bound
        // because it touches the mapper VM + AutoWalk timer state.
        _core.MapperGotoRequested     += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Mapper.GotoByName(args));

        // Mapper Edit-Exit requests — user right-clicks a map node, picks
        // "Edit Exit ▶ {verb}". MapperViewModel raises the event, we open
        // the dialog, and on save we ask the mapper to persist the zone.
        Mapper.EditExitRequested += (node, exit) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                var fromTitle = node.Title;
                var toTitle   = exit.DestinationId.HasValue
                                && _core.AutoMapper.ActiveZone.Nodes.TryGetValue(exit.DestinationId.Value, out var toNode)
                    ? toNode.Title
                    : "(unknown)";
                var editVm = new EditExitViewModel(exit, fromTitle, toTitle);
                var ok = await ShowEditExitDialog.Handle(editVm);
                if (ok) Mapper.SaveCurrentZone();
            });

        // ── Container noun map: harvest <container target='#NNNN' title='…'/>
        // events into a per-session dict so BuildLinkEcho can substitute
        // container IDs in click-echoes ("get a cutlass in My Backpack"
        // rather than "get a cutlass in #37666728"). Subscription doesn't
        // need to marshal to the UI thread — the dict is concurrent-safe
        // and only read inside OnLinkClicked (which can run on any thread).
        _core.GameEvents
            .OfType<ContainerEvent>()
            .Subscribe(e =>
            {
                if (string.IsNullOrEmpty(e.TargetId)) return;
                if (string.IsNullOrEmpty(e.Title))
                    _containerNouns.TryRemove(e.TargetId, out _);
                else
                    _containerNouns[e.TargetId] = e.Title;
            });

        // ── Fallback: when a stream tool is hidden, mirror its text into the
        // main Game window so the player still sees it. The StreamBuffer
        // continues to accumulate either way, so the history is preserved
        // when the tool is re-opened.
        _core.GameEvents
            .OfType<TextEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var streamVisible = e.Stream.ToLowerInvariant() switch
                {
                    "logons"   => LogonsVisible,
                    "talk"     => TalkVisible,
                    "whispers" => WhispersVisible,
                    "thoughts" => ThoughtsVisible,
                    "combat"   => CombatVisible,
                    _          => true   // main + non-tool streams: nothing to mirror
                };
                if (!streamVisible)
                    GameText.AddStreamLine(e.Stream, e.Text);
            });

        await _core.ConnectAsync();
    }

    // ── Command-line startup connect ──────────────────────────────────────────

    private bool _startupConnectDone;

    /// <summary>
    /// Acts on <see cref="Startup"/> exactly once: resolves the launch flags
    /// (and/or named profile) into a <see cref="ConnectionConfig"/> and connects
    /// without showing the dialog. Called from the view after the window is
    /// shown. A no-op when there's nothing to connect to, or if an unknown
    /// profile name was supplied (surfaced via the title bar / error log).
    /// </summary>
    public async Task RunStartupConnectAsync()
    {
        if (_startupConnectDone) return;
        _startupConnectDone = true;

        if (Startup is null || !Startup.HasConnectIntent) return;

        try
        {
            var (cfg, profile) = ResolveStartupConfig(Startup);
            if (cfg is null) return;
            await ConnectAsync(cfg, profile);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("RunStartupConnectAsync", ex);
        }
    }

    /// <summary>
    /// Turns launch flags into a connection. A named profile (if found) supplies
    /// the base config; explicit <c>--host</c>/<c>--port</c> override it. With no
    /// profile and no explicit mode, a bare host/port is treated as a Lich proxy
    /// (direct SGE needs a password we never take from the command line).
    /// </summary>
    private (ConnectionConfig? cfg, ConnectionProfile? profile) ResolveStartupConfig(StartupOptions o)
    {
        ConnectionProfile? profile = null;
        ConnectionConfig?  cfg     = null;

        if (!string.IsNullOrWhiteSpace(o.Profile))
        {
            profile = Profiles.Profiles.FirstOrDefault(p =>
                string.Equals(p.Name, o.Profile, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                ConnectionStatus = $"Startup: no profile named '{o.Profile}'";
                ErrorLog.Log("ResolveStartupConfig",
                    new InvalidOperationException($"No profile named '{o.Profile}'"));
                // Fall through: an explicit --host can still drive the connect.
            }
            else
            {
                cfg = ConfigFromProfile(profile);
            }
        }

        // Determine the effective mode: explicit flag > profile's mode > Lich
        // (the only credential-free option valid for a CLI-supplied endpoint).
        var mode = o.Mode ?? cfg?.Mode ?? ConnectionMode.LichProxy;

        // No profile resolved but a host was given → build a fresh config.
        if (cfg is null)
        {
            if (string.IsNullOrWhiteSpace(o.Host) && o.Port is null) return (null, null);
            cfg = new ConnectionConfig { Mode = mode };
        }

        // Apply host/port overrides. For Lich these are the proxy endpoint; for
        // direct SGE they override the eaccess host/port (rarely needed, but
        // honored for symmetry).
        if (cfg.Mode == ConnectionMode.LichProxy || mode == ConnectionMode.LichProxy)
        {
            cfg = cfg with
            {
                Mode          = ConnectionMode.LichProxy,
                LichProxyHost = string.IsNullOrWhiteSpace(o.Host) ? cfg.LichProxyHost : o.Host!,
                LichProxyPort = o.Port ?? cfg.LichProxyPort,
            };
        }
        else
        {
            cfg = cfg with
            {
                SgeHost = string.IsNullOrWhiteSpace(o.Host) ? cfg.SgeHost : o.Host!,
                SgePort = o.Port ?? cfg.SgePort,
            };
        }

        return (cfg, profile);
    }

    /// <summary>Builds a ready-to-connect <see cref="ConnectionConfig"/> from a
    /// saved profile, decrypting the stored password for the SGE path.</summary>
    private ConnectionConfig ConfigFromProfile(ConnectionProfile p) =>
        p.Mode == ConnectionMode.LichProxy
            ? new ConnectionConfig
              {
                  Mode          = ConnectionMode.LichProxy,
                  LichProxyHost = string.IsNullOrWhiteSpace(p.Host) ? "127.0.0.1" : p.Host,
                  LichProxyPort = p.Port > 0 ? p.Port : 8000,
                  CharacterName = p.CharacterName,
                  GameCode      = string.IsNullOrWhiteSpace(p.GameCode) ? "DR" : p.GameCode,
                  FrontEndId    = p.FrontEndId,
              }
            : new ConnectionConfig
              {
                  Mode            = ConnectionMode.DirectSGE,
                  SgeHost         = "eaccess.play.net",
                  SgePort         = 7900,
                  AccountName     = p.AccountName,
                  AccountPassword = Profiles.GetPassword(p),
                  CharacterName   = p.CharacterName,
                  GameCode        = string.IsNullOrWhiteSpace(p.GameCode) ? "DR" : p.GameCode,
                  FrontEndId      = p.FrontEndId,
              };

    private async Task DisconnectAsync()
    {
        // Stop the raw-XML recorder when the user disconnects — letting it run
        // after the session ends just produces a file that trails off mid-tag.
        // The user can re-toggle Record Session on the next connect.
        Recorder.Stop();
        if (_core is not null)
            await _core.DisconnectAsync();
    }

    /// <summary>
    /// Route the typed command through the full Genie pipeline:
    /// alias expansion → separator split → #cmd dispatch → game send.
    /// Local echo of the typed text happens inside <see cref="GenieCore"/>.
    /// </summary>
    // ── Layout save / load helpers ─────────────────────────────────────

    /// <summary>
    /// Capture the current layout-affecting state into a fresh
    /// <see cref="Settings.SavedLayout"/> ready to persist. Reads
    /// from the live VM + DisplaySettings + DockFactory.
    /// </summary>
    private Settings.SavedLayout CaptureCurrentLayout()
    {
        var layout = new Settings.SavedLayout
        {
            HandsStripVisible     = Display.ShowHandsBar,
            HandsStripAtBottom    = Display.HandsAtBottom,
            ShowStatusBar         = Display.ShowStatusBar,
            RoundTimeOnHandsStrip = Display.RoundTimeOnHandsStrip,
            ShowGameText          = Display.ShowGameText,
            ShowEchoText          = Display.ShowEchoText,
            ShowScriptText        = Display.ShowScriptText,
            MapBackgroundHex      = Display.MapBackgroundHex,
            WindowedMode          = Display.WindowedMode,
        };

        // Main-window geometry — captured from the View (if it has wired the
        // bridge) so window size/position/maximized ride on the layout profile.
        if (CaptureWindowGeometry is { } capture)
        {
            var g = capture();
            layout.WindowWidth     = g.Width;
            layout.WindowHeight    = g.Height;
            layout.WindowX         = g.X;
            layout.WindowY         = g.Y;
            layout.WindowMaximized = g.Maximized;
            layout.HasWindowGeometry = true;
        }

        // Visible-tool list — walk the dock factory's known tools and
        // record which ones are currently in the dock tree. The factory
        // knows the canonical set; checking IsToolVisible per id gives
        // us a stable, normalised list. Kept as a fallback for clients
        // that read VisibleTools; DockTree below is authoritative.
        if (DockFactory is Docking.GenieDockFactory factory)
        {
            foreach (var id in factory.ToolIds)
            {
                if (factory.IsToolVisible(id))
                    layout.VisibleTools.Add(id);
            }

            // Full-tree snapshot — captures arrangement (proportions,
            // alignments, active tabs, container structure), so loading the
            // layout restores the visual arrangement, not just visibility.
            layout.DockTree = factory.CaptureLayout();

            // In windowed mode the tree snapshot isn't the arrangement — the
            // per-window MDI geometry is. Capture it so the layout reopens with
            // each floating window where it was.
            if (Display.WindowedMode)
                layout.MdiBounds = factory.CaptureMdiBounds();
        }
        return layout;
    }

    /// <summary>
    /// Push a loaded <see cref="Settings.SavedLayout"/> back into the
    /// live VM + DisplaySettings + DockFactory. Display settings are
    /// applied first (cheap, instant); tool-visibility toggles run
    /// last because Dock.Avalonia mutations need a UI-thread tick.
    /// </summary>
    private void ApplyLayout(Settings.SavedLayout layout)
    {
        // Display flags — these have property-changed observers that
        // push through to the Avalonia resources so changes show
        // immediately.
        Display.ShowHandsBar           = layout.HandsStripVisible;
        Display.HandsAtBottom          = layout.HandsStripAtBottom;
        Display.ShowStatusBar          = layout.ShowStatusBar;
        Display.RoundTimeOnHandsStrip  = layout.RoundTimeOnHandsStrip;
        Display.ShowGameText           = layout.ShowGameText;
        Display.ShowEchoText           = layout.ShowEchoText;
        Display.ShowScriptText         = layout.ShowScriptText;
        if (!string.IsNullOrWhiteSpace(layout.MapBackgroundHex))
            Display.MapBackgroundHex   = layout.MapBackgroundHex;
        // Switch document mode to match the saved layout BEFORE rebuilding the
        // dock, so a layout saved in windowed mode reopens windowed (not tabbed).
        Display.WindowedMode           = layout.WindowedMode;
        Display.Save(_displayPath);

        // Restore the main-window geometry from the layout (only when the
        // layout actually captured it — older layouts leave the window as-is).
        if (layout.HasWindowGeometry)
            ApplyWindowGeometry?.Invoke(
                layout.WindowWidth, layout.WindowHeight,
                layout.WindowX, layout.WindowY, layout.WindowMaximized);

        if (DockFactory is Docking.GenieDockFactory factory)
        {
            if (layout.WindowedMode)
            {
                // Windowed (MDI): the dock-tree snapshot doesn't capture MDI
                // arrangement — the per-window geometry does. Prefer the
                // layout's own saved geometry, falling back to the in-session
                // cache (null is fine — BuildMdiLayout cascades from defaults).
                var bounds = layout.MdiBounds is { Count: > 0 }
                    ? layout.MdiBounds
                    : _mdiBoundsCache;
                DockLayout = factory.BuildMdiLayout(bounds);
            }
            else if (layout.DockTree is not null)
            {
                // Authoritative path: rebuild the whole tree from the snapshot
                // and swap it into the bound DockControl. Restores proportions,
                // alignments, active tabs — the full arrangement.
                DockLayout = factory.BuildLayout(layout.DockTree);
            }
            else
            {
                // Legacy fallback (layout saved before full-tree serialisation,
                // or Reset). Rebuild the default tree FIRST so every parent
                // ToolDock exists again — Dock auto-removes a ToolDock when its
                // last child is closed, so without the rebuild a hidden tool
                // (e.g. the mapper) could never be re-shown. Then apply the
                // saved visibility set on top of the fresh default.
                DockLayout = factory.BuildDefaultLayout();
                var wanted = new HashSet<string>(layout.VisibleTools, StringComparer.OrdinalIgnoreCase);
                foreach (var id in factory.ToolIds)
                    factory.SetToolVisibility(id, wanted.Contains(id));
            }
            RefreshVisibilityBools();
        }
    }

    /// <summary>
    /// On connect, apply the appropriate default layout: the profile's own
    /// <see cref="ConnectionProfile.DefaultLayoutName"/> from its store, else
    /// the global default (<see cref="DisplaySettings.GlobalDefaultLayout"/>),
    /// else leave the current/built-in layout untouched.
    /// </summary>
    private void ApplyDefaultLayoutForConnect(ConnectionProfile? profile)
    {
        var profileDefault = profile?.DefaultLayoutName;
        if (!string.IsNullOrWhiteSpace(profileDefault)
            && _profileLayouts?.Load(profileDefault) is { } pl)
        {
            ApplyLayout(pl);
            return;
        }

        var globalDefault = Display.GlobalDefaultLayout;
        if (!string.IsNullOrWhiteSpace(globalDefault)
            && _globalLayouts.Load(globalDefault) is { } gl)
        {
            ApplyLayout(gl);
        }
        // else: keep whatever is showing (built-in default from startup).
    }

    /// <summary>Re-read the disk and rebuild <see cref="SavedLayouts"/>
    /// so the Layout → Load ▶ submenu reflects current state.</summary>
    private void RefreshSavedLayoutList()
    {
        SavedLayouts.Clear();

        // Profile layouts first (the character's own), then global presets
        // suffixed "(Global)" so duplicate names across scopes stay distinct.
        if (_profileLayouts is not null)
            foreach (var name in _profileLayouts.List())
                SavedLayouts.Add(new LayoutMenuItem(name, name, LayoutScope.Profile, LoadLayoutCommand));

        foreach (var name in _globalLayouts.List())
            SavedLayouts.Add(new LayoutMenuItem($"{name} (Global)", name, LayoutScope.Global, LoadLayoutCommand));
    }

    // ── #config command bar handler ─────────────────────────────────────────
    //
    //   #config                         → open Configuration dialog
    //   #config save                    → flush DisplaySettings to display.json
    //   #config load                    → reload DisplaySettings from display.json
    //   #config edit                    → open display.json in user's editor
    //   #config <key>                   → echo current value (placeholder)
    //   #config <key> <value>           → set value (placeholder)
    //
    // Genie 4 parity for the four sub-forms (settings.cfg → display.json in
    // Genie 5). Key/value get-set is a placeholder for now — the Configuration
    // dialog covers the realistic editing path; a script-level setting API
    // can land later when needed.
    //
    // Aliases #set / #setting / #settings route here too via CommandEngine.
    private void HandleConfigCommand(string args)
    {
        var trimmed = (args ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            // Bare #config → open the Configuration dialog (the same one as
            // Edit → Configuration… in the menu). ReactiveCommand handles
            // the modal lifecycle.
            ConfigurationCommand.Execute().Subscribe(
                _   => { },
                ex  => Diagnostics.ErrorLog.Log("ConfigCommand.OpenDialog", ex));
            return;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub   = split[0].ToLowerInvariant();
        var rest  = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (sub)
        {
            case "save":
                // Most settings auto-save on change (each menu command calls
                // Display.Save). #config save is the explicit form for users
                // muscle-memoried from Genie 4. Safe to call repeatedly.
                Display.Save(_displayPath);
                GameText.AddSystemLine($"[config] Settings saved → {_displayPath}");
                break;

            case "load":
                // Replace in-place would require swapping the reactive
                // instance everywhere, which is invasive. Echo a clear next
                // step instead: the persisted file is what the next launch
                // reads, and the dialog is the right surface to live-edit.
                GameText.AddSystemLine(
                    "[config] Display settings live-reload not supported. Restart Genie to pick up external edits to display.json.");
                break;

            case "edit":
            {
                // Open display.json in a text editor. Fallback ladder, in order:
                //   1. Display.EditorPath if the user has explicitly set one
                //      (set via Configuration → Display Settings → Editor Path).
                //   2. Notepad on Windows — always present, opens .json as
                //      text. Skips the "Choose an app for .json" OS picker
                //      that UseShellExecute=true would otherwise trigger.
                //   3. macOS / Linux — try `open` / `xdg-open` with the file;
                //      these usually route to TextEdit / the user's default
                //      text app, not a generic file-association picker.
                //   4. Last-resort UseShellExecute=true — only if the OS-
                //      specific helpers above aren't available.
                try
                {
                    var path = _displayPath;
                    if (!System.IO.File.Exists(path))
                        Display.Save(path);  // first-write — make sure there's a file to open

                    var editor = Display.EditorPath;
                    if (!string.IsNullOrWhiteSpace(editor) && System.IO.File.Exists(editor))
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(editor, "\"" + path + "\"")
                            { UseShellExecute = false });
                    }
                    else if (OperatingSystem.IsWindows())
                    {
                        // Notepad is always available on Windows and opens
                        // .json files as text without the file-association
                        // chooser dialog.
                        System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        System.Diagnostics.Process.Start("open", "-t \"" + path + "\"");
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        System.Diagnostics.Process.Start("xdg-open", "\"" + path + "\"");
                    }
                    else
                    {
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                    }
                    GameText.AddSystemLine($"[config] Opened {_displayPath}");
                    GameText.AddSystemLine(
                        "[config] To use a different editor, set Edit → Configuration → Display Settings → Editor Path.");
                }
                catch (Exception ex)
                {
                    GameText.AddSystemLine($"[config] Could not open editor: {ex.Message}");
                    Diagnostics.ErrorLog.Log("ConfigCommand.Edit", ex);
                }
                break;
            }

            default:
                // Genie 4's #config <key> [value] uses the settings.cfg
                // dictionary schema; ours lives on the strongly-typed
                // DisplaySettings record. Until/unless there's demand for
                // a script-level reflection-based getter/setter, point the
                // user at the dialog.
                GameText.AddSystemLine(
                    $"[config] '#config {sub}' — script-level get/set not wired yet. Use #config (no args) to open the Configuration dialog.");
                break;
        }
    }

    // ── #layout command bar handler ─────────────────────────────────────────
    //
    //   #layout                        list all (★ = default)
    //   #layout list
    //   #layout load <name>            load (profile first, then global)
    //   #layout save <name>            save to current scope (profile if connected)
    //   #layout save global  <name>    save to global
    //   #layout save profile <name>    save to this profile
    //   #layout default <name>         set <name> as default in its scope
    //   #layout delete <name>          delete <name> from its scope
    //   #layout reset                  built-in default layout
    private void HandleLayoutCommand(string args)
    {
        var trimmed = (args ?? string.Empty).Trim();
        var split   = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub     = split.Length > 0 ? split[0].ToLowerInvariant() : "list";
        var rest    = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (sub)
        {
            case "":
            case "list":    LayoutCmdList();              break;
            case "load":
            case "apply":   LayoutCmdLoad(rest);          break;
            case "save":    LayoutCmdSave(rest);          break;
            case "default": LayoutCmdSetDefault(rest);    break;
            case "delete":
            case "remove":  LayoutCmdDelete(rest);        break;
            case "reset":   ResetLayoutCommand.Execute().Subscribe(); break;
            default:
                GameText.AddSystemLine(
                    "[layout] usage: #layout [list | load <name> | save [global|profile] <name> | default <name> | delete <name> | reset]");
                break;
        }
    }

    private void LayoutCmdList()
    {
        var any = false;
        GameText.AddSystemLine("[layout] saved layouts:");
        if (_profileLayouts is not null)
            foreach (var n in _profileLayouts.List())
            {
                var def = string.Equals(n, ConnectedProfile?.DefaultLayoutName, StringComparison.OrdinalIgnoreCase) ? "  ★ default" : "";
                GameText.AddSystemLine($"  {n}  (profile){def}");
                any = true;
            }
        foreach (var n in _globalLayouts.List())
        {
            var def = string.Equals(n, Display.GlobalDefaultLayout, StringComparison.OrdinalIgnoreCase) ? "  ★ default" : "";
            GameText.AddSystemLine($"  {n}  (global){def}");
            any = true;
        }
        if (!any) GameText.AddSystemLine("  (none saved)");
    }

    /// <summary>Resolve a layout name to its store + scope — profile first
    /// (when connected), then global. Null if not found in either.</summary>
    private (Settings.LayoutStore Store, LayoutScope Scope)? ResolveLayout(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_profileLayouts is not null && _profileLayouts.Exists(name))
            return (_profileLayouts, LayoutScope.Profile);
        if (_globalLayouts.Exists(name))
            return (_globalLayouts, LayoutScope.Global);
        return null;
    }

    private void LayoutCmdLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { GameText.AddSystemLine("[layout] usage: #layout load <name>"); return; }
        if (ResolveLayout(name) is not { } hit) { GameText.AddSystemLine($"[layout] not found: '{name}'"); return; }
        var loaded = hit.Store.Load(name);
        if (loaded is null) { GameText.AddSystemLine($"[layout] could not read '{name}'"); return; }
        ApplyLayout(loaded);
        GameText.AddSystemLine($"[layout] loaded '{name}'{(hit.Scope == LayoutScope.Global ? " (global)" : "")}");
    }

    private void LayoutCmdSave(string rest)
    {
        var scope = LayoutScope.Profile;
        var name  = rest;

        // Optional leading scope word, but only when a name follows it — so a
        // layout literally named "global"/"profile" can still be saved.
        var sp = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (sp.Length == 2)
        {
            var first = sp[0].ToLowerInvariant();
            if (first is "global" or "profile")
            {
                scope = first == "global" ? LayoutScope.Global : LayoutScope.Profile;
                name  = sp[1].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(name)) { GameText.AddSystemLine("[layout] usage: #layout save [global|profile] <name>"); return; }

        Settings.LayoutStore store;
        if (scope == LayoutScope.Profile && _profileLayouts is not null)
            store = _profileLayouts;
        else
        {
            if (scope == LayoutScope.Profile)
                GameText.AddSystemLine("[layout] no profile connected — saving to global.");
            store = _globalLayouts;
        }

        var layout  = CaptureCurrentLayout();
        layout.Name = name.Trim();
        store.Save(layout);
        RefreshSavedLayoutList();
        GameText.AddSystemLine($"[layout] saved '{layout.Name}' ({(ReferenceEquals(store, _profileLayouts) ? "profile" : "global")})");
    }

    private void LayoutCmdSetDefault(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { GameText.AddSystemLine("[layout] usage: #layout default <name>"); return; }
        if (ResolveLayout(name) is not { } hit) { GameText.AddSystemLine($"[layout] not found: '{name}'"); return; }

        if (hit.Scope == LayoutScope.Profile)
        {
            if (ConnectedProfile is null) { GameText.AddSystemLine("[layout] no connected profile to set a default on."); return; }
            ConnectedProfile.DefaultLayoutName = name;
            SaveProfiles();
            GameText.AddSystemLine($"[layout] '{name}' is now this profile's default.");
        }
        else
        {
            Display.GlobalDefaultLayout = name;
            Display.Save(_displayPath);
            GameText.AddSystemLine($"[layout] '{name}' is now the global default.");
        }
    }

    private void LayoutCmdDelete(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { GameText.AddSystemLine("[layout] usage: #layout delete <name>"); return; }
        if (ResolveLayout(name) is not { } hit) { GameText.AddSystemLine($"[layout] not found: '{name}'"); return; }

        // Clear a default pointing at the deleted layout.
        if (hit.Scope == LayoutScope.Profile && ConnectedProfile is not null
            && string.Equals(ConnectedProfile.DefaultLayoutName, name, StringComparison.OrdinalIgnoreCase))
        {
            ConnectedProfile.DefaultLayoutName = "";
            SaveProfiles();
        }
        else if (hit.Scope == LayoutScope.Global
            && string.Equals(Display.GlobalDefaultLayout, name, StringComparison.OrdinalIgnoreCase))
        {
            Display.GlobalDefaultLayout = "";
            Display.Save(_displayPath);
        }

        hit.Store.Delete(name);
        RefreshSavedLayoutList();
        GameText.AddSystemLine($"[layout] deleted '{name}'");
    }

    private Task SendCommand(string cmd)
    {
        if (_core is not null && !string.IsNullOrWhiteSpace(cmd))
        {
            // Typed user input cancels any in-flight auto-walk — per the
            // compliance review, "any non-map-click input cancels." The
            // user has taken manual control of the session; respecting
            // that immediately keeps the walker on the responsive side
            // of DR policy. Cancel BEFORE dispatching the typed input
            // so the cancelled walk doesn't try to send a stale step
            // when the next room-change fires.
            if (Mapper.AutoWalk?.Current is not null)
                Mapper.AutoWalk.Cancel("user took manual command");

            _core.ProcessInput(cmd);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the named script file in the user-configured external editor
    /// (<see cref="DisplaySettings.EditorPath"/>) or, when none is set,
    /// in the OS default handler for <c>.cmd</c> files.
    /// <para>
    /// Looks up <c>{ScriptsDir}/{name}.cmd</c> first, then <c>.inc</c>.
    /// If neither exists, surfaces a system line in the Game window so
    /// the user knows nothing happened (rather than a silent failure).
    /// </para>
    /// </summary>
    public void OpenScriptInEditor(string name)
    {
        if (_core is null || string.IsNullOrWhiteSpace(name)) return;

        var dir = _core.Scripts.ScriptsDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            GameText.AddSystemLine($"[editor] scripts directory not found: '{dir}'");
            return;
        }

        // Try .cmd first, fall back to .inc. Scripts are typically .cmd
        // but #include-style helpers use .inc.
        var candidate = Path.Combine(dir, name + ".cmd");
        if (!File.Exists(candidate))
            candidate = Path.Combine(dir, name + ".inc");
        if (!File.Exists(candidate))
        {
            GameText.AddSystemLine(
                $"[editor] script not found: '{name}' (looked for {name}.cmd, {name}.inc in {dir})");
            return;
        }

        try
        {
            var editorPath = Display.EditorPath;
            if (!string.IsNullOrWhiteSpace(editorPath) && File.Exists(editorPath))
            {
                // Configured editor: invoke it with the file path as a
                // single argument. Works for Notepad++, VS Code, Sublime,
                // any GUI editor that takes a file path.
                System.Diagnostics.Process.Start(editorPath, $"\"{candidate}\"");
            }
            else
            {
                // No configured editor — let the OS open with whatever's
                // associated with `.cmd`. UseShellExecute=true is required
                // for shell-association launch.
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(candidate) { UseShellExecute = true });
            }
            GameText.AddSystemLine($"[editor] opened '{Path.GetFileName(candidate)}'");
        }
        catch (Exception ex)
        {
            ErrorLog.Log("OpenScriptInEditor", ex);
            GameText.AddSystemLine($"[editor] failed to open '{name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns script-name completions for the given prefix, sorted
    /// alphabetically (case-insensitive). Used by the command bar's Tab-
    /// completion handler: typing <c>.MC</c> + Tab cycles through
    /// <c>MC_Setup</c>, <c>MC_Hunt</c>, etc.
    /// <para>
    /// Scans the current <c>ScriptsDir</c> for <c>*.cmd</c> and <c>*.inc</c>
    /// files (the two extensions our engine accepts) and returns the
    /// basename (without extension). An empty prefix returns ALL scripts
    /// so the user can cycle through the whole library by typing
    /// <c>.</c> + Tab.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> GetScriptCompletions(string prefix)
    {
        if (_core is null) return Array.Empty<string>();
        var dir = _core.Scripts.ScriptsDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(dir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".inc", StringComparison.OrdinalIgnoreCase);
                })
                .Select(f => Path.GetFileNameWithoutExtension(f) ?? "")
                .Where(n => !string.IsNullOrEmpty(n) &&
                            n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorLog.Log("GetScriptCompletions", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Build the friendly echo string for a clicked <c>&lt;d cmd&gt;</c> link.
    /// DR's links carry server-bound item-exist-IDs like
    /// <c>get #49489411 in #49489410</c>, but the visible link text is the
    /// human-readable noun (<c>a tapered cutlass</c>). When the user clicks
    /// the link, the Game-window echo should show the noun-form rather than
    /// the IDs.
    /// <para>
    /// Approach: substitute the FIRST <c>#NNNN</c> occurrence with the link's
    /// visible text (the item being acted on), then substitute any further
    /// <c>#NNNN</c>s against <see cref="_containerNouns"/> — the per-session
    /// map populated from <c>&lt;container&gt;</c> events. The typical
    /// two-ID command <c>get #49489411 in #37666728</c> renders as
    /// <c>get a tapered cutlass in My Backpack</c>. IDs not in the dict
    /// (rare — a container we somehow missed at session start) fall back
    /// to the raw <c>#NNNN</c> form rather than guessing.
    /// </para>
    /// <para>
    /// If <paramref name="cmd"/> has no IDs at all (e.g.
    /// <c>&lt;d&gt;look around&lt;/d&gt;</c> bare-text links), the cmd IS
    /// the readable form and is returned unchanged.
    /// </para>
    /// </summary>
    private string BuildLinkEcho(string cmd, string displayText)
    {
        if (string.IsNullOrWhiteSpace(cmd))          return cmd;
        if (string.IsNullOrWhiteSpace(displayText))  return cmd;
        if (cmd.IndexOf('#') < 0)                    return cmd;

        var index = 0;
        return System.Text.RegularExpressions.Regex.Replace(
            cmd, @"#\d+", m =>
            {
                if (index++ == 0)
                    return displayText;
                return _containerNouns.TryGetValue(m.Value, out var title)
                    ? title
                    : m.Value;
            });
    }
}
