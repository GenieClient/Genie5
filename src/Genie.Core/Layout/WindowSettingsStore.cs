using Genie.Core.Persistence;

namespace Genie.Core.Layout;

public sealed class WindowSettingsStore
{
    private readonly Dictionary<string, WindowSettings> _settings = new();
    public IReadOnlyDictionary<string, WindowSettings> All => _settings;

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
    private static readonly Dictionary<string, string?> DefaultIfClosed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["talk"]     = "log",
            ["whispers"] = "log",
        };

    public WindowSettings Register(string id, string defaultTitle)
    {
        DefaultIfClosed.TryGetValue(id, out var defIfClosed);
        var s = new WindowSettings
        {
            Id = id, DefaultTitle = defaultTitle, DisplayTitle = defaultTitle,
            FontFamily = "Cascadia Mono,Consolas,Courier New,monospace",
            FontSize = 13, Foreground = "Default", Background = "",
            Timestamp = false, IfClosed = defIfClosed,
        };
        _settings[id] = s;
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
        if (!_settings.TryGetValue(m.Id, out var s)) return;

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
        if (m.HasIfClosed) s.IfClosed = m.IfClosed;
    }
}
