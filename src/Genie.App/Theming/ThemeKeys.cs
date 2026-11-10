namespace Genie.App.Theming;

/// <summary>
/// Semantic resource keys for the app-wide theme palette (#20). Every
/// paintable chrome surface in AXAML references one of these via
/// <c>{DynamicResource Theme.*}</c>; <see cref="ThemeService"/> pushes a
/// <c>SolidColorBrush</c> per key into <c>Application.Resources</c> when a
/// theme is applied, so the whole UI repaints live — same mechanism as
/// <c>DisplaySettings.Apply</c> uses for <c>GameBrush</c>.
///
/// <para>Deliberately a SMALL set (~25 roles, not one key per widget):
/// themes stay authorable by hand, and new UI picks an existing role
/// instead of minting a new colour. One-off accent chips (stance badges,
/// status-effect chips) intentionally keep their hardcoded colours — they
/// are data-encoding colours, not chrome.</para>
/// </summary>
public static class ThemeKeys
{
    // ── Surfaces ──────────────────────────────────────────────────────────
    /// <summary>Main / MDI window background.</summary>
    public const string WindowBg     = "Theme.WindowBg";
    /// <summary>Default panel surface (tool panels, flyouts, dialogs).</summary>
    public const string PanelBg      = "Theme.PanelBg";
    /// <summary>Recessed wells: log output, raw-XML dump, script output.</summary>
    public const string PanelBgDeep  = "Theme.PanelBgDeep";
    /// <summary>Toolbars and secondary bars above/below content.</summary>
    public const string ToolbarBg    = "Theme.ToolbarBg";
    /// <summary>Full-width strips: hands strip, command bar, status strips.</summary>
    public const string StripBg      = "Theme.StripBg";

    // ── Lines ─────────────────────────────────────────────────────────────
    /// <summary>Borders, separators, outlines everywhere.</summary>
    public const string Border       = "Theme.Border";

    // ── Text ──────────────────────────────────────────────────────────────
    /// <summary>Primary readable text (titles, values).</summary>
    public const string TextPrimary  = "Theme.TextPrimary";
    /// <summary>Secondary text (labels, descriptions).</summary>
    public const string TextSecondary = "Theme.TextSecondary";
    /// <summary>De-emphasised text (hints, placeholders, paths).</summary>
    public const string TextMuted    = "Theme.TextMuted";
    /// <summary>ALL-CAPS section headers (the #7a92a8 steel-blue family).</summary>
    public const string SectionHeader = "Theme.SectionHeader";

    // ── Accents ───────────────────────────────────────────────────────────
    /// <summary>Interactive / informational accent (links, exits, compass).</summary>
    public const string Accent       = "Theme.Accent";
    /// <summary>Background for accent-tinted chips/buttons.</summary>
    public const string AccentBg     = "Theme.AccentBg";
    /// <summary>Border for accent-tinted chips/buttons.</summary>
    public const string AccentBorder = "Theme.AccentBorder";
    /// <summary>Positive: players, git-managed badge, success notes.</summary>
    public const string Success      = "Theme.Success";
    public const string SuccessBg    = "Theme.SuccessBg";
    /// <summary>Attention: mobs, stale-data badges, unsaved dots.</summary>
    public const string Warning      = "Theme.Warning";
    public const string WarningBg    = "Theme.WarningBg";
    /// <summary>Errors and destructive actions.</summary>
    public const string Danger       = "Theme.Danger";
    public const string DangerBg     = "Theme.DangerBg";

    // ── Misc ──────────────────────────────────────────────────────────────
    /// <summary>Text-selection highlight (semi-transparent).</summary>
    public const string Selection    = "Theme.Selection";

    // ── Vitals bars ───────────────────────────────────────────────────────
    public const string HealthBar    = "Theme.HealthBar";
    public const string ManaBar      = "Theme.ManaBar";
    public const string SpiritBar    = "Theme.SpiritBar";
    public const string StaminaBar   = "Theme.StaminaBar";
    public const string ConcBar      = "Theme.ConcBar";

    // ── Game-text defaults (routed to DisplaySettings, not resources) ─────
    // Applying a theme seeds GameColorHex from this; the
    // user's later Display Settings tweaks (and per-stream presets) always
    // win — a theme only writes them at explicit apply time, never at
    // startup re-apply (#20 "per-stream color overrides win").
    public const string GameText     = "Theme.GameText";

    // ── Echo colours (routed to the Genie 4 presets, public #406) ──────────
    // Applying a theme writes these into the inputuser / inputother /
    // scriptecho presets, which colour echo lines. Same apply-time-only rule
    // as GameText.
    public const string InputUser    = "Theme.InputUser";
    public const string InputOther   = "Theme.InputOther";
    public const string ScriptEcho   = "Theme.ScriptEcho";

    /// <summary>Legacy single echo colour (before #406). Read only: a custom
    /// theme file that has it but none of the three echo keys applies it to
    /// all three presets, so the theme keeps its look.</summary>
    public const string GameEcho     = "Theme.GameEcho";

    /// <summary>The echo theme keys and the preset each one sets.</summary>
    public static readonly (string Key, string PresetId)[] EchoPresetKeys =
    {
        (InputUser,  Genie.Core.EchoKindPresets.InputUser),
        (InputOther, Genie.Core.EchoKindPresets.InputOther),
        (ScriptEcho, Genie.Core.EchoKindPresets.ScriptEcho),
    };

    /// <summary>
    /// Every key that maps to an <c>Application.Resources</c> brush, in
    /// display order for editors. <see cref="GameText"/> and the echo keys are
    /// excluded — they route through <c>DisplaySettings</c> and the presets.
    /// </summary>
    public static readonly string[] BrushKeys =
    {
        WindowBg, PanelBg, PanelBgDeep, ToolbarBg, StripBg,
        Border,
        TextPrimary, TextSecondary, TextMuted, SectionHeader,
        Accent, AccentBg, AccentBorder,
        Success, SuccessBg, Warning, WarningBg, Danger, DangerBg,
        Selection,
        HealthBar, ManaBar, SpiritBar, StaminaBar, ConcBar,
    };
}
