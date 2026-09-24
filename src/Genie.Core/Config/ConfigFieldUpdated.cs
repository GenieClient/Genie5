namespace Genie.Core.Config;

public enum ConfigFieldUpdated
{
    Reconnect,
    Autolog,
    KeepInput,
    Muted,
    AutoMapper,
    LogDir,
    CheckForUpdates,
    AutoUpdate,
    ClassicConnect,
    ImagesEnabled,
    SizeInputToGame,
    AlwaysOnTop,
    UpdateMapperScripts,
    /// <summary>A built-in tracker toggle changed (spelltimer / showexperience /
    /// showtimetracker) — the host re-syncs each extension's Enabled flag.</summary>
    Trackers,
    /// <summary>A rule-engine master enable changed (highlights / triggers /
    /// substitutes / gags / aliases) — GenieCore re-syncs each engine's
    /// Enabled flag and the File ▸ Master Toggles menu re-reads its checks.</summary>
    MasterToggles,
    /// <summary>The monster-count ignore list changed (Mobs-panel editor or a
    /// typed <c>#config monstercountignorelist</c>) — GenieCore re-filters
    /// Room.Creatures and the Mobs panel reloads its rows.</summary>
    MonsterIgnore,
    /// <summary>Owned-Lich debug-log mirror toggle changed (<c>#config lichdebug</c>)
    /// — the host may start/stop tailing that session's <c>temp/debug-*.log</c>.</summary>
    LichDebug,
    /// <summary>The Objects panel's include-creatures toggle changed (the
    /// panel checkbox or a typed <c>#config objectscreatures</c>) — the panel
    /// rebuilds its rows with creatures shown or filtered out.</summary>
    ObjectsCreatures,
    /// <summary>The Objects panel's config-bar visibility changed (the window's
    /// "Show Config Bar" menu item or a typed <c>#config objectsconfigbar</c>)
    /// — the panel shows or hides its header row.</summary>
    ObjectsConfigBar,
    /// <summary>Automapper room-resolution trace toggle changed
    /// (<c>#config mapperdebug</c>) — GenieCore attaches/detaches the engine's
    /// diagnostic sink.</summary>
    MapperDebug,
    /// <summary>Server-driven dialog master toggle changed
    /// (<c>#config serverdialogs</c>, #156) — the host hides every dialog window
    /// when it goes off and re-renders the mapped ones when it comes back on.</summary>
    ServerDialogs,
    /// <summary>"Always show scrollbars" changed (<c>#config alwaysshowscrollbars</c>,
    /// public #365) — the host mirrors it into display.json, which applies it.</summary>
    AlwaysShowScrollbars
}
