using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.Core.Config;

namespace Genie.App.Views;

/// <summary>
/// Script-engine settings editor (the "Scripts" tab of the Configuration
/// dialog). Exposes the script-related fields of <see cref="GenieConfig"/> —
/// script/command characters, timeout, GoSub depth, connect script, etc. —
/// which previously had no UI and could only be changed by hand-editing
/// <c>settings.cfg</c>.
///
/// <para>Unlike the rule-engine tabs (triggers, aliases, …) these settings
/// live on the global <see cref="GenieConfig"/>, not per-profile, so the panel
/// is profile-independent. It binds directly to the live config object and
/// invokes the persistence callback (which writes <c>settings.cfg</c>) on
/// Apply. Follows LayoutPanel's named-control + Apply/Reset idiom.</para>
/// </summary>
public partial class ScriptsPanel : UserControl
{
    private GenieConfig? _config;
    private Action?      _onChanged;

    public ScriptsPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Hand the panel the live <see cref="GenieConfig"/> plus a callback that
    /// persists it. A null config means there's no connected core yet — the
    /// form disables itself and shows a hint, mirroring how the other panels
    /// stay empty when their engine isn't available.
    /// </summary>
    public void Initialize(GenieConfig? config, Action? onChanged = null)
    {
        _config    = config;
        _onChanged = onChanged;

        IsEnabled = config is not null;
        if (config is null)
        {
            StatusText.Text = "Connect to a game first — script settings load with the session.";
            return;
        }

        LoadForm(config);
        StatusText.Text = string.Empty;
    }

    private void LoadForm(GenieConfig c)
    {
        ScriptCharBox.Text       = c.ScriptChar.ToString();
        ScriptExtensionBox.Text  = c.ScriptExtension;
        ScriptTimeoutBox.Text    = c.ScriptTimeout.ToString();
        MaxGoSubDepthBox.Text     = c.MaxGoSubDepth.ToString();
        AbortDupeScriptCheck.IsChecked = c.AbortDupeScript;
        ConnectScriptBox.Text    = c.ConnectScript;

        SeparatorCharBox.Text    = c.SeparatorChar.ToString();
        CommandCharBox.Text      = c.CommandChar.ToString();
        TriggerOnInputCheck.IsChecked = c.TriggerOnInput;

        ScriptDirBox.Text        = c.ScriptDirRaw;
        RepoScriptDirBox.Text    = c.RepoScriptDirRaw;
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_config is null) return;

        // ── Validate the single-character fields. Each must be exactly one
        //    non-whitespace character; the TextBoxes are MaxLength=1 so the
        //    only failure modes are "empty" or "whitespace". ────────────────
        if (!TryReadChar(ScriptCharBox.Text, out var scriptChar))
        { StatusText.Text = "Script character must be a single character."; return; }
        if (!TryReadChar(SeparatorCharBox.Text, out var separatorChar))
        { StatusText.Text = "Command separator must be a single character."; return; }
        if (!TryReadChar(CommandCharBox.Text, out var commandChar))
        { StatusText.Text = "Genie command character must be a single character."; return; }

        // ── Validate the integer fields. ───────────────────────────────────
        if (!int.TryParse(ScriptTimeoutBox.Text?.Trim(), out var timeout) || timeout < 0)
        { StatusText.Text = "Script timeout must be a non-negative whole number of milliseconds."; return; }
        if (!int.TryParse(MaxGoSubDepthBox.Text?.Trim(), out var depth) || depth < 1)
        { StatusText.Text = "Max GoSub depth must be a whole number of 1 or more."; return; }

        // ── Commit to the live config. Blank text/dir fields fall back to the
        //    same defaults SetSetting() uses so we never persist an empty
        //    extension or script directory. ─────────────────────────────────
        _config.ScriptChar      = scriptChar;
        _config.SeparatorChar   = separatorChar;
        _config.CommandChar     = commandChar;
        _config.ScriptTimeout   = timeout;
        _config.MaxGoSubDepth   = depth;
        _config.AbortDupeScript = AbortDupeScriptCheck.IsChecked == true;
        _config.TriggerOnInput  = TriggerOnInputCheck.IsChecked == true;

        var ext = ScriptExtensionBox.Text?.Trim();
        _config.ScriptExtension = string.IsNullOrWhiteSpace(ext) ? "cmd" : ext;
        _config.ConnectScript   = ConnectScriptBox.Text?.Trim() ?? string.Empty;

        var dir = ScriptDirBox.Text?.Trim();
        _config.ScriptDirRaw    = string.IsNullOrWhiteSpace(dir) ? "Scripts" : dir;

        // Blank is a valid value here — it means "pull repo scripts into the
        // script directory" (the pre-#221 behavior), so no default fallback.
        var repoDir = RepoScriptDirBox.Text?.Trim();
        _config.RepoScriptDirRaw = string.IsNullOrWhiteSpace(repoDir) ? "" : repoDir;

        // Reflect any normalization (blank → default) back into the form.
        LoadForm(_config);
        _onChanged?.Invoke();
        StatusText.Text = "Applied.";
    }

    /// <summary>Revert the form to the values currently held by the config,
    /// discarding any unsaved edits. (Reset-to-saved, not reset-to-factory —
    /// these are global settings shared by every profile, so silently
    /// restoring defaults would be a surprising, far-reaching change.)</summary>
    private void OnReset(object? sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        LoadForm(_config);
        StatusText.Text = "Reverted unsaved changes.";
    }

    private static bool TryReadChar(string? text, out char value)
    {
        value = '\0';
        if (string.IsNullOrEmpty(text)) return false;
        var c = text[0];
        if (char.IsWhiteSpace(c)) return false;
        value = c;
        return true;
    }
}
