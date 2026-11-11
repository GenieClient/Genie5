namespace Genie.Core;

/// <summary>
/// What produced a locally echoed line (public #406). Each maps to the Genie 4
/// preset that colours it — see <see cref="EchoKindPresets"/>.
/// </summary>
public enum EchoKind
{
    /// <summary>A command the player typed (Genie 4 <c>inputuser</c>).</summary>
    Command,
    /// <summary>A game command a script sent (Genie 4 <c>inputother</c>).</summary>
    ScriptCommand,
    /// <summary>Script output: <c>echo</c>, status and debug lines (Genie 4 <c>scriptecho</c>).</summary>
    ScriptEcho,
    /// <summary>Genie's own messages and diagnostics (Genie 4 <c>scriptecho</c>).</summary>
    Message,
}

/// <summary>The preset id each <see cref="EchoKind"/> is coloured with.</summary>
public static class EchoKindPresets
{
    public const string InputUser  = "inputuser";
    public const string InputOther = "inputother";
    public const string ScriptEcho = "scriptecho";

    public static string PresetId(this EchoKind kind) => kind switch
    {
        EchoKind.Command       => InputUser,
        EchoKind.ScriptCommand => InputOther,
        _                      => ScriptEcho,
    };
}

/// <summary>The Genie 4 presets that colour the vitals bars (public #406):
/// foreground = the bar's fill, background = its track and border, as in Genie 4
/// (<c>FormMain.cs</c>, <c>ComponentBarsHealth.ForegroundColor = PresetList["health"]…</c>).</summary>
public static class VitalsPresets
{
    public const string Health        = "health";
    public const string Mana          = "mana";
    public const string Stamina       = "stamina";
    public const string Spirit        = "spirit";
    public const string Concentration = "concentration";

    public static readonly string[] All = { Health, Mana, Stamina, Spirit, Concentration };
}

/// <summary>
/// Genie 4's window-chrome presets (public #406). In Genie 4 they coloured the
/// menu bar and its menus, the command input, the status strip and the
/// AutoMapper Settings window. In Genie 5 the theme colours those surfaces; a
/// <c>ui.*</c> preset set to a colour paints its surface over the theme, and
/// "Default" (the stock value) leaves the theme's look.
/// </summary>
public static class UiPresets
{
    public const string Menu          = "ui.menu";
    public const string MenuHighlight = "ui.menu.highlight";
    public const string MenuChecked   = "ui.menu.checked";
    public const string TextBox       = "ui.textbox";
    public const string Status        = "ui.status";
    public const string Window        = "ui.window";
    public const string Button        = "ui.button";

    public static readonly string[] All = { Menu, MenuHighlight, MenuChecked, TextBox, Status, Window, Button };
}
