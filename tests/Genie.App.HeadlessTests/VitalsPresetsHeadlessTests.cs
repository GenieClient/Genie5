using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.App.Theming;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #406 (part 2) — the health / mana / stamina / spirit / concentration
/// presets colour the vitals bars as in Genie 4: foreground = fill, background =
/// track and border. Themes carry a fill and a track per bar and write them into
/// the presets when picked; the theme editor edits the presets.
/// </summary>
[Collection("highlight-statics")]
public class VitalsPresetsHeadlessTests
{
    private static Color? C(IBrush? b) => (b as ISolidColorBrush)?.Color;

    private sealed class EngineRig : IDisposable
    {
        private readonly PresetEngine? _prev = DefaultHighlights.PresetEngine;
        public PresetEngine Engine { get; } = new();
        public EngineRig() => DefaultHighlights.PresetEngine = Engine;
        public void Dispose() => DefaultHighlights.PresetEngine = _prev;
    }

    private static (ThemeService Themes, PresetColorStore Presets, PresetEngine Engine, string Dir) Rig(PresetEngine? engine = null)
    {
        var dir     = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_vitals_" + Guid.NewGuid().ToString("N"))).FullName;
        engine    ??= new PresetEngine();
        var presets = new PresetColorStore(engine, dir, () => null);
        var themes  = new ThemeService(Path.Combine(dir, "Themes"), new DisplaySettings()) { Presets = presets };
        return (themes, presets, engine, dir);
    }

    private static ProgressBar ShownBar(string preset)
    {
        var bar = new ProgressBar();
        PresetBar.SetPreset(bar, preset);
        new Window { Content = bar }.Show();
        return bar;
    }

    // ── The bars ───────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Bar_fill_is_the_preset_foreground_and_track_and_border_its_background()
    {
        using var rig = new EngineRig();
        rig.Engine.Apply(new PresetRule { Id = "health", ForegroundColor = "#112233", BackgroundColor = "#445566" });

        var bar = ShownBar("health");

        Assert.Equal(Color.Parse("#112233"), C(bar.Foreground));
        Assert.Equal(Color.Parse("#445566"), C(bar.Background));
        Assert.Equal(Color.Parse("#445566"), C(bar.BorderBrush));
    }

    [AvaloniaFact]
    public void Bar_repaints_when_presets_change()
    {
        using var rig = new EngineRig();
        var bar = ShownBar("mana");

        rig.Engine.Apply(new PresetRule { Id = "mana", ForegroundColor = "#ABCDEF", BackgroundColor = "#010203" });
        UserHighlights.NotifyRulesChanged();

        Assert.Equal(Color.Parse("#ABCDEF"), C(bar.Foreground));
        Assert.Equal(Color.Parse("#010203"), C(bar.Background));
    }

    [AvaloniaFact]
    public void No_background_leaves_the_normal_track()
    {
        using var rig = new EngineRig();
        rig.Engine.Apply(new PresetRule { Id = "spirit", ForegroundColor = "Red", BackgroundColor = "#010203" });
        var bar = ShownBar("spirit");

        rig.Engine.Apply(new PresetRule { Id = "spirit", ForegroundColor = "Red", BackgroundColor = "" });
        UserHighlights.NotifyRulesChanged();

        // Back to the style's own track, the same as a plain bar's.
        var plain = new ProgressBar();
        new Window { Content = plain }.Show();
        Assert.Equal(C(plain.Background),  C(bar.Background));
        Assert.Equal(C(plain.BorderBrush), C(bar.BorderBrush));
        Assert.NotEqual(Color.Parse("#010203"), C(bar.Background));
        Assert.Equal(Colors.Red, C(bar.Foreground));
    }

    [AvaloniaFact]
    public void Stock_vitals_presets_are_Genie_4s()
    {
        var e = new PresetEngine();
        Assert.Equal("Maroon", e.GetForeground("health"));
        Assert.Equal("Navy",   e.GetForeground("mana"));
        Assert.Equal("Green",  e.GetForeground("stamina"));
        Assert.Equal("Purple", e.GetForeground("spirit"));
        Assert.Equal("Navy",   e.GetForeground("concentration"));
        Assert.All(VitalsPresets.All, id => Assert.Equal("", e.GetBackground(id)));
    }

