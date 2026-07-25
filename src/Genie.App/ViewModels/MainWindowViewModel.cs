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
using Genie.Core.Capture;
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
    public ActiveSpellsViewModel ActiveSpells { get; } = new();
    public TimeTrackerViewModel TimeTracker { get; } = new();
    public InventoryViewViewModel InventoryView { get; } = new();
    public ScriptBarViewModel  ScriptBar  { get; } = new();

    /// <summary>Backs the dockable Scripts panel (running list with per-script
    /// Stop, a Start… picker, and the script output log). Distinct from
    /// <see cref="ScriptBar"/>, which is the always-on bottom strip.</summary>
    public ScriptsViewModel    Scripts    { get; } = new();

    /// <summary>Backs the dockable Scene panel — DR room/scene artwork
    /// (<c>&lt;resource picture&gt;</c>), gated by <c>showimages</c>.</summary>
    public SceneViewModel      Scene      { get; } = new();

    /// <summary>Backs the dockable Mobs panel — creatures in the room (#86).</summary>
    public MobsViewModel       Mobs       { get; } = new();

    /// <summary>Backs the dockable Players panel — other players in the room (#86).</summary>
    public PlayersViewModel    Players    { get; } = new();

    /// <summary>Backs the dockable Raw XML panel — verbatim live server stream
    /// for parser dev / protocol debugging (#14). Hidden by default.</summary>
    public RawXmlViewModel     RawXml     { get; } = new();

    /// <summary>Backs the dockable Injuries panel — body-silhouette wound/scar
    /// display from the server's injuries dialog (#18). Hidden by default.</summary>
    public InjuriesViewModel   Injuries   { get; } = new();

    /// <summary>Backs the dockable Analytics panel — skill-history charts
    /// (XP/hour, gain curves, session comparison) over the local recorder's
    /// data. Hidden by default.</summary>
    public AnalyticsViewModel  Analytics  { get; } = new();

    /// <summary>
    /// Global layout presets, shared across every character —
    /// <c>{AppData}/Genie5/Layouts/</c>. Always present.
    /// </summary>
    private Settings.LayoutStore _globalLayouts = null!;

    /// <summary>
    /// Per-profile layout presets for the connected saved profile —
    /// <c>Profiles/{Char}-{Acct}/Layouts/</c>. Null when not connected, or
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

    /// <summary>Padlock glyph for the status bar showing the login transport:
    /// 🔒 = TLS (encrypted), 🔓 = plaintext (TLS unavailable), "" = N/A /
    /// disconnected. <see cref="ConnectionSecurityTip"/> is the hover text.</summary>
    [Reactive] public string             ConnectionSecurity    { get; private set; } = "";
    [Reactive] public string             ConnectionSecurityTip { get; private set; } = "";

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

    // ── Edit → Theme (#20) ──────────────────────────────────────────────────
    /// <summary>Theme presets + custom themes; rebuilt on menu open so radio
    /// checks and newly-dropped Config/Themes files stay fresh.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ThemeMenuItem> ThemeMenuItems { get; }
        = new();
    public ReactiveCommand<ThemeMenuItem, Unit>           ApplyThemeCommand        { get; }
    public ReactiveCommand<Unit, Unit>                    SaveThemeAsCommand       { get; }
    public ReactiveCommand<Unit, Unit>                    EditThemeCommand         { get; }
    public ReactiveCommand<Unit, Unit>                    ResetThemeCommand        { get; }
    public ReactiveCommand<Unit, Unit>                    RefreshThemeListCommand  { get; }
    /// <summary>Input = suggested name; output = chosen name or null (cancel).</summary>
    public Interaction<string, string?>                   ShowThemeNamePrompt      { get; } = new();
    /// <summary>Theme editor dialog; output = true when the user saved.</summary>
    public Interaction<ThemeEditorViewModel, bool>        ShowThemeEditorDialog    { get; } = new();
    /// <summary>Theme registry + apply pipeline. Created right after
    /// <see cref="Display"/> loads; startup-applies the persisted theme.</summary>
    public Theming.ThemeService Themes { get; }

    // ── Help menu (Updates) ─────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit>                    ShowUpdatesCommand       { get; }
    public Interaction<UpdatesDialogViewModel, Unit>      ShowUpdatesDialog        { get; } = new();

    // ── Help menu (About) ───────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit>                    ShowAboutCommand         { get; }
    public Interaction<Unit, Unit>                        ShowAboutDialog          { get; } = new();
    public ReactiveCommand<Unit, Unit>                    ShowChangelogCommand     { get; }
    public Interaction<Unit, Unit>                        ShowChangelogDialog      { get; } = new();
    public ReactiveCommand<Unit, Unit>                    ShowMapperSettingsCommand{ get; }
    public Interaction<Unit, Unit>                        ShowMapperSettingsDialog { get; } = new();

    /// <summary>Fired on a session-ending disconnect (one that is NOT
    /// auto-reconnecting) when <see cref="DisplaySettings.ShowDisconnectPopup"/>
    /// is on — surfaces a modal "Disconnected" notice for unmissable leave-game
    /// feedback (#114). The string payload is the notice body.</summary>
    public Interaction<string, Unit>                      ShowDisconnectNotice     { get; } = new();

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

    // ── Help ▸ Update Settings (per-kind startup-check + auto-apply) ──────────
    // Eight checkboxes persisted in update-feeds.json (FeedConfig.*Policy).
    // Setters write through immediately; _updateTogglesLoading suppresses the
    // write-back while the constructor seeds the initial values from disk.

    private bool _updateTogglesLoading;
    private bool _checkCoreOnStartup    = true;
    private bool _checkMapsOnStartup    = true;
    private bool _checkPluginsOnStartup = true;
    private bool _checkScriptsOnStartup = true;
    private bool _autoUpdateCore;
    private bool _autoUpdateMaps;
    private bool _autoUpdatePlugins;
    private bool _autoUpdateScripts;

    public bool CheckCoreOnStartup
    {
        get => _checkCoreOnStartup;
        set { this.RaiseAndSetIfChanged(ref _checkCoreOnStartup, value); SaveUpdatePolicy(c => c.CorePolicy.CheckOnStartup = value); }
    }
    public bool CheckMapsOnStartup
    {
        get => _checkMapsOnStartup;
        set { this.RaiseAndSetIfChanged(ref _checkMapsOnStartup, value); SaveUpdatePolicy(c => c.MapsPolicy.CheckOnStartup = value); }
    }
    public bool CheckPluginsOnStartup
    {
        get => _checkPluginsOnStartup;
        set { this.RaiseAndSetIfChanged(ref _checkPluginsOnStartup, value); SaveUpdatePolicy(c => c.PluginsPolicy.CheckOnStartup = value); }
    }
    public bool CheckScriptsOnStartup
    {
        get => _checkScriptsOnStartup;
        set { this.RaiseAndSetIfChanged(ref _checkScriptsOnStartup, value); SaveUpdatePolicy(c => c.ScriptsPolicy.CheckOnStartup = value); }
    }
    /// <summary>Auto-apply for Core downloads + stages via Velopack, installing
    /// when the app EXITS — never a surprise restart mid-session.</summary>
    public bool AutoUpdateCore
    {
        get => _autoUpdateCore;
        set { this.RaiseAndSetIfChanged(ref _autoUpdateCore, value); SaveUpdatePolicy(c => c.CorePolicy.AutoApply = value); }
    }
    public bool AutoUpdateMaps
    {
        get => _autoUpdateMaps;
        set { this.RaiseAndSetIfChanged(ref _autoUpdateMaps, value); SaveUpdatePolicy(c => c.MapsPolicy.AutoApply = value); }
    }
    public bool AutoUpdatePlugins
    {
        get => _autoUpdatePlugins;
        set { this.RaiseAndSetIfChanged(ref _autoUpdatePlugins, value); SaveUpdatePolicy(c => c.PluginsPolicy.AutoApply = value); }
    }
    public bool AutoUpdateScripts
    {
        get => _autoUpdateScripts;
        set { this.RaiseAndSetIfChanged(ref _autoUpdateScripts, value); SaveUpdatePolicy(c => c.ScriptsPolicy.AutoApply = value); }
    }

    /// <summary>Load-modify-save one policy field in update-feeds.json. Skipped
    /// while the constructor is seeding the checkboxes from that same file.</summary>
    private void SaveUpdatePolicy(Action<Genie.Core.Update.FeedConfig> mutate)
    {
        if (_updateTogglesLoading) return;
        try
        {
            var store = new Genie.Core.Update.FeedConfigStore(_configDir);
            var cfg   = store.Load();
            mutate(cfg);
            store.Save(cfg);
        }
        catch { /* a failed save costs one forgotten checkbox, not a crash */ }
    }

    /// <summary>Seed the eight Update Settings checkboxes from update-feeds.json.</summary>
    private void LoadUpdatePolicies()
    {
        _updateTogglesLoading = true;
        try
        {
            var cfg = new Genie.Core.Update.FeedConfigStore(_configDir).Load();
            CheckCoreOnStartup    = cfg.CorePolicy.CheckOnStartup;
            CheckMapsOnStartup    = cfg.MapsPolicy.CheckOnStartup;
            CheckPluginsOnStartup = cfg.PluginsPolicy.CheckOnStartup;
            CheckScriptsOnStartup = cfg.ScriptsPolicy.CheckOnStartup;
            AutoUpdateCore        = cfg.CorePolicy.AutoApply;
            AutoUpdateMaps        = cfg.MapsPolicy.AutoApply;
            AutoUpdatePlugins     = cfg.PluginsPolicy.AutoApply;
            AutoUpdateScripts     = cfg.ScriptsPolicy.AutoApply;
        }
        catch { /* defaults stand */ }
        finally { _updateTogglesLoading = false; }
    }

    // ── Status-bar update notice (non-intrusive strip above the vitals bar) ──

    /// <summary>One-line summary of what the startup check found / auto-applied
    /// (e.g. "Updates available: Core, Scripts" or "Auto-updated Maps (3 files)").
    /// Empty = the notice strip is collapsed. Click opens the Updates dialog;
    /// the × dismisses until the next check finds something new.</summary>
    [Reactive] public string UpdateNotice { get; private set; } = "";

    public ReactiveCommand<Unit, Unit> DismissUpdateNoticeCommand { get; private set; } = null!;

    // ── Report-XML-gap notice (crowdsourced parser-gap reporting) ────────────

    /// <summary>One-line prompt shown when the parser meets an XML element it
    /// doesn't consume (<see cref="UnknownTagEvent"/>). Empty = the strip is
    /// collapsed. Click drafts a REDACTED GitHub issue and opens the prefill URL
    /// in the browser (nothing is posted automatically); the × dismisses it.
    /// One prompt per tag type per session — the parser rarely meets an unknown
    /// element, so this does not nag.</summary>
    [Reactive] public string XmlGapNotice { get; private set; } = "";

    /// <summary>Prefill new-issue URL for the pending XML-gap draft, opened by
    /// <see cref="ReportXmlGapCommand"/>. Empty when no gap is pending.</summary>
    private string _pendingXmlGapUrl = "";

    /// <summary>Tag types already surfaced this session — dedup so the same
    /// unknown element never re-prompts.</summary>
    private readonly System.Collections.Generic.HashSet<string> _reportedGapTags =
        new(System.StringComparer.OrdinalIgnoreCase);

    public ReactiveCommand<Unit, Unit> ReportXmlGapCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DismissXmlGapNoticeCommand { get; private set; } = null!;

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
    public ReactiveCommand<Unit, Unit>                    ToggleIconBarCommand     { get; }

    /// <summary>Backs the Icon Bar status-chip strip (Genie 4 parity, #26).</summary>
    public IconBarViewModel IconBar { get; } = new();

    /// <summary>Backs the ten positional <c>#statusbar</c> slots rendered under
    /// the vitals Status Bar (Genie 4 StatusStrip parity, #111).</summary>
    public StatusSlotsViewModel StatusSlots { get; } = new();
    public ReactiveCommand<Unit, Unit>                    ToggleMagicPanelsCommand { get; }
    public ReactiveCommand<Unit, Unit>                    ToggleZoneRoomIdCommand  { get; }
    public ReactiveCommand<Unit, Unit>                    ToggleZoneRoomNumberCommand { get; }
    public ReactiveCommand<Unit, Unit>                    ToggleWindowedModeCommand{ get; }
    public ReactiveCommand<Unit, Unit>                    ToggleGuildInTitleCommand{ get; }
    /// <summary>Window → Disconnect Popup. Toggles the modal "Disconnected" notice (#114).</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleDisconnectPopupCommand { get; }
    /// <summary>Layout → Always on Top. Keeps the main window above other applications.</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleAlwaysOnTopCommand { get; }
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

    /// <summary>File → Mute Sounds — flip the <c>muted</c> config (the
    /// <c>PlaySounds</c> gate in GenieCore.PlaySound) and persist settings.cfg.</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleMuteCommand        { get; }

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

    /// <summary>True while SFX are muted (<c>Config.PlaySounds</c> false / the
    /// <c>muted</c> settings.cfg key). Drives the File-menu checkbox; kept in
    /// sync with <c>#config muted</c> and profile loads via ConfigChanged.</summary>
    [Reactive] public bool IsMuted            { get; private set; }

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

    /// <summary>Automatic rendered-text session log (Genie 4 AutoLog). Started
    /// on connect when <c>Config.AutoLog</c> is set; writes the game window to
    /// <c>{AppData}/Genie5/Logs/{Char}{Game}_{date}.log</c>.</summary>
    public SessionTextLogger AutoLogger { get; }
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

    // File ▸ Open Directory submenu (issue #26 — Genie 4 had a single "Open
    // Directory" that opened the data root; the submenu adds direct jumps).
    // Maps / Scripts / Plugins reuse their existing commands.

    /// <summary>Opens the user-data root ({AppData}/Genie5/ or the portable
    /// dir) — Genie 4's File ▸ Open Directory.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenDataFolderCommand { get; }

    /// <summary>Opens the active config directory — the per-character profile
    /// dir when connected (Profiles/{Char}-{Acct}/), else the shared Config/.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenConfigFolderCommand { get; }

    // ── Analyst Capture (redacted, analyst-readable session captures) ──────────

    /// <summary>Master gate for the Analyst Capture feature. OFF by default each
    /// session (it's a policy-sensitive capability); enabling it shows a one-time
    /// explainer. Drives the Analyst-menu checkbox and enables the run items.</summary>
    [Reactive] public bool AnalystCaptureEnabled { get; private set; }

    /// <summary>True while an analyst capture is actively writing. Drives the
    /// title-bar "🔴 CAP" suffix and the Stop Capture menu item.</summary>
    [Reactive] public bool IsCapturing { get; private set; }

    /// <summary>Recipes for the <b>Analyst ▸ Run Capture Recipe</b> submenu —
    /// built-ins (shipped beside the exe) plus any in the user recipe dir.</summary>
    public System.Collections.ObjectModel.ObservableCollection<RecipeMenuItem> CaptureRecipes { get; } = new();

    /// <summary>Analyst → Enable Analyst Capture — toggles the feature (one-time
    /// explainer on first enable). Async because the explainer is a dialog.</summary>
    public ReactiveCommand<Unit, Unit>                    ToggleAnalystCaptureCommand { get; }

    /// <summary>Runs a capture recipe: confirm dialog → start capture → run the
    /// recipe's `.cmd` via the script engine. Bound per-row in the recipe submenu.</summary>
    public ReactiveCommand<RecipeMenuItem, Unit>          RunRecipeCommand            { get; }

    /// <summary>Analyst → Start Manual Capture — begin a capture with no recipe
    /// (the user drives the game; Stop Capture ends it).</summary>
    public ReactiveCommand<Unit, Unit>                    StartManualCaptureCommand   { get; }

    /// <summary>Analyst → Stop Capture — end the active capture and write meta.</summary>
    public ReactiveCommand<Unit, Unit>                    StopCaptureCommand          { get; }

    /// <summary>Analyst → Open Capture Folder — reveal the capture output dir.</summary>
    public ReactiveCommand<Unit, Unit>                    OpenCaptureFolderCommand    { get; }

    /// <summary>Analyst → Set Capture Folder — pick where captures are written
    /// (the readable dir the analyst reads from). Persisted per machine.</summary>
    public ReactiveCommand<Unit, Unit>                    SetCaptureFolderCommand     { get; }

    /// <summary>Analyst → Set Recipe Folder — pick an extra directory of user
    /// recipes loaded alongside the built-ins.</summary>
    public ReactiveCommand<Unit, Unit>                    SetRecipeFolderCommand      { get; }

    // ── Tool visibility (display-only; private setters guarantee these only
    // change via the Toggle commands, never via stray binding pushes) ────────
    [Reactive] public bool GameVisible     { get; private set; } = true;
    [Reactive] public bool VitalsVisible   { get; private set; } = true;
    [Reactive] public bool RoomVisible     { get; private set; } = true;
    [Reactive] public bool BackpackVisible { get; private set; } = true;
    [Reactive] public bool MapperVisible   { get; private set; } = true;
    [Reactive] public bool ExperienceVisible { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool AnalyticsVisible  { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool ActiveSpellsVisible { get; private set; } // hidden by default (opt-in)
    [Reactive] public bool TimeTrackerVisible { get; private set; }  // hidden by default (opt-in)
    [Reactive] public bool InventoryViewVisible { get; private set; } // hidden by default (opt-in)
    [Reactive] public bool LogonsVisible   { get; private set; } = true;
    [Reactive] public bool TalkVisible     { get; private set; } = true;
    [Reactive] public bool WhispersVisible { get; private set; } = true;
    [Reactive] public bool ThoughtsVisible { get; private set; } = true;
    [Reactive] public bool CombatVisible   { get; private set; } = true;
    [Reactive] public bool FamiliarVisible { get; private set; } = true;
    [Reactive] public bool DeathVisible    { get; private set; } = true;
    [Reactive] public bool AssessVisible   { get; private set; } = true;
    [Reactive] public bool AtmosphericsVisible { get; private set; }   // hidden by default (opt-in, #85)
    [Reactive] public bool LogVisible      { get; private set; } = true;
    [Reactive] public bool ItemLogVisible  { get; private set; } = true;
    [Reactive] public bool ScriptsVisible  { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool SceneVisible    { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool MobsVisible     { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool PlayersVisible  { get; private set; }   // hidden by default (opt-in)
    [Reactive] public bool RawXmlVisible   { get; private set; }   // hidden by default (opt-in, #14)
    [Reactive] public bool InjuriesVisible { get; private set; }   // hidden by default (opt-in, #18)

    // ── Toggle commands (one per dockable) ───────────────────────────────────
    public ReactiveCommand<Unit, Unit> ToggleGameCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleVitalsCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleRoomCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleBackpackCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMapperCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleExperienceCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleAnalyticsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleActiveSpellsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleTimeTrackerCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleInventoryViewCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLogonsCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleTalkCommand     { get; }
    public ReactiveCommand<Unit, Unit> ToggleWhispersCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThoughtsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleCombatCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleFamiliarCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleDeathCommand    { get; }
    public ReactiveCommand<Unit, Unit> ToggleAssessCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleAtmosphericsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLogCommand      { get; }
    public ReactiveCommand<Unit, Unit> ToggleItemLogCommand  { get; }
    public ReactiveCommand<Unit, Unit> ToggleScriptsCommand  { get; }
    public ReactiveCommand<Unit, Unit> ToggleSceneCommand    { get; }
    public ReactiveCommand<Unit, Unit> ToggleMobsCommand     { get; }
    public ReactiveCommand<Unit, Unit> TogglePlayersCommand  { get; }
    public ReactiveCommand<Unit, Unit> ToggleRawXmlCommand   { get; }
    public ReactiveCommand<Unit, Unit> ToggleInjuriesCommand { get; }

    // ── Core ──────────────────────────────────────────────────────────────────

    private GenieCore? _core;

    /// <summary>The data root fixed for this app session when the core was first
    /// built (empty = the default discovered AppData/portable location). The core
    /// is persistent, so this is NOT repointed per connect — a later connect to a
    /// profile with a different data directory warns instead. See WireCore.</summary>
    private string _sessionDataRoot = "";

    /// <summary>Identity ("char\0account") whose rule files are currently loaded into
    /// the persistent engines. Used to clear-then-load on a CHARACTER change while
    /// leaving a same-character reconnect's runtime rules + running scripts intact
    /// (issue #88 / #46 Phase 3). Empty until the first connect loads rules.</summary>
    private string _loadedRuleKey = "";

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

    // ── Performance overlay + per-component regex safety ──────────────────────

    /// <summary>Live per-stage timing overlay (Performance menu → Show Overlay).</summary>
    public PerfOverlayViewModel Perf { get; } = new();

    /// <summary>Performance → Show Performance Overlay. Toggles the overlay and,
    /// with it, metrics collection (off = zero instrumentation overhead).</summary>
    public ReactiveCommand<Unit, Unit> TogglePerfOverlayCommand { get; }

    private bool _triggersSafety    = true;
    private bool _highlightsSafety  = true;
    private bool _substitutesSafety = true;
    private bool _gagsSafety        = true;

    /// <summary>Regex match-timeout + literal pre-filter for user triggers. ON by
    /// default; toggling applies live to the connected engine and is remembered
    /// for the next connect.</summary>
    public bool TriggersSafety
    {
        get => _triggersSafety;
        set { this.RaiseAndSetIfChanged(ref _triggersSafety, value); if (_core is not null) _core.Triggers.SafetyEnabled = value; }
    }
    /// <summary>Regex safety for user highlight rules (Regex match type).</summary>
    public bool HighlightsSafety
    {
        get => _highlightsSafety;
        set { this.RaiseAndSetIfChanged(ref _highlightsSafety, value); if (_core is not null) _core.Highlights.SafetyEnabled = value; }
    }
    /// <summary>Regex safety for substitute rules.</summary>
    public bool SubstitutesSafety
    {
        get => _substitutesSafety;
        set { this.RaiseAndSetIfChanged(ref _substitutesSafety, value); if (_core is not null) _core.Substitutes.SafetyEnabled = value; }
    }
    /// <summary>Regex safety for gag rules.</summary>
    public bool GagsSafety
    {
        get => _gagsSafety;
        set { this.RaiseAndSetIfChanged(ref _gagsSafety, value); if (_core is not null) _core.Gags.SafetyEnabled = value; }
    }

    // ── Master toggles (File ▸ Master Toggles) ─────────────────────────────
    // Config-backed (settings.cfg keys highlights / triggers / substitutes /
    // gags / aliases / showimages) so they survive restart and stay in sync
    // with a typed `#config triggers off` via ConfigChanged(MasterToggles) —
    // showimages fires ConfigChanged(ImagesEnabled) instead. Setters write
    // through to config and persist; the engines follow via
    // GenieCore.SyncMasterToggles (images via SceneViewModel). Rules stay
    // loaded while off.

    private bool _highlightsEnabled  = true;
    private bool _triggersEnabled    = true;
    private bool _substitutesEnabled = true;
    private bool _gagsEnabled        = true;
    private bool _aliasesEnabled     = true;
    private bool _imagesEnabled      = true;
    private bool _syncingMasterToggles;

    /// <summary>Master enable for user highlight rules (colours + sounds).</summary>
    public bool HighlightsEnabled
    {
        get => _highlightsEnabled;
        set { this.RaiseAndSetIfChanged(ref _highlightsEnabled, value); WriteMasterToggle("highlights", value); }
    }
    /// <summary>Master enable for user triggers.</summary>
    public bool TriggersEnabled
    {
        get => _triggersEnabled;
        set { this.RaiseAndSetIfChanged(ref _triggersEnabled, value); WriteMasterToggle("triggers", value); }
    }
    /// <summary>Master enable for substitute rules.</summary>
    public bool SubstitutesEnabled
    {
        get => _substitutesEnabled;
        set { this.RaiseAndSetIfChanged(ref _substitutesEnabled, value); WriteMasterToggle("substitutes", value); }
    }
    /// <summary>Master enable for gag rules.</summary>
    public bool GagsEnabled
    {
        get => _gagsEnabled;
        set { this.RaiseAndSetIfChanged(ref _gagsEnabled, value); WriteMasterToggle("gags", value); }
    }
    /// <summary>Master enable for alias expansion.</summary>
    public bool AliasesEnabled
    {
        get => _aliasesEnabled;
        set { this.RaiseAndSetIfChanged(ref _aliasesEnabled, value); WriteMasterToggle("aliases", value); }
    }
    /// <summary>Master enable for room/scene artwork (settings.cfg key
    /// <c>showimages</c>). SceneViewModel reacts to the resulting
    /// ConfigChanged(ImagesEnabled) — off clears the picture, on re-fetches.</summary>
    public bool ImagesEnabled
    {
        get => _imagesEnabled;
        set { this.RaiseAndSetIfChanged(ref _imagesEnabled, value); WriteMasterToggle("showimages", value); }
    }

    /// <summary>Push a menu-toggle change into config and persist it. The
    /// engines react to the resulting ConfigChanged(MasterToggles), so this
    /// never touches them directly. No-op while syncing FROM config.</summary>
    private void WriteMasterToggle(string key, bool value)
    {
        if (_syncingMasterToggles || _core is null) return;
        _core.Config.SetSetting(key, value.ToString(), showException: false);
        _core.Config.Save();
    }

    /// <summary>Pull the master toggles from config into the menu
    /// checkboxes — on core wire-up and whenever config changes underneath us
    /// (typed #config, settings.cfg load, profile switch).</summary>
    private void SyncMasterTogglesFromConfig()
    {
        if (_core is null) return;
        _syncingMasterToggles = true;
        try
        {
            HighlightsEnabled  = _core.Config.EnableHighlights;
            TriggersEnabled    = _core.Config.EnableTriggers;
            SubstitutesEnabled = _core.Config.EnableSubstitutes;
            GagsEnabled        = _core.Config.EnableGags;
            AliasesEnabled     = _core.Config.EnableAliases;
            ImagesEnabled      = _core.Config.ShowImages;
        }
        finally { _syncingMasterToggles = false; }
    }

    // ── Auto Log (File ▸ Auto Log) ─────────────────────────────────────────
    // Config-backed (settings.cfg key `autolog`), same checkbox⇄config pattern
    // as the master toggles — but also applied LIVE: flipping it mid-session
    // starts/stops the logger instead of arming the next connect. Both routes
    // (menu click and typed `#config autolog`) funnel through
    // ConfigChanged(Autolog) → SyncAutoLogFromConfig, so they can't drift.

    private bool   _autoLogEnabled = true;
    private bool   _syncingAutoLog;
    private string _autoLogCharacter = string.Empty;   // session identity captured in ConnectAsync
    private string _autoLogGame      = string.Empty;   // so a mid-session enable names the file correctly

    /// <summary>Automatic rendered-text session log (Genie 4 AutoLog) on/off.</summary>
    public bool AutoLogEnabled
    {
        get => _autoLogEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoLogEnabled, value);
            if (_syncingAutoLog || _core is null) return;
            _core.Config.SetSetting("autolog", value.ToString(), showException: false);
            _core.Config.Save();
        }
    }

    // ── Align Input to Game Window (Layout menu — Genie 4 SizeInputToGame) ──
    // Config-backed (settings.cfg key `sizeinputtogame` — defined since the
    // config port but consumed by NOTHING until now). Horizontal-only per
    // Jason's spec: when on, the command bar's left/right margins track the
    // game window's horizontal extent inside the dock (MainWindow.axaml.cs
    // does the geometry); off (default) = full-width command bar.

    private bool _alignInputToGame;
    private bool _syncingAlignInput;

    /// <summary>Layout ▸ Align Input to Game Window on/off (Genie 4 parity).</summary>
    public bool AlignInputToGame
    {
        get => _alignInputToGame;
        set
        {
            this.RaiseAndSetIfChanged(ref _alignInputToGame, value);
            if (_syncingAlignInput || _core is null) return;
            _core.Config.SetSetting("sizeinputtogame", value.ToString(), showException: false);
            _core.Config.Save();
        }
    }

    /// <summary>Pull `sizeinputtogame` from config into the menu checkbox —
    /// both routes (menu click and typed #config) funnel through
    /// ConfigChanged(SizeInputToGame), same shape as the Auto Log sync.</summary>
    private void SyncAlignInputFromConfig()
    {
        if (_core is null) return;
        _syncingAlignInput = true;
        try { AlignInputToGame = _core.Config.SizeInputToGame; }
        finally { _syncingAlignInput = false; }
    }

    /// <summary>Pull `autolog` from config into the menu checkbox, then apply it
    /// to the running session: start the logger on a mid-session enable, stop it
    /// on a disable. While disconnected the flag only arms the next connect
    /// (ConnectAsync does the connect-time start).</summary>
    private void SyncAutoLogFromConfig()
    {
        if (_core is null) return;
        _syncingAutoLog = true;
        try { AutoLogEnabled = _core.Config.AutoLog; }
        finally { _syncingAutoLog = false; }

        if (!IsConnected || _autoLogCharacter.Length == 0) return;
        if (_core.Config.AutoLog && !AutoLogger.IsLogging)
        {
            // Notice BEFORE Start subscribes, so it isn't itself logged.
            GameText.AddSystemLine("[autolog] session logging on → Logs folder. Turn off with File ▸ Auto Log or #config autolog false.");
            AutoLogger.Start(GameText, _autoLogCharacter, _autoLogGame);
        }
        else if (!_core.Config.AutoLog && AutoLogger.IsLogging)
        {
            AutoLogger.Stop();
            GameText.AddSystemLine("[autolog] session logging off.");
        }
    }

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
    /// Directory of the built-in Analyst Capture recipes shipped beside the
    /// executable (<c>{appdir}/CaptureRecipes/</c>, copied there by the csproj).
    /// Loaded alongside any user recipe dir (<see cref="PathSettings.CaptureRecipeDirectory"/>).
    /// </summary>
    private string _builtinRecipesDir = "";

    /// <summary>The active analyst capture, or null when none is running.
    /// Recreated per Start because the output dir is user-chosen and may change.</summary>
    private AnalystCapture? _analystCapture;

    /// <summary>UI-thread heartbeat that pumps <see cref="Genie.Core.Scripting.ScriptEngine.Tick"/>
    /// so time-based script unblocks (pause / delay / waitfor) fire even with no
    /// incoming game traffic. Started on connect, stopped on disconnect (#61).</summary>
    private Avalonia.Threading.DispatcherTimer? _scriptHeartbeat;

    // ── Auto-reconnect (Genie 4 parity: Game.cs CheckReconnect / EventConnectionLost) ──
    /// <summary>1s ticker that fires a scheduled reconnect once its due time
    /// passes. Lives on the VM (not the per-connect core) so it survives the
    /// core rebuild a reconnect performs. Created lazily, then left running.</summary>
    private Avalonia.Threading.DispatcherTimer? _reconnectTimer;
    /// <summary>When set, a reconnect is due at this wall-clock time; null = none pending.</summary>
    private DateTime? _reconnectAt;
    /// <summary>Number of reconnect attempts since the last good connect — drives the backoff ladder.</summary>
    private int  _reconnectAttempts;
    /// <summary>The user (or an explicit #connect/Disconnect) tore the link down — do NOT auto-reconnect.</summary>
    private bool _manualDisconnect;
    /// <summary>We reached Connected this session. Only an established link that drops auto-reconnects
    /// (a failed initial login does not).</summary>
    private bool _wasConnected;
    /// <summary>Policy guard: the user has sent at least one command since this connect. An untouched,
    /// unattended session must never resurrect itself (DR ToS: no auto-reconnect-and-resume while away).</summary>
    private bool _userInputSinceConnect;
    /// <summary>The user sent a deliberate disconnect verb (DR's <c>quit</c>/<c>exit</c>) — the server
    /// close that follows is INTENTIONAL, so do NOT auto-reconnect (the user chose to leave; this is the
    /// sharpest edge of the no-auto-resume policy). Cleared on the next non-quit command (a rejected quit
    /// means they're still playing) and on each fresh connect.</summary>
    private bool _intentionalGameExit;
    /// <summary>This drop was an UNEXPECTED dead link — the transport raised an <see cref="ConnectionEventKind.Error"/>
    /// (read exception / keepalive failure / activity watchdog) before the paired Disconnected. Only such a drop
    /// auto-reconnects. A clean server-initiated close (DR idle-kick, boot, server exit/quit) arrives as a bare
    /// Disconnected with no Error and must NOT auto-reconnect — the server deliberately ended the session and the
    /// user is idle/away (no resume-while-away). Reset on each fresh connect.</summary>
    private bool _dropWasError;
    /// <summary>The actionable reason for the most recent drop (bad password / PROBLEM / dead
    /// link / timeout), captured from the <see cref="ConnectionEventKind.Error"/> message so the
    /// session-ending "Disconnected" popup (#114) can show it. Null for a clean close.</summary>
    private string? _lastDropReason;

    /// <summary>PID of a Lich process Genie auto-launched this session. Null when we only
    /// attached to an already-running Lich (never kill that). Cleared after <see cref="StopOwnedLich"/>.</summary>
    private int? _ownedLichProcessId;

    /// <summary>Character + port key for the owned auto-launched Lich — used to detect a
    /// character/port switch that needs a stop-then-relaunch before connect.</summary>
    private string? _ownedLichLaunchKey;

    /// <summary>Base name of the recipe script whose completion should auto-stop
    /// the current capture (null for a manual capture, which the user stops).</summary>
    private string? _activeCaptureScript;

    /// <summary>Cross-platform SFX backend for trigger/highlight sounds and
    /// <c>#play</c>. Fed gate-passed absolute paths via GenieCore.SoundRequested.</summary>
    private readonly Services.AudioService _audio = new();

    /// <summary>Offline neural TTS backend for <c>#speak</c> (and later
    /// per-stream read-aloud). Fed text via GenieCore.SpeakRequested. Created at
    /// connect time once the voice dir is known.</summary>
    private Services.TtsService? _tts;

    /// <summary>Downloads + installs TTS voice models for <c>#tts install</c>.</summary>
    private readonly Services.VoiceInstaller _voiceInstaller = new();

    /// <summary>True once the one-time "what Analyst Capture does" explainer has
    /// been shown this session, so enabling it again doesn't re-nag.</summary>
    private bool _captureExplained;

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
        // Built-in capture recipes ship beside the exe (csproj copies them).
        _builtinRecipesDir = Path.Combine(AppContext.BaseDirectory, "CaptureRecipes");

        // Global layout presets — one JSON per layout, at {AppData}/Genie5/Layouts/.
        // Per-profile presets attach on connect (see SetProfileLayoutScope).
        var layoutsDir = Path.Combine(Path.GetDirectoryName(_configDir)!, "Layouts");
        _globalLayouts = new Settings.LayoutStore(layoutsDir);

        ErrorLog.Initialize(_configDir);

        // Announce the resolved data root as the first game-window line.
        // "Which folder is Genie actually reading?" is the first question in
        // every portable-install report (#138) — answer it up front instead
        // of leaving users to folder archaeology.
        GameText.AddSystemLine(
            $"[data] root: {dir.Current.BasePath} ({(dir.Current.IsLocal ? "portable" : "user folder")})");

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

        // ── Themes (#20) ──────────────────────────────────────────────────
        // Re-apply the persisted theme's chrome + Fluent variant on top of
        // the design-time Dark defaults. Chrome only: game-text colours come
        // from display.json (Display.Apply() above), so user tweaks survive.
        Themes = new Theming.ThemeService(Path.Combine(_configDir, "Themes"), Display);
        Themes.ApplyStartup();

        // Register every known dockable with the window-settings store so the
        // Layout tab in Configuration sees the full list. Genie 4 standard ids
        // (talk, whispers, …) inherit IfClosed defaults from the store's
        // built-in table; ours (game-text, vitals, …) just take generic defaults.
        WindowSettings.Register("game-text", "Game");
        WindowSettings.Register("vitals",    "Vitals");
        WindowSettings.Register("room",      "Room");
        WindowSettings.Register("backpack",  "Inventory");
        WindowSettings.Register("logons",    "Logons");
        WindowSettings.Register("talk",      "Talk");
        WindowSettings.Register("whispers",  "Whispers");
        WindowSettings.Register("thoughts",  "Thoughts");
        WindowSettings.Register("combat",    "Combat");
        WindowSettings.Register("familiar",  "Familiar");
        WindowSettings.Register("death",     "Deaths");
        WindowSettings.Register("assess",    "Assess");
        WindowSettings.Register("atmospherics", "Atmospherics");
        WindowSettings.Register("log",       "Log");
        WindowSettings.Register("itemlog",   "ItemLog");
        WindowSettings.Register("mapper",    "Mapper");
        WindowSettings.Register("experience", "Experience");
        WindowSettings.Register("analytics",  "Analytics");
        WindowSettings.Register("active-spells", "Active Spells");
        WindowSettings.Register("time-tracker", "Time Tracker");
        WindowSettings.Register("inventory-view", "Inventory View");
        WindowSettings.Register("scripts",   "Script Manager");   // id predates the panel's evolution into the manager
        WindowSettings.Register("scene",     "Portrait");   // Genie 4's Portrait window (room/scene art); id predates the rename
        WindowSettings.Register("mobs",      "Mobs");
        WindowSettings.Register("players",   "Players");
        WindowSettings.Register("raw-xml",   "Raw XML");
        WindowSettings.Register("injuries",  "Injuries");

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
        // Out-of-box default: the Mapper floats in its own window rather than
        // docking at the centre-bottom. Arm the pending-float flag; the window
        // floats it from MainWindow.OnOpened, once the dock tree + owner window
        // are live (FloatDockable needs both).
        //
        // BUT only for a genuinely fresh setup: if the user has already defined
        // the Mapper's location in a saved default layout/profile, that layout
        // owns placement (applied on connect via ApplyDefaultLayoutForConnect) —
        // don't override it with a float. This is the "keep what the user chose"
        // rule, and it also removes the float→re-dock flicker for those users.
        if (!HasUserDefinedDefaultLayout())
            factory.PendingMapperFloat = true;

        // Wire the Mapper's "pop out" button. Done here (after factory exists)
        // because the VM doesn't carry a factory reference itself.
        Mapper.FloatRequested = () => factory.FloatTool("mapper");

        ConnectCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var result = await ShowConnectDialog.Handle(Unit.Default);
            if (result is null) return;
            // Same teardown as #connect — character change must drop the old
            // session (and Genie-owned Lich) before the new auto-launch.
            if (IsConnected) await DisconnectAsync();
            else _manualDisconnect = true;
            await ConnectAsync(result.Config, result.Profile);
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
            var cfgVm = new ConfigurationViewModel(_core, _configDir, Profiles, ConnectedProfile, WindowSettings, Display, _displayPath,
                                                   GetProfileConfigDir);
            // TTS hooks for the Text-to-Speech tab — this VM owns the TtsService.
            cfgVm.SpeakSample     = text => _tts?.Speak(text, Services.TtsPriority.High);
            cfgVm.TtsVoiceChanged = () => _tts?.Reset();
            await ShowConfigurationDialog.Handle(cfgVm);
        });

        // File -> Import from Genie 4...
        // Opens the Genie4ImportDialog with auto-detected source path +
        // probe + per-type checkboxes + Global/Profile routing. The dialog
        // needs the live engines to apply imports to, so build the core on
        // demand exactly like the first typed command does — offline import
        // into the global Config folder is a designed use case (#164).
        Genie4ImportCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            EnsureCoreBuilt(null, eagerLoadOfflineRules: true);
            if (_core is null) return;   // EnsureCoreBuilt always assigns; guards the nullable field
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

        // ── Edit → Theme commands (#20) ───────────────────────────────────
        // Apply is instant + persisted; the whole UI repaints live via the
        // DynamicResource palette (no restart). Applying a theme also seeds
        // the game-text/echo colours — later Display Settings tweaks and
        // per-stream preset colours always win over the theme.
        ApplyThemeCommand = ReactiveCommand.Create<ThemeMenuItem>(item =>
        {
            if (item is null) return;
            var theme = Themes.Find(item.Name);
            if (theme is null)
            {
                GameText.AddSystemLine($"[theme] unknown theme '{item.Name}'");
                return;
            }
            Themes.Apply(theme);
            Display.Save(_displayPath);
            RefreshThemeList();
            GameText.AddSystemLine($"[theme] applied '{theme.Name}'");
        });

        // Snapshot the live palette (incl. any tweaked game/echo colours)
        // into Config/Themes/{name}.json and make it the active theme.
        SaveThemeAsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var name = await ShowThemeNamePrompt.Handle("My Theme");
            if (string.IsNullOrWhiteSpace(name)) return;
            var saved = Themes.SaveCurrentAs(name);
            if (saved is null)
            {
                GameText.AddSystemLine(
                    $"[theme] can't save as '{name}' — that name is reserved by a built-in theme");
                return;
            }
            Display.Save(_displayPath);
            RefreshThemeList();
            GameText.AddSystemLine(
                $"[theme] saved current colours as '{saved.Name}' → {Path.Combine(_configDir, "Themes")}");
        });

        // Edit Theme… — full colour-role editor with live preview. The VM
        // previews every change through ThemeService.Preview, so by the time
        // the dialog closes the app is painted with the edited palette:
        // Save persists + activates it; Cancel/✕ restores the pre-edit state.
        EditThemeCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var editor = new ThemeEditorViewModel(Themes, Display);
            var saved  = await ShowThemeEditorDialog.Handle(editor);
            if (!saved)
            {
                editor.RestoreOriginal();
                return;
            }

            var theme = editor.BuildTheme();
            if (!Themes.SaveOrUpdateCustom(theme))
            {
                // CanSave gating makes this near-impossible; belt & braces.
                editor.RestoreOriginal();
                GameText.AddSystemLine(
                    $"[theme] can't save '{theme.Name}' — that name is reserved by a built-in theme");
                return;
            }
            Themes.Apply(theme);
            Display.Save(_displayPath);
            RefreshThemeList();
            GameText.AddSystemLine(
                $"[theme] saved and applied '{theme.Name}' → {Path.Combine(_configDir, "Themes")}");
        });

        ResetThemeCommand = ReactiveCommand.Create(() =>
        {
            Themes.Apply(Theming.BuiltInThemes.Dark);
            Display.Save(_displayPath);
            RefreshThemeList();
            GameText.AddSystemLine("[theme] reset to Dark");
        });

        RefreshThemeListCommand = ReactiveCommand.Create(RefreshThemeList);
        RefreshThemeList();

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
            // Reset is a *default* presentation, so the Mapper should float
            // (matches first-run). Arm before ApplyLayout — its legacy fallback
            // rebuilds the default tree — then float once the tree settles.
            if (DockFactory is GenieDockFactory rf) rf.PendingMapperFloat = true;
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
            FloatMapperAfterLayout();
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
            // Scripts pull into the SAME folder the script engine loads from
            // (Core's resolved ScriptDir when connected — see issue #37's
            // OpenScriptsFolder rationale); pre-connect, the shared root.
            var scriptsDir = _core is not null
                                 ? _core.Config.ScriptDir
                                 : Path.Combine(Path.GetDirectoryName(_configDir)!, "Scripts");
            var vm = new UpdatesDialogViewModel(store, mapsDir, _pluginsDir, scriptsDir, zoneRepo, pluginMgr);
            await ShowUpdatesDialog.Handle(vm);
            // Re-run a background check so the badge reflects post-dialog state
            // (a successful update during the session should clear the dot).
            _ = CheckForUpdatesInBackgroundAsync();
        });

        // Help → About — version, links, license, credits. No input/output.
        ShowAboutCommand = ReactiveCommand.CreateFromTask(async () =>
            await ShowAboutDialog.Handle(Unit.Default));

        // Help → Changelog — read the bundled release notes in-app (#155).
        ShowChangelogCommand = ReactiveCommand.CreateFromTask(async () =>
            await ShowChangelogDialog.Handle(Unit.Default));

        // Maps → AutoMapper Settings (#147) — slim consolidation dialog.
        ShowMapperSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
            await ShowMapperSettingsDialog.Handle(Unit.Default));

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
            OpenFolder(scriptsDir, "OpenScriptsFolder");
        });

        // Seed the Help ▸ Update Settings checkboxes from update-feeds.json,
        // then fire the startup check so the Help-menu badge + status-bar
        // notice surface any pending updates without the user having to open
        // the dialog. Errors are swallowed — badge stays clear on network
        // failure. The check honours the per-kind CheckOnStartup policies and
        // auto-applies where the matching Auto-Apply toggle is on.
        LoadUpdatePolicies();
        DismissUpdateNoticeCommand = ReactiveCommand.Create(() => { UpdateNotice = ""; });

        DismissXmlGapNoticeCommand = ReactiveCommand.Create(() => { XmlGapNotice = ""; });
        ReportXmlGapCommand = ReactiveCommand.Create(() =>
        {
            if (!string.IsNullOrEmpty(_pendingXmlGapUrl))
                OpenUrl(_pendingXmlGapUrl, "the XML gap report");
            XmlGapNotice = "";
        });
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

        ToggleIconBarCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowIconBar = !Display.ShowIconBar;
            Display.Save(_displayPath);
        });

        ToggleMagicPanelsCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowMagicPanels = !Display.ShowMagicPanels;
            Display.Save(_displayPath);
        });

        ToggleZoneRoomIdCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowZoneRoomId = !Display.ShowZoneRoomId;
            Display.Save(_displayPath);
        });

        ToggleZoneRoomNumberCommand = ReactiveCommand.Create(() =>
        {
            Display.ZoneRoomShowNumber = !Display.ZoneRoomShowNumber;
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
            // Close any floating windows on the outgoing root so a floated tool
            // (e.g. the Mapper) doesn't survive the tree swap as an orphan next
            // to its rebuilt copy — same duplicate-window guard as ApplyLayout.
            factory.CloseFloatingWindows();
            DockLayout = Display.WindowedMode
                ? factory.BuildMdiLayout(_mdiBoundsCache)
                : factory.BuildDefaultLayout();
            // Returning to tabbed mode presents the default → float the Mapper.
            // (MDI mode already shows it as its own window, so don't arm there.)
            if (!Display.WindowedMode)
            {
                factory.PendingMapperFloat = true;
                FloatMapperAfterLayout();
            }
            RefreshVisibilityBools();
            GameText.AddSystemLine($"[layout] {(Display.WindowedMode ? "windowed (MDI)" : "tabbed")} mode");
        });

        ToggleGuildInTitleCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowGuildInTitle = !Display.ShowGuildInTitle;
            Display.Save(_displayPath);
        });

        ToggleDisconnectPopupCommand = ReactiveCommand.Create(() =>
        {
            Display.ShowDisconnectPopup = !Display.ShowDisconnectPopup;
            Display.Save(_displayPath);
        });

        ToggleAlwaysOnTopCommand = ReactiveCommand.Create(() =>
        {
            Display.AlwaysOnTop = !Display.AlwaysOnTop;
            Display.Save(_displayPath);
            // Mirror into settings.cfg so #config alwaysontop / #config list
            // report what the window is actually doing (Genie 4 parity key).
            if (_core?.Config is { } cfg && cfg.AlwaysOnTop != Display.AlwaysOnTop)
            {
                cfg.AlwaysOnTop = Display.AlwaysOnTop;
                cfg.Save();
            }
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

        // AutoLog (Genie 4) — automatic rendered-text session log. Started in
        // ConnectAsync when Config.AutoLog is on; stopped in DisconnectAsync.
        AutoLogger = new SessionTextLogger(_logsDir);
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

        ToggleMuteCommand = ReactiveCommand.Create(() =>
        {
            var config = _core?.Config;
            if (config is null)
            {
                // Same auto-toggle trap as Record Session above: the checkmark
                // already flipped visually, so say why nothing happened and let
                // the reconcile below repaint it unchecked.
                GameText.AddSystemLine("[sounds] Connect to a game first — settings load with the session.");
            }
            else
            {
                // Route through SetSetting so this is the same write path as
                // `#config muted` — it fires ConfigChanged(Muted) for any other
                // listener — then persist like the #config handler does.
                bool mute = config.PlaySounds;
                config.SetSetting("muted", mute.ToString());
                config.Save();
                if (mute) _audio.Stop();   // cut any in-flight SFX immediately
                GameText.AddSystemLine(mute
                    ? "[sounds] muted — trigger/highlight sounds and #play are off (#config muted true)."
                    : "[sounds] unmuted.");
            }

            // Reconcile the checkbox to the config's actual state (see the
            // IsRecording note above for why this must run on every toggle).
            IsMuted = !(_core?.Config.PlaySounds ?? true);
        });

        // Title-bar composition — show "🔴 REC" suffix while recording so the
        // user can't forget the capture is running (matches the compliance
        // review's recommendation that recording always be visibly indicated).
        // The red-circle emoji (U+1F534) is rendered with its intrinsic red
        // glyph by every modern color-emoji font; OS window-title chrome
        // strings are otherwise plain text with no markup for per-char color,
        // so this is the simplest reliable way to make the indicator red.
        this.WhenAnyValue(x => x.ConnectionStatus, x => x.CharacterGuild,
                          x => x.IsRecording, x => x.Display.ShowGuildInTitle,
                          x => x.IsCapturing, x => x.ConnectionSecurity)
            .Subscribe(_ =>
            {
                var guild = (Display.ShowGuildInTitle && !string.IsNullOrWhiteSpace(CharacterGuild))
                    ? $" — {CharacterGuild}" : "";
                var sec   = string.IsNullOrEmpty(ConnectionSecurity) ? "" : $"  {ConnectionSecurity}";
                var rec   = IsRecording ? "  🔴 REC" : "";
                var cap   = IsCapturing ? "  🔴 CAP" : "";
                WindowTitle = $"Genie 5 {Genie.Core.GenieCore.HostVersionString} — {ConnectionStatus}{sec}{guild}{rec}{cap}";
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
        // Mapper gets a bespoke toggle: when shown and the user hasn't pinned
        // its placement via a default layout, open it as a FLOATING window
        // (its natural V5 home — matches the startup auto-float) rather than
        // docked at the centre-bottom. A user with a saved default layout keeps
        // their docked placement.
        ToggleMapperCommand   = ReactiveCommand.Create(() =>
        {
            if (DockFactory is not GenieDockFactory factory) return;
            var newVisible = !factory.IsToolVisible("mapper");
            if (newVisible && !HasUserDefinedDefaultLayout())
                factory.ShowToolFloating("mapper");
            else
                factory.SetToolVisibility("mapper", newVisible);
            MapperVisible = newVisible;
        });
        ToggleExperienceCommand = MakeToggleCommand("experience", v => ExperienceVisible = v);
        ToggleAnalyticsCommand  = MakeToggleCommand("analytics",  v => AnalyticsVisible  = v);
        ToggleActiveSpellsCommand = MakeToggleCommand("active-spells", v => ActiveSpellsVisible = v);
        ToggleTimeTrackerCommand = MakeToggleCommand("time-tracker", v => TimeTrackerVisible = v);
        ToggleInventoryViewCommand = MakeToggleCommand("inventory-view", v => InventoryViewVisible = v);
        ToggleLogonsCommand   = MakeToggleCommand("logons",    v => LogonsVisible   = v);
        ToggleTalkCommand     = MakeToggleCommand("talk",      v => TalkVisible     = v);
        ToggleWhispersCommand = MakeToggleCommand("whispers",  v => WhispersVisible = v);
        ToggleThoughtsCommand = MakeToggleCommand("thoughts",  v => ThoughtsVisible = v);
        ToggleCombatCommand   = MakeToggleCommand("combat",    v => CombatVisible   = v);
        ToggleFamiliarCommand = MakeToggleCommand("familiar",  v => FamiliarVisible = v);
        ToggleDeathCommand    = MakeToggleCommand("death",     v => DeathVisible    = v);
        ToggleAssessCommand   = MakeToggleCommand("assess",    v => AssessVisible   = v);
        ToggleAtmosphericsCommand = MakeToggleCommand("atmospherics", v => AtmosphericsVisible = v);
        ToggleLogCommand      = MakeToggleCommand("log",       v => LogVisible      = v);
        ToggleItemLogCommand  = MakeToggleCommand("itemlog",   v => ItemLogVisible  = v);
        ToggleScriptsCommand  = MakeToggleCommand("scripts",   v => ScriptsVisible  = v);
        ToggleSceneCommand    = MakeToggleCommand("scene",     v => SceneVisible    = v);
        ToggleMobsCommand     = MakeToggleCommand("mobs",      v => MobsVisible     = v);
        TogglePlayersCommand  = MakeToggleCommand("players",   v => PlayersVisible  = v);
        ToggleRawXmlCommand   = MakeToggleCommand("raw-xml",   v => RawXmlVisible   = v);
        ToggleInjuriesCommand = MakeToggleCommand("injuries",  v => InjuriesVisible = v);

        // (ResetLayoutCommand is assigned earlier — using ApplyLayout() with a
        // SavedLayout that goes through factory.BuildDefaultLayout(). A second
        // assignment here previously overwrote that with a shallow
        // SetToolVisibility loop, which left drag-redocked panels in place and
        // forced Vitals into a docked slot. Removed; the earlier assignment is
        // the canonical reset path.)

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
        // File ▸ Open Directory: data root + active config dir (Logs/Maps/
        // Scripts/Plugins reuse their existing commands). The config dir is
        // read at click time so it tracks the per-character profile switch.
        OpenDataFolderCommand       = ReactiveCommand.Create(
            () => OpenFolder(Path.GetDirectoryName(_configDir)!, "OpenDataFolder"));
        OpenConfigFolderCommand     = ReactiveCommand.Create(
            () => OpenFolder(_core?.Config.ConfigProfileDir ?? _configDir, "OpenConfigFolder"));
        TogglePerfOverlayCommand    = ReactiveCommand.Create(() => Perf.Toggle());

        // ── Analyst Capture commands ───────────────────────────────────────
        ToggleAnalystCaptureCommand = ReactiveCommand.CreateFromTask(ToggleAnalystCaptureAsync);
        RunRecipeCommand            = ReactiveCommand.CreateFromTask<RecipeMenuItem>(item => RunRecipeAsync(item.Recipe));
        StartManualCaptureCommand   = ReactiveCommand.CreateFromTask(StartManualCaptureAsync);
        StopCaptureCommand          = ReactiveCommand.Create(() => StopCapture("stopped by user"));
        OpenCaptureFolderCommand    = ReactiveCommand.Create(OpenCaptureFolder);
        SetCaptureFolderCommand     = ReactiveCommand.CreateFromTask(async () => { await SetCaptureFolderAsync(); });
        SetRecipeFolderCommand      = ReactiveCommand.CreateFromTask(SetRecipeFolderAsync);
        RefreshRecipes();

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
    // Runs cheap CheckAsync on every enabled feed in the background, honouring
    // the per-kind Help ▸ Update Settings policies: kinds with CheckOnStartup
    // off are skipped entirely; kinds with Auto-Apply on install what the
    // check finds (Maps / Plugins / Scripts immediately; Core stages via
    // Velopack to install on exit — never a surprise restart). Results drive
    // the Help-menu ● badge and the status-bar UpdateNotice strip. Failures
    // are silent — we'd rather a flaky network not produce a misleading badge.
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

            var available = new List<string>();   // kinds with updates awaiting a click
            var applied   = new List<string>();   // kinds the auto-apply toggles handled

            if (cfg.MapsPolicy.CheckOnStartup)
                foreach (var feed in cfg.Maps.Where(f => f.Enabled))
                {
                    try
                    {
                        var src = new Genie.Core.Update.Sources.GithubContentsSource(
                            feed.Owner, feed.Repo, feed.Path, feed.Extension);
                        var u = new Genie.Core.Update.Updaters.MapsUpdater(
                            zoneRepo, mapsDir, new[] { (Genie.Core.Update.Sources.IFileListSource)src });
                        if (!(await u.CheckAsync()).UpdateAvailable) continue;
                        if (cfg.MapsPolicy.AutoApply && (await u.ApplyAsync()).Succeeded)
                            applied.Add("Maps");
                        else
                            available.Add("Maps");
                    }
                    catch { /* silent — see method header */ }
                }

            if (cfg.PluginsPolicy.CheckOnStartup)
                foreach (var feed in cfg.Plugins.Where(f => f.Enabled))
                {
                    try
                    {
                        var src = new Genie.Core.Update.Sources.GithubReleasesSource(feed.Owner, feed.Repo);
                        var u = new Genie.Core.Update.Updaters.PluginUpdater(
                            feed, src, _pluginsDir, pluginMgr, channel);
                        if (!(await u.CheckAsync()).UpdateAvailable) continue;
                        if (cfg.PluginsPolicy.AutoApply && (await u.ApplyAsync()).Succeeded)
                            applied.Add("Plugins");
                        else
                            available.Add("Plugins");
                    }
                    catch { /* silent — see method header */ }
                }

            var scriptsDir = _core is not null
                                 ? _core.Config.ScriptDir
                                 : Path.Combine(Path.GetDirectoryName(_configDir)!, "Scripts");
            if (cfg.ScriptsPolicy.CheckOnStartup)
                foreach (var feed in cfg.Scripts.Where(f => f.Enabled))
                {
                    try
                    {
                        var src = UpdatesDialogViewModel.MakeScriptsSource(feed);
                        var u = new Genie.Core.Update.Updaters.ScriptsUpdater(scriptsDir, new[] { src });
                        if (!(await u.CheckAsync()).UpdateAvailable) continue;
                        if (cfg.ScriptsPolicy.AutoApply && (await u.ApplyAsync()).Succeeded)
                            applied.Add("Scripts");
                        else
                            available.Add("Scripts");
                    }
                    catch { /* silent — see method header */ }
                }

            // Core app — only meaningful when running from a Velopack install
            // (CoreAppUpdater short-circuits with "(dev build)" otherwise, so
            // there's no risk of spurious badges from dev runs).
            if (cfg.CorePolicy.CheckOnStartup)
                try
                {
                    var coreUrl = $"https://github.com/{cfg.Core.Owner}/{cfg.Core.Repo}";
                    var core    = new Services.CoreAppUpdater(coreUrl, channel);
                    var chk     = await core.CheckAsync();
                    if (chk.UpdateAvailable)
                    {
                        if (cfg.CorePolicy.AutoApply && (await core.StageOnExitAsync()).Succeeded)
                            applied.Add($"Genie v{chk.LatestVersion} (installs on exit)");
                        else
                            available.Add("Genie");
                    }
                }
                catch { /* silent — see method header */ }

            // Compose the status-bar notice. Both lists empty → clear it, so a
            // previous notice whose updates have since been applied never lingers.
            var avail = available.Distinct().ToList();
            var done  = applied.Distinct().ToList();
            var parts = new List<string>(2);
            if (avail.Count > 0) parts.Add($"Updates available: {string.Join(", ", avail)}");
            if (done.Count  > 0) parts.Add($"Auto-updated: {string.Join(", ", done)}");
            var notice = string.Join("  ·  ", parts);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdatesAvailable = avail.Count > 0;
                UpdateNotice     = notice;
            });
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
    /// See <see cref="Genie.Core.Runtime.FileBrowser"/> for why this must use
    /// <c>ArgumentList</c> rather than string arguments (paths with spaces,
    /// e.g. macOS <c>~/Library/Application Support/Genie5</c>).</summary>
    private static void OpenFolder(string dir, string logTag)
    {
        try
        {
            Genie.Core.Runtime.FileBrowser.OpenDirectory(dir, createIfMissing: true);
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

    /// <summary>Environment/context for an XML-gap issue draft. Version comes
    /// from the assembly InformationalVersion (single source of truth); the git
    /// hash after '+' becomes the parser-commit line when present.</summary>
    private static Genie.Core.Diagnostics.XmlGapReport.ReportContext BuildXmlGapContext(string trigger)
    {
        var asm  = System.Reflection.Assembly.GetEntryAssembly();
        var info = (asm is null ? null : System.Reflection.CustomAttributeExtensions
                       .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion)
                   ?? "5.0.0";
        var plus    = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;
        var commit  = plus >= 0 ? info[(plus + 1)..] : "local";
        return new Genie.Core.Diagnostics.XmlGapReport.ReportContext(
            AppVersion: version,
            Os:         System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Commit:     commit,
            Trigger:    trigger);
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

        // First-run users typically haven't pulled the Maps repo yet, so
        // OpenFolder creates the configured directory when missing.
        OpenFolder(dir, "OpenMapsFolder");
    }

    /// <summary>
    /// Opens the recordings folder in the OS file browser. Mirrors
    /// <see cref="OpenMapsFolder"/>. Creates the directory if it doesn't
    /// exist yet (user may have installed but never recorded).
    /// </summary>
    private void OpenRecordingsFolder() => OpenFolder(_logsDir, "OpenRecordingsFolder");


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

    // ── Analyst Capture ─────────────────────────────────────────────────────

    /// <summary>Rebuild <see cref="CaptureRecipes"/> from the built-in recipe dir
    /// plus the optional user recipe dir. Each entry carries the shared
    /// <see cref="RunRecipeCommand"/> so the menu container style binds it.</summary>
    private void RefreshRecipes()
    {
        CaptureRecipes.Clear();
        void AddFrom(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            foreach (var r in CaptureRecipe.LoadAll(dir))
                CaptureRecipes.Add(new RecipeMenuItem(r.Name, r, RunRecipeCommand));
        }
        AddFrom(_builtinRecipesDir);
        AddFrom(Paths.CaptureRecipeDirectory);
    }

    /// <summary>Toggle the Analyst Capture feature. First enable shows a one-time
    /// explainer (policy: redaction on, local-only, user-executed). Disabling
    /// stops any active capture.</summary>
    private async Task ToggleAnalystCaptureAsync()
    {
        if (AnalystCaptureEnabled)
        {
            AnalystCaptureEnabled = false;
            if (IsCapturing) StopCapture("analyst capture disabled");
            return;
        }

        if (!_captureExplained &&
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d &&
            d.MainWindow is { } owner)
        {
            var dlg = new Genie.App.Views.ConfirmDialog(
                "Enable Analyst Capture",
                "Analyst Capture records a REDACTED copy of this session to a folder you choose, so it " +
                "can be handed to an analyst (or an AI assistant) for parser / analysis work.\n\n" +
                "• Other players' speech (talk / whispers / thoughts / familiar / OOC / group / logons) is " +
                "stripped by default.\n" +
                "• Files are written locally only — nothing is sent anywhere automatically.\n" +
                "• Recipes never auto-run — you start each capture yourself.\n\n" +
                "Enable it now?");
            if (!await dlg.ShowDialog<bool>(owner)) { AnalystCaptureEnabled = false; return; }
            _captureExplained = true;
        }

        AnalystCaptureEnabled = true;
        GameText.AddSystemLine("[analyst] capture enabled — run a recipe or Start Manual Capture from the Analyst menu.");
    }

    /// <summary>Run a capture recipe: confirm → ensure folder → start capture →
    /// run the recipe's `.cmd` through the script engine.</summary>
    private async Task RunRecipeAsync(CaptureRecipe recipe)
    {
        if (!EnsureCaptureReady()) return;

        var dir = await EnsureCaptureFolderAsync();
        if (dir is null) return;

        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d
            || d.MainWindow is not { } owner) return;

        var sends   = recipe.Sends.Count > 0 ? "  • " + string.Join("\n  • ", recipe.Sends) : "  (none)";
        var streams = string.Join(", ", recipe.BuildRedactor().RedactedStreams);
        var msg =
            (string.IsNullOrWhiteSpace(recipe.Description)  ? "" : recipe.Description + "\n\n") +
            (string.IsNullOrWhiteSpace(recipe.Precondition) ? "" : $"Before you run:\n{recipe.Precondition}\n\n") +
            $"Sends to the game:\n{sends}\n\n" +
            $"Writes a redacted capture (dropping: {streams})\nto: {dir}\n\n" +
            (recipe.EstimatedSeconds > 0 ? $"Takes about {recipe.EstimatedSeconds}s. " : "") +
            "Run now?";

        var dlg = new Genie.App.Views.ConfirmDialog($"Run capture: {recipe.Name}", msg);
        if (!await dlg.ShowDialog<bool>(owner)) return;

        if (recipe.CmdPath is not { } cmdPath || !File.Exists(cmdPath))
        {
            GameText.AddSystemLine($"[analyst] recipe script not found: {recipe.Cmd}");
            return;
        }

        StartCapture(dir, recipe, Path.GetFileNameWithoutExtension(cmdPath));
        if (!_core!.Scripts.TryStartFile(cmdPath))
            StopCapture("recipe failed to start");
    }

    /// <summary>Start a manual (recipe-less) capture; the user drives the game and
    /// stops it via Analyst → Stop Capture.</summary>
    private async Task StartManualCaptureAsync()
    {
        if (!EnsureCaptureReady()) return;
        var dir = await EnsureCaptureFolderAsync();
        if (dir is null) return;
        StartCapture(dir, recipe: null, activeScript: null);
        GameText.AddSystemLine("[analyst] manual capture started — stop it via Analyst → Stop Capture.");
    }

    /// <summary>Guard shared by the capture entry points: feature on + connected.</summary>
    private bool EnsureCaptureReady()
    {
        if (!AnalystCaptureEnabled)
        {
            GameText.AddSystemLine("[analyst] enable Analyst Capture first (Analyst menu).");
            return false;
        }
        if (_core is null || !IsConnected)
        {
            GameText.AddSystemLine("[analyst] connect first — capture needs a live session.");
            return false;
        }
        return true;
    }

    /// <summary>(Re)create the capture for <paramref name="dir"/> and start it on
    /// the current core's streams. Recreated per start since the dir is user-chosen.</summary>
    private void StartCapture(string dir, CaptureRecipe? recipe, string? activeScript)
    {
        var name = !string.IsNullOrWhiteSpace(_core!.State.CharacterName)
            ? _core.State.CharacterName
            : ConnectedProfile?.CharacterName ?? "unknown";

        var meta = new Dictionary<string, string?>
        {
            ["guild"]          = string.IsNullOrWhiteSpace(CharacterGuild) ? null : CharacterGuild,
            ["connectionMode"] = LastConnectionConfig?.Mode.ToString(),
        };

        _analystCapture?.Dispose();
        _analystCapture = new AnalystCapture(dir,
            m => Avalonia.Threading.Dispatcher.UIThread.Post(() => GameText.AddSystemLine(m)));
        _analystCapture.Start(_core.RawXmlStream, _core.GameEvents, name, DateTime.UtcNow, recipe, extraMeta: meta);

        _activeCaptureScript = activeScript;
        IsCapturing = true;
    }

    /// <summary>Stop the active capture (if any), writing its meta sidecar.</summary>
    private void StopCapture(string reason)
    {
        if (!IsCapturing && _analystCapture is null) return;
        _analystCapture?.Stop();
        _activeCaptureScript = null;
        IsCapturing = false;
        GameText.AddSystemLine($"[analyst] capture stopped ({reason}).");
    }

    /// <summary>Resolve the capture output dir, prompting for one if unset
    /// ("user-picked"). Returns null if the user cancels.</summary>
    private async Task<string?> EnsureCaptureFolderAsync()
    {
        var dir = Paths.CaptureOutputDirectory;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            try { Directory.CreateDirectory(dir); return dir; }
            catch (Exception ex) { ErrorLog.Log("EnsureCaptureFolder", ex); }
        }
        return await SetCaptureFolderAsync();
    }

    /// <summary>Folder picker for the capture output dir. Persists + returns the
    /// chosen path (null on cancel).</summary>
    private async Task<string?> SetCaptureFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        if (desktop.MainWindow?.StorageProvider is not { } sp) return null;

        IStorageFolder? start = null;
        var current = Paths.CaptureOutputDirectory;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            try   { start = await sp.TryGetFolderFromPathAsync(current); }
            catch { /* picker falls back to its default location */ }
        }

        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Choose a folder for analyst captures",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        var folder = picked?.FirstOrDefault();
        if (folder is null) return null;

        var path = folder.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        Paths.CaptureOutputDirectory = path;
        try   { Paths.Save(_pathsPath); }
        catch (Exception ex) { ErrorLog.Log("SaveCaptureFolder", ex); }
        GameText.AddSystemLine($"[analyst] capture folder: {path}");
        return path;
    }

    /// <summary>Folder picker for an extra user-recipe dir; reloads the recipe list.</summary>
    private async Task SetRecipeFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        if (desktop.MainWindow?.StorageProvider is not { } sp) return;

        IStorageFolder? start = null;
        var current = Paths.CaptureRecipeDirectory;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            try   { start = await sp.TryGetFolderFromPathAsync(current); }
            catch { /* picker falls back to its default location */ }
        }

        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Choose a folder of capture recipes",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        var folder = picked?.FirstOrDefault();
        if (folder is null) return;

        var path = folder.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        Paths.CaptureRecipeDirectory = path;
        try   { Paths.Save(_pathsPath); }
        catch (Exception ex) { ErrorLog.Log("SaveRecipeFolder", ex); }
        RefreshRecipes();
        GameText.AddSystemLine($"[analyst] recipe folder: {path} ({CaptureRecipes.Count} recipe(s) total).");
    }

    /// <summary>Open the capture output dir in the OS file browser (creates it if
    /// needed). Mirrors <see cref="OpenRecordingsFolder"/>.</summary>
    private void OpenCaptureFolder()
    {
        var dir = Paths.CaptureOutputDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            GameText.AddSystemLine("[analyst] no capture folder set — use Analyst → Set Capture Folder first.");
            return;
        }
        OpenFolder(dir, "OpenCaptureFolder");
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
        SetVisibilityBool("analytics",  factory.IsToolVisible("analytics"));
        SetVisibilityBool("active-spells", factory.IsToolVisible("active-spells"));
        SetVisibilityBool("time-tracker", factory.IsToolVisible("time-tracker"));
        SetVisibilityBool("inventory-view", factory.IsToolVisible("inventory-view"));
        SetVisibilityBool("logons",    factory.IsToolVisible("logons"));
        SetVisibilityBool("talk",      factory.IsToolVisible("talk"));
        SetVisibilityBool("whispers",  factory.IsToolVisible("whispers"));
        SetVisibilityBool("thoughts",  factory.IsToolVisible("thoughts"));
        SetVisibilityBool("combat",    factory.IsToolVisible("combat"));
        SetVisibilityBool("familiar",  factory.IsToolVisible("familiar"));
        SetVisibilityBool("death",     factory.IsToolVisible("death"));
        SetVisibilityBool("assess",    factory.IsToolVisible("assess"));
        SetVisibilityBool("atmospherics", factory.IsToolVisible("atmospherics"));
        SetVisibilityBool("log",       factory.IsToolVisible("log"));
        SetVisibilityBool("itemlog",   factory.IsToolVisible("itemlog"));
        SetVisibilityBool("scripts",   factory.IsToolVisible("scripts"));
        SetVisibilityBool("scene",     factory.IsToolVisible("scene"));
        SetVisibilityBool("mobs",      factory.IsToolVisible("mobs"));
        SetVisibilityBool("players",   factory.IsToolVisible("players"));
        SetVisibilityBool("raw-xml",   factory.IsToolVisible("raw-xml"));
        SetVisibilityBool("injuries",  factory.IsToolVisible("injuries"));
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
            "experience", "analytics", "active spells", "time tracker", "inventory view", "main", "game", "game-text", "room", "vitals",
            "backpack", "mapper", "scripts", "scene",
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
        // A reserved name never spawns a duplicate plugin panel, but the text
        // must still land somewhere (Genie 4 never ate a directed echo):
        // stream names append to their buffers, everything else — main/game
        // plus the non-text panels (mapper, vitals, experience, …) — falls
        // back to the main game window, keeping the echo colour.
        core.EchoToWindow += (text, window, color) =>
        {
            var buf = window?.Trim().ToLowerInvariant() switch
            {
                "logons"       => StreamTabs.Logons,
                "talk"         => StreamTabs.Talk,
                "whispers"     => StreamTabs.Whispers,
                "thoughts"     => StreamTabs.Thoughts,
                "combat"       => StreamTabs.Combat,
                "familiar"     => StreamTabs.Familiar,
                "death"        => StreamTabs.Death,
                "assess"       => StreamTabs.Assess,
                "atmospherics" => StreamTabs.Atmospherics,
                "log"          => StreamTabs.Log,
                "itemlog"      => StreamTabs.ItemLog,
                _              => null,
            };
            if (buf is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => buf.Add(text));
                return;
            }
            if (IsReservedWindow(window))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (GameText.DisplaySettings?.ShowEchoText == false) return;
                    GameText.AddEcho(text, color, mono: false);
                });
                return;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DockFactory is GenieDockFactory f)
                    f.GetOrCreatePluginWindow(window!, show: false).AppendLine(text);
            });
        };

        // #link [>window] {text} {command} → a clickable menu line. Clicking
        // runs {command} through the same OnLinkClicked → ProcessInput path the
        // game's own <d cmd> links use. A custom >window renders the link INTO
        // that window (next to its #echo text) so a mm_train-style menu is
        // self-contained; no/reserved/log target falls back to Main. (Stream
        // buffers — log/itemlog — can't render links, so those go to Main too.)
        core.EchoLinkLine += (text, command, window) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var w = window?.Trim();
                if (!string.IsNullOrEmpty(w) &&
                    !w.Equals("main", StringComparison.OrdinalIgnoreCase) &&
                    !w.Equals("game", StringComparison.OrdinalIgnoreCase) &&
                    w.ToLowerInvariant() is not ("log" or "itemlog") &&
                    !IsReservedWindow(w) &&
                    DockFactory is GenieDockFactory f)
                {
                    f.GetOrCreatePluginWindow(w, show: false).AppendLink(text, command);
                    return;
                }
                GameText.AddLink(text, command);
            });

        // #clear [>window] → empty a window. No target clears Main; a custom
        // >window clears that plugin/menu window (mm_train redraws its menu with
        // "#clear >Menu" before rebuilding). Reserved built-in panels are left
        // alone; log/itemlog clear their line buffers.
        core.ClearWindow += window =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var w = window?.Trim();
                if (string.IsNullOrEmpty(w) ||
                    w.Equals("main", StringComparison.OrdinalIgnoreCase) ||
                    w.Equals("game", StringComparison.OrdinalIgnoreCase))
                { GameText.Clear(); return; }

                switch (w.ToLowerInvariant())
                {
                    case "log":     StreamTabs.Log.Lines.Clear();     return;
                    case "itemlog": StreamTabs.ItemLog.Lines.Clear(); return;
                }
                if (IsReservedWindow(w)) return;
                if (DockFactory is GenieDockFactory f)
                    f.GetOrCreatePluginWindow(w, show: false).Clear();
            });

        // #window <add|open|show|close|hide|remove|clear> "name" → named-window
        // lifecycle for menu scripts (mm_train adds+opens a window, writes to it,
        // then removes it). add/open/show create + reveal the panel; close/hide/
        // remove hide it; clear empties it. Reserved built-in windows are left
        // alone so a stray #window can't disturb Game/Room/Combat/etc.
        core.WindowCommandRequested += (sub, window) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var w = window?.Trim();
                if (string.IsNullOrEmpty(w) || IsReservedWindow(w) ||
                    DockFactory is not GenieDockFactory f)
                    return;
                switch (sub.Trim().ToLowerInvariant())
                {
                    case "add":
                    case "open":
                    case "show":
                        f.GetOrCreatePluginWindow(w, show: true);
                        break;
                    case "clear":
                        f.GetOrCreatePluginWindow(w, show: false).Clear();
                        break;
                    case "close":
                    case "hide":
                    case "remove":
                        f.SetToolVisibility(GenieDockFactory.PluginWindowId(w), false);
                        break;
                }
            });

        // #comment <window> <text> (#179): annotate a panel's title bar, e.g.
        // `#comment Room 69. 120` → "Room (69. 120)". Blank text clears it.
        core.WindowCommentRequested += (window, comment) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DockFactory is GenieDockFactory f)
                    f.SetWindowComment(window?.Trim() ?? "", comment ?? "");
            });
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
    ///
    /// <para>The path comes from <see cref="Genie.Core.Config.GenieConfig.ProfileDirFor"/>
    /// so it is the SAME <c>Profiles/{Char}-{Acct}/</c> directory Core switches
    /// to at connect (<c>ApplyCharacterProfile</c>) and loads <c>*.cfg</c> rule
    /// files from — and the same one the wiki documents. An earlier scheme used
    /// <c>Config/Profiles/{profile-guid}/</c>, which Core never read: a Genie 4
    /// import saved "this character only" landed in the GUID dir and silently
    /// vanished on restart. Any existing GUID dir is migrated on first resolve.</para>
    ///
    /// <para>A profile with no character/account identity resolves to the shared
    /// <c>Config/</c> dir — again matching where Core actually loads from for
    /// such a session.</para>
    /// </summary>
    public string GetProfileConfigDir(ConnectionProfile? profile)
    {
        if (profile is null) return _configDir;

        // Honor a per-profile data root: when the profile points at its own
        // folder, everything lives under it — matching Core, which repoints
        // its whole data root there (UseExplicitRoot). Empty = default root.
        var dataRoot = profile.DataDirectory is { Length: > 0 } dd
            ? Path.GetFullPath(dd)
            : Path.GetDirectoryName(_configDir)!;

        var dir = Genie.Core.Config.GenieConfig.ProfileDirFor(
            dataRoot, profile.CharacterName, profile.AccountName);
        Directory.CreateDirectory(dir);
        MigrateLegacyGuidProfileDir(dataRoot, profile, dir);
        return dir;
    }

    /// <summary>
    /// One-time migration from the pre-unification per-profile scheme
    /// (<c>Config/Profiles/{profile-guid}/</c>) to the per-character dir Core
    /// reads (<c>Profiles/{Char}-{Acct}/</c>). Moves each file/subfolder that
    /// doesn't already exist at the destination — never overwrites — then
    /// removes the GUID dir if that emptied it. Anything that can't move stays
    /// put; best-effort like the Config\Maps → Maps migration above.
    /// </summary>
    private static void MigrateLegacyGuidProfileDir(string dataRoot, ConnectionProfile profile, string newDir)
    {
        try
        {
            var legacy = Path.Combine(dataRoot, "Config", "Profiles", profile.Id.ToString("N"));
            if (!Directory.Exists(legacy)) return;
            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(newDir),
                              StringComparison.OrdinalIgnoreCase)) return;

            MoveContentsIfAbsent(legacy, newDir);

            // Clean up the GUID dir (and its Profiles parent) when emptied so
            // the stale scheme doesn't linger and confuse folder archaeology.
            if (!Directory.EnumerateFileSystemEntries(legacy).Any())
            {
                Directory.Delete(legacy);
                var parent = Path.GetDirectoryName(legacy)!;
                if (!Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
        }
        catch { /* best-effort — a failed migration must not block startup/connect */ }
    }

    /// <summary>Recursively move everything under <paramref name="from"/> into
    /// <paramref name="to"/>, skipping entries that already exist at the
    /// destination (existing per-character data always wins over legacy).</summary>
    private static void MoveContentsIfAbsent(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            if (!File.Exists(dest))
                try { File.Move(file, dest); } catch { /* leave it in the legacy dir */ }
        }
        foreach (var sub in Directory.GetDirectories(from))
        {
            MoveContentsIfAbsent(sub, Path.Combine(to, Path.GetFileName(sub)));
            try
            {
                if (!Directory.EnumerateFileSystemEntries(sub).Any())
                    Directory.Delete(sub);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Persist the per-window settings (font / colour / timestamp / name-list /
    /// title) to <c>windows.json</c> in the connected profile's config dir — the
    /// same target the Configuration → Layout tab writes. Called when a window
    /// right-click menu toggles Time Stamp or Name List Only so the choice
    /// survives a restart. Best-effort: a write failure must not disrupt play.
    /// </summary>
    public void SaveWindowSettings()
    {
        try
        {
            var dir = GetProfileConfigDir(ConnectedProfile);
            new PersistenceService()
                .SaveWindowSettings(Path.Combine(dir, "windows.json"), WindowSettings);
        }
        catch { /* best-effort — never block gameplay on a settings write */ }
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
    /// Saved-layout names offered by the Connect dialog's startup-Layout
    /// picker: the global store's layouts plus (when a profile is selected)
    /// that profile's own store, de-duplicated case-insensitively. The profile
    /// store is resolved fresh here — the dialog can browse profiles we're not
    /// connected to, so <see cref="_profileLayouts"/> (the CONNECTED scope)
    /// can't be used.
    /// </summary>
    public IReadOnlyList<string> ListLayoutNamesFor(ConnectionProfile? profile)
    {
        var names = new List<string>(_globalLayouts.List());
        if (profile is not null)
        {
            try
            {
                var store = new Settings.LayoutStore(
                    Path.Combine(GetProfileConfigDir(profile), "Layouts"));
                foreach (var n in store.List())
                    if (!names.Contains(n, StringComparer.OrdinalIgnoreCase))
                        names.Add(n);
            }
            catch { /* unreadable profile dir — offer globals only */ }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Replay every saved rule file from the connected profile's config dir
    /// (or the global dir for an ad-hoc connection) into the live engines so
    /// anything configured offline via the Configuration dialog is active
    /// from the moment the connection comes up.
    /// </summary>
    private void LoadSavedConfiguration(GenieCore core)
    {
        var p          = new PersistenceService();
        var profileDir = GetProfileConfigDir(ConnectedProfile);
        var globalDir  = _configDir;

        // Resolve a rule file with profile-over-global precedence: the connected
        // profile's own copy wins when present, otherwise fall back to the shared
        // global Config dir. This lets per-profile configs override the shared
        // set while legacy / pre-per-profile configs (e.g. imported Genie 4 or
        // earlier-prototype files that live in the global Config dir) still load
        // for a profile that hasn't customised that rule type yet. Returns null
        // when neither location has the file. For a profile-less (ad-hoc)
        // connection profileDir == globalDir, so this is a plain "load if present".
        string? Pick(string fileName)
        {
            var profilePath = Path.Combine(profileDir, fileName);
            if (File.Exists(profilePath)) return profilePath;
            var globalPath = Path.Combine(globalDir, fileName);
            return File.Exists(globalPath) ? globalPath : null;
        }

        // Classes first so Ensure() calls from the rule loaders below don't
        // clobber persisted active/inactive state (matches Genie5.Kzin ordering).
        SafeLoad(Pick("classes.json"), path =>
        {
            foreach (var m in p.LoadClasses(path))
                core.Classes.Set(m.Name, m.IsActive);
        });

        SafeLoad(Pick("highlights.json"), path =>
        {
            foreach (var m in p.LoadHighlights(path))
            {
                core.Highlights.RemoveRule(m.Pattern);
                core.Highlights.AddRule(
                    m.Pattern, m.ForegroundColor, m.BackgroundColor,
                    Enum.TryParse<HighlightMatchType>(m.MatchType, out var mt) ? mt : HighlightMatchType.String,
                    m.CaseSensitive, m.IsEnabled, m.ClassName, m.SoundFile, m.Speak, m.Windows);
            }
        });

        SafeLoad(Pick("names.json"), path =>
        {
            foreach (var m in p.LoadNames(path))
                core.NameHighlights.Add(m.Name, m.ForegroundColor, m.BackgroundColor);   // Add upserts (#154/#148)
        });

        SafeLoad(Pick("presets.json"), path =>
        {
            foreach (var m in p.LoadPresets(path))   // #149 — override the seeded default for each saved token
                core.Presets.Apply(new Genie.Core.Presets.PresetRule
                {
                    Id = m.Id, ForegroundColor = m.ForegroundColor,
                    BackgroundColor = m.BackgroundColor, HighlightLine = m.HighlightLine,
                });
        });

        SafeLoad(Pick("triggers.json"), path =>
        {
            foreach (var m in p.LoadTriggers(path))
            {
                core.Triggers.RemoveTrigger(m.Pattern);
                core.Triggers.AddTrigger(m.Pattern, m.Action, m.CaseSensitive, m.IsEnabled, m.ClassName,
                                         m.SoundFile, m.Speak, m.Eval, m.MatchAll);
            }
        });

        SafeLoad(Pick("substitutes.json"), path =>
        {
            foreach (var m in p.LoadSubstitutes(path))
            {
                core.Substitutes.RemoveRule(m.Pattern);
                core.Substitutes.AddRule(m.Pattern, m.Replacement, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(Pick("gags.json"), path =>
        {
            foreach (var m in p.LoadGags(path))
            {
                core.Gags.RemoveRule(m.Pattern);
                core.Gags.AddRule(m.Pattern, m.CaseSensitive, m.IsEnabled, m.ClassName);
            }
        });

        SafeLoad(Pick("aliases.json"), path =>
        {
            foreach (var m in p.LoadAliases(path))
            {
                core.Aliases.RemoveAlias(m.Name);
                core.Aliases.AddAlias(m.Name, m.Expansion, m.IsEnabled);
            }
        });

        var macrosPath = Pick("macros.json");
        if (macrosPath is not null)
        {
            SafeLoad(macrosPath, path =>
            {
                foreach (var m in p.LoadMacros(path))
                    core.Macros.Add(m.Key, m.Action);
            });

            // #140 migration: profiles seeded before the numpad OPERATOR
            // hotkeys existed get just those four filled in — but only when
            // NONE of them are bound. A profile with even one operator
            // binding has seen the feature, so a deliberately-deleted key
            // stays deleted; rebinds always win because the seeder never
            // touches a bound key.
            // Save back to the file we loaded (profile OR global) so the
            // migration doesn't fork a profile-local copy of global macros.
            if (SeedOperatorHotkeys(core.Macros))
                try { p.SaveMacros(macrosPath, core.Macros.Rules); } catch { /* best-effort */ }
        }
        else
        {
            // First run for this profile (and no global macros either): seed
            // Genie 4's classic numpad movement pad so 10-key travel works out
            // of the box. Persisted to the profile dir so it appears in the
            // Macros panel and the user can edit/remove any of it freely.
            SeedDefaultMovementMacros(core.Macros);
            try { p.SaveMacros(Path.Combine(profileDir, "macros.json"), core.Macros.Rules); } catch { /* best-effort seed */ }
        }

        SafeLoad(Pick("variables.json"), path =>
        {
            foreach (var m in p.LoadVariables(path))
                core.Variables.Store.Set(m.Name, m.Value);
        });

        SafeLoad(Pick("windows.json"), path =>
        {
            foreach (var m in p.LoadWindowSettings(path))
                WindowSettings.Apply(m);

            // The dock (StreamTools + their right-click window menus) was built
            // in the constructor, before this connect-time load, so each menu
            // cached its Show-in-Main / Time Stamp / Name List Only / Word Wrap
            // checkmark from the build-time DEFAULT. Apply() mutates the fields
            // silently (no Changed), leaving those checkmarks stale — e.g. a
            // persisted "Show in Main = off" leaves the menu stuck showing "on",
            // disagreeing with the Layout tab which reads the live value. Fire
            // Changed on every registered window so the menus (and each
            // StreamTool's ApplySettings) resync to the loaded values. Marshal to
            // the UI thread — the subscribers touch bound properties, and this
            // load can run off the UI thread on the connect path. Mirrors the
            // Display-change notify loop in the constructor.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var s in WindowSettings.All.Values)
                    s.NotifyChanged();
            });
        });
    }

    /// <summary>Run a load callback against <paramref name="path"/> when it's
    /// non-null and present, swallowing exceptions (corrupt JSON shouldn't block
    /// connect). Pair with the profile-over-global path resolver in the caller.</summary>
    private static void SafeLoad(string? path, Action<string> load)
    {
        if (path is null || !File.Exists(path)) return;
        try { load(path); } catch { /* corrupt JSON shouldn't block connect */ }
    }

    /// <summary>
    /// Genie 4's classic numpad ("10-key") movement pad. Seeded on first run
    /// for a profile so directional travel works out of the box; the user can
    /// rebind or delete any of these from the Macros panel. Requires NumLock
    /// on (so the OS reports NumPadN rather than the navigation keys).
    /// <code>
    ///   / assess  * health  - fatigue  + look
    ///   7 nw      8 n       9 ne
    ///   4 w       5 out     6 e
    ///   1 sw      2 s       3 se
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
        SeedOperatorHotkeys(macros);
    }

    /// <summary>
    /// Genie 3/4's non-movement 10-key hotkeys (public #140): numpad
    /// <c>/ * - +</c> → <c>assess / health / fatigue / look</c>. Seeded only
    /// when NONE of the four is currently bound — first run gets them via
    /// <see cref="SeedDefaultMovementMacros"/>, pre-#140 profiles get them
    /// once via the connect-time migration, and a user who deleted them all
    /// stays clean after re-save. Returns true when anything was added.
    /// </summary>
    private static bool SeedOperatorHotkeys(Genie.Core.Macros.MacroEngine macros)
    {
        var keys = new[] { "num/", "num*", "num-", "num+" };
        if (keys.Any(k => macros.Get(k) is not null)) return false;

        macros.Add("num/", "assess");
        macros.Add("num*", "health");
        macros.Add("num-", "fatigue");
        macros.Add("num+", "look");
        return true;
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
            case "analytics": ForceSet(visible, v => AnalyticsVisible = v, () => AnalyticsVisible); break;
            case "active-spells": ForceSet(visible, v => ActiveSpellsVisible = v, () => ActiveSpellsVisible); break;
            case "time-tracker": ForceSet(visible, v => TimeTrackerVisible = v, () => TimeTrackerVisible); break;
            case "inventory-view": ForceSet(visible, v => InventoryViewVisible = v, () => InventoryViewVisible); break;
            case "logons":    ForceSet(visible, v => LogonsVisible   = v, () => LogonsVisible);   break;
            case "talk":      ForceSet(visible, v => TalkVisible     = v, () => TalkVisible);     break;
            case "whispers":  ForceSet(visible, v => WhispersVisible = v, () => WhispersVisible); break;
            case "thoughts":  ForceSet(visible, v => ThoughtsVisible = v, () => ThoughtsVisible); break;
            case "combat":    ForceSet(visible, v => CombatVisible   = v, () => CombatVisible);   break;
            case "familiar":  ForceSet(visible, v => FamiliarVisible = v, () => FamiliarVisible); break;
            case "death":     ForceSet(visible, v => DeathVisible    = v, () => DeathVisible);    break;
            case "assess":    ForceSet(visible, v => AssessVisible   = v, () => AssessVisible);   break;
            case "atmospherics": ForceSet(visible, v => AtmosphericsVisible = v, () => AtmosphericsVisible); break;
            case "log":       ForceSet(visible, v => LogVisible      = v, () => LogVisible);      break;
            case "itemlog":   ForceSet(visible, v => ItemLogVisible  = v, () => ItemLogVisible);  break;
            case "scripts":   ForceSet(visible, v => ScriptsVisible  = v, () => ScriptsVisible);  break;
            case "scene":     ForceSet(visible, v => SceneVisible    = v, () => SceneVisible);    break;
            case "mobs":      ForceSet(visible, v => MobsVisible     = v, () => MobsVisible);     break;
            case "players":   ForceSet(visible, v => PlayersVisible  = v, () => PlayersVisible);  break;
            case "raw-xml":   ForceSet(visible, v => RawXmlVisible   = v, () => RawXmlVisible);   break;
            case "injuries":  ForceSet(visible, v => InjuriesVisible = v, () => InjuriesVisible); break;
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

    /// <summary>
    /// Show a connection/login failure reason in the Game window so it's visible
    /// and persistent — not just a transient title-bar "Error" or a dialog status
    /// that vanishes on close. Used by the connect path (the <c>Error</c>
    /// ConnectionEvent) and by the Connect dialog's character-fetch failure, so
    /// the user always sees *why* a login failed regardless of how they tried.
    /// </summary>
    public void ReportConnectionFailure(string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            GameText.AddSystemLine($"✖ Connection failed: {detail}");
    }

    /// <summary>
    /// On a successful connect, tell the user which login transport was used and
    /// set the status-bar padlock. TLS = encrypted; plaintext means the TLS
    /// attempt fell back to 7900 (password obfuscated, not encrypted) — flagged
    /// more prominently because it's a security downgrade worth noticing. Non-SGE
    /// modes (Lich/DevReplay) report no transport, so no indicator is shown.
    /// </summary>
    private void ReportConnectionTransport(string? transport)
    {
        if (string.Equals(transport, "TLS", StringComparison.OrdinalIgnoreCase))
        {
            ConnectionSecurity    = "🔒";
            ConnectionSecurityTip = "Login sent over TLS (encrypted).";
            GameText.AddSystemLine("🔒 Connected over TLS (encrypted).");
        }
        else if (string.Equals(transport, "plaintext", StringComparison.OrdinalIgnoreCase))
        {
            ConnectionSecurity    = "🔓";
            ConnectionSecurityTip = "Login sent over plaintext (port 7900) — TLS was unavailable. Your password was obfuscated, not encrypted.";
            GameText.AddSystemLine("🔓 Connected over plaintext (7900) — TLS (7910) was unavailable; your login was obfuscated, not encrypted.");
        }
        else
        {
            ConnectionSecurity    = "";
            ConnectionSecurityTip = "";
        }
    }

    /// <summary>Connect (or reconnect) the given config/profile. Builds the one
    /// persistent <see cref="GenieCore"/> and its once-per-app App wiring on first
    /// use (<see cref="EnsureCoreBuilt"/> → <see cref="WireCore"/>), then runs a
    /// single per-connect pass and dials. The core, engines, a running <c>.cmd</c>
    /// and all rule/script state survive disconnect→reconnect (issue #88 / #46
    /// Phase 3) — we no longer dispose-and-rebuild the core per connect.</summary>
    private async Task ConnectAsync(ConnectionConfig cfg, ConnectionProfile? profile, bool isAutoReconnect = false)
    {
        EnsureCoreBuilt(profile);
        if (_core is null) return;   // ctor failure (already logged) — nothing to connect
        WarnIfDataRootMismatch(profile);

        // Auto-reconnect owns its own backoff ladder (ScheduleReconnect), so the
        // core does a SINGLE establish attempt — otherwise GameConnection's
        // internal retry loop would compound with the VM-level one.
        var coreCfg          = isAutoReconnect ? cfg with { MaxReconnectAttempts = 0 } : cfg;
        ConnectedProfile     = profile;   // null if user typed credentials without picking a saved profile
        LastConnectionConfig = cfg;       // original cadence preserved for the Connect dialog + manual reconnects
        CharacterGuild       = "";        // cleared until this session's `info` reveals the guild

        // Scope the per-profile layout store to this profile (global-only for
        // bare-credential connects), refresh the Load menu, and auto-apply the
        // profile's default layout (falling back to the global default, then
        // the built-in layout if neither resolves).
        SetProfileLayoutScope(profile);
        RefreshSavedLayoutList();
        ApplyDefaultLayoutForConnect(profile);

        // Clear-then-load the rule files ONLY on a character change. The engines
        // persist now (the core isn't rebuilt per connect), so without this a
        // second character in the same session would inherit the first's rules.
        // A same-character reconnect (auto-reconnect / #reconnect) keeps reloadRules
        // false so runtime-added rules and a running script's state survive — the
        // whole point of the persistent core (issue #88 / #46 Phase 3). The same
        // flag gates the core's own .cfg load so both rule formats stay in lockstep.
        var ruleKey      = $"{cfg.CharacterName}\0{cfg.AccountName}";
        var alreadyLoaded = string.Equals(ruleKey, _loadedRuleKey, StringComparison.OrdinalIgnoreCase);
        // CLEAR the previous character's rules ONLY on a genuine A→B switch; the
        // first connect from offline must NOT clear (a logon script's #var/#class
        // setup, or a same-char reconnect's runtime rules, must survive). LOAD the
        // profile rules whenever they differ (first connect loads them ON TOP).
        var charSwitch   = _loadedRuleKey.Length > 0 && !alreadyLoaded;
        var reloadRules  = !alreadyLoaded;
        if (reloadRules)
        {
            if (charSwitch) _core!.ResetRuleEngines();   // drop the PREVIOUS character's rules — never the offline/first-connect setup
            LoadSavedConfiguration(_core);    // replay this character's saved .json rules
            _loadedRuleKey = ruleKey;
        }

        // Re-arm the per-session overlay/metrics that DisconnectAsync detaches.
        UserHighlights.Metrics = _core!.Metrics;   // time the render-path highlight pass
        Perf.Attach(_core.Metrics);

        // AutoLog (Genie 4): begin the rendered-text session log if enabled. The
        // notice is emitted BEFORE Start subscribes, so it isn't itself logged.
        // The identity is remembered so a mid-session File ▸ Auto Log enable
        // (SyncAutoLogFromConfig) names the file for THIS session.
        _autoLogCharacter = cfg.CharacterName;
        _autoLogGame      = cfg.GameCode;
        if (_core.Config.AutoLog)
        {
            GameText.AddSystemLine("[autolog] session logging on → Logs folder. Turn off with File ▸ Auto Log or #config autolog false.");
            AutoLogger.Start(GameText, cfg.CharacterName, cfg.GameCode);
        }

        // Honour ShowLinks + conndebug for this connect (both can change between
        // sessions via #config).
        Highlighting.DefaultHighlights.LinksEnabled = _core.Config.ShowLinks;
        Highlighting.DefaultHighlights.MonsterBoldEnabled = _core.Config.MonsterBold;   // #131
        _core.ConnectionVerboseDiag = _core.Config.ConnDebug;

        // Genie 4 #lc parity: optionally start Lich for the user before connecting
        // through it. Opt-in (Config.LichAutoLaunch) and idempotent — if Lich is
        // already listening on the proxy port this attaches without launching, so
        // a manually-started Lich still works. A hard launch failure aborts the
        // connect (a doomed LichProxy connect would only fail with a vaguer error).
        // lichargs may use {character}/{port} placeholders filled from this connect.
        // Genie-started Lich is owned (PID tracked) and stopped on disconnect /
        // character change; an AlreadyRunning attach is never claimed as owned.
        if (coreCfg.Mode == ConnectionMode.LichProxy && _core.Config.LichAutoLaunch)
        {
            if (!Genie.Core.Connection.LichLauncher.TryExpandArguments(
                    _core.Config.LichArguments,
                    coreCfg.CharacterName,
                    coreCfg.LichProxyPort,
                    out var lichArgs,
                    out var expandError))
            {
                GameText.AddSystemLine(expandError);
                return;
            }

            var launchKey = OwnedLichLaunchKey(coreCfg.CharacterName, coreCfg.LichProxyPort);
            // Character/port switch: stop the previous Genie-started Lich so the
            // new --login can bind the proxy port (AlreadyRunning would otherwise
            // leave us attached to the wrong character).
            if (_ownedLichProcessId is not null
                && !string.Equals(_ownedLichLaunchKey, launchKey, StringComparison.OrdinalIgnoreCase))
                StopOwnedLich();

            // Same character/port reconnect while we still own a live Lich: attach
            // only. Calling EnsureRunningAsync here could launch a *second* Lich if
            // the FE port is briefly not listening (Lich recreates the listener
            // after an FE drop) and then overwrite ownership with the new PID.
            if (_ownedLichProcessId is int ownedPid
                && string.Equals(_ownedLichLaunchKey, launchKey, StringComparison.OrdinalIgnoreCase)
                && Genie.Core.Connection.LichLauncher.TryIsProcessAlive(ownedPid))
            {
                GameText.AddSystemLine(
                    $"[lich] reattaching to owned process {ownedPid} on {coreCfg.LichProxyHost}:{coreCfg.LichProxyPort}.");
            }
            else
            {
                // Owned PID gone (crash / external kill) — drop the stale claim so a
                // fresh Launched outcome can take ownership again.
                if (_ownedLichProcessId is int stalePid
                    && !Genie.Core.Connection.LichLauncher.TryIsProcessAlive(stalePid))
                {
                    _ownedLichProcessId = null;
                    _ownedLichLaunchKey = null;
                }

                // EnsureRunningAsync uses ConfigureAwait(false); marshal progress onto
                // the UI thread so GameText.Lines isn't mutated during / across a
                // CollectionChanged (character change races the "disconnected" line).
                var lich = await Genie.Core.Connection.LichLauncher.EnsureRunningAsync(
                    coreCfg.LichProxyHost, coreCfg.LichProxyPort,
                    _core.Config.LichRubyPath, _core.Config.LichPath, lichArgs,
                    _core.Config.LichStartPause,
                    progress: msg => Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => GameText.AddSystemLine(msg)));
                GameText.AddSystemLine(lich.Message);
                if (lich.Outcome == Genie.Core.Connection.LichLaunchOutcome.Failed)
                    return;   // don't attempt a connect we know can't reach Lich

                // Only claim ownership on a fresh launch, and never overwrite an
                // existing owned PID (AlreadyRunning must not become "ours").
                if (lich.Outcome == Genie.Core.Connection.LichLaunchOutcome.Launched
                    && lich.ProcessId is int pid
                    && _ownedLichProcessId is null)
                {
                    _ownedLichProcessId = pid;
                    _ownedLichLaunchKey = launchKey;
                }
            }
        }

        await _core.ConnectAsync(coreCfg, reloadRules: reloadRules, clearPerCharacter: charSwitch);
    }

    /// <summary>Stable key for the owned auto-launched Lich (character + proxy port).</summary>
    private static string OwnedLichLaunchKey(string? characterName, int port) =>
        $"{characterName ?? ""}\0{port}";

    /// <summary>Kill a Lich process Genie auto-launched. No-op when we only attached
    /// to an already-running Lich (<see cref="_ownedLichProcessId"/> null).</summary>
    private void StopOwnedLich()
    {
        if (_ownedLichProcessId is not int pid) return;
        _ownedLichProcessId = null;
        _ownedLichLaunchKey = null;
        Genie.Core.Connection.LichLauncher.TryStop(pid, out var message);
        if (!string.IsNullOrEmpty(message))
            GameText.AddSystemLine(message);
    }

    /// <summary>Build the one persistent core + run all once-per-app App wiring the
    /// first time it's needed (a connect, or the first command typed while offline).
    /// The session data root is fixed here: from <paramref name="rootProfile"/> if
    /// it specifies one, else the default discovered (AppData/portable) location.</summary>
    private void EnsureCoreBuilt(ConnectionProfile? rootProfile, bool eagerLoadOfflineRules = false)
    {
        if (_core is not null) return;

        _sessionDataRoot = rootProfile is { DataDirectory.Length: > 0 } ? rootProfile.DataDirectory : "";
        _core = new GenieCore(
            dataDirectoryOverride: string.IsNullOrEmpty(_sessionDataRoot) ? null : _sessionDataRoot,
            aiConfig:              null,
            loggerFactory:         null);

        // A profile Data Directory override repoints Core's whole data root
        // away from the session default the startup [data] line announced —
        // say so, or the two silently disagree (#138).
        if (!string.IsNullOrEmpty(_sessionDataRoot))
            GameText.AddSystemLine(
                $"[data] profile override: scripts, rules and layouts load from {Path.GetFullPath(_sessionDataRoot)}");

        WireCore();

        // Offline-before-connect (#88): when the core is built outside a connect —
        // e.g. the user types a command or runs a logon script while disconnected —
        // eagerly load the global/default saved rules so aliases/triggers/classes/
        // variables are available immediately, instead of running against empty
        // engines. ConnectedProfile is null here, so LoadSavedConfiguration resolves
        // the global Config dir. We deliberately leave _loadedRuleKey empty: the
        // first connect's clear-then-load (ConnectAsync) then reconciles to the
        // connecting character's own profile rules. Skipped on the connect path,
        // where ConnectAsync loads the profile's rules itself.
        if (eagerLoadOfflineRules)
            LoadSavedConfiguration(_core);
    }

    /// <summary>A later connect to a profile whose data directory differs from the
    /// session's fixed root can't relocate everything live (the core is persistent),
    /// so warn that a restart is needed rather than silently using the wrong root.</summary>
    private void WarnIfDataRootMismatch(ConnectionProfile? profile)
    {
        var want = profile is { DataDirectory.Length: > 0 } ? Path.GetFullPath(profile.DataDirectory) : "";
        var have = string.IsNullOrEmpty(_sessionDataRoot) ? "" : Path.GetFullPath(_sessionDataRoot);
        if (!string.Equals(want, have, StringComparison.OrdinalIgnoreCase))
            GameText.AddSystemLine(
                "[data] this profile's data directory differs from the one in use this session — " +
                $"restart Genie to switch data directories. In use: {(have.Length == 0 ? "(default)" : have)}.");
    }

    /// <summary>Handle a <c>#tts</c> subcommand (install / use / voices / read /
    /// mute / priority / rate / volume / stop / status). Runs on the engine (UI) thread; the
    /// actual download runs off-thread and reports back via UI-marshaled system
    /// lines.</summary>
    private void HandleTtsCommand(string args)
    {
        if (_core is null) return;
        void Echo(string m) => GameText.AddSystemLine(m);

        var parts = (args ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string voiceDir = _core.Config.TtsVoiceDir;

        switch (sub)
        {
            case "install":
            {
                var voice = parts.Length > 1
                    ? Services.VoiceCatalog.Find(parts[1])
                    : Services.VoiceCatalog.Default;
                if (voice is null)
                {
                    Echo($"[tts] unknown voice '{parts[1]}'. See #tts voices.");
                    return;
                }
                _ = Task.Run(async () =>
                {
                    var ok = await _voiceInstaller.InstallAsync(
                        voice, voiceDir,
                        msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => GameText.AddSystemLine(msg)));
                    if (ok)
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            // Installing a voice signals intent to use it — make it active.
                            _core.Config.TtsVoice = voice.Id;
                            _tts?.Reset();
                            GameText.AddSystemLine($"[tts] active voice: {voice.DisplayName}.");
                        });
                });
                break;
            }

            case "use":
            case "select":
            {
                if (parts.Length < 2) { Echo("Usage: #tts use <voice>"); break; }
                var v = Services.VoiceCatalog.Find(parts[1]);
                string id = v?.Id ?? parts[1];   // allow a raw folder name too
                if (!Services.VoiceInstaller.IsInstalled(System.IO.Path.Combine(voiceDir, id)))
                {
                    Echo($"[tts] '{parts[1]}' isn't installed — try #tts install {parts[1]}, or #tts voices.");
                    break;
                }
                _core.Config.TtsVoice = id;
                _tts?.Reset();
                Echo($"[tts] now using {v?.DisplayName ?? id}. Persists when settings are saved.");
                break;
            }

            case "voices":
            case "list":
                Echo("[tts] voices (install with #tts install <name>):");
                foreach (var v in Services.VoiceCatalog.All)
                {
                    string path = System.IO.Path.Combine(voiceDir, v.Id);
                    bool inst = Services.VoiceInstaller.IsInstalled(path);
                    bool active = string.Equals(_core.Config.TtsVoice, v.Id, StringComparison.OrdinalIgnoreCase);
                    Echo($"  {v.Alias,-8} {v.DisplayName} (~{v.ApproxMb} MB)" +
                         $"{(inst ? "  [installed]" : "")}{(active ? "  [active]" : "")}");
                }
                break;

            case "read":
            {
                if (parts.Length < 2)
                {
                    Echo($"[tts] read-aloud is {(_core.Config.TtsRead ? "on" : "off")}; " +
                         $"streams: {_core.Config.TtsReadStreamsRaw}");
                    Echo("  #tts read on|off  |  #tts read <stream>  |  #tts mute <stream>");
                    break;
                }
                string arg = parts[1].ToLowerInvariant();
                if (arg is "on" or "off")
                {
                    _core.Config.TtsRead = arg == "on";
                    Echo($"[tts] read-aloud {arg}.");
                }
                else
                {
                    var set = SplitStreams(_core.Config.TtsReadStreamsRaw);
                    set.Add(arg);
                    _core.Config.TtsReadStreamsRaw = string.Join(",", set);
                    _core.Config.TtsRead = true;   // enabling a stream implies on
                    Echo($"[tts] reading: {_core.Config.TtsReadStreamsRaw}");
                }
                break;
            }

            case "mute":
            {
                if (parts.Length < 2) { Echo("Usage: #tts mute <stream>"); break; }
                var set = SplitStreams(_core.Config.TtsReadStreamsRaw);
                set.Remove(parts[1].ToLowerInvariant());
                _core.Config.TtsReadStreamsRaw = string.Join(",", set);
                Echo($"[tts] reading: {_core.Config.TtsReadStreamsRaw}");
                break;
            }

            case "priority":
            case "prio":
            {
                if (parts.Length < 2)
                {
                    var overrides = _core.Config.TtsStreamPriorityOverrides();
                    var streams = SplitStreams(_core.Config.TtsReadStreamsRaw);
                    foreach (var key in overrides.Keys) streams.Add(key);
                    Echo("[tts] stream priorities (high barges in, low yields):");
                    foreach (var s in streams)
                        Echo($"  {s,-14} {_core.Config.TtsUrgencyFor(s).ToString().ToLowerInvariant()}" +
                             (overrides.ContainsKey(s) ? "  [override]" : "  [default]"));
                    Echo("  #tts priority <stream> <low|normal|high>  |  #tts priority <stream> default");
                    break;
                }
                string stream = parts[1].ToLowerInvariant();
                if (parts.Length < 3)
                {
                    Echo($"[tts] {stream}: {_core.Config.TtsUrgencyFor(stream).ToString().ToLowerInvariant()}. " +
                         "Usage: #tts priority <stream> <low|normal|high|default>");
                    break;
                }
                string word = parts[2].ToLowerInvariant();
                var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in _core.Config.TtsStreamPriorityOverrides())
                    map[kv.Key] = kv.Value.ToString().ToLowerInvariant();
                if (word == "default")
                    map.Remove(stream);
                else if (word is "low" or "normal" or "high")
                    map[stream] = word;
                else
                {
                    Echo($"[tts] unknown priority '{parts[2]}' — use low, normal, high, or default.");
                    break;
                }
                _core.Config.TtsStreamPriorityRaw = string.Join(",", map.Select(kv => $"{kv.Key}:{kv.Value}"));
                Echo($"[tts] {stream}: {_core.Config.TtsUrgencyFor(stream).ToString().ToLowerInvariant()}" +
                     $"{(word == "default" ? " (default)" : "")}. Persists when settings are saved.");
                break;
            }

            case "stop":
            case "silence":
            case "shutup":
                _tts?.Stop();
                Echo("[tts] stopped.");
                break;

            case "rate":
            {
                if (parts.Length < 2)
                {
                    Echo($"[tts] rate: {FormatRate(_core.Config.TtsRate)} (0.5–3, 1 = normal). Usage: #tts rate <n>");
                    break;
                }
                // SetSetting owns the parse + clamp; garbage leaves the value as-is.
                _core.Config.SetSetting("ttsrate", parts[1], showException: false);
                Echo($"[tts] rate: {FormatRate(_core.Config.TtsRate)}. Applies from the next spoken line; persists when settings are saved.");
                break;
            }

            case "volume":
            case "vol":
            {
                if (parts.Length < 2)
                {
                    Echo($"[tts] volume: {_core.Config.TtsVolume}% (0–100). Usage: #tts volume <n>");
                    break;
                }
                _core.Config.SetSetting("ttsvolume", parts[1], showException: false);
                Echo($"[tts] volume: {_core.Config.TtsVolume}%. Applies from the next spoken line; persists when settings are saved.");
                break;
            }

            case "status":
            {
                Echo($"[tts] voice dir: {voiceDir}");
                int n = 0;
                foreach (var v in Services.VoiceCatalog.All)
                    if (Services.VoiceInstaller.IsInstalled(System.IO.Path.Combine(voiceDir, v.Id)))
                    { Echo($"  installed: {v.DisplayName}"); n++; }
                if (n == 0) Echo("  no voices installed — run #tts install");
                Echo($"  read-aloud: {(_core.Config.TtsRead ? "on" : "off")}; " +
                     $"streams: {_core.Config.TtsReadStreamsRaw}");
                if (!string.IsNullOrEmpty(_core.Config.TtsStreamPriorityRaw))
                    Echo($"  priorities: {_core.Config.TtsStreamPriorityRaw} (see #tts priority)");
                Echo($"  rate: {FormatRate(_core.Config.TtsRate)}; volume: {_core.Config.TtsVolume}%");
                break;
            }

            default:
                Echo("Usage: #tts install [voice] | use <voice> | voices | read [on|off|<stream>] | mute <stream> | priority [<stream> <low|normal|high|default>] | rate <n> | volume <n> | stop | status");
                break;
        }
    }

    /// <summary>One-time advisory the first time skill-history recording is
    /// active for this profile: what's recorded, where it lives, how to turn
    /// it off. Three-way: Keep enabled / Turn off (both persist
    /// <c>analyticsnoticeshown</c>) / Esc = decide later (asks again next
    /// connect). Recording is already on while the dialog shows — local-only
    /// data, so nothing leaves the machine either way.</summary>
    private void MaybeShowAnalyticsNotice()
    {
        if (_core?.SkillHistory is not { } hist) return;
        if (_core.Config.AnalyticsNoticeShown || !_core.Config.Analytics) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var owner = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d
                ? d.MainWindow : null;
            if (owner is null) return;

            var dlg = new Views.ConfirmDialog(
                "Skill history recording",
                "Genie now records your character's skill history locally, powering the new\n" +
                "Analytics window (Window → Analytics): XP/hour, skill-gain curves, and\n" +
                "session summaries.\n\n" +
                $"The data is your own character's skill table and nothing else. It is stored\n" +
                $"only on this computer, under:\n  {hist.Directory}\n" +
                "and is never uploaded.\n\n" +
                "Change this anytime: #config analytics off, or the Record toggle in the\n" +
                "Analytics panel.",
                "Keep enabled", "Turn off", "Decide later");
            var choice = await dlg.ShowDialog<bool?>(owner);
            if (choice is null) return;               // ask again next connect
            if (choice == false)
                _core.Config.SetSetting("analytics", "False", showException: false);
            _core.Config.SetSetting("analyticsnoticeshown", "True", showException: false);
            _core.Config.Save();
        });
    }

    /// <summary>Map a stream to its read-aloud urgency — user overrides from
    /// <c>#tts priority</c> first, else the classic defaults (whispers/death
    /// barge in; the chatty/background streams stay Low so they yield).
    /// The map lives in <see cref="Genie.Core.Config.GenieConfig.TtsUrgencyFor"/>;
    /// the enums share numeric values so the cast is exact.</summary>
    private Services.TtsPriority TtsPriorityFor(string stream) =>
        _core is null
            ? Services.TtsPriority.Normal
            : (Services.TtsPriority)(int)_core.Config.TtsUrgencyFor(stream);

    private static SortedSet<string> SplitStreams(string csv) =>
        new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(s => s.ToLowerInvariant()));

    /// <summary>Rate as typed/stored ("1", "1.25") — invariant dot decimal, no
    /// trailing zeros — so the echo matches what #config ttsrate round-trips.</summary>
    private static string FormatRate(double rate) =>
        rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>One-time wiring of the persistent core to the App: VM attaches,
    /// observable subscriptions, #-command handlers, link/sound handlers, plugin
    /// discovery, and the script tick pump. Runs exactly once per app session — the
    /// core and its relay observables persist across reconnect, so these
    /// subscriptions are taken once and never re-taken (re-taking would duplicate
    /// every handler). Per-connect work lives in <see cref="ConnectAsync"/>.</summary>
    private void WireCore()
    {
        if (_core is null) return;   // assigned by EnsureCoreBuilt immediately before this call

        // Mute checkbox ⇄ config. Seed from the freshly built core's config,
        // then track every later write — `#config muted`, settings.cfg loads at
        // connect, `#config reload` — all of which land via SetSetting and fire
        // ConfigChanged(Muted). SetSetting can run off the UI thread (scripts),
        // so marshal the property write.
        IsMuted = !_core.Config.PlaySounds;
        _core.Config.ConfigChanged += field =>
        {
            if (field == Genie.Core.Config.ConfigFieldUpdated.Muted)
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => IsMuted = !(_core?.Config.PlaySounds ?? true));
        };

        // Master-toggle checkboxes ⇄ config, same pattern as the mute checkbox:
        // seed from the core's config, then follow every later write —
        // `#config gags off`, settings.cfg loads, profile switches.
        SyncMasterTogglesFromConfig();
        _core.Config.ConfigChanged += field =>
        {
            if (field is Genie.Core.Config.ConfigFieldUpdated.MasterToggles
                      or Genie.Core.Config.ConfigFieldUpdated.ImagesEnabled)
                Avalonia.Threading.Dispatcher.UIThread.Post(SyncMasterTogglesFromConfig);
        };

        // Auto Log checkbox ⇄ config — and the live mid-session start/stop.
        // This also covers the settings.cfg load during connect: ConnectAsync
        // starts the logger from the pre-load config value, and if the loaded
        // file disagrees this handler corrects it right after.
        SyncAutoLogFromConfig();
        _core.Config.ConfigChanged += field =>
        {
            if (field == Genie.Core.Config.ConfigFieldUpdated.Autolog)
                Avalonia.Threading.Dispatcher.UIThread.Post(SyncAutoLogFromConfig);
        };

        // Align Input ⇄ config (`sizeinputtogame`), same seed-then-follow shape.
        SyncAlignInputFromConfig();
        _core.Config.ConfigChanged += field =>
        {
            if (field == Genie.Core.Config.ConfigFieldUpdated.SizeInputToGame)
                Avalonia.Threading.Dispatcher.UIThread.Post(SyncAlignInputFromConfig);
        };

        // Guild for the title bar — fires when the player runs `info`. DR has
        // no structured guild tag, so we parse it from the info output.
        _core.GameEvents.OfType<GuildEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => CharacterGuild = e.Guild);

        // Character name discovered mid-session (Lich attach ident, issue
        // #127). A Lich quick-connect carries no name, so ConnectionStatus was
        // built from an empty identity and the guild suffix read as the name.
        // Adopt the discovered name into the status line and the remembered
        // config (so Reconnect and the Connect dialog keep it) — but never
        // override a name the user supplied.
        _core.GameEvents.OfType<Genie.Core.Events.CharacterNameEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                if (string.IsNullOrWhiteSpace(e.Name)) return;
                if (!string.IsNullOrWhiteSpace(LastConnectionConfig?.CharacterName)) return;
                if (LastConnectionConfig is not null)
                    LastConnectionConfig = LastConnectionConfig with { CharacterName = e.Name };
                if (IsConnected)
                    ConnectionStatus = $"Connected — {Genie.Core.Profiles.CharacterIdentity.Format(e.Name, LastConnectionConfig?.AccountName ?? "")}";
            });

        // Report XML Gap: when the parser meets an element it doesn't consume
        // (UnknownTagEvent), offer a one-click, human-in-the-loop report. The
        // draft is redacted (policy gate G2) and only a browser prefill URL is
        // produced — nothing is posted automatically. One prompt per tag type
        // per session so a routine gap doesn't nag.
        _core.GameEvents.OfType<UnknownTagEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                if (string.IsNullOrWhiteSpace(e.TagName)) return;
                if (!_reportedGapTags.Add(e.TagName)) return;
                try
                {
                    var fate  = Genie.Core.Parser.DrXmlParser.ClassifyTag(e.TagName);
                    var draft = Genie.Core.Diagnostics.XmlGapReport.Build(
                        e.TagName, fate, e.RawXml ?? "", BuildXmlGapContext($"live element <{e.TagName}>"));
                    _pendingXmlGapUrl = draft.Url;
                    XmlGapNotice = $"Genie received an unrecognized game element: <{e.TagName}>. Help improve the parser — report it?";
                }
                catch (Exception ex) { ErrorLog.Log("XmlGapReport", ex); }
            });

        _core.ConnectionState.ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                IsConnected      = e.Kind == ConnectionEventKind.Connected;
                ConnectionStatus = e.Kind switch
                {
                    ConnectionEventKind.Connected    => $"Connected — {Genie.Core.Profiles.CharacterIdentity.Format(LastConnectionConfig?.CharacterName ?? "", LastConnectionConfig?.AccountName ?? "")}",
                    ConnectionEventKind.Error        => "Connection failed",
                    ConnectionEventKind.Reconnecting => $"Reconnecting (attempt {e.Attempt})…",
                    _                                 => e.Kind.ToString(),
                };

                // Surface the FULL reason in the Game window. A drop AFTER we were
                // connected (server idle-out, dead link, watchdog trip) is a
                // "Connection lost"; an Error before we ever connected is a failed
                // login — GameConnection carries the actionable detail either way
                // (bad password / PROBLEM 1-4 / TLS / timeout). Before #87 the
                // lost-link case printed nothing at all.
                if (e.Kind == ConnectionEventKind.Error)
                {
                    if (_wasConnected)
                        GameText.AddSystemLine($"⚠ Connection lost: {e.Message}");
                    else
                        ReportConnectionFailure(e.Message);
                    _dropWasError  = true;   // unexpected dead link → eligible to auto-reconnect
                    _lastDropReason = e.Message;   // shown in the #114 disconnect popup if the session ends
                }

                // On connect, announce the login transport (TLS vs plaintext),
                // drive the status-bar padlock, and reset reconnect bookkeeping.
                // Cleared on disconnect/error.
                if (e.Kind == ConnectionEventKind.Connected)
                {
                    ReportConnectionTransport(e.Message);
                    OnConnectionEstablished();
                    MaybeShowAnalyticsNotice();
                }
                else if (e.Kind is ConnectionEventKind.Disconnected or ConnectionEventKind.Error)
                {
                    ConnectionSecurity    = "";
                    ConnectionSecurityTip = "";
                }

                // Only an UNEXPECTED dead link auto-reconnects. The transport raises
                // Error (read failure / keepalive trip / activity watchdog) before
                // the paired Disconnected; a clean server-initiated close — DR
                // idle-kick, boot, server `exit`/`quit` — arrives as a bare
                // Disconnected with no Error. A deliberate server close must NOT
                // auto-reconnect (the user is idle/away; no resume-while-away).
                // Network drops still recover via the bounded ladder. Runs before
                // the session-ended notice below so _reconnectAt reflects this drop.
                if (_dropWasError && e.Kind is ConnectionEventKind.Disconnected or ConnectionEventKind.Error)
                    MaybeArmAutoReconnect();

                // Session has truly ended — a Disconnected with no reconnect armed
                // (clean close / manual disconnect / server exit / a dead link that
                // won't recover or whose ladder gave up). Genie 4 parity (#114):
                // always emit a timestamped "disconnected" marker in the Game
                // window, and — unless the user turned it off — pop a modal notice
                // so leaving the game is unmissable. Suppressed while reconnecting
                // (a future Connected will resume the session).
                if (e.Kind == ConnectionEventKind.Disconnected && _reconnectAt is null)
                {
                    GameText.AddSystemLine(WindowTimestamp.Prefix() + "disconnected");

                    // Final session end (no auto-reconnect): stop a Genie-started
                    // Lich. Idempotent with DisconnectAsync's StopOwnedLich. Skipped
                    // while reconnect is armed so a transient drop can reattach.
                    StopOwnedLich();

                    if (Display.ShowDisconnectPopup)
                    {
                        var body = string.IsNullOrWhiteSpace(_lastDropReason)
                            ? "You have been disconnected from the game."
                            : $"You have been disconnected from the game.\n\n{_lastDropReason}";
                        // Fire-and-forget: a modal notice handled by MainWindow. The
                        // empty error handler keeps a missing/closing handler (e.g.
                        // app shutdown) from surfacing as an unobserved exception.
                        ShowDisconnectNotice.Handle(body).Subscribe(_ => { }, _ => { });
                    }
                }

                // Close any in-flight analyst capture when the session ends, so
                // its meta sidecar is written rather than left dangling.
                if (e.Kind is ConnectionEventKind.Disconnected or ConnectionEventKind.Error
                    && IsCapturing)
                    StopCapture("disconnected");
            });

        // Expose the persistent engines to the renderer once (rule reload itself
        // is per-connect — LoadSavedConfiguration runs in ConnectAsync). The engines
        // persist, so the renderer holds them for the whole session.
        UserHighlights.Engine  = _core.Highlights;
        Highlighting.DefaultHighlights.PresetEngine = _core.Presets;      // preset colours (#19)
        Highlighting.DefaultHighlights.NameEngine   = _core.NameHighlights; // name colours (#154)

        // ── Performance / safety ──────────────────────────────────────────────
        // Push the user's current safety choices onto the engines (defaults ON).
        // The overlay attach + metrics handoff is per-connect (Perf.Detach() runs
        // on disconnect), so it lives in ConnectAsync; only the once-per-session
        // sinks are wired here.
        _core.Triggers.SafetyEnabled    = TriggersSafety;
        _core.Highlights.SafetyEnabled  = HighlightsSafety;
        _core.Substitutes.SafetyEnabled = SubstitutesSafety;
        _core.Gags.SafetyEnabled        = GagsSafety;
        // JS overlay: time the .js line-dispatch into the JavaScript stage, and
        // feed the running-.js list. Record() no-ops when the overlay is hidden.
        _core.Scripts.JsDispatchMsSink = ms =>
            _core.Metrics.Record(Genie.Core.Diagnostics.PipelineStage.JavaScript, ms);
        Perf.JsStatsProvider = () => _core.Scripts.JsRunningStats();

        // Wire <d cmd="..."> link clicks to the command pipeline so they
        // behave like the user typed the command. Mirror the ShowLinks
        // config gate so users can opt out of clickable styling entirely.
        // Pass the display text as echoOverride so the Game window shows
        // "get a tapered cutlass" (readable) instead of the raw cmd
        // "get #49489411 in #49489410" (item exist-IDs).
        Highlighting.DefaultHighlights.OnLinkClicked = (cmd, displayText) =>
            _core?.ProcessInput(cmd, echoOverride: BuildLinkEcho(cmd, displayText));
        // LinksEnabled is refreshed per connect in ConnectAsync (ShowLinks can
        // change between sessions via #config).

        // External URL hyperlinks (<a href='URL'>) — DR emits these in the
        // news/login resources block (Simucoin Store, Elanthipedia, etc.).
        // Hand them off to the OS shell so the user's default browser opens
        // the URL. UseShellExecute=true is required by .NET for URL strings
        // (the runtime won't launch them as raw filenames).
        Highlighting.DefaultHighlights.OnUrlClicked = async url =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            // WebLinkSafety (Genie 4 parity + anti-phishing): confirm before
            // opening an external URL, showing the FULL destination so a
            // disguised link can't smuggle the user somewhere unexpected. Only
            // real http(s) links reach here — game-command links (<d cmd>) are
            // dispatched separately and are never affected.
            if (_core?.Config.WebLinkSafety == true)
            {
                var owner = (Avalonia.Application.Current?.ApplicationLifetime
                                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (owner is not null)
                {
                    var ok = await new Views.ConfirmDialog(
                        "Open external link?",
                        $"This will open the following address in your web browser:\n\n{url}\n\nOnly continue if you trust it.")
                        .ShowDialog<bool>(owner);
                    if (!ok) return;
                }
            }

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

        // Highlight SFX → route through GenieCore.PlaySound (gate + resolve),
        // which raises SoundRequested for the audio backend below.
        Highlighting.DefaultHighlights.OnHighlightSound = name => _core?.PlaySound(name);

        // Per-highlight speak → route through GenieCore.Speak as urgent, so a
        // speak-flagged highlight barges in over ordinary stream read-aloud.
        Highlighting.DefaultHighlights.OnHighlightSpeak = text => _core?.Speak(text, urgent: true);

        // SFX backend: play gate-passed absolute paths from trigger/highlight
        // sounds and #play.
        _core.SoundRequested += path => _audio.Play(path);

        // #beep / #bell → system alert bell (already PlaySounds-gated in Core).
        _core.BeepRequested += () => _audio.Beep();

        // TTS backend: synthesize + speak text from #speak (off-thread, lazy
        // engine init). Voice dir is read live (via the provider) so a runtime
        // #config ttsvoicedir applies without reconnecting; status messages are
        // marshaled to the UI thread and shown as system lines.
        _tts = new Services.TtsService(
            () => _core.Config.TtsVoiceDir,
            () => _core.Config.TtsVoice,
            msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => GameText.AddSystemLine(msg)),
            rateProvider: () => (float)_core.Config.TtsRate,
            volumeProvider: () => _core.Config.TtsVolume / 100f);
        _core.SpeakRequested += (text, urgent) =>
            _tts.Speak(text, urgent ? Services.TtsPriority.High : Services.TtsPriority.Normal);

        // #tts install / voices / status — manage downloadable voices.
        _core.TtsCommandRequested += HandleTtsCommand;

        // Per-stream read-aloud (off by default): auto-speak text on enabled
        // streams. Config is read live so #tts read/mute applies immediately.
        // Enqueue is thread-safe, so no UI marshaling — keep it off the UI thread.
        _core.GameEvents
            .OfType<Genie.Core.Events.TextEvent>()
            .Subscribe(e =>
            {
                if (_tts is null || string.IsNullOrWhiteSpace(e.Text)) return;
                if (_core.Config.TtsReadsStream(e.Stream))
                    _tts.Speak(e.Text, TtsPriorityFor(e.Stream));
            });

        // GameText filters lines based on the user's per-tag visibility
        // toggles (Window → Game Window) — supply Display so it can read
        // ShowGameText / ShowEchoText / ShowScriptText at subscription time.
        GameText.DisplaySettings = Display;
        GameText.Attach(_core);

        // Analytics panel — reads the skill-history store; re-hooks to the
        // per-connection recorder on each Connected event internally.
        Analytics.Attach(_core);
        // AutoLog start is per-connect (uses the login character/game) — see ConnectAsync.

        Vitals.Attach(_core);
        Room.Attach(_core);
        Inventory.Attach(_core);
        Mapper.Attach(_core);
        StreamTabs.Attach(_core, GameText);
        Experience.Attach(_core);
        ActiveSpells.Attach(_core);
        TimeTracker.Attach(_core);
        InventoryView.Attach(_core);
        // /iv open, /iv search, and scan completion pop the Inventory View
        // window (the VM raises this on the UI thread).
        InventoryView.OpenRequested += () =>
        {
            if (DockFactory is not GenieDockFactory ivFactory) return;
            ivFactory.SetToolVisibility("inventory-view", true);
            InventoryViewVisible = true;
        };
        Scripts.Attach(_core);
        Scene.Attach(_core);
        IconBar.Attach(_core);
        Mobs.Attach(_core);
        Players.Attach(_core);
        RawXml.Attach(_core);
        Injuries.Attach(_core);
        // Gate the injuries auto-refresh poll on the panel actually being open
        // — no visible window, no silent `health` sends. InjuriesVisible is the
        // canonical mirror of the dock tool's state (toggle commands + the
        // X-close sync both maintain it), and a bare bool read is safe from the
        // poll's timer thread.
        _core.InjuriesPanelVisible = () => InjuriesVisible;
        AttachPluginWindows(_core);

        // Load external plugin DLLs from {AppData}/Genie5/Plugins (the builtin
        // Experience plugin is already registered in GenieCore's ctor), then
        // populate the Plugins menu.
        _core.Plugins.DiscoverAndLoad(_pluginsDir);
        RefreshPluginList();
        ScriptBar.Attach(_core);

        // Analyst Capture: when a recipe's `.cmd` finishes, close the capture it
        // opened so the meta/timings land without the user clicking Stop. Manual
        // captures (no _activeCaptureScript) are unaffected — they stop on demand.
        Observable.FromEvent<Action<string>, string>(
                h => _core.Scripts.ScriptFinished += h,
                h => _core.Scripts.ScriptFinished -= h)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(name =>
            {
                if (IsCapturing && _activeCaptureScript is { } s &&
                    string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                    StopCapture("recipe complete");
            });

        // Edit-in-editor requests come from two places, both routed to the
        // same handler: the Script Bar's pencil button (per-running-script),
        // and the `#edit <name>` meta-command (from the command bar). Both
        // ultimately want to open the script file in the user-configured
        // editor (or OS default `.cmd` handler) — so the App owns the
        // launch logic and both sources fan in here.
        ScriptBar.EditScript          += OpenScriptInEditor;
        Scripts.EditScriptRequested   += OpenScriptInEditor;
        Scripts.EditFileRequested     += path =>
        {
            // Exact-path variant for subfolder scripts / running rows — the
            // name-based OpenScriptInEditor rejects path separators by design.
            try
            {
                LaunchExternalEditor(path);
                GameText.AddSystemLine($"[editor] opened '{Path.GetFileName(path)}'");
            }
            catch (Exception ex)
            {
                ErrorLog.Log("ScriptManager.EditFile", ex);
                GameText.AddSystemLine($"[editor] failed to open '{path}': {ex.Message}");
            }
        };
        _core.EditScriptRequested     += name =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => OpenScriptInEditor(name));

        // Script Manager panel: folder-open reuses the menu command's logic
        // (issue #37 pathing); #script explorer opens/reveals the panel — the
        // typed equivalent of Window → Script Manager (Genie 4 parity).
        Scripts.OpenFolderRequested   += () => OpenScriptsFolderCommand.Execute().Subscribe();

        // Editor strip at the bottom of the panel: shows which rung of the
        // LaunchExternalEditor ladder Edit will use, and lets the user set /
        // clear the Display-settings override (the same value the Display
        // Settings dialog's text box edits).
        Scripts.ResolveEditorDescription = () =>
        {
            if (!string.IsNullOrWhiteSpace(Display.EditorPath))
                return Display.EditorPath;
            if (!string.IsNullOrWhiteSpace(_core?.Config?.Editor))
                return $"{_core!.Config.Editor}  (#config editor)";
            return OperatingSystem.IsWindows() ? "OS default (notepad)" : "OS default";
        };
        Scripts.EditorPathChanged = path =>
        {
            Display.EditorPath = path ?? string.Empty;
            try { Display.Save(_displayPath); }
            catch (Exception ex) { ErrorLog.Log("ScriptManager.SetEditor", ex); }
        };
        Scripts.RefreshEditorDisplay();
        _core.ScriptExplorerRequested += () =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DockFactory is GenieDockFactory factory &&
                    !factory.IsToolVisible("scripts"))
                {
                    factory.SetToolVisibility("scripts", true);
                    ScriptsVisible = true;
                }
            });

        // #statusbar / #status [N] {text} — scripts write progress to the ten
        // positional slots under the vitals Status Bar (#111; moved off the
        // Script Bar so the text survives with no script running). Raised on
        // the engine thread; marshal to the UI thread before touching
        // reactive state.
        _core.StatusBarRequested      += (text, index) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusSlots.SetStatus(index, text));

        // (The script tick pump — #61 — is wired at the end of WireCore so it runs
        // for the core's whole life, including while disconnected, for offline
        // scripts; #88.)

        // #layout … from the command bar — dock + store work happens on the
        // UI thread.
        _core.LayoutCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleLayoutCommand(args));

        // #plugin … — load/unload/enable/disable from the command bar.
        _core.PluginCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandlePluginCommand(args));

        // #config / #set / #setting / #settings — open the Configuration dialog
        // (bare), or operate on settings.cfg: get/set a key, save, load, edit,
        // list. UI-thread-bound because the dialog handler executes via
        // ReactiveCommand and the editor launch touches process state.
        _core.ConfigCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleConfigCommand(args));

        // Always-on-top lives in display.json (applies at app launch, before any
        // core exists) with settings.cfg's Genie 4 `alwaysontop` key as a live
        // alias: #config alwaysontop on|off / #config load land here and drive
        // the same Display flag the window's Topmost is bound to. The menu
        // toggle writes both stores, so this only fires on a genuine change.
        _core.Config.ConfigChanged += field =>
        {
            if (field != Genie.Core.Config.ConfigFieldUpdated.AlwaysOnTop) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_core is null || Display.AlwaysOnTop == _core.Config.AlwaysOnTop) return;
                Display.AlwaysOnTop = _core.Config.AlwaysOnTop;
                Display.Save(_displayPath);
                GameText.AddSystemLine($"[layout] always on top {(Display.AlwaysOnTop ? "on" : "off")}");
            });
        };
        // The ctor's settings.cfg load ran before this subscription, so a stale
        // file value can't undo a toggle made while the core didn't exist yet —
        // display.json is the authority: push it into the live config so
        // #config alwaysontop / #config list report what the window is doing.
        _core.Config.AlwaysOnTop = Display.AlwaysOnTop;

        // #goto / #go2 … from the command bar or a script — resolve the room
        // against the active zone and start an attended walk. UI-thread-bound
        // because it touches the mapper VM + AutoWalk timer state.
        _core.MapperGotoRequested     += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Mapper.GotoByName(args));

        // #mapper save/load/clear/zone/color/allowdupes/record (#146) — reset is
        // handled in Core; the rest touch the mapper VM (zone files, canvas
        // colours), so run on the UI thread.
        _core.MapperCommandRequested  += args =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleMapperCommand(args));

        // #connect / #reconnect / #lichconnect from the command bar or a script —
        // resolve to a config (reconnect-last / saved profile / explicit creds)
        // and drive the connection. UI-thread-bound because it touches the
        // connection lifecycle. The cold-start path (no live core) routes the
        // same request through TryHandleColdConnect in SendCommand.
        _core.ConnectRequested        += req =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = HandleConnectRequest(req));

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
                    "familiar" => FamiliarVisible,
                    "death"    => DeathVisible,
                    "assess"   => AssessVisible,
                    _          => true   // main + non-tool streams: nothing to mirror
                };
                if (!streamVisible)
                    GameText.AddStreamLine(e.Stream, e.Text);
            });

        // Surface timed connect-progress (TLS attempt, per-SGE-step timings,
        // fallback, game-server connect) into the game window so a stall can be
        // pinned to an exact step. Marshalled to the UI thread (fires from the
        // connection's background task). ConnectionVerboseDiag (the #config
        // conndebug gate) is refreshed per connect in ConnectAsync.
        _core.ConnectionDiag = msg =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => GameText.AddSystemLine(msg));

        // The script tick pump must run for the core's whole life — including while
        // disconnected — so offline scripts' time-based unblocks (PAUSE/delay/
        // WAITFOR) still fire (issue #88). Wired once here and never stopped on
        // disconnect. ScheduleTick gives RT-precise one-shot wakeups; the 100ms
        // heartbeat covers the pause/delay/condition waits the engine doesn't
        // self-schedule (#61).
        _core.Scripts.ScheduleTick = delay =>
            Avalonia.Threading.DispatcherTimer.RunOnce(() => _core?.Scripts.Tick(), delay);
        _scriptHeartbeat ??= new Avalonia.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(100) };
        _scriptHeartbeat.Tick -= OnScriptHeartbeat;
        _scriptHeartbeat.Tick += OnScriptHeartbeat;
        _scriptHeartbeat.Start();
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
        // User-initiated teardown: mark it so the resulting Disconnected event
        // does NOT auto-reconnect, and cancel any reconnect already scheduled.
        _manualDisconnect  = true;
        CancelPendingReconnect();

        // Stop the raw-XML recorder when the user disconnects — letting it run
        // after the session ends just produces a file that trails off mid-tag.
        // The user can re-toggle Record Session on the next connect.
        Recorder.Stop();
        AutoLogger.Stop();          // close the AutoLog rendered-text file (#15)
        Perf.Detach();              // stop the overlay timer + disable metrics collection
        UserHighlights.Metrics = null;
        // NOTE: the script heartbeat is intentionally NOT stopped here. With a
        // persistent core a .cmd can keep running while disconnected (and a login
        // script sets up + #connects), so its time-based unblocks must keep firing
        // (issue #88). The pump is cheap when no scripts are active.
        if (_core is not null)
            await _core.DisconnectAsync();

        // Manual disconnect ends the Genie-owned Lich session (logout). Kept
        // AFTER the TCP teardown so the FE detach completes first. Auto-reconnect
        // paths do not call this method, so a transient drop can reattach.
        StopOwnedLich();
    }

    /// <summary>Heartbeat handler — pumps the script engine so time-based
    /// unblocks (pause / delay / waitfor) resume without incoming game text (#61).</summary>
    private void OnScriptHeartbeat(object? sender, EventArgs e) => _core?.Scripts.Tick();

    // ── Auto-reconnect (Genie 4 parity: Game.cs CheckReconnect) ───────────────

    /// <summary>Reset reconnect bookkeeping on a fresh, successful connect.
    /// Crucially re-arms the policy guard: the user must send a command in THIS
    /// session before it will ever auto-reconnect.</summary>
    private void OnConnectionEstablished()
    {
        _wasConnected          = true;
        _manualDisconnect      = false;
        _userInputSinceConnect = false;
        _intentionalGameExit   = false;
        _reconnectAttempts     = 0;
        _reconnectAt           = null;
        _dropWasError          = false;
        _lastDropReason        = null;
        EnsureReconnectTimer();
    }

    /// <summary>Decide whether a just-dropped session should auto-reconnect, and
    /// if so schedule the first attempt. No-op for failed logins, manual
    /// teardown, or when <c>#config reconnect</c> is off.</summary>
    private void MaybeArmAutoReconnect()
    {
        if (_reconnectAt is not null) return;            // already scheduled for this drop
        if (!_wasConnected)            return;            // never established (failed login) — don't loop
        if (_manualDisconnect)         return;            // user/explicit teardown — stay down
        if (_intentionalGameExit)      return;            // user typed quit/exit — they chose to leave
        if (!(_core?.Config.Reconnect ?? false)) return; // auto-reconnect disabled (#config reconnect false)
        if (LastConnectionConfig is null) return;

        _wasConnected = false;                            // consume so the paired event doesn't re-arm

        // Policy guard (DR ToS: no auto-reconnect-and-resume while away). Decided
        // here, at drop time, so an unattended session says so once instead of
        // flickering "Attempting…" then "aborted". A session the user actually
        // drove (sent ≥1 command) is eligible.
        if (!_userInputSinceConnect)
        {
            GameText.AddSystemLine("Auto-reconnect skipped — no commands were sent this session.");
            return;
        }

        ScheduleReconnect();
    }

    /// <summary>Number of auto-reconnect attempts before giving up. The bounded
    /// ladder is ~2 retries at 8s then 3 at 30s (≈1.75 min total), after which we
    /// stop and show Disconnected rather than retrying forever.</summary>
    private const int MaxAutoReconnectAttempts = 5;

    /// <summary>Schedule the next reconnect attempt on the bounded backoff ladder:
    /// the first couple of retries at 8s, then 30s, then give up at
    /// <see cref="MaxAutoReconnectAttempts"/> and surface "Disconnected". <see
    /// cref="_reconnectAttempts"/> is the count already made (incremented in
    /// CheckReconnect before each attempt), so the switch picks the delay BEFORE the
    /// next attempt.</summary>
    private void ScheduleReconnect()
    {
        if (_reconnectAttempts >= MaxAutoReconnectAttempts)
        {
            // Ladder exhausted — stop retrying and tell the user plainly.
            _reconnectAt = null;
            IsConnected      = false;
            ConnectionStatus = "Disconnected";
            GameText.AddSystemLine(
                $"Auto-reconnect gave up after {MaxAutoReconnectAttempts} attempts. Disconnected — use #connect to retry.");
            return;
        }

        var delay = _reconnectAttempts switch
        {
            <= 1 => TimeSpan.FromSeconds(8),    // first couple, quick
            _    => TimeSpan.FromSeconds(30),   // then hold at 30s
        };
        _reconnectAt = DateTime.Now + delay;
        GameText.AddSystemLine(
            $"Attempting to reconnect in {delay.TotalSeconds:0} seconds (attempt {_reconnectAttempts + 1}/{MaxAutoReconnectAttempts}).");
        EnsureReconnectTimer();
    }

    private void CancelPendingReconnect()
    {
        _reconnectAt       = null;
        _reconnectAttempts = 0;
    }

    /// <summary>Lazily create the 1s reconnect ticker. It only acts when a
    /// reconnect is actually pending, so leaving it running idle is cheap.</summary>
    private void EnsureReconnectTimer()
    {
        if (_reconnectTimer is not null) return;
        _reconnectTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnectTimer.Tick += (_, _) => CheckReconnect();
        _reconnectTimer.Start();
    }

    /// <summary>Fire a due reconnect — but abort if the user never touched the
    /// session (the DR-ToS "no auto-reconnect-and-resume while away" guard).</summary>
    private void CheckReconnect()
    {
        if (_reconnectAt is null)              return;
        if (DateTime.Now < _reconnectAt.Value) return;

        if (!_userInputSinceConnect)
        {
            _reconnectAt = null;
            GameText.AddSystemLine("Reconnect aborted — no user input since the last connect.");
            return;
        }

        _reconnectAt = null;
        _reconnectAttempts++;
        _ = DoAutoReconnectAsync();
    }

    private async Task DoAutoReconnectAsync()
    {
        var cfg     = LastConnectionConfig;
        var profile = ConnectedProfile;
        if (cfg is null) return;

        try
        {
            // Single establish attempt (isAutoReconnect) — success fires the
            // Connected event, which resets all the bookkeeping above.
            await ConnectAsync(cfg, profile, isAutoReconnect: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("DoAutoReconnect", ex);
            // Failed to re-establish: escalate and try again, unless the user
            // disabled reconnect or tore things down while we were trying.
            if ((_core?.Config.Reconnect ?? false) && !_manualDisconnect && !_intentionalGameExit)
                ScheduleReconnect();
        }
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

            // Floating windows live in IRootDock.Windows, OUTSIDE the tree
            // snapshot — capture them separately or a saved layout silently
            // drops every floated panel (field report, 2026-07-16).
            layout.FloatingWindows = factory.CaptureFloatingWindows();

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
            // Tear down any floating HostWindows on the OUTGOING root first.
            // Swapping in a rebuilt tree below doesn't close them (Dock only
            // owns windows via the live root), so a floated tool — e.g. the
            // Mapper, which the default layout floats — would otherwise linger
            // as an orphaned window beside the freshly-floated copy (duplicate
            // travel window on Reset to Default Layout).
            factory.CloseFloatingWindows();

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

            // Re-float the layout's floating windows at their saved geometry.
            // Deferred a UI tick: FloatDockable needs the freshly-assigned tree
            // live on the DockControl and the owner window realized (the same
            // constraint FloatMapperIfPending documents).
            if (layout.FloatingWindows is { Count: > 0 } floats)
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => { factory.RestoreFloatingWindows(floats); RefreshVisibilityBools(); },
                    Avalonia.Threading.DispatcherPriority.Background);

            RefreshVisibilityBools();
        }
    }

    /// <summary>
    /// Float the Mapper out into its own window iff a default-layout
    /// presentation armed <see cref="Docking.GenieDockFactory.PendingMapperFloat"/>.
    /// Called from <c>MainWindow.OnOpened</c> for the startup default, and posted
    /// after runtime default rebuilds (Reset, leaving windowed mode). No-op when
    /// the flag isn't armed — e.g. when a saved layout is showing.
    /// </summary>
    public void TryFloatPendingMapper()
    {
        if (DockFactory is GenieDockFactory f) f.FloatMapperIfPending();
    }

    /// <summary>
    /// True when the user has already defined where the Mapper lives by setting
    /// a default layout — the global default (<see cref="DisplaySettings.GlobalDefaultLayout"/>)
    /// or any profile's <see cref="ConnectionProfile.DefaultLayoutName"/>. Such a
    /// layout is applied on connect and owns the Mapper's placement, so the
    /// startup auto-float is suppressed to honour the user's choice. (Explicit
    /// "Reset to Default Layout" still floats — that action asks for the factory
    /// default on purpose.)
    /// </summary>
    private bool HasUserDefinedDefaultLayout()
        => !string.IsNullOrWhiteSpace(Display.GlobalDefaultLayout)
           || Profiles.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.DefaultLayoutName));

    /// <summary>
    /// Post <see cref="TryFloatPendingMapper"/> to run after the dock tree has
    /// settled on the freshly-assigned layout. Used by the runtime rebuild paths
    /// (Reset to Default, windowed→tabbed) where the window is already shown.
    /// </summary>
    private void FloatMapperAfterLayout()
        => Avalonia.Threading.Dispatcher.UIThread.Post(
               TryFloatPendingMapper,
               Avalonia.Threading.DispatcherPriority.Background);

    /// <summary>
    /// On connect, apply the appropriate default layout: the profile's own
    /// <see cref="ConnectionProfile.DefaultLayoutName"/> from its store, else
    /// the global default (<see cref="DisplaySettings.GlobalDefaultLayout"/>),
    /// else leave the current/built-in layout untouched.
    /// </summary>
    private void ApplyDefaultLayoutForConnect(ConnectionProfile? profile)
    {
        var profileDefault = profile?.DefaultLayoutName;
        if (!string.IsNullOrWhiteSpace(profileDefault))
        {
            // The profile's own store wins; fall back to the global store —
            // the Connect dialog's Layout picker offers both, so a global
            // layout name stored on the profile must resolve too.
            if (_profileLayouts?.Load(profileDefault) is { } pl)
            {
                ApplyLayout(pl);
                return;
            }
            if (_globalLayouts.Load(profileDefault) is { } pgl)
            {
                ApplyLayout(pgl);
                return;
            }
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

    /// <summary>Rebuild <see cref="ThemeMenuItems"/> for the Edit → Theme
    /// submenu (#20): re-scan Config/Themes for hand-dropped files and
    /// refresh which entry carries the radio check.</summary>
    private void RefreshThemeList()
    {
        Themes.ReloadCustomThemes();
        ThemeMenuItems.Clear();
        foreach (var t in Themes.All)
            ThemeMenuItems.Add(new ThemeMenuItem(
                t.IsBuiltIn ? t.Name : $"{t.Name} (Custom)",
                t.Name,
                t.Name.Equals(Themes.CurrentName, StringComparison.OrdinalIgnoreCase),
                ApplyThemeCommand));
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

        // Everything past the bare form operates on settings.cfg (GenieConfig),
        // the Genie 4 #config store — NOT display.json. display.json is the
        // App-only visual store, edited via the Configuration dialog + menus.
        var config = _core?.Config;
        if (config is null)
        {
            GameText.AddSystemLine("[config] Connect to a game first — settings load with the session.");
            return;
        }

        var split = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub   = split[0];                       // preserve case for the key form
        var verb  = sub.ToLowerInvariant();
        var rest  = split.Length > 1 ? split[1].Trim() : string.Empty;

        switch (verb)
        {
            case "save":
                // #config save → flush settings.cfg. Genie 4 parity; safe to
                // call repeatedly. (display.json auto-saves on every menu edit.)
                if (config.Save())
                    GameText.AddSystemLine($"[config] settings.cfg saved → {config.ConfigDir}");
                else
                    GameText.AddSystemLine("[config] Could not save settings.cfg.");
                break;

            case "load":
            case "reload":
                // #config load → re-read settings.cfg into the live config.
                // Values read live (command/script chars, directories) take
                // effect at once; those captured at startup apply on reconnect.
                if (config.Load())
                    GameText.AddSystemLine("[config] settings.cfg reloaded. Some settings apply on reconnect.");
                else
                    GameText.AddSystemLine("[config] No settings.cfg found to load.");
                break;

            case "list":
                // #config list → dump every key and its current value, grouped
                // under category headers (Connection / Display / Scripting / …)
                // so the long listing is scannable. Keys whose feature isn't
                // wired yet are flagged "(reserved)" so the listing doesn't
                // imply they do something. See GenieConfig.ReservedKeys and
                // GenieConfig.ConfigCategories.
                GameText.AddSystemLine("[config] current settings (settings.cfg) — (reserved) = not yet wired:");
                {
                    var pairs = config.ToConfigPairs()
                                      .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
                    var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    // Print one category at a time, alphabetised within the
                    // section and column-aligned to that section's widest key.
                    void EmitGroup(string header, IEnumerable<string> keys)
                    {
                        var present = keys.Where(pairs.ContainsKey)
                                          .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                                          .ToList();
                        if (present.Count == 0) return;
                        var pad = present.Max(k => k.Length);
                        GameText.AddSystemLine("");
                        GameText.AddSystemLine($"[{header}]");
                        foreach (var k in present)
                        {
                            shown.Add(k);
                            var line = $"  {k.PadRight(pad)} = {pairs[k]}";
                            GameText.AddSystemLine(
                                Genie.Core.Config.GenieConfig.IsReserved(k) ? $"{line}  (reserved)" : line);
                        }
                    }

                    foreach (var (category, keys) in Genie.Core.Config.GenieConfig.ConfigCategories)
                        EmitGroup(category, keys);

                    // Catch-all: any key not assigned to a category above (e.g. a
                    // newly added setting) so the listing always covers every key.
                    EmitGroup("Other", pairs.Keys.Where(k => !shown.Contains(k)));
                }
                break;

            case "edit":
                OpenSettingsCfgInEditor(config);
                break;

            default:
                // #config <key>          → echo the current value
                // #config <key> <value>  → set it and persist settings.cfg
                if (rest.Length == 0)
                {
                    var current = config.GetSetting(sub);
                    var reserved = Genie.Core.Config.GenieConfig.IsReserved(verb) ? "  (reserved — not yet wired)" : "";
                    GameText.AddSystemLine(current is null
                        ? $"[config] Unknown setting '{sub}'. Try #config list."
                        : $"[config] {verb} = {current}{reserved}");
                }
                else
                {
                    try
                    {
                        config.SetSetting(sub, rest);   // throws on an unrecognized key
                        config.Save();
                        var reserved = Genie.Core.Config.GenieConfig.IsReserved(verb) ? "  (reserved — saved, but not yet wired)" : "  (saved)";
                        GameText.AddSystemLine($"[config] {verb} = {config.GetSetting(sub)}{reserved}");
                    }
                    catch
                    {
                        GameText.AddSystemLine($"[config] Unknown setting '{sub}'. Try #config list.");
                    }
                }
                break;
        }
    }

    /// <summary>Open settings.cfg in an external text editor, using the shared
    /// <see cref="LaunchExternalEditor"/> resolution ladder (Display Settings →
    /// Editor Path → <c>#config editor</c> → OS default). This is #config edit's
    /// Genie 4 behavior, pointed at settings.cfg (GenieConfig) rather than
    /// display.json.</summary>
    private void OpenSettingsCfgInEditor(Genie.Core.Config.GenieConfig config)
    {
        try
        {
            var path = System.IO.Path.Combine(config.ConfigDir, "settings.cfg");
            if (!System.IO.File.Exists(path))
                config.Save();  // first-write — make sure there's a file to open

            LaunchExternalEditor(path);
            GameText.AddSystemLine($"[config] Opened {path}");
            GameText.AddSystemLine(
                "[config] Edits apply on the next #config load or restart. To set a different editor, use \"#config editor <path>\" or Edit → Configuration → Display Settings → Editor Path.");
        }
        catch (Exception ex)
        {
            GameText.AddSystemLine($"[config] Could not open editor: {ex.Message}");
            Diagnostics.ErrorLog.Log("ConfigCommand.Edit", ex);
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
    /// <summary>#mapper save/load/clear/zone/color/allowdupes/record (#146),
    /// dispatched on the UI thread from the command engine (`reset` is handled in
    /// Core). Unknown subcommands echo usage; nothing reaches the game.</summary>
    private void HandleMapperCommand(string args)
    {
        void Echo(string m) => GameText.AddSystemLine(m);
        var parts = (args ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sub   = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

        switch (sub)
        {
            case "save":
                Mapper.SaveCurrentZone();
                Echo($"[mapper] {Mapper.LoadStatus}");
                break;

            case "load":
                var reloaded = Mapper.ReloadActiveZone();
                Echo(reloaded is null ? "[mapper] No zone loaded." : $"[mapper] Reloaded {reloaded}.");
                break;

            case "clear":
                Mapper.ClearActiveZone();
                Echo("[mapper] Current zone cleared in memory — #mapper save to persist, or #mapper load to undo.");
                break;

            case "zone":
                if (parts.Length > 1)
                {
                    var target = string.Join(" ", parts.Skip(1));
                    Echo(Mapper.SwitchZone(target)
                        ? $"[mapper] Zone: {Mapper.ZoneName} (id {Mapper.CurrentZoneId})"
                        : $"[mapper] No zone matching '{target}'.");
                }
                else Echo($"[mapper] Current zone: {Mapper.ZoneName} (id {Mapper.CurrentZoneId})");
                break;

            case "color":
            case "colour":
                HandleMapperColor(parts, Echo);
                break;

            case "allowdupes":
                var dup = parts.Length > 1 ? ParseOnOff(parts[1], Mapper.AllowDuplicate) : !Mapper.AllowDuplicate;
                Mapper.AllowDuplicate = dup;
                Echo($"[mapper] Allow duplicate rooms: {(dup ? "on" : "off")}");
                break;

            case "record":
                // Auto-map as you walk = the AutoMapper master toggle (Genie 3/4 flavour).
                var rec = parts.Length > 1 ? ParseOnOff(parts[1], _core!.Config.AutoMapper) : !_core!.Config.AutoMapper;
                _core.Config.SetSetting("automapper", rec.ToString(), showException: false);
                Echo($"[mapper] Record (auto-map as you walk): {(rec ? "on" : "off")}");
                break;

            default:
                Echo("[mapper] Usage: #mapper reset | save | load | clear | zone [<id|name>] | " +
                     "color <bg|text> <colour> | allowdupes [on|off] | record [on|off]");
                break;
        }
    }

    private void HandleMapperColor(string[] parts, Action<string> echo)
    {
        if (parts.Length < 3) { echo("[mapper] Usage: #mapper color <bg|text> <colour>"); return; }
        if (!Avalonia.Media.Color.TryParse(parts[2], out var c)) { echo($"[mapper] Not a colour: {parts[2]}"); return; }
        switch (parts[1].ToLowerInvariant())
        {
            case "bg": case "background": Mapper.MapBackground = c; echo($"[mapper] Map background: {parts[2]}"); break;
            case "text": case "fg": case "foreground": Mapper.MapTextColor = c; echo($"[mapper] Map text colour: {parts[2]}"); break;
            default: echo("[mapper] Usage: #mapper color <bg|text> <colour>"); break;
        }
    }

    /// <summary>Parse an on/off/toggle flag argument; anything unrecognised toggles.</summary>
    private static bool ParseOnOff(string token, bool current) => token.ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1"  => true,
        "off" or "false" or "no" or "0" => false,
        _                               => !current,
    };

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
        if (string.IsNullOrWhiteSpace(cmd)) return Task.CompletedTask;

        // Cold #connect / #reconnect / #lichconnect typed as the FIRST action:
        // handle it before the core is built so the chosen profile's data root is
        // honored when the core is first constructed (the persistent core's data
        // root is fixed at build time). Once a core exists these verbs flow through
        // ProcessInput → CommandEngine → ConnectRequested → HandleConnectRequest.
        if (_core is null && TryHandleColdConnect(cmd))
            return Task.CompletedTask;

        // Persistent core: build it on first use so commands, scripts, #var/#class,
        // aliases, triggers etc. all work while disconnected — a logon script can
        // set variables, toggle trigger classes, then #connect (issue #88). Built
        // with the default data root; a subsequent connect to a custom-root profile
        // warns rather than relocating (see WarnIfDataRootMismatch). The eager flag
        // loads the user's saved global rules so this very first offline command /
        // logon script sees them, not empty engines (M4).
        EnsureCoreBuilt(null, eagerLoadOfflineRules: true);

        // Typed user input cancels any in-flight auto-walk — per the
        // compliance review, "any non-map-click input cancels." The
        // user has taken manual control of the session; respecting
        // that immediately keeps the walker on the responsive side
        // of DR policy. Cancel BEFORE dispatching the typed input
        // so the cancelled walk doesn't try to send a stale step
        // when the next room-change fires.
        if (Mapper.AutoWalk?.Current is not null)
            Mapper.AutoWalk.Cancel("user took manual command");

        // Policy guard for auto-reconnect: the user has actively driven this
        // session, so a later unexpected drop may be auto-reconnected.
        _userInputSinceConnect = true;

        // DR's disconnect verbs (the login banner literally says "Type QUIT or
        // EXIT!"): a server close that follows a deliberate quit/exit is the user
        // CHOOSING to leave, so it must NOT auto-reconnect (both a UX fix and the
        // sharpest edge of the DR "no auto-reconnect-and-resume" policy). Any other
        // command means they're still playing, so clear the flag — that covers a
        // quit the game rejected (e.g. mid-roundtime).
        var verb = cmd.Trim();
        _intentionalGameExit = verb.Equals("quit", StringComparison.OrdinalIgnoreCase)
                            || verb.Equals("exit", StringComparison.OrdinalIgnoreCase);

        _core!.ProcessInput(cmd);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Recognize a <c>#connect</c> / <c>#reconnect</c> / <c>#lichconnect</c> typed
    /// while disconnected (no live core to route it through) and drive the same
    /// <see cref="HandleConnectRequest"/> path the in-core command wire uses.
    /// Returns <c>true</c> when the input was a connect verb (handled), <c>false</c>
    /// otherwise so the caller can hint that there's nothing to run while
    /// disconnected. Uses the default command char <c>#</c> — the configurable
    /// char only matters once a core (and its config) exists.
    /// </summary>
    private bool TryHandleColdConnect(string cmd)
    {
        var trimmed = cmd.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;

        var parts = Genie.Core.Parsing.ArgumentParser.ParseArgs(trimmed[1..]);
        if (parts.Count == 0) return false;

        var verb = parts[0].ToLowerInvariant();
        if (verb is not ("connect" or "reconnect" or "lichconnect" or "lconnect" or "lc")) return false;

        IReadOnlyList<string> args = verb == "reconnect"
            ? System.Array.Empty<string>()
            : parts.Skip(1).ToList();
        var req = new Genie.Core.Commanding.ConnectRequest(
            args, IsLich: verb is "lichconnect" or "lconnect" or "lc");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = HandleConnectRequest(req));
        return true;
    }

    /// <summary>
    /// Interpret a <see cref="Genie.Core.Commanding.ConnectRequest"/> (Genie 4
    /// grammar: 0 args = reconnect last, 1 arg = saved profile by name, 4 args =
    /// explicit <c>account password character game</c>) and drive the connection,
    /// disconnecting any live session first. The Lich variant forces
    /// <see cref="ConnectionMode.LichProxy"/>.
    /// </summary>
    private async Task HandleConnectRequest(Genie.Core.Commanding.ConnectRequest req)
    {
        var args = req.Args;
        ConnectionConfig?  cfg;
        ConnectionProfile? profile = null;

        switch (args.Count)
        {
            case 0:   // reconnect the last session
                if (LastConnectionConfig is null)
                {
                    GameText.AddSystemLine("[connect] nothing to reconnect to — connect once first.");
                    return;
                }
                cfg     = LastConnectionConfig;
                profile = ConnectedProfile;
                break;

            case 1:   // saved profile by name
            {
                var name = args[0];
                profile = Profiles.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (profile is null)
                {
                    GameText.AddSystemLine($"[connect] no profile named '{name}'.");
                    return;
                }
                cfg = ConfigFromProfile(profile);
                if (req.IsLich && cfg.Mode != ConnectionMode.LichProxy)
                    cfg = cfg with { Mode = ConnectionMode.LichProxy };
                break;
            }

            case 4:   // explicit: account password character game
                cfg = new ConnectionConfig
                {
                    Mode            = req.IsLich ? ConnectionMode.LichProxy : ConnectionMode.DirectSGE,
                    SgeHost         = "eaccess.play.net",
                    SgePort         = 7900,
                    AccountName     = args[0],
                    AccountPassword = args[1],
                    CharacterName   = args[2],
                    GameCode        = args[3],
                };
                break;

            default:
                GameText.AddSystemLine(
                    "[connect] usage: #connect <profile> | #connect account password character game " +
                    "(use a saved profile to keep your password out of scripts)");
                return;
        }

        var who = string.IsNullOrWhiteSpace(cfg.CharacterName) ? "last session" : cfg.CharacterName;
        GameText.AddSystemLine($"[connect] connecting {who}...");

        // An explicit connect supersedes any pending auto-reconnect.
        CancelPendingReconnect();

        try
        {
            if (IsConnected) await DisconnectAsync();
            else _manualDisconnect = true;   // ensure an in-flight drop doesn't race an auto-reconnect
            await ConnectAsync(cfg, profile);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("HandleConnectRequest", ex);
            GameText.AddSystemLine($"[connect] failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the external editor for opening script/config files and launches
    /// it on <paramref name="file"/>. Preference ladder, first that launches wins:
    /// <list type="number">
    ///   <item>Display Settings → Editor Path (<see cref="DisplaySettings.EditorPath"/>)</item>
    ///   <item><c>#config editor</c> (<see cref="Genie.Core.Config.GenieConfig.Editor"/>) — Genie 4 parity</item>
    ///   <item>OS default plain-text editor (notepad / <c>open -t</c> / xdg-open)</item>
    /// </list>
    /// Each configured candidate is launched inside try/catch; if it throws (stale
    /// path, or the Windows-only <c>"notepad.exe"</c> default on macOS/Linux) the
    /// next rung is attempted. A bare executable name (e.g. <c>code</c>,
    /// <c>notepad++.exe</c>) resolves via PATH because <c>UseShellExecute=false</c>.
    /// Never shell-launches the file directly: the Windows shell association for
    /// <c>.cmd</c> would EXECUTE it instead of opening it for editing (issue #63).
    /// Returns a human-readable description of whatever launched.
    /// </summary>
    private string LaunchExternalEditor(string file)
    {
        // Normalize once so every rung — custom/config candidates and the OS
        // default — passes the editor the same absolute path.
        var fullPath = System.IO.Path.GetFullPath(file);

        // 1) explicit GUI override, then 2) the #config editor value.
        foreach (var candidate in new[] { Display.EditorPath, _core?.Config?.Editor })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(candidate)
                    { UseShellExecute = false, ArgumentList = { fullPath } });
                return candidate;
            }
            catch (Exception ex)
            {
                // Stale path or wrong-OS default — log and fall to the next rung.
                Diagnostics.ErrorLog.Log($"LaunchExternalEditor({candidate})", ex);
            }
        }

        // 3) OS default plain-text editor (see Genie.Core.Runtime.FileBrowser
        // for the platform ladder and the ArgumentList spaces rationale).
        System.Diagnostics.Process.Start(Genie.Core.Runtime.FileBrowser.BuildOpenInDefaultTextEditorInfo(fullPath));
        return "OS default";
    }

    /// <summary>Script file extensions the engine accepts, in lookup priority
    /// order: <c>.cmd</c> (Genie/Wizard dialect), <c>.inc</c> (include helpers),
    /// <c>.js</c> (JavaScript array scripts). Used by <see cref="OpenScriptInEditor"/>
    /// for both existing-file lookup and the new-script type picker — keep in
    /// sync with <see cref="Views.NewScriptTypeDialog"/>.</summary>
    public static readonly string[] SupportedScriptExtensions = { ".cmd", ".inc", ".js" };

    /// <summary>
    /// Opens the named script in the user-configured external editor
    /// (Display Settings → Editor Path, then <c>#config editor</c>; see
    /// <see cref="LaunchExternalEditor"/> for the full ladder).
    /// <para>
    /// Lookup: if <paramref name="name"/> carries a supported extension that
    /// exact file is used; otherwise <c>{name}.cmd</c> → <c>.inc</c> → <c>.js</c>
    /// are tried in turn. When no file exists the script is <b>created</b> first
    /// (Genie 4 <c>#edit</c> parity): the typed extension is used if given, else
    /// a small dialog asks which supported type to create. Cancelling the dialog
    /// creates nothing.
    /// </para>
    /// </summary>
    public async void OpenScriptInEditor(string name)
    {
        if (_core is null || string.IsNullOrWhiteSpace(name)) return;

        var dir = _core.Scripts.ScriptsDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            GameText.AddSystemLine($"[editor] scripts directory not found: '{dir}'");
            return;
        }

        name = name.Trim();

        // Script names are bare basenames living directly under ScriptsDir.
        // Reject anything that could escape it (path separators, `..`, or a
        // rooted/drive-qualified path) before we ever touch the filesystem.
        if (name.IndexOfAny(new[] { '/', '\\' }) >= 0
            || name.Contains("..")
            || Path.IsPathRooted(name))
        {
            GameText.AddSystemLine($"[editor] invalid script name: '{name}'");
            return;
        }

        // Did the user type an explicit supported extension (e.g. `foo.js`)?
        var typedExt = Path.GetExtension(name);
        var hasSupportedExt =
            SupportedScriptExtensions.Contains(typedExt, StringComparer.OrdinalIgnoreCase);
        var baseName = hasSupportedExt ? Path.GetFileNameWithoutExtension(name) : name;

        // Locate an existing file.
        string? candidate = null;
        if (hasSupportedExt)
        {
            var p = Path.Combine(dir, baseName + typedExt);
            if (File.Exists(p)) candidate = p;
        }
        else
        {
            foreach (var ext in SupportedScriptExtensions)
            {
                var p = Path.Combine(dir, baseName + ext);
                if (File.Exists(p)) { candidate = p; break; }
            }
        }

        // Not found → create it (Genie 4 #edit parity). Honour the typed
        // extension; otherwise ask which supported type to create.
        if (candidate is null)
        {
            var ext = hasSupportedExt
                ? typedExt.ToLowerInvariant()
                : await PromptForScriptTypeAsync(baseName);
            if (ext is null) return;  // user cancelled the type picker

            candidate = Path.Combine(dir, baseName + ext);
            try
            {
                // Seed an empty file — parity with Genie 4's create-on-edit.
                File.WriteAllText(candidate, string.Empty);
                GameText.AddSystemLine($"[editor] created '{Path.GetFileName(candidate)}'");
            }
            catch (Exception ex)
            {
                ErrorLog.Log("OpenScriptInEditor.Create", ex);
                GameText.AddSystemLine(
                    $"[editor] could not create '{baseName + ext}': {ex.Message}");
                return;
            }
        }

        try
        {
            LaunchExternalEditor(candidate);
            GameText.AddSystemLine($"[editor] opened '{Path.GetFileName(candidate)}'");
        }
        catch (Exception ex)
        {
            ErrorLog.Log("OpenScriptInEditor", ex);
            GameText.AddSystemLine($"[editor] failed to open '{name}': {ex.Message}");
        }
    }

    /// <summary>Asks the user which supported script type to create for a new
    /// script, via <see cref="Views.NewScriptTypeDialog"/>. Returns the chosen
    /// extension (incl. dot) or null on cancel. When no desktop window is
    /// available (headless/tests) falls back to the default <c>.cmd</c> dialect
    /// rather than blocking.</summary>
    private async Task<string?> PromptForScriptTypeAsync(string baseName)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return ".cmd";

        return await Views.NewScriptTypeDialog.Show(owner, baseName);
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
                        || ext.Equals(".inc", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".js",  StringComparison.OrdinalIgnoreCase);
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
