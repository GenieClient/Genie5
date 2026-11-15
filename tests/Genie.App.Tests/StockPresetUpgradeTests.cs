using System;
using System.IO;
using System.Linq;
using Genie.App.Settings;
using Genie.Core.Persistence;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.Tests;

/// <summary>#406 review — saved presets still holding Genie 5's old stock vitals
/// colours are reset to the current defaults; chosen colours and unreadable files
/// are left alone; character folders without a saved profile are covered.</summary>
public class StockPresetUpgradeTests
{
    private static string TempDataRoot()
        => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_upgrade_" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Save(string dir, params PresetRule[] rules)
    {
        Directory.CreateDirectory(dir);
        new PersistenceService().SavePresets(Path.Combine(dir, "presets.json"), rules);
    }

    private static PresetPersistenceModel Read(string dir, string id)
        => new PersistenceService().LoadPresets(Path.Combine(dir, "presets.json")).Single(m => m.Id == id);

    [Fact]
    public void Old_stock_vitals_get_the_current_defaults_and_chosen_colours_stay()
    {
        var config = Path.Combine(TempDataRoot(), "Config");
        Save(config,
            new PresetRule { Id = "concentration", ForegroundColor = "White",  BackgroundColor = "#000040" },
            new PresetRule { Id = "mana",          ForegroundColor = "Aqua",   BackgroundColor = "#000040" },
            new PresetRule { Id = "health",        ForegroundColor = "#FF00FF", BackgroundColor = "#400000" },   // chosen
            new PresetRule { Id = "speech",        ForegroundColor = "Lime" });

        StockPresetUpgrade.Run(StockPresetUpgrade.ConfigDirs(config, Array.Empty<string>()),
            StockPresetUpgrade.OldVitals, StockPresetUpgrade.CurrentStock);

        var stock = new PresetEngine();
        Assert.Equal(stock.GetForeground("concentration"), Read(config, "concentration").ForegroundColor);
        Assert.Equal(stock.GetBackground("concentration"), Read(config, "concentration").BackgroundColor);
        Assert.Equal(stock.GetForeground("mana"),          Read(config, "mana").ForegroundColor);
        Assert.Equal("#FF00FF", Read(config, "health").ForegroundColor);
        Assert.Equal("Lime",    Read(config, "speech").ForegroundColor);
    }

    [Fact]
    public void Character_folder_without_a_saved_profile_is_upgraded()
    {
        var root   = TempDataRoot();
        var config = Path.Combine(root, "Config");
        Directory.CreateDirectory(config);
        var unsaved = Path.Combine(root, "Profiles", "Kzin-ACCOUNT");   // reached with #connect, never saved
        Save(unsaved, new PresetRule { Id = "concentration", ForegroundColor = "White", BackgroundColor = "#000040" });

        StockPresetUpgrade.Run(StockPresetUpgrade.ConfigDirs(config, Array.Empty<string>()),
            StockPresetUpgrade.OldVitals, StockPresetUpgrade.CurrentStock);

        Assert.Equal(new PresetEngine().GetForeground("concentration"), Read(unsaved, "concentration").ForegroundColor);
    }

    [Fact]
    public void Old_stock_roundtime_gets_the_current_default()
    {
        // Aqua on #00004B under the countdown bar's white label (#418 review).
        var config = Path.Combine(TempDataRoot(), "Config");
        Save(config, new PresetRule { Id = "roundtime", ForegroundColor = "Aqua", BackgroundColor = "#00004B" });

        StockPresetUpgrade.Run(StockPresetUpgrade.ConfigDirs(config, Array.Empty<string>()),
            StockPresetUpgrade.OldRoundtime, StockPresetUpgrade.CurrentStock);

        Assert.Equal("MediumBlue", Read(config, "roundtime").ForegroundColor);
        Assert.Equal("",           Read(config, "roundtime").BackgroundColor);
    }

    [Fact]
    public void Unreadable_presets_file_is_not_rewritten()
    {
        var config = Path.Combine(TempDataRoot(), "Config");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "presets.json");
        const string broken = "[ { \"Id\": \"concentration\", \"ForegroundColor\": \"White\" }, ]x";
        File.WriteAllText(path, broken);

        StockPresetUpgrade.Run(StockPresetUpgrade.ConfigDirs(config, Array.Empty<string>()),
            StockPresetUpgrade.OldVitals, StockPresetUpgrade.CurrentStock);

        Assert.Equal(broken, File.ReadAllText(path));
    }
}