    // ── Themes ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Applying_a_theme_saves_fill_and_track_to_the_presets()
    {
        var (themes, presets, _, _) = Rig();
        themes.Apply(BuiltInThemes.Light);

        foreach (var (fillKey, trackKey, id) in ThemeKeys.VitalsPresetKeys)
        {
            Assert.Equal(BuiltInThemes.Light.Get(fillKey),  presets.GetForeground(id));
            Assert.Equal(BuiltInThemes.Light.Get(trackKey), presets.GetBackground(id));
        }
    }

    [AvaloniaFact]
    public void Dark_theme_clears_the_tracks()
    {
        var (themes, presets, _, _) = Rig();
        presets.Set("health", "Red", "#202020");
        themes.Apply(BuiltInThemes.Dark);
        Assert.Equal("#800000", presets.GetForeground("health"));
        Assert.Equal("",        presets.GetBackground("health"));
    }

    [AvaloniaFact]
    public void Preview_shows_vitals_but_does_not_save()
    {
        var (themes, _, engine, dir) = Rig();
        themes.Preview(BuiltInThemes.SolarizedLight);
        Assert.Equal(BuiltInThemes.SolarizedLight.Get(ThemeKeys.ManaTrack), engine.GetBackground("mana"));
        Assert.False(File.Exists(Path.Combine(dir, "presets.json")));
    }

    [AvaloniaFact]
    public void Theme_without_track_keys_leaves_the_preset_backgrounds()
    {
        var (themes, presets, _, _) = Rig();
        presets.Set("stamina", "Green", "#123456");
        var old = new Theme { Name = "Old", BaseVariant = "Dark" };
        old.Colors[ThemeKeys.StaminaBar] = "#E0C050";

        themes.Apply(old);

        Assert.Equal("#E0C050", presets.GetForeground("stamina"));
        Assert.Equal("#123456", presets.GetBackground("stamina"));
    }

    [AvaloniaFact]
    public void Save_current_as_snapshots_fill_and_track()
    {
        var (themes, presets, _, _) = Rig();
        presets.Set("concentration", "#0000AA", "#000011");
        var saved = themes.SaveCurrentAs("Mine")!;
        Assert.Equal("#0000AA", saved.Get(ThemeKeys.ConcBar));
        Assert.Equal("#000011", saved.Get(ThemeKeys.ConcTrack));
    }

    [AvaloniaFact]
    public void Preset_store_set_changes_only_the_given_colours_on_disk()
    {
        var (_, presets, _, dir) = Rig();
        presets.Set("health", "Red", "#330000");
        presets.Set("health", null, "");          // clear the track only

        var saved = new PersistenceService().LoadPresets(Path.Combine(dir, "presets.json")).Single(m => m.Id == "health");
        Assert.Equal("Red", saved.ForegroundColor);
        Assert.Equal("",    saved.BackgroundColor);
    }

    [AvaloniaFact]
    public void Cancelling_Display_Settings_after_trying_a_theme_puts_the_vitals_back()
    {
        var (themes, presets, _, _) = Rig();
        presets.Set("mana", "#123456", "#000011");
        var vm = new DisplaySettingsViewModel(new DisplaySettings(), themes, presets);

        vm.SelectedThemeName = BuiltInThemes.Light.Name;     // tries the theme: writes its bar colours
        Assert.NotEqual("#123456", presets.GetForeground("mana"));

        vm.RestoreOriginalTheme();

        Assert.Equal("#123456", presets.GetForeground("mana"));
        Assert.Equal("#000011", presets.GetBackground("mana"));
    }

    // ── Theme editor ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Theme_editor_edits_the_vitals_presets()
    {
        var (themes, presets, _, _) = Rig();
        presets.Set("health", "#A00000", "");     // no track

        var editor = new ThemeEditorViewModel(themes, new DisplaySettings());
        var fill  = editor.Vitals.Single(e => e.Key == ThemeKeys.HealthBar);
        var track = editor.Vitals.Single(e => e.Key == ThemeKeys.HealthTrack);
        Assert.Equal(Color.Parse("#A00000"), fill.Value);    // seeded from the preset, not the theme
        Assert.Equal(0, track.Value.A);                      // none shows as transparent
        Assert.Equal(10, editor.Vitals.Count);

        var built = editor.BuildTheme();
        Assert.Equal("#A00000", built.Get(ThemeKeys.HealthBar));
        Assert.Equal("",        built.Get(ThemeKeys.HealthTrack));   // transparent → none

        track.Value = Color.Parse("#200000");
        Assert.Equal("#200000", editor.BuildTheme().Get(ThemeKeys.HealthTrack));
    }
}
