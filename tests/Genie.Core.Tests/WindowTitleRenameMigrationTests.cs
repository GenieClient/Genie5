using Genie.Core.Layout;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #197 — the "scripts" panel was renamed "Scripts" → "Script Manager".
/// A windows.json persisted from the old build still carries
/// DisplayTitle="Scripts"; on load it must be treated as unset so the panel
/// shows the current default ("Script Manager") consistently, matching the
/// existing "backpack" → "Inventory" rename migration. A genuinely custom
/// title is left alone.
/// </summary>
public class WindowTitleRenameMigrationTests
{
    private static WindowSettingsStore StoreWith(string id, string defaultTitle, string savedDisplayTitle)
    {
        var store = new WindowSettingsStore();
        store.Register(id, defaultTitle);
        store.Apply(new WindowSettingsPersistenceModel { Id = id, DisplayTitle = savedDisplayTitle });
        return store;
    }

    [Fact]
    public void Stale_Scripts_title_migrates_to_Script_Manager()
    {
        var store = StoreWith("scripts", "Script Manager", savedDisplayTitle: "Scripts");
        Assert.Equal("Script Manager", store.Get("scripts").DisplayTitle);
    }

    [Fact]
    public void Custom_scripts_title_is_preserved()
    {
        var store = StoreWith("scripts", "Script Manager", savedDisplayTitle: "My Scripts");
        Assert.Equal("My Scripts", store.Get("scripts").DisplayTitle);
    }

    [Fact]
    public void Existing_backpack_rename_still_migrates()
    {
        // Regression guard on the pre-existing entry.
        var store = StoreWith("backpack", "Inventory", savedDisplayTitle: "Backpack");
        Assert.Equal("Inventory", store.Get("backpack").DisplayTitle);
    }
}
