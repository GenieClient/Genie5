using Genie.Core.Persistence;

namespace Genie.Core.Layout;

public sealed class WindowSettingsStore
{
    private readonly Dictionary<string, WindowSettings> _settings = new();
    public IReadOnlyDictionary<string, WindowSettings> All => _settings;

    /// <summary>
    /// Persisted rows for ids nobody has registered yet this session — the
    /// dynamic windows (server dialogs, plugin windows) that only register when
    /// the server or a plugin first opens them, which is after windows.json
    /// loads. <see cref="Register"/> applies a held row the moment its id
    /// arrives, and the save writes held rows back out, so a window that simply
    /// has not appeared yet this session keeps its fonts instead of losing them
    /// at the next save. Before this they were dropped on load, which is why
    /// per-dialog fonts were session-only.
    /// </summary>
    private readonly Dictionary<string, WindowSettingsPersistenceModel> _held = new();

    /// <summary>Rows held for not-yet-registered ids, for the save path.</summary>
    public IReadOnlyCollection<WindowSettingsPersistenceModel> Held => _held.Values;

    public WindowSettings Get(string id) => _settings.TryGetValue(id, out var s) ? s : Fallback;

    private static readonly WindowSettings Fallback = new()
    {
        Id = "", DefaultTitle = "", DisplayTitle = "",
        FontFamily = "Cascadia Mono,Consolas,Courier New,monospace",
        FontSize = 13, Foreground = "Default", Background = "", Timestamp = false, IfClosed = null,
    };

    // Per-window IfClosed defaults, keyed by REGISTERED window id (public #211).
    // Any id absent here defaults to null = "route to Main when closed", so we
    // only list the streams that want a different target. The Log window is our
    // consolidated conversation feed (id "log", Genie 4 "conversation" parity),
    // and talk/whispers are already mirrored into it at StreamTabsViewModel.
    // Values must be registered ids (or the "main"/game-text main-window target)
    // or the IfClosedResolver treats them as unknown and safely routes to Main.
    //
    // "" = drop when closed. DR declares the OOC window that way itself
    // (<streamWindow id='ooc' … ifClosed=''/>) because it already sends a bare
    // `main` copy of every OOC line as the fallback — see DefaultNoEchoToMain.
    private static readonly Dictionary<string, string?> DefaultIfClosed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["talk"]     = "log",
            ["whispers"] = "log",
            ["ooc"]      = "",
        };

    // Windows whose EchoToMain starts OFF. The property defaults to true (Genie
    // 4's per-stream "also show in Main"), which is right for streams DR sends
    // ONCE. OOC is not one of those: DR sends each OOC line on `whispers`, again
    // on `ooc`, and a third time bare on `main` (public #256). The bare copy is
    // already the main-window rendering, so echoing the `ooc` copy on top of it
    // puts the line in Main twice — the very duplicate #256 fixed.
    private static readonly HashSet<string> DefaultNoEchoToMain =
        new(StringComparer.OrdinalIgnoreCase) { "ooc" };

    public WindowSettings Register(string id, string defaultTitle)
        => Register(id, defaultTitle, "Cascadia Mono,Consolas,Courier New,monospace", 13);

    /// <summary>
    /// Register with a window-specific default font — for a panel that is not
    /// a monospaced text stream (the server dialogs render proportional
    /// controls, so a monospace default would change how they look the first
    /// time anyone opened Configuration → Layout). A row persisted for this id
    /// and held since load wins over the defaults.
    /// </summary>
    public WindowSettings Register(string id, string defaultTitle,
                                   string defaultFontFamily, double defaultFontSize)
    {
        DefaultIfClosed.TryGetValue(id, out var defIfClosed);
        var s = new WindowSettings
        {
            Id = id, DefaultTitle = defaultTitle, DisplayTitle = defaultTitle,
            FontFamily = defaultFontFamily,
            FontSize = defaultFontSize, Foreground = "Default", Background = "",
            Timestamp = false, IfClosed = defIfClosed,
            EchoToMain = !DefaultNoEchoToMain.Contains(id),
        };
        _settings[id] = s;
        if (_held.Remove(id, out var held)) Apply(held);
        return s;
    }

    /// <summary>
    /// Window ids whose default title changed across versions, mapped to the
    /// <b>old</b> shipped title. A persisted <see cref="WindowSettings.DisplayTitle"/>
    /// still equal to the old default is treated as "unset" on load so the
    /// window picks up its new <see cref="WindowSettings.DefaultTitle"/>. A user
    /// who set a genuinely custom title (anything else) is left untouched.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["backpack"] = "Backpack",   // → "Inventory" (2026-06)
            ["scripts"]  = "Scripts",    // → "Script Manager" (public #197)
        };

    public void Apply(WindowSettingsPersistenceModel m)
    {
        if (string.IsNullOrEmpty(m.Id)) return;
        if (!_settings.TryGetValue(m.Id, out var s))
        {
            // Not registered yet — hold it for Register (see _held). A later
            // load layer (profile over global) replaces an earlier held row,
            // the same precedence a registered window gets.
            _held[m.Id] = m;
            return;
        }

        // Rename migration: if the saved title is still the old shipped
        // default for a since-renamed window, drop it so the new DefaultTitle
        // wins below. Idempotent — runs harmlessly every load, and a custom
        // title won't match; the next SaveWindowSettings persists the new name.
        var displayTitle = m.DisplayTitle;
        if (RenamedDefaults.TryGetValue(m.Id, out var oldDefault) &&
            string.Equals(displayTitle, oldDefault, StringComparison.Ordinal))
            displayTitle = string.Empty;

        s.DisplayTitle = string.IsNullOrEmpty(displayTitle) ? s.DefaultTitle : displayTitle;

        // Accept sentinel values verbatim — empty string for FontFamily and
        // non-positive for FontSize both mean "use the global default" per
        // the Option A architecture. The previous behaviour of substituting
        // the per-window default for empty meant explicitly checking
        // "Use default" in the Configuration → Layout panel was a no-op
        // (the sentinel got overwritten on every load with the hardcoded
        // default). See WindowSettings.cs for the full sentinel table.
        s.FontFamily = m.FontFamily   ?? string.Empty;
        s.FontSize   = m.FontSize;
        s.Foreground = string.IsNullOrEmpty(m.Foreground) ? s.Foreground : m.Foreground;
        s.Background = m.Background;
        s.Timestamp  = m.Timestamp;
        s.NameListOnly = m.NameListOnly;
        s.EchoToMain = m.EchoToMain;
        s.WordWrap   = m.WordWrap;
        s.FlashOnActivity = m.FlashOnActivity;
        if (m.HasIfClosed) s.IfClosed = m.IfClosed;
    }
}
