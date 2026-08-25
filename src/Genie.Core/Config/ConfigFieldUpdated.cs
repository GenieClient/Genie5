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
    /// <summary>Automapper room-resolution trace toggle changed
    /// (<c>#config mapperdebug</c>) — GenieCore attaches/detaches the engine's
    /// diagnostic sink.</summary>
    MapperDebug
}
