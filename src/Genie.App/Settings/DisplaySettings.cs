using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.Settings;

/// <summary>
/// User-tweakable visual settings for the game text display. Stored as JSON
/// in the app's <c>Config\display.json</c> file.
///
/// Property changes push immediately into <see cref="Application.Resources"/>
/// so any control bound via <c>DynamicResource</c> repaints live without
/// needing the data layer to raise per-line property-changed notifications.
/// </summary>
public sealed class DisplaySettings : ReactiveObject
{
    // ── Resource keys (referenced from AXAML via {DynamicResource ...}) ───────

    public const string GameBrushKey      = "GameBrush";
    public const string EchoBrushKey      = "EchoBrush";
    public const string GameFontFamilyKey = "GameFontFamily";
    public const string GameFontSizeKey   = "GameFontSize";
    public const string EchoFontStyleKey  = "EchoFontStyle";

    // ── Persisted properties (stored as hex strings / primitives) ─────────────

    [Reactive] public string GameColorHex  { get; set; } = "#CCCCCC";
    [Reactive] public string EchoColorHex  { get; set; } = "#88BBCC";
    [Reactive] public bool   EchoItalic    { get; set; } = true;
    [Reactive] public string FontFamily    { get; set; } = "Consolas,Courier New,monospace";
    [Reactive] public double FontSize      { get; set; } = 13d;

    /// <summary>
    /// Background colour of the Mapper canvas. User-editable via a
    /// ColorPickerButton in the Mapper panel's Details expander. Default
    /// matches the rest of the dark theme; users who prefer the cream/tan
    /// Genie 4 AutoMapper aesthetic can pick something like #F5EFD8 and the
    /// canvas updates live.
    /// </summary>
    [Reactive] public string MapBackgroundHex { get; set; } = "#1A1A1A";

    /// <summary>
    /// Whether the Wrayth-style horizontal status bar (health/mana/stamina/spirit/
    /// concentration) is shown below the command bar. Independent of the dockable
    /// <c>VitalsTool</c>, so you can have both, either, or neither.
    /// </summary>
    [Reactive] public bool   ShowStatusBar { get; set; } = true;

    /// <summary>
    /// Whether the optional zone/room location line (the mapper's current zone
    /// + <c>$roomid</c>) is shown at the bottom of the window (#66). Opt-in (off
    /// by default) — handy for travel/scripting so you can always see where you
    /// are without opening the Mapper.
    /// </summary>
    [Reactive] public bool   ShowZoneRoomId { get; set; }

    /// <summary>
    /// For the zone/room location line (#66): when <c>true</c> the Zone field
    /// shows the numeric <c>$zoneid</c> (e.g. "33"); when <c>false</c> (default)
    /// it shows the zone name (e.g. "Riverhaven West Gate"). The Room field is
    /// always <c>$roomid</c>. Only meaningful when <see cref="ShowZoneRoomId"/>
    /// is on.
    /// </summary>
    [Reactive] public bool   ZoneRoomShowNumber { get; set; }

    /// <summary>
    /// Windowed (MDI) document mode — every panel becomes a free-floating
    /// child window inside the main window, à la Genie 4, instead of the
    /// tabbed/docked layout.
    /// <para>
    /// Deliberately <see cref="JsonIgnore"/>: the mode is NOT auto-persisted
    /// across restarts. It's session/layout state only — fresh launches start
    /// tabbed, and the windowed choice (with its per-window geometry) rides on
    /// a saved <see cref="SavedLayout"/> instead. Loading a layout sets this;
    /// the Window-menu toggle flips it for the current session.
    /// </para>
    /// </summary>
    [JsonIgnore]
    [Reactive] public bool   WindowedMode { get; set; }

    /// <summary>
    /// Name of the global layout preset auto-applied on connect when the
    /// connected profile has no <c>DefaultLayoutName</c> of its own (and for
    /// bare-credential connections). Empty means no global default — fall back
    /// to the built-in layout.
    /// </summary>
    [Reactive] public string GlobalDefaultLayout { get; set; } = string.Empty;

    /// <summary>
    /// Whether the character's guild is appended to the window title
    /// ("Genie 5 — Connected — Name — Guild"). Off hides the guild slot even
    /// when a guild is known. Default on.
    /// </summary>
    [Reactive] public bool ShowGuildInTitle { get; set; } = true;

    /// <summary>
    /// Whether the Wrayth-style hands / prepared-spell strip is shown at all.
    /// Position (top vs. bottom) is controlled by <see cref="HandsAtBottom"/>.
    /// </summary>
    [Reactive] public bool   ShowHandsBar  { get; set; } = true;

