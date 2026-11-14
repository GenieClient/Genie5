using System;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #406 — the input / script echo presets and the familiar preset colour
/// text, as in Genie 4, and Display Settings / the theme editor edit those
/// presets through <see cref="PresetColorStore"/> instead of a second setting.
/// </summary>
[Collection("highlight-statics")]
public class EchoAndFamiliarPresetTests
{
    private static Color? C(IBrush? b) => (b as ISolidColorBrush)?.Color;

    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_presets_" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed class PresetRig : IDisposable
    {
        private readonly PresetEngine? _prev = DefaultHighlights.PresetEngine;
        public PresetEngine Engine { get; } = new();
        public PresetRig() => DefaultHighlights.PresetEngine = Engine;
        public void Dispose() => DefaultHighlights.PresetEngine = _prev;
    }

    // ── Echo lines take their preset's colour ───────────────────────────────

    [Theory]
    [InlineData("inputuser",  "#FFFF00")]   // Yellow
    [InlineData("inputother", "#ADFF2F")]   // GreenYellow
    [InlineData("scriptecho", "#00FFFF")]   // Cyan
    public void Echo_line_uses_its_preset_colour(string preset, string hex)
    {
        using var _ = new PresetRig();
        var line = new TextLine("look", StreamColor.System, EchoPreset: preset);
        Assert.Equal(Color.Parse(hex), C(line.EchoForeground()));
    }

    [Fact]
    public void Editing_the_preset_recolours_the_line()
    {
        using var rig = new PresetRig();
        var line = new TextLine("look", StreamColor.System, EchoPreset: "inputuser");
        rig.Engine.Apply(new PresetRule { Id = "inputuser", ForegroundColor = "#123456" });
        Assert.Equal(Color.Parse("#123456"), C(line.EchoForeground()));
    }

    [Fact]
    public void Explicit_echo_colour_beats_the_preset()
    {
        using var _ = new PresetRig();
        var line = new TextLine("hi", StreamColor.System, EchoColor: "Red", EchoPreset: "scriptecho");
        Assert.Equal(Colors.Red, C(line.EchoForeground()));
    }

    [Fact]
    public void Default_preset_leaves_the_echo_class_colour()
    {
        using var rig = new PresetRig();
        rig.Engine.Apply(new PresetRule { Id = "scriptecho", ForegroundColor = "Default" });
        Assert.Null(new TextLine("hi", StreamColor.System, EchoPreset: "scriptecho").EchoForeground());
    }

    // ── Familiar base colour ────────────────────────────────────────────────

    [Fact]
    public void Familiar_stream_gets_a_whole_line_familiar_span_after_the_games_own()
    {
        var inner = new PresetSpan(0, 3, "speech");
        var e = StreamTabsViewModel.WithStreamPresetBase(
            new TextEvent("familiar", "Your familiar purrs.", PresetSpans: new[] { inner }));

        Assert.Equal(2, e.PresetSpans!.Count);
        Assert.Equal(inner, e.PresetSpans[0]);                                    // game's span first → wins
        Assert.Equal(new PresetSpan(0, "Your familiar purrs.".Length, "familiar"), e.PresetSpans[1]);
    }

    [Fact]
    public void Familiar_preset_colours_the_line()
    {
        using var rig = new PresetRig();
        // Avoids words the pre-#396 built-in colour rules would claim ("Your", numbers, directions).
        const string text = "A small cat purrs softly.";
        var e = StreamTabsViewModel.WithStreamPresetBase(new TextEvent("familiar", text));
        var map = DefaultHighlights.BuildStyleMap(text, presetSpans: e.PresetSpans, window: "familiar");
        Assert.All(map.Foreground, b => Assert.Equal(Colors.PaleGreen, C(b)));
    }

    [Fact]
    public void Other_streams_are_unchanged()
    {
        var e = new TextEvent("thoughts", "x");
        Assert.Same(e, StreamTabsViewModel.WithStreamPresetBase(e));
    }

    // ── PresetColorStore: which file an edit goes to ─────────────────────────

    [Fact]
    public void Offline_edit_writes_the_shared_file_only_that_entry()
    {
        var global = TempDir();
        var p = new PersistenceService();
        p.SavePresets(Path.Combine(global, "presets.json"),
            new[] { new PresetRule { Id = "speech", ForegroundColor = "Lime" } });
        var store = new PresetColorStore(new PresetEngine(), global, () => null);

        store.SetForeground("inputuser", "#010203");

        var saved = p.LoadPresets(Path.Combine(global, "presets.json"));
        Assert.Equal("#010203", saved.Single(m => m.Id == "inputuser").ForegroundColor);
        Assert.Equal("Lime",    saved.Single(m => m.Id == "speech").ForegroundColor);   // kept
        Assert.Equal("#010203", store.GetForeground("inputuser"));
    }

    [Fact]
    public void Connected_edit_of_a_character_override_writes_the_profile_file()
    {
        var global = TempDir();
        var profile = TempDir();
        var engine = new PresetEngine();
        engine.Apply(new PresetRule { Id = "inputother", ForegroundColor = "Red", Scope = RuleScope.Character });
        var store = new PresetColorStore(engine, global, () => profile);

        store.SetForeground("inputother", "#0A0B0C");

        var p = new PersistenceService();
        Assert.Equal("#0A0B0C", p.LoadPresets(Path.Combine(profile, "presets.json")).Single(m => m.Id == "inputother").ForegroundColor);
        Assert.False(File.Exists(Path.Combine(global, "presets.json")));
        Assert.Equal("#0A0B0C", engine.GetForeground("inputother"));
        Assert.Equal(RuleScope.Character, engine.Get("inputother")!.Scope);
    }

    [Fact]
    public void Preview_changes_the_engine_but_writes_nothing()
    {
        var global = TempDir();
        var engine = new PresetEngine();
        var store  = new PresetColorStore(engine, global, () => global);

        store.Preview("scriptecho", "#445566");

        Assert.Equal("#445566", engine.GetForeground("scriptecho"));
        Assert.False(File.Exists(Path.Combine(global, "presets.json")));
    }
}
