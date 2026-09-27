using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.Settings;
using Genie.App.Theming;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #304 — Game text and Echoes appear in Display Settings AND in the
/// theme editor. Verified: it is ONE value each (DisplaySettings.GameColorHex /
/// EchoColorHex in display.json); the theme editor seeds from and writes back
/// to it, and a theme only sets it when picked. These tests pin that, plus the
/// labelling and the Reset-to-the-active-theme fix.
/// </summary>
public class GameColorsOneHomeHeadlessTests
{
    private static ThemeService Themes(DisplaySettings display) =>
        new(Path.Combine(Path.GetTempPath(), "genie_themes_none_" + Guid.NewGuid().ToString("N")), display);

    [AvaloniaFact]
    public void ThemeEditor_EditsTheSameLiveValueDisplaySettingsShows()
    {
        var display = new DisplaySettings { GameColorHex = "#123456", EchoColorHex = "#654321" };
        var editor  = new ThemeEditorViewModel(Themes(display), display);

        var game = editor.GameRoles.Single(e => e.Key == ThemeKeys.GameText);
        var echo = editor.GameRoles.Single(e => e.Key == ThemeKeys.GameEcho);
        Assert.Equal(Color.Parse("#123456"), game.Value);                    // seeded from Display Settings
        Assert.Equal(Color.Parse("#654321"), echo.Value);
        Assert.Contains("Display Settings", game.Label);                     // and labelled as the same setting
        Assert.Contains("Display Settings", echo.Label);

        var built = editor.BuildTheme();
        Assert.Equal("#123456", built.Get(ThemeKeys.GameText));
    }

    [AvaloniaFact]
    public void DisplaySettingsReset_UsesTheActiveThemesGameColours()
    {
        var display = new DisplaySettings { ThemeName = "Light", GameColorHex = "#FF0000", EchoColorHex = "#00FF00" };
        var vm      = new DisplaySettingsViewModel(display, Themes(display));

        vm.ResetCommand.Execute().Subscribe();

        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.GameText)!), vm.GameColor);
        Assert.Equal(Color.Parse(BuiltInThemes.Light.Get(ThemeKeys.GameEcho)!), vm.EchoColor);
    }

    [AvaloniaFact]
    public void DisplaySettingsReset_WithoutThemes_FallsBackToTheBuiltInDefaults()
    {
        var vm = new DisplaySettingsViewModel(new DisplaySettings { GameColorHex = "#FF0000" }, null);
        vm.ResetCommand.Execute().Subscribe();
        Assert.Equal(Color.Parse(new DisplaySettings().GameColorHex), vm.GameColor);
    }
}
