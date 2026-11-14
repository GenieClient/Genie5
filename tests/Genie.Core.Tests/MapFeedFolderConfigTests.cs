using System;
using System.IO;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#config mapfestivals on|off</c> / <c>#config mapquests on|off</c> — the
/// typed form of the Updates dialog's per-row Festivals / Quests checkboxes.
/// The keys are NOT settings.cfg state: their truth is
/// <see cref="FeedEntry.IncludeFestivalMaps"/> / <see cref="FeedEntry.IncludeQuestMaps"/>
/// in update-feeds.json, so the command and the dialog can never disagree, and
/// settings.cfg must neither write the keys nor restore them from a hand-added
/// line (which would overwrite the dialog's choice at the next launch).
/// </summary>
public class MapFeedFolderConfigTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "genie_mapfeedcfg_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieMapFeedCfgTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    private FeedConfigStore Store(GenieConfig cfg) => new(cfg.ConfigDir);

    [Fact]
    public void Both_keys_read_off_on_a_fresh_install()
    {
        var cfg = NewConfig();
        Assert.Equal("False", cfg.GetSetting("mapfestivals"));
        Assert.Equal("False", cfg.GetSetting("mapquests"));
    }

    [Fact]
    public void Config_command_writes_the_feed_store_not_settings_cfg()
    {
        var cfg = NewConfig();
        cfg.SetSetting("mapfestivals", "on");

        // The official row in update-feeds.json carries the flag…
        var feeds = Store(cfg).Load();
        var official = Assert.Single(feeds.Maps);
        Assert.True(official.IncludeFestivalMaps);
        Assert.False(official.IncludeQuestMaps);

        // …the read side reflects it…
        Assert.Equal("True",  cfg.GetSetting("mapfestivals"));
        Assert.Equal("False", cfg.GetSetting("mapquests"));

        // …and settings.cfg never carries either key.
        Assert.True(cfg.Save());
        var settings = File.ReadAllText(Path.Combine(cfg.ConfigDir, "settings.cfg"));
        Assert.DoesNotContain("mapfestivals", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mapquests",    settings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quests_are_a_separate_switch()
    {
        var cfg = NewConfig();
        cfg.SetSetting("mapquests", "true");

        var official = Assert.Single(Store(cfg).Load().Maps);
        Assert.False(official.IncludeFestivalMaps);
        Assert.True(official.IncludeQuestMaps);

        cfg.SetSetting("mapquests", "off");
        Assert.False(Assert.Single(Store(cfg).Load().Maps).IncludeQuestMaps);
    }

    [Fact]
    public void Command_flips_every_maps_row_and_the_dialog_choice_is_what_is_read()
    {
        var cfg   = NewConfig();
        var store = Store(cfg);
        var feeds = FeedConfig.CreateDefault();
        var fork  = FeedEntry.OfficialMaps();
        fork.Id = "my-fork"; fork.Owner = "someone"; fork.Name = "someone/Maps";
        feeds.Maps.Add(fork);
        Assert.True(store.Save(feeds));

        cfg.SetSetting("mapfestivals", "on");
        Assert.All(store.Load().Maps, f => Assert.True(f.IncludeFestivalMaps));

        // The dialog turns one row off: the typed key still reads on (any row).
        var edited = store.Load();
        edited.Maps[0].IncludeFestivalMaps = false;
        store.Save(edited);
        Assert.Equal("True", cfg.GetSetting("mapfestivals"));

        cfg.SetSetting("mapfestivals", "off");
        Assert.All(store.Load().Maps, f => Assert.False(f.IncludeFestivalMaps));
        Assert.Equal("False", cfg.GetSetting("mapfestivals"));
    }

    [Fact]
    public void A_hand_added_settings_line_is_ignored_on_load()
    {
        var cfg = NewConfig();
        Directory.CreateDirectory(cfg.ConfigDir);
        File.WriteAllText(Path.Combine(cfg.ConfigDir, "settings.cfg"),
            "#config {mapfestivals} {True}\n#config {mapquests} {True}\n");

        Assert.True(cfg.Load());

        // Neither the feed store nor the read side moved.
        Assert.False(File.Exists(Store(cfg).FilePath));
        Assert.Equal("False", cfg.GetSetting("mapfestivals"));
        Assert.Equal("False", cfg.GetSetting("mapquests"));
    }

    [Fact]
    public void Keys_are_listed_under_updates_and_marked_not_persisted()
    {
        var updates = GenieConfig.ConfigCategories.Single(c => c.Category == "Updates").Keys;
        Assert.Contains("mapfestivals", updates);
        Assert.Contains("mapquests",    updates);
        Assert.Contains("mapfestivals", GenieConfig.NotPersistedKeys);
        Assert.Contains("mapquests",    GenieConfig.NotPersistedKeys);
        // Every key the listing shows is a key SetSetting accepts.
        var cfg = NewConfig();
        Assert.NotNull(cfg.GetSetting("mapfestivals"));
        Assert.NotNull(cfg.GetSetting("mapquests"));
    }
}