    /// <summary>
    /// When the hands strip is visible, should it sit at the BOTTOM of the
    /// window (between the command bar and the vitals strip — Genie 4 style)
    /// or at the TOP (just below the menu bar). Default <c>true</c> = bottom,
    /// matching Genie 4 muscle memory. Toggle via Window → Hands Strip Position.
    /// </summary>
    [Reactive] public bool   HandsAtBottom { get; set; } = true;

    /// <summary>True iff the hands strip is positioned at the top.</summary>
    [JsonIgnore] public bool HandsAtTop      => !HandsAtBottom;

    /// <summary>True iff the hands strip is visible AND positioned at the top.</summary>
    [JsonIgnore] public bool ShowHandsTop    => ShowHandsBar && !HandsAtBottom;

    /// <summary>True iff the hands strip is visible AND positioned at the bottom.</summary>
    [JsonIgnore] public bool ShowHandsBottom => ShowHandsBar &&  HandsAtBottom;

    /// <summary>
    /// Toggles between two visual styles for the hands / status strip.
    /// <para>
    /// <c>false</c> (default) — the original Genie 5 "classic" strip: text
    /// L/R hand labels, six-badge text stance row, and an inline RT badge.
    /// </para>
    /// <para>
    /// <c>true</c> — the "enhanced" strip derived from dylb0t's Genie 5
    /// fork (icon set donated under GPL-3.0, see CREDITS.md). Adds the
    /// pixel-art compass rose, the posture sprite, and the status-effect
    /// icon strip alongside the existing hand / spell / stance widgets.
    /// </para>
    /// Toggled via <c>Window → Enhanced Hands Strip</c>. Persists per-user.
    /// </summary>
    [Reactive] public bool   UseEnhancedHandsStrip { get; set; }

    /// <summary>True iff the hands strip is visible at the top AND classic style.</summary>
    [JsonIgnore] public bool ShowHandsTopClassic     => ShowHandsTop    && !UseEnhancedHandsStrip;
    /// <summary>True iff the hands strip is visible at the top AND enhanced style.</summary>
    [JsonIgnore] public bool ShowHandsTopEnhanced    => ShowHandsTop    &&  UseEnhancedHandsStrip;
    /// <summary>True iff the hands strip is visible at the bottom AND classic style.</summary>
    [JsonIgnore] public bool ShowHandsBottomClassic  => ShowHandsBottom && !UseEnhancedHandsStrip;
    /// <summary>True iff the hands strip is visible at the bottom AND enhanced style.</summary>
    [JsonIgnore] public bool ShowHandsBottomEnhanced => ShowHandsBottom &&  UseEnhancedHandsStrip;

    /// <summary>
    /// When the character is in roundtime, where should the "⏱ N.Ns" badge
    /// render? <c>false</c> (default) = inline with the command bar at the
    /// bottom of the window, matching Genie 4's input row. <c>true</c> = as
    /// an inline sub-group on the hands strip alongside L/R/S, so all the
    /// "what's locked right now" indicators live together.
    /// </summary>
    [Reactive] public bool   RoundTimeOnHandsStrip { get; set; }

    /// <summary>Convenience inverse for radio-button binding in the Window menu.</summary>
    [JsonIgnore] public bool RoundTimeOnCommandBar => !RoundTimeOnHandsStrip;

    // ── Per-tag visibility filters (Window → Game Window menu) ────────────
    // Each flag gates one class of line in the main Game window:
    //   ShowGameText   — server-emitted text (room descriptions, combat, NPC speech, …)
    //   ShowEchoText   — typed commands echoed back ("> look", "> n", etc.)
    //   ShowScriptText — script-lifecycle + diagnostic lines ([script] X done, [recorder] …)
    // Defaults to all-visible. The filters run at the GameTextViewModel
    // subscription site, so disabling a class hides BOTH new lines and
    // (after a re-tokenize pass) already-rendered ones.
    [Reactive] public bool ShowGameText   { get; set; } = true;
    [Reactive] public bool ShowEchoText   { get; set; } = true;
    [Reactive] public bool ShowScriptText { get; set; } = true;

