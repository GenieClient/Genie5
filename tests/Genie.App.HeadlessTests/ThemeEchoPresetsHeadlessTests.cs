using System;
using System.IO;
using Avalonia.Headless.XUnit;
using Genie.App.Settings;
using Genie.App.Theming;
using Genie.Core;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #406 — picking a theme writes its echo colours into the inputuser /
/// inputother / scriptecho presets (saved), the editor's live preview only shows
/// them, and a custom theme file from before #406 (one legacy "Echoes" key)
/// still sets all three.
/// </summary>
[Collection("highlight-statics")]
public class ThemeEchoPresetsHeadlessTests
{
    private static (ThemeService Themes, PresetColorStore Presets, PresetEngine Engine, string Dir) Rig()
    {
        var dir     = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_theme_" + Guid.NewGuid().ToString("N"))).FullName;
        var engine  = new PresetEngine();
        var presets = new PresetColorStore(engine, dir, () => null);
        var themes  = new ThemeService(Path.Combine(dir, "Themes"), new DisplaySettings()) { Presets = presets };
        return (themes, presets, engine, dir);
    }

    [AvaloniaFact]
    public void Applying_a_theme_saves_its_echo_colours_to_the_presets()
    {
        var (themes, presets, _, _) = Rig();
        themes.Apply(BuiltInThemes.Light);

        Assert.Equal(BuiltInThemes.Light.Get(ThemeKeys.InputUser),  presets.GetForeground(EchoKindPresets.InputUser));
        Assert.Equal(BuiltInThemes.Light.Get(ThemeKeys.InputOther), presets.GetForeground(EchoKindPresets.InputOther));
        Assert.Equal(BuiltInThemes.Light.Get(ThemeKeys.ScriptEcho), presets.GetForeground(EchoKindPresets.ScriptEcho));
    }

    [AvaloniaFact]
    public void Startup_apply_leaves_the_presets_alone()
    {
        var (themes, presets, _, _) = Rig();
        presets.SetForeground(EchoKindPresets.InputUser, "#010101");
        themes.ApplyStartup();
        Assert.Equal("#010101", presets.GetForeground(EchoKindPresets.InputUser));
    }

    [AvaloniaFact]
    public void Preview_shows_but_does_not_save()
    {
        var (themes, _, engine, dir) = Rig();
        themes.Preview(BuiltInThemes.SolarizedDark);
        Assert.Equal(BuiltInThemes.SolarizedDark.Get(ThemeKeys.InputUser), engine.GetForeground(EchoKindPresets.InputUser));
        Assert.False(File.Exists(Path.Combine(dir, "presets.json")));
    }

    [AvaloniaFact]
    public void Legacy_theme_with_one_echo_colour_sets_all_three()
    {
        var (themes, presets, _, _) = Rig();
        var legacy = new Theme { Name = "Old", BaseVariant = "Dark" };
        legacy.Colors[ThemeKeys.GameEcho] = "#ABCDEF";

        themes.Apply(legacy);

        foreach (var (_, id) in ThemeKeys.EchoPresetKeys)
            Assert.Equal("#ABCDEF", presets.GetForeground(id));
    }

    [AvaloniaFact]
    public void Save_current_as_snapshots_the_echo_presets()
    {
        var (themes, presets, _, _) = Rig();
        presets.SetForeground(EchoKindPresets.ScriptEcho, "#FEDCBA");
        var saved = themes.SaveCurrentAs("Mine")!;
        Assert.Equal("#FEDCBA", saved.Get(ThemeKeys.ScriptEcho));
        Assert.Null(saved.Get(ThemeKeys.GameEcho));
    }
}
