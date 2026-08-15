namespace Genie.Core.Layout;

/// <summary>
/// Per-window display + routing settings (one instance per registered
/// dockable: Game, Vitals, Room, Backpack, Logons, Talk, …). Edited via the
/// Configuration → Layout tab; persisted per-profile.
///
/// <para>
/// <b>Sentinel-based fallback semantics (Option A):</b> when the user
/// hasn't explicitly overridden a field, the field carries a sentinel
/// value and the consuming code falls back to the global
/// <c>DisplaySettings</c> (or the corresponding
/// <c>Application.Resources</c> key) at render time. Sentinels are:
/// </para>
/// <list type="bullet">
///   <item><see cref="FontFamily"/> = <c>""</c> (empty) → use
///         <c>DisplaySettings.FontFamily</c>.</item>
///   <item><see cref="FontSize"/> = <c>0</c> (non-positive) → use
///         <c>DisplaySettings.FontSize</c>.</item>
///   <item><see cref="Foreground"/> = <c>"Default"</c> or empty → use
///         <c>DisplaySettings.GameColorHex</c>.</item>
///   <item><see cref="Background"/> = <c>""</c> (empty) or <c>"(none)"</c>
///         → transparent (the historical "None" semantic — no
///         global-fallback for background, by design).</item>
/// </list>
/// Resolution is centralised in <c>Genie.App.Controls.WindowSettingsResolver</c>.
/// </summary>
public sealed class WindowSettings
{
    public string  Id           { get; init; } = "";
    public string  DefaultTitle { get; init; } = "";
    public string  DisplayTitle { get; set; } = "";

    /// <summary>Empty string = use global default (sentinel).</summary>
    public string  FontFamily   { get; set; } = "Cascadia Mono,Consolas,Courier New,monospace";

    /// <summary>Non-positive value = use global default (sentinel).</summary>
    public double  FontSize     { get; set; } = 13;

    /// <summary>"Default" or empty = use global default (sentinel).</summary>
    public string  Foreground   { get; set; } = "Default";

    /// <summary>Empty string = transparent (the "None" semantic).</summary>
    public string  Background   { get; set; } = "";

    public bool    Timestamp    { get; set; } = false;

    /// <summary>
    /// Genie 4 "Name List Only" — when true the window only displays lines that
    /// mention a name in the player's Names list (matched via
    /// <c>NameHighlightEngine</c>). A live view filter applied at append time;
    /// scrollback already shown is not retroactively hidden. Toggled from the
    /// window right-click menu and persisted per-window.
    /// </summary>
    public bool    NameListOnly { get; set; } = false;

    /// <summary>
    /// Genie 4 "also show in Main" — when true, lines routed to this stream
    /// window are <b>additionally</b> echoed into the main game window (the
    /// stream's own panel keeps showing them too). <b>On by default</b>: every
    /// stream mirrors into Main even when its own window is open, so nothing is
    /// hidden from the main feed until the user opts a stream <i>out</i> (via the
    /// window right-click menu or the Layout tab). Only consulted for the text
    /// stream windows (Combat, Talk, Whispers, Thoughts, Familiar, Deaths,
    /// Logons, Assess, Atmospherics, OOC, ItemLog); a no-op for other dockables.
    /// Distinct from <see cref="IfClosed"/>, which only fires when the panel
    /// is <i>closed</i>.
    /// <para>
    /// The default is per-window: <c>ooc</c> ships with it OFF, because DR sends
    /// every OOC line to <c>main</c> itself in addition to the stream copy
    /// (public #256) — echoing on top of that shows it twice. See
    /// <c>WindowSettingsStore.DefaultNoEchoToMain</c>.
    /// </para>
    /// </summary>
    public bool    EchoToMain   { get; set; } = true;

    /// <summary>
    /// Per-window word wrap (#120). On (the default) long lines wrap to the
    /// window width — the shipped behaviour. Off, each line stays on one row
    /// and the window scrolls horizontally; useful for column-aligned output
    /// (inventory tables, score sheets). Toggled from the window right-click
    /// menu and persisted per-window.
    /// </summary>
    public bool    WordWrap     { get; set; } = true;

    public string? IfClosed     { get; set; }
    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();
}