    /// <summary>
    /// Absolute path to the user's preferred text editor for "Edit Script"
    /// actions (the pencil button in the Script Bar, plus the
    /// <c>#edit &lt;name&gt;</c> command). When empty (the default) we fall
    /// back to the OS default `.cmd`-file handler via shell launch — usually
    /// Notepad on Windows, TextEdit on macOS, the system default on Linux.
    /// <para>
    /// Genie 4 stored this as <c>editor</c> in settings.cfg; same field,
    /// JSON-serialised. Typical values: <c>C:\Program Files\Notepad++\notepad++.exe</c>,
    /// <c>/usr/bin/code</c>, <c>/Applications/Visual Studio Code.app</c>.
    /// </para>
    /// </summary>
    [Reactive] public string EditorPath { get; set; } = "";

    /// <summary>
    /// True once the user has clicked "Don't ask again" on the Mapper's
    /// "Fetch your skills?" prompt. The prompt suggests running `skills`
    /// so the skill-weighted pathfinder has data to filter exits with;
    /// some users don't care and want it to stop nagging. Persisted so
    /// the choice survives restarts.
    /// </summary>
    [Reactive] public bool SkillsPromptDismissed { get; set; } = false;

    // ── Wiring: push into Application.Resources whenever anything changes ────

    [JsonIgnore]
    public bool IsApplied { get; private set; }

    /// <summary>
    /// Hook this once at app startup. Initial values are pushed to resources,
    /// then any later property change is propagated automatically.
    /// </summary>
    public void Apply()
    {
        PushAll();
        this.WhenAnyValue(
                x => x.GameColorHex,
                x => x.EchoColorHex,
                x => x.EchoItalic,
                x => x.FontFamily,
                x => x.FontSize)
            .Subscribe(_ =>
            {
                PushAll();
                // Force already-rendered lines to repaint with the new
                // values. Each TextLine bakes its brushes into Avalonia
                // `Run` inlines at tokenization time, so a change to
                // GameBrush / GameFontFamily / GameFontSize doesn't
                // retroactively recolour existing inlines. Re-running
                // tokenization rebuilds every line with fresh brushes /
                // fonts derived from the new resources.
                //
                // Piggybacks on the highlight pipeline's existing
                // notification — GameTextViewModel already subscribes to
                // UserHighlights.RulesChanged → RetokenizeAllLines.
                // Reusing that path avoids a parallel event bus for
                // settings-driven repaints.
                Highlighting.UserHighlights.NotifyRulesChanged();
            });

        // The hands-strip visibility/position bools are computed properties
        // (JsonIgnore — derived from ShowHandsBar + HandsAtBottom). ReactiveUI's
        // [Reactive] only fires PropertyChanged for the underlying field, so the
        // derived getters won't refresh their bindings on their own. Re-raise
        // PropertyChanged for the derivatives whenever either underlying flips.
        this.WhenAnyValue(x => x.ShowHandsBar, x => x.HandsAtBottom, x => x.UseEnhancedHandsStrip)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(HandsAtTop));
                this.RaisePropertyChanged(nameof(ShowHandsTop));
                this.RaisePropertyChanged(nameof(ShowHandsBottom));
                this.RaisePropertyChanged(nameof(ShowHandsTopClassic));
                this.RaisePropertyChanged(nameof(ShowHandsTopEnhanced));
                this.RaisePropertyChanged(nameof(ShowHandsBottomClassic));
                this.RaisePropertyChanged(nameof(ShowHandsBottomEnhanced));
            });

        // RoundTimeOnCommandBar is JsonIgnore + derived, so it doesn't fire on
        // its own. Re-raise PropertyChanged for it whenever the underlying
        // RoundTimeOnHandsStrip flips so the menu's command-bar radio button
        // refreshes too.
        this.WhenAnyValue(x => x.RoundTimeOnHandsStrip)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(RoundTimeOnCommandBar)));
        IsApplied = true;
    }

    private void PushAll()
    {
        var res = Application.Current?.Resources;
        if (res is null) return;

        res[GameBrushKey]      = MakeBrush(GameColorHex, fallback: Colors.LightGray);
        res[EchoBrushKey]      = MakeBrush(EchoColorHex, fallback: Color.FromRgb(0x88, 0xbb, 0xcc));
        res[GameFontFamilyKey] = new FontFamily(FontFamily);
        res[GameFontSizeKey]   = FontSize;
        res[EchoFontStyleKey]  = EchoItalic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;
    }

    private static IBrush MakeBrush(string hex, Color fallback)
        => new SolidColorBrush(Color.TryParse(hex, out var c) ? c : fallback);

    // ── JSON persistence ──────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DisplaySettings Load(string path)
    {
        if (!File.Exists(path)) return new DisplaySettings();
        try
        {
            return JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(path), Json)
                   ?? new DisplaySettings();
        }
        catch
        {
            return new DisplaySettings();
        }
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
