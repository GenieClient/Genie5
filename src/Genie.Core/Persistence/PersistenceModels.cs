namespace Genie.Core.Persistence;

public sealed class AliasPersistenceModel
{
    public string Name { get; set; } = string.Empty;
    public string Expansion { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
}

public sealed class TriggerPersistenceModel
{
    public string Pattern { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public bool CaseSensitive { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string ClassName { get; set; } = string.Empty;
    public string SoundFile { get; set; } = string.Empty;
    /// <summary>Per-rule TTS: "" = off, "*" = speak the matched line, else the text to speak.</summary>
    public string Speak { get; set; } = string.Empty;
    /// <summary>Opt-in script-expression evaluation of the action (#150): evaluate
    /// <c>{…}</c> blocks before dispatch. Default off preserves existing triggers.</summary>
    public bool Eval { get; set; }
    /// <summary>Opt-in "match all" (#23): fire the action once per match on the
    /// line instead of once per line. Default off preserves existing triggers.</summary>
    public bool MatchAll { get; set; }
}

public sealed class VariablePersistenceModel
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Scope { get; set; } = "User";
}

public sealed class HighlightPersistenceModel
{
    public string Pattern { get; set; } = string.Empty;
    public string ForegroundColor { get; set; } = "Yellow";
    public string BackgroundColor { get; set; } = string.Empty;

    /// <summary>"String" | "Line" | "BeginsWith" | "Regex". Empty falls back to IsRegex.</summary>
    public string MatchType { get; set; } = string.Empty;

    /// <summary>Legacy field; if true and MatchType is empty, treat as Regex; else String.</summary>
    public bool IsRegex { get; set; }

    public bool CaseSensitive { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string ClassName { get; set; } = string.Empty;
    public string SoundFile { get; set; } = string.Empty;
    /// <summary>Per-rule TTS: "" = off, "*" = speak the matched line, else the text to speak.</summary>
    public string Speak { get; set; } = string.Empty;

    /// <summary>Windows this rule paints in (canonical ids). Empty / absent =
    /// every window — so older highlights.json files and Genie 4 cfg imports
    /// (which have no window column) load as apply-everywhere, unchanged.</summary>
    public List<string> Windows { get; set; } = new();
}

public sealed class ClassPersistenceModel
{
    public string Name     { get; set; } = string.Empty;
    public bool   IsActive { get; set; } = true;
}

public sealed class NamePersistenceModel
{
    public string Name            { get; set; } = string.Empty;
    public string ForegroundColor { get; set; } = "Yellow";
    public string BackgroundColor { get; set; } = string.Empty;
}

public sealed class SubstitutePersistenceModel
{
    public string Pattern       { get; set; } = string.Empty;
    public string Replacement   { get; set; } = string.Empty;
    public bool   CaseSensitive { get; set; }
    public bool   IsEnabled     { get; set; } = true;
    public string ClassName     { get; set; } = string.Empty;
}

public sealed class GagPersistenceModel
{
    public string Pattern       { get; set; } = string.Empty;
    public bool   CaseSensitive { get; set; }
    public bool   IsEnabled     { get; set; } = true;
    public string ClassName     { get; set; } = string.Empty;
}

public sealed class MacroPersistenceModel
{
    public string Key    { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public sealed class PresetPersistenceModel
{
    public string Id              { get; set; } = string.Empty;
    public string ForegroundColor { get; set; } = "Default";
    public string BackgroundColor { get; set; } = string.Empty;
    public bool   HighlightLine   { get; set; } = false;
}

/// <summary>
/// Persisted UI toggle/checkbox state. Grows as new toggles are added.
/// Stored in a single <c>clientstate.json</c> file.
/// </summary>
public sealed class ClientState
{
    public bool   AutoLogEnabled       { get; set; }
    public bool   MapperEnabled        { get; set; }
    public bool   MapperDebug          { get; set; }
    /// <summary>When true, #goto launches automapper.cmd script instead of
    /// the built-in mapper walk engine.</summary>
    public bool   MapperUseScript      { get; set; }
    /// <summary>Script name to run for #goto walks (default: "automapper").</summary>
    public string MapperScriptName     { get; set; } = "automapper";
    /// <summary>When true, the client runs the GitHub Update Maps refresh
    /// once at startup (after connect). Off by default — opt-in.</summary>
    public bool   UpdateMapsAtStartup  { get; set; }
    /// <summary>When true, every Update Maps run also pulls helper walking
    /// scripts (automapper.cmd, sharddetour.cmd, etc.) from the repo's
    /// "Copy These to Genie's Scripts Folder" subdirectory and overwrites
    /// any local file whose git blob sha differs upstream.</summary>
    public bool   OverwriteScriptsOnUpdate { get; set; }
    /// <summary>"Tabbed" or "Mdi". Controls how output windows are displayed.</summary>
    public string LayoutMode           { get; set; } = "Tabbed";
}

public sealed class LayoutState
{
    /// <summary>Avalonia WindowState name: Normal, Maximized, FullScreen.</summary>
    public string WindowState { get; set; } = "Normal";

    // Restore-position geometry (only meaningful when WindowState == Normal).
    public int    WindowX      { get; set; } = 100;
    public int    WindowY      { get; set; } = 100;
    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    /// <summary>Dock pane proportions keyed by pane Id.</summary>
    public Dictionary<string, double> DockProportions { get; set; } = new();

    /// <summary>"Tabbed" or "Mdi".</summary>
    public string LayoutMode { get; set; } = "Tabbed";

    /// <summary>Per-dockable MDI bounds keyed by dockable Id (MDI mode only).</summary>
    public Dictionary<string, MdiWindowBounds> MdiBounds { get; set; } = new();

    /// <summary>IDs of dockables that were hidden (closed) at save time.</summary>
    public List<string> HiddenDockables { get; set; } = new();
}

public sealed class MdiWindowBounds
{
    public double X      { get; set; }
    public double Y      { get; set; }
    public double Width  { get; set; }
    public double Height { get; set; }
    /// <summary>Normal, Minimized, Maximized.</summary>
    public string State  { get; set; } = "Normal";
}

public sealed class WindowSettingsPersistenceModel
{
    public string  Id           { get; set; } = string.Empty;
    public string  DisplayTitle { get; set; } = string.Empty;
    public string  FontFamily   { get; set; } = string.Empty;
    public double  FontSize     { get; set; } = 13;
    public string  Foreground   { get; set; } = "Default";
    public string  Background   { get; set; } = string.Empty;
    public bool    Timestamp    { get; set; }
    public bool    NameListOnly { get; set; }
    /// <summary>Defaults true so streams mirror into the Main window unless the
    /// user opts out; a pre-existing windows.json with no field (or the old
    /// default-off era) picks up the new always-echo behaviour on load.</summary>
    public bool    EchoToMain   { get; set; } = true;
    /// <summary>Defaults true so pre-#120 windows.json files (no field) keep
    /// the shipped always-wrap behaviour.</summary>
    public bool    WordWrap     { get; set; } = true;
    /// <summary>Defaults true so windows.json files predating the toggle (no
    /// field) keep the shipped flash-on-unread-activity behaviour.</summary>
    public bool    FlashOnActivity { get; set; } = true;
    public string? IfClosed     { get; set; }
    public bool    HasIfClosed  { get; set; }
}
