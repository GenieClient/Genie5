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

    /// <summary>Config layer of each held row (#315), so a dynamic window
    /// that opens late still saves back to the file its row came from.</summary>
    private readonly Dictionary<string, RuleScope> _heldScope = new();

    /// <summary>Rows held for not-yet-registered ids, for the save path.</summary>
    public IReadOnlyCollection<WindowSettingsPersistenceModel> Held => _held.Values;

    /// <summary>
    /// Every row the save path writes — registered windows plus held rows for
    /// ids that have not registered — each with the config layer it saves back
    /// to (public #257/#315). A split save sends Character rows to the
    /// profile's <c>windows.json</c> and Global rows to the shared one.
    /// </summary>
    public IEnumerable<(WindowSettingsPersistenceModel Row, RuleScope Scope)> ScopedRows() =>
        _settings.Values.Select(s => (ToModel(s), s.Scope))
            .Concat(_held.Values
                .Where(h => !_settings.ContainsKey(h.Id))
                .Select(h => (h, _heldScope.TryGetValue(h.Id, out var sc) ? sc : RuleScope.Character)));

    /// <summary>The persisted shape of one window's settings.</summary>
    public static WindowSettingsPersistenceModel ToModel(WindowSettings s) => new()
    {
        Id           = s.Id,
        DisplayTitle = s.DisplayTitle,
        FontFamily   = s.FontFamily,
        FontSize     = s.FontSize,
        Foreground   = s.Foreground,
        Background   = s.Background,
        Timestamp    = s.Timestamp,
        NameListOnly = s.NameListOnly,
        EchoToMain   = s.EchoToMain,
        WordWrap     = s.WordWrap,
        FlashOnActivity = s.FlashOnActivity,
        HideTitleBarWhenAlone = s.HideTitleBarWhenAlone,
        IfClosed     = s.IfClosed,
        HasIfClosed  = true,    // value above is authoritative
        IfClosedRevision = IfClosedRevision,   // public #260 rewrite done
    };

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
    // "" = drop when closed. DR declares the OOC, Conversation and Group
    // windows that way itself (<streamWindow id='ooc' … ifClosed=''/>, and the
    // same for 'conversation' and 'group') because it already sends a bare
    // `main` copy of every such line as the fallback — see DefaultNoEchoToMain.
    private static readonly Dictionary<string, string?> DefaultIfClosed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["talk"]         = "log",
            ["whispers"]     = "log",
            ["ooc"]          = "",
            ["conversation"] = "",
            ["group"]        = "",
        };

    // Windows whose EchoToMain starts OFF. The property defaults to true (Genie
    // 4's per-stream "also show in Main"), which is right for streams DR sends
    // ONCE. OOC is not one of those: DR sends each OOC line on `whispers`, again
    // on `ooc`, and a third time bare on `main` (public #256). The bare copy is
    // already the main-window rendering, so echoing the `ooc` copy on top of it
    // puts the line in Main twice — the very duplicate #256 fixed. DR declares
    // `conversation` and `group` the same way (ifClosed=''), i.e. their text
    // reaches Main on its own, so they start with the echo off too.
    private static readonly HashSet<string> DefaultNoEchoToMain =
        new(StringComparer.OrdinalIgnoreCase) { "ooc", "conversation", "group" };

    /// <summary>
    /// Revision of the persisted <see cref="WindowSettings.IfClosed"/> values.
    /// Written on every saved row; a row below it gets the
    /// <see cref="DeadTargetMigrations"/> rewrite once, on load, and the next
    /// save stamps it current so the rewrite never runs on that row again.
    /// </summary>
    public const int IfClosedRevision = 1;

    /// <summary>
    /// Persisted <see cref="WindowSettings.IfClosed"/> targets that named a
    /// window Genie 5 did not have before revision 1 (public #260), mapped to
    /// what they are rewritten to. An unregistered target falls back to Main
    /// (<see cref="IfClosedResolver"/>'s anti-rot rule), so those values were
    /// dead; registering the real window would silently bring them to life and
    /// move the text into a panel the user has never opened.
    /// <list type="bullet">
    /// <item><c>conversation</c> → <c>log</c>. DR declares talk and whispers
    /// <c>ifClosed='conversation'</c>, and that value reaches windows.json in
    /// the wild (Genie 4 imports, hand edits). Our Log window is the
    /// consolidated conversation feed and <c>log</c> is the shipped talk /
    /// whispers default, so such a row lands on the default.</item>
    /// <item><c>group</c> → <c>null</c> (Main). DR declares nothing that
    /// redirects into Group and no profile seen carries it, but the exposure is
    /// identical; Main is exactly what such a row did before.</item>
    /// </list>
    /// A user who picks Conversation or Group AFTER upgrading saves at the
    /// current revision and keeps the choice.
    /// </summary>
    private static readonly Dictionary<string, string?> DeadTargetMigrations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["conversation"] = "log",
            ["group"]        = null,
        };

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
        var s = Build(id, defaultTitle, defaultFontFamily, defaultFontSize);
        _settings[id] = s;
        if (_held.Remove(id, out var held))
        {
            RuleScope? heldScope = _heldScope.Remove(id, out var hs) ? hs : null;
            Apply(held, heldScope);
        }
        return s;
    }

    /// <summary>
    /// The registration-time defaults for <paramref name="id"/> as a detached
    /// instance, for a Reset. Unlike <see cref="Register"/> this leaves the
    /// store alone: re-registering replaced the live instance, so the open
    /// window (subscribed to the old one) stopped seeing later edits.
    /// </summary>
    public WindowSettings DefaultsFor(string id, string defaultTitle)
    {
        var live = Get(id);
        return Build(id, defaultTitle,
            string.IsNullOrEmpty(live.Id) ? Fallback.FontFamily : live.RegisteredFontFamily,
            string.IsNullOrEmpty(live.Id) ? Fallback.FontSize   : live.RegisteredFontSize);
    }

    private static WindowSettings Build(string id, string defaultTitle,
                                        string defaultFontFamily, double defaultFontSize)
    {
        DefaultIfClosed.TryGetValue(id, out var defIfClosed);
        return new WindowSettings
        {
            Id = id, DefaultTitle = defaultTitle, DisplayTitle = defaultTitle,
            FontFamily = defaultFontFamily,
            FontSize = defaultFontSize, Foreground = "Default", Background = "",
            Timestamp = false, IfClosed = defIfClosed,
            EchoToMain = !DefaultNoEchoToMain.Contains(id),
            RegisteredFontFamily = defaultFontFamily,
            RegisteredFontSize   = defaultFontSize,
        };
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

    /// <summary>Apply a persisted row without changing the window's config
    /// layer (a held row defaults to Character).</summary>
    public void Apply(WindowSettingsPersistenceModel m) => Apply(m, null);

    /// <summary>Apply a persisted row loaded from the <paramref name="scope"/>
    /// layer (#257/#315): the window then saves back to that file.</summary>
    public void Apply(WindowSettingsPersistenceModel m, RuleScope? scope)
    {
        if (string.IsNullOrEmpty(m.Id)) return;
        if (!_settings.TryGetValue(m.Id, out var s))
        {
            // Not registered yet — hold it for Register (see _held). A later
            // load layer (profile over global) replaces an earlier held row,
            // the same precedence a registered window gets.
            _held[m.Id] = m;
            if (scope is { } hs) _heldScope[m.Id] = hs;
            else                 _heldScope.Remove(m.Id);
            return;
        }
        if (scope is { } sc) s.Scope = sc;

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
        s.HideTitleBarWhenAlone = m.HideTitleBarWhenAlone;
        if (m.HasIfClosed)
            s.IfClosed = m.IfClosedRevision < IfClosedRevision
                         && m.IfClosed is { } target
                         && DeadTargetMigrations.TryGetValue(target.Trim(), out var migrated)
                ? migrated
                : m.IfClosed;
    }
}
