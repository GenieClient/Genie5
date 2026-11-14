using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.Settings;
using Genie.App.Theming;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #304 — Game text and the echo colours appear in Display Settings AND in
/// the theme editor. It is ONE value each: Game text is DisplaySettings.GameColorHex,
/// and since #406 the three echo colours ARE the Genie 4 presets inputuser /
/// inputother / scriptecho. The theme editor seeds from and writes back to them,
/// and a theme only sets them when picked. These tests pin that, plus the
/// labelling and the Reset-to-the-active-theme fix.
/// </summary>
[Collection("highlight-statics")]
public class GameColorsOneHomeHeadlessTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_cfg_" + Guid.NewGuid().ToString("N"))).FullName;

    private static (ThemeService Themes, PresetColorStore Presets) Rig(DisplaySettings display, PresetEngine? engine = null)
    {
        var dir     = TempDir();
        var presets = new PresetColorStore(engine ?? new PresetEngine(), dir, () => null);
        var themes  = new ThemeService(Path.Combine(dir, "Themes"), display) { Presets = presets };
        return (themes, presets);
    }

    [AvaloniaFact]
    public void ThemeEditor_EditsTheSameLiveValuesDisplaySettingsShows()
    {
        var display = new DisplaySettings { GameColorHex = "#123456" };
        var (themes, presets) = Rig(display);
        presets.SetForeground(EchoKindPresets.InputUser, "#654321");

        var editor = new ThemeEditorViewModel(themes, display);

        var game  = editor.GameRoles.Single(e => e.Key == ThemeKeys.GameText);
        var typed = editor.GameRoles.Single(e => e.Key == ThemeKeys.InputUser);
        Assert.Equal(Color.Parse("#123456"), game.Value);                    // seeded from Display Settings
        Assert.Equal(Color.Parse("#654321"), typed.Value);                   // seeded from the preset
        Assert.Contains("Display Settings", game.Label);                     // labelled as the same setting
        Assert.Contains("inputuser", typed.Label);                           // and as the preset
        Assert.Equal(4, editor.GameRoles.Count);

        var built = editor.BuildTheme();
        Assert.Equal("#123456", built.Get(ThemeKeys.GameText));
        Assert.Equal("#654321", built.Get(ThemeKeys.InputUser));
    }

    [AvaloniaFact]
    public void DisplaySettingsReset_UsesTheActiveThemesColours()
    {
        var display = new DisplaySettings { ThemeName = "Light", GameColorHex = "#FF0000" };
        var (themes, _) = Rig(display);
        var vm = new DisplaySettingsViewModel(display, themes);

        vm.ResetCommand.Execute().Subscribe();

        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.GameText)!),   vm.GameColor);
        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.InputUser)!),  vm.InputUserColor);
        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.InputOther)!), vm.InputOtherColor);
        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.ScriptEcho)!), vm.ScriptEchoColor);
    }

    [AvaloniaFact]
    public void DisplaySettingsReset_WithoutThemes_FallsBackToTheBuiltInDefaults()
    {
        var vm = new DisplaySettingsViewModel(new DisplaySettings { GameColorHex = "#FF0000" }, null);
        vm.ResetCommand.Execute().Subscribe();
        Assert.Equal(Color.Parse(new DisplaySettings().GameColorHex), vm.GameColor);
        Assert.Equal(Colors.Yellow, vm.InputUserColor);                       // stock inputuser preset
    }

    [AvaloniaFact]
    public void DisplaySettingsOk_WritesOnlyTheEchoPresetsThatChanged()
    {
        var display = new DisplaySettings { GameColorHex = "#CCCCCC" };
        var engine  = new PresetEngine();
        var (themes, presets) = Rig(display, engine);
        presets.SetForeground(EchoKindPresets.InputOther, "Default");       // shows as the text colour

        var vm = new DisplaySettingsViewModel(display, themes);
        Assert.Equal(Color.Parse("#CCCCCC"), vm.InputOtherColor);
        vm.InputUserColor = Color.Parse("#112233");
        vm.OkCommand.Execute().Subscribe();

        Assert.Equal("#112233", presets.GetForeground(EchoKindPresets.InputUser));
        Assert.Equal("Default", presets.GetForeground(EchoKindPresets.InputOther));   // untouched, not turned into #CCCCCC
        Assert.Equal("Cyan",    presets.GetForeground(EchoKindPresets.ScriptEcho));
    }
}
