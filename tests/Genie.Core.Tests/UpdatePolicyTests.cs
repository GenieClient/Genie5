using System;
using System.IO;
using Genie.Core.Update;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Per-kind update policies (Help ▸ Update Settings — issue #26): each of
/// Core / Maps / Plugins / Scripts carries a CheckOnStartup + AutoApply pair
/// in update-feeds.json. Defaults must be check-ON / auto-apply-OFF (matching
/// the pre-policy behavior: everything was checked, nothing self-installed),
/// old files without the properties must load as those defaults, and toggles
/// must round-trip through FeedConfigStore.
/// </summary>
public class UpdatePolicyTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "GenieUpdatePolicyTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Defaults_are_check_on_and_autoapply_off_for_all_kinds()
    {
        var cfg = FeedConfig.CreateDefault();
        foreach (var p in new[] { cfg.CorePolicy, cfg.MapsPolicy, cfg.PluginsPolicy, cfg.ScriptsPolicy })
        {
            Assert.True(p.CheckOnStartup);
            Assert.False(p.AutoApply);
        }
    }

    [Fact]
    public void Toggles_round_trip_through_the_store()
    {
        var store = new FeedConfigStore(_dir);
        var cfg   = FeedConfig.CreateDefault();
        cfg.ScriptsPolicy.CheckOnStartup = false;
        cfg.MapsPolicy.AutoApply         = true;
        cfg.CorePolicy.AutoApply         = true;
        Assert.True(store.Save(cfg));

        var loaded = store.Load();
        Assert.False(loaded.ScriptsPolicy.CheckOnStartup);
        Assert.True(loaded.MapsPolicy.AutoApply);
        Assert.True(loaded.CorePolicy.AutoApply);
        // Untouched policies keep their defaults through the round-trip.
        Assert.True(loaded.PluginsPolicy.CheckOnStartup);
        Assert.False(loaded.PluginsPolicy.AutoApply);
    }

    [Fact]
    public void Legacy_file_without_policies_loads_with_defaults()
    {
        // A pre-policy update-feeds.json — no *Policy properties at all.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "update-feeds.json"), """
        {
          "core": { "kind": "github-releases", "owner": "GenieClient", "repo": "Genie5",
                    "channel": "beta", "assetPattern": "Genie5-{os}-{arch}.zip",
                    "checkOnStartup": true },
          "maps": [], "plugins": [], "scripts": []
        }
        """);

        var loaded = new FeedConfigStore(_dir).Load();
        Assert.True(loaded.CorePolicy.CheckOnStartup);    // default, not crash
        Assert.False(loaded.ScriptsPolicy.AutoApply);
        Assert.Equal("Genie5", loaded.Core.Repo);          // rest of the file intact
    }
}
