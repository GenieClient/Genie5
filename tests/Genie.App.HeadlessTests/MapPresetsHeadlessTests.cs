using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Genie.Core.Mapper;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #406 (part 3): the map draws from Genie 4's <c>automapper.*</c>
/// presets, and the preset is the one place each map colour is kept. The map
/// background and label text used to live in display.json (written by the
/// Mapper's pickers and <c>#mapper color</c>) and in each layout; those now all
/// edit <c>automapper.panel</c>, and an existing colour carries over.
/// </summary>
[Collection("highlight-statics")]
public class MapPresetsHeadlessTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "genie_mappresets_" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Panel = MapPalette.PanelPreset;

    private PresetColorStore OfflineStore(string? dir = null) =>
        new(new PresetEngine(), dir ?? _dir, () => null);

    private PresetPersistenceModel? Saved(string id, string? dir = null)
    {
        var path = Path.Combine(dir ?? _dir, "presets.json");
        return File.Exists(path)
            ? new PersistenceService().LoadPresets(path).FirstOrDefault(m => m.Id == id)
            : null;
    }

    // ── The palette ────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Stock_palette_is_Genie_4s_AutoMapper()
    {
        var p = MapPalette.Stock;
        Assert.Equal(Colors.PaleGoldenrod, p.Background);
        Assert.Equal(Colors.Black,  p.Text);
        Assert.Equal(Colors.Black,  p.Line);       Assert.Equal(Colors.White, p.LineOtherFloor);
        Assert.Equal(Colors.Blue,   p.LineGo);     Assert.Equal(Colors.White, p.LineGoOtherFloor);
        Assert.Equal(Colors.Green,  p.LineClimb);  Assert.Equal(Colors.White, p.LineClimbOtherFloor);
        Assert.Equal(Colors.Cyan,   p.LineStump);  Assert.Equal(Colors.White, p.LineStumpOtherFloor);
        Assert.Equal(Colors.White,  p.Node);       Assert.Equal(Colors.White, p.NodeOtherFloor);
        Assert.Equal(Colors.Green,  p.Path);       Assert.Equal(Colors.LightGreen, p.PathOtherFloor);
    }

    [AvaloniaFact]
    public void Palette_reads_each_preset_and_falls_back_to_stock_for_unset_colours()
    {
        var engine = new PresetEngine();
        engine.Apply(new PresetRule { Id = "automapper.line",   ForegroundColor = "#102030", BackgroundColor = "#405060" });
        engine.Apply(new PresetRule { Id = "automapper.linego", ForegroundColor = "Default", BackgroundColor = "" });

        var p = MapPalette.From(engine.GetForeground, engine.GetBackground);

        Assert.Equal(Color.Parse("#102030"), p.Line);
        Assert.Equal(Color.Parse("#405060"), p.LineOtherFloor);
        Assert.Equal(Colors.Blue,  p.LineGo);              // "Default" → stock
        Assert.Equal(Colors.White, p.LineGoOtherFloor);    // none → stock
    }

    [AvaloniaFact]
    public void Legend_shows_the_palettes_colours()
    {
        var zone = new MapZone();
        var a = new MapNode { Id = 1, X = 0,  Y = 0 };
        var b = new MapNode { Id = 2, X = 20, Y = 0 };
        a.Exits.Add(new MapExit { Direction = Direction.East, DestinationId = 2, MoveCommand = "east" });
        a.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north" });   // unmapped → stub
        zone.Nodes[1] = a; zone.Nodes[2] = b;
        var custom = MapPalette.Stock with { Node = Colors.Khaki, Line = Colors.Maroon, LineStump = Colors.Orange };

        var rows = MapCanvas.BuildLegend(zone, 0, null, null, true, false, Colors.Red, custom);

        Assert.Equal(Colors.Khaki,  rows.Single(r => r.Label == "Room").Color);
        Assert.Equal(Colors.Maroon, rows.Single(r => r.Label == "Path").Color);
        Assert.Equal(Colors.Orange, rows.Single(r => r.Label.StartsWith("Exit")).Color);
    }

    // ── The Mapper's pickers edit automapper.panel ─────────────────────────

    [AvaloniaFact]
    public void Map_background_picker_sets_the_panel_presets_background()
    {
        var vm = new MapperViewModel { PresetStore = OfflineStore() };

        vm.MapBackground = Color.Parse("#112233");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#112233", Saved(Panel)!.BackgroundColor);
        Assert.Equal("Black",   Saved(Panel)!.ForegroundColor);      // text untouched
        Assert.Equal(Color.Parse("#112233"), vm.Palette.Background);
    }

    [AvaloniaFact]
    public void Map_text_picker_sets_the_panel_presets_colour()
    {
        var vm = new MapperViewModel { PresetStore = OfflineStore() };

        vm.MapTextColor = Color.Parse("#AA0000");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#AA0000",       Saved(Panel)!.ForegroundColor);
        Assert.Equal("PaleGoldenrod", Saved(Panel)!.BackgroundColor); // background untouched
        Assert.Equal(Color.Parse("#AA0000"), vm.Palette.Text);
    }

    [AvaloniaFact]
    public void A_preset_edit_repaints_the_map_and_shows_in_the_pickers()
    {
        var store = OfflineStore();
        var vm = new MapperViewModel { PresetStore = store };

        store.Set(Panel, "Navy", "#202020");                  // e.g. the Presets tab
        store.Set("automapper.lineclimb", "Red", "Gray");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#202020"), vm.MapBackground);
        Assert.Equal(Colors.Navy, vm.MapTextColor);
        Assert.Equal(Colors.Red,  vm.Palette.LineClimb);
        Assert.Equal(Colors.Gray, vm.Palette.LineClimbOtherFloor);
    }

    // ── Upgrade: display.json's map colours move into the preset ──────────

    [AvaloniaFact]
    public void Upgrade_moves_picked_map_colours_into_the_preset_and_clears_display_json()
    {
        var display = new DisplaySettings { MapBackgroundHex = "#334455", MapTextHex = "#FFFFFF" };

        Assert.True(MapperViewModel.MigrateLegacyColours(display, OfflineStore()));

        Assert.Equal("#334455", Saved(Panel)!.BackgroundColor);
        Assert.Equal("#FFFFFF", Saved(Panel)!.ForegroundColor);
        Assert.Null(display.MapBackgroundHex);
        Assert.Null(display.MapTextHex);
    }

    [AvaloniaFact]
    public void Upgrade_leaves_the_preset_alone_when_display_json_held_only_defaults()
    {
        // e.g. a panel colour imported from Genie 4 — it never showed before,
        // and nobody picked the display.json defaults.
        OfflineStore().Set(Panel, "#010101", "#C0C0C0");
        var display = new DisplaySettings { MapBackgroundHex = "#EEE8AA", MapTextHex = "#000000" };

        Assert.True(MapperViewModel.MigrateLegacyColours(display, OfflineStore()));

        Assert.Equal("#C0C0C0", Saved(Panel)!.BackgroundColor);
        Assert.Equal("#010101", Saved(Panel)!.ForegroundColor);
        Assert.Null(display.MapBackgroundHex);
    }

    [AvaloniaFact]
    public void Upgrade_ignores_the_old_dark_default()
    {
        var display = new DisplaySettings { MapBackgroundHex = "#1A1A1A" };
        MapperViewModel.MigrateLegacyColours(display, OfflineStore());
        Assert.Null(Saved(Panel));
    }

    [AvaloniaFact]
    public void Upgrade_runs_once()
    {
        Assert.False(MapperViewModel.MigrateLegacyColours(new DisplaySettings(), OfflineStore()));
    }

    [AvaloniaFact]
    public void Display_json_no_longer_writes_the_map_colours()
    {
        var path = Path.Combine(_dir, "display.json");
        new DisplaySettings().Save(path);
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("MapBackgroundHex", json);
        Assert.DoesNotContain("MapTextHex", json);
    }

    [AvaloniaFact]
    public void Starting_Genie_with_an_old_display_json_keeps_the_map_colour()
    {
        var config = Directory.CreateDirectory(Path.Combine(_dir, "Config")).FullName;
        File.WriteAllText(Path.Combine(config, "display.json"),
            """{ "MapBackgroundHex": "#334455", "MapTextHex": "#000000" }""");

        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#334455"), vm.Mapper.Palette.Background);
        Assert.Equal("#334455", Saved(Panel, config)!.BackgroundColor);
        Assert.DoesNotContain("MapBackgroundHex", File.ReadAllText(Path.Combine(config, "display.json")));
    }

    // ── Layouts carry the map background through the preset ───────────────

    [AvaloniaFact]
    public void Loading_a_layout_sets_the_map_background_preset_at_once()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        var config = Path.Combine(_dir, "Config");

        vm.ApplyLayout(new SavedLayout { MapBackgroundHex = "#445566" });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#445566"), vm.Mapper.Palette.Background);
        Assert.Equal("#445566", Saved(Panel, config)!.BackgroundColor);
    }

    [AvaloniaFact]
    public void A_layout_without_a_map_background_or_with_the_old_dark_default_leaves_the_map_alone()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        vm.ApplyLayout(new SavedLayout { MapBackgroundHex = "#445566" });
        Dispatcher.UIThread.RunJobs();

        vm.ApplyLayout(new SavedLayout());                               // older layout: no field
        vm.ApplyLayout(new SavedLayout { MapBackgroundHex = "#1A1A1A" }); // old default
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Color.Parse("#445566"), vm.Mapper.Palette.Background);
    }

    [AvaloniaFact]
    public void Saving_a_layout_records_the_presets_map_background()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        vm.Mapper.MapBackground = Color.Parse("#0A0B0C");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("#0A0B0C", vm.CaptureCurrentLayout().MapBackgroundHex);
    }
}
